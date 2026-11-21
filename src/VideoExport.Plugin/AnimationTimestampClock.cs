using System.Diagnostics;

namespace AAVideoExport.Plugin;

// Stopwatch-compatible timestamps, scoped to adapted animation methods only.
// Keeping an offset until active animations are rebased preserves cancellation.
internal sealed class AnimationTimestampClock
{
    private readonly Func<long> _wallTime;
    private readonly long _frequency;
    private Func<double>? _videoTime;
    private double _videoStart;
    private long _origin;
    private long _offset;

    internal AnimationTimestampClock(Func<long>? wallTime = null, long frequency = 0)
    {
        _wallTime = wallTime ?? Stopwatch.GetTimestamp;
        _frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        if (_frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
    }

    internal bool Active => _videoTime != null;
    internal long Offset => _offset;

    internal long Read()
    {
        if (_videoTime == null) return checked(_wallTime() + _offset);
        double elapsed = _videoTime() - _videoStart;
        if (!double.IsFinite(elapsed) || elapsed < 0)
            throw new InvalidOperationException("Animation video clock moved backwards or is invalid.");
        return checked(_origin + (long)Math.Round(elapsed * _frequency));
    }

    internal void Begin(Func<double> videoTime)
    {
        if (Active) throw new InvalidOperationException("An animation export clock is already active.");
        ArgumentNullException.ThrowIfNull(videoTime);
        double start = videoTime();
        if (!double.IsFinite(start)) throw new ArgumentOutOfRangeException(nameof(videoTime));
        _origin = Read();
        _videoStart = start;
        _videoTime = videoTime;
    }

    internal void End()
    {
        if (!Active) return;
        long final = Read();
        _offset = checked(final - _wallTime());
        _videoTime = null;
    }

    internal void ClearRebasedOffset()
    {
        if (Active) throw new InvalidOperationException("Cannot rebase an active animation export clock.");
        _offset = 0;
    }
}
