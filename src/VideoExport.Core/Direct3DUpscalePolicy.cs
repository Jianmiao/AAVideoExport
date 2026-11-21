namespace AAVideoExport.Core;
internal static class Direct3DUpscalePolicy
{
    internal static bool IsEligible(ExportOptions options, string encoder)
    {
        if (!OperatingSystem.IsWindows() || !options.UseDirect3DUpscale || !UpscaleShaders.UsesGpu(options) || options.UpscaleAlgorithm is not ("anime4k-cnn" or "anime4k-rcas"))
            return false;
        var canvas = options.GetCanvasLayout();
        // Bound the new backend to canvases verified by the release tests.
        // Larger canvases and other models retain the original Vulkan path.
        return canvas.IsProxy && (long)canvas.OutputWidth * canvas.OutputHeight <= 1920L * 1080L && canvas.OutputWidth <= canvas.CaptureWidth * 2 && canvas.OutputHeight <= canvas.CaptureHeight * 2 && (encoder.EndsWith("_nvenc", StringComparison.Ordinal) || encoder.EndsWith("_amf", StringComparison.Ordinal) || encoder.EndsWith("_qsv", StringComparison.Ordinal));
    }

    internal static ExportOptions EncodingOptions(ExportOptions options) => options with
    {
        SuperResolutionEnabled = false,
        CaptureMode = "native",
        SourceWidth = 0,
        SourceHeight = 0,
        CanvasMode = "viewport"
    };
}
