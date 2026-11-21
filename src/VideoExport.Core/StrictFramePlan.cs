namespace AAVideoExport.Core;

/// <summary>
/// An immutable timeline sealed by the preparation pass. The video pass must
/// replay this plan, not derive a new duration from its own playback ending.
/// Production execution plans carry only their actual frame/sample contract.
/// The separate identity-bearing constructor supports development auditing;
/// its caller supplies the source/content/settings identity.
/// </summary>
public sealed class StrictFramePlan
{
    public string SourceIdentity { get; }
    public bool HasSourceIdentity { get; }
    public long FrameCount { get; }
    public int Fps { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public long AudioSampleFrameCount { get; }
    public double DurationSeconds => FrameCount / (double)Fps;

    public StrictFramePlan(string sourceIdentity, long frameCount, int fps, int sampleRate, int channels)
        : this(sourceIdentity, frameCount, fps, sampleRate, channels, true) { }

    public static StrictFramePlan CreateExecution(long frameCount, int fps, int sampleRate, int channels)
        => new("", frameCount, fps, sampleRate, channels, false);

    private StrictFramePlan(string sourceIdentity, long frameCount, int fps, int sampleRate, int channels, bool auditedIdentity)
    {
        if (auditedIdentity && string.IsNullOrWhiteSpace(sourceIdentity)) throw new ArgumentException("A frozen source identity is required.", nameof(sourceIdentity));
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is not (1 or 2 or 4 or 5 or 6 or 8)) throw new ArgumentOutOfRangeException(nameof(channels));
        SourceIdentity = sourceIdentity;
        HasSourceIdentity = auditedIdentity;
        FrameCount = frameCount;
        Fps = fps;
        SampleRate = sampleRate;
        Channels = channels;
        AudioSampleFrameCount = ExportTimeline.AudioSampleFramesForVideoFrames(frameCount, fps, sampleRate);
    }

    public void ValidateCaptureIdentity(string sourceIdentity, int fps, int sampleRate, int channels)
    {
        if ((HasSourceIdentity && !string.Equals(SourceIdentity, sourceIdentity, StringComparison.Ordinal)) ||
            Fps != fps || SampleRate != sampleRate || Channels != channels)
            throw new ExportException("timeline_source_changed", "The source or capture clock changed after the preparation timeline was sealed.");
    }

    public void ValidateNextVideoFrame(long zeroBasedFrameIndex)
    {
        if (zeroBasedFrameIndex < 0 || zeroBasedFrameIndex >= FrameCount)
            throw new ExportException("video_timeline_mismatch",
                $"Video frame {zeroBasedFrameIndex} lies outside the sealed timeline of {FrameCount} frames.");
    }

    public void ValidateVideoEnd(long observedEndFrameCount) => ValidateVideoCount(observedEndFrameCount, "Playback end");
    public void ValidateVideoComplete(long capturedFrames) => ValidateVideoCount(capturedFrames, "Captured video");

    public void ValidateAudioComplete(long sampleFrames)
    {
        if (sampleFrames != AudioSampleFrameCount)
            throw new ExportException("audio_timeline_mismatch",
                $"Audio sample count differs from the sealed timeline: expected={AudioSampleFrameCount}, actual={sampleFrames}, fps={Fps}, sample_rate={SampleRate}.");
    }

    private void ValidateVideoCount(long actual, string stage)
    {
        if (actual != FrameCount)
            throw new ExportException("video_timeline_mismatch",
                $"{stage} differs from the sealed preparation timeline: expected_frames={FrameCount}, actual_frames={actual}, fps={Fps}.");
    }
}
