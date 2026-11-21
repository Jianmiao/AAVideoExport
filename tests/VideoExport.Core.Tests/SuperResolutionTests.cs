using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestSuperResolution()
    {
        await Test("720p output enables quality and balanced reconstruction without changing delivery settings", () =>
        {
            foreach (var row in new[] { ("quality", 1066, 600), ("balanced", 854, 480) })
            foreach (bool portrait in new[] { false, true })
            foreach (string model in new[] { "anime4k-cnn", "anime4k-rcas", "fsr1-luma", "bilinear", "bicubic", "lanczos" })
            {
                var options = Options("sr720") with
                {
                    Width = portrait ? 720 : 1280, Height = portrait ? 1280 : 720,
                    SuperResolutionEnabled = true, SuperResolutionTier = row.Item1, UpscaleAlgorithm = model,
                    BitrateKbps = 6000, Fps = 60, AudioQuality = "aac192"
                };
                Check(options.IsSuperResolutionTierAvailable(row.Item1), "720p must offer " + row.Item1);
                Equal(options, options.NormalizeSuperResolutionForOutput(), "output switch must retain enabled model and tier");
                options.Validate();
                var canvas = options.GetCanvasLayout();
                Equal(portrait ? row.Item3 : row.Item2, canvas.CaptureWidth, "internal width");
                Equal(portrait ? row.Item2 : row.Item3, canvas.CaptureHeight, "internal height");
                Equal(options.Width, canvas.OutputWidth, "720p output width remains exact");
                Equal(options.Height, canvas.OutputHeight, "720p output height remains exact");
                Check(canvas.IsProxy, "enabling must really invoke upscale instead of a disabled no-op");
                var encoded = Direct3DUpscalePolicy.EncodingOptions(options);
                Equal(options.BitrateKbps, encoded.BitrateKbps, "bitrate preserved");
                Equal(options.Fps, encoded.Fps, "fps preserved");
                Equal(options.AudioQuality, encoded.AudioQuality, "audio preserved");
            }
            return Task.CompletedTask;
        });
        await Test("super resolution opens in balanced mode; disabled preserves native pixels", () =>
        {
            var options = Options("sr-default") with { Width = 1920, Height = 1080 };
            Equal("balanced", options.SuperResolutionTier, "default tier");
            Equal("anime4k-cnn", options.UpscaleAlgorithm, "default model requested by user");
            Check(!options.SuperResolutionEnabled && options.GetCanvasLayout().IsIdentity, "native when disabled");
            var canvas = (options with { SuperResolutionEnabled = true }).GetCanvasLayout();
            Equal(1280, canvas.CaptureWidth, "default enabled capture width");
            Equal(720, canvas.CaptureHeight, "default enabled capture height");
            return Task.CompletedTask;
        });
        await Test("relative tiers follow output dimensions in landscape, portrait and square", () =>
        {
            foreach (var row in new[] { (1080, "quality", 900), (1080, "balanced", 720),
                (1440, "quality", 1200), (1440, "balanced", 960), (1440, "performance", 720),
                (2160, "quality", 1800), (2160, "balanced", 1440), (2160, "performance", 1080) })
            foreach (int orientation in new[] { 0, 1, 2 })
            {
                int longEdge = row.Item1 * 16 / 9;
                var options = Options("sr-shape") with { Width = orientation == 1 ? row.Item1 : longEdge,
                    Height = orientation == 0 ? row.Item1 : orientation == 1 ? longEdge : longEdge,
                    SuperResolutionEnabled = true, SuperResolutionTier = row.Item2 };
                if (orientation == 2) options = options with { Width = row.Item1, Height = row.Item1 };
                options.Validate(); var canvas = options.GetCanvasLayout();
                Equal(row.Item3, Math.Min(canvas.CaptureWidth, canvas.CaptureHeight), "relative short edge");
                Check(canvas.CaptureWidth % 2 == 0 && canvas.CaptureHeight % 2 == 0, "encoder-safe even dimensions");
                Check(Math.Abs((double)canvas.CaptureWidth / canvas.CaptureHeight - (double)options.Width / options.Height) < 0.003,
                    "orientation and composition aspect preserved");
                Equal(options.Width, canvas.OutputWidth, "output width unchanged");
                Equal(options.Height, canvas.OutputHeight, "output height unchanged");
            }
            return Task.CompletedTask;
        });
        await Test("720p and 1080p low-detail performance tiers and sub-720 output are rejected", () =>
        {
            foreach (var row in new[] { (1080, "performance"), (900, "performance"), (720, "performance"), (718, "quality"), (480, "balanced") })
            {
                var options = Options("sr-too-small") with { Width = row.Item1, Height = row.Item1,
                    SuperResolutionEnabled = true, SuperResolutionTier = row.Item2 };
                Check(!options.IsSuperResolutionTierAvailable(row.Item2), "disabled option policy");
                ThrowsCode("invalid_settings", options.Validate, "cannot bypass UI minimum");
            }
            return Task.CompletedTask;
        });
        await Test("resolution transitions retain balanced at 900p and 720p then use native below 720p", () =>
        {
            var options = Options("sr-transitions") with { Width = 2560, Height = 1440,
                SuperResolutionEnabled = true, SuperResolutionTier = "performance" };
            options = (options with { Width = 1920, Height = 1080 }).NormalizeSuperResolutionForOutput();
            Equal("balanced", options.SuperResolutionTier, "1080p fallback"); options.Validate();
            options = (options with { Width = 1600, Height = 900 }).NormalizeSuperResolutionForOutput();
            Equal("balanced", options.SuperResolutionTier, "900p balanced"); options.Validate();
            options = (options with { Width = 1280, Height = 720 }).NormalizeSuperResolutionForOutput();
            Check(options.SuperResolutionEnabled && options.GetCanvasLayout().IsProxy, "720p remains enabled");
            Equal(480, options.GetCanvasLayout().CaptureHeight, "720p balanced capture");
            options = (options with { Width = 852, Height = 480 }).NormalizeSuperResolutionForOutput();
            Check(!options.SuperResolutionEnabled && options.GetCanvasLayout().IsIdentity, "sub-720p falls back to native");
            options = (options with { Width = 1920, Height = 1080, SuperResolutionEnabled = true }).NormalizeSuperResolutionForOutput();
            Equal("balanced", options.SuperResolutionTier, "default tier restored after native fallback");
            return Task.CompletedTask;
        });
        await Test("720p capture tiers use the existing Direct3D policy and preserve full-HD tier rules", () =>
        {
            foreach (string vendor in new[] { "nvenc", "amf", "qsv" })
            foreach (string model in new[] { "anime4k-cnn", "anime4k-rcas" })
            foreach (string tier in new[] { "quality", "balanced" })
            {
                var options = Options("sr720-policy") with { Width = 1280, Height = 720,
                    SuperResolutionEnabled = true, UpscaleAlgorithm = model, SuperResolutionTier = tier };
                Equal(OperatingSystem.IsWindows(), Direct3DUpscalePolicy.IsEligible(options, "h264_" + vendor), "uses unchanged candidate policy");
                Equal(480, options.MinimumSuperResolutionShortEdge, "720p floor");
            }
            foreach (var row in new[] { (1080, false), (1200, false), (1440, true), (2160, true) })
            {
                var options = Options("fullhd-policy") with { Width = row.Item1, Height = row.Item1 };
                Equal(720, options.MinimumSuperResolutionShortEdge, "existing full-HD floor");
                Equal(row.Item2, options.IsSuperResolutionTierAvailable("performance"), "existing performance availability");
            }
            return Task.CompletedTask;
        });
        await Test("algorithm selection reaches a single scale filter without changing bitrate or quality", () =>
        {
            foreach (string algorithm in new[] { "bilinear", "bicubic", "lanczos" })
            foreach (string encoder in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "qtrle" })
            {
                var options = Options("sr-filter") with { Width = 1920, Height = 1080, SuperResolutionEnabled = true,
                    UpscaleAlgorithm = algorithm, Codec = encoder == "qtrle" ? "qtrle" : "h264" };
                var args = EncoderArguments.Video(options, encoder, true).ToArray();
                string filter = args[Array.IndexOf(args, "-vf") + 1];
                Equal(1, filter.Split("scale=", StringSplitOptions.None).Length - 1, "one scale pass");
                Check(filter.Contains("flags=" + algorithm), "selected algorithm used");
                if (encoder == "h264_nvenc") Equal("p4", args[Array.IndexOf(args, "-preset") + 1], "retain encoder quality");
            }
            return Task.CompletedTask;
        });
        await Test("unknown tiers, fake models and mixed capture modes cannot start", () =>
        {
            var options = Options("sr-invalid") with { Width = 1920, Height = 1080, SuperResolutionEnabled = true };
            ThrowsCode("invalid_settings", (options with { SuperResolutionTier = "unknown" }).Validate, "unknown tier");
            foreach (string algorithm in new[] { "dlss", "unknown", "bilinear,negate" })
                ThrowsCode("invalid_settings", (options with { UpscaleAlgorithm = algorithm }).Validate, "unsupported algorithm");
            ThrowsCode("invalid_settings", (options with { CaptureMode = "proxy720" }).Validate, "mixed capture requests");
            return Task.CompletedTask;
        });
        await Test("GPU model filters load their actual shader without changing the selected dimensions", () =>
        {
            foreach (string model in new[] { "fsr1-luma", "anime4k-cnn", "anime4k-rcas" })
            {
                var options = Options("sr-gpu-rule") with { Width = 1920, Height = 1080,
                    SuperResolutionEnabled = true, UpscaleAlgorithm = model, Codec = "h264" };
                var args = EncoderArguments.Video(options, "h264_nvenc", true).ToArray();
                string filter = args[Array.IndexOf(args, "-vf") + 1];
                Check(filter.Contains("libplacebo=w=1920:h=1080") && filter.Contains("custom_shader_path=" + UpscaleShaders.FileName(model)),
                    "actual selected GPU model is part of the encode filter");
                var device = new List<string> { "-hide_banner" };
                EncoderArguments.AddHardwareDeviceOptions(device, options, "h264_nvenc");
                Check(device.Contains("vulkan=aa_upscale:NVIDIA"), "GPU filter is initialized on the selected NVIDIA device");
                ThrowsCode("invalid_settings", (options with { Codec = "qtrle" }).Validate, "GPU output format cannot masquerade as lossless RGB");
            }
            return Task.CompletedTask;
        });
        await Test("RCAS sharpness validates finite 0..1 values including bypass at zero", () =>
        {
            var options = Options("sr-rcas") with { Width = 1920, Height = 1080, SuperResolutionEnabled = true,
                UpscaleAlgorithm = "anime4k-rcas" };
            foreach (double strength in new[] { 0d, 0.87, 1d }) (options with { RcasSharpness = strength }).Validate();
            foreach (double strength in new[] { -0.01, 1.01, double.NaN, double.PositiveInfinity })
                ThrowsCode("invalid_settings", (options with { RcasSharpness = strength }).Validate, "invalid sharpness");
            return Task.CompletedTask;
        });
    }
}
