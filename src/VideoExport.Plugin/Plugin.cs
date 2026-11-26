using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using AAVideoExport.Core;
using HarmonyLib;
using System.Security.Cryptography;
using System.Text;

namespace AAVideoExport.Plugin;

[BepInPlugin(Guid, "AA Video Export", Version)]
[BepInProcess("AzureArchive.exe")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "halocue.aa.videoexport";
    public const string Version = "1.0.0";
    internal static string FfmpegPath = "ffmpeg.exe";
    internal static string FfprobePath = "ffprobe.exe";
    internal static bool ReduceProgressUiWork = true;
    internal static bool UseAsyncReadback = true;
    internal static bool UseNativeFrameBuffers = true;
    internal static int Direct3DReadbackFrames = 1;
    // Cache reuse is enabled only after the sandbox capability inventory proves
    // the loaded Mod set deterministic. Publishing a receipt is always safe;
    // reusing it for an un-certified runtime could hide a changed event trace.
    internal static bool StrictPreflightCacheReuseEnabled = false;
    internal static bool StrictSandboxAudioCommandTracingEnabled = false;
    internal static bool StrictSandboxStoryCommandTracingEnabled = false;
    internal static string TimelineReceiptDirectory = "";
    internal static string HostIdentity = "";
    private static ConfigEntry<string>? _outputDirectory;
    private static string _lastSavedOutputDirectory = "";
    internal static string OutputDirectory => OutputDirectoryPreference.Resolve(_outputDirectory?.Value);
    private static ManualLogSource? _performanceLog;
    private Harmony? _harmony;
    private ExportHost? _host;

    public override void Load()
    {
        TimelineReceiptDirectory = Path.Combine(Path.GetDirectoryName(Config.ConfigFilePath)!, ExportBuildProfile.DevelopmentAuditEnabled ? "aave-timelines" : "aave-media-cache");
        HostIdentity = "production-execution";
        if (ExportBuildProfile.DevelopmentAuditEnabled)
        {
            RuntimeAttributionClient.TryMark("AAVE.Plugin.Load.enter", message => Log.LogWarning(message));
            using var binary = File.OpenRead(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
            using var hash = System.Security.Cryptography.SHA256.Create();
            HostIdentity = Convert.ToHexString(hash.ComputeHash(binary));
        }
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
            GenericStopwatchBridge.Initialize(_harmony, message => Log.LogInfo(message), typeof(Plugin).Assembly);
            GenericStopwatchBridge.AttachLoadedModules(assembly =>
                StrictCapabilityInventory.IsModAssembly(assembly, assembly.GetName().Name ?? ""));
            RuntimeVirtualizationHost.Initialize(message => Log.LogInfo(message));
            RuntimeVirtualizationHost.Probe("clock", RuntimePlaneState.Probed,
                "patched=" + GenericStopwatchBridge.PatchedMethods.Count + "; status=" + GenericStopwatchBridge.Status);
            AASkipIntroExportBoundary.Initialize(_harmony);
            AutoSelectionHooks.Initialize(_harmony, () => ExportHost.Current?.AutoSelection,
                error => ExportHost.Current?.AutoSelectionFailed(error), message => Log.LogInfo(message));
            MoreEffectsCompatibility.Initialize(_harmony, message => Log.LogInfo(message));
            FlandrePortraitClock.Initialize(_harmony, message => Log.LogInfo(message));
            CharacterVoiceDelayClock.Initialize(_harmony, message => Log.LogInfo(message),
                error => ExportHost.Current?.CharacterVoiceClockFailed(error));
            CharacterVoiceDelayClock.TryInstall();
            RuntimeModBoundaries.Initialize(_harmony, message => Log.LogInfo(message), Path.GetDirectoryName(Config.ConfigFilePath)!);
            MultilingualCaptureAdapter.Initialize(_harmony, message => Log.LogInfo(message));
            RetainedMenuPlaybackBoundary.Initialize(_harmony, message => Log.LogInfo(message),
                error => ExportHost.Current?.RetainedBoundaryFailed(error), Path.GetDirectoryName(Config.ConfigFilePath)!);
            _host = AddComponent<ExportHost>();
            string? commandLineStory = null, commandLineOutput = null;
            bool commandLineExit = false;
            int commandLineCount = 1;
            float commandLineCancelAfter = -1f;
            int commandLineCancelFrame = -1;
            string commandLineEncoding = "";
            string commandLineCodec = "";
            string commandLineEncoder = "";
            string commandLineSoftwarePreset = "superfast";
            int commandLineBitrateKbps = 0;
            int commandLineWidth = 1280, commandLineHeight = 720, commandLineFps = 30;
            var commandLineArgs = Environment.GetCommandLineArgs();
            for (int i = 0; i < commandLineArgs.Length; i++)
            {
                if (commandLineArgs[i] == "--aa-export-story" && i + 1 < commandLineArgs.Length &&
                    !commandLineArgs[i + 1].StartsWith("-") &&
                    string.Equals(Path.GetExtension(commandLineArgs[i + 1]), ".aas", StringComparison.OrdinalIgnoreCase))
                    commandLineStory = commandLineArgs[++i];
                else if (commandLineArgs[i] == "--aa-export-output" && i + 1 < commandLineArgs.Length &&
                    !commandLineArgs[i + 1].StartsWith("-"))
                    commandLineOutput = commandLineArgs[++i];
                else if (commandLineArgs[i] == "--aa-export-exit")
                    commandLineExit = true;
                else if (commandLineArgs[i] == "--aa-export-count" && i + 1 < commandLineArgs.Length &&
                    int.TryParse(commandLineArgs[++i], out var parsedCount))
                    commandLineCount = Math.Max(1, parsedCount);
                else if (commandLineArgs[i] == "--aa-export-cancel-after" && i + 1 < commandLineArgs.Length &&
                    float.TryParse(commandLineArgs[++i], out var parsedCancel))
                    commandLineCancelAfter = Math.Max(0f, parsedCancel);
                else if (commandLineArgs[i] == "--aa-export-cancel-frame" && i + 1 < commandLineArgs.Length &&
                    int.TryParse(commandLineArgs[++i], out var parsedCancelFrame))
                    commandLineCancelFrame = Math.Max(0, parsedCancelFrame);
                else if (commandLineArgs[i] == "--aa-export-encoding" && i + 1 < commandLineArgs.Length &&
                    !commandLineArgs[i + 1].StartsWith("-"))
                    commandLineEncoding = commandLineArgs[++i];
                else if (commandLineArgs[i] == "--aa-export-codec" && i + 1 < commandLineArgs.Length &&
                    !commandLineArgs[i + 1].StartsWith("-"))
                    commandLineCodec = commandLineArgs[++i];
                else if (commandLineArgs[i] == "--aa-export-encoder" && i + 1 < commandLineArgs.Length &&
                    !commandLineArgs[i + 1].StartsWith("-"))
                    commandLineEncoder = commandLineArgs[++i];
                else if (commandLineArgs[i] == "--aa-export-bitrate" && i + 1 < commandLineArgs.Length &&
                    int.TryParse(commandLineArgs[++i], out var parsedBitrate))
                    commandLineBitrateKbps = Math.Max(0, parsedBitrate);
                else if (commandLineArgs[i] == "--aa-export-software-preset" && i + 1 < commandLineArgs.Length &&
                    (commandLineArgs[++i] is "superfast" or "ultrafast"))
                    commandLineSoftwarePreset = commandLineArgs[i];
                else if (commandLineArgs[i] == "--aa-export-width" && i + 1 < commandLineArgs.Length &&
                    int.TryParse(commandLineArgs[++i], out var parsedWidth) && parsedWidth > 0)
                    commandLineWidth = parsedWidth;
                else if (commandLineArgs[i] == "--aa-export-height" && i + 1 < commandLineArgs.Length &&
                    int.TryParse(commandLineArgs[++i], out var parsedHeight) && parsedHeight > 0)
                    commandLineHeight = parsedHeight;
                else if (commandLineArgs[i] == "--aa-export-fps" && i + 1 < commandLineArgs.Length &&
                    int.TryParse(commandLineArgs[++i], out var parsedFps) && parsedFps > 0)
                    commandLineFps = parsedFps;
            }
            if (commandLineStory != null)
            {
                _host.ConfigureCommandLineExport(commandLineStory,
                    Path.GetFullPath(commandLineOutput ?? OutputDirectory), commandLineExit, commandLineCount,
                    commandLineCancelAfter, commandLineCancelFrame, commandLineEncoding, commandLineCodec,
                    commandLineEncoder, commandLineSoftwarePreset, commandLineBitrateKbps,
                    commandLineWidth, commandLineHeight, commandLineFps);
                Log.LogInfo("AAVE command-line export armed: " + commandLineStory);
            }
            if (ExportBuildProfile.DevelopmentAuditEnabled)
                RuntimeAttributionClient.TryMark("AAVE.Plugin.Load.ready", message => Log.LogWarning(message));
            Log.LogInfo($"AA Video Export loaded [{Version}; single-pass={ExportBuildProfile.SinglePassEnabled}; development-audit={ExportBuildProfile.DevelopmentAuditEnabled}]. Ctrl+Shift+E opens settings. RenderControlV1 requires explicit participant ownership.");
        }
        catch
        {
            _host?.Shutdown();
            if (_host?.RetainedCleanupPending == true)
            {
                Log.LogError("AA Video Export: retained controller cleanup remains pending after load failure.");
                throw;
            }
            if (!RetainedMenuPlaybackBoundary.Shutdown()) throw;
            // A retained voice deadline or partially removed prefix pair must
            // remain retryable; UnpatchSelf would discard that recovery path.
            if (!CharacterVoiceDelayClock.Shutdown())
            {
                Log.LogError("AA Video Export: CharacterVoice cleanup remains pending after load failure; unload must retry before removing remaining patches.");
                throw;
            }
            AASkipIntroExportBoundary.Shutdown();
            if (!MultilingualCaptureAdapter.Shutdown() || !RuntimeModBoundaries.Shutdown())
            {
                Log.LogError("AA Video Export: scoped translation/dependency cleanup remains pending after load failure.");
                throw;
            }
            AutoSelectionHooks.Shutdown();
            MoreEffectsCompatibility.Shutdown();
            FlandrePortraitClock.Shutdown();
            GenericStopwatchBridge.Shutdown();
            RuntimeVirtualizationHost.Shutdown();
            _harmony.UnpatchSelf();
            throw;
        }
    }

    internal static void LogPerformance(string message) => _performanceLog?.LogInfo(message);

    /// <summary>Hashes the active selected plugin payloads for strict timeline cache identity.</summary>
    internal static string ComputeStrictRuntimeFingerprint()
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled)
            throw new InvalidOperationException("Runtime fingerprints are available only in a DevelopmentAudit build.");
        string root = Paths.PluginPath;
        // Keep this read-only and allocation-bounded on Unity's background
        // preparation worker. The selected profile/load-root identity changes
        // when AA swaps its active plugin set; binary payload hashes are added
        // by the sandbox capability inventory before a cache is certified.
        string value = HostIdentity + "|" + Version + "|" + root + "|" +
            (Directory.Exists(root) ? Directory.GetLastWriteTimeUtc(root).Ticks.ToString() : "missing");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

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
        if (_host?.RetainedCleanupPending == true || !RetainedMenuPlaybackBoundary.Shutdown()) return false;
        if (!CharacterVoiceDelayClock.Shutdown()) return false;
        if (!MultilingualCaptureAdapter.Shutdown() || !RuntimeModBoundaries.Shutdown()) return false;
        AASkipIntroExportBoundary.Shutdown();
        if (!MoreEffectsCompatibility.Shutdown()) return false;
        if (!FlandrePortraitClock.Shutdown()) return false;
        AutoSelectionHooks.Shutdown();
        GenericStopwatchBridge.Shutdown();
        RuntimeVirtualizationHost.Shutdown();
        _harmony?.UnpatchSelf();
        if (_host != null) UnityEngine.Object.Destroy(_host);
        return true;
    }
}
