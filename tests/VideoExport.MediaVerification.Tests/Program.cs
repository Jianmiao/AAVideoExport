using System.Globalization;
using System.Text.Json;
using AAVideoExport.Core;

if (args.FirstOrDefault() == "-v") return RunProbeFixture(args);
int passed = 0, failed = 0;
void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Reject(Action action, string part)
{
    try { action(); throw new Exception("Expected verification failure: " + part); }
    catch (ExportException ex) { Check(ex.Code == "verification_failed" && ex.Message.Contains(part, StringComparison.OrdinalIgnoreCase), ex.Message); }
}
VideoPtsSequenceVerifier Grid(long count, int fps = 60, long scale = 60) => new(count, fps, new Rational(1, scale));
string fixture = Environment.ProcessPath!;
var options = new ExportOptions { Width = 96, Height = 64, Fps = 60, Container = "mp4", Codec = "h264", AudioQuality = "none" };

await Test("old bounded JSON path reproduces long-film truncation", async () =>
{
    var result = await FfmpegProcess.RunAsync(fixture, new[] { "-v", "error", "-show_entries", "packet=pts", "-of", "json", "fixture-long" }, TimeSpan.FromSeconds(30), CancellationToken.None);
    result.EnsureSuccess("fixture");
    Check(result.Output.Length == 262144, "legacy bounded diagnostic output limit changed");
    try { using var ignored = JsonDocument.Parse(result.Output); throw new Exception("Old long JSON unexpectedly parsed"); }
    catch (JsonException) { }
});
await Test("streamed 200000-frame verification survives previous stdout cap", () =>
    MediaVerifier.VerifyAsync(fixture, "fixture-long", options, 200000, TimeSpan.FromSeconds(30), CancellationToken.None));
