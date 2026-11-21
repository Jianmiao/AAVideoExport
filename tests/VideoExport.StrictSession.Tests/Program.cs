using System.Globalization;
using System.Runtime.InteropServices;
using AAVideoExport.Core;

if (args.Length != 1) { Console.Error.WriteLine("Pass the directory containing ffmpeg.exe and ffprobe.exe."); return 2; }
string ffmpeg = Path.Combine(args[0], "ffmpeg.exe"), ffprobe = Path.Combine(args[0], "ffprobe.exe");
string root = Path.Combine(AppContext.BaseDirectory, "strict-session-media", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0, failed = 0, serial = 0;
void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
ExportOptions Options(int fps, string quality = "pcm24", int outputRate = 48000) => new()
{
    Title = "strict-" + ++serial, OutputDirectory = root, Width = 96, Height = 64, Fps = fps,
    Encoder = "libx264", EncodingMode = "software", Codec = "h264", Container = "mp4",
    StrictSynchronization = true, AudioQuality = quality, AudioSampleRate = outputRate, WriteCover = false,
    BitrateKbps = 500
};
float[] Pulses(int count)
{
    var samples = new float[count * 2];
    foreach (int p in new[] { 0, 1, 17, 799, 800, 1599, 1600, 1999, 2000, count / 2, count - 2, count - 1 }.Where(i => i >= 0 && i < count).Distinct())
    { samples[p * 2] = 0.5f; samples[p * 2 + 1] = -0.25f; }
    return samples;
}
void Append(PreparedAudioTrack track, float[] samples, bool split)
{
    track.Append(Array.Empty<float>(), 0);
    if (!split) { track.Append(samples, samples.Length); return; }
    int[] sizes = { 1, 7, 2048, 0, 511, 8192, 31 }; int at = 0, block = 0;
    while (at < samples.Length)
    {
        int floats = Math.Min(sizes[block++ % sizes.Length] * 2, samples.Length - at);
        float[] copy = samples.AsSpan(at, floats).ToArray(); track.Append(copy, copy.Length); at += floats;
    }
}
async Task<float[]> Decode(string path)
{
    string decoded = path + ".decoded.f32";
    (await FfmpegProcess.RunAsync(ffmpeg, new[] { "-v", "error", "-nostdin", "-n", "-i", path, "-map", "0:a:0", "-f", "f32le", "-acodec", "pcm_f32le", decoded }, TimeSpan.FromSeconds(30), CancellationToken.None)).EnsureSuccess("decode fixture audio");
    return MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(decoded)).ToArray();
}
async Task<(ExportResult Result, float[] Source, string PreparedHash)> Export(int fps, int frames, bool split, string quality = "pcm24", int outputRate = 48000)
{
    var options = Options(fps, quality, outputRate);
    using var session = new ExportSession(options, ffmpeg, ffprobe, "libx264", 48000, 2, false);
    using var prepared = session.CreateAudioPreparationTrack();
    float[] source = Pulses(checked((int)(frames * 48000L / fps)));
    Append(prepared, source, split); prepared.Seal(frames);
    string hash = prepared.SamplesSha256;
    session.BindStrictTimeline(new StrictFramePlan("source-fixture-" + fps + "-" + frames, frames, fps, 48000, 2));
    byte[] rgba = new byte[96 * 64 * 4]; for (int p = 3; p < rgba.Length; p += 4) rgba[p] = 255;
    for (int frame = 0; frame < frames; frame++)
    {
        rgba[0] = (byte)(frame * 7);
        session.WriteFrame(rgba, rgba.Length);
        var audio = prepared.ReadFrame(frame); session.WriteAudioFrame(frame, audio.Samples, audio.Samples.Length);
    }
    prepared.CompleteReplay(); prepared.Dispose();
    var result = session.Complete();
    Check(result.Frames == frames && result.DurationSeconds == frames / (double)fps, "final frame contract changed");
    Check(File.Exists(result.OutputPath), "verified output was not committed");
    await MediaVerifier.VerifyAsync(ffprobe, result.OutputPath, options, frames, TimeSpan.FromSeconds(30), CancellationToken.None);
    return (result, source, hash);
}

