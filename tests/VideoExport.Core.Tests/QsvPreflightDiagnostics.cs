using System.Diagnostics;
using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestQsvPreflightDiagnostics()
    {
        foreach (string mode in new[] { "vbr", "cbr" })
        {
            var options = Options("qsv-preflight-" + mode) with { Width = 1920, Height = 1080, Fps = 60,
                Encoder = "h264_qsv", RateControl = mode, BitrateKbps = 12000, AudioQuality = "none", WriteCover = false };
            var canvas = options.GetCanvasLayout();
            var command = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
                "color=c=black:s=" + canvas.CaptureWidth + "x" + canvas.CaptureHeight + ":r=" + options.Fps,
                "-frames:v", "3", "-an" };
            EncoderArguments.AddHardwareDeviceOptions(command, options, "h264_qsv");
            EncoderArguments.AddFilterThreadOptions(command, options);
            command.AddRange(EncoderArguments.Video(options, "h264_qsv", false));
            command.AddRange(new[] { "-f", "null", "-" });
            Console.WriteLine("QSV PREFLIGHT ARGS " + mode + ": " + JsonSerializer.Serialize(command));
            var timer = Stopwatch.StartNew();
            var result = await FfmpegProcess.RunAsync(ffmpeg, command, TimeSpan.FromSeconds(10), CancellationToken.None, gpuEncoder: "h264_qsv");
            result.EnsureSuccess("Exact QSV preflight");
            Console.WriteLine($"QSV PREFLIGHT {mode}: {timer.Elapsed.TotalSeconds:F3}s; exit={result.ExitCode}");
        }
    }
}
