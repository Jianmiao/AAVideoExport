using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

var tests = new (string Name, Action Run)[]
{
    ("export folder restores on host restart and story changes retain it", () =>
    {
        string directory = Path.GetFullPath("artifacts/remembered-export-folder");
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        Check(panel.Options.OutputDirectory == Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "first launch must use Videos itself, without an AA Exports child");
        panel.OutputDirectoryCommitted!(directory);
        host.Shutdown();
        host = CreateHost();
        panel = Field<NativeExportPanel>(host, "_panel");
        Check(panel.Options.OutputDirectory == directory, "host restart discarded remembered folder");
        panel.SetStoryTitle("another-story");
        Check(panel.Options.OutputDirectory == directory, "story selection reset remembered folder");
        panel.OutputDirectoryCommitted!("relative-folder");
        Check(Plugin.OutputDirectory == directory, "invalid folder overwrote saved preference");
        host.Shutdown();
    }),
    ("remembered folder validation does not create folders and rejects invalid values", () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "aa-export-preference-" + Guid.NewGuid().ToString("N"));
        Check(OutputDirectoryPreference.TryNormalize(directory, out var normalized) && normalized == directory, "absolute folder rejected");
        Check(!Directory.Exists(directory), "remembering folder created it");
        foreach (string? invalid in new string?[] { null, "", "  ", "relative-folder", directory + "\0", directory + "\n", directory + "*", directory + "?", directory + "\"" })
            Check(!OutputDirectoryPreference.TryNormalize(invalid, out _), "invalid folder accepted");
        if (OperatingSystem.IsWindows()) Check(!OutputDirectoryPreference.TryNormalize(directory + ":stream", out _), "alternate stream accepted as folder");
        Check(OutputDirectoryPreference.Resolve("relative-folder") == Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "invalid saved configuration prevented Videos default");
        string escapedSegment = Path.GetFullPath(Path.Combine("artifacts", "native-compare-final-20261002"));
        string configValue = OutputDirectoryPreference.ToPortableValue(escapedSegment);
        Check(!configValue.Contains('\\'), "portable path must not contain NGUI escape backslashes");
        Check(OutputDirectoryPreference.TryNormalize(configValue, out var roundTrip) && roundTrip == escapedSegment, "config path round-trip changed the export folder");
    }),
    ("performance reporting never acquires a target or consumes a failing readback", () =>
    {
        var host = CreateHost();
        var options = Options();
        var session = new ExportSession(options, "", "", "fixture", 48000, 2, true);
        var frames = new FramePipeline(options, session, true) { RejectTargetAccess = true };
        Set(host, "_options", options); Set(host, "_frames", frames); Set(host, "_captured", 106L);
        try
        {
            Call(host, "ReportPerformance", true);
            Check(frames.TargetReads == 0, "diagnostic sampling touched the GPU capture path");
        }
        finally { Set(host, "_frames", null); host.Shutdown(); }
    }),
    ("readback failure cancels once even if the diagnostic sink fails and allows a new capture", () =>
    {
        foreach (bool failLog in new[] { false, true })
        {
            var host = CreateHost();
            var options = Options();
            var panel = Field<NativeExportPanel>(host, "_panel");
            panel.Options = options;
            var session = new ExportSession(options, "", "", "fixture", 48000, 2, true);
            var frames = new FramePipeline(options, session, true)
            {
                RejectTargetAccess = true,
                DrainError = new ExportException("gpu_readback_failed", "fixture original readback failure")
            };
            var player = new Test { EndAction = () => { SceneManager.Active = new() { name = "CatalogScene", isLoaded = true }; new CatalogFileInfo(); } };
            var native = new NativeCaptureScope(player, options, new UnityEngine.RenderTexture(), false);
            SceneManager.Active = new() { name = "PlayScene", isLoaded = true };
            Set(host, "_options", options); Set(host, "_frames", frames); Set(host, "_captured", 106L);
            Set(host, "_session", session); Set(host, "_native", native); Set(host, "_player", player);
            int errorsBefore = UnityEngine.Debug.Messages.Count(m => m.Contains("failure=gpu_readback_failed"));
            Plugin.FailPerformanceLog = failLog;
            try
            {
                host.Update();
                Check(!host.Capturing && session.Cancelled && frames.Disposed && native.Disposed, "failure did not release capture and cancel encoder");
                Check(frames.TargetReads == 0, "failure diagnostics acquired another target");
                Field<Task>(host, "_cleanup").GetAwaiter().GetResult();
                for (int i = 0; i < 10; i++) host.Update();
                Check(session.Disposed && player.EndCalls == 1, "cleanup or native end was skipped or repeated");
                Check(panel.Visible && !panel.LastBusy, "failed export left settings busy");
                Check(Field<string>(host, "_status").Contains("显卡读取视频帧失败"), "cleanup masked the localized original error");
                Check(UnityEngine.Debug.Messages.Any(m => m.Contains("failure=gpu_readback_failed")
                    && m.Contains("fixture original readback failure")), "localization lost the original diagnostic from the log");
                Check(UnityEngine.Debug.Messages.Count(m => m.Contains("failure=gpu_readback_failed")) - errorsBefore == 1, "failure loop flooded the log");
                Check(panel.Options == options, "failure reset user's export settings");
                Plugin.FailPerformanceLog = false;
                var nextSession = new ExportSession(options, "", "", "fixture", 48000, 2, true);
                Set(host, "_session", nextSession);
                Call(host, "Begin", new Test());
                Check(host.Capturing, "failed export prevented a fresh capture");
                Call(host, "Cancel");
                Field<Task>(host, "_cleanup").GetAwaiter().GetResult();
                host.Update();
            }
            finally { Plugin.FailPerformanceLog = false; host.Shutdown(); }
        }
    }),
    ("cancel returns to retained settings without selecting a catalog card", () =>
    {
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        panel.Options = Options() with { Width = 1280, Height = 720, BitrateKbps = 8000 };
        var player = new Test { EndAction = () => { SceneManager.Active = new() { name = "CatalogScene", isLoaded = true }; new CatalogFileInfo(); } };
        SceneManager.Active = new() { name = "PlayScene", isLoaded = true };
        Set(host, "_player", player);
        panel.Flow.ObserveBusy(true);
        panel.Flow.AskToCancel();
        Check(panel.Flow.ConfirmCancel(), "cancel confirmation failed");
        Call(host, "Cancel");
        host.Update(); host.Update();
        Check(player.EndCalls == 1, "cancel did not end native playback exactly once");
        Check(panel.Visible, "settings were hidden after native scene returned");
        Check(!panel.LastBusy && panel.Flow.Phase == ExportUiPhase.Settings, "returned catalog without a selected block left cancellation stuck");
        Check(panel.Options.Width == 1280 && panel.Options.Height == 720 && panel.Options.BitrateKbps == 8000, "cancel reset export options");
        // The card's .aas block is intentionally absent after a catalog scene
        // rebuild. A retained stable source key must still resolve a loader
        // shell so the second export can start without reopening the settings.
        Set(host, "_selectedStoryKey", Path.GetFullPath("fixture.aas"));
        new CatalogFileInfo { block = new() { path = null! } };
        var resolved = (ValueTuple<CatalogFileInfo, string>)CallResult(host, "ResolveStory");
        Check(resolved.Item1 != null && resolved.Item2.EndsWith("fixture.aas", StringComparison.OrdinalIgnoreCase), "retry lost the retained story source");
        host.Shutdown();
    }),
    ("returning waits for the native catalog scene before allowing retry", () =>
    {
        var host = CreateHost();
        var player = new Test();
        SceneManager.Active = new() { name = "PlayScene", isLoaded = true };
        Set(host, "_player", player);
        Call(host, "Cancel");
        new CatalogFileInfo { block = new() { path = "stale.aas" } };
        host.Update(); host.Update();
        Check(Field<bool>(host, "_returningToCatalog"), "stale catalog card incorrectly released native load guard");
        SceneManager.Active = new() { name = "CatalogScene", isLoaded = true };
        host.Update(); host.Update();
        Check(!Field<NativeExportPanel>(host, "_panel").LastBusy, "catalog scene never released guard");
        host.Shutdown();
    }),
    ("newly selected story refreshes the export title instead of reusing the previous project", () =>
    {
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        panel.Options = Options() with { Title = "previous-story" };
        AAVideoExport.Plugin.ExportHost.LoadedStoryTitle = "new-story-0";
        Studio.Scripts.StudioCommon.instance = new() { projectName = "old-project-name" };
        host.Update();
        Check(panel.LastProjectName == "new-story-0", "selected story title must take precedence over the stale Studio project name");
        panel.SetStoryTitle("new-story-0");
        Check(panel.Options.Title == "new-story-0", "opening a different story must refresh the editable default filename");
        host.Shutdown();
    })
};
int failed = 0;
foreach (var test in tests)
{
    Object.ResetRegistry();
    UnityEngine.Debug.Messages.Clear();
    Plugin.SavedOutputDirectory = "";
    ScenarioResourceManager.Instance = new();
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed (production ExportHost lifecycle; fake native/encoder boundaries)");
return failed == 0 ? 0 : 1;

static ExportHost CreateHost()
{
    var host = new ExportHost(new IntPtr(1)); host.Awake();
    Set(host, "_probeAttempted", true); Set(host, "_capabilities", new FfmpegCapabilities());
    return host;
}
static ExportOptions Options() => new() { Title = "fixture", OutputDirectory = Path.GetFullPath("artifacts/host-test") };
static T Field<T>(ExportHost host, string name) => (T)typeof(ExportHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
static void Set(ExportHost host, string name, object? value) => typeof(ExportHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);
static void Call(ExportHost host, string name, params object[] args) => typeof(ExportHost).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, args);
static object CallResult(ExportHost host, string name, params object[] args) => typeof(ExportHost).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, args)!;
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
