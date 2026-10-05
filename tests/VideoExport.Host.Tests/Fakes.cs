// Deliberately replaces native AA/Unity and encoding boundaries only. The host,
// launch gate, options, and UI state machine above are production source links.
using System.Collections;
using AAVideoExport.Core;

namespace UnityEngine
{
    public class Object
    {
        private static readonly List<Object> Objects = new();
        public Object() => Objects.Add(this);
        public static T[] FindObjectsOfType<T>() => Objects.OfType<T>().ToArray();
        public static void ResetRegistry() => Objects.Clear();
    }
    public class GameObject : Object { public bool activeInHierarchy = true; }
    public class MonoBehaviour : Object
    {
        public MonoBehaviour() { }
        public MonoBehaviour(IntPtr pointer) => Pointer = pointer;
        public IntPtr Pointer { get; set; } = new(1);
        public GameObject gameObject = new();
        public Coroutine StartCoroutine(Il2CppSystem.Collections.IEnumerator iterator) => new();
        public void StopCoroutine(Coroutine coroutine) { }
    }
    public class Coroutine { }
    public class Camera { }
    public class RenderTexture { public int width = 1920, height = 1080; }
    public class WaitForEndOfFrame { }
    public enum KeyCode { LeftControl, LeftShift, E }
    public static class Input { public static bool GetKey(KeyCode key) => false; public static bool GetKeyDown(KeyCode key) => false; }
    public static class SystemInfo { public static string graphicsDeviceName = "fixture GPU"; public static int maxTextureSize = 16384; public static int graphicsDeviceVendorID; public static int graphicsDeviceID; }
    public static class Screen { public static int width = 1920, height = 1080; }
    public static class Time { public static int frameCount; public static float captureDeltaTime; }
    public static class Application { public static int targetFrameRate; public static bool isFocused = true; }
    public static class QualitySettings { public static int vSyncCount; }
    public enum AudioSpeakerMode { Mono, Stereo, Quad, Surround, Mode5point1, Mode7point1 }
    public static class AudioSettings { public static int outputSampleRate = 48000; public static AudioSpeakerMode speakerMode = AudioSpeakerMode.Stereo; }
    public static class Debug
    {
        public static readonly List<string> Messages = new();
        public static void Log(object message) => Messages.Add(message.ToString()!);
        public static void LogWarning(object message) => Messages.Add(message.ToString()!);
        public static void LogError(object message) => Messages.Add(message.ToString()!);
    }
}
namespace UnityEngine.Rendering { public static class GraphicsSettings { public static object? currentRenderPipeline; } }
namespace UnityEngine.Rendering
{
    public static class OnDemandRendering { public static int renderFrameInterval = 1; }
    public struct ScriptableRenderContext { }
    public static class RenderPipelineManager
    {
        public static void add_endFrameRendering(Il2CppSystem.Action<ScriptableRenderContext, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Camera>> callback) { }
        public static void remove_endFrameRendering(Il2CppSystem.Action<ScriptableRenderContext, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Camera>> callback) { }
    }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { public string name; public bool isLoaded; }
    public static class SceneManager
    {
        public static Scene Active = new() { name = "CatalogScene", isLoaded = true };
        public static Scene GetActiveScene() => Active;
    }
}
namespace Il2CppInterop.Runtime
{
    public class Placeholder { }
    public static class DelegateSupport
    {
        public static T ConvertDelegate<T>(Delegate source) where T : Delegate =>
            (T)Delegate.CreateDelegate(typeof(T), source.Target, source.Method);
    }
}
namespace Il2CppInterop.Runtime.Attributes { public class HideFromIl2CppAttribute : Attribute { } }
namespace Il2CppInterop.Runtime.InteropTypes.Arrays { public class Placeholder { } }
namespace Il2CppInterop.Runtime.InteropTypes.Arrays { public class Il2CppReferenceArray<T> { } }
namespace Il2CppSystem
{
    public delegate void Action<T1, T2>(T1 first, T2 second);
}
namespace Il2CppSystem.Collections { public interface IEnumerator { } }
namespace BepInEx.Unity.IL2CPP.Utils.Collections
{
    public static class CollectionExtensions
    {
        private sealed class Wrapped : Il2CppSystem.Collections.IEnumerator { }
        public static Il2CppSystem.Collections.IEnumerator WrapToIl2Cpp(IEnumerator iterator) => new Wrapped();
    }
}
namespace Studio.Scripts
{
    public sealed class StudioCommon { public static StudioCommon? instance; public string projectName = ""; }
}
public sealed class CatalogBlock { public string path = ""; }
public sealed class CatalogFileInfo : UnityEngine.MonoBehaviour
{
    public CatalogBlock? block;
    public UnityEngine.GameObject? controlPanel;
    public int LoadCalls;
    public Il2CppSystem.Collections.IEnumerator CoLoadSave()
    {
        LoadCalls++;
        return BepInEx.Unity.IL2CPP.Utils.Collections.CollectionExtensions.WrapToIl2Cpp(Array.Empty<object>().GetEnumerator());
    }
}
public sealed class Test : UnityEngine.MonoBehaviour
{
    public bool hasSelection, IsAutoEnabled = true;
    public int cur;
    public SelectionManager? selectionManager;
    public bool previewMode;
    public object? scn = new();
    public int EndCalls;
    public Action? EndAction;
    public void End() { EndCalls++; EndAction?.Invoke(); }
}
public sealed class SelectionManager : UnityEngine.MonoBehaviour
{
    public bool isSelectionActive, autoModeEnabled = true;
    public int defaultSelectionIndex = -1;
    public float autoSelectDelaySeconds = 2;
    public List<SelectionElement>? elements = new();
}
public sealed class SelectionElement : UnityEngine.MonoBehaviour
{
    public UI.MXButton? button = new();
    public void OnSelect() { if (button != null) button.disabled = true; }
}
namespace UI { public sealed class MXButton : UnityEngine.MonoBehaviour { public bool disabled; } }
public sealed class GenericScenarioExcelTable { }
public static class PersistentData { public static GenericScenarioExcelTable? saveData; public static string projectPath = ""; }
public sealed class UserSettings { public static UserSettings? Instance; public string WorkspacePath = ""; }
public sealed class ScenarioResourceManager
{
    public static ScenarioResourceManager? Instance;
    // Native AA falls back to Application.persistentDataPath for an unset workspace.
    public static string PersistentDataPath = Path.GetTempPath();
    public static string GetPersistentFilePath(string folderName, string fileName = "", string extension = ".aap2")
    {
        string workspace = UserSettings.Instance?.WorkspacePath ?? "";
        if (string.IsNullOrEmpty(workspace)) workspace = PersistentDataPath;
        return Path.Combine(workspace, "data", folderName, fileName + extension).Replace('\\', '/');
    }
    public bool Preloading;
    public readonly List<string> LoadedNames = new();
    public GenericScenarioExcelTable LoadGenericScenario(string name) { LoadedNames.Add(name); return new(); }
}
public static class RealTime { public static float time = 100; }
public sealed class UITweener : UnityEngine.Object { public bool ignoreTimeScale, mStarted; public float mStartTime; }
public static class UICamera { public static bool ignoreAllEvents; }

