using AAVideoExport.Core;
using AAVideoExport.Plugin;
using UnityEngine;
using UnityEngine.Rendering;

int passed = 0;
await Test("4K async readback keeps a three-target ring", () =>
{
    var encoder = new ExportSession();
    using var pipeline = new FramePipeline(new ExportOptions { Width = 3840, Height = 2160 }, encoder);
    Check(RenderTexture.Created.Count == 3, "4K capture uses three render targets to keep the GPU fed");
    Check(pipeline.TargetCount == 3, "4K target count is exposed for diagnostics");
    Check(pipeline.AsyncReadback, "4K target contract exercises the asynchronous readback path");
    return Task.CompletedTask;
});
await Test("1080p async readback keeps the deeper six-target ring", () =>
{
    var encoder = new ExportSession();
    using var pipeline = new FramePipeline(new ExportOptions { Width = 1920, Height = 1080 }, encoder);
    Check(RenderTexture.Created.Count == 6, "1080p capture uses six render targets to hide readback latency");
    Check(pipeline.TargetCount == 6, "1080p target count is exposed for diagnostics");
    Check(pipeline.AsyncReadback, "1080p target contract exercises the asynchronous readback path");
    return Task.CompletedTask;
});
await Test("near-FHD fill captures keep the conservative ring", () =>
{
    var encoder = new ExportSession();
    using var pipeline = new FramePipeline(new ExportOptions { Width = 2000, Height = 1080 }, encoder);
    Check(pipeline.TargetCount == 3, "a fill margin above 1080p must use the conservative ring");
    return Task.CompletedTask;
});
await Test("out-of-order GPU completions preserve FIFO and unique pending targets", async () =>
{
    var encoder = new ExportSession();
    using var pipeline = new FramePipeline(new ExportOptions(), encoder);
    for (int i = 1; i <= 6; i++) { pipeline.RenderTarget.Marker = i; pipeline.Capture(); }
    Check(RenderTexture.Created.Count == 6, "bounded six-target 1080p ring");
    Check(AsyncGPUReadback.Requests.Select(r => r.Target).Distinct().Count() == 6, "pending frames own distinct targets");
    Check(pipeline.BufferedFrames == 6, "pending frames are bounded by the ring");
    for (int i = 1; i < AsyncGPUReadback.Requests.Count; i++) AsyncGPUReadback.Requests[i].Done = true;
    pipeline.Drain(false);
    Check(pipeline.PendingReadbacks == 0, "later completed frames must be copied this frame after consuming the first");
    var target = pipeline.RenderTarget;
    Check(AsyncGPUReadback.Requests[0].Waits == 1, "full ring waits oldest");
    Check(ReferenceEquals(target, AsyncGPUReadback.Requests[0].Target), "completed oldest target can be reused");
    target.Marker = 7; pipeline.Capture();
    await pipeline.FinishFrames();
    Check(encoder.Frames.SequenceEqual(new[] { 1, 2, 3, 4, 5, 6, 7 }), "writer receives every frame once in order");
    Check(pipeline.PeakPendingReadbacks >= 6 && pipeline.PeakBufferedFrames >= 6, "pipeline records bounded peak occupancy");
});
await Test("finish drains final pending frames and releases completed targets", async () =>
{
    var encoder = new ExportSession();
    var pipeline = new FramePipeline(new ExportOptions(), encoder);
    pipeline.Target.Marker = 7; pipeline.Capture();
    await pipeline.FinishFrames();
    pipeline.Dispose();
    Check(encoder.Frames.SequenceEqual(new[] { 7 }), "one-frame export retained");
    Check(RenderTexture.Created.All(t => t.Released), "all finished targets released");
});
await Test("out-of-order completions are copied before Unity expires their data on the next frame", async () =>
{
    var encoder = new ExportSession();
    using var pipeline = new FramePipeline(new ExportOptions(), encoder);
    for (int i = 1; i <= 3; i++) { pipeline.RenderTarget.Marker = i; pipeline.Capture(); }
    AsyncGPUReadback.Requests[1].Done = true;
    AsyncGPUReadback.Requests[1].AvailableFrame = AsyncGPUReadback.Frame;
    pipeline.Drain(false);
    Check(AsyncGPUReadback.Requests[0].Waits == 1, "only wait for the unfinished head preceding completed data");
    Check(AsyncGPUReadback.Requests[2].Waits == 0 && pipeline.PendingReadbacks == 1, "unfinished tail remains asynchronous");
    AsyncGPUReadback.Frame++;
    await pipeline.FinishFrames();
    Check(encoder.Frames.SequenceEqual(new[] { 1, 2, 3 }), "all pixels survive native completion expiry in FIFO order");
});
await Test("capture dimensions remain readable without waiting, draining, or touching released textures", async () =>
{
    var encoder = new ExportSession();
    var pipeline = new FramePipeline(new ExportOptions { Width = 32, Height = 18 }, encoder);
    for (int i = 0; i < 6; i++) { pipeline.RenderTarget.Marker = i + 1; pipeline.Capture(); }
    AsyncGPUReadback.Requests[0].Error = true;
    for (int i = 0; i < 50; i++)
    {
        Check(pipeline.CaptureWidth == 32 && pipeline.CaptureHeight == 18, "dimensions must be immutable capture metadata");
        Check(pipeline.PendingReadbacks == 6 && encoder.Frames.Count == 0, "metadata must not drain or submit pending frames");
        Check(AsyncGPUReadback.Requests.All(r => r.Waits == 0), "metadata must not wait for the GPU");
    }
    pipeline.Dispose();
    await pipeline.WriterCompletion;
    Check(pipeline.CaptureWidth == 32 && pipeline.CaptureHeight == 18, "dimensions survive texture cleanup");
});
await Test("cancel waits pending GPU requests but does not enqueue their pixels", async () =>
{
    var encoder = new ExportSession();
    var pipeline = new FramePipeline(new ExportOptions(), encoder);
    pipeline.Target.Marker = 1; pipeline.Capture();
    pipeline.Target.Marker = 2; pipeline.Capture();
    pipeline.Dispose();
    await pipeline.WriterCompletion;
    pipeline.Dispose();
    Check(encoder.Cancelled && encoder.Frames.Count == 0, "cancel does not commit pending frames");
    Check(AsyncGPUReadback.Requests.All(r => r.Done && r.Waits == 1), "requests complete before release");
    Check(RenderTexture.Created.All(t => t.Released), "cancel releases targets");
});
await Test("readback error and wrong sizes fail rather than switch paths", async () =>
{
    foreach (bool corruptSize in new[] { false, true })
    {
        var encoder = new ExportSession();
        var pipeline = new FramePipeline(new ExportOptions(), encoder);
        pipeline.Target.Marker = 4; pipeline.Capture();
        var request = AsyncGPUReadback.Requests[^1];
        request.Done = true;
        if (corruptSize) request.Size--; else request.Error = true;
        string? code = null;
        try { pipeline.Drain(false); } catch (ExportException ex) { code = ex.Code; }
        Check(code == (corruptSize ? "gpu_readback_size_invalid" : "gpu_readback_failed"), "stable readback error");
        pipeline.Dispose(); await pipeline.WriterCompletion;
        Check(encoder.Frames.Count == 0, "invalid bytes never reach encoder");
    }
});
await Test("unsupported device and explicit sync choice retain ordered synchronous path", async () =>
{
    foreach (bool supported in new[] { false, true })
    {
        SystemInfo.supportsAsyncGPUReadback = supported;
        var encoder = new ExportSession();
        using var pipeline = new FramePipeline(new ExportOptions(), encoder, useAsync: !supported);
        Check(!pipeline.AsyncReadback, "synchronous path selected before first frame");
        pipeline.Target.Marker = 8; pipeline.Capture();
        pipeline.Target.Marker = 9; pipeline.Capture();
        await pipeline.FinishFrames();
        Check(encoder.Frames.SequenceEqual(new[] { 8, 9 }), "sync order preserved");
        Check(AsyncGPUReadback.Requests.Count == 0, "no asynchronous calls in sync path");
    }
});
await Test("cleanup retains still-pending texture when GPU wait returns without completion", async () =>
{
    var encoder = new ExportSession();
    var pipeline = new FramePipeline(new ExportOptions(), encoder);
    pipeline.Target.Marker = 1; pipeline.Capture();
    pipeline.Target.Marker = 2; pipeline.Capture();
    var unfinished = AsyncGPUReadback.Requests[0];
    unfinished.WaitLeavesPending = true;
    try { pipeline.Dispose(); }
    finally { await pipeline.WriterCompletion; }
    Check(encoder.Cancelled && encoder.Frames.Count == 0, "cancel commits no pending pixels");
    Check(unfinished.Waits == 1 && !unfinished.Done, "wait can return while request remains pending");
    Check(!unfinished.Target.Released && !UnityEngine.Object.Destroyed.Contains(unfinished.Target), "unsafe target retained until AA exits");
    Check(RenderTexture.Created.Where(t => t != unfinished.Target).All(t => t.Released), "safe targets still released");
    Check(UnityEngine.Debug.Warnings.Any(w => w.Contains("Restart AA before retrying")), "unsafe retention is reported");
    pipeline.Dispose();
    Check(unfinished.Waits == 1, "repeated disposal does not retry or release unsafe target");
});
await Test("native queue owns pixels after the GPU readback is overwritten", async () =>
{
    var encoder = new ExportSession();
    encoder.AllowNativeWrite.Reset();
    var pipeline = new FramePipeline(new ExportOptions { Width = 16, Height = 16 }, encoder);
    pipeline.Target.Marker = 37; pipeline.Capture();
    AsyncGPUReadback.Requests[^1].Done = true;
    pipeline.Drain(false);
    await encoder.NativeWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    System.Runtime.InteropServices.Marshal.WriteByte(AsyncGPUReadback.Requests[^1].Data, 99);
    encoder.AllowNativeWrite.Set();
    await pipeline.FinishFrames();
    Check(encoder.Frames.SequenceEqual(new[] { 37 }) && encoder.NativeWrites == 1 && encoder.ManagedWrites == 0,
        "native writer consumes an owned snapshot rather than Unity's expiring pointer");
    pipeline.Dispose(); await pipeline.DisposalCompletion;
    Check(pipeline.AllocatedNativeBuffers == 1 && pipeline.ReleasedNativeBuffers == 1, "owned native memory is released after writing");
});
await Test("native queue stays bounded through a long ordered capture", async () =>
{
    var encoder = new ExportSession();
    var pipeline = new FramePipeline(new ExportOptions { Width = 16, Height = 16 }, encoder);
    for (int i = 0; i < 200; i++)
    {
        pipeline.Target.Marker = i; pipeline.Capture();
        AsyncGPUReadback.Requests[^1].Done = true; pipeline.Drain(false);
    }
    await pipeline.FinishFrames();
    Check(encoder.Frames.SequenceEqual(Enumerable.Range(0, 200)), "native memory reuse keeps every frame in order");
    Check(pipeline.AllocatedNativeBuffers <= pipeline.QueueCapacity + 2, "queue, worker and producer bound native allocation");
    pipeline.Dispose(); await pipeline.DisposalCompletion;
    Check(pipeline.ReleasedNativeBuffers == pipeline.AllocatedNativeBuffers, "all pooled native buffers are released");
});
await Test("cancel unblocks native writer before releasing its owned buffer", async () =>
{
    var encoder = new ExportSession(); encoder.AllowNativeWrite.Reset();
    var pipeline = new FramePipeline(new ExportOptions { Width = 16, Height = 16 }, encoder);
    pipeline.Target.Marker = 11; pipeline.Capture(); AsyncGPUReadback.Requests[^1].Done = true; pipeline.Drain(false);
    await encoder.NativeWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    pipeline.Dispose(); await pipeline.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    Check(encoder.Cancelled && encoder.Frames.Count == 0, "cancelled native frame is never committed");
    Check(pipeline.ReleasedNativeBuffers == pipeline.AllocatedNativeBuffers, "native memory outlives the writer and is then released");
});
await Test("managed comparison path preserves frame order without native allocations", async () =>
{
    var encoder = new ExportSession();
    var pipeline = new FramePipeline(new ExportOptions { Width = 16, Height = 16 }, encoder, nativeBuffers: false);
    for (int i = 0; i < 3; i++) { pipeline.Target.Marker = i + 5; pipeline.Capture(); }
    await pipeline.FinishFrames(); pipeline.Dispose(); await pipeline.DisposalCompletion;
    Check(!pipeline.NativeBufferQueue && pipeline.AllocatedNativeBuffers == 0 && encoder.NativeWrites == 0, "managed mode has no native pool");
    Check(encoder.Frames.SequenceEqual(new[] { 5, 6, 7 }), "managed mode retains FIFO");
});
Console.WriteLine($"RESULT: {passed}/15 passed.");

async Task Test(string name, Func<Task> body)
{
    try { await body(); Console.WriteLine("PASS " + name); passed++; }
    finally { AsyncGPUReadback.Reset(); }
}
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
