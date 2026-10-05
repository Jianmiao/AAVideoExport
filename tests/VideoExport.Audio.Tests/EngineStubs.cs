// Minimal boundary doubles, deliberately not an alternative capture algorithm.
namespace UnityEngine
{
    internal static class Object
    {
        public static T[] FindObjectsOfType<T>(bool includeInactive) => Array.Empty<T>();
    }
    internal sealed class GameObject
    {
        public int scene = 1;
        public bool activeSelf = true;
        private GameObject? _parent;
        private readonly List<GameObject> _children = new();
        public GameObject? parent
        {
            get => _parent;
            set
            {
                _parent?._children.Remove(this);
                _parent = value;
                _parent?._children.Add(this);
            }
        }
        public bool activeInHierarchy => activeSelf && (parent?.activeInHierarchy ?? true);
        public UIWidget[] widgets = Array.Empty<UIWidget>();
        public Transform transform = new();
        public int WidgetScans;
        public int SetActiveCalls;
        public void SetActive(bool active) { SetActiveCalls++; activeSelf = active; }
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : class
        {
            WidgetScans++;
            return DescendantWidgets(includeInactive, isRoot: true).OfType<T>().ToArray();
        }
        private IEnumerable<UIWidget> DescendantWidgets(bool includeInactive, bool isRoot = false)
        {
            if (!isRoot && !includeInactive && !activeInHierarchy) yield break;
            foreach (var widget in widgets) yield return widget;
            foreach (var child in _children)
                foreach (var widget in child.DescendantWidgets(includeInactive)) yield return widget;
        }
    }
    internal sealed class Transform
    {
        public Vector3 localPosition = new(0, 0, 0);
    }
    internal struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) => (this.x, this.y, this.z) = (x, y, z);
    }
    internal class Component { public GameObject gameObject = new(); }
    internal sealed class RenderTexture
    {
        public int width = 1920, height = 1080;
        public RenderTexture() { }
        public RenderTexture(int width, int height, int depth, object format) => (this.width, this.height) = (width, height);
    }
    internal readonly record struct Rect(float X, float Y, float Width, float Height);
    internal sealed class Camera
    {
        public static Camera[] allCameras = Array.Empty<Camera>();
        public static Camera? main;
        public bool enabled = true;
        public GameObject gameObject = new();
        public RenderTexture targetTexture = null!;
        public float depth = 0;
        public float aspect = 16f / 9;
        public Rect rect = new(0, 0, 1, 1);
        public Action? OnRender;
        public T? GetComponent<T>() where T : class => null;
        public void Render() => OnRender?.Invoke();
    }
    internal static class Application
    {
        public static int targetFrameRate;
        public static bool runInBackground;
    }
    internal static class Screen { public static int width = 1920; public static int height = 1080; }
    internal static class QualitySettings { public static int vSyncCount; }
    internal static class Time
    {
        public static float captureDeltaTime;
        public static float timeScale;
        public static int captureFramerate { set => captureDeltaTime = value == 0 ? 0 : 1f / value; }
    }
    internal enum AudioSpeakerMode { Mono, Stereo, Quad, Surround, Mode5point1, Mode7point1 }
    internal static class AudioSettings
    {
        public static int outputSampleRate;
        public static AudioSpeakerMode speakerMode;
    }
    internal static class AudioRenderer
    {
        public static bool StartResult = true;
        public static bool RenderResult = true;
        public static int SampleCount;
        public static int LastLength;
        public static int RenderCalls;
        public static int StartCalls;
        public static int StopCalls;
        public static bool Start() { StartCalls++; return StartResult; }
        public static bool Stop() { StopCalls++; return true; }
        public static int GetSampleCountForCaptureFrame() => SampleCount;
        public static unsafe bool Internal_AudioRenderer_Render(float* buffer, int length)
        {
            RenderCalls++;
            LastLength = length;
            if (length > 0) { buffer[0] = 0.25f; buffer[length - 1] = 0.5f; }
            return RenderResult;
        }
        public static void Reset()
        {
            StartResult = RenderResult = true;
            SampleCount = LastLength = RenderCalls = StartCalls = StopCalls = 0;
        }
    }
    internal static class Debug
    {
        public static void Log(string message) => Console.WriteLine(message);
        public static void LogWarning(string message) => Console.Error.WriteLine(message);
    }
}
namespace AAVideoExport.Plugin
{
    // Layout has a separate native integration harness; this fake observes its
    // lifetime without duplicating the NGUI/Screen implementation.
    internal sealed class NativeExportViewport : IDisposable
    {
        public NativeExportViewport(Test player, IReadOnlyList<UnityEngine.Camera> cameras, int width, int height) { }
        public void RefreshLayout() { }
        public void Dispose() { }
    }
}
namespace UnityEngine.Rendering.Universal
{
    internal enum CameraRenderType { Base, Overlay }
    internal sealed class UniversalAdditionalCameraData { public CameraRenderType renderType = CameraRenderType.Base; }
}
namespace UnityEngine.Rendering
{
    internal static class GraphicsSettings { public static object? currentRenderPipeline = new object(); }
}
namespace UI
{
    internal sealed class MenuButton : UnityEngine.Component
    {
        public UnityEngine.GameObject menuPopup = new();
    }
}
internal sealed class Test
{
    public UnityEngine.GameObject gameObject = new();
    public UnityEngine.GameObject menu = new();
    public UI.MenuButton menuBtn = new();
    public UnityEngine.Component autoToggle = new();
    public SelectionManager selectionManager = new();
    public bool auto;
    public Test()
    {
        // AA's Test.menu references Top, the shared parent of both controls.
        // MenuPopup is their sibling, referenced separately by UI.MenuButton.
        menu.parent = gameObject;
        menuBtn.gameObject.parent = menu;
        autoToggle.gameObject.parent = menu;
        menuBtn.menuPopup.parent = menu;
    }
}
internal sealed class SelectionManager
{
    public bool AutoEnabled;
    public int AutoModeChanges;
    public void OnAutoModeChanged(bool auto) { AutoModeChanges++; AutoEnabled = auto; }
}
internal sealed class UIWidget { public UIPanel? panel; }
internal sealed class TouchEffect : UnityEngine.Component { }
internal sealed class UICursor : UnityEngine.Component
{
    public static UICursor? instance;
}
internal sealed class UIPanel
{
    public Action? OnRefresh;
    public int RefreshCalls;
    public void Refresh() { RefreshCalls++; OnRefresh?.Invoke(); }
}
internal sealed class ScenarioResourceManager
{
    public static ScenarioResourceManager? Instance = null;
    public bool Preloading = false;
}
