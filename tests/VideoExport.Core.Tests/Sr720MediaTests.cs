using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    // Local functional smoke only: three frames per tier. No throughput/timing
    // measurement; missing Intel hardware is a failure, never a claimed pass.
    private static async Task Test720pMedia()
    {
        foreach (var tier in new[] { "quality", "balanced" })
        await Test("720p " + tier + " Anime4K to Intel QSV retains dimensions, colors and frame direction", async () =>
        {
            var options = Options("sr720-media-" + tier) with
            {
                Width = 1280, Height = 720, Fps = 30, Encoder = "h264_qsv", BitrateKbps = 6000,
                AudioQuality = "none", WriteCover = false, SuperResolutionEnabled = true,
                SuperResolutionTier = tier, UpscaleAlgorithm = "anime4k-cnn"
            };
            var plan = options.GetCanvasLayout();
            byte[] frame = new byte[plan.CaptureWidth * plan.CaptureHeight * 4];
            byte[][] colors = { new byte[] { 210, 35, 45 }, new byte[] { 30, 200, 40 },
                new byte[] { 30, 50, 200 }, new byte[] { 210, 200, 40 } };
            for (int y = 0; y < plan.CaptureHeight; y++)
            for (int x = 0; x < plan.CaptureWidth; x++)
            {
                var rgb = colors[(y < plan.CaptureHeight / 2 ? 0 : 2) + (x < plan.CaptureWidth / 2 ? 0 : 1)];
                int offset = ((plan.CaptureHeight - 1 - y) * plan.CaptureWidth + x) * 4;
                for (int c = 0; c < 3; c++) frame[offset + c] = rgb[c];
                frame[offset + 3] = 255;
            }
            using var session = NewSession(options);
            Check(session.UpscaleExecution is "d3d11-nv12" or "d3d11", "actual Anime4K Direct3D path must execute for this smoke");
            for (int n = 0; n < 3; n++) session.WriteFrame(frame, frame.Length);
            var result = session.Complete();
            using var metadata = await Probe(result.OutputPath);
            var video = Stream(metadata, "video");
            Equal("1280", Property(video, "width"), "output width");
            Equal("720", Property(video, "height"), "output height");
            Equal("3", Property(video, "nb_read_frames"), "all three frames");
            Equal("tv", Property(video, "color_range"), "limited output range");
            Equal("bt709", Property(video, "color_space"), "matrix");
            var decoded = await DecodeVideo(result.OutputPath);
            Equal(3 * 1280 * 720 * 4, decoded.Length, "decoded frame bytes");
            for (int n = 0; n < 3; n++)
            for (int q = 0; q < 4; q++)
            {
                int x = q % 2 == 0 ? 320 : 960, y = q < 2 ? 180 : 540;
                int offset = ((n * 720 + y) * 1280 + x) * 4;
                for (int c = 0; c < 3; c++)
                    Check(Math.Abs(decoded[offset + c] - colors[q][c]) < 20, "quadrant direction and color remain correct");
            }
        });
    }
}
