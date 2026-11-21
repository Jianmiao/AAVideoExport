// Deliberately replaces native AA/Unity and encoding boundaries only. The host,
// launch gate, options, and UI state machine above are production source links.
using System.Collections;
using AAVideoExport.Core;
using FlatData;

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
    public static class Time { public static int frameCount; public static float captureDeltaTime; public static float realtimeSinceStartup; }
    public static class Random
    {
        public struct State { public int Seed; }
        public static State state;
        public static void InitState(int seed) => state = new State { Seed = seed };
    }
    public static class Application { public static int targetFrameRate; public static bool isFocused = true; }
    public static class QualitySettings { public static int vSyncCount; }
    public enum AudioSpeakerMode { Mono, Stereo, Quad, Surround, Mode5point1, Mode7point1 }
    public static class AudioSettings { public static int outputSampleRate = 48000; public static AudioSpeakerMode speakerMode = AudioSpeakerMode.Stereo; public static double dspTime = 1000d; }
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
        public static void add_beginFrameRendering(Il2CppSystem.Action<ScriptableRenderContext, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Camera>> callback) { }
        public static void remove_beginFrameRendering(Il2CppSystem.Action<ScriptableRenderContext, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Camera>> callback) { }
        public static void add_beginCameraRendering(Il2CppSystem.Action<ScriptableRenderContext, UnityEngine.Camera> callback) { }
        public static void remove_beginCameraRendering(Il2CppSystem.Action<ScriptableRenderContext, UnityEngine.Camera> callback) { }
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
    public int AdvanceCalls;
    public Action<bool, IScenarioScriptExcel?>? AdvanceAction;
    public void AdvanceScenario(bool preview = false, IScenarioScriptExcel? scriptOverride = null)
    {
        if (!preview)
        {
            if (!(AAVideoExport.Plugin.ExportHost.Current?.BeforePlayerStart(this) ?? true)) return;
            if (!(AAVideoExport.Plugin.ExportHost.Current?.AllowStrictScenarioAdvance(this, scriptOverride) ?? true)) return;
        }
        AdvanceCalls++;
        AdvanceAction?.Invoke(preview, scriptOverride);
    }
    public void End() { EndCalls++; EndAction?.Invoke(); }
}
namespace FlatData { public interface IScenarioScriptExcel { } }
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
public sealed class UserSettings
{
    public static UserSettings? Instance;
    public string WorkspacePath = "";
    public SettingData data = new();
    public sealed class SettingData
    {
        public int autoDelay = 1, fontIndex;
        public float masterVolume = 1, voiceVolume = 1, bgmVolume = 1, soundEffectVolume = 1;
        public bool stEnabled = true;
    }
}
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
        public bool Completed;
        public Action? BeforeOutputCommit;
        public Func<IDisposable?>? AcquireOutputCommitScope;
        public StrictFramePlan? BoundPlan;
        private bool _liveTimeline;
        public readonly List<float> WrittenAudio = new();
        private readonly int _sampleRate, _channels;
        public ExportOptions Options;
        public string Encoder;
        public ExportSession(ExportOptions options, string ffmpeg, string ffprobe, string encoder, int sampleRate, int channels, bool flip)
        { Options = options; Encoder = encoder; _sampleRate = sampleRate; _channels = channels; lock (Created) Created.Add(this); }
        public RuntimeFactoryRecipeReceipt? RuntimeFactoryRecipe { get; set; }
        public void Cancel() => Cancelled = true;
        public void Dispose() => Disposed = true;
        public PreparedAudioTrack CreateAudioPreparationTrack()
        {
            Directory.CreateDirectory(Options.OutputDirectory);
            return new PreparedAudioTrack(Path.Combine(Options.OutputDirectory, ".fixture-prepared-" + Guid.NewGuid().ToString("N")), Options.Fps, _sampleRate, _channels);
        }
        public void BindStrictTimeline(StrictFramePlan plan) => BoundPlan = plan;
        public void BeginLiveTimeline()
        {
            if(_liveTimeline || BoundPlan!=null || FramesAccepted!=0 || AudioSampleFrames!=0) throw new ExportException("invalid_state","Live timeline must begin before output.");
            _liveTimeline=true;
        }
        public void SealLiveTimeline(StrictFramePlan plan)
        {
            if(!_liveTimeline || BoundPlan!=null) throw new ExportException("invalid_state","Live timeline must seal once.");
            BoundPlan=plan;
        }
        public void WriteAudio(float[] audio, int count) => WriteAudio(audio, 0, count);
        public void WriteAudio(float[] audio, int offset, int count)
        {
            AudioSampleFrames += count / _channels;
            WrittenAudio.AddRange(audio.Skip(offset).Take(count));
        }
        public void WriteAudioFrame(long videoFrameIndex, float[] audio, int count) => WriteAudio(audio, count);
        public ExportResult Complete(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            BoundPlan?.ValidateVideoComplete(FramesWritten);
            if (Options.AudioQuality != "none") BoundPlan?.ValidateAudioComplete(AudioSampleFrames);
            BeforeOutputCommit?.Invoke();
            using var commitScope = AcquireOutputCommitScope?.Invoke();
            Completed = true;
            return new(Path.Combine(Options.OutputDirectory, Options.Title + ".mp4"), FramesWritten, FramesWritten / (double)Options.Fps, "fixture", 1, null);
        }
    }
}
namespace AAVideoExport.Plugin
{
    internal static class StaticInertModEvidence
    {
        internal const string Scope = "StaticInertModEvidence.empty-load.v1";
        internal static bool Present, Invalidated, RejectAcquisition, RejectCommit;
        internal static int Creations, Frames, Images;
        internal static System.Reflection.Assembly[] LoadedSupportedAssemblies() { ProductionAuditTrap.Check("StaticInert.Inventory"); return Present ? new[] { typeof(StaticInertModEvidence).Assembly } : Array.Empty<System.Reflection.Assembly>(); }
        internal static bool TryCreateReceipt(System.Reflection.Assembly assembly, out Receipt? receipt, out string reason)
        {
            receipt = null; reason = "fixture acquire rejection";
            if (RejectAcquisition) return false;
            Creations++; receipt = new Receipt(); return receipt.TryValidate(out reason);
        }
        internal static void Reset() { Present=Invalidated=RejectAcquisition=RejectCommit=false;Creations=Frames=Images=0; }
        internal sealed class Receipt
        {
            private bool faulted;
            internal string PayloadSha256 => new string('a',64);
            internal string ModuleName => "ExampleCore";
            internal bool TryValidate(out string reason) { Images++; if(RejectCommit)faulted=true;return TryValidateFrame(out reason); }
            internal bool TryValidateFrame(out string reason) { Frames++;faulted|=Invalidated;reason=faulted?"fixture retained receipt invalidated":"";return !faulted; }
        }
    }
    internal static class AASkipIntroExportBoundary
    {
        internal static bool Present, Invalidated, RejectAcquisition, RejectCommit;
        internal static int Acquires, Disposes, Validations, CommitValidations;
        internal static bool IsActive;
        private static bool snapshotTaken, absent;
        internal static IDisposable? TryAcquire()
        {
            snapshotTaken = true; absent = !Present;
            if (absent) return null;
            if (RejectAcquisition) throw new AAVideoExport.Core.ExportException("strict_startup_boundary_invalid", "fixture acquisition");
            Acquires++; IsActive = true; return new Lease();
        }
        internal static void ValidateActive()
        {
            if (snapshotTaken && absent && Present)
                throw new AAVideoExport.Core.ExportException("strict_startup_boundary_invalid", "fixture Mod appeared after absence");
            if (!IsActive) return;
            Validations++;
            if (Invalidated) throw new AAVideoExport.Core.ExportException("strict_startup_boundary_invalid", "fixture invalidation");
        }
        internal static void ValidateForCommit()
        {
            CommitValidations++;
            if (!snapshotTaken || (!absent && !IsActive) || (absent && Present) || Invalidated || RejectCommit)
                throw new AAVideoExport.Core.ExportException("strict_startup_boundary_invalid", "fixture commit invalidation");
        }
        internal static void Reset()
        {
            Present = Invalidated = RejectAcquisition = RejectCommit = IsActive = false;
            snapshotTaken = absent = false;
            Acquires = Disposes = Validations = CommitValidations = 0;
        }
        internal static void ReleaseAbsentSnapshot() { snapshotTaken = false; absent = false; }
        internal static IDisposable AcquireCommitScope()
        {
            if (!snapshotTaken || (!absent && !IsActive) || (absent && Present) || Invalidated || RejectCommit)
                throw new AAVideoExport.Core.ExportException("strict_startup_boundary_invalid", "fixture commit scope invalidation");
            return new CommitScope();
        }
        private sealed class CommitScope : IDisposable { public void Dispose() { } }
        private sealed class Lease : IDisposable
        {
            private bool disposed;
            public void Dispose() { if (disposed) return; disposed = true; Disposes++; IsActive = false; }
        }
    }
    internal static class StrictCapabilityInventory
    {
        internal static AAVideoExport.Core.StrictCapabilitySnapshot Snapshot =
            AAVideoExport.Core.StrictCapabilitySnapshot.Create(Array.Empty<AAVideoExport.Core.StrictCapability>());
        internal static int AssemblyGeneration;
        internal static AAVideoExport.Core.StrictCapabilitySnapshot Capture(StaticInertScope? scope = null, CharacterVoiceSessionAdapter? voice = null, RetainedRecipeScope? recipes = null, FrameworkGeneratedAssemblyScope? framework = null)
        {
            ProductionAuditTrap.Check("CapabilityInventory.Capture");
            FrameworkGeneratedAssemblyScope.AcquisitionOrder.Add(framework == null ? "inventory-without-framework" : "inventory");
            var modules = Snapshot.Modules.ToList();
            if (CharacterVoiceSessionAdapter.IsProviderLoaded)
            {
                try
                {
                    (voice ?? throw new InvalidOperationException("missing held voice scope")).ValidateController();
                    modules.Add(new("Rukari.CharacterVoice", new string('b', 64), AAVideoExport.Core.StrictCapabilityLevel.Observed, new[] { CharacterVoiceSessionAdapter.ControllerEvidenceMarker }));
                }
                catch (Exception error) { modules.Add(new("Rukari.CharacterVoice", new string('b', 64), AAVideoExport.Core.StrictCapabilityLevel.Rejected, new[] { error.Message })); }
            }
            if (!StaticInertModEvidence.Present) return AAVideoExport.Core.StrictCapabilitySnapshot.Create(modules);
            AAVideoExport.Core.StrictCapability item;
            try { item = new("ExampleCore",new string('a',64),AAVideoExport.Core.StrictCapabilityLevel.Observed,
                (scope ?? throw new InvalidOperationException("missing retained inert scope")).CaptureProof(typeof(StaticInertModEvidence).Assembly)); }
            catch(Exception error) { item = new("ExampleCore",new string('a',64),AAVideoExport.Core.StrictCapabilityLevel.Rejected,new[]{error.Message}); }
            return AAVideoExport.Core.StrictCapabilitySnapshot.Create(modules.Append(item));
        }
        internal static void Reset()
        {
            Snapshot = AAVideoExport.Core.StrictCapabilitySnapshot.Create(Array.Empty<AAVideoExport.Core.StrictCapability>());
            AssemblyGeneration = 0;
        }
    }

