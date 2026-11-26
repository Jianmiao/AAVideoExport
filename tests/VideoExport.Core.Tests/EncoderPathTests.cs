using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestEncoderPaths()
    {
        await Test("1080p fill margins do not switch QSV preprocessing away from the full-HD path", () =>
        {
            var options = Options("fill-path") with
            {
                Width = 1920, Height = 1080, SourceWidth = 1920, SourceHeight = 1106,
                Encoder = "h264_qsv", CanvasMode = "fill", BitrateKbps = 6000
            };
            var canvas = options.GetCanvasLayout();
            Equal(8494080, canvas.CaptureWidth * canvas.CaptureHeight * 4, "native regression shape");
            Check(!EncoderArguments.UsesQsvVpp(options, options.Encoder), "small crop margins must not activate the larger-canvas path");
            var args = EncoderArguments.Video(options, options.Encoder, true).ToArray();
            string filter = args[Array.IndexOf(args, "-vf") + 1];
            Check(filter.StartsWith("vflip,crop=1920:1080:0:13,setsar=1,", StringComparison.Ordinal), "native pixels are flipped and center-cropped without resizing");
            Check(!filter.Contains("hwupload", StringComparison.Ordinal), "full-HD pipeline avoids another GPU upload stage");
            Equal("h264_qsv", args[Array.IndexOf(args, "-c:v") + 1], "hardware encoder retained");
            Equal("6000k", args[Array.IndexOf(args, "-b:v") + 1], "user bitrate retained");
            Equal("bt709", args[Array.IndexOf(args, "-colorspace") + 1], "color contract retained");
            return Task.CompletedTask;
        });
        await Test("QSV preprocessing stays on the stable CPU path for every delivered canvas", () =>
        {
            // The Intel driver hangs in vpp_qsv's RGBA->NV12/transpose chain at
            // 1080p and 2K. QSV remains the encoder, but frames are converted by
            // the CPU first so fit/fill and 1440p/2160p outputs always complete.
            foreach (var shape in new[] { (1920, 1080), (1080, 1920), (1280, 720), (2560, 1440), (3840, 2160) })
            foreach (string mode in new[] { "fit", "fill" })
            foreach (string codec in new[] { "h264", "hevc", "av1" })
            {
                var options = Options("canvas-path") with
                {
                    Width = shape.Item1, Height = shape.Item2, SourceWidth = 1920, SourceHeight = 1106,
                    CanvasMode = mode, Codec = codec, Encoder = codec + "_qsv"
                };
                Check(!EncoderArguments.UsesQsvVpp(options, options.Encoder), "QSV VPP must stay disabled");
                var args = EncoderArguments.Video(options, options.Encoder, true).ToArray();
                string filter = args[Array.IndexOf(args, "-vf") + 1];
                Check(!filter.Contains("vpp_qsv", StringComparison.Ordinal), "CPU preprocessing must not invoke vpp_qsv");
                Check(!filter.Contains("hwupload", StringComparison.Ordinal), "CPU preprocessing must not upload to QSV VPP");
                bool is4K = shape.Item1 >= 3840 || shape.Item2 >= 2160;
                Equal(is4K ? "nv12" : "yuv420p", args[Array.IndexOf(args, "-pix_fmt") + 1], "CPU frames are converted before QSV encoding");
                Equal(options.Encoder, args[Array.IndexOf(args, "-c:v") + 1], "hardware encoder retained");
                if (is4K)
                {
                    Equal("1", args[Array.IndexOf(args, "-low_power") + 1], "4K QSV uses the VDEnc low-power path");
                    Equal("2", args[Array.IndexOf(args, "-async_depth") + 1], "4K QSV keeps only two surfaces in flight");
                }
                else
                {
                    Equal("6", args[Array.IndexOf(args, "-async_depth") + 1], "lower-resolution QSV keeps six frames in flight");
                }
                Check(!EncoderArguments.UsesQsvVpp(options, codec + "_nvenc"), "NVENC does not receive QSV filters");
                Check(!EncoderArguments.UsesQsvVpp(options, codec + "_amf"), "AMF does not receive QSV filters");
            }
            return Task.CompletedTask;
        });
        await Test("4K hardware encoders select each vendor's native encoder and rate control", () =>
        {
            foreach (string codec in new[] { "h264", "hevc", "av1" })
            foreach (string vendor in new[] { "qsv", "nvenc", "amf" })
            {
                var options = Options("4k-vendor") with
                {
                    Width = 3840, Height = 2160, SourceWidth = 1920, SourceHeight = 1106,
                    Codec = codec, Encoder = codec + "_" + vendor, BitrateKbps = 8000
                };
                var args = EncoderArguments.Video(options, options.Encoder, true).ToArray();
                Equal(options.Encoder, args[Array.IndexOf(args, "-c:v") + 1], "vendor encoder is passed to FFmpeg");
                Equal("8000k", args[Array.IndexOf(args, "-b:v") + 1], "4K bitrate retained");
                Equal("12000k", args[Array.IndexOf(args, "-maxrate") + 1], "4K peak bitrate retained");
                Equal("16000k", args[Array.IndexOf(args, "-bufsize") + 1], "4K VBV buffer retained");
                if (vendor == "qsv")
                {
                    Equal("nv12", args[Array.IndexOf(args, "-pix_fmt") + 1], "QSV receives native NV12");
                    Equal("1", args[Array.IndexOf(args, "-low_power") + 1], "QSV 4K uses VDEnc");
                    Equal("2", args[Array.IndexOf(args, "-async_depth") + 1], "QSV 4K limits in-flight surfaces");
                }
                else if (vendor == "nvenc")
                {
                    Equal("p4", args[Array.IndexOf(args, "-preset") + 1], "NVENC uses its own preset");
                    Equal("vbr", args[Array.IndexOf(args, "-rc") + 1], "NVENC uses its own rate control");
                    Check(!args.Contains("-quality"), "NVENC does not receive AMF options");
                }
                else
                {
                    Equal("speed", args[Array.IndexOf(args, "-quality") + 1], "AMF uses its own quality mode");
                    Equal("vbr_peak", args[Array.IndexOf(args, "-rc") + 1], "AMF uses its own rate control");
                    Check(!args.Contains("-low_power"), "AMF does not receive QSV options");
                }
            }
            return Task.CompletedTask;
        });
        await Test("D3D preprocessing follows the selected hardware vendor", () =>
        {
            Equal(0x8086, Direct3DAnimeUpscaler.VendorIdForEncoder("h264_qsv", 0x1234), "QSV owns Intel adapter");
            Equal(0x10de, Direct3DAnimeUpscaler.VendorIdForEncoder("hevc_nvenc", 0x1234), "NVENC owns NVIDIA adapter");
            Equal(0x1002, Direct3DAnimeUpscaler.VendorIdForEncoder("av1_amf", 0x1234), "AMF owns AMD adapter");
            Equal(0x1234, Direct3DAnimeUpscaler.VendorIdForEncoder("libx264", 0x1234), "software encoding keeps the render adapter");
            return Task.CompletedTask;
        });
    }
}
