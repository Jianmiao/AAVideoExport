using UnityEngine;

namespace AAVideoExport.Plugin;

// Native Auto and selection countdowns use unscaled time. Keep them on the
// same frame clock as capture without replacing AA's playback coroutines.
internal static class NativeExportClock
{
    private struct WaitEntry
    {
        public WaitForSecondsRealtime Wait;
        public double? Deadline;
        public long? FrameDeadline;
        public Func<long>? ReadFrame;
        public int Fps;
        public WaitEntry(WaitForSecondsRealtime wait) => Wait = wait;
    }

    // Retain the native wrappers until restoration so an address cannot be
    // collected/reused while its pending deadline is owned by this session.
    private static readonly Dictionary<IntPtr, WaitEntry> Waits = new();
    private static Func<float>? _readVirtualTime;
    private static int _snapshotFrame;
    private static float _now;
    private static float _realtimeOrigin;
    private static float _virtualStart;
    private static float _delta;
    private static Func<long>? _readTimelineFrame;
    private static Func<bool>? _readAdvancing;
    private static int _timelineFps;
    private static long _timelineFrame, _lastRawFrame, _lastAdvancedFrame;
    private static double _elapsedSeconds;
    private static bool _timelineStarted, _hasAdvancedFrame, _snapshotAdvancing;
    internal static bool IsActive { get; private set; }
    internal static bool IsTimelineActive => IsActive && _readTimelineFrame != null;
    internal static float Delta { get { if (IsTimelineActive) RefreshSnapshot(); return _delta; } }
    internal static long Frame { get { RefreshSnapshot(); return _timelineFrame; } }
    internal static double ElapsedSeconds { get { RefreshSnapshot(); return _elapsedSeconds; } }
    internal static long RegisteredWaitCount { get; private set; }
    internal static int ActiveWaitCount => Waits.Count;
    internal static int PeakActiveWaitCount { get; private set; }
    internal static long WaitPollCount { get; private set; }
    internal static long UnscaledTimeReadCount { get; private set; }
    internal static long UnscaledDeltaReadCount { get; private set; }

    internal static float Now
    {
        get
        {
            RefreshSnapshot();
            return _now;
        }
    }

    private static void RefreshSnapshot()
    {
        int frame = Time.frameCount;
        if (!IsActive || frame == _snapshotFrame) return;
        if (_readTimelineFrame != null) PublishTimelineFrame(resume: false);
        else
        {
            _snapshotFrame = frame;
            _now = _readVirtualTime!();
        }
    }

    // Begin held: mixer priming and resource work must not advance authored
    // consumers, even when Unity continues to run native updates. The host
    // explicitly resumes just before releasing the first authored event.
    internal static void BeginTimeline(int fps, Func<long> readFrame, Func<bool> advancing)
    {
        if (IsActive) throw new InvalidOperationException("An export clock is already active.");
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        ArgumentNullException.ThrowIfNull(readFrame);
        ArgumentNullException.ThrowIfNull(advancing);
        long frame = readFrame();
        if (frame < 0) throw new ArgumentOutOfRangeException(nameof(readFrame));
        Begin((float)(frame / (double)fps), 1f / fps, () => 0);
        _readVirtualTime = null;
        _readTimelineFrame = readFrame;
        _readAdvancing = advancing;
        _timelineFps = fps;
        _timelineFrame = _lastRawFrame = frame;
        _elapsedSeconds = frame / (double)fps;
        _timelineStarted = _hasAdvancedFrame = _snapshotAdvancing = false;
        _delta = 0;
    }

    // This is the sole intentional same-Unity-frame publication boundary.
    // A priming read may already have latched a held snapshot; publish frame
    // zero before the original AdvanceScenario starts any authored effect.
    // Repeated calls cannot mint another tick for an already published frame.
    internal static void ResumeTimelineFrame()
    {
        if (!IsTimelineActive) throw new InvalidOperationException("A shared export timeline is not active.");
        if (_timelineStarted && _snapshotAdvancing && _snapshotFrame == Time.frameCount) return;
        PublishTimelineFrame(resume: true);
    }

