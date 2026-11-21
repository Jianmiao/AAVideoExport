using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Core;

string tools = args.Length > 0 ? Path.GetFullPath(args[0]) : @"F:\AzureArchive_101\mods\AAVideoExport\0.2.4\tools";
string ffmpeg = Path.Combine(tools, "ffmpeg.exe"), ffprobe = Path.Combine(tools, "ffprobe.exe");
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "artifacts", "controlled-video-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var results = new List<object>();
int passed = 0, failed = 0;
const int width = 8, height = 4, bytesPerFrame = width * height * 4;
byte[][] originals = Enumerable.Range(0, 4).Select(MakeFrame).ToArray();
string raw = Path.Combine(root, "colors.rgba");
File.WriteAllBytes(raw, originals.SelectMany(frame => frame).ToArray());
string cfr = Path.Combine(root, "cfr.mkv"), vfr = Path.Combine(root, "vfr.mkv");
await Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-n", "-f", "rawvideo", "-pixel_format", "rgba",
    "-video_size", "8x4", "-framerate", "10", "-i", raw, "-c:v", "ffv1", "-level", "3", "-pix_fmt", "bgra", cfr });
await Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-n", "-f", "rawvideo", "-pixel_format", "rgba",
    "-video_size", "8x4", "-framerate", "10", "-i", raw, "-vf", @"setpts=if(eq(N\,0)\,0\,if(eq(N\,1)\,1\,if(eq(N\,2)\,4\,6)))",
    "-vsync", "0", "-c:v", "ffv1", "-level", "3", "-pix_fmt", "bgra", vfr });

await Test("source frames decode once without pixel synthesis and retain source identity", async () =>
{
    string before = Hash(cfr);
    using var store = await ControlledVideoFrameStore.PrepareAsync(cfr, ffmpeg, ffprobe, Cache("cfr"));
    Check(store.Width == 8 && store.Height == 4 && store.FrameCount == 4 && store.FrameByteCount == bytesPerFrame, "Dimensions/frame count changed.");
    Check(store.SourceSha256 == before && Hash(cfr) == before, "Source hash was not bound or source changed.");
    Check(File.Exists(Path.Combine(store.DirectoryPath, "index.json")), "Completed frame index was not published.");
    var buffer = new byte[bytesPerFrame];
    for (int i = 0; i < 4; i++)
    {
        var selected = store.CopyFrameForOutput(i, 10, false, buffer);
        Check(selected.Index == i && selected.ByteOffset == i * bytesPerFrame, "Source frame/offset changed.");
        Check(buffer.SequenceEqual(originals[i]), "Lossless frame pixels changed at " + i);
    }
    await store.ValidateSourceIdentityAsync();
});

await Test("rational output clocks select exact PTS intervals and explicit loop rollover", async () =>
{
    using var store = await ControlledVideoFrameStore.PrepareAsync(cfr, ffmpeg, ffprobe, Cache("clocks"));
    foreach (var point in new[] { (0L, 24, 0), (2L, 24, 0), (3L, 24, 1), (5L, 24, 2),
        (8L, 24, 3), (10L, 24, 0), (3L, 30, 1), (6L, 30, 2), (9L, 30, 3),
        (12L, 30, 0), (6L, 60, 1), (12L, 60, 2), (18L, 60, 3), (24L, 60, 0) })
        Check(store.SelectFrame(point.Item1, point.Item2, true).Index == point.Item3,
            $"Wrong source frame at {point.Item1}/{point.Item2}.");
    Check(store.SelectFrame(12, 30, false).Index == 3, "Non-looping playback did not hold the last source frame.");
    Check(store.SelectFrame(long.MaxValue, 30, true).Index == 2, "Large rational time overflowed or rounded across a boundary.");
});

await Test("variable source PTS hold frames until the next actual PTS", async () =>
{
    using var store = await ControlledVideoFrameStore.PrepareAsync(vfr, ffmpeg, ffprobe, Cache("vfr"));
    Check(store.TimeBaseNumerator == 1 && store.TimeBaseDenominator == 1000, "Fixture time base changed.");
    Check(store.Frames.Select(frame => frame.PresentationTimestamp).SequenceEqual(new long[] { 0, 100, 400, 600 }), "VFR PTS were synthesized.");
    Check(store.DurationTicks == 700, "Final source frame duration was not retained.");
    var buffer = new byte[bytesPerFrame];
    foreach (var point in new[] { (0L, 0), (1L, 1), (3L, 1), (4L, 2), (5L, 2), (6L, 3), (7L, 0), (10L, 1) })
    {
        var selected = store.CopyFrameForOutput(point.Item1, 10, true, buffer);
        Check(selected.Index == point.Item2 && buffer.SequenceEqual(originals[point.Item2]), "VFR interval or pixels changed.");
    }
});

await Test("source identity changes are detected before reuse", async () =>
{
    string path = Path.Combine(root, "source-can-change.mkv");
    File.Copy(cfr, path);
    using var store = await ControlledVideoFrameStore.PrepareAsync(path, ffmpeg, ffprobe, Cache("identity"));
    using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write)) writer.WriteByte(1);
    await RejectAsync(() => store.ValidateSourceIdentityAsync(), "changed source was accepted", "video_source_changed");
});

