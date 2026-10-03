// Explicit test doubles for native GPU, platform selection and external media
// boundaries. The queue, ExportSession, options and canvas are production links.
// These tests do NOT validate D3D, real FFmpeg output, audio samples or AA.
using System.Runtime.InteropServices;
namespace AAVideoExport.Core;

internal sealed class Direct3DAnimeUpscaler : IQueuedFrameProcessor, IDisposable
{
    internal static readonly List<Direct3DAnimeUpscaler> Instances = new();
    internal static bool RejectFourSlots, RejectAll;
    internal static byte FailSubmitMarker, FailReceiveMarker;
    internal static Action? OnReceive;
    private readonly Queue<byte> pending = new();
    private readonly byte[] output = new byte[4];
    public int Capacity { get; }
    public int PendingFrames => pending.Count;
    public string AdapterName => "explicit GPU test double";
    public bool Nv12Output { get; }
    internal bool Disposed;
    internal int Reads;
    public Direct3DAnimeUpscaler(ExportOptions options, string encoder, bool flipVertical, bool useNv12 = true, int queueDepth = 1)
    {
        if (RejectAll || RejectFourSlots && queueDepth == 4) throw new InvalidOperationException("fixture GPU refused staging allocation");
        Capacity = queueDepth; Nv12Output = useNv12 && options.Width % 4 == 0; Instances.Add(this);
    }
    public unsafe void Submit(byte[] pixels) { fixed (byte* pointer = pixels) Submit((IntPtr)pointer); }
    public void Submit(IntPtr pixels)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(Direct3DAnimeUpscaler));
        byte value = Marshal.ReadByte(pixels);
        if (FailSubmitMarker != 0 && value == FailSubmitMarker) throw new InvalidOperationException("fixture GPU submit failure");
        if (PendingFrames == Capacity) throw new InvalidOperationException("fixture full staging ring");
        pending.Enqueue(value);
    }
    public byte[] Receive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;
        if (FailReceiveMarker != 0 && pending.Peek() == FailReceiveMarker) throw new InvalidOperationException("fixture GPU read failure");
        output[0] = pending.Dequeue(); OnReceive?.Invoke(); return output;
    }
    public void Dispose() { Disposed = true; pending.Clear(); }
    internal static void Reset()
    { Instances.Clear(); RejectFourSlots = RejectAll = false; FailSubmitMarker = FailReceiveMarker = 0; OnReceive = null; }
}
internal static class Direct3DUpscalePolicy
{
    internal static bool IsEligible(ExportOptions options, string encoder) => options.SuperResolutionEnabled;
    internal static ExportOptions EncodingOptions(ExportOptions options) => options with { SuperResolutionEnabled = false, CaptureMode = "native", SourceWidth = 0, SourceHeight = 0, CanvasMode = "viewport" };
}
internal static class FfmpegCapabilities
{
    internal static bool RejectNv12, RejectD3D, RejectAll;
    internal static Task<ExportOptions> PrepareEncoderAsync(string path, ExportOptions options, string encoder, CancellationToken token, bool nv12Input = false)
    {
        if (RejectAll || RejectNv12 && nv12Input || RejectD3D && !options.SuperResolutionEnabled)
            throw new ExportException("encoder_failed", "fixture encoder refusal");
        return Task.FromResult(options);
    }
}
internal static class UpscaleShaders
{
    internal static bool UsesGpu(ExportOptions options) => options.SuperResolutionEnabled;
    internal static void Stage(string directory, ExportOptions options) { }
}
internal static class EncoderArguments
{
    internal static void AddHardwareDeviceOptions(List<string> args, ExportOptions options, string encoder) { }
    internal static void AddFilterThreadOptions(List<string> args, ExportOptions options) { }
    internal static IEnumerable<string> VideoNv12(ExportOptions options, string encoder) => Array.Empty<string>();
    internal static IEnumerable<string> Video(ExportOptions options, string encoder, bool flip) => Array.Empty<string>();
    internal static IEnumerable<string> Audio(ExportOptions options) => Array.Empty<string>();
}
internal sealed class FfmpegProcess : IDisposable
{
    internal static readonly List<FfmpegProcess> Instances = new();
    internal static int FailWriteNumber;
    internal static bool FailMux;
    internal static string FailureCode = "encoder_failed";
    internal static Action? BeforeWrite, OnKill;
    internal readonly List<byte> Markers = new();
    internal bool Killed, Closed, Disposed;
    internal Stream Input { get; }
    private readonly string output;
    internal FfmpegProcess(string executable, IEnumerable<string> arguments, bool input = false, string? workingDirectory = null, string? gpuEncoder = null)
    { output = arguments.Last(); Input = new RecordingStream(this); Instances.Add(this); }
    internal void CloseInput()
    {
        if (Closed) return;
        Closed = true;
        if (!Killed) File.WriteAllBytes(output, Markers.ToArray());
    }
    internal void Kill() { Killed = true; Closed = true; OnKill?.Invoke(); }
    internal Task<ProcessResult> WaitAsync(TimeSpan timeout, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(new ProcessResult(Killed ? 1 : 0)); }
    public void Dispose() { Disposed = true; Kill(); Input.Dispose(); }
    internal static Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string[] args = arguments.ToArray();
        bool mux = !args.Contains("lavfi");
        if (mux && FailMux) return Task.FromResult(new ProcessResult(1));
        File.WriteAllBytes(args.Last(), new byte[] { 1, 2, 3 });
        return Task.FromResult(new ProcessResult(0));
    }
    private sealed class RecordingStream : MemoryStream
    {
        private readonly FfmpegProcess owner;
        public RecordingStream(FfmpegProcess owner) => this.owner = owner;
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BeforeWrite?.Invoke();
            if (owner.Killed || owner.Closed || FailWriteNumber != 0 && owner.Markers.Count + 1 == FailWriteNumber)
                throw new IOException("fixture pipe failure");
            owner.Markers.Add(buffer[0]);
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
    }
    internal static void Reset() { Instances.Clear(); FailWriteNumber = 0; FailMux = false; FailureCode = "encoder_failed"; BeforeWrite = OnKill = null; }
}
internal sealed record ProcessResult(int ExitCode)
{
    internal void EnsureSuccess(string stage) { if (ExitCode != 0) throw new ExportException(FfmpegProcess.FailureCode, stage + " fixture failure"); }
}
internal static class MediaVerifier
{
    internal static long Frames;
    internal static Task VerifyAsync(string executable, string path, ExportOptions options, long frames, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Frames = frames;
        if (FfmpegProcess.Instances.Last().Markers.Count != frames)
            throw new ExportException("verification_failed", "Frame count does not match fixture sink.");
        return Task.CompletedTask;
    }
}
