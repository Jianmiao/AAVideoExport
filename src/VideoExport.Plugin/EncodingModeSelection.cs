using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

// Pure option transition shared by the native panel and its regression tests.
internal static class EncodingModeSelection
{
    internal static ExportOptions Change(ExportOptions current, string mode)
    {
        if (mode is not ("hardware" or "software"))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (current.EncodingMode == mode) return current;

        bool gpuFilter = current.UpscaleAlgorithm is "anime4k-cnn" or "anime4k-rcas" or "fsr1-luma";
        return current with
        {
            EncodingMode = mode,
            Codec = "h264",
            Encoder = mode == "software" ? "libx264" : "auto",
            SuperResolutionEnabled = current.SuperResolutionEnabled && !(mode == "software" && gpuFilter),
            UpscaleAlgorithm = mode == "software" && gpuFilter ? "bilinear" : current.UpscaleAlgorithm
        };
    }
}
