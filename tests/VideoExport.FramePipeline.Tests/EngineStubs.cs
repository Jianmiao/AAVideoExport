using System.Runtime.InteropServices;

namespace AAVideoExport.Core
{
    public sealed class ExportOptions
    {
        public int Width { get; init; } = 16;
        public int Height { get; init; } = 16;
        public (int CaptureWidth, int CaptureHeight) GetCanvasLayout() => (Width, Height);
    }
    public sealed class ExportException : Exception
    {
        public string Code { get; }
        public ExportException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
    }
    public sealed class ExportSession
    {
        public readonly List<int> Frames = new();
        public bool Cancelled;
        public int ManagedWrites, NativeWrites;
        public readonly ManualResetEventSlim AllowNativeWrite = new(true);
        public readonly TaskCompletionSource NativeWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void WriteFrame(byte[] bytes, int length) { ManagedWrites++; lock (Frames) Frames.Add(bytes[0]); }
        public void WriteFrame(IntPtr bytes, int length)
        {
            NativeWrites++; NativeWriteEntered.TrySetResult(); AllowNativeWrite.Wait();
            if (Cancelled) throw new OperationCanceledException();
            lock (Frames) Frames.Add(Marshal.ReadByte(bytes));
        }
        public void Cancel() { Cancelled = true; AllowNativeWrite.Set(); }
    }
}

namespace UnityEngine
{
    public enum HideFlags { HideAndDontSave }
    public enum RenderTextureFormat { ARGB32 }
    public enum TextureFormat { RGBA32 }
    public readonly struct Rect { public Rect(float x, float y, float width, float height) { } }
    public class Texture { public int width, height; }
    public sealed class RenderTexture : Texture
    {
        public static readonly List<RenderTexture> Created = new();
        public static RenderTexture? active;
        public HideFlags hideFlags;
        public int Marker;
        public bool Released;
        public RenderTexture(int width, int height, int depth, RenderTextureFormat format)
        { this.width = width; this.height = height; Created.Add(this); }
        public bool Create() => true;
        public void Release()
        {
            if (Rendering.AsyncGPUReadback.Requests.Any(r => r.Target == this && !r.Done))
                throw new InvalidOperationException("freed a target while GPU still reads it");
            Released = true;
        }
    }
    public sealed class Texture2D : Texture
    {
        public HideFlags hideFlags;
        private byte[] _pixels;
        public Texture2D(int width, int height, TextureFormat format, bool mip)
        { this.width = width; this.height = height; _pixels = new byte[width * height * 4]; }
        public void ReadPixels(Rect rect, int x, int y, bool mip) => Array.Fill(_pixels, (byte)RenderTexture.active!.Marker);
        public byte[] GetRawTextureData() => _pixels;
    }
    public static class Object
    {
        public static readonly List<object> Destroyed = new();
        public static void Destroy(object value) => Destroyed.Add(value);
    }
    public static class Debug
    {
        public static readonly List<string> Warnings = new();
        public static void LogWarning(string value) { Warnings.Add(value); Console.WriteLine(value); }
    }
    public static class SystemInfo { public static bool supportsAsyncGPUReadback = true; }
}

namespace UnityEngine.Rendering
{
    public sealed class RequestState
    {
        public RenderTexture Target = null!;
        public bool Done, Error, WaitLeavesPending;
        public int Size, Waits;
        public int? AvailableFrame;
        public IntPtr Data;
    }
    public struct AsyncGPUReadbackRequest
    {
        private readonly RequestState _state;
        public AsyncGPUReadbackRequest(RequestState state) => _state = state;
        public bool done => _state.Done;
        public bool hasError => _state.Error || (_state.AvailableFrame.HasValue && AsyncGPUReadback.Frame > _state.AvailableFrame.Value);
        public int layerCount => 1;
        public int layerDataSize => _state.Size;
        public void WaitForCompletion() { _state.Waits++; if (!_state.WaitLeavesPending) _state.Done = true; }
        public IntPtr GetDataRaw(int layer)
        {
            if (!_state.Done || hasError) throw new InvalidOperationException("read unfinished or expired request");
            return _state.Data;
        }
    }
    public static class AsyncGPUReadback
    {
        public static int Frame;
        public static readonly List<RequestState> Requests = new();
        public static AsyncGPUReadbackRequest Request(Texture texture, int mip, TextureFormat format, object? callback)
        {
            var target = (RenderTexture)texture;
            if (Requests.Any(r => r.Target == target && !r.Done)) throw new InvalidOperationException("target reused while pending");
            int bytes = target.width * target.height * 4;
            var data = Marshal.AllocHGlobal(bytes);
            var source = Enumerable.Repeat((byte)target.Marker, bytes).ToArray();
            Marshal.Copy(source, 0, data, bytes);
            var state = new RequestState { Target = target, Size = bytes, Data = data };
            Requests.Add(state);
            return new AsyncGPUReadbackRequest(state);
        }
        public static void Reset()
        {
            Frame = 0;
            foreach (var request in Requests) Marshal.FreeHGlobal(request.Data);
            Requests.Clear();
            RenderTexture.Created.Clear();
            UnityEngine.Object.Destroyed.Clear();
            Debug.Warnings.Clear();
            SystemInfo.supportsAsyncGPUReadback = true;
        }
    }
}

public static class Il2CppBulkCopy
{
    public static void Copy(byte[] raw, byte[] dest, int offset, int length) => Array.Copy(raw, 0, dest, offset, length);
}
