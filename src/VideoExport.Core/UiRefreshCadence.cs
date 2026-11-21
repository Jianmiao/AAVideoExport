namespace AAVideoExport.Core;

/// <summary>Schedules UI presentation using wall time, independently of video time.</summary>
public sealed class UiRefreshCadence
{
    private const double IntervalSeconds = .1;
    private double _lastRefresh = double.NegativeInfinity;
    private bool _invalidated = true;

    public void Invalidate() => _invalidated = true;

    public bool ShouldRefresh(double wallSeconds)
    {
        if (!double.IsFinite(wallSeconds)) throw new ArgumentOutOfRangeException(nameof(wallSeconds));
        if (!_invalidated && wallSeconds >= _lastRefresh && wallSeconds - _lastRefresh + 1e-9 < IntervalSeconds)
            return false;
        _invalidated = false;
        _lastRefresh = wallSeconds;
        return true;
    }
}
