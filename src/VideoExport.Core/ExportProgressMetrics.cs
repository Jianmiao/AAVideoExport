namespace AAVideoExport.Core;

/// <summary>
/// Values shown by the native export progress page.  Capture and encoder
/// clocks are deliberately separate: strict two-pass preparation renders the
/// scene to establish the audio timeline, but it does not submit video frames
/// to the encoder yet.
/// </summary>
public readonly record struct ExportProgressMetrics(
    long CapturedFrames,
    long EncodedFrames,
    double CaptureElapsedSeconds,
    double EncodingElapsedSeconds,
    int BufferedFrames,
    bool StrictAudioPreparation)
{
    public double RenderFramesPerSecond => FramesPerSecond(CapturedFrames, CaptureElapsedSeconds);

    public double EncodeFramesPerSecond => StrictAudioPreparation
        ? 0
        : FramesPerSecond(EncodedFrames, EncodingElapsedSeconds);

    public long PendingFrames => Math.Max(0, CapturedFrames - EncodedFrames);

    public static double FramesPerSecondForDisplay(long frames, double seconds)
    {
        if (frames <= 0 || !double.IsFinite(seconds) || seconds <= 0) return 0;
        return frames / seconds;
    }

    private static double FramesPerSecond(long frames, double seconds) => FramesPerSecondForDisplay(frames, seconds);
}
