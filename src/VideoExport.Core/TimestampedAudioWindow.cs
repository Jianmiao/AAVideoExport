namespace AAVideoExport.Core;

/// <summary>
/// Validates a contiguous raw interleaved capture stream and exposes only the
/// requested content interval as borrowed synchronous slices. The caller owns
/// the source buffer and must consume each slice before reusing that buffer.
/// No sample values are generated, resampled or altered. Raw spans outside the
/// declared content interval remain accounted for by the lead/tail receipts.
/// </summary>
public sealed class TimestampedAudioWindow
{
    private readonly long rawOriginSample;
    private readonly long leadSampleFrames;
    private readonly long contentSampleFrames;
    private readonly int channels;
    private readonly long contentStartSample;
    private readonly long contentEndSample;
    private long nextRawSample;
    private long contentSampleFramesEmitted;
    private bool acknowledgedRawBounds;
    private bool completed;

    public TimestampedAudioWindow(long rawOriginSample, long leadSampleFrames, long contentSampleFrames, int channels)
    {
        RequireNonNegative(rawOriginSample, nameof(rawOriginSample));
        RequireNonNegative(leadSampleFrames, nameof(leadSampleFrames));
        RequireNonNegative(contentSampleFrames, nameof(contentSampleFrames));
        if (channels is not (1 or 2 or 4 or 5 or 6 or 8))
            throw new ArgumentOutOfRangeException(nameof(channels));
        this.rawOriginSample = rawOriginSample;
        this.leadSampleFrames = leadSampleFrames;
        this.contentSampleFrames = contentSampleFrames;
        this.channels = channels;
        contentStartSample = checked(rawOriginSample + leadSampleFrames);
        contentEndSample = checked(contentStartSample + contentSampleFrames);
        nextRawSample = rawOriginSample;
    }

    public long RawOriginSample => rawOriginSample;
    public long LeadSampleFrames => leadSampleFrames;
    public long ContentStartSample => contentStartSample;
    public long ContentEndSampleExclusive => contentEndSample;
    public long ContentSampleFrames => contentSampleFrames;
    public int Channels => channels;
    public long NextRawSample => nextRawSample;
    public long RawSamplesAcknowledged => checked(nextRawSample - rawOriginSample);
    public long RawLeadSampleFramesAcknowledged => Math.Min(RawSamplesAcknowledged, leadSampleFrames);
    public long RawTrailingSampleFrames => Math.Max(0, RawSamplesAcknowledged - leadSampleFrames - contentSampleFrames);
    public long ContentSampleFramesEmitted => contentSampleFramesEmitted;
    public bool HasAcknowledgedRawBounds => acknowledgedRawBounds;
    public bool IsComplete => completed;

    /// <summary>Append an interleaved block whose raw start is expressed in sample frames.</summary>
    public TimestampedAudioAppendReceipt Append(float[] samples, int offset, int interleavedCount, long rawStartSample)
    {
        if (completed) throw new ExportException("invalid_state", "The timestamped audio window is already complete.");
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (offset < 0 || interleavedCount < 0 || offset > samples.Length - interleavedCount
            || offset % channels != 0 || interleavedCount % channels != 0)
            throw new ExportException("invalid_audio", "Audio must contain complete interleaved capture-channel sample frames.");
        RequireNonNegative(rawStartSample, nameof(rawStartSample));
        long rawFrames = interleavedCount / channels;
        long rawEndSample;
        try { rawEndSample = checked(rawStartSample + rawFrames); }
        catch (OverflowException error) { throw new ExportException("audio_raw_overflow", "Raw audio interval exceeds the supported sample clock.", error); }
        if (rawStartSample != nextRawSample)
        {
            string code = rawStartSample < nextRawSample ? "audio_raw_overlap" : "audio_raw_gap";
            throw new ExportException(code, $"Raw audio interval must start at {nextRawSample}, received {rawStartSample}.");
        }
        for (int index = offset; index < offset + interleavedCount; index++)
            if (!float.IsFinite(samples[index]))
                throw new ExportException("invalid_audio", "Audio samples must be finite numbers.");

        // Validate the complete call before mutating the continuity receipt.
        long intersectionStart = Math.Max(rawStartSample, contentStartSample);
        long intersectionEnd = Math.Min(rawEndSample, contentEndSample);
        long contentFrames = Math.Max(0, intersectionEnd - intersectionStart);
        long newContentTotal;
        try { newContentTotal = checked(contentSampleFramesEmitted + contentFrames); }
        catch (OverflowException error) { throw new ExportException("audio_timeline_overflow", "Content sample receipt overflowed.", error); }
        if (newContentTotal > contentSampleFrames)
            throw new ExportException("audio_timeline_extra", "Raw audio overlaps the fixed content interval more than once.");

        int contentLength = checked((int)(contentFrames * channels));
        var slice = contentLength == 0
            ? (TimestampedAudioContentSlice?)null
            : new TimestampedAudioContentSlice(intersectionStart, contentFrames,
                new ArraySegment<float>(samples, checked(offset + (int)((intersectionStart - rawStartSample) * channels)), contentLength));

        nextRawSample = rawEndSample;
        acknowledgedRawBounds |= rawFrames > 0;
        contentSampleFramesEmitted = newContentTotal;
        return new TimestampedAudioAppendReceipt(rawStartSample, rawFrames, RawSamplesAcknowledged,
            RawLeadSampleFramesAcknowledged, RawTrailingSampleFrames, slice);
    }