    private static void PublishTimelineFrame(bool resume)
    {
        long raw = _readTimelineFrame!();
        bool advancing = _readAdvancing!();
        if (raw < 0 || raw < _lastRawFrame)
            throw new InvalidOperationException("The export frame clock moved backwards.");
        if (resume && !advancing)
            throw new InvalidOperationException("The export timeline cannot resume while its host is held.");
        bool publish = advancing && (_timelineStarted || resume);
        bool step = publish && (!_hasAdvancedFrame || raw > _lastAdvancedFrame);
        // Validate the entire input before changing any visible snapshot.
        _lastRawFrame = raw;
        _snapshotFrame = Time.frameCount;
        _snapshotAdvancing = publish;
        _timelineStarted |= resume;
        _delta = step ? 1f / _timelineFps : 0;
        if (!step) return;
        _timelineFrame = _lastAdvancedFrame = raw;
        _elapsedSeconds = raw / (double)_timelineFps;
        _now = (float)_elapsedSeconds;
        _hasAdvancedFrame = true;
    }

    internal static void Begin(float initialVirtualTime, float frameDelta, Func<float> readVirtualTime)
    {
        if (IsActive) throw new InvalidOperationException("An export clock is already active.");
        if (!float.IsFinite(initialVirtualTime) || !float.IsFinite(frameDelta) || frameDelta <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameDelta));
        ArgumentNullException.ThrowIfNull(readVirtualTime);
        Waits.Clear();
        NativeScaledWaitHooks.ClearAuthoredWaits();
        // An existing native wait has an absolute realtime deadline. Map it
        // from the entry baseline even when its first export poll is delayed.
        _realtimeOrigin = Time.realtimeSinceStartup;
        _virtualStart = initialVirtualTime;
        _now = initialVirtualTime;
        _delta = frameDelta;
        _readVirtualTime = readVirtualTime;
        _readTimelineFrame = null;
        _readAdvancing = null;
        _snapshotFrame = Time.frameCount;
        RegisteredWaitCount = WaitPollCount = UnscaledTimeReadCount = UnscaledDeltaReadCount = 0;
        PeakActiveWaitCount = 0;
        IsActive = true;
    }

    internal static float ReadTime() { UnscaledTimeReadCount++; return Now; }
    internal static float ReadDelta() { UnscaledDeltaReadCount++; return Delta; }
    internal static void RegisterFrameWait(WaitForSecondsRealtime wait, float seconds, long frame, int fps, Func<long> readFrame)
    {
        if (!IsActive || !float.IsFinite(seconds) || seconds < 0 || frame < 0 || fps <= 0)
            throw new InvalidOperationException("Invalid frame-owned native wait.");
        // General native waits have only float seconds. Authored #wait values
        // take the separate integer path, before this lossy representation.
        long ticks = checked((long)Math.Round((double)seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero));
        long duration = Math.Max(1L, checked((ticks * fps + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond));
        RegisterFrameDuration(wait, duration, frame, fps, readFrame);
    }

    internal static void RegisterFrameWaitMilliseconds(WaitForSecondsRealtime wait, long milliseconds, long frame, int fps, Func<long> readFrame)
    {
        if (!IsActive || milliseconds < 0 || frame < 0 || fps <= 0)
            throw new InvalidOperationException("Invalid authored frame-owned native wait.");
        // Split before multiplying so valid long durations do not overflow
        // merely because milliseconds * fps would. Ceil only the remainder.
        long duration = Math.Max(1L, checked(milliseconds / 1000 * fps
            + (milliseconds % 1000 * fps + 999) / 1000));
        RegisterFrameDuration(wait, duration, frame, fps, readFrame);
    }

    private static void RegisterFrameDuration(WaitForSecondsRealtime wait, long duration, long frame, int fps, Func<long> readFrame)
    {
        ArgumentNullException.ThrowIfNull(readFrame);
        Waits[wait.Pointer] = new WaitEntry(wait)
        {
            FrameDeadline = checked(frame + duration), ReadFrame = readFrame, Fps = fps
        };
        RegisteredWaitCount++;
        if (Waits.Count > PeakActiveWaitCount) PeakActiveWaitCount = Waits.Count;
    }
    internal static bool TryKeepWaiting(WaitForSecondsRealtime wait, out bool keepWaiting)
    {
        keepWaiting = false;
        if (!IsActive) return false;
        double now = Now;
        if (!Waits.TryGetValue(wait.Pointer, out var entry))
        {
            // Constructor hooks are unsupported by the installed IL2CPP
            // Harmony backend. Adopt at the actual coroutine polling boundary.
            // A wait already running before capture keeps its remaining time.
            entry = new WaitEntry(wait);
            float nativeDeadline = wait.m_WaitUntilTime;
            if (nativeDeadline >= 0)
                entry.Deadline = _virtualStart + Math.Max(0, (double)nativeDeadline - _realtimeOrigin);
            Waits.Add(wait.Pointer, entry);
            RegisteredWaitCount++;
            if (Waits.Count > PeakActiveWaitCount) PeakActiveWaitCount = Waits.Count;
        }
        WaitPollCount++;
        if (entry.FrameDeadline is long deadlineFrame)
        {
            keepWaiting = entry.ReadFrame!() < deadlineFrame;
            if (!keepWaiting) { Waits.Remove(wait.Pointer); wait.Reset(); }
            return true;
        }
        // Unity starts its timer on the first poll, not in the constructor.
        // A subsequent waitTime assignment does not change an active timer.
        entry.Deadline ??= now + wait.waitTime;
        keepWaiting = now < entry.Deadline.Value;
        if (!keepWaiting)
        {
            // Completed waits no longer need to be kept alive for restoration.
            // Retaining every historical WaitForSecondsRealtime wrapper made
            // long exports grow the dictionary and managed memory over time.
            Waits.Remove(wait.Pointer);
            wait.Reset();
        }
        else Waits[wait.Pointer] = entry;
        return true;
    }

    internal static void Reset(WaitForSecondsRealtime wait)
    {
        if (IsActive && Waits.TryGetValue(wait.Pointer, out var entry))
        {
            entry.Deadline = null;
            entry.FrameDeadline = null; entry.ReadFrame = null;
            Waits[wait.Pointer] = entry;
        }
    }

    internal static void Restore()
    {
        if (!IsActive) return;
        double virtualNow = Now;
        IsActive = false;
        _readVirtualTime = null;
        // Realtime itself was never hooked. Stop virtual dispatch before
        // reading it, and preserve remaining wait time on cancellation.
        float realNow = Time.realtimeSinceStartup;
        List<Exception>? restoreErrors = null;
        try
        {
            foreach (var entry in Waits.Values)
            {
                try
                {
                    entry.Wait.m_WaitUntilTime = entry.FrameDeadline is long frameDeadline
                        ? realNow + Math.Max(0, frameDeadline - entry.ReadFrame!()) / (float)entry.Fps
                        : entry.Deadline is double deadline
                        ? realNow + (float)Math.Max(0, deadline - virtualNow)
                        : -1f;
                }
                catch (Exception error)
                {
                    (restoreErrors ??= new()).Add(error);
                    Debug.LogWarning("AA Video Export: realtime wait restoration failed (" + error.GetType().Name + ").");
                }
            }
        }
        finally
        {
            Waits.Clear(); NativeScaledWaitHooks.ClearAuthoredWaits();
            _readTimelineFrame = null; _readAdvancing = null;
            _timelineStarted = _hasAdvancedFrame = _snapshotAdvancing = false;
        }
        if (restoreErrors != null)
            throw new AggregateException("One or more native realtime waits could not be restored.", restoreErrors);
    }
}
