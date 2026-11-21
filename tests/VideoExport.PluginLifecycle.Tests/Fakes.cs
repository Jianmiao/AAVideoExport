// Source-links production Plugin lifecycle. Only BepInEx/Unity and adapter
// boundaries are substituted; this fixture never loads AA or any Mod binary.
using System.Reflection;

internal static class Events
{
    internal static readonly List<string> Items = new();
    internal static bool FailAddComponent;
    internal static void Reset() { Items.Clear(); FailAddComponent = false; }
}
namespace BepInEx
{
    public sealed class BepInPlugin : Attribute { public BepInPlugin(string id, string name, string version) { } }
    public sealed class BepInProcess : Attribute { public BepInProcess(string process) { } }
    public static class Paths { public static string GameRootPath = "", PluginPath = ""; }
}
namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public void LogInfo(object value) { }
        public void LogWarning(object value) { }
        public void LogError(object value) { }
    }
}
namespace BepInEx.Configuration
{
    public sealed class AcceptableValueList<T> { public AcceptableValueList(params T[] values) { } }
    public sealed class ConfigDescription { public ConfigDescription(string text, object values) { } }
    public sealed class ConfigEntry<T>
    {
        public T Value { get; set; }
        public ConfigFile ConfigFile { get; }
        public ConfigEntry(T value, ConfigFile file) { Value = value; ConfigFile = file; }
    }
    public sealed class ConfigFile
    {
        public string ConfigFilePath = Path.Combine(BepInEx.Paths.GameRootPath, "config", "plugin.cfg");
        public bool SaveOnConfigSet;
        public ConfigEntry<T> Bind<T>(string section, string key, T value, object description) => new(value, this);
        public void Save() { }
    }
}
namespace BepInEx.Unity.IL2CPP
{
    public abstract class BasePlugin
    {
        public BepInEx.Configuration.ConfigFile Config { get; } = new();
        public BepInEx.Logging.ManualLogSource Log { get; } = new();
        public abstract void Load();
        public virtual bool Unload() => true;
        protected T AddComponent<T>() where T : new()
        {
            Events.Items.Add("host.create");
            if (Events.FailAddComponent) throw new InvalidOperationException("fixture host creation failure");
            return new T();
        }
    }
}
namespace HarmonyLib
{
    public sealed class Harmony
    {
        public Harmony(string id) { }
        public void PatchAll(Assembly assembly) => Events.Items.Add("harmony.patch");
        public void UnpatchSelf() => Events.Items.Add("harmony.unpatch");
    }
}
namespace UnityEngine { public static class Object { public static void Destroy(object value) => Events.Items.Add("host.destroy"); } }
namespace AAVideoExport.Core
{
    internal static class EncoderToolPaths { internal static string Resolve(string value, string name, string directory) => value; }
}
namespace AAVideoExport.Plugin
{
    internal static class RuntimeAttributionClient { internal static void TryMark(string marker, Action<string> log) { } }
    public sealed class ExportHost
    {
        internal static bool PendingCleanup;
        internal bool RetainedCleanupPending => PendingCleanup;
        internal static ExportHost? Current;
        internal object? AutoSelection => null;
        internal int Faults;
        internal Exception? LastFault;
        public ExportHost() => Current = this;
        internal void AutoSelectionFailed(Exception error) { }
        internal void CharacterVoiceClockFailed(Exception error) { Faults++; LastFault = error; }
        internal void RetainedBoundaryFailed(Exception error) { Faults++; LastFault = error; }
        public void Shutdown() => Events.Items.Add("host.shutdown");
    }
    internal static class AASkipIntroExportBoundary
    {
        internal static void Initialize(HarmonyLib.Harmony harmony) { }
        internal static void Shutdown() { }
    }
    internal static class AutoSelectionHooks
    {
        internal static void Initialize(HarmonyLib.Harmony harmony, Func<object?> selected, Action<Exception> fault, Action<string> log) { }
        internal static void Shutdown() { }
    }
    internal static class MoreEffectsCompatibility
    {
        internal static void Initialize(HarmonyLib.Harmony harmony, Action<string> log) { }
        internal static bool Shutdown() => true;
    }
    internal static class FlandrePortraitClock
    {
        internal static void Initialize(HarmonyLib.Harmony harmony, Action<string> log) { }
        internal static bool Shutdown() => true;
    }
    internal static class MultilingualCaptureAdapter
    {
        internal static bool ShutdownSucceeds = true;
        internal static void Initialize(HarmonyLib.Harmony harmony, Action<string> log) { }
        internal static bool Shutdown() { Events.Items.Add("translation.shutdown"); return ShutdownSucceeds; }
    }
    internal static class RuntimeModBoundaries
    {
        internal static bool ShutdownSucceeds = true;
        internal static void Initialize(HarmonyLib.Harmony harmony, Action<string> log, string root) { }
        internal static bool Shutdown() { Events.Items.Add("runtime.shutdown"); return ShutdownSucceeds; }
    }
    internal static class RetainedMenuPlaybackBoundary
    {
        internal static void Initialize(HarmonyLib.Harmony harmony, Action<string> log, Action<Exception> fault, string root) { }
        internal static bool Shutdown() => true;
    }
    internal static class ControlledBackgroundVideoAdapter
    {
        internal static bool ShutdownSucceeds = true;
        internal static void Initialize(HarmonyLib.Harmony harmony, Action<string> log) { }
        internal static bool Shutdown() { Events.Items.Add("background.shutdown"); return ShutdownSucceeds; }
    }
    internal static class CharacterVoiceDelayClock
    {
        internal static Action<Exception>? Fault;
        internal static bool ShutdownSucceeds = true;
        internal static void Reset() { Fault = null; ShutdownSucceeds = true; }
        internal static void Initialize(HarmonyLib.Harmony harmony, Action<string> log, Action<Exception> onFault)
        { Events.Items.Add("voice.initialize"); Fault = onFault; }
        internal static void TryInstall() => Events.Items.Add("voice.install");
        internal static bool Shutdown() { Events.Items.Add("voice.shutdown"); return ShutdownSucceeds; }
    }
}
