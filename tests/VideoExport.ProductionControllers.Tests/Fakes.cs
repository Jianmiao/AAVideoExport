namespace UnityEngine
{
    public class Object { public System.IntPtr Pointer { get; set; } = new(1); }
    public class MonoBehaviour : Object { public bool isActiveAndEnabled = true; }
    public enum AudioDataLoadState { Unloaded, Loading, Loaded, Failed }
    public class AudioClip : Object
    {
        public AudioDataLoadState loadState = AudioDataLoadState.Loaded; public int samples = 48000, frequency = 48000, channels = 2;
        public bool GetData(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float> values, int offset) => throw new InvalidOperationException("AUDIT PCM READ REACHED IN PRODUCTION");
    }
    public class AudioSource : Object { public AudioClip? clip; public float volume = 1, pitch = 1; }
}
public sealed class Test : UnityEngine.MonoBehaviour
{
    public static Test? Instance; public bool previewMode; public int GetInstanceID() => 1;
}
public sealed class AudioManager : UnityEngine.Object
{
    public static AudioManager? Instance; public UnityEngine.AudioSource voiceSource = new(); public Volume volumeInfo = new();
    public sealed class Volume { public float masterVolume = 1, voiceVolume = 1; }
}
public sealed class UserSettings
{
    public static UserSettings Instance = new(); public Values data = new(); public sealed class Values { public float masterVolume = 1, voiceVolume = 1; }
}
public sealed class ScenarioResourceManager : UnityEngine.Object
{
    public static ScenarioResourceManager? Instance; public object GlobalOverrideData = new(), LocalOverrideData = new(); public bool Preloading;
    public Dictionary<string, UnityEngine.AudioClip> _loadedCustomVoices = new(); public Dictionary<string, string> Paths = new();
    public bool TryGetVoiceOverridePath(string id, out string path) => Paths.TryGetValue(id, out path!);
    public object CoTryPreloadVoice(string id) => new(); public void StartCoroutine(object routine) { }
}
public sealed class ProductionVoiceGate { public bool Active => false; public long RequestSequence => 0; public int Phase => 0; }
namespace FlatData
{
    public sealed class GenericScenarioExcelTable { public object ByteBuffer = new(); public int DataListLength => 1; public GenericScenarioExcel DataList(int index) => new(); }
    public sealed class GenericScenarioExcel
    {
        public string? VoiceJp; public T Cast<T>() where T : class => (this as T)!; public NativeType GetIl2CppType() => new();
        public sealed class NativeType { public string FullName => "GenericScenarioExcel"; }
    }
}
namespace Il2CppInterop.Runtime { public static class Marker { } }
namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public sealed class Il2CppStructArray<T>(T[] values) { public T[] ToArray() => values; }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { public string name; }
    public static class SceneManager { public static Scene GetActiveScene() => new() { name = "MainScene" }; }
}
namespace AAVideoExport.Integration
{
    public static class RenderControlV1 { public static bool IsRenderingOwned = true; public static void EnsureMainThread() { } }
}
