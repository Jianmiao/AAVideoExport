namespace AAVideoExport.Core;

/// <summary>
/// Converts the integer video frame clock into an integer audio sample clock.
/// The boundary is always rounded down, so the sum of per-frame budgets is the
/// same as the budget for the complete video timeline.
/// </summary>
public static class ExportTimeline
{
    public static long AudioSampleFramesForVideoFrames(long videoFrameCount, int fps, int sampleRate)
    {
        Require(videoFrameCount >= 0, nameof(videoFrameCount));
        Require(fps > 0, nameof(fps));
        Require(sampleRate > 0, nameof(sampleRate));
        return checked(videoFrameCount * (long)sampleRate / fps);
    }

    public static long AudioSampleFramesForVideoFrame(long videoFrameIndex, int fps, int sampleRate)
    {
        Require(videoFrameIndex >= 0, nameof(videoFrameIndex));
        long start = AudioSampleFramesForVideoFrames(videoFrameIndex, fps, sampleRate);
        long end = AudioSampleFramesForVideoFrames(checked(videoFrameIndex + 1), fps, sampleRate);
        return end - start;
    }

    private static void Require(bool valid, string parameter)
    {
        if (!valid) throw new ArgumentOutOfRangeException(parameter);
    }
}
