using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Integration;
using AAVideoExport.Plugin;
using AAVideoExport.Core;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Temporary native experiment. A correctly sized buffer alone is not a timing pass.
public sealed class AudioClockChecks : MonoBehaviour
{
    private const int Rate = 48000, Fps = 30, PerFrame = 1600, ClipFrames = 4800, CaptureFrames = 12;
    private const BindingFlags Members = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly int[] Markers = { 0, 1599, 1600, 3199, 3200, ClipFrames - 1 };
    private static readonly float[] Amplitudes = { .25f, -.35f, .45f, -.55f, .65f, -.75f };
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly Stopwatch _experiment = new();
    private readonly List<(AudioSource Source, bool Mute)> _muted = new();
    private readonly List<ModeEvidence> _modes = new();
    private readonly List<string> _errors = new();
    private IDisposable? _ownership;
    private GameObject? _fixture;
    private AudioSource? _continuous, _event;
    private AudioClip? _continuousClip, _eventClip;
    private ModeEvidence? _current;
    private TimestampedAudioWindow? _window;
    private AudioFrameScheduler? _scheduler;
    private readonly List<float> _content = new();
    private bool _renderer, _snapshots, _finished, _cleanupFailed;
    private float _oldDelta, _oldScale;
    private int _oldFps, _oldVsync, _oldInterval, _channels, _stage, _modeIndex, _primeFrames, _frame;
    private bool _oldBackground;
    private int _dspBlock, _dspBlocks;
    private long _samples;