await Test("valid B-frame decode order preserves exact presentation grid", () =>
{
    var grid = Grid(6); foreach (int i in new[] { 0, 3, 1, 2, 5, 4 }) grid.Accept(i.ToString(CultureInfo.InvariantCulture)); grid.Complete();
    Check(grid.Count == 6, "count"); return Task.CompletedTask;
});
await Test("missing timestamp cannot pass with correct metadata count", () =>
{
    var grid = Grid(3); grid.Accept("0"); grid.Accept("2"); Reject(grid.Complete, "count"); return Task.CompletedTask;
});
await Test("duplicate packet plus missing frame is rejected", () =>
{
    var grid = Grid(3); grid.Accept("0"); grid.Accept("1"); grid.Accept("1"); Reject(grid.Complete, "more than once"); return Task.CompletedTask;
});
await Test("out of timeline PTS is rejected", () =>
{
    var grid = Grid(3); grid.Accept("0"); grid.Accept("1"); grid.Accept("3"); Reject(grid.Complete, "outside"); return Task.CompletedTask;
});
await Test("negative presentation start is rejected", () =>
{
    var grid = Grid(3); grid.Accept("-1"); Reject(grid.Complete, "negative"); return Task.CompletedTask;
});
await Test("missing or malformed PTS is rejected", () =>
{
    foreach (string value in new[] { "N/A", "null", "NaN", "1|2", "9223372036854775808" })
    { var grid = Grid(1); grid.Accept(value); Reject(grid.Complete, "non-integer"); }
    return Task.CompletedTask;
});
await Test("Matroska 24fps uses exact rounded millisecond grid", () =>
{
    var grid = Grid(5, 24, 1000); foreach (int pts in new[] { 0, 167, 83, 42, 125 }) grid.Accept(pts.ToString()); grid.Complete(); return Task.CompletedTask;
});
await Test("one millisecond off-grid timestamp no longer passes loose tolerance", () =>
{
    var grid = Grid(3, 24, 1000); grid.Accept("0"); grid.Accept("43"); grid.Accept("83"); Reject(grid.Complete, "grid"); return Task.CompletedTask;
});
await Test("extra packet is rejected", () =>
{
    var grid = Grid(1); grid.Accept("0"); grid.Accept("1"); Reject(grid.Complete, "more frames"); return Task.CompletedTask;
});
await Test("stream reader drains even after malformed early timestamp", async () =>
{
    try { await MediaVerifier.VerifyAsync(fixture, "fixture-malformed", options, 200000, TimeSpan.FromSeconds(30), CancellationToken.None); throw new Exception("accepted malformed timestamp"); }
    catch (ExportException ex) { Check(ex.Code == "verification_failed" && ex.Message.Contains("non-integer"), ex.Message); }
});
await Test("streamed process preserves cancellation", async () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await FfmpegProcess.RunLinesAsync(fixture, new[] { "-v", "error", "fixture-long" }, _ => { }, TimeSpan.FromSeconds(30), cts.Token); throw new Exception("accepted cancellation"); }
    catch (OperationCanceledException) { }
});
await Test("throwing stdout consumer drains the process and preserves its error", async () =>
{
    try
    {
        await FfmpegProcess.RunLinesAsync(fixture, new[] { "-v", "error", "-show_entries", "packet=pts", "fixture-long" },
            _ => throw new InvalidOperationException("sink fixture"), TimeSpan.FromSeconds(30), CancellationToken.None);
        throw new Exception("stdout callback error was ignored");
    }
    catch (InvalidOperationException ex) { Check(ex.Message == "sink fixture", ex.Message); }
});
await Test("AAC explicit priming and tail discard produce exact effective samples", () =>
{
    var audio = new AudioPtsSequenceVerifier(1500);
    audio.Accept("pts=-1024|duration=1024|side_datum/skip_samples:side_data_type=Skip Samples|side_datum/skip_samples:skip_samples=1024|side_datum/skip_samples:discard_padding=0");
    audio.Accept("pts=0|duration=1024");
    audio.Accept("pts=1024|duration=1024|skip_samples=0|discard_padding=548");
    audio.Complete(); return Task.CompletedTask;
});
await Test("AAC negative priming without explicit skip cannot pass", () =>
{
    var audio = new AudioPtsSequenceVerifier(1024); audio.Accept("pts=-1024|duration=1024");
    Reject(audio.Complete, "gap or overlap"); return Task.CompletedTask;
});
await Test("audio gap and overlap each fail even when track metadata would match", () =>
{
    foreach (int start in new[] { 1023, 1025 })
    { var audio = new AudioPtsSequenceVerifier(2048); audio.Accept("pts=0|duration=1024"); audio.Accept($"pts={start}|duration=1024"); Reject(audio.Complete, "gap or overlap"); }
    return Task.CompletedTask;
});
await Test("audio packet count must end at exact sample and cannot hide truncation", () =>
{
    var audio = new AudioPtsSequenceVerifier(1024); audio.Accept("pts=0|duration=1023");
    Reject(audio.Complete, "sample count"); return Task.CompletedTask;
});
await Test("audio codec padding beyond packet bounds fails", () =>
{
    var audio = new AudioPtsSequenceVerifier(1024); audio.Accept("pts=0|duration=1024|discard_padding=1025");
    Reject(audio.Complete, "exceeds"); return Task.CompletedTask;
});
await Test("unknown audio packet duration is unverifiable", () =>
{
    var audio = new AudioPtsSequenceVerifier(1024); audio.Accept("pts=0|duration=N/A");
    Reject(audio.Complete, "non-integer"); return Task.CompletedTask;
});

