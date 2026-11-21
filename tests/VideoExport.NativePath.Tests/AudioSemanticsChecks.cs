using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using AAVideoExport.Plugin;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Diagnostic-only native probe. It validates Unity AudioSource pitch, volume,
// Pause and UnPause against AudioRenderer PCM; it never enables production hooks.
public sealed class AudioSemanticsChecks : MonoBehaviour
{
    private const int Rate = 48000, Channels = 2, Lead = 4096, ClipFrames = 8192;
    private const int PauseAfterOutputSamples = 1536;
    private const float Volume = .5f, Pitch = 2f, SilenceTolerance = .00001f;
    private static readonly int[] MarkerFrames = { 512, 1024, 1536, 2048, 2560, 3072, 3584, 4096, 4608, 5120, 5632, 6144, 6656, 7168, 7680 };
    private static readonly float[] MarkerValues = { .4f, -.6f, .8f, -.3f, .9f, -.5f, .7f, -.8f, .55f, -.75f, .65f, -.45f, .85f, -.35f, .95f };
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly List<float> left = new();
    private readonly List<(AudioSource Source, bool Mute)> muted = new();
    private IDisposable? ownership;
    private GameObject? fixture;
    private AudioSource? source;
    private AudioClip? clip;
    private double epoch, dspEnd;
    private int stage, updates, dspBlock, dspBlocks;
    private long pausePcmSample, resumePcmSample;
    private int pauseCursor, cursorWhilePaused, cursorAfterResume;
    private bool renderer, finished, sourceConfigured;
    private bool timeSnapshots;
    private float oldDelta, oldScale;
    private int oldFps, oldVsync, oldInterval;
    private bool oldBackground;
    private string failure = "";

