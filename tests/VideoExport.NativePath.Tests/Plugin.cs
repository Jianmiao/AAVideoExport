using System.Collections;
using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Local, temporary acceptance plugin. Never include in the shipped mod.
[BepInPlugin("halocue.aa.videoexport.nativepathtests", "AA Export Native Path Tests", "0.1.0")]
[BepInDependency("halocue.aa.videoexport")]
public sealed class Plugin : BasePlugin
{
    internal static Action<string> Report = _ => { };
    internal static string DiagnosticsRoot { get; private set; } = "";
    public override void Load()
    {
        Report = message =>
        {
            Log.LogInfo(message);
            if (!string.IsNullOrEmpty(DiagnosticsRoot))
                File.AppendAllText(Path.Combine(DiagnosticsRoot, "native-checks.log"), message + Environment.NewLine);
            if (message.StartsWith("PATH-CHECK RESULT", StringComparison.Ordinal)
                && Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_QUIT") == "1")
                Application.Quit();
        };
        if (Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_NATIVE_TESTS") != "1")
        {
            Report("PATH-CHECK DISABLED: set AA_VIDEOEXPORT_NATIVE_TESTS=1 and AA_VIDEOEXPORT_TEST_OUTPUT to opt in.");
            return;
        }
        string? output = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(output) || !Path.IsPathFullyQualified(output))
        {
            Report("PATH-CHECK DISABLED: AA_VIDEOEXPORT_TEST_OUTPUT must be an explicit absolute diagnostic folder.");
            return;
        }
        DiagnosticsRoot = Path.GetFullPath(output);
        Directory.CreateDirectory(DiagnosticsRoot);
        Report("PATH-CHECK RUN pid=" + Environment.ProcessId);
        Report("PATH-CHECK provider=" + typeof(ExportHost).Assembly.Location);
        if (Environment.GetCommandLineArgs().Contains("--aa-export-settings"))
        {
            SettingsChecks.InstallViewportFixture();
            AddComponent<SettingsChecks>();
        }
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-buttons"))
        {
            ButtonsChecks.InstallHooks();
            AddComponent<ButtonsChecks>();
        }
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-story"))
            AddComponent<StoryExportChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-prepare-controlled-story"))
            AddComponent<ControlledStoryAuthoringChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-official-controlled-story-authoring"))
            AddComponent<OfficialControlledStoryAuthoringChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-controlled-story-playback"))
            AddComponent<ControlledStoryPlaybackChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-clocks"))
            AddComponent<ClockChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-audio-clock"))
            AddComponent<AudioClockChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-audio-hooks"))
            AddComponent<AudioHookChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-audio-command"))
            AddComponent<AudioCommandChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-audio-semantics"))
            AddComponent<AudioSemanticsChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-character-voice"))
            AddComponent<CharacterVoiceChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-bgm-lifecycle"))
            AddComponent<NativeBgmLifecycleChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-bgm-inventory"))
            AddComponent<NativeBgmInventoryChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-controlled-background"))
            AddComponent<ControlledBackgroundChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-background-restore"))
            AddComponent<BackgroundNativeRestorationChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-multilingual-capture"))
            AddComponent<MultilingualCaptureChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-aamultilingual-boundary"))
            AddComponent<AAMultilingualBoundaryChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-aaskipintro-boundary"))
            AddComponent<AASkipIntroBoundaryChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-menubgm-boundary"))
            AddComponent<MenuBgmSelectorBoundaryChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-sfxfolder-boundary"))
            AddComponent<SfxFolderModBoundaryChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-aamenu-boundary"))
            AddComponent<AAMenuPrototypeBoundaryChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-startup-controller"))
            AddComponent<StartupControllerChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-bootstrap-attribution"))
            AddComponent<BootstrapAttributionChecks>();
        else if (Environment.GetCommandLineArgs().Contains("--aa-export-runtime-recipe-probe"))
            AddComponent<RuntimeRecipeChecks>();
        else AddComponent<PathChecks>();
    }
}

public sealed class PathChecks : MonoBehaviour
{
    private bool _ran;
    public PathChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (_ran) return;
        _ran = true;
        int passed = 0, failed = 0;
        var cases = new (string Name, string Path)[]
        {
            ("actual-config-to-panel", (string)typeof(AAVideoExport.Plugin.Plugin).GetProperty("OutputDirectory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!),
            ("remembered-native-folder", Path.Combine(Plugin.DiagnosticsRoot, "remembered-folder")),
            ("backslash-n-and-t", Path.Combine(Plugin.DiagnosticsRoot, @"native\new\tools\render\中文 [demo]")),
            ("forward-slash-folder", Path.Combine(Plugin.DiagnosticsRoot, "native/new/tools/render/中文 [demo]").Replace('\\', '/')),
            ("plain-folder", Path.Combine(Plugin.DiagnosticsRoot, "exports")),
            ("unc-folder-no-network-access", @"\\fixture-server\share\native\new")
        };
        foreach (var test in cases)
        {
            try { CheckPanel(test.Path); passed++; Plugin.Report("PATH-CHECK PASS " + test.Name); }
            catch (Exception error)
            {
                failed++;
                Plugin.Report("PATH-CHECK FAIL " + test.Name + ": " + DescribeFailure(error));
            }
        }
        try { CheckInvalidSubmission(); passed++; Plugin.Report("PATH-CHECK PASS control-characters-blocked-before-export"); }
        catch (Exception error) { failed++; Plugin.Report("PATH-CHECK FAIL control-characters: " + DescribeFailure(error)); }
        if (Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_NATIVE_MEDIA") == "1")
        {
            try { CheckMediaExport(); passed++; Plugin.Report("PATH-CHECK PASS real-encoder-output-in-native-folder"); }
            catch (Exception error) { failed++; Plugin.Report("PATH-CHECK FAIL real-encoder: " + DescribeFailure(error)); }
        }
        else Plugin.Report("PATH-CHECK SKIP real-encoder: AA_VIDEOEXPORT_NATIVE_MEDIA is not enabled.");
        Plugin.Report($"PATH-CHECK RESULT {passed} passed; {failed} failed");
    }

    private static ExportOptions CheckPanel(string directory)
    {
        var parent = new GameObject("AA path regression (inactive)");
        parent.SetActive(false);
        using var panel = new NativeExportPanel();
        try
        {
            panel.Options = new ExportOptions { Title = "path-fixture", OutputDirectory = directory };
            Call(panel, "BuildSharedFields", parent.transform);
            var fields = (IList)typeof(NativeExportPanel).GetField("_fields", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
            var pathField = fields[1]!;
            var input = (UIInput)pathField.GetType().GetProperty("Input")!.GetValue(pathField)!;
            // Unity calls this after the programmatic field assignment, on its
            // first active frame. Keep the test hierarchy off-screen/inactive.
            input.Start();
            var widgetValue = input.value;
            if (widgetValue.Any(char.IsControl) || !SamePath(widgetValue, directory))
            {
                Plugin.Report("PATH-CHECK widget " + ComparePaths(widgetValue, directory));
                throw new InvalidOperationException("Widget changed folder; " + ComparePaths(widgetValue, directory));
            }
            if (input.label.text.Contains('\n'))
                throw new InvalidOperationException($"Label contains newline; labelChars={input.label.text.Length}");
            // A picker changes the model, then the refresh reads the same getter
            // as BuildSharedFields. Manual input after Start uses the native setter.
            var pickerDirectory = Path.Combine(directory, "next-folder");
            panel.Options = panel.Options with { OutputDirectory = pickerDirectory };
            var read = (Func<string>)pathField.GetType().GetProperty("Read")!.GetValue(pathField)!;
            input.value = read();
            if (input.value.Any(char.IsControl) || !SamePath(input.value, pickerDirectory))
                throw new InvalidOperationException("Refresh changed picker folder; " + ComparePaths(input.value, pickerDirectory));
            pathField.GetType().GetProperty("LastValue")!.SetValue(pathField, input.value);
            input.value = directory;
            Call(panel, "PollInputs");
            if (!SamePath(panel.Options.OutputDirectory, pickerDirectory))
                throw new InvalidOperationException("Inactive controls changed the output model");
            // Production accepts edits only from active controls. Keep the
            // fixture offscreen while activating it for the submission check.
            parent.transform.position = new Vector3(100000, 100000, 0);
            parent.SetActive(true);
            if (!input.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Path input fixture did not activate");
            typeof(NativeExportPanel).GetField("_encoders", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel,
                new EncoderCapability[] { new("h264_qsv", "h264", true, true, "fixture") });
            ExportOptions? submitted = null;
            panel.ExportRequested = options => submitted = options;
            Call(panel, "RequestExport");
            if (submitted == null || submitted.OutputDirectory.Any(char.IsControl)
                || !SamePath(submitted.OutputDirectory, directory))
                throw new InvalidOperationException("Submission changed folder; " + ComparePaths(submitted?.OutputDirectory, directory));
            return submitted;
        }
        finally { Object.Destroy(parent); }
    }

    private static void CheckInvalidSubmission()
    {
        using var panel = new NativeExportPanel();
        typeof(NativeExportPanel).GetField("_encoders", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel,
            new EncoderCapability[] { new("h264_qsv", "h264", true, true, "fixture") });
        int submissions = 0;
        panel.ExportRequested = _ => submissions++;
        foreach (var control in new[] { '\n', '\r', '\t', '\0' })
        {
            panel.Options = new ExportOptions { Title = "path-fixture", OutputDirectory = Path.Combine(Plugin.DiagnosticsRoot, "invalid") + control + "ative" };
            Call(panel, "RequestExport");
        }
        if (submissions != 0) throw new InvalidOperationException($"Corrupted path reached export; submissions={submissions}");
    }

    private static void CheckMediaExport()
    {
        string directory = Path.Combine(Plugin.DiagnosticsRoot, "native-input-export");
        var accepted = CheckPanel(directory);
        var options = accepted with
        {
            Title = "path-fixture-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"),
            Width = 96, Height = 64, Fps = 24, AudioQuality = "none", WriteCover = false,
            Encoder = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_ENCODER") ?? "h264_qsv"
        };
        using var session = new ExportSession(options, ResolveMediaTool("ffmpeg"), ResolveMediaTool("ffprobe"), options.Encoder);
        var frame = new byte[96 * 64 * 4];
        for (int offset = 0; offset < frame.Length; offset += 4) { frame[offset] = 80; frame[offset + 1] = 160; frame[offset + 2] = 220; frame[offset + 3] = 255; }
        for (int i = 0; i < 6; i++) session.WriteFrame(frame, frame.Length);
        var result = session.Complete();
        string? outputDirectory = Path.GetDirectoryName(result.OutputPath);
        if (result.Frames != 6 || !File.Exists(result.OutputPath) || !SamePath(outputDirectory, directory))
            throw new InvalidOperationException($"Output was not written to the chosen folder; frames={result.Frames}, outputExists={File.Exists(result.OutputPath)}, {ComparePaths(outputDirectory, directory)}");
    }

    private static string ResolveMediaTool(string name)
    {
        string filename = name + ".exe";
        string? directory = Environment.GetEnvironmentVariable("FFMPEG_DIR");
        var folders = !string.IsNullOrWhiteSpace(directory)
            ? new[] { directory }
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (string folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            string candidate = Path.Combine(folder.Trim().Trim('"'), filename);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("Set FFMPEG_DIR or PATH for the explicitly enabled media fixture.", filename);
    }

    private static void Call(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);

    private static string DescribeFailure(Exception error)
    {
        var cause = Unwrap(error);
        string message = DiagnosticValue(cause.Message);
        string? location = cause.StackTrace?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
        return cause.GetType().Name + (message.Length == 0 ? "" : ": " + message)
            + (string.IsNullOrWhiteSpace(location) ? "" : " [" + DiagnosticValue(location.Trim()) + "]");
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }

    private static bool SamePath(string? actual, string? expected)
    {
        if (!OutputDirectoryPreference.TryNormalize(actual, out var actualPath)
            || !OutputDirectoryPreference.TryNormalize(expected, out var expectedPath)) return false;
        return string.Equals(actualPath, expectedPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string ComparePaths(string? actual, string? expected) =>
        $"actual={DiagnosticValue(actual)}; expected={DiagnosticValue(expected)}; " +
        $"actualChars={actual?.Length ?? -1}; actualControls={actual?.Count(char.IsControl) ?? -1}; " +
        $"expectedChars={expected?.Length ?? -1}; expectedControls={expected?.Count(char.IsControl) ?? -1}; " +
        $"sameNormalizedPath={SamePath(actual, expected)}";

    private static string DiagnosticValue(string? value)
    {
        string result = value ?? "<null>";
        string? configuredPath = null;
        try
        {
            configuredPath = typeof(AAVideoExport.Plugin.Plugin)
                .GetProperty("OutputDirectory", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as string;
        }
        catch { }
        foreach (string sensitivePath in new[] { Plugin.DiagnosticsRoot, configuredPath }
            .Where(path => !string.IsNullOrWhiteSpace(path))!)
            result = result.Replace(sensitivePath, "<configured-path>",
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return result.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }
}
