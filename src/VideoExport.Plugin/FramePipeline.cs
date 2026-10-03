using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AAVideoExport.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace AAVideoExport.Plugin;

// Bounded render-target/readback ring -> ordered RGBA queue -> GPU encoder.
internal sealed class FramePipeline : IDisposable
{
    private readonly ExportSession _encoder;
    private readonly BlockingCollection<FrameBuffer> _queue;
    private readonly ConcurrentBag<IntPtr> _nativePool = new();
    private readonly bool _nativeBuffers;
    private int _nativeAllocated, _nativeReleased;
    private readonly Task _writer;
    private readonly CancellationTokenSource _stop = new();
    private readonly int _bytes;
    private readonly int _capacity;
    private readonly bool _legacySingleTarget;
    private readonly Texture2D? _readback;
    private readonly List<RenderTexture> _targets = new();
    private readonly Queue<RenderTexture> _available = new();
    private readonly Queue<PendingReadback> _pending = new();
    private RenderTexture? _renderTarget;
    private bool _finished;
    private bool _disposed;
    private Task? _completion;
    private Task? _cleanup;
    private Exception? _writeError;
    private int _peakBufferedFrames;
    private int _peakPendingReadbacks;
    public RenderTexture Target => RenderTarget;
    public RenderTexture RenderTarget
    {
        get
        {
            CheckWriter();
            if (_disposed || _finished) throw new ObjectDisposedException(nameof(FramePipeline));
            if (_legacySingleTarget)
            {
                _renderTarget = _targets[0];
                return _renderTarget;
            }
            if (_renderTarget != null) return _renderTarget;
            Drain(false);
            if (_available.Count == 0) ConsumeOldest(wait: true);
            _renderTarget = _available.Dequeue();
            return _renderTarget;
        }
    }
    public bool AsyncReadback { get; }
    public bool NativeBufferQueue => _nativeBuffers;
    public int AllocatedNativeBuffers => Volatile.Read(ref _nativeAllocated);
    public int ReleasedNativeBuffers => Volatile.Read(ref _nativeReleased);
    public int BufferedFrames => _queue.Count + _pending.Count;
    public int PendingReadbacks => _pending.Count;
    public int QueueCapacity => _capacity;
    public int TargetCount => _targets.Count;
    // Diagnostic accessors must never call RenderTarget: reading metrics during
    // failure/cancellation cannot consume or wait for a GPU request.
    public int CaptureWidth { get; }
    public int CaptureHeight { get; }
    public int PeakBufferedFrames => Volatile.Read(ref _peakBufferedFrames);
    public int PeakPendingReadbacks => Volatile.Read(ref _peakPendingReadbacks);
    public Task WriterCompletion => _writer;
    public Task DisposalCompletion => _cleanup ?? Task.CompletedTask;
    public long ReadbackTicks { get; private set; }
    public long RawDataTicks { get; private set; }
    public long CopyTicks { get; private set; }
    public long QueueTicks { get; private set; }
    public long ReadbackWaitTicks { get; private set; }
    private long _encoderTicks;
    public long EncoderTicks => Interlocked.Read(ref _encoderTicks);

    public FramePipeline(ExportOptions options, ExportSession encoder, bool useAsync = true, bool legacySingleTarget = false, bool nativeBuffers = true)
    {
        _encoder = encoder;
        _legacySingleTarget = legacySingleTarget && useAsync;
        var canvas = options.GetCanvasLayout();
        CaptureWidth = canvas.CaptureWidth;
        CaptureHeight = canvas.CaptureHeight;
        _bytes = checked(canvas.CaptureWidth * canvas.CaptureHeight * 4);
        _capacity = Math.Clamp((256 * 1024 * 1024) / _bytes, 1, 6);
        _queue = new BlockingCollection<FrameBuffer>(_capacity);
        AsyncReadback = useAsync && SystemInfo.supportsAsyncGPUReadback;
        _nativeBuffers = nativeBuffers && AsyncReadback;
        try
        {
            // Keep a deeper ring for smaller frames, where several in-flight
            // requests hide readback latency cheaply. Large frames use a
            // bounded three-target ring at 4K and fall back to one target only
            // when a single frame is already too large for that budget.
            const long asyncRingBudget = 384L * 1024 * 1024;
            const int fullHdBytes = 1920 * 1080 * 4;
            // Keep the previous pixel-budget policy while the initial readback
            // failure is investigated separately from the diagnostic failure.
            int slots = _legacySingleTarget ? 1 : AsyncReadback
                ? _bytes <= fullHdBytes
                    ? (int)Math.Clamp((256L * 1024 * 1024) / _bytes, 1, 6)
                    : (int)Math.Clamp(asyncRingBudget / (_bytes * 3L), 1, 3)
                : 1;
            for (int i = 0; i < slots; i++)
            {
                var target = new RenderTexture(canvas.CaptureWidth, canvas.CaptureHeight, 24, RenderTextureFormat.ARGB32);
                target.hideFlags = HideFlags.HideAndDontSave;
                _targets.Add(target);
                if (!target.Create()) throw new ExportException("render_target_failed", "显卡无法创建所选尺寸的离屏画布。");
                if (!_legacySingleTarget) _available.Enqueue(target);
            }
            if (!AsyncReadback)
            {
                _readback = new Texture2D(canvas.CaptureWidth, canvas.CaptureHeight, TextureFormat.RGBA32, false);
                _readback.hideFlags = HideFlags.HideAndDontSave;
            }
            _writer = Task.Run(WriteFrames);
        }
        catch
        {
            foreach (var target in _targets) { target.Release(); UnityEngine.Object.Destroy(target); }
            if (_readback != null) UnityEngine.Object.Destroy(_readback);
            _queue.Dispose(); _stop.Dispose();
            throw;
        }
    }

