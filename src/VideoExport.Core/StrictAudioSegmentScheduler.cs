namespace AAVideoExport.Core;

public sealed record StrictScheduledAudioSegment(
    StrictAudioSegment Segment,
    double DspStartSeconds,
    double DspEndSeconds);

/// <summary>Maps validated sample segments to exact DSP timestamps.</summary>
public sealed class StrictAudioSegmentScheduler
{
    private readonly double epoch;
    private readonly double sampleRate;

    public StrictAudioSegmentScheduler(double dspEpochSeconds, int sampleRate)
    {
        if (!double.IsFinite(dspEpochSeconds) || dspEpochSeconds < 0) throw new ArgumentOutOfRangeException(nameof(dspEpochSeconds));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        epoch = dspEpochSeconds; this.sampleRate = sampleRate;
        double ulp = Math.BitIncrement(epoch) - epoch;
        if (!double.IsFinite(ulp) || ulp * sampleRate >= .25d)
            throw new ExportException("audio_clock_precision", "DSP epoch cannot represent strict audio sample positions.");
    }

    public IReadOnlyList<StrictScheduledAudioSegment> Schedule(IEnumerable<StrictAudioSegment> segments)
    {
        var ordered = segments?.OrderBy(segment => segment.StartSample).ThenBy(segment => segment.EndSample).ToArray()
            ?? throw new ArgumentNullException(nameof(segments));
        var activeByTarget = new Dictionary<string, StrictAudioSegment>(StringComparer.Ordinal);
        var result = new List<StrictScheduledAudioSegment>(ordered.Length);
        foreach (var segment in ordered)
        {
            if (segment.StartSample < 0 || segment.EndSample <= segment.StartSample)
                throw new ExportException("audio_segment_invalid", "Audio segments must have a positive sample interval.");
            if (!segment.OneShot && activeByTarget.TryGetValue(segment.TargetId, out var active) && segment.StartSample < active.EndSample)
                throw new ExportException("audio_segment_overlap", $"Audio source {segment.TargetId} has overlapping non-OneShot segments.");
            if (!segment.OneShot) activeByTarget[segment.TargetId] = segment;
            double start = Dsp(segment.StartSample), end = Dsp(segment.EndSample);
            if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start)
                throw new ExportException("audio_clock_precision", "Audio segment DSP timestamps are invalid.");
            double startError = Math.Abs((start - epoch) * sampleRate - segment.StartSample);
            double endError = Math.Abs((end - epoch) * sampleRate - segment.EndSample);
            if (startError >= .25d || endError >= .25d)
                throw new ExportException("audio_clock_precision", "Audio segment DSP timestamp exceeds quarter-sample precision.");
            result.Add(new StrictScheduledAudioSegment(segment, start, end));
        }
        return result;
    }

    private double Dsp(long sample) => epoch + sample / sampleRate;
}