foreach (int fps in new[] { 24, 30, 60 })
    await Test($"strict session {fps}fps split vs merged preparation preserves nonzero PCM sample positions", async () =>
    {
        var split = await Export(fps, 13, true); var merged = await Export(fps, 13, false);
        Check(split.PreparedHash == merged.PreparedHash, "block segmentation changed the sealed PCM");
        float[] first = await Decode(split.Result.OutputPath), second = await Decode(merged.Result.OutputPath);
        Check(first.Length == split.Source.Length && second.Length == split.Source.Length, "decoded PCM sample count changed");
        Check(first.Any(value => value != 0), "fixture audio became silent");
        for (int i = 0; i < first.Length; i++)
            Check(Math.Abs(first[i] - split.Source[i]) <= 1f / 8388608 && first[i] == second[i], "PCM pulse shifted/changed at interleaved sample " + i);
    });
foreach (int delta in new[] { -1, 1 })
    await Test($"prepared PCM {delta:+#;-#} sample cannot start video or publish output", () =>
    {
        var options = Options(24); using var session = new ExportSession(options, ffmpeg, ffprobe, "libx264", 48000, 2, false);
        using var prepared = session.CreateAudioPreparationTrack(); var source = Pulses(26000 + delta); Append(prepared, source, true);
        try { prepared.Seal(13); throw new Exception("preparation mismatch was accepted"); }
        catch (ExportException ex) { Check(ex.Code == (delta < 0 ? "audio_timeline_incomplete" : "audio_timeline_extra"), ex.Message); }
        Check(session.FramesAccepted == 0 && !File.Exists(session.OutputPath), "unsealed audio wrote output video");
        return Task.CompletedTask;
    });
foreach (int delta in new[] { -1, 1 })
    await Test($"strict session aggregate PCM {delta:+#;-#} sample cannot publish output", () =>
    {
        var options = Options(24); using var session = new ExportSession(options, ffmpeg, ffprobe, "libx264", 48000, 2, false);
        session.BindStrictTimeline(new StrictFramePlan("invalid-final-fixture", 13, 24, 48000, 2));
        var source = Pulses(26000 + delta); session.WriteAudio(source, source.Length);
        var rgba = new byte[96 * 64 * 4]; for (int i = 0; i < 13; i++) session.WriteFrame(rgba, rgba.Length);
        try { session.Complete(); throw new Exception("aggregate mismatch accepted"); }
        catch (ExportException ex) { Check(ex.Code == "audio_timeline_mismatch", ex.Message); }
        Check(!File.Exists(session.OutputPath), "mismatched export was committed"); return Task.CompletedTask;
    });
await Test("strict session AAC 13 frames at24fps preserves non-millisecond endpoint", async () =>
{
    var media = await Export(24, 13, true, "aac192");
    var decoded = await Decode(media.Result.OutputPath);
    Check(decoded.Length / 2 >= 26000 && decoded.Length / 2 < 26000 + 1024, "unexpected AAC codec padding");
    Check(decoded.Any(value => Math.Abs(value) > 0.05f), "AAC pulses disappeared");
});
foreach (int fps in new[] { 24, 30, 60 })
foreach (string quality in new[] { "pcm24", "aac192" })
    await Test($"strict session 48k to44.1k {quality} resampling preserves floor endpoint at {fps}fps", async () =>
    {
        var media = await Export(fps, 13, true, quality, 44100);
        var decoded = await Decode(media.Result.OutputPath);
        long expected = 13L * 44100 / fps;
        if (quality == "pcm24") Check(decoded.Length / 2 == expected, "resampled PCM does not end at expected floor sample boundary");
        else Check(decoded.Length / 2 >= expected && decoded.Length / 2 < expected + 1024, "unexpected resampled AAC codec padding");
        Check(decoded.Any(value => Math.Abs(value) > 0.05f), "resampled pulses disappeared");
    });

