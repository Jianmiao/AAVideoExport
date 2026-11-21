namespace AAVideoExport.Core;

/// <summary>
/// Renders the source composition at the output pixel density, then centers it
/// on the output canvas. This never requests an additional supersampling pass.
/// Source dimensions describe the composition's aspect ratio, not a raw buffer.
/// </summary>
public sealed record CanvasLayout
{
    public int CaptureWidth { get; }
    public int CaptureHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public string Mode { get; }
    // The image origin for fit, or the crop origin in the source image for fill.
    public int OffsetX { get; }
    public int OffsetY { get; }
    public bool IsIdentity => CaptureWidth == OutputWidth && CaptureHeight == OutputHeight;
    public bool IsProxy => Mode == "viewport" && !IsIdentity;

    /// <summary>RGBA geometry filter; null means no crop, padding or scaling is needed.</summary>
    // Proxy viewport geometry is handled by the encoder's single scale/color
    // pass; it must never be treated as a fill crop larger than its input.
    public string? VideoFilter => IsIdentity || Mode == "viewport" ? null : Mode == "fit"
        ? FormattableString.Invariant($"pad={OutputWidth}:{OutputHeight}:{OffsetX}:{OffsetY}:color=black,setsar=1")
        : FormattableString.Invariant($"crop={OutputWidth}:{OutputHeight}:{OffsetX}:{OffsetY},setsar=1");

    private CanvasLayout(int captureWidth, int captureHeight, int outputWidth, int outputHeight, string mode)
    {
        CaptureWidth = captureWidth;
        CaptureHeight = captureHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        Mode = mode;
        OffsetX = mode == "viewport" ? 0 : Math.Abs(captureWidth - outputWidth) / 2;
        OffsetY = mode == "viewport" ? 0 : Math.Abs(captureHeight - outputHeight) / 2;
    }

    internal CanvasLayout WithCaptureShortEdge(int shortEdge)
    {
        if (Mode != "viewport")
            throw new ExportException("invalid_settings", "Proxy capture requires the native viewport canvas.");
        int outputShortEdge = Math.Min(OutputWidth, OutputHeight);
        // Never enlarge a low-resolution output just to satisfy a proxy preset.
        if (outputShortEdge <= shortEdge) return this;
        int width = (int)RoundToEven((long)OutputWidth * shortEdge, outputShortEdge);
        int height = (int)RoundToEven((long)OutputHeight * shortEdge, outputShortEdge);
        return new CanvasLayout(width, height, OutputWidth, OutputHeight, Mode);
    }

    public static CanvasLayout Create(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, string mode = "fit")
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ExportException("invalid_settings", "Source composition dimensions must both be positive.");
        if (outputWidth < 16 || outputHeight < 16 || outputWidth > 7680 || outputHeight > 7680 ||
            Math.Min(outputWidth, outputHeight) > 4320 || outputWidth % 2 != 0 || outputHeight % 2 != 0)
            throw new ExportException("invalid_settings", "Dimensions must be even, at least 16 pixels, and within 7680 × 4320 (or portrait equivalent).");
        // A viewport is a new composition at the chosen dimensions. Unity/NGUI
        // lay out the scene for this target before drawing; no image fit/crop.
        if (mode == "viewport")
            return new CanvasLayout(outputWidth, outputHeight, outputWidth, outputHeight, mode);
        if (mode is not ("fit" or "fill"))
            throw new ExportException("invalid_settings", "Canvas mode must be viewport, fit, or fill.");

        bool sourceIsWider = (long)sourceWidth * outputHeight >= (long)outputWidth * sourceHeight;
        bool useOutputWidth = mode == "fit" ? sourceIsWider : !sourceIsWider;
        long captureWidth = useOutputWidth ? outputWidth : RoundToEven((long)outputHeight * sourceWidth, sourceHeight);
        long captureHeight = useOutputWidth ? RoundToEven((long)outputWidth * sourceHeight, sourceWidth) : outputHeight;
        // Crop-to-fill can need a much wider render target than the final file.
        // Bound it before allocation; do not silently reduce quality or stretch.
        if (captureWidth > 7680 || captureHeight > 7680 || captureWidth * captureHeight > 7680L * 4320)
            throw new ExportException("invalid_settings", "This fill composition needs a render target larger than the supported canvas. Choose fit or a lower output resolution.");
        return new CanvasLayout((int)captureWidth, (int)captureHeight, outputWidth, outputHeight, mode);
    }

    // One axis is exact. Quantize the other to its nearest even pixel (ties up),
    // keeping the aspect error within one pixel without any extra scale filter.
    private static long RoundToEven(long numerator, int denominator) =>
        Math.Max(2, 2 * ((numerator + denominator) / (2L * denominator)));
}