    private void WriteFrames()
    {
        try
        {
            foreach (var buffer in _queue.GetConsumingEnumerable(_stop.Token))
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    if (buffer.Native != IntPtr.Zero) _encoder.WriteFrame(buffer.Native, _bytes);
                    else _encoder.WriteFrame(buffer.Managed!, _bytes);
                }
                finally
                {
                    Interlocked.Add(ref _encoderTicks, Stopwatch.GetTimestamp() - started);
                    ReturnBuffer(buffer);
                }
            }
        }
        catch (Exception error) { _writeError = error; _stop.Cancel(); }
        finally { while (_queue.TryTake(out var buffer)) ReturnBuffer(buffer); }
    }

    public void Capture()
    {
        CheckWriter();
        if (_renderTarget == null) throw new ExportException("capture_target_missing", "尚未渲染当前视频帧。");
        var texture = _renderTarget;
        if (AsyncReadback)
        {
            // No callback wrapper or managed pixel-array conversion. The target
            // remains exclusively owned by its request until its bytes are copied.
            long started = Stopwatch.GetTimestamp();
            var request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32, null);
            ReadbackTicks += Stopwatch.GetTimestamp() - started;
            _pending.Enqueue(new PendingReadback(texture, request));
            UpdatePeaks();
            _renderTarget = null;
            Drain(false);
            return;
        }
        {
            var prior = RenderTexture.active;
            try
            {
                RenderTexture.active = texture;
                long stage = Stopwatch.GetTimestamp();
                _readback!.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                ReadbackTicks += Stopwatch.GetTimestamp() - stage;
                stage = Stopwatch.GetTimestamp();
                var native = _readback.GetRawTextureData();
                RawDataTicks += Stopwatch.GetTimestamp() - stage;
                var buffer = ArrayPool<byte>.Shared.Rent(_bytes);
                try
                {
                    stage = Stopwatch.GetTimestamp();
                    Il2CppBulkCopy.Copy(native, buffer, 0, _bytes);
                    CopyTicks += Stopwatch.GetTimestamp() - stage;
                    Enqueue(new FrameBuffer(buffer));
                }
                catch { ArrayPool<byte>.Shared.Return(buffer); throw; }
            }
            finally
            {
                RenderTexture.active = prior;
                if (!_legacySingleTarget) _available.Enqueue(texture);
                _renderTarget = null;
            }
        }
    }

    public void Drain(bool wait)
    {
        CheckWriter();
        if (!wait)
        {
            // Unity releases a completed request's data on the following frame.
            // A later completion cannot remain behind an unfinished FIFO head:
            // consume up to the last completed request in this Unity frame.
            // Only that prefix may block; the unfinished tail stays asynchronous.
            int index = 0, readyPrefix = 0;
            foreach (var pending in _pending)
            {
                index++;
                if (pending.Request.done || pending.Request.hasError) readyPrefix = index;
            }
            for (int i = 0; i < readyPrefix; i++) ConsumeOldest(wait: true);
        }
        while (_pending.Count != 0 && ConsumeOldest(wait)) { }
    }

    private unsafe bool ConsumeOldest(bool wait)
    {
        if (_pending.Count == 0) return false;
        var pending = _pending.Peek();
        var request = pending.Request;
        if (!request.done)
        {
            if (!wait) return false;
            long started = Stopwatch.GetTimestamp();
            request.WaitForCompletion();
            ReadbackWaitTicks += Stopwatch.GetTimestamp() - started;
        }
        if (!request.done || request.hasError)
            throw new ExportException("gpu_readback_failed", "显卡异步读取视频帧失败，已停止导出。");
        if (request.layerCount != 1 || request.layerDataSize != _bytes)
            throw new ExportException("gpu_readback_size_invalid", "显卡返回的视频帧尺寸与导出画布不一致。");
        long stage = Stopwatch.GetTimestamp();
        IntPtr data = request.GetDataRaw(0);
        RawDataTicks += Stopwatch.GetTimestamp() - stage;
        if (data == IntPtr.Zero)
            throw new ExportException("gpu_readback_empty", "显卡返回了空的视频帧数据。");
        FrameBuffer buffer;
        if (_nativeBuffers)
        {
            if (!_nativePool.TryTake(out var memory))
            {
                memory = Marshal.AllocHGlobal(_bytes);
                Interlocked.Increment(ref _nativeAllocated);
            }
            buffer = new FrameBuffer(memory);
        }
        else buffer = new FrameBuffer(ArrayPool<byte>.Shared.Rent(_bytes));
        try
        {
            stage = Stopwatch.GetTimestamp();
            if (buffer.Native != IntPtr.Zero) Buffer.MemoryCopy((void*)data, (void*)buffer.Native, _bytes, _bytes);
            else Marshal.Copy(data, buffer.Managed!, 0, _bytes);
            CopyTicks += Stopwatch.GetTimestamp() - stage;
            Enqueue(buffer);
        }
        catch { ReturnBuffer(buffer); throw; }
        _pending.Dequeue();
        if (!_legacySingleTarget) _available.Enqueue(pending.Target);
        return true;
    }

    private void ReturnBuffer(FrameBuffer buffer)
    {
        if (buffer.Native != IntPtr.Zero) _nativePool.Add(buffer.Native);
        else ArrayPool<byte>.Shared.Return(buffer.Managed!);
    }

    private void Enqueue(FrameBuffer buffer)
    {
        // A stuck encoder must not lock the Unity main thread forever.
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (!_queue.TryAdd(buffer, 15000, _stop.Token))
                throw new ExportException("encoder_backpressure_timeout", "编码器连续 15 秒没有接收下一帧，已停止导出。");
            UpdatePeaks();
        }
        finally { QueueTicks += Stopwatch.GetTimestamp() - started; }
    }

    private void UpdatePeaks()
    {
        int buffered = _queue.Count + _pending.Count;
        int prior;
        do
        {
            prior = Volatile.Read(ref _peakBufferedFrames);
            if (buffered <= prior) break;
        } while (Interlocked.CompareExchange(ref _peakBufferedFrames, buffered, prior) != prior);
        int pending = _pending.Count;
        do
        {
            prior = Volatile.Read(ref _peakPendingReadbacks);
            if (pending <= prior) break;
        } while (Interlocked.CompareExchange(ref _peakPendingReadbacks, pending, prior) != prior);
    }

    private void CheckWriter()
    {
        if (_writeError != null) throw new ExportException("encode_failed", "视频编码失败：" + _writeError.Message, _writeError);
        _stop.Token.ThrowIfCancellationRequested();
    }

    public Task FinishFrames()
    {
        Drain(true);
        _finished = true;
        _queue.CompleteAdding();
        _completion = _writer.ContinueWith(task => { task.GetAwaiter().GetResult(); CheckWriter(); }, TaskScheduler.Default);
        return _completion;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_finished) { _encoder.Cancel(); _stop.Cancel(); }
        if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
        // Unity calls stay on the capture thread. Outstanding requests must stop
        // using their textures before cancellation can release those textures.
        var unsafeTargets = new HashSet<RenderTexture>();
        while (_pending.Count != 0)
        {
            var pending = _pending.Dequeue();
            try
            {
                if (!pending.Request.done) pending.Request.WaitForCompletion();
                if (!pending.Request.done)
                {
                    unsafeTargets.Add(pending.Target);
                    UnityEngine.Debug.LogWarning("AA Video Export: readback cleanup wait returned before completion. Restart AA before retrying.");
                }
            }
            catch (Exception error)
            {
                // Safer to retain a texture until AA exits than free GPU memory
                // while an outstanding command may still reference it.
                unsafeTargets.Add(pending.Target);
                UnityEngine.Debug.LogWarning("AA Video Export: readback cleanup wait failed (" + error.GetType().Name + "). Restart AA before retrying.");
            }
        }
        foreach (var target in _targets)
            if (!unsafeTargets.Contains(target)) { target.Release(); UnityEngine.Object.Destroy(target); }
        _targets.Clear(); _available.Clear(); _renderTarget = null;
        if (_readback != null) UnityEngine.Object.Destroy(_readback);
        // Queue/token lifetime ends after writer; no racing Dispose against enumeration.
        _cleanup = (_completion ?? _writer).ContinueWith(_ =>
        {
            // The producer has stopped and every synchronous native pipe write
            // has returned. No readback or child process owns these pointers.
            while (_nativePool.TryTake(out var memory))
            {
                Marshal.FreeHGlobal(memory);
                Interlocked.Increment(ref _nativeReleased);
            }
            _queue.Dispose(); _stop.Dispose();
        }, TaskScheduler.Default);
    }

    private sealed class FrameBuffer
    {
        internal readonly byte[]? Managed;
        internal readonly IntPtr Native;
        internal FrameBuffer(byte[] managed) => Managed = managed;
        internal FrameBuffer(IntPtr native) => Native = native;
    }

    private sealed record PendingReadback(RenderTexture Target, AsyncGPUReadbackRequest Request);
}