foreach (string outcome in new[] { "commit", "receipt-failure", "scope-refusal", "fault-during-receipt" })
    await Test("strict publication holds its validation scope through " + outcome, () =>
    {
        var options = Options(30);
        using var session = new ExportSession(options, ffmpeg, ffprobe, "libx264", 48000, 2, false);
        session.BindStrictTimeline(new StrictFramePlan("commit-scope-fixture", 1, 30, 48000, 2));
        var rgba = new byte[96 * 64 * 4];
        session.WriteFrame(rgba, rgba.Length);
        session.WriteAudioFrame(0, Pulses(1600), 3200);
        bool held = false, receiptEntered = false, disposed = false, invalidated = false;
        int acquisitions = 0;
        Func<IDisposable?> acquire = () =>
        {
            acquisitions++;
            Check(receiptEntered, "potentially slow receipt work ran inside the publication scope");
            if (outcome == "scope-refusal" || invalidated) throw new ExportException("fixture_boundary_invalid", "scope rejected");
            held = true;
            return new CommitTestScope(() =>
            {
                Check(held && receiptEntered, "scope was released before receipt handling");
                Check(File.Exists(session.OutputPath) == (outcome == "commit"), "scope ended before the expected publication decision");
                held = false; disposed = true;
            });
        };
        var property = typeof(ExportSession).GetProperty("AcquireOutputCommitScope");
        Check(property != null, "ExportSession has no publication validation scope");
        property!.SetValue(session, acquire);
        session.BeforeOutputCommit = () =>
        {
            Check(!held && !File.Exists(session.OutputPath), "receipt flush held the publication lock or ran after publication");
            receiptEntered = true;
            if (outcome == "receipt-failure") throw new ExportException("fixture_receipt_failed", "receipt failed");
            if (outcome == "fault-during-receipt") invalidated = true;
        };
        string failure = "";
        try { session.Complete(); }
        catch (ExportException error) { failure = error.Code; }
        Check(acquisitions == (outcome == "receipt-failure" ? 0 : 1), "publication acquired its scope at the wrong point");
        if (outcome == "commit") Check(failure.Length == 0 && disposed && !held, "normal output did not release its scope");
        if (outcome == "receipt-failure") Check(failure == "fixture_receipt_failed" && !disposed && !held, "failed receipt acquired or leaked the scope");
        if (outcome is "scope-refusal" or "fault-during-receipt") Check(failure == "fixture_boundary_invalid" && receiptEntered && !disposed, "invalid boundary reached publication");
        Check(File.Exists(session.OutputPath) == (outcome == "commit"), "incorrect final output publication");
        return Task.CompletedTask;
    });

foreach (string outcome in new[] { "commit", "scope-refusal", "existing-user-sidecar" })
    await Test("runtime recipe terminal sidecar follows guarded media publication: " + outcome, () =>
    {
        var property = typeof(ExportSession).GetProperty("RuntimeFactoryRecipe");
        Check(property != null, "ExportSession has no terminal recipe receipt integration");
        var options = Options(30);
        using var session = new ExportSession(options, ffmpeg, ffprobe, "libx264", 48000, 2, false);
        session.BindStrictTimeline(new StrictFramePlan("recipe-terminal-fixture", 1, 30, 48000, 2));
        var rgba = new byte[96 * 64 * 4]; session.WriteFrame(rgba, rgba.Length); session.WriteAudioFrame(0, Pulses(1600), 3200);
        property!.SetValue(session, new RuntimeFactoryRecipeReceipt(new string('a', 64), new string('b', 64)));
        string receipt = session.OutputPath + ".runtime-recipes.json";
        if (outcome == "existing-user-sidecar") File.WriteAllText(receipt, "user-owned");
        session.AcquireOutputCommitScope = () =>
        {
            Check(File.Exists(receipt) == (outcome == "existing-user-sidecar"), "sidecar published before entering final guard");
            if (outcome == "scope-refusal") throw new ExportException("fixture_recipe_refused", "recipe guard refused");
            return new CommitTestScope(() => Check(File.Exists(session.OutputPath) == (outcome == "commit"), "guard released before publication decision"));
        };
        string code = ""; try { session.Complete(); } catch (ExportException error) { code = error.Code; }
        if (outcome == "commit")
        {
            var verified = RuntimeFactoryRecipeReceipt.VerifyPublished(receipt);
            Check(code.Length == 0 && verified.RecipesSha256 == new string('b', 64), "successful movie lacks its associated terminal proof");
        }
        else
        {
            Check(!File.Exists(session.OutputPath), "refused movie was published");
            if (outcome == "scope-refusal") Check(code == "fixture_recipe_refused" && !File.Exists(receipt), "refused scope left a public receipt");
            else Check(code == "output_exists" && File.ReadAllText(receipt) == "user-owned", "existing user receipt was changed or removed");
        }
        return Task.CompletedTask;
    });

Console.WriteLine("Artifacts: " + root);
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

sealed class CommitTestScope : IDisposable
{
    private readonly Action release;
    private bool disposed;
    public CommitTestScope(Action release) => this.release = release;
    public void Dispose() { if (disposed) return; release(); disposed = true; }
}
