namespace AAVideoExport.Core;

/// <summary>
/// Main-thread boundary between encoder preparation and launching one selected
/// story. The host owns sessions, native players and cancellation cleanup.
/// </summary>
public sealed class PlaybackLaunchGate
{
    private enum Phase { Idle, Preparing, Waiting }
    private Phase _phase;
    private long _ticket;

    public bool IsWaiting => _phase == Phase.Waiting;
    public string StoryKey { get; private set; } = "";

    public long Begin(string storyKey)
    {
        if (_phase != Phase.Idle)
            throw new ExportException("export_already_started", "已有剧情导出正在准备或加载。");
        if (string.IsNullOrWhiteSpace(storyKey))
            throw new ExportException("story_not_selected", "请先在鉴赏列表选择要导出的剧情。");
        StoryKey = storyKey;
        _phase = Phase.Preparing;
        return ++_ticket;
    }

    /// <returns>False when a late or duplicate preparation result must be discarded.</returns>
    public bool CompletePreparation(long ticket, Action<string> load)
    {
        if (ticket != _ticket || _phase != Phase.Preparing) return false;
        ArgumentNullException.ThrowIfNull(load);
        // Set the phase first: a native loader may synchronously start playback.
        _phase = Phase.Waiting;
        try { load(StoryKey); }
        catch
        {
            // A loader callback must not invalidate a newer, reentrant launch.
            if (ticket == _ticket) Cancel();
            throw;
        }
        return true;
    }

    public bool AcceptPlayback(long ticket, string storyKey)
    {
        if (ticket != _ticket || _phase != Phase.Waiting ||
            !string.Equals(StoryKey, storyKey, StringComparison.Ordinal)) return false;
        _phase = Phase.Idle;
        return true;
    }

    public void Cancel()
    {
        ++_ticket;
        _phase = Phase.Idle;
        StoryKey = "";
    }

    /// <summary>Pass real elapsed seconds since invoking the native loader.</summary>
    public void CheckTimeout(double elapsedSeconds)
    {
        if (!IsWaiting || elapsedSeconds < 120) return;
        Cancel();
        throw new ExportException("playback_start_timeout", "剧情加载超时，未开始导出。请检查剧情和素材后重试。");
    }
}
