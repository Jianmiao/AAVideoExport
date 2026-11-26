namespace AAVideoExport.Core;

internal static class EncoderArguments
{
    internal static bool UsesQsvVpp(ExportOptions options, string encoder)
    {
        // QSV VPP's RGBA->NV12/transpose chain hangs on the validated Intel
        // driver at both 1080p and 2K. Use the proven CPU preprocessing path
        // for every QSV output size; the hardware encoder still does the encode.
        return false;
    }

    internal static void AddHardwareDeviceOptions(List<string> args, ExportOptions options, string encoder)
    {
        if (UpscaleShaders.UsesGpu(options))
            args.InsertRange(1, new[] { "-init_hw_device", VulkanDevicePolicy.DeviceArgument(encoder, options.UpscaleGpuIndex), "-filter_hw_device", "aa_upscale" });
        else if (UsesQsvVpp(options, encoder))
            args.InsertRange(1, new[] { "-init_hw_device", "qsv=hw", "-filter_hw_device", "hw" });
    }

    internal static IEnumerable<string> Video(ExportOptions options, string encoder, bool flipVertical)
    {
        if (!EncoderCatalog.All.Any(e => e.Name == encoder && e.Codec == options.Codec))
            throw new ExportException("invalid_settings", "The encoder does not match the selected codec.");
        var args = new List<string> { "-c:v", encoder };
        var canvas = options.GetCanvasLayout();
        bool qsv = UsesQsvVpp(options, encoder);
        bool qsv4k = encoder.EndsWith("_qsv", StringComparison.Ordinal) &&
            (canvas.OutputWidth >= 3840 || canvas.OutputHeight >= 2160);
        if (UpscaleShaders.UsesGpu(options))
        {
            string filter = (flipVertical ? "vflip," : "") +
                "scale=in_range=full:out_range=tv:out_color_matrix=bt709,format=yuv420p," +
                "setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709," +
                $"hwupload,libplacebo=w={canvas.OutputWidth}:h={canvas.OutputHeight}:format=yuv420p:" +
                "colorspace=bt709:range=tv:color_primaries=bt709:color_trc=bt709:" +
                "downscaler=lanczos:custom_shader_path=" + UpscaleShaders.FileName(options.UpscaleAlgorithm) +
                ",hwdownload,format=yuv420p";
            args.AddRange(new[] { "-vf", filter, "-pix_fmt", "yuv420p", "-color_range", "tv", "-colorspace", "bt709",
                "-color_trc", "bt709", "-color_primaries", "bt709" });
        }
        else if (qsv)
        {
            // Keep any crop/pad required by the canvas contract in the source
            // frame, then move format conversion and vertical orientation to
            // QSV VPP. This avoids the CPU scale/format path for 4K exports.
            var filter = new List<string> { "format=rgba" };
            if (!string.IsNullOrEmpty(canvas.VideoFilter)) filter.Add(canvas.VideoFilter!);
            filter.Add("hwupload=extra_hw_frames=64");
            string vpp = "vpp_qsv=" + (flipVertical ? "transpose=6:" : "") +
                "format=nv12:out_range=tv:out_color_matrix=bt709:out_color_primaries=bt709:out_color_transfer=bt709";
            filter.Add(vpp);
            args.AddRange(new[] { "-vf", string.Join(',', filter), "-pix_fmt", "nv12" });
        }
        else
        {
            string geometry = (flipVertical ? "vflip," : "") +
                (string.IsNullOrEmpty(canvas.VideoFilter) ? "" : canvas.VideoFilter + ",");
            if (options.Codec == "qtrle")
            {
                string resize = canvas.IsProxy ? $"scale={canvas.OutputWidth}:{canvas.OutputHeight}:flags={options.EffectiveUpscaleAlgorithm}," : "";
                args.AddRange(new[] { "-vf", geometry + resize + "format=argb", "-pix_fmt", "argb" });
                return args;
            }
            // Unity supplies full-range RGB. Convert explicitly to limited-range Rec.709 SDR.
            string dimensions = canvas.IsProxy ? $"w={canvas.OutputWidth}:h={canvas.OutputHeight}:flags={options.EffectiveUpscaleAlgorithm}:" : "";
            string pixelFormat = qsv4k ? "nv12" : "yuv420p";
            args.AddRange(new[] { "-vf", geometry + "scale=" + dimensions + "in_range=full:out_range=tv:out_color_matrix=bt709,format=" + pixelFormat +
                ",setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
                "-pix_fmt", pixelFormat, "-color_range", "tv", "-colorspace", "bt709", "-color_trc", "bt709", "-color_primaries", "bt709" });
        }
        string rate = options.BitrateKbps + "k";
        string buffer = (options.BitrateKbps * 2) + "k";
        bool cbr = options.RateControl == "cbr";
        args.AddRange(new[] { "-b:v", rate });
        // SVT's VBR/CBR modes reject FFmpeg maxrate (SVT reserves it for capped CRF).
        if (encoder != "libsvtav1") args.AddRange(new[] { "-maxrate", cbr ? rate : (options.BitrateKbps * 3 / 2) + "k", "-bufsize", buffer });
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            args.AddRange(new[] { "-preset", "p4", "-rc", cbr ? "cbr" : "vbr" });
            if (cbr) args.AddRange(new[] { "-minrate", rate });
        }
        else if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
        {
            args.AddRange(new[] { "-preset", "veryfast" });
            if (qsv4k)
            {
                // Arc's H.264 PAK path can fail late in a 4K export when six
                // surfaces are in flight. VDEnc plus two surfaces keeps 4K
                // hardware H.264/HEVC/AV1 sessions inside the driver budget.
                args.AddRange(new[] { "-low_power", "1", "-async_depth", "2" });
            }
            else
            {
                // Keep six frames in flight for the CPU-conversion path. This
                // only changes pipeline overlap; rate control, pixel format,
                // timestamps and codec quality remain unchanged.
                args.AddRange(new[] { "-async_depth", "6" });
            }
            if (cbr) args.AddRange(new[] { "-minrate", rate });
        }
        else if (encoder.EndsWith("_amf", StringComparison.Ordinal))
        {
            args.AddRange(new[] { "-quality", "speed", "-rc", cbr ? "cbr" : "vbr_peak" });
            if (cbr) args.AddRange(new[] { "-minrate", rate });
        }
        else if (encoder == "libx264")
        {
            // Superfast stays the default; ultrafast is an explicit software
            // speed profile. Both preserve the single-thread strict contract.
            args.AddRange(new[] { "-preset", options.SoftwarePreset });
            if (cbr) args.AddRange(new[] { "-minrate", rate });
            string parameters = cbr ? "nal-hrd=cbr:force-cfr=1" : "";
            if (options.StrictSynchronization)
            {
                // strict-libx264-single-thread-v1: frame-threaded x264 with
                // VBV produced different decoded pixels from identical input.
                // Keep the bitrate/VBV contract; serialize this encoder only.
                args.AddRange(new[] { "-threads:v", "1" });
                parameters += (parameters.Length == 0 ? "" : ":") + "lookahead-threads=1";
            }
            if (parameters.Length != 0) args.AddRange(new[] { "-x264-params", parameters });
        }
        else if (encoder == "libx265")
        {
            args.AddRange(new[] { "-preset", "veryfast", "-x265-params", cbr ? "strict-cbr=1:hrd=1:pools=4" : "pools=4" });
            if (cbr) args.AddRange(new[] { "-minrate", rate });
        }
        else if (encoder == "libsvtav1")
            args.AddRange(new[] { "-preset", "10", "-svtav1-params", cbr ? "rc=2:lp=4:pred-struct=1" : "rc=1:lp=4" });
        else if (encoder == "libaom-av1")
        {
            args.AddRange(new[] { "-cpu-used", "8", "-row-mt", "1", "-threads", "4" });
            if (cbr) args.AddRange(new[] { "-minrate", rate });
        }
        return args;
    }

    internal static void AddFilterThreadOptions(List<string> args, ExportOptions options)
    {
        // This fixed bound matches the measured research proxy pipeline;
        // native exports retain their existing FFmpeg defaults.
        if (options.GetCanvasLayout().IsProxy) args.AddRange(new[] { "-filter_threads", "8" });
    }

    internal static IEnumerable<string> VideoNv12(ExportOptions options, string encoder)
    {
        if (options.GetCanvasLayout().IsProxy || UpscaleShaders.UsesGpu(options) ||
            options.Codec == "qtrle" || UsesQsvVpp(options, encoder))
            throw new ExportException("invalid_settings", "Preconverted NV12 requires a native hardware encoding canvas.");
        var args=Video(options,encoder,false).ToList();
        args[args.IndexOf("-vf")+1]="format=nv12,setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709";
        args[args.IndexOf("-pix_fmt")+1]="nv12";
        return args;
    }

    internal static IEnumerable<string> Audio(ExportOptions options)
    {
        if (options.AudioQuality == "none") return new[] { "-an" };
        var args = new List<string> { "-ac", "2", "-ar", options.AudioSampleRate.ToString() };
        if (options.AudioQuality.StartsWith("aac", StringComparison.Ordinal))
            args.AddRange(new[] { "-c:a", "aac", "-b:a", options.AudioQuality[3..] + "k" });
        else args.AddRange(new[] { "-c:a", options.AudioQuality == "pcm16" ? "pcm_s16le" : "pcm_s24le" });
        return args;
    }
}
