namespace AAVideoExport.Core;

/// <summary>
/// Serialized, bounded GPU submission/readback contract. Submit snapshots input
/// before returning. Receive returns a reusable buffer, consumed synchronously
/// by the sink before the next Receive. The owner disposes the processor.
/// </summary>
internal interface IQueuedFrameProcessor
{
    int Capacity { get; }
    int PendingFrames { get; }
    void Submit(IntPtr pixels);
    byte[] Receive(CancellationToken cancellationToken);
}

/// <summary>
/// Keeps future GPU work in flight without retaining capture buffers or moving
/// the story/audio clock. Only natural completion drains; failure/cancel aborts.
/// Methods are serialized by the export's existing encoder worker/completion.
/// </summary>
internal sealed class QueuedVideoFrameWriter
{
    private IQueuedFrameProcessor? processor;
    private Action<byte[]>? sink;
    private long acceptedFrames, writtenFrames;
    private bool closed;
    public long AcceptedFrames => Interlocked.Read(ref acceptedFrames);
    public long WrittenFrames => Interlocked.Read(ref writtenFrames);

    public QueuedVideoFrameWriter(IQueuedFrameProcessor processor, Action<byte[]> sink)
    {
        this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        if (processor.Capacity is < 1 or > 4 || processor.PendingFrames != 0)
            throw new ArgumentException("The GPU readback queue must be empty and have one to four slots.", nameof(processor));
    }

    public unsafe void Write(byte[] pixels, CancellationToken cancellationToken)
    {
        if (pixels == null) throw new ArgumentNullException(nameof(pixels));
        fixed (byte* pointer = pixels) Write((IntPtr)pointer, cancellationToken);
    }

    public void Write(IntPtr pixels, CancellationToken cancellationToken)
    {
        if (closed) throw new InvalidOperationException("The video queue is no longer accepting frames.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pixels == IntPtr.Zero) throw new ArgumentException("A frame pointer is required.", nameof(pixels));
            if (processor!.PendingFrames >= processor.Capacity)
                throw new InvalidOperationException("The GPU readback queue unexpectedly remained full.");
            // UpdateSubresource has snapshotted the input when Submit returns.
            // The caller may now reuse it even while GPU work is still pending.
            processor.Submit(pixels);
            Interlocked.Increment(ref acceptedFrames);
            cancellationToken.ThrowIfCancellationRequested();
            if (processor.PendingFrames == processor.Capacity) WriteOldest(cancellationToken);
        }
        catch { Abort(); throw; }
    }

    public void Drain(CancellationToken cancellationToken)
    {
        if (closed) throw new InvalidOperationException("The video queue cannot be drained after completion or failure.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (processor!.PendingFrames != 0) WriteOldest(cancellationToken);
            if (AcceptedFrames != WrittenFrames)
                throw new InvalidOperationException("Accepted and encoded frame counts differ after GPU drain.");
        }
        finally { Abort(); }
    }

    private void WriteOldest(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] output = processor!.Receive(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        sink!(output); // Never abandon an async write holding this reusable buffer.
        Interlocked.Increment(ref writtenFrames);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Abort()
    {
        // Never Map/drain on cancel, failure or disposal. Releasing GPU resources
        // belongs to the owner after the serialized writer has stopped.
        closed = true;
        processor = null;
        sink = null;
    }
}
