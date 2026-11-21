namespace AAVideoExport.Core;

/// <summary>
/// The native positions associated with prepared audio. Consecutive identical
/// positions share one in-memory segment. This is execution data only: no
/// content hash, transcript file, provenance, baseline or audit receipt.
/// </summary>
public sealed class PreparedFrameSchedule
{
    private const int MaximumSegments = 1_000_000;
    private readonly List<Segment> segments = new();
    private long recordedFrames, replayFrame;
    private int replaySegment;
    private bool sealedSchedule;
    public long FrameCount { get; private set; }
    public int SegmentCount => segments.Count;

    public void Record(long frame, int row, bool selection, int branch)
    {
        if (sealedSchedule || frame != recordedFrames || frame < 0)
            throw Mismatch("Native preparation frames must be recorded once in increasing order.");
        var position = Position(row, selection, branch);
        if (segments.Count == 0 || segments[^1].Position != position)
        {
            if (segments.Count >= MaximumSegments) throw Mismatch("The native execution schedule exceeds its bounded segment capacity.");
            segments.Add(new(frame, position));
        }
        recordedFrames = checked(recordedFrames + 1);
    }

    public void Seal(long frameCount)
    {
        if (sealedSchedule || frameCount <= 0 || frameCount != recordedFrames || segments.Count == 0)
            throw Mismatch("The prepared execution schedule does not cover its actual frame count.");
        FrameCount = frameCount;
        sealedSchedule = true;
    }

    public void Consume(long frame, int row, bool selection, int branch)
    {
        if (!sealedSchedule || frame != replayFrame || frame < 0 || frame >= FrameCount)
            throw Mismatch("Native replay frames must consume the prepared execution schedule in order.");
        if (replaySegment + 1 < segments.Count && segments[replaySegment + 1].Frame == frame) replaySegment++;
        if (segments[replaySegment].Position != Position(row, selection, branch))
            throw Mismatch($"Native playback at frame {frame} differs from the story/branch position associated with prepared audio.");
        replayFrame++;
    }

    public void Complete(long frameCount)
    {
        if (!sealedSchedule || frameCount != FrameCount || replayFrame != FrameCount)
            throw Mismatch("Native replay did not consume the complete prepared audio execution schedule.");
    }

    private static PlaybackPosition Position(int row, bool selection, int branch)
    {
        if (row < 0 || selection && branch < -1) throw Mismatch("The native story position is unavailable.");
        return new(row, selection, selection ? branch : -1);
    }
    private static ExportException Mismatch(string message) => new("playback_schedule_mismatch", message);
    private readonly record struct PlaybackPosition(int Row, bool Selection, int Branch);
    private readonly record struct Segment(long Frame, PlaybackPosition Position);
}