    public AudioSemanticsChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (finished) return;
        try
        {
            if (stage == 0)
            {
                if (wall.Elapsed.TotalSeconds > 45) throw new TimeoutException("Native audio semantics probe exceeded its startup deadline.");
                if (!RenderControlV1.IsReady || SceneManager.GetActiveScene().name != "CatalogScene") return;
                Begin();
                stage = 1;
                return;
            }
            if (++updates > 180) throw new TimeoutException("Native audio semantics probe exceeded its bounded update budget.");
            RenderAudio();

            if (stage == 1)
            {
                var scheduler = new StrictAudioSegmentScheduler(epoch, Rate);
                var segment = new StrictAudioSegment(source!.Pointer.ToString(), "native-pitch-pause", Lead,
                    Lead + ClipFrames + 8192, 0, false, Volume, Pitch);
                var planned = scheduler.Schedule(new[] { segment })[0];
                StrictAudioDspDispatcher.Schedule(source, planned);
                dspEnd = planned.DspEndSeconds;
                sourceConfigured = source.volume == Volume && source.pitch == Pitch;
                if (!sourceConfigured) throw new InvalidOperationException($"Dispatcher property mapping differed: volume={source.volume:R}; pitch={source.pitch:R}.");
                stage = 2;
                return;
            }

            if (stage == 2 && AudioSettings.dspTime >= epoch + (Lead + PauseAfterOutputSamples) / (double)Rate)
            {
                pausePcmSample = left.Count;
                source!.Pause();
                pauseCursor = source.timeSamples;
                stage = 3;
                return;
            }

            if (stage == 3)
            {
                cursorWhilePaused = source!.timeSamples;
                if (cursorWhilePaused != pauseCursor)
                    throw new InvalidOperationException($"Pause cursor advanced during an offline render: before={pauseCursor}; after={cursorWhilePaused}.");
                var pausedBlock = left.Skip(checked((int)pausePcmSample)).ToArray();
                int nonSilent = pausedBlock.Count(value => Math.Abs(value) > SilenceTolerance);
                if (nonSilent != 0)
                    throw new InvalidOperationException($"Pause did not silence the next native render block: non_silent_samples={nonSilent}.");
                source.UnPause();
                cursorAfterResume = source.timeSamples;
                if (cursorAfterResume != pauseCursor)
                    throw new InvalidOperationException($"UnPause changed the source cursor before rendering: paused={pauseCursor}; resumed={cursorAfterResume}.");
                resumePcmSample = left.Count;
                stage = 4;
                return;
            }

            if (stage == 4 && left.Count >= resumePcmSample + ClipFrames / (int)Pitch)
            {
                Verify();
                Finish();
            }
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            Plugin.Report("PATH-CHECK FAIL audio-semantics: " + error);
            Finish();
        }
    }

    [HideFromIl2Cpp]
    private void Begin()
    {
        if (RenderControlV1.IsRenderingOwned) throw new InvalidOperationException("Audio semantics probe found rendering ownership already active.");
        ownership = (IDisposable)typeof(RenderControlV1).GetMethod("Acquire", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
        oldDelta = Time.captureDeltaTime; oldScale = Time.timeScale; oldFps = Application.targetFrameRate;
        oldVsync = QualitySettings.vSyncCount; oldInterval = OnDemandRendering.renderFrameInterval; oldBackground = Application.runInBackground;
        timeSnapshots = true;
        if (AudioSettings.outputSampleRate != Rate || AudioSettings.speakerMode != AudioSpeakerMode.Stereo)
            throw new InvalidOperationException("Probe requires the existing 48000 Hz stereo mixer and will not reconfigure the audio device.");
        AudioSettings.GetDSPBufferSize(out dspBlock, out dspBlocks);
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; OnDemandRendering.renderFrameInterval = 1;
        Application.runInBackground = true; Time.timeScale = 1; Time.captureFramerate = 30;
        MuteOtherSources();
        fixture = new GameObject("AAVE temporary native audio semantics probe"); Object.DontDestroyOnLoad(fixture);
        if (!Object.FindObjectsOfType<AudioListener>().Any(listener => listener != null && listener.enabled)) fixture.AddComponent<AudioListener>();
        clip = CreateClip();
        source = fixture.AddComponent<AudioSource>();
        source.playOnAwake = false; source.loop = false; source.mute = false; source.ignoreListenerPause = true;
        source.spatialBlend = 0; source.dopplerLevel = 0; source.priority = 0; source.ignoreListenerVolume = true;
        source.bypassEffects = true; source.bypassListenerEffects = true; source.bypassReverbZones = true; source.clip = clip;
        epoch = AudioSettings.dspTime;
        if (!AudioRenderer.Start()) throw new InvalidOperationException("Offline AudioRenderer did not start.");
        renderer = true;
        Plugin.Report($"PATH-CHECK AUDIO-SEMANTICS begin rate={Rate}; channels={Channels}; dsp_block={dspBlock}; dsp_blocks={dspBlocks}; lead={Lead}; pause_after={PauseAfterOutputSamples}; volume={Volume:R}; pitch={Pitch:R}");
    }

    [HideFromIl2Cpp]
    private AudioClip CreateClip()
    {
        var samples = new float[ClipFrames * Channels];
        for (int i = 0; i < MarkerFrames.Length; i++)
        {
            int frame = MarkerFrames[i];
            samples[frame * Channels] = MarkerValues[i];
            samples[frame * Channels + 1] = MarkerValues[i];
        }
        var value = AudioClip.Create("AAVE pitch pause markers", ClipFrames, Channels, Rate, false);
        if (value == null || !value.SetData(new Il2CppStructArray<float>(samples), 0))
            throw new InvalidOperationException("Could not create the native marker clip.");
        return value;
    }

    [HideFromIl2Cpp]
    private void MuteOtherSources()
    {
        foreach (var audioSource in Object.FindObjectsOfType<AudioSource>(true))
        {
            if (audioSource == null || audioSource == source) continue;
            if (!muted.Any(state => state.Source == audioSource)) muted.Add((audioSource, audioSource.mute));
            audioSource.mute = true;
        }
    }

    private unsafe void RenderAudio()
    {
        int frames = AudioRenderer.GetSampleCountForCaptureFrame();
        if (frames <= 0 || frames > Rate) throw new InvalidOperationException("Native renderer advertised an invalid audio frame count.");
        var buffer = new float[frames * Channels];
        fixed (float* pointer = buffer)
            if (!AudioRenderer.Internal_AudioRenderer_Render(pointer, buffer.Length)) throw new InvalidOperationException("Native offline audio render failed.");
        for (int i = 0; i < frames; i++) left.Add(buffer[i * Channels]);
    }

    [HideFromIl2Cpp]
    private void Verify()
    {
        if (!sourceConfigured || pauseCursor <= 0 || cursorWhilePaused != pauseCursor || cursorAfterResume != pauseCursor)
            throw new InvalidOperationException("Native source volume/pitch/pause cursor contract was not preserved.");
        if (source!.pitch != Pitch || source.volume != Volume) throw new InvalidOperationException("Source pitch or volume changed during the probe.");

        var evidence = new List<object>();
        int checkedBeforePause = 0, checkedAfterResume = 0;
        for (int i = 0; i < MarkerFrames.Length; i++)
        {
            int marker = MarkerFrames[i];
            long expected;
            if (marker < pauseCursor)
            {
                expected = Lead + marker / (int)Pitch;
                if (expected >= pausePcmSample) continue;
                checkedBeforePause++;
            }
            else
            {
                expected = resumePcmSample + (long)Math.Ceiling((marker - pauseCursor) / (double)Pitch);
                checkedAfterResume++;
            }
            var actual = FindPulse(expected, 4);
            float expectedAmplitude = MarkerValues[i] * Volume;
            if (actual.Position < 0 || Math.Abs(actual.Position - expected) > 1
                || Math.Abs(actual.Value - expectedAmplitude) > .03f)
                throw new InvalidOperationException($"Pitch/volume/resume marker differed at source_frame={marker}: expected_pcm={expected}; actual_pcm={actual.Position}; expected_value={expectedAmplitude:R}; actual_value={actual.Value:R}; pause_cursor={pauseCursor}; resume_pcm={resumePcmSample}.");
            evidence.Add(new { SourceFrame = marker, ExpectedPcmFrame = expected, ActualPcmFrame = actual.Position, ExpectedValue = expectedAmplitude, ActualValue = actual.Value });
        }
        if (checkedBeforePause < 2 || checkedAfterResume < 2)
            throw new InvalidOperationException($"Insufficient native marker evidence on both sides of pause: before={checkedBeforePause}; after={checkedAfterResume}.");

        byte[] bytes = new byte[left.Count * sizeof(float)];
        var pcm = new float[left.Count * Channels];
        // Retain both channels so the evidence is independently inspectable.
        // The probe stores the left-channel stream and hashes its exact bytes.
        left.CopyTo(pcm, 0);
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        string pcmFile = "audio-semantics-left.f32le";
        File.WriteAllBytes(Path.Combine(Plugin.DiagnosticsRoot, pcmFile), bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "audio-semantics-evidence.json"), JsonSerializer.Serialize(new
        {
            Schema = 1, Rate, Channels, Lead, ClipFrames, Pitch, Volume, PauseAfterOutputSamples,
            DspBlock = dspBlock, DspBlocks = dspBlocks, PausePcmFrame = pausePcmSample, ResumePcmFrame = resumePcmSample,
            PauseCursor = pauseCursor, CursorWhilePaused = cursorWhilePaused, CursorAfterResume = cursorAfterResume,
            PauseWindowSilent = true, SourcePropertiesRestoredDuringProbe = source.pitch == Pitch && source.volume == Volume,
            CheckedBeforePause = checkedBeforePause, CheckedAfterResume = checkedAfterResume, Markers = evidence,
            PcmFile = pcmFile, PcmSha256 = hash, PcmSamplesPerChannel = left.Count,
            Scope = "Native Unity AudioSource and AudioRenderer diagnostic only; no production command hooks were enabled."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Report($"PATH-CHECK PASS audio-semantics-pitch-volume-pause-resume pitch={Pitch:R}; volume={Volume:R}; pause_cursor={pauseCursor}; pause_pcm={pausePcmSample}; resume_pcm={resumePcmSample}; markers_before={checkedBeforePause}; markers_after={checkedAfterResume}; pcm_sha256={hash}");
    }

    private (int Position, float Value) FindPulse(long expected, int radius)
    {
        int start = (int)Math.Max(0, expected - radius), end = Math.Min(left.Count, checked((int)expected + radius + 1));
        int best = -1; float value = 0;
        for (int i = start; i < end; i++)
            if (Math.Abs(left[i]) > Math.Abs(value)) { best = i; value = left[i]; }
        return (best, value);
    }

    [HideFromIl2Cpp]
    private void Finish()
    {
        if (finished) return;
        finished = true;
        bool cleanupOk = true;
        Restore(() => { if (renderer && !AudioRenderer.Stop()) throw new InvalidOperationException("AudioRenderer.Stop returned false."); renderer = false; }, ref cleanupOk);
        Restore(() => { if (source != null) source.Stop(); }, ref cleanupOk);
        Restore(() => { if (fixture != null) Object.Destroy(fixture); }, ref cleanupOk);
        Restore(() => { if (clip != null) Object.Destroy(clip); }, ref cleanupOk);
        foreach (var state in muted) Restore(() => { if (state.Source != null) state.Source.mute = state.Mute; }, ref cleanupOk);
        if (timeSnapshots)
        {
            Restore(() => Time.captureDeltaTime = oldDelta, ref cleanupOk); Restore(() => Time.timeScale = oldScale, ref cleanupOk);
            Restore(() => Application.targetFrameRate = oldFps, ref cleanupOk); Restore(() => QualitySettings.vSyncCount = oldVsync, ref cleanupOk);
            Restore(() => OnDemandRendering.renderFrameInterval = oldInterval, ref cleanupOk); Restore(() => Application.runInBackground = oldBackground, ref cleanupOk);
        }
        if (!cleanupOk)
            Restore(() => typeof(RenderControlV1).GetMethod("RestorationFailed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null), ref cleanupOk);
        else
            Restore(() => { ownership?.Dispose(); ownership = null; }, ref cleanupOk);

        bool passed = failure.Length == 0 && cleanupOk;
        if (!passed && failure.Length == 0) failure = "Native fixture cleanup failed.";
        try
        {
            File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "audio-semantics-result.json"), JsonSerializer.Serialize(new
            {
                Passed = passed, Failure = failure, CleanupOk = cleanupOk, RenderOwnedAfterCleanup = RenderControlV1.IsRenderingOwned,
                ProviderReadyAfterCleanup = RenderControlV1.IsReady, ElapsedWallSeconds = wall.Elapsed.TotalSeconds
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { passed = false; Plugin.Report("PATH-CHECK FAIL audio-semantics evidence-write: " + error.Message); }
        Plugin.Report(passed ? "PATH-CHECK PASS audio-semantics" : "PATH-CHECK FAIL audio-semantics result=" + failure);
        Plugin.Report($"PATH-CHECK RESULT {(passed ? 1 : 0)} passed; {(passed ? 0 : 1)} failed");
    }

    [HideFromIl2Cpp]
    private static void Restore(Action action, ref bool cleanupOk)
    {
        try { action(); }
        catch (Exception error) { cleanupOk = false; Plugin.Report("PATH-CHECK audio-semantics cleanup: " + error); }
    }

    public void OnDestroy() { if (!finished) Finish(); }
}