    public AudioClockChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (_finished) return;
        try
        {
            if (_stage == 0 ? _wall.Elapsed.TotalSeconds > 45 : _experiment.Elapsed.TotalSeconds > 5)
                throw new TimeoutException("Native audio probe exceeded its startup or five-second experiment deadline.");
            if (_stage == 0)
            {
                if (!RenderControlV1.IsReady || SceneManager.GetActiveScene().name != "CatalogScene") return;
                _experiment.Start();
                Initialize();
                _stage = 1;
                return;
            }
            MuteOtherSources();
            if (_stage == 1) BeginMode();
            else if (_stage == 2) Prime();
            else if (_stage == 3) Capture();
            else if (_stage == 4)
            {
                EndMode();
                if (++_modeIndex < 3) _stage = 1;
                else Finish();
            }
        }
        catch (Exception error)
        {
            _errors.Add(error.ToString());
            Plugin.Report("PATH-CHECK FAIL audio-clock: " + error);
            Finish();
        }
    }

    [HideFromIl2Cpp]
    private void Initialize()
    {
        if (RenderControlV1.IsRenderingOwned)
            throw new InvalidOperationException("Cannot run the audio probe while rendering is already owned.");
        _ownership = (IDisposable)typeof(RenderControlV1).GetMethod("Acquire", Members)!.Invoke(null, null)!;
        _oldDelta = Time.captureDeltaTime;
        _oldScale = Time.timeScale;
        _oldFps = Application.targetFrameRate;
        _oldVsync = QualitySettings.vSyncCount;
        _oldInterval = OnDemandRendering.renderFrameInterval;
        _oldBackground = Application.runInBackground;
        _snapshots = true;
        if (AudioSettings.outputSampleRate != Rate || AudioSettings.speakerMode != AudioSpeakerMode.Stereo)
            throw new InvalidOperationException("Probe requires the existing mixer to be 48000 Hz stereo; it never reconfigures the user's audio device.");
        _channels = 2;
        AudioSettings.GetDSPBufferSize(out _dspBlock, out _dspBlocks);
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        OnDemandRendering.renderFrameInterval = 1;
        Application.runInBackground = true;
        Time.timeScale = 1;
        Time.captureFramerate = Fps;
        MuteOtherSources();
        _fixture = new GameObject("AAVE temporary native audio clock probe");
        Object.DontDestroyOnLoad(_fixture);
        if (!Object.FindObjectsOfType<AudioListener>().Any(listener => listener != null && listener.enabled))
            _fixture.AddComponent<AudioListener>();
        _continuousClip = CreateClip("AAVE left continuity markers", 0);
        _eventClip = CreateClip("AAVE right frame-one event markers", 1);
        _continuous = CreateSource(_continuousClip);
        _event = CreateSource(_eventClip);
        Plugin.Report($"PATH-CHECK AUDIO-CLOCK begin rate={Rate}; channels={_channels}; dsp_block={_dspBlock}; dsp_blocks={_dspBlocks}; fps={Fps}");
    }

    [HideFromIl2Cpp]
    private AudioClip CreateClip(string name, int channel)
    {
        var samples = new float[ClipFrames * _channels];
        for (int i = 0; i < Markers.Length; i++) samples[Markers[i] * _channels + channel] = Amplitudes[i];
        var clip = AudioClip.Create(name, ClipFrames, _channels, Rate, false);
        if (clip == null || !clip.SetData(new Il2CppStructArray<float>(samples), 0))
            throw new InvalidOperationException("Native AudioClip.Create/SetData failed for " + name);
        return clip;
    }

    [HideFromIl2Cpp]
    private AudioSource CreateSource(AudioClip clip)
    {
        var source = _fixture!.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
        source.pitch = 1;
        source.volume = 1;
        source.mute = false;
        source.spatialBlend = 0;
        source.dopplerLevel = 0;
        source.priority = 0;
        source.ignoreListenerVolume = true;
        source.ignoreListenerPause = true;
        source.bypassEffects = true;
        source.bypassListenerEffects = true;
        source.bypassReverbZones = true;
        source.clip = clip;
        return source;
    }

    [HideFromIl2Cpp]
    private void MuteOtherSources()
    {
        foreach (var source in Object.FindObjectsOfType<AudioSource>(true))
        {
            if (source == null || source == _continuous || source == _event) continue;
            if (!_muted.Any(state => state.Source == source)) _muted.Add((source, source.mute));
            source.mute = true;
        }
    }

    [HideFromIl2Cpp]
    private void BeginMode()
    {
        _current = new ModeEvidence
        {
            Mode = _modeIndex switch { 0 => "advertised", 1 => "exact-1600", _ => "advertised-scheduled" },
            Scheduled = _modeIndex == 2,
            LeadSamples = _modeIndex == 2 ? checked(4 * _dspBlock) : 0
        };
        _modes.Add(_current);
        _window = _current.Scheduled ? new TimestampedAudioWindow(0, _current.LeadSamples, CaptureFrames * PerFrame, _channels) : null;
        _scheduler = null;
        _content.Clear();
        _samples = 0;
        _frame = 0;
        _primeFrames = 0;
        _continuous!.Stop();
        _event!.Stop();
        _continuous.timeSamples = 0;
        _event.timeSamples = 0;
        if (!AudioRenderer.Start()) throw new InvalidOperationException("Offline audio renderer did not start.");
        _renderer = true;
        _stage = 2;
    }

    [HideFromIl2Cpp]
    private void Prime()
    {
        int advertised = AudioRenderer.GetSampleCountForCaptureFrame();
        var read = Render(advertised, advertised);
        _current!.Prime.Add(read);
        if (!read.Success || read.UnwrittenValues != 0 || read.GuardChanged)
            throw new InvalidOperationException("Advertised-size renderer priming failed.");
        if (++_primeFrames == 4) _stage = 3;
    }

    [HideFromIl2Cpp]
    private void Capture()
    {
        var mode = _current!;
        bool draining = _frame == CaptureFrames;
        if (draining && mode.Drain.Count >= 8)
        {
            mode.Rejection = "Scheduled diagnostic did not cover its measured interval within eight native drain updates.";
            _stage = 4;
            return;
        }
        if (mode.Scheduled)
        {
            if (_frame == 0)
            {
                mode.DspEpoch = AudioSettings.dspTime;
                _scheduler = new AudioFrameScheduler(0, mode.LeadSamples, Fps, Rate, mode.DspEpoch.Value);
                Schedule(_continuous!, mode.LeadSamples);
            }
            if (_frame == 1) Schedule(_event!, mode.LeadSamples + PerFrame);
        }
        else
        {
            // The right-channel event is issued on a later video update. Its
            // placement is measured independently of the amount of PCM returned.
            if (_frame == 0) _continuous!.Play();
            if (_frame == 1) _event!.Play();
        }
        int advertised = AudioRenderer.GetSampleCountForCaptureFrame();
        int requested = _modeIndex == 1 ? PerFrame : advertised;
        var read = Render(requested, advertised);
        read.VideoFrame = _frame;
        read.AudioDrainOnly = draining;
        read.ExpectedVideoSample = (long)_frame * PerFrame;
        read.OutputStartSample = _samples;
        if (draining) mode.Drain.Add(read);
        else mode.Frames.Add(read);
        if (!read.Success || read.UnwrittenValues != 0 || read.GuardChanged)
        {
            mode.Rejection = "Native render rejected the request, left unwritten values, or changed the guard.";
            _stage = 4;
            return;
        }
        mode.Pcm.AddRange(read.Samples);
        if (_window != null)
        {
            double rawPosition = (read.DspBefore - mode.DspEpoch!.Value) * Rate;
            double rawEnd = (read.DspAfter - mode.DspEpoch.Value) * Rate;
            long rawStart = checked((long)Math.Round(rawPosition));
            if (Math.Abs(rawPosition - rawStart) >= .25 || Math.Abs(rawEnd - rawStart - requested) >= .25)
                throw new InvalidOperationException("Native DSP timestamps do not match the returned PCM interval.");
            var receipt = _window.Append(read.Samples, 0, read.Samples.Length, rawStart);
            if (receipt.ContentSlice != null) _content.AddRange(receipt.ContentSlice.Samples);
        }
        _samples += requested;
        // Only the diagnostic audio drain advances here. The fixture's video
        // cursor and event schedule remain at the end of the twelve-frame clip.
        if (!draining) _frame++;
        if (_frame == CaptureFrames
            && (!mode.Scheduled || _samples >= mode.LeadSamples + (long)CaptureFrames * PerFrame))
            _stage = 4;
    }

    [HideFromIl2Cpp]
    private void Schedule(AudioSource source, int expectedRawSample)
    {
        var planned = _scheduler!.Schedule(_frame);
        if (planned.RawSampleStart != expectedRawSample) throw new InvalidOperationException("Core scheduler disagrees with the independent expected event sample.");
        double requestedDsp = planned.DspStartSeconds;
        var schedule = new ScheduleEvidence
        {
            VideoFrame = _frame, ObservedDspAtCall = AudioSettings.dspTime,
            RequestedDspStart = requestedDsp, ExpectedRawSample = expectedRawSample
        };
        long endSample = ReferenceEquals(source, _continuous)
            ? checked(_current!.LeadSamples + (long)CaptureFrames * PerFrame)
            : checked(planned.RawSampleStart + ClipFrames);
        var segment = new StrictAudioSegment(source.Pointer.ToString(), "native-probe", planned.RawSampleStart,
            endSample, 0, false);
        var scheduled = new StrictScheduledAudioSegment(segment, requestedDsp, requestedDsp + (endSample - planned.RawSampleStart) / (double)Rate);
        StrictAudioDspDispatcher.Schedule(source, scheduled);
        _current!.Schedule.Add(schedule);
    }

    [HideFromIl2Cpp]
    private unsafe RenderEvidence Render(int requested, int advertised)
    {
        if (requested < 0 || advertised < 0 || Math.Max(requested, advertised) > Rate)
            throw new InvalidOperationException("Invalid native render sample count.");
        int values = checked(requested * _channels);
        // A larger backing allocation lets the probe detect an ignored length
        // without placing an immediately adjacent managed object at risk.
        var buffer = Enumerable.Repeat(float.NaN, checked(Math.Max(requested, advertised) * _channels + 64)).ToArray();
        var evidence = new RenderEvidence
        {
            AdvertisedSamples = advertised, RequestedSamples = requested,
            DspBefore = AudioSettings.dspTime,
            ContinuousPositionBefore = _continuous!.timeSamples, ContinuousPlayingBefore = _continuous.isPlaying,
            EventPositionBefore = _event!.timeSamples, EventPlayingBefore = _event.isPlaying
        };
        fixed (float* pointer = buffer) evidence.Success = AudioRenderer.Internal_AudioRenderer_Render(pointer, values);
        evidence.DspAfter = AudioSettings.dspTime;
        evidence.ContinuousPositionAfter = _continuous.timeSamples;
        evidence.ContinuousPlayingAfter = _continuous.isPlaying;
        evidence.EventPositionAfter = _event.timeSamples;
        evidence.EventPlayingAfter = _event.isPlaying;
        evidence.UnwrittenValues = buffer.Take(values).Count(float.IsNaN);
        evidence.GuardChanged = buffer.Skip(values).Any(value => !float.IsNaN(value));
        evidence.Samples = buffer.Take(values).ToArray();
        return evidence;
    }

    [HideFromIl2Cpp]
    private void EndMode()
    {
        _continuous?.Stop();
        _event?.Stop();
        if (_renderer)
        {
            if (!AudioRenderer.Stop()) throw new InvalidOperationException("Offline audio renderer did not stop.");
            _renderer = false;
        }
        if (_current == null || _current.Analyzed) return;
        _current.Analyzed = true;
        _current.OutputSamplesPerChannel = _current.Pcm.Count / Math.Max(1, _channels);
        _current.ExpectedSamplesPerChannel = (long)_current.Frames.Count * PerFrame;
        var pcm = _current.Pcm.ToArray();
        var bytes = new byte[checked(pcm.Length * sizeof(float))];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        _current.PcmFile = "audio-clock-" + _current.Mode + ".f32le";
        File.WriteAllBytes(Path.Combine(Plugin.DiagnosticsRoot, _current.PcmFile), bytes);
        _current.PcmSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        _current.Left = Analyze(pcm, 0, _current.LeadSamples);
        _current.Right = Analyze(pcm, 1, _current.LeadSamples + PerFrame);
        _current.SampleCountExact = _current.Frames.Count == CaptureFrames
            && _current.OutputSamplesPerChannel == _current.ExpectedSamplesPerChannel;
        _current.SignalAligned = _current.Left.ExactExpectedPositions && _current.Right.ExactExpectedPositions;
        _current.StrictPass = _current.SampleCountExact && _current.SignalAligned && _current.Rejection.Length == 0;
        if (_current.Scheduled)
        {
            _current.MeasuredIntervalStart = _current.LeadSamples;
            _current.MeasuredIntervalEndExclusive = _current.LeadSamples + (long)CaptureFrames * PerFrame;
            _current.MeasuredIntervalCovered = _current.OutputSamplesPerChannel >= _current.MeasuredIntervalEndExclusive;
            // Keep every native sample: the measurement window is an explicit
            // interval in the raw capture, with the lead and overshoot retained.
            _current.Segments.Add(SaveSegment(bytes, "lead", 0, _current.MeasuredIntervalStart));
            _current.Segments.Add(SaveSegment(bytes, "measured-interval", _current.MeasuredIntervalStart, _current.MeasuredIntervalEndExclusive));
            _current.Segments.Add(SaveSegment(bytes, "trailing-native-buffer", _current.MeasuredIntervalEndExclusive, _current.OutputSamplesPerChannel));
            _current.SchedulingDiagnosticPass = _current.MeasuredIntervalCovered
                && _current.Frames.Count == CaptureFrames && _current.Schedule.Count == 2
                && _current.Left.NonStartMarkersExact && _current.Right.NonStartMarkersExact
                && _current.Left.UnexpectedNonzeroCount == 0 && _current.Right.UnexpectedNonzeroCount == 0
                && _current.Left.NonFiniteCount == 0 && _current.Right.NonFiniteCount == 0
                && _current.Rejection.Length == 0;
            if (_current.MeasuredIntervalCovered && _current.Rejection.Length == 0)
            {
                _window!.Complete();
                _current.CoreContentSamples = _window.ContentSampleFramesEmitted;
                _current.CoreLeadSamples = _window.RawLeadSampleFramesAcknowledged;
                _current.CoreTrailingSamples = _window.RawTrailingSampleFrames;
                var content = _content.ToArray();
                var contentBytes = new byte[checked(content.Length * sizeof(float))];
                Buffer.BlockCopy(content, 0, contentBytes, 0, contentBytes.Length);
                string filename = "audio-clock-core-window.f32le";
                File.WriteAllBytes(Path.Combine(Plugin.DiagnosticsRoot, filename), contentBytes);
                _current.CoreWindowFile = filename;
                _current.CoreWindowPass = _current.CoreContentSamples == CaptureFrames * PerFrame
                    && Analyze(content, 0, 0).ExactExpectedPositions && Analyze(content, 1, PerFrame).ExactExpectedPositions
                    && content.SequenceEqual(pcm.Skip(_current.LeadSamples * _channels).Take(content.Length))
                    && _current.CoreLeadSamples + _current.CoreContentSamples + _current.CoreTrailingSamples == _current.OutputSamplesPerChannel;
                using var prepared = new PreparedAudioTrack(Path.Combine(Plugin.DiagnosticsRoot, "audio-clock-prepared.pcm"), Fps, Rate, _channels);
                prepared.Append(content, content.Length);
                prepared.Seal(CaptureFrames);
                for (int frame = 0; frame < CaptureFrames; frame++)
                {
                    var replay = prepared.ReadFrame(frame);
                    if (!replay.Samples.SequenceEqual(content.Skip(frame * PerFrame * _channels).Take(PerFrame * _channels)))
                        throw new InvalidOperationException("Prepared audio replay changed native samples.");
                }
                prepared.CompleteReplay();
                _current.CorePreparedPcmHash = prepared.SamplesSha256;
                Plugin.Report($"PATH-CHECK AUDIO-CLOCK CORE-WINDOW {(_current.CoreWindowPass ? "PASS" : "FAIL")}; content={_current.CoreContentSamples}; lead={_current.CoreLeadSamples}; tail={_current.CoreTrailingSamples}; whole_story_verified=false");
            }
            Plugin.Report($"PATH-CHECK AUDIO-CLOCK SCHEDULING {( _current.SchedulingDiagnosticPass ? "PASS" : "FAIL")}; lead={_current.LeadSamples}; interval=[{_current.MeasuredIntervalStart},{_current.MeasuredIntervalEndExclusive}); covered={_current.MeasuredIntervalCovered}; drain_updates={_current.Drain.Count}; left_start_missing={_current.Left.StartMarkerMissing}; right_start_missing={_current.Right.StartMarkerMissing}; left_unexpected={_current.Left.UnexpectedNonzeroCount}; right_unexpected={_current.Right.UnexpectedNonzeroCount}; diagnostic_only=true");
        }
        Plugin.Report($"PATH-CHECK AUDIO-CLOCK mode={_current.Mode}; rendered={_current.OutputSamplesPerChannel}; expected={_current.ExpectedSamplesPerChannel}; left_first={_current.Left.FirstNonzero}; right_first={_current.Right.FirstNonzero}; exact_signal={_current.SignalAligned}; strict_pass={_current.StrictPass}; rejection={_current.Rejection}");
    }

    [HideFromIl2Cpp]
    private SegmentEvidence SaveSegment(byte[] bytes, string name, long requestedStart, long requestedEnd)
    {
        long available = bytes.LongLength / (sizeof(float) * _channels);
        long start = Math.Min(requestedStart, available);
        long end = Math.Max(start, Math.Min(requestedEnd, available));
        var part = new byte[checked((int)((end - start) * _channels * sizeof(float)))];
        Buffer.BlockCopy(bytes, checked((int)(start * _channels * sizeof(float))), part, 0, part.Length);
        string filename = "audio-clock-" + _current!.Mode + "-" + name + ".f32le";
        File.WriteAllBytes(Path.Combine(Plugin.DiagnosticsRoot, filename), part);
        return new SegmentEvidence
        {
            Name = name, RequestedStart = requestedStart, RequestedEndExclusive = requestedEnd,
            ActualStart = start, ActualEndExclusive = end, SamplesPerChannel = end - start,
            File = filename, Sha256 = Convert.ToHexString(SHA256.HashData(part)).ToLowerInvariant()
        };
    }

    [HideFromIl2Cpp]
    private ChannelEvidence Analyze(float[] pcm, int channel, int start)
    {
        var result = new ChannelEvidence { ExpectedPositions = Markers.Select(sample => sample + start).ToArray() };
        for (int sample = 0; sample < pcm.Length / _channels; sample++)
        {
            float value = pcm[sample * _channels + channel];
            if (!float.IsFinite(value)) { result.NonFiniteCount++; continue; }
            if (Math.Abs(value) < .00001f) continue;
            if (result.FirstNonzero < 0) result.FirstNonzero = sample;
            result.NonzeroCount++;
            if (!result.ExpectedPositions.Contains(sample)) result.UnexpectedNonzeroCount++;
            if (result.Nonzero.Count < 256) result.Nonzero.Add(new Pulse { Position = sample, Value = value });
        }
        result.ExactExpectedPositions = result.NonzeroCount == Markers.Length
            && result.Nonzero.Select(pulse => pulse.Position).SequenceEqual(result.ExpectedPositions);
        result.FirstOffset = result.FirstNonzero < 0 ? null : result.FirstNonzero - start;
        result.StartMarkerMissing = !result.Nonzero.Any(pulse => pulse.Position == start);
        result.NonStartMarkersExact = result.NonzeroCount == result.Nonzero.Count
            && result.Nonzero.Where(pulse => pulse.Position != start).Select(pulse => pulse.Position)
                .SequenceEqual(result.ExpectedPositions.Skip(1));
        return result;
    }

    [HideFromIl2Cpp]
    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        TryRestore(EndMode);
        if (_renderer) TryRestore(() => { if (!AudioRenderer.Stop()) throw new InvalidOperationException("Cleanup AudioRenderer.Stop returned false."); _renderer = false; });
        TryRestore(() => { if (_fixture != null) Object.Destroy(_fixture); });
        TryRestore(() => { if (_continuousClip != null) Object.Destroy(_continuousClip); });
        TryRestore(() => { if (_eventClip != null) Object.Destroy(_eventClip); });
        foreach (var state in _muted)
            TryRestore(() => { if (state.Source != null) state.Source.mute = state.Mute; });
        if (_snapshots)
        {
            TryRestore(() => Time.captureDeltaTime = _oldDelta);
            TryRestore(() => Time.timeScale = _oldScale);
            TryRestore(() => Application.targetFrameRate = _oldFps);
            TryRestore(() => QualitySettings.vSyncCount = _oldVsync);
            TryRestore(() => OnDemandRendering.renderFrameInterval = _oldInterval);
            TryRestore(() => Application.runInBackground = _oldBackground);
        }
        if (_cleanupFailed && _ownership != null)
            TryRestore(() => typeof(RenderControlV1).GetMethod("RestorationFailed", Members)!.Invoke(null, null));
        else
            TryRestore(() => { _ownership?.Dispose(); _ownership = null; });
        bool passed = _errors.Count == 0 && _modes.Count == 3 && _modes[1].StrictPass;
        bool scheduledDiagnosticPassed = _errors.Count == 0 && _modes.Count == 3 && _modes[2].SchedulingDiagnosticPass;
        try
        {
            File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "audio-clock-evidence.json"), JsonSerializer.Serialize(new
            {
                Schema = 3, Rate, Fps, Channels = _channels, PerFrame, ClipFrames, CaptureFrames,
                MarkerPositions = Markers, MarkerAmplitudes = Amplitudes, EventVideoFrame = 1,
                NonzeroDetectionThreshold = .00001f,
                DspBlock = _dspBlock, DspBlocks = _dspBlocks, ElapsedWallSeconds = _wall.Elapsed.TotalSeconds,
                Scope = "Native Unity AudioSource and AudioRenderer experiment; no production timing correction or PCM padding/trimming.",
                PassRequires = "All six left markers at their authored samples, all six right markers at authored samples plus 1600, and exact total sample count.",
                SchedulingDiagnosticRequires = "All five non-start markers per channel at lead plus authored position (plus 1600 for the second source), no unexpected nonzero samples, and native coverage of [lead, lead+19200). Missing start markers are recorded separately and prevent a full strict pass.",
                SchedulingDiagnosticLimit = "A passing scheduled-source experiment does not establish a complete strict export pipeline. The full raw PCM, lead, measured interval, and trailing native buffer are retained; no source samples are invented or silently discarded.",
                MutesRestored = _muted.Count, RenderOwnedAfterCleanup = RenderControlV1.IsRenderingOwned,
                ProviderReadyAfterCleanup = RenderControlV1.IsReady, Modes = _modes, Errors = _errors,
                ScheduledDiagnosticPassed = scheduledDiagnosticPassed, Passed = passed
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            passed = false;
            scheduledDiagnosticPassed = false;
            Plugin.Report("PATH-CHECK FAIL audio-clock evidence: " + error);
        }
        Plugin.Report(passed ? "PATH-CHECK PASS audio-clock-exact-samples-and-event-markers" : "PATH-CHECK FAIL audio-clock strict native signal alignment not established; inspect audio-clock-evidence.json");
        Plugin.Report(scheduledDiagnosticPassed ? "PATH-CHECK PASS audio-clock-scheduled-non-start-markers-diagnostic-only" : "PATH-CHECK FAIL audio-clock-scheduled-non-start-markers-diagnostic-only");
        Plugin.Report($"PATH-CHECK RESULT {(passed ? 1 : 0) + (scheduledDiagnosticPassed ? 1 : 0)} passed; {(passed ? 0 : 1) + (scheduledDiagnosticPassed ? 0 : 1)} failed");
    }

    [HideFromIl2Cpp]
    private void TryRestore(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            _cleanupFailed = true;
            _errors.Add("Cleanup: " + error);
            Plugin.Report("PATH-CHECK AUDIO-CLOCK cleanup: " + error);
        }
    }

    public void OnDestroy() { if (!_finished) Finish(); }

    private sealed class ModeEvidence
    {
        public string Mode { get; set; } = "";
        public bool Scheduled { get; set; }
        public int LeadSamples { get; set; }
        public double? DspEpoch { get; set; }
        public List<ScheduleEvidence> Schedule { get; set; } = new();
        public List<RenderEvidence> Prime { get; set; } = new();
        public List<RenderEvidence> Frames { get; set; } = new();
        public List<RenderEvidence> Drain { get; set; } = new();
        [System.Text.Json.Serialization.JsonIgnore] public List<float> Pcm { get; } = new();
        public string Rejection { get; set; } = "";
        public bool Analyzed { get; set; }
        public long OutputSamplesPerChannel { get; set; }
        public long ExpectedSamplesPerChannel { get; set; }
        public string PcmFile { get; set; } = "";
        public string PcmSha256 { get; set; } = "";
        public ChannelEvidence Left { get; set; } = new();
        public ChannelEvidence Right { get; set; } = new();
        public bool SampleCountExact { get; set; }
        public bool SignalAligned { get; set; }
        public bool StrictPass { get; set; }
        public long MeasuredIntervalStart { get; set; }
        public long MeasuredIntervalEndExclusive { get; set; }
        public bool MeasuredIntervalCovered { get; set; }
        public bool SchedulingDiagnosticPass { get; set; }
        public bool CoreWindowPass { get; set; }
        public long CoreContentSamples { get; set; }
        public long CoreLeadSamples { get; set; }
        public long CoreTrailingSamples { get; set; }
        public string CoreWindowFile { get; set; } = "";
        public string CorePreparedPcmHash { get; set; } = "";
        public List<SegmentEvidence> Segments { get; set; } = new();
    }

    private sealed class RenderEvidence
    {
        public int VideoFrame { get; set; } = -1;
        public bool AudioDrainOnly { get; set; }
        public long ExpectedVideoSample { get; set; }
        public long OutputStartSample { get; set; }
        public int AdvertisedSamples { get; set; }
        public int RequestedSamples { get; set; }
        public bool Success { get; set; }
        public int UnwrittenValues { get; set; }
        public bool GuardChanged { get; set; }
        public double DspBefore { get; set; }
        public double DspAfter { get; set; }
        public int ContinuousPositionBefore { get; set; }
        public int ContinuousPositionAfter { get; set; }
        public bool ContinuousPlayingBefore { get; set; }
        public bool ContinuousPlayingAfter { get; set; }
        public int EventPositionBefore { get; set; }
        public int EventPositionAfter { get; set; }
        public bool EventPlayingBefore { get; set; }
        public bool EventPlayingAfter { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public float[] Samples { get; set; } = Array.Empty<float>();
    }

    private sealed class ChannelEvidence
    {
        public int[] ExpectedPositions { get; set; } = Array.Empty<int>();
        public List<Pulse> Nonzero { get; set; } = new();
        public int NonzeroCount { get; set; }
        public int FirstNonzero { get; set; } = -1;
        public int? FirstOffset { get; set; }
        public bool ExactExpectedPositions { get; set; }
        public bool StartMarkerMissing { get; set; }
        public bool NonStartMarkersExact { get; set; }
        public int UnexpectedNonzeroCount { get; set; }
        public int NonFiniteCount { get; set; }
    }

    private sealed class ScheduleEvidence
    {
        public int VideoFrame { get; set; }
        public double ObservedDspAtCall { get; set; }
        public double RequestedDspStart { get; set; }
        public int ExpectedRawSample { get; set; }
    }

    private sealed class SegmentEvidence
    {
        public string Name { get; set; } = "";
        public long RequestedStart { get; set; }
        public long RequestedEndExclusive { get; set; }
        public long ActualStart { get; set; }
        public long ActualEndExclusive { get; set; }
        public long SamplesPerChannel { get; set; }
        public string File { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    private sealed class Pulse
    {
        public int Position { get; set; }
        public float Value { get; set; }
    }
}