namespace AAVideoExport.Core
{
    public sealed class FfmpegCapabilities
    {
        public IReadOnlyList<EncoderCapability> HardwareEncoders { get; init; } = new[] { new EncoderCapability("fixture", "h264", true, true, "fake native boundary") };
        public IReadOnlyList<EncoderCapability> SoftwareEncoders { get; init; } = new[] { new EncoderCapability("libx264", "h264", false, true, "fake native boundary") };
        public static Func<string, CancellationToken, Task<FfmpegCapabilities>>? ProbeOverride;
        public static Task<FfmpegCapabilities> ProbeHardwareAsync(string path, string gpu, CancellationToken cancellation) =>
            ProbeOverride?.Invoke("hardware", cancellation) ?? Task.FromResult(new FfmpegCapabilities());
        public static Task<FfmpegCapabilities> ProbeSoftwareAsync(string path, CancellationToken cancellation) =>
            ProbeOverride?.Invoke("software", cancellation) ?? Task.FromResult(new FfmpegCapabilities());
        public string PickForMode(ExportOptions options) =>
            (options.EncodingMode == "software" ? SoftwareEncoders : HardwareEncoders)
                .First(e => e.Codec == options.Codec && (options.Encoder == "auto" || options.Encoder == e.Name)).Name;
    }
    public sealed class ExportSession : IDisposable
    {
        public static readonly List<ExportSession> Created = new();
        public long FramesWritten, AudioSampleFrames;
        public long FramesAccepted => FramesWritten;
        public int UpscaleReadbackFrames => Options.Direct3DReadbackFrames;
        public string UpscaleExecution => Options.SuperResolutionEnabled ? "fixture" : "none";
        public string UpscaleAdapter => "fixture";
        public string UpscaleFallbackReason => "";
        public bool Cancelled, Disposed;
        public ExportOptions Options;
        public string Encoder;
        public ExportSession(ExportOptions options, string ffmpeg, string ffprobe, string encoder, int sampleRate, int channels, bool flip)
        { Options = options; Encoder = encoder; lock (Created) Created.Add(this); }
        public void Cancel() => Cancelled = true;
        public void Dispose() => Disposed = true;
        public void WriteAudio(float[] audio, int count) { }
        public ExportResult Complete(CancellationToken token) => new(Path.Combine(Options.OutputDirectory, Options.Title + ".mp4"), FramesWritten, 1, "fixture", 1, null);
    }
}
namespace AAVideoExport.Plugin
{
    internal static class MoreEffectsCompatibility
    {
        internal static void TryInstall() { }
        internal static void BeginExport(Func<double> readTime) { }
        internal static void EndExport() { }
    }
    public static class Plugin
    {
        internal static bool UseNativeFrameBuffers = true;
        internal static int Direct3DReadbackFrames = 1;
        public static string FfmpegPath = "unused", FfprobePath = "unused";
        public static bool ReduceProgressUiWork = true, UseAsyncReadback = true;
        public static bool FailPerformanceLog;
        public static string SavedOutputDirectory = "";
        public static string OutputDirectory => OutputDirectoryPreference.Resolve(SavedOutputDirectory);
        public static void RememberOutputDirectory(string value)
        {
            if (OutputDirectoryPreference.TryNormalize(value, out var directory)) SavedOutputDirectory = directory;
        }
        public static void LogPerformance(string message)
        {
            if (FailPerformanceLog) throw new IOException("fixture diagnostic sink failure");
        }
    }
    public sealed class NativeExportPanel : IDisposable
    {
        public ExportOptions Options = new();
        public bool Visible, IsCapturing, ReduceCaptureUiWork, ShowPlaybackButtons;
        public string LastProjectName = "";
        public double CaptureElapsedSeconds;
        public long EncodedFrames, ControlRefreshCount, CameraScanCount;
        public string ExportStage = "";
        public Action? ProbeRequested, CancelRequested;
        public Action<ExportOptions>? ExportRequested;
        public Action<string>? OutputDirectoryCommitted;
        public readonly ExportUiFlow Flow = new();
        public bool LastBusy;
        public IReadOnlyList<EncoderCapability>? LastEncoders;
        public string LastStatus = "";
        public void Draw(string title, string gpu, string status, bool busy, long captured, long total, object? progress, IReadOnlyList<EncoderCapability>? hardware)
        { LastProjectName = title; LastBusy = busy; LastEncoders = hardware; LastStatus = status; Flow.ObserveBusy(busy); }
        public void SetSourceCanvas(int width, int height) { }
        public void SetStoryTitle(string title) { Options = Options with { Title = title }; }
        public void Tick() { }
        public void Dispose() { }
        public void ReleaseIdleBudget() { }
        public void ResetPerformanceCounters() { }
        public void ExcludeFrom(UnityEngine.Camera camera) { }
    }
    public sealed class CatalogExportAction : IDisposable
    {
        public CatalogExportAction(Action<CatalogFileInfo> open) { }
        public void Tick(bool hidden) { }
        public void Dispose() { }
    }
    public sealed class NativeCaptureScope : IDisposable
    {
        public long VisibilityTicks, CameraRenderTicks, PanelRefreshCount;
        public bool AutomaticCameraCapture;
        public bool Disposed;
        public bool RestorationFailed;
        public UnityEngine.Camera LastCamera { get; } = new();
        public NativeCaptureScope(Test player, ExportOptions options, UnityEngine.RenderTexture target, bool showButtons, bool automaticCapture = true) { AutomaticCameraCapture = automaticCapture; }
        public void Dispose() => Disposed = true;
        public void PrepareAutomaticFrame() { }
        public void RestoreAutomaticFrame() { }
        public void SetTarget(UnityEngine.RenderTexture target) { }
        public void ExcludeOverlayLayer(Action<UnityEngine.Camera> exclude) { }
        public void RenderFrame(UnityEngine.RenderTexture target) { }
        public (float[] Samples, int Count) RenderAudio() => (Array.Empty<float>(), 0);
    }
    public sealed class FramePipeline : IDisposable
    {
        public bool NativeBufferQueue => true;
        public int AllocatedNativeBuffers => 0;
        public FramePipeline(ExportOptions options, ExportSession session, bool useAsync, bool legacySingleTarget = false, bool nativeBuffers = true) { }
        private readonly UnityEngine.RenderTexture _target = new();
        public bool RejectTargetAccess, Disposed;
        public int TargetReads;
        public Exception? DrainError;
        public UnityEngine.RenderTexture Target => RenderTarget;
        public UnityEngine.RenderTexture RenderTarget
        {
            get
            {
                TargetReads++;
                if (RejectTargetAccess) throw new ExportException("gpu_readback_failed", "fixture pending readback failure");
                return _target;
            }
        }
        public long ReadbackTicks, RawDataTicks, CopyTicks, QueueTicks, ReadbackWaitTicks, EncoderTicks;
        public bool AsyncReadback = true;
        public int BufferedFrames, PendingReadbacks, QueueCapacity = 6, TargetCount = 6;
        public int CaptureWidth = 1920, CaptureHeight = 1080;
        public int PeakBufferedFrames, PeakPendingReadbacks;
        public Task WriterCompletion => Task.CompletedTask;
        public Task FinishFrames() => Task.CompletedTask;
        public void Drain(bool wait) { if (DrainError != null) throw DrainError; }
        public void Capture() { }
        public void Dispose() => Disposed = true;
    }
    public static class NativeExportClock
    {
        public static long WaitPollCount, RegisteredWaitCount, UnscaledDeltaReadCount;
        public static int ActiveWaitCount;
        public static int PeakActiveWaitCount;
        public static void Begin(float epoch, float delta, Func<float> clock) { }
        public static void Restore() { }
    }
    internal static class AutoSelectionHooks { internal static void EnsureSupported() { } }
}
