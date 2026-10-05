using System.Globalization;

namespace AAVideoExport.Core;

public sealed record ExportOptions
{
    public string Title { get; init; } = "Export";
    public string OutputDirectory { get; init; } = "";
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    // Lay out the native scene at the exact output size. Legacy callers can
    // still request fit/fill for an already composed source image.
    public string CanvasMode { get; init; } = "viewport";
    // Zero together preserves existing callers whose frames already match the
    // output canvas. Otherwise these freeze the source composition aspect ratio.
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    // Adapter identity supplied by AA, independent of each API's GPU index.
    public int RenderGpuVendorId { get; init; }
    public int RenderGpuDeviceId { get; init; }
    internal int UpscaleGpuIndex { get; init; } = -1;
    internal bool UseComputeUpscale { get; init; } = true;
    internal bool UseDirect3DUpscale { get; init; } = true;
    internal bool UseDirect3DNv12 { get; init; } = true;
    // Four slots are an explicitly enabled integration preview. One retains
    // the measured stable per-frame readback path pending full AA acceptance.
    public int Direct3DReadbackFrames { get; init; } = 1;
    // Native preserves output pixels. Proxy modes lower only the capture
    // density; the story still lays out for the selected output canvas.
    public string CaptureMode { get; init; } = "native";
    public bool SuperResolutionEnabled { get; init; }
    public string SuperResolutionTier { get; init; } = "balanced";
    public string UpscaleAlgorithm { get; init; } = "anime4k-cnn";
    public double RcasSharpness { get; init; } = 0.87;
    // The older fixed-resolution research callers retain their measured filter.
    internal string EffectiveUpscaleAlgorithm => SuperResolutionEnabled ? UpscaleAlgorithm : "bilinear";
    public int Fps { get; init; } = 30;
    // Offline audio is emitted in mixer blocks. Permit a small final tail,
    // with comparable time limits at each supported output frame rate.
    internal int AudioTimelineToleranceFrames => Fps switch
    {
        24 or 25 => 2,
        30 => 3,
        50 => 4,
        _ => 5
    };
    internal double AudioTimelineToleranceSeconds => (double)AudioTimelineToleranceFrames / Fps;
    public string Container { get; init; } = "mp4";
    public string Codec { get; init; } = "h264";
    // The product explicitly chooses a mode. Automatic selection stays within
    // that mode and never silently substitutes CPU encoding for a failed GPU.
    public string EncodingMode { get; init; } = "hardware";
    public string Encoder { get; init; } = "auto";
    public int BitrateKbps { get; init; } = 4000;
    public string RateControl { get; init; } = "vbr";
    public string AudioQuality { get; init; } = "aac192";
    public int AudioSampleRate { get; init; } = 48000;
    public bool FastStart { get; init; } = true;
    // Retained for earlier callers; export now accepts only direct output-size rendering.
    public int Supersampling { get; init; } = 1;
    public bool WriteCover { get; init; } = true;

