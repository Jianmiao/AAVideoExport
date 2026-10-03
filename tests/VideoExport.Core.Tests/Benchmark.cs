using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task BenchmarkFill()
    {
        // Match the native regression's 8,494,080 RGBA bytes/frame. Source
        // generation is outside the timer and the exported file is fully decoded.
        var options = Options("benchmark-fill") with
        {
            Width = 1920, Height = 1080, SourceWidth = 1920, SourceHeight = 1106,
            CanvasMode = "fill", Fps = 60, Encoder = "h264_qsv", BitrateKbps = 6000,
            AudioQuality = "none", WriteCover = false
        };
        var plan = options.GetCanvasLayout();
        Equal(8494080, plan.CaptureWidth * plan.CaptureHeight * 4, "native capture byte count");
        var frame = new byte[plan.CaptureWidth * plan.CaptureHeight * 4];
        for (int y = 0; y < plan.CaptureHeight; y++)
        for (int x = 0; x < plan.CaptureWidth; x++)
        {
            int at = ((plan.CaptureHeight - 1 - y) * plan.CaptureWidth + x) * 4;
            frame[at] = (byte)(x % 256); frame[at + 1] = (byte)(y % 256);
            frame[at + 2] = (byte)((x + y) % 256); frame[at + 3] = 255;
            if (x < 64 && y < 96) { frame[at] = 220; frame[at + 1] = 24; frame[at + 2] = 24; }
            if (x < 64 && y >= plan.CaptureHeight - 96) { frame[at] = 24; frame[at + 1] = 24; frame[at + 2] = 220; }
        }
        using var session = NewSession(options, flipVertical: true);
        for (int i = 0; i < 600; i++)
        {
            // A moving stripe prevents a single repeated frame from hiding work.
            for (int x = 900; x < 932; x++)
                frame[(550 * plan.CaptureWidth + x) * 4] = (byte)(32 + i % 24 * 8);
            session.WriteFrame(frame, frame.Length);
        }
        var result = session.Complete();
        using var probe = await Probe(result.OutputPath);
        var stream = Stream(probe, "video");
        Equal("600", Property(stream, "nb_read_frames"), "all fill frames decoded");
        Equal("1920", Property(stream, "width"), "fill delivery width");
        Equal("1080", Property(stream, "height"), "fill delivery height");
        Equal("60/1", Property(stream, "avg_frame_rate"), "fill frame rate");
        Equal(10d, result.DurationSeconds, "fill duration");
        var decoded = await RunBinary(ffmpeg, "-v", "error", "-i", result.OutputPath,
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");
        int top = (16 * options.Width + 16) * 4;
        int bottom = ((options.Height - 16) * options.Width + 16) * 4;
        Check(Math.Abs(decoded[top] - 220) <= 12 && Math.Abs(decoded[top + 1] - 24) <= 12 && Math.Abs(decoded[top + 2] - 24) <= 12, "top marker preserves orientation and color");
        Check(Math.Abs(decoded[bottom] - 24) <= 12 && Math.Abs(decoded[bottom + 1] - 24) <= 12 && Math.Abs(decoded[bottom + 2] - 220) <= 12, "bottom marker preserves orientation and color");
        var fps = 600 / session.EncodingDuration.TotalSeconds;
        Console.WriteLine($"FILL BENCHMARK: {plan.CaptureWidth}x{plan.CaptureHeight} -> 1920x1080; 6000Kbps VBR; encoding={session.EncodingDuration.TotalSeconds:F3}s; {fps:F1} fps; decoded frame count/orientation/color passed.");
        File.WriteAllText(Path.Combine(Root, "fill-benchmark.json"), JsonSerializer.Serialize(new
        {
            CaptureWidth = plan.CaptureWidth, CaptureHeight = plan.CaptureHeight,
            Frames = result.Frames, Seconds = session.EncodingDuration.TotalSeconds, Fps = fps,
            Verification = "600 decoded frames, 1080p60, 10s, orientation and color markers"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ResolveArtifactsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Run this harness from a build inside the repository.");
        return Path.Combine(directory.FullName, "artifacts", "tests", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
    }

    private static async Task Benchmark()
    {
        const int frameCount = 600;
        var results = new List<object>();
        foreach (string rateControl in new[] { "vbr", "cbr" })
        {
            var options = Options("benchmark-qsv-1080p60-" + rateControl) with { Width = 1920, Height = 1080, Fps = 60,
                Encoder = "h264_qsv", RateControl = rateControl, BitrateKbps = 12000, AudioQuality = "none", WriteCover = false };
            // Source preparation is excluded. Most of the gradient is static; a central marker changes
            // each frame. This measures the RGBA pipe, conversion, encoder and mux, not AA rendering.
            var frame = new byte[options.Width * options.Height * 4];
            for (int y = 0; y < options.Height; y++)
            for (int x = 0; x < options.Width; x++)
            {
                int offset = (y * options.Width + x) * 4;
                frame[offset] = (byte)(x % 256); frame[offset + 1] = (byte)(y % 256);
                frame[offset + 2] = (byte)((x + y) % 256); frame[offset + 3] = 255;
                if (x < 64 && y < 64) { frame[offset] = 220; frame[offset + 1] = 24; frame[offset + 2] = 24; }
                if (x < 64 && y >= options.Height - 64) { frame[offset] = 24; frame[offset + 1] = 24; frame[offset + 2] = 220; }
            }
            var total = Stopwatch.StartNew();
            using var session = NewSession(options, flipVertical: false);
            for (int index = 0; index < frameCount; index++)
            {
                byte gray = (byte)(32 + index % 24 * 8);
                for (int y = 500; y < 532; y++)
                for (int x = 900; x < 932; x++)
                {
                    int offset = (y * options.Width + x) * 4;
                    frame[offset] = gray; frame[offset + 1] = gray; frame[offset + 2] = gray;
                }
                session.WriteFrame(frame, frame.Length);
            }
            var result = session.Complete();
            total.Stop();
            // Independent full-decode verification is outside the measured export total. Keep only
            // ffprobe JSON and one decoded RGBA frame, never the full ~5GB raw benchmark video.
            using var probe = await Probe(result.OutputPath);
            var video = Stream(probe, "video");
            Equal("600", Property(video, "nb_read_frames"), "benchmark fully decoded frame count");
            Equal("1920", Property(video, "width"), "benchmark width");
            Equal("1080", Property(video, "height"), "benchmark height");
            Equal("60/1", Property(video, "avg_frame_rate"), "benchmark exact fps");
            Equal("bt709", Property(video, "color_space"), "benchmark Rec.709 matrix");
            Equal("bt709", Property(video, "color_transfer"), "benchmark Rec.709 transfer");
            Equal("bt709", Property(video, "color_primaries"), "benchmark Rec.709 primaries");
            Equal(10d, result.DurationSeconds, "benchmark duration");
            var firstFrame = await RunBinary(ffmpeg, "-v", "error", "-i", result.OutputPath, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");
            Equal(frame.Length, firstFrame.Length, "benchmark decoded first frame size");
            int top = (16 * options.Width + 16) * 4;
            int bottom = ((options.Height - 16) * options.Width + 16) * 4;
            Check(Math.Abs(firstFrame[top] - 220) <= 10 && Math.Abs(firstFrame[top + 1] - 24) <= 10 && Math.Abs(firstFrame[top + 2] - 24) <= 10, "benchmark top corner color");
            Check(Math.Abs(firstFrame[bottom] - 24) <= 10 && Math.Abs(firstFrame[bottom + 1] - 24) <= 10 && Math.Abs(firstFrame[bottom + 2] - 220) <= 10, "benchmark bottom corner color");
            double sustainedFps = frameCount / session.EncodingDuration.TotalSeconds;
            Console.WriteLine($"BENCHMARK 1080p60 QSV {rateControl}: preflight={session.PreflightDuration.TotalSeconds:F3}s; frame writes+encoder drain={session.EncodingDuration.TotalSeconds:F3}s ({sustainedFps:F1} fps); finalization={session.FinalizationDuration.TotalSeconds:F3}s; total={total.Elapsed.TotalSeconds:F3}s ({frameCount / total.Elapsed.TotalSeconds:F1} fps). Verified all600 decoded frames,60fps,10s,Rec.709,corner pixels.");
            results.Add(new { result.OutputPath, result.Frames, result.DurationSeconds, result.FileBytes, RateControl = rateControl,
                PreflightSeconds = session.PreflightDuration.TotalSeconds, EncodingSeconds = session.EncodingDuration.TotalSeconds,
                FinalizationSeconds = session.FinalizationDuration.TotalSeconds, TotalSeconds = total.Elapsed.TotalSeconds,
                SustainedFps = sustainedFps, TotalFps = frameCount / total.Elapsed.TotalSeconds, LogicalProcessors = Environment.ProcessorCount,
                Content = "Synthetic mostly-static gradient with a changing central marker; RGBA source generation excluded; no AA rendering or audio.",
                Verification = "Independent full decode: 600 frames; 1920x1080; 60/1 fps; 10s; Rec.709; first-frame corner colors." });
        }
        File.WriteAllText(Path.Combine(Root, "benchmark-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