await Test("nonzero source PTS maps its first decoded frame to playback zero", async () =>
{
    string source = Path.Combine(root, "offset.mkv");
    await Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-n", "-i", cfr, "-map", "0:v:0",
        "-c", "copy", "-output_ts_offset", "2", source });
    using var store = await ControlledVideoFrameStore.PrepareAsync(source, ffmpeg, ffprobe, Cache("offset"));
    Check(store.Frames[0].PresentationTimestamp == 2000 && store.DurationTicks == 400, "Source offset was discarded or counted as content duration.");
    var buffer = new byte[bytesPerFrame];
    Check(store.CopyFrameForOutput(0, 30, true, buffer).Index == 0 && buffer.SequenceEqual(originals[0]), "First source frame was skipped.");
    Check(store.CopyFrameForOutput(12, 30, true, buffer).Index == 0 && buffer.SequenceEqual(originals[0]), "Nonzero origin changed loop rollover.");
});

await Test("a raw video with no presentation timestamps cannot publish a guessed timeline", async () =>
{
    string source = Path.Combine(root, "no-pts.h264");
    await Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-n", "-i", cfr, "-map", "0:v:0",
        "-c:v", "libx264", "-pix_fmt", "yuv444p", "-f", "h264", source });
    string cache = Cache("missing-pts");
    await RejectAsync(() => ControlledVideoFrameStore.PrepareAsync(source, ffmpeg, ffprobe, cache), "Missing PTS was silently inferred.", "video_frame_store_invalid");
    Check(!Directory.EnumerateFileSystemEntries(cache).Any(), "Missing timestamp source published a partial store.");
});

await Test("invalid input or decoded size budget never publish partial stores", async () =>
{
    string invalid = Path.Combine(root, "invalid.mkv");
    File.WriteAllText(invalid, "not a video");
    string malformedCache = Cache("malformed");
    await RejectAsync(() => ControlledVideoFrameStore.PrepareAsync(invalid, ffmpeg, ffprobe, malformedCache), "invalid source was accepted");
    Check(!Directory.EnumerateFileSystemEntries(malformedCache).Any(), "Invalid source published a partial store.");
    string limitedCache = Cache("limited");
    await RejectAsync(() => ControlledVideoFrameStore.PrepareAsync(cfr, ffmpeg, ffprobe, limitedCache,
        maxDecodedBytes: bytesPerFrame * 4 - 1), "decoded size limit was ignored", "video_store_limit");
    Check(!Directory.EnumerateFileSystemEntries(limitedCache).Any(), "Oversized decode published a partial store.");
});

await Test("cancellation while decoding removes only the owned unpublished store", async () =>
{
    string source = Path.Combine(root, "cancel.mkv");
    await Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "color=c=red:s=640x360:r=30",
        "-frames:v", "300", "-c:v", "ffv1", source });
    string cache = Cache("cancel");
    string unrelated = Path.Combine(cache, "unrelated.txt");
    File.WriteAllText(unrelated, "preserve");
    using var cancel = new CancellationTokenSource();
    var work = ControlledVideoFrameStore.PrepareAsync(source, ffmpeg, ffprobe, cache, cancellationToken: cancel.Token);
    var deadline = Stopwatch.StartNew();
    bool reachedDecode = false;
    while (!work.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(15))
    {
        if (Directory.EnumerateFiles(cache, "frames.rgba", SearchOption.AllDirectories).Any())
        { reachedDecode = true; cancel.Cancel(); break; }
        await Task.Delay(2);
    }
    cancel.Cancel();
    await RejectAsync(async () => { using var unexpected = await work; }, "cancelled decode completed", "cancelled");
    Check(reachedDecode, "Cancellation test did not reach actual FFmpeg decode.");
    Check(Directory.EnumerateFileSystemEntries(cache).SequenceEqual(new[] { unrelated }) &&
        File.ReadAllText(unrelated) == "preserve", "Cancelled decode changed unowned files or left partial output.");
});

File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { Passed = passed, Failed = failed,
    Scope = "Real FFmpeg frame decoding and rational PTS selection; no Unity texture, native Mod or story certification.",
    Results = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Artifacts: " + root);
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

string Cache(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
static byte[] MakeFrame(int number)
{
    var frame = new byte[bytesPerFrame];
    byte[] rgb = number switch { 0 => new byte[] { 240, 10, 20 }, 1 => new byte[] { 10, 230, 30 },
        2 => new byte[] { 20, 30, 220 }, _ => new byte[] { 240, 230, 220 } };
    for (int i = 0; i < width * height; i++) { frame[i * 4] = (byte)(rgb[0] - i % 4); frame[i * 4 + 1] = rgb[1]; frame[i * 4 + 2] = (byte)(rgb[2] - i / width); frame[i * 4 + 3] = 255; }
    return frame;
}
static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
async Task Test(string name, Func<Task> body)
{
    try { await body(); passed++; results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; results.Add(new { name, passed = false, error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error); }
}
static async Task RejectAsync(Func<Task> body, string message, string? expectedCode = null)
{
    try { await body(); }
    catch (ExportException error) when (expectedCode == null || error.Code == expectedCode) { return; }
    catch (OperationCanceledException) when (expectedCode == null || expectedCode == "cancelled") { return; }
    throw new Exception(message);
}
static async Task Tool(string executable, IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
    catch { if (!process.HasExited) process.Kill(true); throw; }
    if (process.ExitCode != 0) throw new Exception("Fixture media tool failed: " + await stderr);
    await stdout; await stderr;
}