    public void Validate()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new ExportException("invalid_settings", message);
        }
        Require(!string.IsNullOrWhiteSpace(Title) && Title.Length <= 120, "Title must contain 1–120 characters.");
        Require(Title == Title.Trim() && !Title.EndsWith('.') && !Title.Contains("..", StringComparison.Ordinal), "Title cannot contain traversal segments or end with a dot or space.");
        Require(!Title.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)), "Title contains a character that cannot be used in a filename.");
        string stem = Title.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        Require(stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
            !System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$"), "Title is a reserved device name.");
        Require(OutputDirectoryPreference.TryNormalize(OutputDirectory, out _), "Choose a valid absolute output folder without control characters.");
        _ = GetCanvasLayout();
        Require(Direct3DReadbackFrames is 1 or 4, "Direct3D readback frames must be 1 (stable) or 4 (experimental). ");
        Require(Fps is 24 or 25 or 30 or 50 or 60, "Frame rate must be 24, 25, 30, 50, or 60.");
        Require(Container is "mp4" or "mov" or "mkv", "Container must be mp4, mov, or mkv.");
        Require(Codec is "h264" or "hevc" or "av1" or "qtrle", "Unsupported video codec.");
        Require(EncodingMode is "hardware" or "software", "Encoding mode must be hardware or software.");
        Require(Codec != "qtrle" || Container == "mov", "Lossless QuickTime Animation requires MOV.");
        Require(Codec != "av1" || Container != "mov", "AV1 is offered in MP4 or MKV; use one of those containers.");
        Require(RateControl is "vbr" or "cbr", "Rate control must be vbr or cbr.");
        Require(Codec == "qtrle" || BitrateKbps is >= 100 and <= 500000, "Bitrate must be 100–500000 kbps.");
        Require(AudioQuality is "none" or "aac128" or "aac192" or "aac320" or "pcm16" or "pcm24", "Unsupported audio quality.");
        Require(AudioSampleRate is 44100 or 48000, "Audio sample rate must be 44100 or 48000 Hz.");
        Require(Supersampling == 1, "Rendering is fixed at 1x; use the selected output dimensions.");
        Require(Encoder == "auto" || EncoderCatalog.All.Any(e => e.Name == Encoder && e.Codec == Codec), "The selected encoder does not support this codec.");
    }

    public CanvasLayout GetCanvasLayout()
    {
        var canvas = SourceWidth == 0 && SourceHeight == 0
            ? CanvasLayout.Create(Width, Height, Width, Height, CanvasMode)
            : CanvasLayout.Create(SourceWidth, SourceHeight, Width, Height, CanvasMode);
        if (SuperResolutionTier is not ("quality" or "balanced" or "performance"))
            throw new ExportException("invalid_settings", "Unsupported super-resolution tier.");
        if (UpscaleAlgorithm is not ("bilinear" or "bicubic" or "lanczos" or "fsr1-luma" or "anime4k-cnn" or "anime4k-rcas"))
            throw new ExportException("invalid_settings", "Unsupported upscaling algorithm.");
        if (!double.IsFinite(RcasSharpness) || RcasSharpness < 0 || RcasSharpness > 1)
            throw new ExportException("invalid_settings", "RCAS sharpness must be between 0 and 1.");
        if (SuperResolutionEnabled)
        {
            if (CaptureMode != "native")
                throw new ExportException("invalid_settings", "Choose super resolution or a legacy capture preset, not both.");
            if (!IsSuperResolutionTierAvailable(SuperResolutionTier))
                throw new ExportException("invalid_settings", "This super-resolution tier is unavailable for the selected output size. Choose a higher tier or native rendering.");
            if (Codec == "qtrle" && UpscaleAlgorithm is "fsr1-luma" or "anime4k-cnn" or "anime4k-rcas")
                throw new ExportException("invalid_settings", "GPU upscaling currently supports H.264, HEVC and AV1 outputs.");
            return canvas.WithCaptureShortEdge(GetSuperResolutionShortEdge(SuperResolutionTier));
        }
        return CaptureMode switch
        {
            "native" => canvas,
            "proxy720" => canvas.WithCaptureShortEdge(720),
            "proxy900" => canvas.WithCaptureShortEdge(900),
            _ => throw new ExportException("invalid_settings", "Capture mode must be native, proxy720 or proxy900.")
        };
    }

    public int GetSuperResolutionShortEdge(string tier)
    {
        var (numerator, denominator) = tier switch
        {
            "quality" => (5, 6), "balanced" => (2, 3), "performance" => (1, 2),
            _ => throw new ExportException("invalid_settings", "Unsupported super-resolution tier.")
        };
        // Quantize to even pixels, matching the capture canvas calculation.
        return (int)(2 * (((long)Math.Min(Width, Height) * numerator + denominator) / (2L * denominator)));
    }

    // Keep the existing >=1080p policy. Smaller 720p/900p deliveries can use
    // their relative quality/balanced captures, never below 480 on the short
    // edge. This is a product quality guard, not a GPU/model minimum size.
    public int MinimumSuperResolutionShortEdge => Math.Min(720, Math.Max(480, GetSuperResolutionShortEdge("balanced")));

    public bool IsSuperResolutionTierAvailable(string tier)
    {
        int shortEdge = GetSuperResolutionShortEdge(tier);
        int outputShortEdge = Math.Min(Width, Height);
        return outputShortEdge >= 720 && shortEdge >= MinimumSuperResolutionShortEdge && shortEdge < outputShortEdge;
    }

    // UI transitions use this policy; direct callers still receive validation
    // errors rather than silently changing the requested capture quality.
    public ExportOptions NormalizeSuperResolutionForOutput()
    {
        if (IsSuperResolutionTierAvailable(SuperResolutionTier)) return this;
        if (IsSuperResolutionTierAvailable("balanced")) return this with { SuperResolutionTier = "balanced" };
        if (IsSuperResolutionTierAvailable("quality")) return this with { SuperResolutionTier = "quality" };
        return this with { SuperResolutionEnabled = false, SuperResolutionTier = "balanced" };
    }

    public double EstimateBytes(double durationSeconds) => Codec == "qtrle"
        ? durationSeconds * Fps * Width * Height * 4
        : durationSeconds * (BitrateKbps * 1000d + (AudioQuality switch
        {
            "aac128" => 128000, "aac192" => 192000, "aac320" => 320000,
            "pcm16" => AudioSampleRate * 2 * 16, "pcm24" => AudioSampleRate * 2 * 24, _ => 0
        })) / 8;
}

public sealed class ExportException : Exception
{
    public string Code { get; }
    public ExportException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;

    internal static ExportException FromIo(Exception error, string operation)
    {
        int native = error.HResult & 0xffff;
        return new ExportException(native is 39 or 112 ? "disk_full" : "io_error", operation + ": " + error.Message, error);
    }
}

public sealed record ExportResult(string OutputPath, long Frames, double DurationSeconds, string Encoder, long FileBytes, string? CoverPath);

public sealed record EncoderCapability(string Name, string Codec, bool Hardware, bool Available, string Detail);

internal static class EncoderCatalog
{
    internal static readonly EncoderCapability[] All =
    {
        new("h264_qsv", "h264", true, false, ""), new("hevc_qsv", "hevc", true, false, ""), new("av1_qsv", "av1", true, false, ""),
        new("h264_nvenc", "h264", true, false, ""), new("hevc_nvenc", "hevc", true, false, ""), new("av1_nvenc", "av1", true, false, ""),
        new("h264_amf", "h264", true, false, ""), new("hevc_amf", "hevc", true, false, ""), new("av1_amf", "av1", true, false, ""),
        new("libx264", "h264", false, false, ""), new("libx265", "hevc", false, false, ""),
        new("libsvtav1", "av1", false, false, ""), new("libaom-av1", "av1", false, false, ""), new("qtrle", "qtrle", false, false, "")
    };

    internal static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
