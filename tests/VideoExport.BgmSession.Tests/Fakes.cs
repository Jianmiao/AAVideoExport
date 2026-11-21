using UnityEngine;

namespace Il2CppSystem
{
    internal class Object
    {
        private static long next;
        public IntPtr Pointer { get; } = new(++next);
        public T Cast<T>() where T : class => (this as T)!;
    }
    internal sealed class Action<T>(System.Action<T> action) : Object { public void Invoke(T value) => action(value); }
}
namespace Il2CppInterop.Runtime
{
    internal static class DelegateSupport
    {
        internal static bool FailNext, ReturnNullNext;
        public static T ConvertDelegate<T>(System.Delegate value) where T : class
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("Delegate conversion failed before native dispatch."); }
            if (ReturnNullNext) { ReturnNullNext = false; return null!; }
            return (T)Activator.CreateInstance(typeof(T), value)!;
        }
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    internal sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class HarmonyPriority : Attribute { public HarmonyPriority(int value) { } }
    internal static class Priority { public const int Last = 0; }
}
namespace AAVideoExport.Core
{
    internal sealed class ExportException(string code, string message) : Exception(message) { public string Code { get; } = code; }
}
namespace UnityEngine
{
    internal sealed class AsyncOperation : Il2CppSystem.Object { }
    internal class Object : Il2CppSystem.Object { }
    internal enum AudioDataLoadState { Unloaded, Loading, Loaded, Failed }
    internal sealed class AudioClip : Object
    {
        public string name = "Theme_12";
        public int samples = 5147674, frequency = 44100, channels = 2;
        public AudioDataLoadState loadState = AudioDataLoadState.Unloaded;
        public bool LoadSucceeds = true, CompleteLoad = true;
        public int LoadCalls;
        public bool LoadAudioData() { LoadCalls++; if (!LoadSucceeds) return false; loadState = CompleteLoad ? AudioDataLoadState.Loaded : AudioDataLoadState.Loading; return true; }
    }
    internal static class Time { public static float realtimeSinceStartup; }
}
namespace UnityEngine.Networking
{
    internal sealed class UnityWebRequest : Il2CppSystem.Object
    {
        internal enum Result { InProgress, Success, ConnectionError, ProtocolError, DataProcessingError }
        public bool isDone;
        public Result result;
    }
}
internal sealed class Test : Il2CppSystem.Object { }
internal sealed class GenericScenarioExcel : Il2CppSystem.Object
{
    public long BGMId;
    public Type GetIl2CppType() => typeof(GenericScenarioExcel);
}
internal sealed class GenericScenarioExcelTable(params long[] ids)
{
    public object ByteBuffer { get; } = new();
    private readonly GenericScenarioExcel[] rows = ids.Select(id => new GenericScenarioExcel { BGMId = id }).ToArray();
    public int DataListLength => rows.Length;
    public Il2CppSystem.Object DataList(int index) => rows[index];
}
internal sealed class AudioManager
{
    internal sealed class BGMInfo : Il2CppSystem.Object
    {
        public long id;
        public string path = "bgms/Theme_12", name = "Theme_12";
        public bool isOverride;
        public float volume = 1, offset, loopInTime, loopOutTime, transitionTime = .2f;
    }
}
internal sealed class ScenarioResourceManager : Il2CppSystem.Object
{
    public static ScenarioResourceManager? Instance;
    public readonly Dictionary<long, AudioManager.BGMInfo> Infos = new();
    public readonly Dictionary<long, AudioClip> Clips = new();
    public readonly Dictionary<IntPtr, int> References = new();
    public readonly Queue<(AudioManager.BGMInfo Info, Il2CppSystem.Action<AudioClip> Callback)> Pending = new();
    public readonly Dictionary<long, AudioClip> _loadedCustomBgms = new();
    public int Loads, Releases, CustomReleases, ReleaseFailuresRemaining, ReleaseAttempts;
    public bool ReleaseThrowsAfterEffect;
    public bool DispatchThrowsBeforeSchedule, DispatchThrowsAfterSchedule;
    public Action? BeforeCustomCachePublish;
    public AudioManager.BGMInfo GetBGM(long id)
    {
        if (!Infos.TryGetValue(id, out var value)) return null!;
        return new() { id = value.id, path = value.path, name = value.name, isOverride = value.isOverride,
            volume = value.volume, offset = value.offset, loopInTime = value.loopInTime, loopOutTime = value.loopOutTime, transitionTime = value.transitionTime };
    }
    public void GetBgmClipAsync(AudioManager.BGMInfo info, Il2CppSystem.Action<AudioClip> callback)
    {
        Loads++;
        if (DispatchThrowsBeforeSchedule) throw new InvalidOperationException("Native dispatch threw before scheduling.");
        if (info.isOverride && _loadedCustomBgms.TryGetValue(info.id, out var cached)) callback.Invoke(cached);
        else Pending.Enqueue((info, callback));
        if (DispatchThrowsAfterSchedule) throw new InvalidOperationException("Native dispatch threw after scheduling.");
    }
    public void CompleteNext(bool customSuccess = true)
    {
        var load = Pending.Dequeue(); var clip = Clips[load.Info.id];
        if (!load.Info.isOverride) References[clip.Pointer] = References.GetValueOrDefault(clip.Pointer) + 1;
        if (!load.Info.isOverride) { load.Callback.Invoke(clip); return; }
        var completion = new __c__DisplayClass100_1
        {
            field_Public___c__DisplayClass100_0_0 = new() { info = load.Info, callback = load.Callback, __4__this = this },
            req = new() { isDone = true, result = customSuccess ? UnityEngine.Networking.UnityWebRequest.Result.Success : UnityEngine.Networking.UnityWebRequest.Result.ConnectionError }
        };
        Exception? error = null;
        try
        {
            if (customSuccess)
            {
                load.Callback.Invoke(clip);
                BeforeCustomCachePublish?.Invoke();
                // The native loader publishes only after our callback returns.
                _loadedCustomBgms[load.Info.id] = clip;
            }
            // The native failure branch logs/notifies without invoking callback.
        }
        catch (Exception caught) { error = caught; throw; }
        finally { ObserveCustomCompletion(completion, error); }
    }
    public static void ObserveCustomCompletion(__c__DisplayClass100_1 completion, Exception? error = null) =>
        typeof(AAVideoExport.Plugin.NativeBgmSessionAdapter).GetMethod("ObserveCustomCompletion",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, new object?[] { completion, error });
    internal sealed class __c__DisplayClass100_0 : Il2CppSystem.Object
    {
        public AudioManager.BGMInfo? info;
        public ScenarioResourceManager? __4__this;
        public Il2CppSystem.Action<AudioClip>? callback;
    }
    internal sealed class __c__DisplayClass100_1 : Il2CppSystem.Object
    {
        public UnityEngine.Networking.UnityWebRequest? req;
        public __c__DisplayClass100_0? field_Public___c__DisplayClass100_0_0;
        public void _GetBgmClipAsync_b__1(AsyncOperation _) { }
    }
    public void CompleteAll() { while (Pending.Count > 0) CompleteNext(); }
    public void ReleaseAsset(UnityEngine.Object asset)
    {
        ReleaseAttempts++;
        bool fail = ReleaseFailuresRemaining > 0;
        if (fail) ReleaseFailuresRemaining--;
        if (fail && !ReleaseThrowsAfterEffect)
        {
            throw new InvalidOperationException("Native release failed before changing ownership.");
        }
        Releases++;
        if (_loadedCustomBgms.Values.Any(x => x.Pointer == asset.Pointer)) { CustomReleases++; throw new Exception("Adapter destroyed a native custom cache asset."); }
        References[asset.Pointer] = References.GetValueOrDefault(asset.Pointer) - 1;
        if (References[asset.Pointer] < 0) throw new Exception("Asset reference was released twice.");
        if (fail) throw new InvalidOperationException("Native release failed after changing ownership.");
    }
}
namespace AAVideoExport.Plugin
{
    internal sealed class ExportHost(Test player)
    {
        public static ExportHost? Current;
        public bool Capturing = true;
        public bool OwnsCapturePlayer(Test other) => Capturing && player.Pointer == other.Pointer;
    }
}
