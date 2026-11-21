using System.Runtime.InteropServices;
using AAVideoExport.Core;

internal static partial class Program
{
    private static void SessionTests()
    {
        foreach (int depth in new[] { 1, 4 })
            foreach (int count in new[] { 1, 2, 3, 4, 11 })
                foreach (bool native in new[] { false, true })
                    SessionTest($"session depth {depth}, {count} {(native ? "native" : "managed")} frames: audio clock, drain before close, exact counters", folder =>
                    {
                        var options = SessionOptions(folder, depth);
                        using var session = new ExportSession(options, "fixture", "fixture", "h264_nvenc");
                        Check(session.FramesAccepted == 0 && session.FramesWritten == 0, "preflight must not advance the story");
                        Check(session.UpscaleReadbackFrames == depth, "requested depth is active");
                        var canvas = options.GetCanvasLayout(); var pixels = new byte[canvas.CaptureWidth * canvas.CaptureHeight * 4];
                        for (int i = 1; i <= count; i++)
                        {
                            pixels[0] = (byte)i;
                            if (native)
                            {
                                var pointer = Marshal.AllocHGlobal(pixels.Length);
                                try { Marshal.Copy(pixels, 0, pointer, pixels.Length); session.WriteFrame(pointer, pixels.Length); }
                                finally { Marshal.FreeHGlobal(pointer); }
                            }
                            else session.WriteFrame(pixels, pixels.Length);
                            pixels[0] = 200;
                            session.WriteAudio(new float[3200], 3200); // 48k stereo / 30fps per captured frame.
                        }
                        Check(session.FramesAccepted == count, "input count uses captured frames");
                        Check(session.FramesWritten == Math.Max(0, count - depth + 1), "input and encoder counts are distinct before drain");
                        Check(session.AudioSampleFrames == count * 1600, "audio follows capture count");
                        var result = session.Complete();
                        Check(result.Frames == count && Math.Abs(result.DurationSeconds - count / 30d) < 1e-12, "final duration is capture-time based");
                        Check(session.FramesWritten == count && MediaVerifier.Frames == count, "tail participates in verification");
                        Check(FfmpegProcess.Instances.Single().Markers.SequenceEqual(Enumerable.Range(1, count).Select(i => (byte)i)), "no tail truncation, reorder or input reuse corruption");
                        Check(File.Exists(result.OutputPath), "only verified staged output is committed");
                        AssertClean(folder);
                        Check(Direct3DAnimeUpscaler.Instances.All(gpu => gpu.Disposed), "all GPU allocations released");
                    });
        SessionTest("cancel with fewer frames than capacity discards pending output and allows same-name retry", folder =>
        {
            var options = SessionOptions(folder, 4) with { AudioQuality = "none" };
            using (var session = new ExportSession(options, "fixture", "fixture", "h264_nvenc"))
            {
                WriteSessionFrame(session, options, 7); session.Cancel();
                Code("cancelled", () => session.Complete());
                Code("cancelled", () => WriteSessionFrame(session, options, 8));
            }
            Check(FfmpegProcess.Instances.Single().Markers.Count == 0, "cancel must not drain the pending frame");
            Check(!File.Exists(Path.Combine(folder, "queued.mp4")), "no cancelled final file"); AssertClean(folder);
            using var retry = new ExportSession(options, "fixture", "fixture", "h264_nvenc");
            WriteSessionFrame(retry, options, 9); Check(retry.Complete().Frames == 1, "same-name retry succeeds");
        });
        SessionTest("cancellation during final drain prevents final output and extra sink writes", folder =>
        {
            var options = SessionOptions(folder, 4) with { AudioQuality = "none" };
            using (var session = new ExportSession(options, "fixture", "fixture", "h264_nvenc"))
            {
                for (byte i = 1; i <= 3; i++) WriteSessionFrame(session, options, i);
                Direct3DAnimeUpscaler.OnReceive = session.Cancel;
                Code("cancelled", () => session.Complete());
            }
            Check(FfmpegProcess.Instances.Single().Markers.Count == 0, "cancel observed between readback and write");
            Check(!File.Exists(Path.Combine(folder, "queued.mp4")), "no final file"); AssertClean(folder);
        });
        foreach (string boundary in new[] { "submit", "receive", "pipe", "mux" })
            SessionTest("session " + boundary + " failure cleans owned resources and supports retry", folder =>
            {
                var options = SessionOptions(folder, 4) with { AudioQuality = "none" };
                using (var session = new ExportSession(options, "fixture", "fixture", "h264_nvenc"))
                {
                    if (boundary == "submit") Direct3DAnimeUpscaler.FailSubmitMarker = 9;
                    if (boundary == "receive") Direct3DAnimeUpscaler.FailReceiveMarker = 9;
                    if (boundary == "pipe") FfmpegProcess.FailWriteNumber = 1;
                    if (boundary == "mux") FfmpegProcess.FailMux = true;
                    Throws<Exception>(() => { WriteSessionFrame(session, options, 9); session.Complete(); });
                }
                Check(!File.Exists(Path.Combine(folder, "queued.mp4")), "failed export must not commit output"); AssertClean(folder);
                Check(Direct3DAnimeUpscaler.Instances.All(gpu => gpu.Disposed) && FfmpegProcess.Instances.All(proc => proc.Disposed), "no GPU or FFmpeg leak");
                Direct3DAnimeUpscaler.FailSubmitMarker = Direct3DAnimeUpscaler.FailReceiveMarker = 0;
                FfmpegProcess.FailWriteNumber = 0; FfmpegProcess.FailMux = false;
                using var retry = new ExportSession(options, "fixture", "fixture", "h264_nvenc");
                WriteSessionFrame(retry, options, 8); Check(retry.Complete().Frames == 1, "same-name retry after failure");
            });
        foreach (string code in new[] { "encoder_failed", "disk_full" })
            SessionTest("short tail pipe failure preserves encoder diagnostic " + code, folder =>
            {
                var options = SessionOptions(folder, 4) with { AudioQuality = "none" };
                using var session = new ExportSession(options, "fixture", "fixture", "h264_nvenc");
                WriteSessionFrame(session, options, 1);
                FfmpegProcess.FailWriteNumber = 1;
                FfmpegProcess.FailureCode = code;
                Code(code, () => session.Complete());
                AssertClean(folder);
                Check(!File.Exists(Path.Combine(folder, "queued.mp4")), "encoder failure cannot commit a file");
            });
        foreach (bool duringDrain in new[] { false, true })
            SessionTest("cross-thread cancel unblocks a synchronous queued sink during " + (duringDrain ? "drain" : "capture"), folder =>
            {
                var options = SessionOptions(folder, 4) with { AudioQuality = "none" };
                using var entered = new ManualResetEventSlim();
                using var released = new ManualResetEventSlim();
                using (var session = new ExportSession(options, "fixture", "fixture", "h264_nvenc"))
                {
                    for (byte i = 1; i <= 3; i++) WriteSessionFrame(session, options, i);
                    FfmpegProcess.BeforeWrite = () =>
                    {
                        entered.Set();
                        if (!released.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture sink was not released by Kill");
                    };
                    FfmpegProcess.OnKill = released.Set;
                    var write = Task.Run(() => Throws<ExportException>(() =>
                    {
                        if (duringDrain) session.Complete();
                        else WriteSessionFrame(session, options, 4);
                    }));
                    try
                    {
                        Check(entered.Wait(TimeSpan.FromSeconds(5)), "writer reached the blocking sink");
                        session.Cancel();
                        Check(write.Wait(TimeSpan.FromSeconds(5)), "cancel released the synchronous writer");
                        Check(write.GetAwaiter().GetResult().Code == "cancelled", "blocked write keeps cancellation identity");
                    }
                    finally { released.Set(); write.GetAwaiter().GetResult(); }
                }
                Check(FfmpegProcess.Instances.Single().Markers.Count == 0, "no queued frame was consumed after Kill");
                Check(!File.Exists(Path.Combine(folder, "queued.mp4")), "no final file after blocked-write cancel"); AssertClean(folder);
            });
        SessionTest("audio duration validation uses accepted frames before draining", folder =>
        {
            var options = SessionOptions(folder, 4);
            using var session = new ExportSession(options, "fixture", "fixture", "h264_nvenc");
            WriteSessionFrame(session, options, 1);
            Code("audio_timeline_mismatch", () => session.Complete());
            Check(FfmpegProcess.Instances.Single().Markers.Count == 0, "invalid audio aborts before drain"); AssertClean(folder);
        });
        SessionTest("four-slot startup refusal retries one slot and reports fallback", folder =>
        {
            Direct3DAnimeUpscaler.RejectFourSlots = true;
            var options = SessionOptions(folder, 4) with { AudioQuality = "none" };
            using var session = new ExportSession(options, "fixture", "fixture", "h264_nvenc");
            Check(session.UpscaleReadbackFrames == 1 && session.UpscaleFallbackReason.Contains("1 slot"), "actual fallback is visible");
            Check(session.FramesAccepted == 0, "fallback cannot advance capture");
            WriteSessionFrame(session, options, 1); session.Complete();
        });
        SessionTest("NV12 refusal retries RGBA with the same four-slot queue", folder =>
        {
            FfmpegCapabilities.RejectNv12 = true;
            var options = SessionOptions(folder, 4) with { AudioQuality = "none" };
            using var session = new ExportSession(options, "fixture", "fixture", "h264_nvenc");
            Check(session.UpscaleReadbackFrames == 4 && session.UpscaleExecution == "d3d11", "RGBA fallback keeps requested depth and D3D model");
            Check(session.UpscaleFallbackReason.Contains("NV12"), "fallback is reported");
            WriteSessionFrame(session, options, 1); session.Complete();
            Check(Direct3DAnimeUpscaler.Instances.All(gpu => gpu.Disposed), "both preflight instances are released");
        });
        SessionTest("total D3D startup refusal preserves the existing external upscaler fallback", folder =>
        {
            Direct3DAnimeUpscaler.RejectAll = true;
            var options = SessionOptions(folder, 4) with { AudioQuality = "none" };
            using var session = new ExportSession(options, "fixture", "fixture", "h264_nvenc");
            Check(session.UpscaleReadbackFrames == 0 && session.UpscaleExecution == "compute8", "existing fallback is used before capture");
            WriteSessionFrame(session, options, 1); session.Complete();
        });
        SessionTest("failed encoder preflight releases successfully probed GPU and creates no final output", folder =>
        {
            FfmpegCapabilities.RejectAll = true;
            Code("encoder_failed", () => new ExportSession(SessionOptions(folder, 4), "fixture", "fixture", "h264_nvenc"));
            Check(Direct3DAnimeUpscaler.Instances.All(gpu => gpu.Disposed), "probe resources are released on constructor failure");
            Check(!Directory.EnumerateFileSystemEntries(folder).Any(), "no preflight leftovers");
        });
        SessionTest("stable default is one slot and unsupported config values are rejected", folder =>
        {
            Check(new ExportOptions().Direct3DReadbackFrames == 1, "experimental acceleration must be explicitly enabled");
            foreach (int invalid in new[] { -1, 0, 2, 3, 5, 100 })
                Code("invalid_settings", () => (SessionOptions(folder, invalid)).Validate());
        });
    }
    private static ExportOptions SessionOptions(string folder, int depth) => new()
    {
        Title = "queued", OutputDirectory = folder, Width = 1920, Height = 1080,
        Encoder = "h264_nvenc", SuperResolutionEnabled = true, Direct3DReadbackFrames = depth,
        AudioQuality = "pcm16", Fps = 30, WriteCover = false
    };
    private static void WriteSessionFrame(ExportSession session, ExportOptions options, byte marker)
    {
        var canvas = options.GetCanvasLayout(); var bytes = new byte[canvas.CaptureWidth * canvas.CaptureHeight * 4];
        bytes[0] = marker; session.WriteFrame(bytes, bytes.Length);
    }
    private static void AssertClean(string folder) => Check(!Directory.EnumerateDirectories(folder, ".aa-video-export-*").Any(), "owned temporary directories removed");
    private static void SessionTest(string name, Action<string> run) => Test(name, () =>
    {
        Direct3DAnimeUpscaler.Reset(); FfmpegProcess.Reset();
        FfmpegCapabilities.RejectNv12 = FfmpegCapabilities.RejectD3D = FfmpegCapabilities.RejectAll = false;
        string folder = Path.Combine(Path.GetTempPath(), "aa-queued-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { run(folder); }
        finally { Directory.Delete(folder, true); }
    });
}
