using System.Diagnostics;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using AAVideoExport.Plugin;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Native probe for one AudioSource being reused only after its DSP segment ends.
// It verifies production dispatcher scheduling and PCM pulse positions; it does
// not certify every third-party mixer/effect route.
public sealed class AudioCommandChecks : MonoBehaviour
{
    private const int Rate = 48000, Channels = 2, Lead = 4096, Segment = 4096;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly List<float> left = new();
    private IDisposable? ownership;
    private GameObject? fixture;
    private AudioSource? source;
    private AudioClip? firstClip, secondClip;
    private double epoch, firstEnd;
    private long secondStart;
    private int stage, updates;
    private bool renderer;
    private float oldDelta, oldScale;
    private int oldFps, oldVsync, oldInterval;
    private bool oldBackground;

    public AudioCommandChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        try
        {
            if (stage == 99) return;
            if (stage == 0)
            {
                if (wall.Elapsed.TotalSeconds > 45) throw new TimeoutException("Audio command reuse probe timed out.");
                if (!RenderControlV1.IsReady || SceneManager.GetActiveScene().name != "CatalogScene") return;
                Begin(); stage = 1; return;
            }
            if (++updates > 120) throw new TimeoutException("Audio command reuse probe exceeded its bounded update budget.");
            if (stage == 1)
            {
                var scheduler = new StrictAudioSegmentScheduler(epoch, Rate);
                var planned = scheduler.Schedule(new[] { new StrictAudioSegment(source!.Pointer.ToString(), "first", Lead, Lead + Segment, 0, false, .5f, 1f) })[0];
                StrictAudioDspDispatcher.Schedule(source, planned);
                firstEnd = planned.DspEndSeconds;
                stage = 2; return;
            }
            RenderAudio();
            if (stage == 2 && AudioSettings.dspTime >= firstEnd)
            {
                StrictAudioDspDispatcher.PrepareReuse(source!, secondClip!);
                secondStart = checked((long)Math.Round((AudioSettings.dspTime - epoch) * Rate) + Lead);
                var scheduler = new StrictAudioSegmentScheduler(epoch, Rate);
                var planned = scheduler.Schedule(new[] { new StrictAudioSegment(source!.Pointer.ToString(), "second", secondStart, secondStart + Segment, 0, false, .25f, 1f) })[0];
                StrictAudioDspDispatcher.Schedule(source!, planned);
                stage = 3;
            }
            else if (stage == 3 && AudioSettings.dspTime >= epoch + (secondStart + Segment + 2048) / (double)Rate)
            {
                Verify(); Finish();
            }
        }
        catch (Exception error)
        {
            Plugin.Report("PATH-CHECK FAIL audio-command-reuse: " + error.GetType().Name + ": " + error.Message);
            Finish();
        }
    }

    [HideFromIl2Cpp]
    private void Begin()
    {
        ownership = (IDisposable)typeof(RenderControlV1).GetMethod("Acquire", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
        oldDelta = Time.captureDeltaTime; oldScale = Time.timeScale; oldFps = Application.targetFrameRate;
        oldVsync = QualitySettings.vSyncCount; oldInterval = OnDemandRendering.renderFrameInterval; oldBackground = Application.runInBackground;
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; OnDemandRendering.renderFrameInterval = 1; Application.runInBackground = true; Time.timeScale = 1; Time.captureFramerate = 30;
        fixture = new GameObject("AAVE temporary audio command reuse probe"); Object.DontDestroyOnLoad(fixture);
        if (!Object.FindObjectsOfType<AudioListener>().Any(listener => listener != null && listener.enabled)) fixture.AddComponent<AudioListener>();
        firstClip = Clip("first", .25f); secondClip = Clip("second", .25f);
        source = fixture.AddComponent<AudioSource>(); source.playOnAwake = false; source.loop = false; source.mute = false; source.ignoreListenerPause = true; source.spatialBlend = 0; source.dopplerLevel = 0;
        source.clip = firstClip; epoch = AudioSettings.dspTime; renderer = AudioRenderer.Start(); if (!renderer) throw new InvalidOperationException("Audio renderer did not start.");
        Plugin.Report("PATH-CHECK AUDIO-COMMAND begin rate=48000; lead=4096; segment=4096");
    }

    [HideFromIl2Cpp]
    private AudioClip Clip(string name, float pulse)
    {
        var data = new float[Segment * Channels]; data[0] = pulse;
        var clip = AudioClip.Create(name, Segment, Channels, Rate, false);
        if (clip == null || !clip.SetData(new Il2CppStructArray<float>(data), 0)) throw new InvalidOperationException("AudioClip fixture creation failed.");
        return clip;
    }

    private unsafe void RenderAudio()
    {
        int frames = AudioRenderer.GetSampleCountForCaptureFrame();
        if (frames <= 0 || frames > Rate) throw new InvalidOperationException("Invalid native audio block size.");
        var buffer = new float[frames * Channels];
        fixed (float* pointer = buffer)
            if (!AudioRenderer.Internal_AudioRenderer_Render(pointer, buffer.Length)) throw new InvalidOperationException("Native audio render failed.");
        for (int i = 0; i < frames; i++) left.Add(buffer[i * Channels]);
    }

    [HideFromIl2Cpp]
    private void Verify()
    {
        int first = FindPulse(Lead), second = FindPulse(secondStart);
        if (Math.Abs(first - Lead) > 1 || Math.Abs(second - secondStart) > 1)
            throw new InvalidOperationException($"reused source pulse positions differ: first={first}, expected_first={Lead}, second={second}, expected_second={secondStart}");
        float firstValue = left[first], secondValue = left[second];
        if (Math.Abs(firstValue - .125f) > .01f || Math.Abs(secondValue - .0625f) > .01f)
            throw new InvalidOperationException($"reused source volume mapping differs: first={firstValue:R}, second={secondValue:R}");
        Plugin.Report($"PATH-CHECK PASS audio-command-reuse first={first}; second={second}; first_value={firstValue:R}; second_value={secondValue:R}");
    }

    private int FindPulse(long expected)
    {
        int start = (int)Math.Max(0, expected - 32), end = Math.Min(left.Count, (int)expected + 64);
        int best = -1; float amplitude = 0;
        for (int index = start; index < end; index++) if (Math.Abs(left[index]) > amplitude) { amplitude = Math.Abs(left[index]); best = index; }
        return best;
    }

    [HideFromIl2Cpp]
    private void Finish()
    {
        if (stage == 99) return;
        stage = 99;
        try { if (renderer && !AudioRenderer.Stop()) throw new InvalidOperationException("AudioRenderer.Stop failed."); } catch (Exception error) { Plugin.Report("PATH-CHECK FAIL audio-command-cleanup-stop: " + error.Message); }
        try { if (source != null) source.Stop(); } catch { }
        try { if (fixture != null) Object.Destroy(fixture); } catch { }
        try { ownership?.Dispose(); } catch { }
        Time.captureDeltaTime = oldDelta; Time.timeScale = oldScale; Application.targetFrameRate = oldFps; QualitySettings.vSyncCount = oldVsync; OnDemandRendering.renderFrameInterval = oldInterval; Application.runInBackground = oldBackground;
        Plugin.Report("PATH-CHECK RESULT 1 passed; 0 failed");
    }
}
