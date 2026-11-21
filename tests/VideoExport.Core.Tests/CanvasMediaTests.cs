using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestCanvasMedia()
    {
        foreach (var example in new[]
        {
            (Name: "viewport-wide-portrait", SourceW: 160, SourceH: 90, W: 72, H: 128, Mode: "viewport"),
            (Name: "viewport-odd-source-landscape", SourceW: 1365, SourceH: 767, W: 128, H: 72, Mode: "viewport"),
            (Name: "fit-wide-square", SourceW: 160, SourceH: 90, W: 96, H: 96, Mode: "fit"),
            (Name: "fill-wide-square", SourceW: 160, SourceH: 90, W: 96, H: 96, Mode: "fill"),
            (Name: "fit-tall-wide", SourceW: 90, SourceH: 160, W: 128, H: 72, Mode: "fit"),
            (Name: "same-ratio-larger", SourceW: 80, SourceH: 45, W: 160, H: 90, Mode: "fill")
        })
        {
            await Test("decoded canvas pixels: " + example.Name, async () =>
            {
                var options = Options(example.Name) with
                {
                    Width = example.W, Height = example.H, SourceWidth = example.SourceW, SourceHeight = example.SourceH,
                    CanvasMode = example.Mode, Codec = "qtrle", Container = "mov", Encoder = "qtrle", AudioQuality = "none"
                };
                var plan = options.GetCanvasLayout();
                if (example.Mode == "viewport")
                {
                    Equal(options.Width, plan.CaptureWidth, "viewport captures the exact target width");
                    Equal(options.Height, plan.CaptureHeight, "viewport captures the exact target height");
                    Check(plan.VideoFilter is null, "viewport pixels must pass through without crop, padding or scale");
                }
                using var session = NewSession(options);
                const int frameCount = 6;
                byte[] Pixel(int x, int y, int frame) => new[] { (byte)(40 + x % 160), (byte)(50 + y % 160), (byte)(70 + frame * 20), (byte)255 };
                for (int frame = 0; frame < frameCount; frame++)
                {
                    var bytes = new byte[plan.CaptureWidth * plan.CaptureHeight * 4];
                    for (int y = 0; y < plan.CaptureHeight; y++)
                    for (int x = 0; x < plan.CaptureWidth; x++)
                        Pixel(x, y, frame).CopyTo(bytes, ((plan.CaptureHeight - 1 - y) * plan.CaptureWidth + x) * 4);
                    session.WriteFrame(bytes, bytes.Length);
                }
                var result = session.Complete();
                var decoded = await DecodeVideo(result.OutputPath);
                Equal(options.Width * options.Height * 4 * frameCount, decoded.Length, "exact output canvas and frame count");
                for (int frame = 0; frame < frameCount; frame++)
                for (int y = 0; y < options.Height; y++)
                for (int x = 0; x < options.Width; x++)
                {
                    int sx = example.Mode == "viewport" ? x : example.Mode == "fit" ? x - plan.OffsetX : x + plan.OffsetX;
                    int sy = example.Mode == "viewport" ? y : example.Mode == "fit" ? y - plan.OffsetY : y + plan.OffsetY;
                    var expected = sx < 0 || sy < 0 || sx >= plan.CaptureWidth || sy >= plan.CaptureHeight
                        ? new byte[] { 0, 0, 0, 255 } : Pixel(sx, sy, frame);
                    int at = ((frame * options.Height + y) * options.Width + x) * 4;
                    for (int c = 0; c < 4; c++)
                        Equal(expected[c], decoded[at + c], $"{example.Name} frame {frame} pixel {x},{y} channel {c}");
                }
                using var metadata = await Probe(result.OutputPath);
                Equal(options.Width.ToString(), Property(Stream(metadata, "video"), "width"), "canvas width");
                Equal(options.Height.ToString(), Property(Stream(metadata, "video"), "height"), "canvas height");
                var cover = await RunBinary(ffmpeg, "-v", "error", "-i", result.CoverPath!, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");
                Check(cover.SequenceEqual(decoded.Take(options.Width * options.Height * 4)), "cover uses same canvas transformation");
            });
        }
    }
}
