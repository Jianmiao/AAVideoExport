using System.Reflection;
using System.Text.Json;
using AAVideoExport.Integration;
using AAVideoExport.Plugin;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Runs only with the explicit acceptance opt-in. Observe the original visible
// playback UI and original translator; no text replacement or locale override.
internal static class MultilingualStoryEvidence
{
    private const BindingFlags Members = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<object> Observations = new();
    private static readonly Dictionary<object, long> Passes = new();
    private static readonly HashSet<object> LateQueuedPasses = new();
    private static readonly Dictionary<object, UILabel[]> LabelsByPass = new();
    private static Harmony? hooks;
    private static bool enabled;
    private static int preparationLabels, replayLabels;
    private static int observedFrames, framesWithoutVisibleUi;
    private static Exception? failure;
    private static object? panel;
    private static PropertyInfo? showButtons;
    private static bool originalButtons;
    private static int originalFps, originalVSync;
    private static ExportHost? host;
    internal static void Install(ExportHost host)
    {
        if (Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_MULTILINGUAL") != "1") return;
        enabled = true;
        MultilingualStoryEvidence.host = host;
        originalFps = Application.targetFrameRate; originalVSync = QualitySettings.vSyncCount;
        panel = typeof(ExportHost).GetField("_panel", Members)!.GetValue(host)!;
        showButtons = panel.GetType().GetProperty("ShowPlaybackButtons", Members)!;
        originalButtons = (bool)showButtons.GetValue(panel)!;
        showButtons.SetValue(panel, true);
        hooks = new Harmony("halocue.aave.translation.visual-evidence");
        hooks.Patch(typeof(ExportHost).GetMethod("LateUpdate", Members)!, postfix: new HarmonyMethod(typeof(MultilingualStoryEvidence), nameof(QueueLateLabel)));
        var captureScope = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeCaptureScope", true)!;
        hooks.Patch(captureScope.GetMethod("RenderFrame", Members)!, prefix: new HarmonyMethod(typeof(MultilingualStoryEvidence), nameof(ObserveManualRender)));
    }
    private static void ObserveManualRender() { if (host != null) ObserveFrame(host); }
    private static void QueueLateLabel(ExportHost __instance)
    {
        if (failure != null) return;
        try
        {
            if (!(bool)typeof(ExportHost).GetProperty("Capturing", Members)!.GetValue(__instance)! ||
                (bool)typeof(ExportHost).GetField("_strictPriming", Members)!.GetValue(__instance)! ||
                (long)typeof(ExportHost).GetField("_captured", Members)!.GetValue(__instance)! != 8) return;
            object native = typeof(ExportHost).GetField("_native", Members)!.GetValue(__instance)!;
            if (!LateQueuedPasses.Add(native)) return;
            var label = Object.FindObjectsOfType<UILabel>(true).First(item => item != null && item.gameObject.activeInHierarchy &&
                PathOf(item.transform).EndsWith("FrontUI/Top/AutoBtn/AutoBtn_Enabled/Label_Menu_Locale (1)", StringComparison.Ordinal));
            // A serialized native label can become enabled with its original
            // mText after LateUpdate. Exercise that queue path before rendering.
            label.mText = "AUTO"; label.MarkAsChanged();
            var mod = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "AAMultilingual");
            mod.GetType("AAMultilingual.SystemUiTranslator", true)!.GetMethod("LabelEnabledPostfix", Members)!.Invoke(null, new object[] { label });
        }
        catch (Exception error) { failure = error; }
    }
    private static void ObserveFrame(ExportHost __instance)
    {
        if (failure != null) return;
        try
        {
            if (!(bool)typeof(ExportHost).GetProperty("Capturing", Members)!.GetValue(__instance)! || !RenderControlV1.IsRenderingOwned ||
                (bool)typeof(ExportHost).GetField("_strictPriming", Members)!.GetValue(__instance)!) return;
            long frame = (long)typeof(ExportHost).GetField("_captured", Members)!.GetValue(__instance)!;
            var native = typeof(ExportHost).GetField("_native", Members)!.GetValue(__instance)!;
            if (Passes.TryGetValue(native, out long previous) && previous == frame) return;
            Passes[native] = frame;
            bool preparation = (bool)typeof(ExportHost).GetField("_strictPreparing", Members)!.GetValue(__instance)!;
            if (!LabelsByPass.TryGetValue(native, out var labels) || labels.Length == 0)
                LabelsByPass[native] = labels = Object.FindObjectsOfType<UILabel>(true)
                .Where(label => label != null &&
                    PathOf(label.transform).Contains("FrontUI/Top/AutoBtn/", StringComparison.Ordinal) &&
                    label.name.StartsWith("Label_Menu_Locale", StringComparison.Ordinal))
                .ToArray();
            observedFrames++;
            var visible = labels.Where(label => label != null && label.gameObject.activeInHierarchy).ToArray();
            // Native opening/transition frames may legitimately hide all
            // controls. Validate both enabled and disabled AUTO variants when
            // visible; never create UI just to make a check pass.
            if (visible.Length == 0) framesWithoutVisibleUi++;
            foreach (var label in visible)
            {
                if (label == null || !label.gameObject.activeInHierarchy || label.text != "自动" ||
                    label.maxLineCount != 1 || label.overflowMethod != UILabel.Overflow.ShrinkContent)
                    throw new InvalidOperationException("Native playback text/layout lost translation: " + label?.text);
                if (frame is 0 or 8) Observations.Add(new { preparation, frame, path = PathOf(label.transform), text = label.text,
                    lines = label.maxLineCount, width = label.width, height = label.height,
                    font = label.trueTypeFont == null ? "" : label.trueTypeFont.name });
            }
            if (preparation) preparationLabels += visible.Length; else replayLabels += visible.Length;
        }
        catch (Exception error) { failure = error; }
    }
    private static string PathOf(Transform transform)
    {
        var parts = new List<string>();
        for (var cursor = transform; cursor != null; cursor = cursor.parent) parts.Add(cursor.name);
        parts.Reverse(); return string.Join("/", parts);
    }
    internal static bool Finish()
    {
        if (!enabled) return true;
        hooks?.UnpatchSelf();
        showButtons?.SetValue(panel, originalButtons);
        if (host != null && (bool)typeof(ExportHost).GetProperty("Busy", Members)!.GetValue(host)!)
            failure ??= new InvalidOperationException("Host still busy at translation restoration check.");
        // The settings panel legitimately owns a separate render lease while
        // open. Use its normal idle close path before checking the last release.
        if (panel is NativeExportPanel exportPanel) exportPanel.Visible = false;
        var adapter = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.MultilingualCaptureAdapter")!;
        bool activeAfter = (bool)adapter.GetProperty("Active", Members)!.GetValue(null)!;
        bool ownedAfter = RenderControlV1.IsRenderingOwned;
        int leases = (int)typeof(RenderControlV1).GetField("_leases", Members)!.GetValue(null)!;
        bool passed = failure == null && preparationLabels > 0 && replayLabels > 0 &&
            LateQueuedPasses.Count >= 2 && !activeAfter && !ownedAfter &&
            Application.targetFrameRate == originalFps && QualitySettings.vSyncCount == originalVSync;
        File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "multilingual-story-evidence.json"), JsonSerializer.Serialize(
            new { Passed = passed, PreparationLabels = preparationLabels, ReplayLabels = replayLabels,
                ObservedFrames = observedFrames, FramesWithoutVisibleUi = framesWithoutVisibleUi,
                Observations, ActiveAfter = activeAfter, RenderOwnedAfter = ownedAfter, LeasesAfter = leases,
                FpsBefore = originalFps, VSyncBefore = originalVSync,
                FpsAfter = Application.targetFrameRate, VSyncAfter = QualitySettings.vSyncCount, LateQueuedPasses = LateQueuedPasses.Count,
                Error = failure?.ToString(), Scope = "Original visible native AUTO label during both export passes; no locale/config writes." },
            new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Report($"PATH-CHECK {(passed ? "PASS" : "FAIL")} original-translated-playback-ui prep={preparationLabels}; replay={replayLabels}; error={failure?.Message}");
        return passed;
    }
}
