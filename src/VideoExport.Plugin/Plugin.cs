using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using AAVideoExport.Core;
using HarmonyLib;

namespace AAVideoExport.Plugin;

[BepInPlugin(Guid, "AA Video Export", Version)]
[BepInProcess("AzureArchive.exe")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "halocue.aa.videoexport";
    public const string Version = "0.2.4";
    internal static string FfmpegPath = "ffmpeg.exe";
    internal static string FfprobePath = "ffprobe.exe";
    internal static bool ReduceProgressUiWork = true;
    internal static bool UseAsyncReadback = true;
    internal static bool UseNativeFrameBuffers = true;
    internal static int Direct3DReadbackFrames = 1;
    private static ConfigEntry<string>? _outputDirectory;
    private static string _lastSavedOutputDirectory = "";
    internal static string OutputDirectory => OutputDirectoryPreference.Resolve(_outputDirectory?.Value);
    private static ManualLogSource? _performanceLog;
    private Harmony? _harmony;
    private ExportHost? _host;

    public override void Load()
    {
        FfmpegPath = EncoderToolPaths.Resolve(Config.Bind("Encoder", "FFmpeg", "ffmpeg.exe", "Default uses bundled tools/ffmpeg.exe when present, otherwise PATH. Custom relative paths resolve from this mod's directory; absolute paths are honored.").Value, "ffmpeg.exe", Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!);
        FfprobePath = EncoderToolPaths.Resolve(Config.Bind("Encoder", "FFprobe", "ffprobe.exe", "Default uses bundled tools/ffprobe.exe when present, otherwise PATH. Custom relative paths resolve from this mod's directory; absolute paths are honored.").Value, "ffprobe.exe", Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!);
        ReduceProgressUiWork = Config.Bind("Diagnostics", "ReduceProgressUiWork", true,
            "Reduce hidden export settings work during capture. False is only for comparing performance diagnostics.").Value;
        UseAsyncReadback = Config.Bind("Capture", "AsyncGPUReadback", true,
            "Pipeline GPU frame transfers when supported. False selects the synchronous compatibility path.").Value;
        UseNativeFrameBuffers = Config.Bind("Capture", "NativeFrameBuffers", true,
            "Use owned native frame buffers for asynchronous capture. False retains the managed buffer path for comparison.").Value;
        Direct3DReadbackFrames = Config.Bind("Capture", "Direct3DReadbackFrames", 1,
            new ConfigDescription("D3D11 Anime4K readback slots: 1 keeps the stable path; 4 enables the experimental bounded queue. Full AA A/B and audio/cancel acceptance is still required. Startup failure retries 1 slot.",
                new AcceptableValueList<int>(1, 4))).Value;
        _outputDirectory = Config.Bind("Export", "OutputDirectory", "",
            "Last selected export folder. Empty uses the current user's Videos folder.");
        _lastSavedOutputDirectory = OutputDirectoryPreference.TryNormalize(_outputDirectory.Value, out var savedDirectory)
            ? savedDirectory
            : "";
        _harmony = new Harmony(Guid);
        try
        {
            _performanceLog = Log;
            _harmony.PatchAll(typeof(Plugin).Assembly);
            AutoSelectionHooks.Initialize(_harmony, () => ExportHost.Current?.AutoSelection,
                error => ExportHost.Current?.AutoSelectionFailed(error), message => Log.LogInfo(message));
            MoreEffectsCompatibility.Initialize(_harmony, message => Log.LogInfo(message));
            _host = AddComponent<ExportHost>();
            Log.LogInfo("AA Video Export loaded [0.2.4-audio-tolerance]. Ctrl+Shift+E opens settings. Encoding mode is explicit; project data is never written.");
        }
        catch
        {
            AutoSelectionHooks.Shutdown();
            MoreEffectsCompatibility.Shutdown();
            _harmony.UnpatchSelf();
            _host?.Shutdown();
            throw;
        }
    }

    internal static void LogPerformance(string message) => _performanceLog?.LogInfo(message);

    internal static void RememberOutputDirectory(string value)
    {
        if (_outputDirectory == null || !OutputDirectoryPreference.TryNormalize(value, out var directory)
            || string.Equals(directory, _lastSavedOutputDirectory, StringComparison.Ordinal)) return;
        try
        {
            // AA routes this plugin's Config to the active Profile. Do not write
            // preferences into the mod version directory or the selected project.
            string configValue = OutputDirectoryPreference.ToPortableValue(directory);
            bool autoSave = _outputDirectory.ConfigFile.SaveOnConfigSet && _outputDirectory.Value != configValue;
            _outputDirectory.Value = configValue;
            if (!autoSave) _outputDirectory.ConfigFile.Save();
            _lastSavedOutputDirectory = directory;
        }
        catch (Exception error)
        {
            _performanceLog?.LogWarning("AA Video Export: could not remember export folder (" + error.GetType().Name + ").");
        }
    }

    public override bool Unload()
    {
        _host?.Shutdown();
        if (!MoreEffectsCompatibility.Shutdown()) return false;
        AutoSelectionHooks.Shutdown();
        _harmony?.UnpatchSelf();
        if (_host != null) UnityEngine.Object.Destroy(_host);
        return true;
    }
}
