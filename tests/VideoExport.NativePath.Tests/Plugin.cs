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
        Report = message => Log.LogInfo(message);
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
        Report("PATH-CHECK RUN pid=" + Environment.ProcessId);
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
                Plugin.Report("PATH-CHECK FAIL " + test.Name + ": " +
                    (error is TargetInvocationException invocation ? invocation.InnerException?.GetType().Name : error.GetType().Name));
            }
        }
        try { CheckInvalidSubmission(); passed++; Plugin.Report("PATH-CHECK PASS control-characters-blocked-before-export"); }
        catch (Exception error) { failed++; Plugin.Report("PATH-CHECK FAIL control-characters: " + error.GetType().Name); }
        if (Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_NATIVE_MEDIA") == "1")
        {
            try { CheckMediaExport(); passed++; Plugin.Report("PATH-CHECK PASS real-encoder-output-in-native-folder"); }
            catch (Exception error) { failed++; Plugin.Report("PATH-CHECK FAIL real-encoder: " + error.GetType().Name); }
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
            if (widgetValue.Any(char.IsControl) || Path.GetFullPath(widgetValue) != Path.GetFullPath(directory))
            {
                Plugin.Report($"PATH-CHECK widget_controls={widgetValue.Count(char.IsControl)} same_path=false");
                throw new InvalidOperationException("Widget changed folder");
            }
            if (input.label.text.Contains('\n')) throw new InvalidOperationException("Label contains newline");
            // A picker changes the model, then the refresh reads the same getter
            // as BuildSharedFields. Manual input after Start uses the native setter.
            var pickerDirectory = Path.Combine(directory, "next-folder");
            panel.Options = panel.Options with { OutputDirectory = pickerDirectory };
            var read = (Func<string>)pathField.GetType().GetProperty("Read")!.GetValue(pathField)!;
            input.value = read();
            if (input.value.Any(char.IsControl) || Path.GetFullPath(input.value) != Path.GetFullPath(pickerDirectory))
                throw new InvalidOperationException("Refresh changed picker folder");
            pathField.GetType().GetProperty("LastValue")!.SetValue(pathField, input.value);
            input.value = directory;
            typeof(NativeExportPanel).GetField("_encoders", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel,
                new EncoderCapability[] { new("h264_qsv", "h264", true, true, "fixture") });
            ExportOptions? submitted = null;
            panel.ExportRequested = options => submitted = options;
            Call(panel, "RequestExport");
            if (submitted == null || submitted.OutputDirectory.Any(char.IsControl)
                || Path.GetFullPath(submitted.OutputDirectory) != Path.GetFullPath(directory))
                throw new InvalidOperationException("Submission changed folder");
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
        if (submissions != 0) throw new InvalidOperationException("Corrupted path reached export");
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
        if (result.Frames != 6 || !File.Exists(result.OutputPath) || Path.GetDirectoryName(result.OutputPath) != directory)
            throw new InvalidOperationException("Output was not written to the chosen folder");
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
}