    public void Complete()
    {
        if (completed) throw new ExportException("invalid_state", "The timestamped audio window has already been completed.");
        if (!acknowledgedRawBounds)
            throw new ExportException("audio_raw_bounds_missing", "At least one non-empty raw interval must be acknowledged before completion.");
        if (nextRawSample < contentEndSample || contentSampleFramesEmitted != contentSampleFrames)
            throw new ExportException("audio_timeline_incomplete",
                $"Raw audio does not cover the fixed content interval: end={nextRawSample}, required={contentEndSample}, emitted={contentSampleFramesEmitted}, expected={contentSampleFrames}.");
        completed = true;
    }

    private static void RequireNonNegative(long value, string parameter)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(parameter);
    }
}

public sealed record TimestampedAudioAppendReceipt(
    long RawStartSample,
    long RawSampleFrames,
    long RawSamplesAcknowledged,
    long RawLeadSampleFramesAcknowledged,
    long RawTrailingSampleFrames,
    TimestampedAudioContentSlice? ContentSlice)
{
    public IReadOnlyList<TimestampedAudioContentSlice> ContentSlices => ContentSlice is null
        ? Array.Empty<TimestampedAudioContentSlice>()
        : new[] { ContentSlice };
}

public sealed record TimestampedAudioContentSlice(
    long ContentStartSample,
    long SampleFrames,
    ArraySegment<float> Samples);

/// <summary>
/// Maps nondecreasing video event frames onto raw samples and a DSP epoch at
/// rawOriginSample. Several events may be scheduled on the same frame.
/// </summary>
public sealed class AudioFrameScheduler
{
    private readonly long rawOriginSample;
    private readonly long leadSampleFrames;
    private readonly int fps;
    private readonly int sampleRate;
    private readonly double dspEpochSeconds;
    private long lastFrame = -1;
    private long lastRelativeStart = -1;
    private double lastDspStart;

    public AudioFrameScheduler(long rawOriginSample, long leadSampleFrames, int fps, int sampleRate, double dspEpochSeconds)
    {
        if (rawOriginSample < 0) throw new ArgumentOutOfRangeException(nameof(rawOriginSample));
        if (leadSampleFrames < 0) throw new ArgumentOutOfRangeException(nameof(leadSampleFrames));
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!double.IsFinite(dspEpochSeconds) || dspEpochSeconds < 0) throw new ArgumentOutOfRangeException(nameof(dspEpochSeconds));
        this.rawOriginSample = rawOriginSample;
        this.leadSampleFrames = leadSampleFrames;
        this.fps = fps;
        this.sampleRate = sampleRate;
        this.dspEpochSeconds = dspEpochSeconds;
        lastDspStart = double.NegativeInfinity;
    }

    public long LastFrame => lastFrame;

    public AudioFrameSchedule Schedule(long videoFrameIndex)
    {
        if (videoFrameIndex < 0 || videoFrameIndex < lastFrame)
            throw new ExportException("audio_frame_order_mismatch", $"Audio schedule cannot move backwards from frame {lastFrame} to {videoFrameIndex}.");
        long relativeStart;
        long relativeEnd;
        try
        {
            relativeStart = checked(leadSampleFrames + ExportTimeline.AudioSampleFramesForVideoFrames(videoFrameIndex, fps, sampleRate));
            relativeEnd = checked(leadSampleFrames + ExportTimeline.AudioSampleFramesForVideoFrames(checked(videoFrameIndex + 1), fps, sampleRate));
        }
        catch (OverflowException error) { throw new ExportException("audio_clock_overflow", "Audio frame schedule exceeds the supported sample clock.", error); }
        long rawStart;
        long rawEnd;
        try
        {
            rawStart = checked(rawOriginSample + relativeStart);
            rawEnd = checked(rawOriginSample + relativeEnd);
        }
        catch (OverflowException error)
        {
            throw new ExportException("audio_clock_overflow", "Audio frame schedule exceeds the supported raw sample clock.", error);
        }
        double dspStart = dspEpochSeconds + relativeStart / (double)sampleRate;
        if (!double.IsFinite(dspStart) || dspStart < lastDspStart)
            throw new ExportException("invalid_audio_clock", "DSP schedule moved backwards or became non-finite.");
        // A finite double is not sufficient: an oversized epoch can collapse
        // distinct sample positions to the same PlayScheduled timestamp.
        double sampleSeconds = 1d / sampleRate;
        double ulp = Math.BitIncrement(dspStart) - dspStart;
        double recoveredSample = (dspStart - dspEpochSeconds) * sampleRate;
        if (relativeStart >= 9007199254740992L || !double.IsFinite(ulp) || ulp >= sampleSeconds
            || Math.Abs(recoveredSample - relativeStart) >= .5d
            || (relativeStart > lastRelativeStart && dspStart <= lastDspStart))
            throw new ExportException("audio_clock_precision", "DSP time cannot represent the requested sample position with less than half a sample of error.");
        lastDspStart = dspStart;
        lastRelativeStart = relativeStart;
        lastFrame = videoFrameIndex;
        return new AudioFrameSchedule(videoFrameIndex, rawStart, rawEnd - rawStart, dspStart);
    }
}

public readonly record struct AudioFrameSchedule(long VideoFrameIndex, long RawSampleStart, long SampleFrames, double DspStartSeconds);
