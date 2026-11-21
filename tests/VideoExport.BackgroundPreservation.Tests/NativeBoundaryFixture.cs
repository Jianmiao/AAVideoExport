using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Managed-only state fixture. The production adapter source is compiled
// unchanged; these boundaries model the transitions independently observed in
// native runs 44/45. This does not claim a real Unity decoder or native acceptance.
namespace UnityEngine
{
    public class Object
    {
        public IntPtr Pointer { get; protected set; }
        public static T[] FindObjectsOfType<T>() => Array.Empty<T>();
        public static void Destroy(object? value) { }
        public static class MarshalledUnityObject
        { public static IntPtr MarshalNotNull<T>(T value) where T : Object => value.Pointer; }
    }
    public sealed class GameObject
    {
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf;
        public void SetActive(bool value) => activeSelf = value;
    }
    public static class Time { public static float realtimeSinceStartup; }
}

namespace UnityEngine.Video
{
    public enum VideoSource { VideoClip, Url }
    public sealed class FixtureTarget { public IntPtr Pointer { get; } = new(4200); }
    public sealed class VideoPlayer : UnityEngine.Object, IDisposable
    {
        private readonly GCHandle handle;
        public readonly List<string> Calls = new();
        public Action<VideoPlayer>? AdvanceBeforePause;
        public bool ThrowAfterPause;
        public readonly UnityEngine.GameObject gameObject = new();
        public bool Prepared = true, Playing, Paused, CanSeek = true, Looping = true;
        public double time = .3;
        public long frame = 3;
        public float frameRate = 10, playbackSpeed = 1;
        public VideoSource source = VideoSource.Url;
        public string url = "fixture://original-video";
        public FixtureTarget targetTexture = new();
        public EventHandler? prepareCompleted, seekCompleted;
        public ErrorEventHandler? errorReceived;
        public VideoPlayer() { handle = GCHandle.Alloc(this); Pointer = GCHandle.ToIntPtr(handle); }
        public void Pause() { Calls.Add("Pause"); AdvanceBeforePause?.Invoke(this); Playing = false; Paused = true; if (ThrowAfterPause) throw new InvalidOperationException("fixture pause completion failure"); }
        public void Play() { Calls.Add("Play"); Playing = Prepared; Paused = false; }
        public void Stop() { Calls.Add("Stop"); Prepared = Playing = Paused = false; time = 0; frame = -1; }
        public void Prepare() { Calls.Add("Prepare"); Prepared = false; }
        public void CompletePreparation() { Prepared = true; prepareCompleted?.Invoke(this); }
        public void Dispose() { if (handle.IsAllocated) handle.Free(); }
        internal static VideoPlayer Get(IntPtr pointer) => (VideoPlayer)GCHandle.FromIntPtr(pointer).Target!;

        public sealed class EventHandler
        {
            private readonly Action<VideoPlayer>? callback;
            public int SubscriberCount => callback?.GetInvocationList().Length ?? 0;
            private EventHandler(Action<VideoPlayer>? value) => callback = value;
            public static implicit operator EventHandler(Action<VideoPlayer> callback) => new(callback);
            public static EventHandler? operator +(EventHandler? left, EventHandler? right) => new(left?.callback + right?.callback);
            public static EventHandler? operator -(EventHandler? left, EventHandler? right) => new(left?.callback - right?.callback);
            public void Invoke(VideoPlayer player) => callback?.Invoke(player);
        }
        public sealed class ErrorEventHandler
        {
            private readonly Action<VideoPlayer, string>? callback;
            public int SubscriberCount => callback?.GetInvocationList().Length ?? 0;
            private ErrorEventHandler(Action<VideoPlayer, string>? value) => callback = value;
            public static implicit operator ErrorEventHandler(Action<VideoPlayer, string> callback) => new(callback);
            public static ErrorEventHandler? operator +(ErrorEventHandler? left, ErrorEventHandler? right) => new(left?.callback + right?.callback);
            public static ErrorEventHandler? operator -(ErrorEventHandler? left, ErrorEventHandler? right) => new(left?.callback - right?.callback);
            public void Invoke(VideoPlayer player, string message) => callback?.Invoke(player, message);
        }

        // Match the exact generated binding shape required by the production
        // reader; these fixture pointers are local callbacks, not Unity icalls.
        private static readonly FixtureIcalls.ByteGetter get_isPrepared_InjectedDelegateField = FixtureIcalls.Get("isPrepared");
        private static readonly FixtureIcalls.ByteGetter get_isPlaying_InjectedDelegateField = FixtureIcalls.Get("isPlaying");
        private static readonly FixtureIcalls.ByteGetter get_isPaused_InjectedDelegateField = FixtureIcalls.Get("isPaused");
        private static readonly FixtureIcalls.ByteGetter get_canSetTime_InjectedDelegateField = FixtureIcalls.Get("canSetTime");
        private static readonly FixtureIcalls.ByteGetter get_isLooping_InjectedDelegateField = FixtureIcalls.Get("isLooping");
    }
    internal static class FixtureIcalls
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate byte ByteGetter(IntPtr value);
        private static readonly Dictionary<string, ByteGetter> Getters = new()
        { ["isPrepared"] = Prepared, ["isPlaying"] = Playing, ["isPaused"] = Paused, ["canSetTime"] = CanSeek, ["isLooping"] = Looping };
        internal static ByteGetter Get(string name) => Getters[name];
        internal static IntPtr Resolve(string name) => Marshal.GetFunctionPointerForDelegate(Get(name));
        private static byte Prepared(IntPtr value) => VideoPlayer.Get(value).Prepared ? (byte)1 : (byte)0;
        private static byte Playing(IntPtr value) => VideoPlayer.Get(value).Playing ? (byte)1 : (byte)0;
        private static byte Paused(IntPtr value) => VideoPlayer.Get(value).Paused ? (byte)1 : (byte)0;
        private static byte CanSeek(IntPtr value) => VideoPlayer.Get(value).CanSeek ? (byte)1 : (byte)0;
        private static byte Looping(IntPtr value) => VideoPlayer.Get(value).Looping ? (byte)1 : (byte)0;
    }
}

namespace Il2CppInterop.Runtime
{
    internal static class IL2CPP
    {
        internal static IntPtr il2cpp_resolve_icall(string method)
        {
            const string prefix = "UnityEngine.Video.VideoPlayer::get_";
            if (!method.StartsWith(prefix, StringComparison.Ordinal) || !method.EndsWith("_Injected", StringComparison.Ordinal))
                throw new NotSupportedException(method);
            return UnityEngine.Video.FixtureIcalls.Resolve(method[prefix.Length..^9]);
        }
    }
}
