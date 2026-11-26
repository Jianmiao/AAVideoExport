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
                Equal("yuv420p", args[Array.IndexOf(args, "-pix_fmt") + 1], "CPU frames are converted before QSV encoding");
                Equal(options.Encoder, args[Array.IndexOf(args, "-c:v") + 1], "hardware encoder retained");
                Check(!EncoderArguments.UsesQsvVpp(options, codec + "_nvenc"), "NVENC does not receive QSV filters");
                Check(!EncoderArguments.UsesQsvVpp(options, codec + "_amf"), "AMF does not receive QSV filters");
            }
            return Task.CompletedTask;
        });
    }
}