int media = Array.IndexOf(args, "--media");
if (media >= 0)
{
    string toolDir = args[media + 1];
    string ffmpeg = Path.Combine(toolDir, "ffmpeg.exe"), ffprobe = Path.Combine(toolDir, "ffprobe.exe");
    string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "verification-media")); Directory.CreateDirectory(output);
    foreach ((string container, int fps, int frames) in new[] { ("mp4", 60, 20000), ("mkv", 24, 240), ("mov", 30, 300), ("mkv", 60, 600) })
        await Test($"real ffprobe {container} {fps}fps {frames} frames with B-frames", async () =>
        {
            string path = Path.Combine(output, $"{fps}-{frames}.{container}");
            var command = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", $"color=c=black:s=96x64:r={fps}", "-frames:v", frames.ToString(), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-bf", "3" };
            if (container is "mp4" or "mov") command.AddRange(new[] { "-video_track_timescale", fps.ToString() });
            command.Add(path);
            (await FfmpegProcess.RunAsync(ffmpeg, command, TimeSpan.FromMinutes(2), CancellationToken.None)).EnsureSuccess("fixture encode");
            await MediaVerifier.VerifyAsync(ffprobe, path, options with { Container = container, Fps = fps }, frames, TimeSpan.FromMinutes(2), CancellationToken.None);
        });
    foreach ((string container, string quality, int rate) in new[] { ("mp4", "pcm24", 48000), ("mov", "pcm16", 44100), ("mp4", "aac192", 48000), ("mov", "aac192", 44100) })
        await Test($"strict {container} {quality} {rate} exact audio sample boundary", async () =>
        {
            string path = await AudioFixture(container, quality, rate);
            await MediaVerifier.VerifyAsync(ffprobe, path, options with { Container = container, AudioQuality = quality, AudioSampleRate = rate, Fps = 24, StrictSynchronization = true }, 13, TimeSpan.FromSeconds(30), CancellationToken.None);
        });
    foreach ((string quality, int delta, bool exactMovieTimescale) in new[] { ("pcm24", -1, true), ("pcm24", 1, true), ("aac192", -1, true), ("aac192", 1, true), ("aac192", 0, false) })
        await Test($"strict rejects {quality} sample mismatch {delta} exacttimescale={exactMovieTimescale} while legacy allows it", async () =>
        {
            string path = await AudioFixture("mp4", quality, 48000, delta, exactMovieTimescale);
            var fixtureOptions = options with { AudioQuality = quality, AudioSampleRate = 48000, Fps = 24 };
            await MediaVerifier.VerifyAsync(ffprobe, path, fixtureOptions, 13, TimeSpan.FromSeconds(30), CancellationToken.None);
            try { await MediaVerifier.VerifyAsync(ffprobe, path, fixtureOptions with { StrictSynchronization = true }, 13, TimeSpan.FromSeconds(30), CancellationToken.None); throw new Exception("strict verification accepted a sample mismatch"); }
            catch (ExportException ex) { Check(ex.Code == "verification_failed", ex.Message); }
        });

    async Task<string> AudioFixture(string container, string quality, int rate, int delta = 0, bool exactMovieTimescale = true)
    {
        long samples = 13L * rate / 24 + delta;
        string path = Path.Combine(output, $"audio-{container}-{quality}-{rate}-{delta}-{exactMovieTimescale}.{container}");
        string codec = quality == "pcm24" ? "pcm_s24le" : quality == "pcm16" ? "pcm_s16le" : "aac";
        var command = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=black:s=96x64:r=24", "-f", "lavfi", "-i", $"sine=frequency=1000:sample_rate={rate},atrim=end_sample={samples}", "-frames:v", "13", "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-bf", "3", "-video_track_timescale", "24", "-c:a", codec, "-ar", rate.ToString(), "-ac", "2" };
        if (exactMovieTimescale) command.AddRange(new[] { "-movie_timescale", (rate == 44100 ? 88200 : 48000).ToString() });
        command.Add(path);
        (await FfmpegProcess.RunAsync(ffmpeg, command, TimeSpan.FromSeconds(30), CancellationToken.None)).EnsureSuccess("audio fixture encode");
        return path;
    }
}
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

static int RunProbeFixture(string[] arguments)
{
    const int count = 200000;
    int at = Array.IndexOf(arguments, "-show_entries");
    string entries = at >= 0 ? arguments[at + 1] : "";
    if (entries == "packet=pts")
    {
        bool json = arguments.Contains("json");
        if (json) Console.Write("{\"packets\":[");
        for (int i = 0; i < count; i++)
        {
            if (json) { if (i > 0) Console.Write(','); Console.Write("{\"pts\":" + i + "}"); }
            else Console.WriteLine(i == 1 && arguments.Contains("fixture-malformed") ? "N/A" : i.ToString(CultureInfo.InvariantCulture));
        }
        if (json) Console.Write("]}");
    }
    else Console.Write(JsonSerializer.Serialize(new { streams = new[] { new { codec_type = "video", codec_name = "h264", width = 96, height = 64, avg_frame_rate = "60/1", r_frame_rate = "60/1", time_base = "1/60", nb_frames = count.ToString(), start_time = "0.000000", duration = (count / 60d).ToString(CultureInfo.InvariantCulture) } }, format = new { duration = (count / 60d).ToString(CultureInfo.InvariantCulture) } }));
    return 0;
}
