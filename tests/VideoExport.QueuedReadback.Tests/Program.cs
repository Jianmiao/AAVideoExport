using System.Runtime.InteropServices;
using AAVideoExport.Core;

internal static partial class Program
{
    private static int passed, failed;
    private static int Main()
    {
        QueueTests();
        SessionTests();
        Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
    private static void Test(string name, Action run)
    {
        try { run(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static void Check(bool value, string detail)
    { if (!value) throw new Exception(detail); }
    private static T Throws<T>(Action run) where T : Exception
    {
        try { run(); } catch (T ex) { return ex; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Code(string code, Action run) =>
        Check(Throws<ExportException>(run).Code == code, "Expected error code " + code);
    private static void QueueTests()
    {
        foreach (int depth in new[] { 1, 4 })
            foreach (int count in new[] { 1, 2, 3, 4, 5, 19, 10000 })
                Test($"queue depth {depth} count {count}: order, boundedness, final drain and reusable output", () =>
                {
                    var gpu = new ProbeProcessor(depth);
                    var output = new List<int>();
                    var writer = new QueuedVideoFrameWriter(gpu, bytes => output.Add(BitConverter.ToInt32(bytes)));
                    for (int i = 0; i < count; i++)
                    {
                        byte[] frame = BitConverter.GetBytes(i);
                        writer.Write(frame, default);
                        Array.Fill(frame, (byte)255); // Safe to reuse input immediately.
                        Check(gpu.PendingFrames < depth, "pending staging frames are bounded");
                        Check(writer.AcceptedFrames == i + 1, "every captured frame is accepted once");
                        Check(writer.WrittenFrames == Math.Max(0, i + 2 - depth), "encoder count excludes pending frames");
                    }
                    writer.Drain(default);
                    Check(output.SequenceEqual(Enumerable.Range(0, count)), "FIFO output has no loss, duplicates or borrowed input corruption");
                    Check(writer.WrittenFrames == count && gpu.PendingFrames == 0, "drain completes the tail");
                    Check(gpu.Peak <= depth, "no unbounded buffering");
                    Throws<InvalidOperationException>(() => writer.Drain(default));
                    Throws<InvalidOperationException>(() => writer.Write(new byte[4], default));
                });
        Test("native pointer is snapshotted before caller releases memory", () =>
        {
            var gpu = new ProbeProcessor(4); var output = new List<int>();
            var writer = new QueuedVideoFrameWriter(gpu, bytes => output.Add(BitConverter.ToInt32(bytes)));
            for (int i = 0; i < 11; i++)
            {
                var pointer = Marshal.AllocHGlobal(4);
                try { Marshal.WriteInt32(pointer, i); writer.Write(pointer, default); Marshal.WriteInt32(pointer, -1); }
                finally { Marshal.FreeHGlobal(pointer); }
            }
            writer.Drain(default);
            Check(output.SequenceEqual(Enumerable.Range(0, 11)), "no retained native input");
        });
        foreach (string stage in new[] { "before", "submit", "receive", "sink", "drain" })
            Test("cancellation at " + stage + " never drains or sends a subsequent frame", () =>
            {
                using var token = new CancellationTokenSource();
                var gpu = new ProbeProcessor(4); int written = 0;
                var writer = new QueuedVideoFrameWriter(gpu, bytes => { written++; if (stage == "sink") token.Cancel(); });
                if (stage == "before") token.Cancel();
                if (stage == "submit") gpu.AfterSubmit = token.Cancel;
                if (stage == "receive") gpu.AfterReceive = token.Cancel;
                if (stage == "drain")
                {
                    writer.Write(new byte[4], token.Token); token.Cancel();
                    Throws<OperationCanceledException>(() => writer.Drain(token.Token));
                }
                else Throws<OperationCanceledException>(() => { for (int i = 0; i < 4; i++) writer.Write(new byte[4], token.Token); });
                int reads = gpu.Reads;
                writer.Abort();
                Throws<InvalidOperationException>(() => writer.Drain(default));
                Check(gpu.Reads == reads, "abort must not read pending GPU data");
                Check(written == (stage == "sink" ? 1 : 0), "no new sink write after cancellation is observed");
            });
        foreach (string stage in new[] { "submit", "receive", "sink", "drain" })
            Test("failure at " + stage + " stops queue, preserves counts and permits fresh retry", () =>
            {
                var gpu = new ProbeProcessor(4); int written = 0;
                var writer = new QueuedVideoFrameWriter(gpu, bytes => { if (stage == "sink") throw new IOException("sink failed"); written++; });
                if (stage == "submit") gpu.AfterSubmit = () => throw new InvalidOperationException("GPU submit failed");
                if (stage == "receive" || stage == "drain") gpu.AfterReceive = () => throw new InvalidOperationException("GPU read failed");
                Throws<Exception>(() => { for (int i = 0; i < (stage == "drain" ? 2 : 4); i++) writer.Write(new byte[4], default); writer.Drain(default); });
                int reads = gpu.Reads;
                Throws<InvalidOperationException>(() => writer.Write(new byte[4], default));
                Throws<InvalidOperationException>(() => writer.Drain(default));
                Check(gpu.Reads == reads && writer.WrittenFrames == written, "faulted queue cannot silently continue");
                var retryGpu = new ProbeProcessor(4); var retry = new QueuedVideoFrameWriter(retryGpu, _ => { });
                retry.Write(new byte[4], default); retry.Drain(default);
                Check(retry.WrittenFrames == 1, "new export queue can retry");
            });
        Test("cancel partway through tail drain leaves remaining outputs unsent", () =>
        {
            using var token = new CancellationTokenSource(); var gpu = new ProbeProcessor(4); int writes = 0;
            var writer = new QueuedVideoFrameWriter(gpu, _ => { writes++; token.Cancel(); });
            for (int i = 0; i < 3; i++) writer.Write(new byte[4], token.Token);
            Throws<OperationCanceledException>(() => writer.Drain(token.Token));
            Check(writes == 1 && gpu.Reads == 1 && gpu.PendingFrames == 2, "remaining tail was discarded, not drained");
        });
        Test("invalid or nonempty queues are rejected", () =>
        {
            Throws<ArgumentException>(() => new QueuedVideoFrameWriter(new ProbeProcessor(0), _ => { }));
            Throws<ArgumentException>(() => new QueuedVideoFrameWriter(new ProbeProcessor(5), _ => { }));
            var gpu = new ProbeProcessor(4);
            var pointer = Marshal.AllocHGlobal(4);
            try { Marshal.WriteInt32(pointer, 1); gpu.Submit(pointer); }
            finally { Marshal.FreeHGlobal(pointer); }
            Throws<ArgumentException>(() => new QueuedVideoFrameWriter(gpu, _ => { }));
        });
    }

    private sealed class ProbeProcessor : IQueuedFrameProcessor
    {
        private readonly Queue<int> queue = new(); private readonly byte[] output = new byte[4];
        public int Capacity { get; }
        public int PendingFrames => queue.Count;
        public int Peak, Reads;
        public Action? AfterSubmit, AfterReceive;
        public ProbeProcessor(int capacity) => Capacity = capacity;
        public void Submit(IntPtr pixels)
        {
            if (queue.Count == Capacity) throw new Exception("overwrote live staging slot");
            queue.Enqueue(Marshal.ReadInt32(pixels)); Peak = Math.Max(Peak, queue.Count); AfterSubmit?.Invoke();
        }
        public byte[] Receive(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Reads++; BitConverter.TryWriteBytes(output, queue.Dequeue()); AfterReceive?.Invoke(); return output; }
    }
}
