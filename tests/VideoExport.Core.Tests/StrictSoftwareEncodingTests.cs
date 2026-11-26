using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static string? StrictSoftwareArgument(IReadOnlyList<string> args, string key)
    {
        for (int i = 0; i < args.Count - 1; i++) if (args[i] == key) return args[i + 1];
        return null;
    }

    private static async Task TestStrictSoftwareArguments()
    {
        foreach (string mode in new[] { "vbr", "cbr" })
            await Test($"strict libx264 {mode} fixes encoder threads while retaining bounded rate control", () =>
            {
                var options = new ExportOptions { StrictSynchronization = true, RateControl = mode, BitrateKbps = 4000 };
                var args = EncoderArguments.Video(options, "libx264", false).ToArray();
                Equal("1", StrictSoftwareArgument(args, "-threads:v"), "strict libx264 frame threads");
                string[] parameters = (StrictSoftwareArgument(args, "-x264-params") ?? "").Split(':');
                Check(parameters.Contains("lookahead-threads=1"), "strict libx264 lookahead must be fixed at one thread");
                Equal(1, args.Count(a => a == "-x264-params"), "one combined x264 parameter option");
                Equal("4000k", StrictSoftwareArgument(args, "-b:v"), "target bitrate retained");
                Equal(mode == "cbr" ? "4000k" : "6000k", StrictSoftwareArgument(args, "-maxrate"), "VBV maximum retained");
                Equal("8000k", StrictSoftwareArgument(args, "-bufsize"), "VBV buffer retained");
                Equal("superfast", StrictSoftwareArgument(args, "-preset"), "speed preset retained");
                if (mode == "cbr")
                {
                    Equal("4000k", StrictSoftwareArgument(args, "-minrate"), "CBR minimum retained");
                    Check(parameters.Contains("nal-hrd=cbr") && parameters.Contains("force-cfr=1"), "strict CBR must retain its HRD and CFR parameters");
                }
                return Task.CompletedTask;
            });

        foreach (string mode in new[] { "vbr", "cbr" })
            await Test($"non-strict libx264 {mode} retains its existing threading and HRD behavior", () =>
            {
                var args = EncoderArguments.Video(new ExportOptions { RateControl = mode }, "libx264", false).ToArray();
                Check(!args.Contains("-threads:v"), "non-strict libx264 must retain automatic frame threading");
                Equal(mode == "cbr" ? "nal-hrd=cbr:force-cfr=1" : null,
                    StrictSoftwareArgument(args, "-x264-params"), "non-strict x264 parameters retained");
                return Task.CompletedTask;
            });

        await Test("strict libx264 explicit ultrafast keeps the single-thread determinism contract", () =>
        {
            var options = new ExportOptions { StrictSynchronization = true, SoftwarePreset = "ultrafast" };
            var args = EncoderArguments.Video(options, "libx264", false).ToArray();
            Equal("ultrafast", StrictSoftwareArgument(args, "-preset"), "explicit software speed preset");
            Equal("1", StrictSoftwareArgument(args, "-threads:v"), "strict ultrafast frame threads");
            Check((StrictSoftwareArgument(args, "-x264-params") ?? "").Split(':').Contains("lookahead-threads=1"),
                "strict ultrafast lookahead must be fixed at one thread");
            return Task.CompletedTask;
        });

        await Test("strict libx264 threading policy does not change other software or hardware encoders", () =>
        {
            foreach (var encoder in EncoderCatalog.All.Where(e => e.Name != "libx264"))
            {
                var options = new ExportOptions { Codec = encoder.Codec, StrictSynchronization = true };
                var args = EncoderArguments.Video(options, encoder.Name, false).ToArray();
                Check(!args.Contains("-threads:v"), encoder.Name + " received libx264-only frame threading");
                Check(!args.Contains("-x264-params"), encoder.Name + " received libx264-only parameters");
            }
            return Task.CompletedTask;
        });
    }

    // This opt-in media test exercises production argument construction with
    // one frozen decoded RGBA fixture. It proves the encoder boundary only;
    // real AA capture/replay still requires independent repeated acceptance.
    private static async Task TestStrictSoftwareRepeat(string? source)
    {
        Check(!string.IsNullOrWhiteSpace(source) && File.Exists(source), "--repeat-input=<existing movie> is required");
        source = Path.GetFullPath(source!);
        using var probe = await Probe(source);
        var stream = probe.RootElement.GetProperty("streams").EnumerateArray()
            .First(s => s.GetProperty("codec_type").GetString() == "video");
        int width = stream.GetProperty("width").GetInt32(), height = stream.GetProperty("height").GetInt32();
        int frameCount = int.Parse(stream.GetProperty("nb_read_frames").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Equal("30/1", stream.GetProperty("r_frame_rate").GetString(), "bounded repeat fixture frame rate");
        var options = new ExportOptions { Width = width, Height = height, Fps = 30,
            StrictSynchronization = true, EncodingMode = "software", Encoder = "libx264", Codec = "h264",
            Container = "mov", RateControl = "vbr", BitrateKbps = 4000, AudioQuality = "none", WriteCover = false };
        string directory = Path.Combine(Root, "strict-software-repeat");
        Directory.CreateDirectory(directory);
        string fixture = Path.Combine(directory, "frozen-input.rgba");
        var decode = new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-threads", "1", "-i", source,
            "-map", "0:v:0", "-an", "-sn", "-dn", "-fps_mode", "passthrough", "-threads", "1", "-pix_fmt", "rgba", "-f", "rawvideo", fixture };
        var prepared = await FfmpegProcess.RunAsync(ffmpeg, decode, TimeSpan.FromMinutes(2), CancellationToken.None);
        File.WriteAllText(Path.Combine(directory, "decode.log"), prepared.Error);
        prepared.EnsureSuccess("Frozen RGBA fixture preparation");
        Equal((long)width * height * 4 * frameCount, new FileInfo(fixture).Length, "frozen raw frame count");
        using var sha256 = SHA256.Create();
        string fixtureSha;
        using (var input = File.OpenRead(fixture)) fixtureSha = Convert.ToHexString(sha256.ComputeHash(input));
        string sourceSha;
        using (var input = File.OpenRead(source)) sourceSha = Convert.ToHexString(sha256.ComputeHash(input));
        var videoArguments = EncoderArguments.Video(options, "libx264", false).ToArray();
        var receipts = new List<object>();
        string[]? firstFrames = null;
        try
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                string output = Path.Combine(directory, $"production-profile-{attempt:00}.mov");
                var args = new List<string> { "-hide_banner", "-loglevel", "info", "-nostdin", "-n", "-f", "rawvideo", "-pixel_format", "rgba",
                    "-video_size", width + "x" + height, "-framerate", "30", "-i", fixture, "-an" };
                EncoderArguments.AddHardwareDeviceOptions(args, options, "libx264");
                EncoderArguments.AddFilterThreadOptions(args, options);
                args.AddRange(videoArguments);
                args.AddRange(new[] { "-fps_mode", "passthrough", "-video_track_timescale", "30", output });
                var result = await FfmpegProcess.RunAsync(ffmpeg, args, TimeSpan.FromMinutes(2), CancellationToken.None, gpuEncoder: "libx264");
                File.WriteAllText(output + ".encode.log", result.Error);
                result.EnsureSuccess("Production strict software repeat " + attempt);
                var hash = await FfmpegProcess.RunAsync(ffmpeg, new[] { "-v", "error", "-threads", "1", "-i", output,
                    "-map", "0:v:0", "-an", "-fps_mode", "passthrough", "-threads", "1", "-pix_fmt", "yuv420p", "-c:v", "rawvideo", "-f", "framehash", "-hash", "sha256", "-" },
                    TimeSpan.FromMinutes(2), CancellationToken.None);
                hash.EnsureSuccess("Production strict repeat decoded hash " + attempt);
                File.WriteAllText(output + ".framehash.txt", hash.Output);
                var frames = hash.Output.Split('\n').Select(s => s.Trim()).Where(s => s.Length != 0 && !s.StartsWith('#')).ToArray();
                int mismatches = firstFrames is null ? 0 : frames.Zip(firstFrames).Count(pair => pair.First != pair.Second);
                string outputSha;
                using (var input = File.OpenRead(output)) outputSha = Convert.ToHexString(sha256.ComputeHash(input));
                receipts.Add(new { Attempt = attempt, Output = output, Arguments = args, Frames = frames.Length,
                    DifferentDecodedFramesFromFirst = mismatches, OutputSha256 = outputSha });
                File.WriteAllText(Path.Combine(directory, "production-parameter-receipt.json"), JsonSerializer.Serialize(new
                {
                    Profile = "strict-libx264-single-thread-v1", Scope = "encoder-only; no AA capture determinism claim",
                    SourceMovie = source, SourceSha256 = sourceSha, FrozenRgbaSha256 = fixtureSha,
                    Width = width, Height = height, Fps = 30, ExpectedFrames = frameCount,
                    BitrateKbps = 4000, MaxrateKbps = 6000, BufferKbits = 8000,
                    ProductionVideoArguments = videoArguments, Attempts = receipts
                }, new JsonSerializerOptions { WriteIndented = true }));
                Equal(frameCount, frames.Length, "exact decoded frame count for attempt " + attempt);
                Equal(0, mismatches, "exact decoded frame/presentation timeline equality for attempt " + attempt);
                Check(result.Error.Contains("threads=1 lookahead_threads=1", StringComparison.Ordinal), "FFmpeg did not apply the deterministic x264 thread profile");
                firstFrames ??= frames;
            }
        }
        finally { File.Delete(fixture); }
    }
}