    internal static class RuntimeAttributionClient
    {
        internal const string ExpectedRecipePolicy = "aa101.interop-initializer.intptr.final-il.v2";
        internal const string ExpectedRecipeEvidenceKind = "PRE_MATERIALIZATION_FINAL_IL";
        private sealed class State { internal bool Sealed; }
        private static readonly Dictionary<string, State> Tokens = new();
        internal static bool Available = true, Invalidated, RefuseCommit;
        internal static int Acquisitions, Validations, Seals, Releases, Enters;
        internal static string RecipesHash = new('b', 64);
        internal static readonly List<string> CommitOrder = new();
        internal static int ActiveTokens { get { lock(Tokens) return Tokens.Count; } }
        internal static void Reset() { lock(Tokens) Tokens.Clear(); Available = true; Invalidated = RefuseCommit = false; Acquisitions = Validations = Seals = Releases = Enters = 0; RecipesHash = new('b',64); CommitOrder.Clear(); }
        private static string Evidence(string token, State state) => System.Text.Json.JsonSerializer.Serialize(new { schema="aave.runtime-recipes.v2",token,valid=!Invalidated,
            policy=ExpectedRecipePolicy,evidenceKind=ExpectedRecipeEvidenceKind,policySha256=new string('a',64),recipesSha256=state.Sealed?RecipesHash:"",knownHostBaseline=true,@sealed=state.Sealed,
            preexistingCoverage=false,allCodeCoverage=false,permitsStrictExport=false });
        internal static bool TryAcquireRecipeScope(out string token,out string evidence,Action<string> report)
        { ProductionAuditTrap.Check("Recipe.Acquire"); lock(Tokens) { token="";evidence="";if(!Available)return false;token="recipe-"+(++Acquisitions);var state=new State();Tokens.Add(token,state);evidence=Evidence(token,state);FrameworkGeneratedAssemblyScope.AcquisitionOrder.Add("recipe");return true;} }
        internal static bool TryValidateRecipeScope(string token,bool commit,out string evidence,Action<string> report)
        { lock(Tokens) { Validations++;evidence="";if(Invalidated||!Tokens.TryGetValue(token,out var state)||commit&&!state.Sealed)return false;evidence=Evidence(token,state);return true;} }
        internal static bool TrySealRecipeScope(string token,out string evidence,Action<string> report)
        { lock(Tokens) { evidence="";if(Invalidated||!Tokens.TryGetValue(token,out var state))return false;state.Sealed=true;Seals++;evidence=Evidence(token,state);return true;} }
        internal static bool TryValidateRecipeHolder(string token,System.Reflection.Assembly assembly,Action<string> report)=>false;
        internal static bool TryEnterRecipeCommitScope(string token,CancellationToken cancellation,out IDisposable? lease,Action<string> report)
        { lock(Tokens) { lease=null;if(cancellation.IsCancellationRequested||Invalidated||RefuseCommit||!Tokens.TryGetValue(token,out var state)||!state.Sealed)return false;Enters++;CommitOrder.Add("recipe");lease=new Lease(token);return true;} }
        internal static void ReleaseRecipeScope(string token,Action<string> report) { lock(Tokens) { Releases++;Tokens.Remove(token); } }
        private sealed class Lease(string token):IDisposable { public void Dispose(){lock(Tokens)Tokens.Remove(token); FrameworkGeneratedAssemblyScope.ExitOrder.Add("recipe");} }
    }
    internal static class RetainedMenuPlaybackBoundary
    {
        internal static bool Ready = true, IsHeld, IsPlayback, Invalidated, RequireBeforeNative, ConstructedBeforeMenu;
        internal static int Acquisitions, Attempts, Begins, Ends, Releases;
        internal static void Reset() { Ready = true; IsHeld = IsPlayback = Invalidated = RequireBeforeNative = ConstructedBeforeMenu = false; Acquisitions = Attempts = Begins = Ends = Releases = 0; }
        internal static void TryInstall() { }
        internal static bool TryAcquireRetained(out IDisposable? lease)
        {
            Attempts++; lease = null; if (!Ready) return false;
            if (IsHeld) throw new InvalidOperationException("menu scope recreated while held");
            IsHeld = true; Acquisitions++; lease = new Lease(); return true;
        }
        internal static void ValidateActive() { if (!IsHeld || Invalidated) throw new InvalidOperationException("menu proof invalid"); }
        internal static string CaptureActiveProof() { ValidateActive(); return "fixture-menu-proof"; }
        internal static string PlaybackSettingsIdentity() => IsHeld ? CaptureActiveProof() : "retained-menu-not-acquired";
        internal static void BeginPlayback() { ValidateActive(); if (IsPlayback) throw new InvalidOperationException("menu pass repeated"); IsPlayback = true; Begins++; }
        internal static void EndPlayback() { if (IsPlayback) Ends++; IsPlayback = false; }
        internal static void ValidateForCommit() { ValidateActive(); if (IsPlayback) throw new InvalidOperationException("menu not restored before publish"); }
        internal static IDisposable AcquireCommitScope() { ValidateForCommit(); RuntimeAttributionClient.CommitOrder.Add("menu"); return new EmptyScope(); }
        private sealed class Lease : IDisposable { private bool done; public void Dispose() { if (done) return; EndPlayback(); IsHeld = false; Releases++; done = true; } }
        private sealed class EmptyScope : IDisposable { public void Dispose() { } }
    }
    internal readonly record struct CharacterVoicePosition(long Frame, long SampleFrame, int Row);
    internal sealed record CharacterVoiceReceipt(CharacterVoicePosition End, string TraceSha256);
    internal sealed class CharacterVoiceSessionAdapter : IDisposable
    {
        internal const string ControllerEvidenceMarker = "CharacterVoiceSessionAdapter.held-controller.v1";
        internal static bool Loaded, PreloadReady = true, Invalidated;
        internal static int Attaches, Binds, Prepares, Pumps, Arms, Seals, Completions, CommitSeals, Disposals, FrameValidations, DisposeFailures;
        internal static CharacterVoiceSessionAdapter? Last;
        internal static string ReceiptTraceSha256 = new string('c', 64);
        internal static bool IsProviderLoaded => Loaded || MoreEffectsCompatibility.CharacterVoiceExactUnsupported;
        internal bool IsAttached => !disposed;
        internal bool StoryBound, IsReady, IsArmed;
        internal bool NoOwnedVoiceExpected => StoryBound;
        internal string FailureReason => "fixture voice failure";
        internal Func<CharacterVoicePosition>? Position;
        internal CharacterVoiceReceipt? Prepared, Expected;
        private bool disposed, sealedForCommit;
        internal static void Reset() { Loaded = Invalidated = false; PreloadReady = true; Attaches = Binds = Prepares = Pumps = Arms = Seals = Completions = CommitSeals = Disposals = FrameValidations = DisposeFailures = 0; Last = null; ReceiptTraceSha256 = new string('c', 64); }
        internal static CharacterVoiceSessionAdapter Attach(Action<Exception> fault, Action<string> log)
        {
            CharacterVoiceCompatibility.ThrowIfExactUnsupported(MoreEffectsCompatibility.CharacterVoiceExactUnsupported, "1.6.8", "fixture has no held controller");
            Attaches++; return Last = new();
        }
        internal void ValidateController() { if (disposed || Invalidated) throw new InvalidOperationException("voice controller proof invalid"); }
        internal static string[] CollectKnownIdentifiers(GenericScenarioExcelTable save) => Array.Empty<string>();
        internal void BindStory(string hash, IEnumerable<string> known) { ValidateController(); if (StoryBound || (AAVideoExport.Core.ExportBuildProfile.DevelopmentAuditEnabled ? hash.Length != 64 : string.IsNullOrWhiteSpace(hash))) throw new InvalidOperationException("voice story rebound or invalid key"); StoryBound = true; Binds++; }
        internal void PreparePass() { ValidateController(); if (!StoryBound || IsArmed) throw new InvalidOperationException("voice prepare out of order"); IsReady = false; Prepares++; }
        internal bool PumpPreload() { ValidateController(); Pumps++; return IsReady = PreloadReady; }
        internal void BeginPass(Test player, int fps, int rate, Func<CharacterVoicePosition> position, CharacterVoiceReceipt? expected = null)
        {
            ValidateController(); if (!IsReady || position().Frame != 0 || position().SampleFrame != 0) throw new InvalidOperationException("voice armed before ready/reset");
            Position = position; Expected = expected; IsArmed = true; Arms++;
        }
        internal void ValidateFrame() { ValidateController(); if (!IsArmed) throw new InvalidOperationException("unarmed voice validation"); FrameValidations++; }
        internal CharacterVoiceReceipt SealPass(CharacterVoicePosition end)
        {
            ValidateFrame(); if (Expected != null && (Expected.End != end || Expected.TraceSha256 != ReceiptTraceSha256)) throw new InvalidOperationException("voice receipt mismatch");
            var receipt = new CharacterVoiceReceipt(end, ReceiptTraceSha256); if (Expected == null) Prepared = receipt; IsArmed = false; Seals++; return receipt;
        }
        internal void EndPass() { IsArmed = IsReady = false; Position = null; }
        internal void CompletePass(CharacterVoicePosition end) { ValidateFrame(); IsArmed = false; Completions++; }
        internal void SealForCommit() { ValidateController(); if (Seals < 2 || IsArmed) throw new InvalidOperationException("voice two-pass receipt not sealed"); sealedForCommit = true; CommitSeals++; }
        internal void ValidateForCommit() { ValidateController(); if (!sealedForCommit) throw new InvalidOperationException("voice commit proof missing"); }
        internal IDisposable AcquireCommitScope() { ValidateForCommit(); RuntimeAttributionClient.CommitOrder.Add("voice"); return new EmptyScope(); }
        public void Dispose() { if (disposed) return; EndPass(); if (DisposeFailures > 0) { DisposeFailures--; throw new IOException("voice cleanup pending"); } disposed = true; Disposals++; }
        private sealed class EmptyScope : IDisposable { public void Dispose() { } }
    }
    internal sealed class NativeBgmSessionAdapter : IDisposable
    {
        internal static bool Ready=true;
        internal static bool PendingCleanup;
        internal static int Preparations,Begins,Ends,Disposals,DisposeAttempts,DisposeFailures;
        internal static bool Active;
        private bool disposed,releasePending;
        private Action<Exception>? cleanupFault;
        internal Exception? CleanupFailure { get; private set; }
        internal static void Reset(){Ready=true;PendingCleanup=false;Preparations=Begins=Ends=Disposals=DisposeAttempts=DisposeFailures=0;Active=false;}
        internal bool CleanupPending => releasePending || disposed && PendingCleanup;
        internal static NativeBgmSessionAdapter Prepare(GenericScenarioExcelTable story, Action<Exception> fault, Action<string> log, Action<Exception>? cleanupFault=null)
        { ArgumentNullException.ThrowIfNull(story);Preparations++;return new(){cleanupFault=cleanupFault}; }
        internal bool PumpPreload(){if(disposed)throw new ObjectDisposedException(nameof(NativeBgmSessionAdapter));return Ready;}
        internal void BeginPass(Test player){if(!Ready||disposed)throw new InvalidOperationException("BGM started before native clips were ready.");Active=true;Begins++;}
        internal void EndPass(){if(Active){Active=false;Ends++;}}
        public void Dispose()
        {
            if(disposed || CleanupFailure!=null)return;DisposeAttempts++;EndPass();
            if(DisposeFailures>0){DisposeFailures--;releasePending=true;CleanupFailure=new IOException("BGM asset release outcome is uncertain");cleanupFault?.Invoke(CleanupFailure);throw CleanupFailure;}
            releasePending=false;disposed=true;Disposals++;
        }
    }
    internal static class MultilingualCaptureAdapter
    {
        internal static bool Active;
        internal static int Begins, Ends;
        internal static readonly List<long> Pumps = new();
        internal static void Reset() { Active = false; Begins = Ends = 0; Pumps.Clear(); }
        internal static void Initialize(object hooks, Action<string> log) { }
        internal static void TryInstall() { }
        internal static void BeginExport() { Active = true; Begins++; }
        internal static void EndExport() { if (Active) Ends++; Active = false; }
        internal static void ValidateActive() { }
        internal static void Pump(long frame) { if (Active) Pumps.Add(frame); }
        internal static string PlaybackSettingsIdentity() => "fixture-translation-absent";
    }
    internal static class RuntimeModBoundaries
    {
        internal static bool Active;
        internal static bool SourcePreparationRequired, SourcePreparedBeforeOffline;
        internal static int SourcePreparations, SourceRevalidations;
        internal static int SourceDisposeFailures;
        internal static EffectsSourceInput? SourceInput;
        internal static Action? BeforeSourcePreparation, BeforeSourceRevalidation;
        internal static void TryInstall() { }
        internal static void PrepareExportDependencies() { }
        internal static void BeginExport() => Active = true;
        internal static void ValidateActive() { }
        internal static void EndExport() => Active = false;
        internal static bool RequiresEffectsSourcePreparation() => SourcePreparationRequired;
        internal static EffectsSourceInput PrepareEffectsSourceInput(string selected, string sha256)
        {
            SourcePreparations++;
            SourcePreparedBeforeOffline = ExportHost.Current?.Capturing != true && !CharacterVoiceDelayClock.Active;
            BeforeSourcePreparation?.Invoke();
            return SourceInput = AAVideoExport.Core.ExportBuildProfile.DevelopmentAuditEnabled
                ? EffectsSourceInput.Acquire(new EffectsSourceFake(selected), selected, sha256)
                : EffectsSourceInput.AcquireExecution(new EffectsExecutionFake(), selected);
        }
        internal static void RevalidateEffectsSourceInput(EffectsSourceInput input) { SourceRevalidations++; BeforeSourceRevalidation?.Invoke(); input.RevalidateForPass(); }
        internal static void ReleaseEffectsSourceInput(EffectsSourceInput input)
        {
            if (SourceDisposeFailures > 0) { SourceDisposeFailures--; throw new IOException("source input cleanup pending"); }
            input.Dispose(); if (ReferenceEquals(SourceInput, input)) SourceInput = null;
        }
        internal static string PlaybackSettingsWithoutEffectsSource() => SourcePreparationRequired ? "fixture-options" : "fixture-runtime-absent";
        internal static string PlaybackSettingsIdentity() => SourcePreparationRequired
            ? AAVideoExport.Core.ExportBuildProfile.DevelopmentAuditEnabled ? "fixture-options|effects-project=" + (SourceInput?.ProjectSha256 ?? "absent") : "fixture-options"
            : "fixture-runtime-absent";
    }
    internal static class ControlledBackgroundVideoAdapter
    {
        internal static Exception? EndFailure = null;
        internal static void TryInstall() { }
        internal static void BeginExport(Func<long> frame, int fps, string cache, string ffmpeg, string ffprobe) { }
        internal static void Pump(long frame) { }
        internal static void ValidateActive() { }
        internal static void EndExport() { if (EndFailure != null) throw EndFailure; }
        internal static string PlaybackSettingsIdentity() => "background-absent";
        internal static IReadOnlyList<ControlledBackgroundCommand> Commands = Array.Empty<ControlledBackgroundCommand>();
        internal static IReadOnlyList<ControlledBackgroundCommand> CommandTrace => Commands;
    }
    internal sealed record ControlledBackgroundCommand(long Sequence, long Frame, string Operation,
        int Layer, string Resource, string SourceSha256, long SourceFrame);
    internal static class CharacterVoiceDelayClock
    {
        internal static Func<long>? FrameIndex;
        internal static int Fps, Begins, Ends, InstallAttempts;
        internal static bool Active;
        internal static Action? OnEnd;
        internal static void Reset() { FrameIndex = null; Fps = Begins = Ends = InstallAttempts = 0; Active = false; OnEnd = null; }
        internal static void TryInstall() => InstallAttempts++;
        internal static void ValidateBinding() { }
        internal static void BeginExport(Func<long> frameIndex, int fps)
        {
            if (Active) throw new InvalidOperationException("previous voice clock was not restored");
            FrameIndex = frameIndex; Fps = fps; Active = true; Begins++;
        }
        internal static void EndExport()
        {
            if (!Active) return;
            OnEnd?.Invoke();
            Ends++; Active = false;
        }
    }
    internal static class FlandrePortraitClock
    {
        internal static void TryInstall() { }
        internal static void EnsureStrictExportAllowed() { }
        internal static void BeginExport(Func<double> readTime) { }
        internal static void EndExport() { }
    }
    internal static class MoreEffectsCompatibility
    {
        internal static bool IsLoaded;
        internal static bool FullClockCoverage = true;
        internal static string Status = "stopped";
        internal static string FailureReason = "";
        internal static bool CharacterVoiceExactUnsupported;
        internal static void TryInstall() { }
        internal static void EnsureStrictExportAllowed()
        {
            EnsureOwnEffectsAllowed();
            CharacterVoiceCompatibility.ThrowIfExactUnsupported(CharacterVoiceExactUnsupported, "1.6.8",
                "VoicePlaybackRuntime 的独立播放门控尚未接入导出帧时钟。");
        }
        internal static void EnsureOwnEffectsAllowed() => StrictModAdmission.EnsureCompatible(IsLoaded, FullClockCoverage, Status, FailureReason);
        internal static void ResetAdmission() { IsLoaded = false; FullClockCoverage = true; Status = "stopped"; FailureReason = ""; CharacterVoiceExactUnsupported = false; }
        internal static void BeginExport(Func<double> readTime) { }
        internal static void EndExport() { }
    }
    public static class Plugin
    {
        public const string Version = "0.2.8";
        internal static bool UseNativeFrameBuffers = true;
        internal static int Direct3DReadbackFrames = 1;
        internal static string TimelineReceiptDirectory = Path.Combine(Path.GetTempPath(), "aave-host-receipts-unused");
        internal static string HostIdentity = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal static bool StrictPreflightCacheReuseEnabled => false;
        public static string FfmpegPath = "unused", FfprobePath = "unused";
        public static bool ReduceProgressUiWork = true, UseAsyncReadback = true;
        public static bool FailPerformanceLog;
        public static string SavedOutputDirectory = "";
        public static string OutputDirectory => OutputDirectoryPreference.Resolve(SavedOutputDirectory);
        internal static string ComputeStrictRuntimeFingerprint() => "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
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
        public bool Visible, IsCapturing, ReduceCaptureUiWork, ShowPlaybackButtons, StatusIsError;
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
        public double RenderFramesPerSecond, EncodeFramesPerSecond;
        public int BufferedFrames;
        public bool StrictAudioPreparation, StrictVideoReplay;
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
        public int SampleRate => UnityEngine.AudioSettings.outputSampleRate;
        public int Channels => UnityEngine.AudioSettings.speakerMode == UnityEngine.AudioSpeakerMode.Mono ? 1 : 2;
        public static readonly Queue<int> AudioSampleCounts = new();
        public static readonly Queue<float[]> AudioBlocks = new();
        public int AudioRenderCalls;
        // Mirrors the production native capture scope: every returned block has
        // one contiguous raw sample-clock position, including drain blocks.
        public long LastAudioRawStartSample { get; private set; }
        public long AudioRawSampleFrames => _audioRawSampleFrames;
        public double AudioRawDspEpochSeconds { get; private set; } = UnityEngine.AudioSettings.dspTime;
        private long _audioRawSampleFrames;
        private readonly int _fps;
        public UnityEngine.Camera LastCamera { get; } = new();
        public NativeCaptureScope(Test player, ExportOptions options, UnityEngine.RenderTexture target, bool showButtons, bool automaticCapture = true)
        {
            if (RetainedMenuPlaybackBoundary.RequireBeforeNative && !RetainedMenuPlaybackBoundary.IsPlayback)
                RetainedMenuPlaybackBoundary.ConstructedBeforeMenu = true;
            AutomaticCameraCapture = automaticCapture; _fps = options.Fps;
        }
        public void Dispose() => Disposed = true;
        public void PrepareAutomaticFrame() { }
        public void RestoreAutomaticFrame() { }
        public void SetTarget(UnityEngine.RenderTexture target) { }
        public void ExcludeOverlayLayer(Action<UnityEngine.Camera> exclude) { }
        public void RenderFrame(UnityEngine.RenderTexture target) { }
        public void ResetAudioTimeline()
        {
            _audioRawSampleFrames = 0;
            LastAudioRawStartSample = 0;
            AudioRawDspEpochSeconds = UnityEngine.AudioSettings.dspTime;
        }
        public (float[] Samples, int Count) RenderAudio()
        {
            AudioRenderCalls++;
            if (AudioBlocks.Count != 0)
            {
                var block = AudioBlocks.Dequeue();
                LastAudioRawStartSample = _audioRawSampleFrames;
                _audioRawSampleFrames += block.Length / Channels;
                return (block, block.Length);
            }
            int count = AudioSampleCounts.Count != 0 ? AudioSampleCounts.Dequeue() : SampleRate / _fps;
            LastAudioRawStartSample = _audioRawSampleFrames;
            _audioRawSampleFrames += count;
            return (new float[count * Channels], count * Channels);
        }
    }
    public sealed class FramePipeline : IDisposable
    {
        public bool NativeBufferQueue => true;
        public int AllocatedNativeBuffers => 0;
        private readonly ExportSession _session;
        public FramePipeline(ExportOptions options, ExportSession session, bool useAsync, bool legacySingleTarget = false, bool nativeBuffers = true) { _session = session; }
        private readonly UnityEngine.RenderTexture _target = new();
        public bool RejectTargetAccess, Disposed;
        public int TargetReads;
        public Exception? DrainError;
        public int DrainCalls;
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
        public long FramesWritten => _session.FramesWritten;
        public double EncodingElapsedSeconds => FramesWritten / 60d;
        public Task? PendingWriter;
        public Task WriterCompletion => PendingWriter ?? Task.CompletedTask;
        public Task FinishFrames() => WriterCompletion;
        public void Drain(bool wait) { DrainCalls++; if (DrainError != null) throw DrainError; }
        public void Capture() { _session.FramesWritten++; }
        public void Dispose() => Disposed = true;
    }
    public static class NativeExportClock
    {
        public static float Now;
        public static long WaitPollCount, RegisteredWaitCount, UnscaledDeltaReadCount;
        public static int ActiveWaitCount;
        public static int PeakActiveWaitCount;
        public static void Begin(float epoch, float delta, Func<float> clock) { }
        private static Func<long>? _frame;
        private static Func<bool>? _advancing;
        private static int _fps;
        public static bool? AdvancingAtRestore;
        public static long Frame => _frame?.Invoke() ?? 0;
        public static double ElapsedSeconds => _fps==0?0:Frame/(double)_fps;
        public static void BeginTimeline(int fps, Func<long> frame, Func<bool> advancing) { _fps=fps; _frame=frame; _advancing=advancing; AdvancingAtRestore=null; }
        public static void ResumeTimelineFrame() { }
        public static void Restore() { if(_advancing!=null) AdvancingAtRestore=_advancing(); _frame=null;_advancing=null;_fps=0; }
    }
    internal static class AutoSelectionHooks { internal static void EnsureSupported() { } }
}
