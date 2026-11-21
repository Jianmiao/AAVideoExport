namespace AAVideoExport.Core;

/// <summary>
/// Converts native DSP interval observations into the raw sample clock used by
/// <see cref="RawAudioPass"/>. Every block must carry finite timestamps whose
/// start and end map to integer sample positions with less than a quarter-sample
/// error. Validation completes before the continuity receipt is advanced.
/// </summary>
public sealed class NativeAudioSampleClock
{
    private readonly double epoch;
    private readonly double sampleRate;

    public NativeAudioSampleClock(double rawDspEpochSeconds, int sampleRate)
    {
        if (!double.IsFinite(rawDspEpochSeconds) || rawDspEpochSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(rawDspEpochSeconds));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        epoch = rawDspEpochSeconds;
        this.sampleRate = sampleRate;
        // If the epoch itself cannot distinguish quarter-sample DSP increments,
        // no later timestamp calculation can provide a strict raw position.
        double ulp = Math.BitIncrement(epoch) - epoch;
        if (!double.IsFinite(ulp) || ulp * sampleRate >= .25d)
            throw new ExportException("audio_clock_precision", "DSP epoch cannot represent quarter-sample positions at the selected sample rate.");
    }

    public long NextRawSample { get; private set; }

    public long ObserveBlock(double dspBefore, double dspAfter, long sampleFrames)
    {
        if (sampleFrames < 0) throw new ExportException("invalid_audio_clock", "Native audio sample count cannot be negative.");
        if (!double.IsFinite(dspBefore) || !double.IsFinite(dspAfter) || dspBefore < epoch || dspAfter < dspBefore)
            throw new ExportException("invalid_audio_clock", "Native DSP timestamps must be finite and monotonic from the captured epoch.");

        double rawBefore = (dspBefore - epoch) * sampleRate;
        double rawAfter = (dspAfter - epoch) * sampleRate;
        if (!double.IsFinite(rawBefore) || !double.IsFinite(rawAfter))
            throw new ExportException("audio_clock_precision", "Native DSP timestamps exceed the representable raw sample clock.");
        long start = RoundSample(rawBefore);
        long end = RoundSample(rawAfter);
        long expectedEnd;
        try { expectedEnd = checked(start + sampleFrames); }
        catch (OverflowException error) { throw new ExportException("audio_clock_precision", "Native audio interval exceeds the supported sample clock.", error); }
        if (Math.Abs(rawBefore - start) >= .25d || Math.Abs(rawAfter - end) >= .25d)
            throw new ExportException("audio_clock_precision", "Native DSP timestamps do not identify the returned sample interval within a quarter sample.");
        if (end != expectedEnd)
            throw new ExportException("audio_raw_interval", $"Native DSP interval covers {end - start} samples; renderer returned {sampleFrames}.");
        if (start != NextRawSample)
        {
            string code = start < NextRawSample ? "audio_raw_overlap" : "audio_raw_gap";
            throw new ExportException(code, $"Native DSP interval must start at sample {NextRawSample}; received {start}.");
        }
        NextRawSample = expectedEnd;
        return start;
    }

    private static long RoundSample(double value)
    {
        if (value < long.MinValue || value > long.MaxValue)
            throw new ExportException("audio_clock_precision", "Native DSP timestamp exceeds the supported raw sample range.");
        double rounded = Math.Round(value, MidpointRounding.ToEven);
        if (rounded < long.MinValue || rounded > long.MaxValue)
            throw new ExportException("audio_clock_precision", "Native DSP timestamp rounds outside the supported raw sample range.");
        return (long)rounded;
    }
}
