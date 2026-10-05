using System.Diagnostics;
using System.Text.Json;
using AAVideoExport.Core;

// Controlled child process boundary: prove probes actually invoke an encoder,
// never probe hardware for software mode, and honor absent/failed encoders.
if (args.FirstOrDefault() == "-hide_banner") return FakeFfmpeg(args);

int passed = 0, failed = 0;
var options = new ExportOptions { Title = "software-test", OutputDirectory = Path.GetTempPath(), EncodingMode = "software" };
var candidates = new[]
{
    new EncoderCapability("h264_qsv", "h264", true, false, "no hardware"),
    new EncoderCapability("h264_nvenc", "h264", true, true, "fixture"),
    new EncoderCapability("libx264", "h264", false, true, "fixture"),
    new EncoderCapability("libx265", "hevc", false, false, "failed"),
    new EncoderCapability("libaom-av1", "av1", false, true, "fixture"),
    new EncoderCapability("libsvtav1", "av1", false, true, "fixture")
};
await Test("default mode preserves hardware", () => Check(new ExportOptions().EncodingMode == "hardware"));
await Test("software auto uses x264 even with working hardware", () => Check(EncoderSelectionPolicy.Select(options, candidates, "NVIDIA") == "libx264"));
await Test("software remains usable when all hardware is unavailable", () => Check(EncoderSelectionPolicy.Select(options, candidates.Where(c => !c.Hardware)) == "libx264"));
await Test("hardware automatic never falls back to software", () => Throws("hardware_encoder_unavailable", () => EncoderSelectionPolicy.Select(options with { EncodingMode = "hardware" }, candidates.Where(c => !c.Hardware))));
await Test("explicit software encoder is retained", () => Check(EncoderSelectionPolicy.Select(options with { Encoder = "libx264" }, candidates) == "libx264"));
await Test("software mode rejects selected hardware encoder", () => Throws("software_encoder_required", () => EncoderSelectionPolicy.Select(options with { Encoder = "h264_nvenc" }, candidates)));
await Test("hardware mode rejects selected software encoder", () => Throws("hardware_encoder_required", () => EncoderSelectionPolicy.Select(options with { EncodingMode = "hardware", Encoder = "libx264" }, candidates)));
await Test("software auto cannot choose failed libx265", () => Throws("software_encoder_unavailable", () => EncoderSelectionPolicy.Select(options with { Codec = "hevc" }, candidates)));
await Test("absent libx264 cannot borrow another codec or hardware", () => Throws("software_encoder_unavailable", () => EncoderSelectionPolicy.Select(options, candidates.Where(c => c.Name != "libx264"))));
await Test("failed explicitly selected software cannot fall back", () => Throws("software_encoder_unavailable", () => EncoderSelectionPolicy.Select(options with { Codec = "hevc", Encoder = "libx265" }, candidates)));
await Test("SVT is preferred to AOM when both passed", () => Check(EncoderSelectionPolicy.Select(options with { Codec = "av1" }, candidates) == "libsvtav1"));
await Test("mode filtering excludes hardware, failures, other codecs and duplicates", () => Check(EncoderSelectionPolicy.GetAvailable(candidates.Concat(candidates), "software", "h264").Select(c => c.Name).SequenceEqual(new[] { "libx264" })));
await Test("invalid modes rejected", () => { Throws("invalid_settings", () => EncoderSelectionPolicy.Select(options with { EncodingMode = "automatic" }, candidates)); Throws("invalid_settings", () => EncoderSelectionPolicy.GetAvailable(candidates, "automatic")); });
await Test("mode error text tells users software dependency and hardware alternative", () =>
{
    Check(ExportErrorText.Describe(new ExportException("software_encoder_unavailable", "fixture")).Contains("libx264"));
    Check(ExportErrorText.Describe(new ExportException("hardware_encoder_unavailable", "fixture")).Contains("CPU"));
});
await TestAsync("missing FFmpeg fails explicitly", async () => await ThrowsAsync("process_start_failed", () => FfmpegCapabilities.ProbeSoftwareAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-ffmpeg.exe"))));

string fixtureExe = Path.Combine(AppContext.BaseDirectory, "VideoExport.SoftwareEncoding.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
await TestAsync("software probe executes real three-frame command without GPU initialization", async () =>
{
    var capabilities = await FfmpegCapabilities.ProbeSoftwareAsync(fixtureExe);
    Check(capabilities.Encoders.All(c => !c.Hardware));
    Check(capabilities.SoftwareEncoders.Select(c => c.Name).SequenceEqual(new[] { "libx264" }));
    Check(capabilities.HardwareEncoders.Count == 0 && capabilities.PickForMode(options) == "libx264");
});
await TestAsync("FFmpeg build lacking libx264 is unavailable", async () =>
{
    Environment.SetEnvironmentVariable("AAVE_TEST_SOFTWARE_FIXTURE", "absent");
    try { var caps = await FfmpegCapabilities.ProbeSoftwareAsync(fixtureExe); Throws("software_encoder_unavailable", () => caps.PickForMode(options)); Check(caps.Encoders.Single(c => c.Name == "libx264").Detail.Contains("Not included")); }
    finally { Environment.SetEnvironmentVariable("AAVE_TEST_SOFTWARE_FIXTURE", null); }
});
await TestAsync("advertised software encoder must also pass actual encode", async () =>
{
    Environment.SetEnvironmentVariable("AAVE_TEST_SOFTWARE_FIXTURE", "failed");
    try { var caps = await FfmpegCapabilities.ProbeSoftwareAsync(fixtureExe); Throws("software_encoder_unavailable", () => caps.PickForMode(options)); Check(caps.Encoders.Single(c => c.Name == "libx264").Detail.Contains("fixture encode failed")); }
    finally { Environment.SetEnvironmentVariable("AAVE_TEST_SOFTWARE_FIXTURE", null); }
});
await TestAsync("hardware-only probe remains hardware-only and cannot fall back", async () =>
{
    var caps = await FfmpegCapabilities.ProbeHardwareAsync(fixtureExe, "fixture GPU");
    Check(caps.Encoders.All(c => c.Hardware) && caps.SoftwareEncoders.Count == 0);
    Throws("hardware_encoder_unavailable", () => caps.PickForMode(options with { EncodingMode = "hardware" }));
});

int mediaIndex = Array.IndexOf(args, "--media");
if (mediaIndex >= 0)
{
    if (mediaIndex + 1 >= args.Length) throw new ArgumentException("--media requires the FFmpeg/ffprobe directory.");
    string mediaTools = Path.GetFullPath(args[mediaIndex + 1]);
    int outputIndex = Array.IndexOf(args, "--output");
    string output = outputIndex >= 0 ? Path.GetFullPath(args[outputIndex + 1]) : Path.Combine(Path.GetTempPath(), "AAVE-0.2.1-software-tests");
    foreach (string rate in new[] { "vbr", "cbr" })
        await TestAsync("real libx264 " + rate + " video + AAC + cover and decoded timeline", async () => await MediaExport(mediaTools, output, rate, false));
    await TestAsync("real libx264 scalar resize uses CPU filters without a GPU upscaler", async () => await MediaExport(mediaTools, output, "vbr", true));
}
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

Task Test(string name, Action run) => TestAsync(name, () => { run(); return Task.CompletedTask; });
async Task TestAsync(string name, Func<Task> run)
{
    try { await run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
}
static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
static void Throws(string code, Action run)
{
    try { run(); } catch (ExportException error) when (error.Code == code) { return; }
    throw new Exception("Expected " + code);
}
static async Task ThrowsAsync(string code, Func<Task> run)
{
    try { await run(); } catch (ExportException error) when (error.Code == code) { return; }
    throw new Exception("Expected " + code);
}
static int FakeFfmpeg(string[] argv)
{
    string? state = Environment.GetEnvironmentVariable("AAVE_TEST_SOFTWARE_FIXTURE");
    if (argv.Contains("-encoders"))
    {
        Console.WriteLine(" V..... h264_nvenc fixture hardware encoder");
        if (state != "absent") Console.WriteLine(" V..... libx264 fixture software encoder");
        return 0;
    }
    bool valid = argv.Contains("libx264") && argv.Contains("-frames:v") && argv[Array.IndexOf(argv, "-frames:v") + 1] == "3" &&
        !argv.Contains("-init_hw_device") && !argv.Any(a => a.Contains("hwupload", StringComparison.Ordinal) || a.Contains("libplacebo", StringComparison.Ordinal));
    if (valid && state != "failed") return 0;
    Console.Error.WriteLine("fixture encode failed: hardware is unavailable or three-frame software contract was violated");
    return 1;
}
static async Task MediaExport(string toolDirectory, string root, string rate, bool resize)
{
    string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
    string ffmpeg = Path.Combine(toolDirectory, "ffmpeg" + suffix), ffprobe = Path.Combine(toolDirectory, "ffprobe" + suffix);
    string title = "cpu-" + rate + (resize ? "-resize" : "") + "-" + Guid.NewGuid().ToString("N");
    var settings = new ExportOptions { Title = title, OutputDirectory = root, Width = resize ? 1280 : 256, Height = resize ? 720 : 144,
        Fps = 30, EncodingMode = "software", Codec = "h264", Encoder = "libx264", AudioQuality = "aac192", RateControl = rate,
        SuperResolutionEnabled = resize, SuperResolutionTier = "balanced", UpscaleAlgorithm = "bilinear", WriteCover = true };
    var capabilities = await FfmpegCapabilities.ProbeSoftwareAsync(ffmpeg);
    string selected = capabilities.PickForMode(settings);
    Check(selected == "libx264");
    var canvas = settings.GetCanvasLayout();
    using var session = new ExportSession(settings, ffmpeg, ffprobe, selected, flipVertical: false);
    Check(session.UpscaleExecution == "none" && session.UpscaleAdapter.Length == 0);
    byte[] pixels = new byte[canvas.CaptureWidth * canvas.CaptureHeight * 4];
    const int frames = 30;
    for (int f = 0; f < frames; f++)
    {
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(f * 7); pixels[i + 1] = 70; pixels[i + 2] = 130; pixels[i + 3] = 255; }
        session.WriteFrame(pixels, pixels.Length);
        var samples = new float[48000 / settings.Fps * 2];
        for (int s = 0; s < samples.Length / 2; s++) samples[s * 2] = samples[s * 2 + 1] = (float)(Math.Sin(2 * Math.PI * 440 * (f * 1600 + s) / 48000) * .15);
        session.WriteAudio(samples, samples.Length);
    }
    var result = session.Complete();
    Check(result.Frames == frames && result.DurationSeconds == 1 && result.Encoder == "libx264" && result.FileBytes > 0);
    Check(result.CoverPath is not null && new FileInfo(result.CoverPath).Length > 0);
    string probe = await RunProcess(ffprobe, "-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=codec_name,width,height,avg_frame_rate,nb_read_frames:format=duration", "-of", "json", result.OutputPath);
    using var json = JsonDocument.Parse(probe);
    var video = json.RootElement.GetProperty("streams")[0];
    Check(video.GetProperty("codec_name").GetString() == "h264" && video.GetProperty("width").GetInt32() == settings.Width && video.GetProperty("height").GetInt32() == settings.Height);
    Check(video.GetProperty("avg_frame_rate").GetString() == "30/1" && video.GetProperty("nb_read_frames").GetString() == "30");
    // Decode all frames and audio too; metadata alone is insufficient evidence.
    await RunProcess(ffmpeg, "-v", "error", "-i", result.OutputPath, "-f", "null", "-");
    Check(!Directory.EnumerateDirectories(root, ".aa-video-export-*").Any());
    Console.WriteLine("MEDIA " + result.OutputPath);
}
static async Task<string> RunProcess(string executable, params string[] args)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in args) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new Exception("Cannot start media verifier.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
    catch { process.Kill(true); throw; }
    string output = await stdout, error = await stderr;
    if (process.ExitCode != 0) throw new Exception("Media verification failed: " + error);
    return output;
}
