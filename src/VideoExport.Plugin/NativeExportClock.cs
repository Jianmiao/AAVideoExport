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
        public WaitEntry(WaitForSecondsRealtime wait) => Wait = wait;
    }

    // Retain the native wrappers until restoration so an address cannot be
    // collected/reused while its pending deadline is owned by this session.
    private static readonly Dictionary<IntPtr, WaitEntry> Waits = new();
    private static Func<float>? _readVirtualTime;
    private static int _snapshotFrame;
    private static float _now;
    internal static bool IsActive { get; private set; }
    internal static float Delta { get; private set; }
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
            int frame = Time.frameCount;
            if (IsActive && frame != _snapshotFrame)
            {
                _snapshotFrame = frame;
                _now = _readVirtualTime!();
            }
            return _now;
        }
    }

    internal static void Begin(float initialVirtualTime, float frameDelta, Func<float> readVirtualTime)
    {
        if (IsActive) throw new InvalidOperationException("An export clock is already active.");
        if (!float.IsFinite(initialVirtualTime) || !float.IsFinite(frameDelta) || frameDelta <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameDelta));
        ArgumentNullException.ThrowIfNull(readVirtualTime);
        Waits.Clear();
        _now = initialVirtualTime;
        Delta = frameDelta;
        _readVirtualTime = readVirtualTime;
        _snapshotFrame = Time.frameCount;
        RegisteredWaitCount = WaitPollCount = UnscaledTimeReadCount = UnscaledDeltaReadCount = 0;
        PeakActiveWaitCount = 0;
        IsActive = true;
    }

    internal static float ReadTime() { UnscaledTimeReadCount++; return Now; }
    internal static float ReadDelta() { UnscaledDeltaReadCount++; return Delta; }

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
                entry.Deadline = now + Math.Max(0, (double)nativeDeadline - Time.realtimeSinceStartup);
            Waits.Add(wait.Pointer, entry);
            RegisteredWaitCount++;
            if (Waits.Count > PeakActiveWaitCount) PeakActiveWaitCount = Waits.Count;
        }
        WaitPollCount++;
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
                    entry.Wait.m_WaitUntilTime = entry.Deadline is double deadline
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
        finally { Waits.Clear(); }
        if (restoreErrors != null)
            throw new AggregateException("One or more native realtime waits could not be restored.", restoreErrors);
    }
}
