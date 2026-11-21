using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using AAVideoExport.Core;

if (args.Length != 1) { Console.Error.WriteLine("Pass the directory containing ffmpeg.exe and ffprobe.exe."); return 2; }
string ffmpeg = Path.Combine(args[0], "ffmpeg.exe"), ffprobe = Path.Combine(args[0], "ffprobe.exe");
string root = Path.Combine(AppContext.BaseDirectory, "live-session-media", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0, failed = 0, serial = 0;
void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
void Reject(string code, Action action)
{
    try { action(); } catch (ExportException error) { Check(error.Code == code, $"Expected {code}; got {error.Code}: {error.Message}"); return; }
    throw new Exception("Expected " + code + " but operation succeeded.");
}
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
}
ExportOptions Options(int fps = 30, string quality = "pcm24", int outputRate = 48000) => new()
{
    Title = "live-" + ++serial, OutputDirectory = root, Width = 64, Height = 64, Fps = fps,
    Encoder = "qtrle", EncodingMode = "software", Codec = "qtrle", Container = "mov",
    StrictSynchronization = true, AudioQuality = quality, AudioSampleRate = outputRate, WriteCover = false
};
ExportSession Session(ExportOptions? options = null) => new(options ?? Options(), ffmpeg, ffprobe, "qtrle", 48000, 2, false);
byte[] Frame(int index = 0)
{
    var rgba = new byte[64 * 64 * 4];
    for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = (byte)(index * 17); rgba[i + 1] = (byte)(i / 4); rgba[i + 2] = (byte)(255 - index); rgba[i + 3] = 255; }
    return rgba;
}
void NativeFrame(ExportSession session, byte[] pixels)
{
    IntPtr pointer = Marshal.AllocHGlobal(pixels.Length);
    try { Marshal.Copy(pixels, 0, pointer, pixels.Length); session.WriteFrame(pointer, pixels.Length); }
    finally { Marshal.FreeHGlobal(pointer); }
}
StrictFramePlan Plan(long frames, int fps = 30) => StrictFramePlan.CreateExecution(frames, fps, 48000, 2);
void Audio(ExportSession session, int sampleFrames) { var samples = new float[sampleFrames * 2]; session.WriteAudio(samples, samples.Length); }
void NoPublication(ExportSession session) => Check(!File.Exists(session.OutputPath), "Invalid session published a movie.");
void Cleaned(ExportSession session)
{
    NoPublication(session);
    Check(!Directory.EnumerateDirectories(root, ".aa-video-export-*").Any(), "Session left private export files behind.");
}
async Task<byte[]> Decode(string path, bool audio)
{
    string output = path + (audio ? ".f32le" : ".rgba");
    var arguments = new List<string> { "-v", "error", "-nostdin", "-n", "-i", path, "-map", audio ? "0:a:0" : "0:v:0" };
    arguments.AddRange(audio ? new[] { "-f", "f32le", "-acodec", "pcm_f32le" } : new[] { "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "rgba" });
    arguments.Add(output);
    (await FfmpegProcess.RunAsync(ffmpeg, arguments, TimeSpan.FromSeconds(30), CancellationToken.None)).EnsureSuccess("decode live fixture");
    return File.ReadAllBytes(output);
}
DelayedProcessor AttachDelayedQueue(ExportSession session)
{
    // Only the hardware readback boundary is replaced. The real bounded queue,
    // session, pipe, FFmpeg encoder, mux and completion path still execute.
    var processor = new DelayedProcessor();
    var sink = (Action<byte[]>)typeof(ExportSession).GetMethod("WriteProcessedFrame", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate(typeof(Action<byte[]>), session);
    var writer = new QueuedVideoFrameWriter(processor, sink);
    typeof(ExportSession).GetField("queuedVideoWriter", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, writer);
    return processor;
}

foreach (string kind in new[] { "managed video", "native video", "contiguous audio", "sliced audio", "frame audio" })
    await Test("strict output requires timeline initialization: " + kind, () =>
    {
        using var session = Session();
        var pixels = Frame();
        Reject("invalid_state", () =>
        {
            if (kind == "managed video") session.WriteFrame(pixels, pixels.Length);
            else if (kind == "native video") NativeFrame(session, pixels);
            else if (kind == "contiguous audio") Audio(session, 1600);
            else if (kind == "sliced audio") LiveApi.WriteAudio(session, new float[3200], 0, 3200);
            else session.WriteAudioFrame(0, new float[3200], 3200);
        });
        Check(session.FramesAccepted == 0 && session.AudioSampleFrames == 0, "Rejected output altered the session clock.");
        return Task.CompletedTask;
    });

await Test("live and prebound timeline modes cannot be mixed or initialized twice", () =>
{
    using (var session = Session())
    {
        Reject("invalid_state", () => LiveApi.Seal(session, Plan(1)));
        LiveApi.Begin(session);
        Reject("invalid_state", () => LiveApi.Begin(session));
        Reject("invalid_state", () => session.BindStrictTimeline(Plan(1)));
        LiveApi.Seal(session, Plan(1));
        Reject("invalid_state", () => LiveApi.Seal(session, Plan(1)));
    }
    using (var session = Session()) { session.BindStrictTimeline(Plan(1)); Reject("invalid_state", () => LiveApi.Begin(session)); }
    using (var session = Session(Options() with { StrictSynchronization = false })) Reject("invalid_state", () => LiveApi.Begin(session));
    return Task.CompletedTask;
});

await Test("first-pass video and audio can precede final length but unsealed completion cannot publish", () =>
{
    var session = Session();
    using (session)
    {
        LiveApi.Begin(session);
        var pixels = Frame(); session.WriteFrame(pixels, pixels.Length); NativeFrame(session, Frame(1)); Audio(session, 3200);
        Check(session.FramesAccepted == 2 && session.FramesWritten == 2 && session.AudioSampleFrames == 3200, "First-pass output was not consumed.");
        Reject("invalid_state", () => session.Complete());
        NoPublication(session);
    }
    Cleaned(session); return Task.CompletedTask;
});

await Test("seal accepts pending external readbacks and bounds later managed and native frames", async () =>
{
    using var session = Session(); LiveApi.Begin(session);
    var first = Frame(0); session.WriteFrame(first, first.Length);
    LiveApi.Seal(session, Plan(3));
    NativeFrame(session, Frame(1)); var last = Frame(2); session.WriteFrame(last, last.Length);
    Reject("video_timeline_mismatch", () => session.WriteFrame(last, last.Length));
    Reject("video_timeline_mismatch", () => NativeFrame(session, last));
    Check(session.FramesAccepted == 3, "Rejected extra frame advanced the accepted count.");
    Audio(session, 4800);
    var result = session.Complete();
    Check(result.Frames == 3, "Sealed export changed its frame count.");
    var pixels = await Decode(result.OutputPath, false);
    Check(pixels.SequenceEqual(Frame(0).Concat(Frame(1)).Concat(Frame(2))), "Deferred video order or pixels changed.");
});

await Test("seal permits bounded GPU outputs still pending and Complete drains exactly once", async () =>
{
    using var session = Session(); var processor = AttachDelayedQueue(session); LiveApi.Begin(session);
    for (int frame = 0; frame < 3; frame++) { var pixels = Frame(frame); session.WriteFrame(pixels, pixels.Length); Array.Fill(pixels, (byte)0); }
    Check(session.FramesAccepted == 3 && session.FramesWritten == 0, "Fixture did not leave GPU outputs pending.");
    LiveApi.Seal(session, Plan(3)); Audio(session, 4800);
    var result = session.Complete();
    Check(session.FramesWritten == 3 && processor.PendingFrames == 0, "Natural completion did not drain every pending frame.");
    var decoded = await Decode(result.OutputPath, false);
    Check(decoded.SequenceEqual(Frame(0).Concat(Frame(1)).Concat(Frame(2))), "GPU queue changed captured content.");
});

await Test("live GPU queue writes before sealing and rejects overrun at acceptance", async () =>
{
    using var session = Session(); AttachDelayedQueue(session); LiveApi.Begin(session);
    for (int frame = 0; frame < 5; frame++) { var pixels = Frame(frame); session.WriteFrame(pixels, pixels.Length); }
    Check(session.FramesAccepted == 5 && session.FramesWritten == 2, "Unsealed live GPU queue did not emit its first-pass frames.");
    LiveApi.Seal(session, Plan(5));
    var extra = Frame(5); Reject("video_timeline_mismatch", () => session.WriteFrame(extra, extra.Length));
    Audio(session, 8000); var result = session.Complete();
    var decoded = await Decode(result.OutputPath, false);
    Check(decoded.SequenceEqual(Enumerable.Range(0, 5).SelectMany(Frame)), "Queue acceptance boundary lost or duplicated frames.");
});

await Test("seal rejects a shorter plan or changed capture clocks without consuming the valid seal", () =>
{
    using var session = Session(); LiveApi.Begin(session);
    var pixels = Frame(); session.WriteFrame(pixels, pixels.Length); session.WriteFrame(pixels, pixels.Length);
    Reject("video_timeline_mismatch", () => LiveApi.Seal(session, Plan(1)));
    Reject("timeline_source_changed", () => LiveApi.Seal(session, StrictFramePlan.CreateExecution(2, 24, 48000, 2)));
    Reject("timeline_source_changed", () => LiveApi.Seal(session, StrictFramePlan.CreateExecution(2, 30, 44100, 2)));
    Reject("timeline_source_changed", () => LiveApi.Seal(session, StrictFramePlan.CreateExecution(2, 30, 48000, 1)));
    LiveApi.Seal(session, Plan(2)); Audio(session, 3200);
    Check(session.Complete().Frames == 2, "Invalid seal consumed or changed the final contract.");
    return Task.CompletedTask;
});

await Test("sealed completion rejects missing video instead of padding or publishing", () =>
{
    var session = Session(); using (session)
    {
        LiveApi.Begin(session); LiveApi.Seal(session, Plan(2));
        var pixels = Frame(); session.WriteFrame(pixels, pixels.Length); Audio(session, 3200);
        Reject("video_timeline_mismatch", () => session.Complete());
    }
    Cleaned(session); return Task.CompletedTask;
});

foreach (int delta in new[] { -1, 1 })
    await Test($"sealed completion rejects {delta:+#;-#} PCM sample frames without padding", () =>
    {
        var session = Session(); using (session)
        {
            LiveApi.Begin(session); LiveApi.Seal(session, Plan(1));
            var pixels = Frame(); session.WriteFrame(pixels, pixels.Length); Audio(session, 1600 + delta);
            Reject("audio_timeline_mismatch", () => session.Complete());
        }
        Cleaned(session); return Task.CompletedTask;
    });

await Test("offset audio validates only its complete borrowed slice and consumes it before return", async () =>
{
    using var session = Session(); LiveApi.Begin(session);
    var buffer = new float[3204]; buffer[0] = float.NaN; buffer[1] = float.PositiveInfinity; buffer[^2] = float.NegativeInfinity; buffer[^1] = float.NaN;
    buffer[2] = .5f; buffer[3] = -.25f; buffer[^4] = .125f; buffer[^3] = -.5f;
    var expected = buffer.AsSpan(2, 3200).ToArray();
    foreach (var (offset, length) in new[] { (-1, 2), (0, -2), (3205, 0), (2, int.MaxValue), (1, 3200), (2, 3), (0, 2) })
        Reject("invalid_audio", () => LiveApi.WriteAudio(session, buffer, offset, length));
    Reject("invalid_audio", () => LiveApi.WriteAudio(session, null!, 0, 0));
    Check(session.AudioSampleFrames == 0, "Rejected slice changed the audio count.");
    LiveApi.WriteAudio(session, buffer, 2, 3200); LiveApi.WriteAudio(session, buffer, buffer.Length, 0);
    Array.Fill(buffer, 0f);
    var pixels = Frame(); session.WriteFrame(pixels, pixels.Length); LiveApi.Seal(session, Plan(1));
    float[] decoded = MemoryMarshal.Cast<byte, float>(await Decode(session.Complete().OutputPath, true)).ToArray();
    Check(decoded.SequenceEqual(expected), "Borrowed PCM slice was truncated, shifted, or retained after return.");
});

await Test("offset audio does not allocate a staging array for every block", () =>
{
    using var session = Session(); LiveApi.Begin(session);
    var block = new float[32772];
    var write = LiveApi.AudioWriter(); write(session, block, 2, 32768);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 32; i++) write(session, block, 2, 32768);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(allocated < 32768, "Audio borrowed-slice writes allocated per-block staging storage: " + allocated);
    return Task.CompletedTask;
});

foreach (bool completionToken in new[] { false, true })
    await Test("live cancellation discards pending frames and private audio: " + (completionToken ? "completion token" : "explicit cancel"), () =>
    {
        var session = Session(); var processor = AttachDelayedQueue(session);
        using (session)
        {
            LiveApi.Begin(session); var pixels = Frame(); session.WriteFrame(pixels, pixels.Length); Audio(session, 1600);
            if (completionToken)
            {
                LiveApi.Seal(session, Plan(1)); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                Reject("cancelled", () => session.Complete(cancelled.Token));
            }
            else
            {
                session.Cancel(); Reject("cancelled", () => LiveApi.Seal(session, Plan(1)));
                Reject("cancelled", () => session.WriteFrame(pixels, pixels.Length));
                Reject("cancelled", () => LiveApi.WriteAudio(session, new float[2], 0, 2));
                Reject("cancelled", () => session.Complete());
            }
            Check(processor.PendingFrames == 1 && session.FramesWritten == 0, "Cancellation drained pending GPU content.");
        }
        Cleaned(session); return Task.CompletedTask;
    });

foreach (int fps in new[] { 24, 30, 60 })
    await Test($"real {fps} fps live mux preserves one-pass video and exact trimmed nonzero raw PCM", async () =>
    {
        var options = Options(fps); using var session = Session(options); LiveApi.Begin(session);
        const int frames = 13, lead = 3, tail = 5;
        int contentFrames = fps == 24 ? 26000 : fps == 30 ? 20800 : 10400;
        var raw = new float[(lead + contentFrames + tail) * 2];
        Array.Fill(raw, .75f, 0, lead * 2); Array.Fill(raw, -.75f, (lead + contentFrames) * 2, tail * 2);
        foreach (int p in new[] { 0, 1, 17, 799, 800, 8191, 8192, contentFrames - 2, contentFrames - 1 })
        { raw[(lead + p) * 2] = .5f; raw[(lead + p) * 2 + 1] = -.25f; }
        var expected = raw.AsSpan(lead * 2, contentFrames * 2).ToArray();
        using var pass = new RawAudioPass(Path.Combine(root, options.Title + ".raw"), 48000, 2, 42, lead);
        pass.Append(0, raw, raw.Length);
        for (int frame = 0; frame < frames; frame++) { var pixels = Frame(frame); session.WriteFrame(pixels, pixels.Length); }
        pass.Seal(frames, fps);
        pass.CopyContentTo(slice => LiveApi.WriteAudio(session, slice.Array!, slice.Offset, slice.Count));
        LiveApi.Seal(session, Plan(frames, fps));
        session.BeforeOutputCommit = () => throw new Exception("Live production entered audit callback.");
        session.AcquireOutputCommitScope = () => throw new Exception("Live production entered audit scope.");
        session.RuntimeFactoryRecipe = new RuntimeFactoryRecipeReceipt(new string('a', 64), new string('b', 64));
        var result = session.Complete();
        Check(result.Frames == frames && result.DurationSeconds == frames / (double)fps, "Live timing changed.");
        Check(result.RuntimeRecipeReceiptPath == null && !File.Exists(result.OutputPath + ".runtime-recipes.json"), "Live production emitted audit proof.");
        await MediaVerifier.VerifyAsync(ffprobe, result.OutputPath, options, frames, TimeSpan.FromSeconds(30), CancellationToken.None);
        float[] audio = MemoryMarshal.Cast<byte, float>(await Decode(result.OutputPath, true)).ToArray();
        Check(audio.SequenceEqual(expected), "Raw content PCM positions changed or lead/tail leaked.");
        byte[] video = await Decode(result.OutputPath, false);
        Check(video.SequenceEqual(Enumerable.Range(0, frames).SelectMany(Frame)), "First-pass video frames changed or were duplicated.");
    });

await Test("live 48k to 44.1k resampling uses the exact floor sample endpoint", async () =>
{
    var options = Options(24, outputRate: 44100); using var session = Session(options); LiveApi.Begin(session);
    for (int i = 0; i < 13; i++) { var pixels = Frame(i); session.WriteFrame(pixels, pixels.Length); }
    var samples = new float[52000]; samples[0] = .5f; samples[^2] = -.25f; session.WriteAudio(samples, samples.Length);
    LiveApi.Seal(session, Plan(13, 24)); var result = session.Complete();
    await MediaVerifier.VerifyAsync(ffprobe, result.OutputPath, options, 13, TimeSpan.FromSeconds(30), CancellationToken.None);
    float[] audio = MemoryMarshal.Cast<byte, float>(await Decode(result.OutputPath, true)).ToArray();
    Check(audio.Length == 47774 && audio.Any(x => Math.Abs(x) > .1f), "Resampled PCM endpoint or real samples changed.");
});

Console.WriteLine("Artifacts: " + root);
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

static class LiveApi
{
    // Reflection lets the initial test run prove the missing public behavior
    // at runtime before the new contract has been implemented.
    public static void Begin(ExportSession session) => Method<Action<ExportSession>>("BeginLiveTimeline", Type.EmptyTypes)(session);
    public static void Seal(ExportSession session, StrictFramePlan plan) => Method<Action<ExportSession, StrictFramePlan>>("SealLiveTimeline", new[] { typeof(StrictFramePlan) })(session, plan);
    public static void WriteAudio(ExportSession session, float[] samples, int offset, int length) => AudioWriter()(session, samples, offset, length);
    public static Action<ExportSession, float[], int, int> AudioWriter() => Method<Action<ExportSession, float[], int, int>>("WriteAudio", new[] { typeof(float[]), typeof(int), typeof(int) });
    private static T Method<T>(string name, Type[] parameters) where T : Delegate =>
        (T)(typeof(ExportSession).GetMethod(name, parameters) ?? throw new Exception("Missing live-session public contract: " + name)).CreateDelegate(typeof(T));
}

sealed class DelayedProcessor : IQueuedFrameProcessor
{
    private readonly Queue<byte[]> pending = new();
    public int Capacity => 4;
    public int PendingFrames => pending.Count;
    public void Submit(IntPtr pixels) { var copy = new byte[64 * 64 * 4]; Marshal.Copy(pixels, copy, 0, copy.Length); pending.Enqueue(copy); }
    public byte[] Receive(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return pending.Dequeue(); }
}
