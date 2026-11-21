using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.NativePath.Tests;

string tools = args.Length > 0 ? Path.GetFullPath(args[0]) : @"F:\AzureArchive_101\mods\AAVideoExport\0.2.4\tools";
string ffmpeg = Path.Combine(tools, "ffmpeg.exe");
string root = Path.Combine(AppContext.BaseDirectory, "artifacts", "decoded-media-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var layout = new DecodedMediaLayout(8, 4, 30, 48000, 2, 4, 6400, "bgra");
byte[] video = new byte[8 * 4 * 4 * 4];
for (int frame = 0; frame < 4; frame++)
    for (int pixel = 0; pixel < 32; pixel++)
    {
        int at = (frame * 32 + pixel) * 4;
        video[at] = (byte)(frame * 40 + pixel); video[at + 1] = (byte)(pixel * 5); video[at + 2] = (byte)(255 - frame * 30); video[at + 3] = 255;
    }
byte[] pcm = new byte[6400 * 2 * 2];
for (int i = 0; i < 6400 * 2; i++)
{
    short sample = (short)(i % 301 - 150); pcm[i * 2] = (byte)sample; pcm[i * 2 + 1] = (byte)(sample >> 8);
}
string baseFile = await Encode("base", video, pcm);
var changedVideo = video.ToArray(); changedVideo[2 * 8 * 4 * 4 + 4] ^= 1;
string videoFile = await Encode("one-pixel", changedVideo, pcm);
var changedPcm = pcm.ToArray(); changedPcm[6000] ^= 1;
string audioFile = await Encode("one-sample", video, changedPcm);
string remuxFile = Path.Combine(root, "remux.mkv");
await Tool(new[] { "-v", "error", "-nostdin", "-n", "-i", baseFile, "-map", "0", "-c", "copy", "-metadata", "title=independent-remux", remuxFile });
string shiftedFile = Path.Combine(root, "shifted.mkv");
await Tool(new[] { "-v", "error", "-nostdin", "-n", "-itsoffset", "1", "-i", baseFile, "-map", "0", "-c", "copy", shiftedFile });
int passed = 0, failed = 0; var results = new List<object>();
DecodedMediaFingerprintResult? baseline = null;
async Task Test(string name, Func<Task> run)
{
    try { await run(); passed++; results.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; results.Add(new { Name = name, Passed = false, Error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
Task<DecodedMediaFingerprintResult> Capture(string media, string name, DecodedMediaLayout? expected = null) =>
    DecodedMediaFingerprint.CaptureAsync(ffmpeg, media, expected ?? layout, Path.Combine(root, name), TimeSpan.FromSeconds(30));
await Test("actual decoded hashes match independently authored pixels and every PCM16 byte", async () =>
{
    baseline = await Capture(baseFile, "base-evidence");
    // The lossless fixture explicitly encodes BGRA. Hash its independently
    // authored native plane order; do not normalize it through RGBA8.
    byte[] nativePixels = video.ToArray();
    for (int offset = 0; offset < nativePixels.Length; offset += 4)
        (nativePixels[offset], nativePixels[offset + 2]) = (nativePixels[offset + 2], nativePixels[offset]);
    byte[] perFrame = Enumerable.Range(0, 4).SelectMany(frame => SHA256.HashData(nativePixels.AsSpan(frame * 128, 128))).ToArray();
    Check(baseline.VideoSha256 == Convert.ToHexString(SHA256.HashData(perFrame)), "decoded video hashes do not match source pixels");
    Check(baseline.AudioPcm16Sha256 == Convert.ToHexString(SHA256.HashData(pcm)), "decoded PCM hash does not match authored samples");
    Check(baseline.VideoFrameBytes == 128, "native decoded frame size changed");
    Check(File.ReadLines(baseline.FramesPath).Count() == 5, "readable frame manifest omitted or added records");
    Check(File.Exists(baseline.FrameHashPath) && File.Exists(baseline.AudioEvidencePath) && File.Exists(baseline.SummaryPath), "decode evidence is missing");
});
await Test("equal-length media with one changed decoded video pixel fails repeat comparison", async () =>
{
    var changed = await Capture(videoFile, "pixel-evidence");
    Check(changed.Layout == baseline!.Layout, "fixture did not preserve frame/sample lengths");
    Reject(() => DecodedMediaFingerprint.RequireEquivalent(baseline!, changed), "video", "zero-based frame 2");
});
await Test("equal-length media with one changed PCM16 value fails repeat comparison", async () =>
{
    var changed = await Capture(audioFile, "sample-evidence");
    Check(changed.Layout == baseline!.Layout, "fixture did not preserve frame/sample lengths");
    Reject(() => DecodedMediaFingerprint.RequireEquivalent(baseline!, changed), "PCM");
});
await Test("font-atlas path still requires exact decoded PCM and timestamps", async () =>
{
    var changed = await Capture(audioFile, "font-audio-negative");
    Reject(() => DecodedMediaFingerprint.RequireSameTimingAndAudio(baseline!, changed), "PCM");
    Reject(() => DecodedMediaFingerprint.RequireSameTimingAndAudio(baseline!, baseline! with { VideoTimelineSha256 = new string('0', 64) }), "PTS");
});
await Test("identical original RGBA cannot authorize changed decoded pixels", () =>
{
    Reject(() => DecodedMediaFingerprint.RequireOriginalPixelsToDecodedConsistency(true, false), "encoder");
    DecodedMediaFingerprint.RequireOriginalPixelsToDecodedConsistency(true, true);
    return Task.CompletedTask;
});
await Test("lossless remux with different container bytes preserves decoded video and PCM identity", async () =>
{
    Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(baseFile))) != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(remuxFile))), "remux did not change container bytes");
    var remux = await Capture(remuxFile, "remux-evidence");
    DecodedMediaFingerprint.RequireEquivalent(baseline!, remux);
});
await Test("native H264 YUV420P planes and MOV PCM survive independent remux comparison", async () =>
{
    string movie = Path.Combine(root, "h264.mov"), copy = Path.Combine(root, "h264-remux.mov");
    await Tool(new[] { "-v", "error", "-nostdin", "-n", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "8x4", "-framerate", "30", "-i", Path.Combine(root, "base.rgba"),
        "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", Path.Combine(root, "base.pcm"), "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "pcm_s16le", movie });
    await Tool(new[] { "-v", "error", "-nostdin", "-n", "-i", movie, "-map", "0", "-c", "copy", "-metadata", "title=remuxed-h264", copy });
    var expected = layout with { VideoPixelFormat = "yuv420p" };
    var first = await Capture(movie, "h264-evidence", expected);
    var second = await Capture(copy, "h264-remux-evidence", expected);
    Check(first.VideoFrameBytes == 48 && first.AudioPcm16Sha256 == baseline!.AudioPcm16Sha256, "native YUV planes or MOV audio sample bytes changed");
    DecodedMediaFingerprint.RequireEquivalent(first, second);
});
await Test("decoded video record count must equal the independent expected count", async () =>
{
    await RejectAsync(() => Capture(baseFile, "wrong-frames", layout with { Frames = 5 }), "count");
});
await Test("decoded PCM sample count must equal the independent expected count", async () =>
{
    await RejectAsync(() => Capture(baseFile, "wrong-samples", layout with { AudioSampleFrames = 6401 }), "count");
});
await Test("decoded frame evidence preserves and rejects a nonzero source presentation start", async () =>
{
    await RejectAsync(() => Capture(shiftedFile, "shifted-evidence"), "PTS");
});
await Test("cancelled decode cannot publish completed fingerprint evidence", async () =>
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await DecodedMediaFingerprint.CaptureAsync(ffmpeg, baseFile, layout, Path.Combine(root, "cancelled"), TimeSpan.FromSeconds(30), cancellation.Token); throw new Exception("cancelled decode succeeded"); }
    catch (OperationCanceledException) { Check(!File.Exists(Path.Combine(root, "cancelled.fingerprint.json")), "cancelled decode published a success summary"); }
});
await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { Passed = passed, Failed = failed, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Artifacts: " + root); Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

async Task<string> Encode(string name, byte[] pixels, byte[] samples)
{
    string raw = Path.Combine(root, name + ".rgba"), audio = Path.Combine(root, name + ".pcm"), output = Path.Combine(root, name + ".mkv");
    await File.WriteAllBytesAsync(raw, pixels); await File.WriteAllBytesAsync(audio, samples);
    await Tool(new[] { "-v", "error", "-nostdin", "-n", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "8x4", "-framerate", "30", "-i", raw,
        "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", audio, "-map", "0:v:0", "-map", "1:a:0", "-c:v", "ffv1", "-level", "3", "-pix_fmt", "bgra", "-c:a", "pcm_s16le", output });
    return output;
}
async Task Tool(IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!; var errors = process.StandardError.ReadToEndAsync();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
    catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
    string text = await errors; Check(process.ExitCode == 0 && string.IsNullOrWhiteSpace(text), "fixture FFmpeg failed: " + text);
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Reject(Action action, string diagnostic, params string[] details)
{
    try { action(); } catch (InvalidDataException error) when (error.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase)
        && details.All(detail => error.Message.Contains(detail, StringComparison.Ordinal))) { return; }
    throw new Exception("same-length changed " + diagnostic + " content was accepted");
}
static async Task RejectAsync(Func<Task<DecodedMediaFingerprintResult>> action, string diagnostic)
{
    try { await action(); } catch (InvalidDataException error) when (error.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new Exception("invalid decoded " + diagnostic + " was accepted");
}
