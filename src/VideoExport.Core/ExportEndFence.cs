namespace AAVideoExport.Core;

/// <summary>
/// Resolves a native End request to an exclusive captured-frame boundary.
/// An End request may run before or after the render callback in a Unity frame;
/// both positions must select the same final frame. Repeated requests cannot
/// extend the boundary. This is a per-pass fence, not a story duration plan.
/// </summary>
public sealed class ExportEndFence
{
    public long FrameCount { get; private set; } = -1;

    public long ObserveEnd(long capturedFrames, int unityFrame, int lastCapturedUnityFrame)
    {
        if (capturedFrames < 0) throw new ArgumentOutOfRangeException(nameof(capturedFrames));
        if (FrameCount >= 0) return FrameCount;
        // The first frame still has to be captured even if a caller supplied a
        // default/current frame number before any actual frame was written.
        FrameCount = capturedFrames > 0 && unityFrame == lastCapturedUnityFrame
            ? capturedFrames : checked(capturedFrames + 1);
        return FrameCount;
    }

    public bool IsComplete(long capturedFrames)
    {
        if (capturedFrames < 0) throw new ArgumentOutOfRangeException(nameof(capturedFrames));
        if (FrameCount < 0) return false;
        if (capturedFrames > FrameCount)
            throw new ExportException("video_timeline_mismatch",
                $"Capture advanced beyond the native end fence: expected_frames={FrameCount}, actual_frames={capturedFrames}.");
        return capturedFrames == FrameCount;
    }
}
