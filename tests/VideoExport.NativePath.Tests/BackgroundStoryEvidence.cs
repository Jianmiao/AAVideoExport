using System.Collections;
using System.Reflection;
using System.Text.Json;
using AAVideoExport.Plugin;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Explicit synthetic input fixture: provide one local dynamic background via
// AA's real resource resolver, then observe the real Mod/Host/render path.
// The selected .aas, Mod DLLs and configuration files remain read-only.
internal static class BackgroundStoryEvidence
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static string assets = "";
    private static ExportHost? host;
    private static Harmony? hooks;
    private static Type? adapter;
    private static long checkedFrames, visibleFrames;
    private static readonly Dictionary<object, long> LastFrame = new();
    private static readonly Dictionary<int, byte[]> Pixels = new();
    private static readonly List<object> Samples = new();
    private static Exception? failure;
    private static readonly Dictionary<object, int> LastRow = new();
    private static readonly List<object> Rows = new();
    private static readonly List<object> Waits = new();
    internal static void Install(ExportHost exportHost)
    {
        assets = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_BACKGROUND_STORY_ASSETS") ?? "";
        if (assets.Length == 0) return;
        if (!Path.IsPathFullyQualified(assets) || !File.Exists(Path.Combine(assets, "controlled-bg.mkv")))
            throw new InvalidOperationException("Background story needs explicit isolated fixture assets.");
        host = exportHost;
        adapter = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.ControlledBackgroundVideoAdapter", true)!;
        for (int i = 0; i < 4; i++) Pixels[i] = File.ReadAllBytes(Path.Combine(assets, $"frame-{i}.rgba"));
        hooks = new Harmony("halocue.aave.background.story.fixture");
        hooks.Patch(typeof(ScenarioResourceManager).GetMethod("ResolveBackground", Flags)!,
            postfix: new HarmonyMethod(typeof(BackgroundStoryEvidence), nameof(ProvideFixtureResource)));
        var scope = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeCaptureScope", true)!;
        hooks.Patch(scope.GetMethod("RenderFrame", Flags)!, prefix: new HarmonyMethod(typeof(BackgroundStoryEvidence), nameof(ObserveFrame)));
        hooks.Patch(typeof(ScenarioAnimation.WaitAnimation).GetMethod("Start", Flags)!, prefix: new HarmonyMethod(typeof(BackgroundStoryEvidence), nameof(WaitStart)));
        hooks.Patch(typeof(ScenarioAnimation.WaitAnimation).GetMethod("Complete", Flags)!, prefix: new HarmonyMethod(typeof(BackgroundStoryEvidence), nameof(WaitComplete)));
        foreach (var type in new[] { typeof(ScenarioAnimation.WaitAnimation), typeof(Test) })
        foreach (var nested in type.GetNestedTypes(Flags).Where(t => t.Name.Contains("_WaitTask_d__") || t.Name.Contains("_DelayedAdvance_d__")))
            hooks.Patch(nested.GetMethod("MoveNext", Flags)!, postfix: new HarmonyMethod(typeof(BackgroundStoryEvidence), nameof(WaitYield)));
        Plugin.Report("PATH-CHECK BACKGROUND fixture=synthetic-local-resource; original-story-bytes-unchanged; assets=" + assets);
    }
    private static void WaitStart(ScenarioAnimation.WaitAnimation __instance) => RecordWait("start", __instance.delayInSeconds, "");
    private static void WaitComplete(ScenarioAnimation.WaitAnimation __instance) => RecordWait("complete", __instance.delayInSeconds, "");
    private static void WaitYield(object __instance, bool __result)
    {
        try
        {
            if (!__result) return;
            var current = __instance.GetType().GetProperty("__2__current", Flags)?.GetValue(__instance) as Il2CppSystem.Object;
            RecordWait("yield", 0, current?.GetIl2CppType().FullName ?? "null");
        }
        catch (Exception error) { failure ??= error; }
    }
    private static void RecordWait(string kind, float delay, string yieldedType)
    {
        if(host == null || !(bool)typeof(ExportHost).GetProperty("Capturing", Flags)!.GetValue(host)!) return;
        Waits.Add(new { kind, delay, yieldedType,
            preparation = (bool)typeof(ExportHost).GetField("_strictPreparing", Flags)!.GetValue(host)!,
            frame = (long)typeof(ExportHost).GetField("_captured", Flags)!.GetValue(host)!,
            time = Time.time, unscaled = Time.unscaledTime });
    }
    private static void ProvideFixtureResource(uint __0, ref BackgroundResource __result)
    {
        if (host == null || __0 == 0 || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "MainScene") return;
        __result = new BackgroundResource(__0, "controlled-bg", ScenarioBGType.Image,
            ScenarioResourceManager.ResourceType.LocalCustom, path: "controlled-bg.png", root: assets, spine: null);
    }
    private static void ObserveFrame()
    {
        if (failure != null || host == null) return;
        try
        {
            if (!(bool)typeof(ExportHost).GetProperty("Capturing", Flags)!.GetValue(host)! ||
                (bool)typeof(ExportHost).GetField("_strictPriming", Flags)!.GetValue(host)!) return;
            long frame = (long)typeof(ExportHost).GetField("_captured", Flags)!.GetValue(host)!;
            object scope = typeof(ExportHost).GetField("_native", Flags)!.GetValue(host)!;
            if (LastFrame.TryGetValue(scope, out var last) && last == frame) return;
            LastFrame[scope] = frame;
            var player = typeof(ExportHost).GetField("_player", Flags)!.GetValue(host) as Test;
            if (player != null && (!LastRow.TryGetValue(scope, out int previousRow) || previousRow != player.cur))
            {
                LastRow[scope] = player.cur;
                Rows.Add(new { preparation = (bool)typeof(ExportHost).GetField("_strictPreparing", Flags)!.GetValue(host)!,
                    frame, row = player.cur, virtualTime = frame /
                        (double)((AAVideoExport.Core.ExportOptions)typeof(ExportHost).GetField("_options", Flags)!.GetValue(host)!).Fps, unityTime = Time.time,
                    nativeUnscaled = Time.unscaledTime, unityDelta = Time.deltaTime });
            }
            var commands = ((IEnumerable)adapter!.GetProperty("CommandTrace", Flags)!.GetValue(null)!).Cast<object>().ToArray();
            var start = commands.FirstOrDefault(command => command.GetType().GetProperty("Operation")!.GetValue(command) as string == "Start");
            if (start == null) return;
            long startFrame = (long)start.GetType().GetProperty("Frame")!.GetValue(start)!;
            var options = (AAVideoExport.Core.ExportOptions)typeof(ExportHost).GetField("_options", Flags)!.GetValue(host)!;
            int sourceFrame = checked((int)(((frame - startFrame) * 10 / options.Fps) % 4));
            foreach (var presenter in Object.FindObjectsOfType<BackgroundPresenter>())
            foreach (var layer in new[] { presenter.main, presenter.overlap })
            {
                if (layer?.Content?.Resource?.Name != "controlled-bg" || layer.Image == null) continue;
                var texture = layer.Image.mainTexture?.TryCast<Texture2D>();
                if (texture == null || texture.width != 8 || texture.height != 4)
                    throw new InvalidOperationException("Story background was not served by the controlled texture backend.");
                var pixels = texture.GetPixels32();
                var expected = Pixels[sourceFrame];
                for (int y = 0; y < 4; y++)
                for (int x = 0; x < 8; x++)
                {
                    var actual = pixels[y * 8 + x]; int at = ((3 - y) * 8 + x) * 4;
                    if (actual.r != expected[at] || actual.g != expected[at + 1] || actual.b != expected[at + 2])
                        throw new InvalidOperationException($"Story video source-frame mismatch at output={frame}; source={sourceFrame}.");
                }
                checkedFrames++;
                if (layer.Image.gameObject.activeInHierarchy && layer.Image.alpha > .001f) visibleFrames++;
                if (Samples.Count < 24) Samples.Add(new { frame, startFrame, sourceFrame, alpha = layer.Image.alpha });
            }
        }
        catch (Exception error) { failure = error; }
    }
    internal static bool Finish()
    {
        if (assets.Length == 0) return true;
        hooks?.UnpatchSelf();
        bool active = adapter?.GetProperty("Active", Flags)?.GetValue(null) is true;
        bool passed = failure == null && checkedFrames >= 100 && visibleFrames > 0 && !active;
        File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "background-story-evidence.json"), JsonSerializer.Serialize(
            new { Passed = passed, CheckedFrames = checkedFrames, VisibleFrames = visibleFrames, ActiveAfter = active, Samples, Rows, Waits,
                Error = failure?.ToString(), Scope = "Synthetic background asset input via native resolver; actual Mod callbacks and exported story." },
            new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Report($"PATH-CHECK {(passed ? "PASS" : "FAIL")} original-background-story-pixels frames={checkedFrames}; visible={visibleFrames}; error={failure?.Message}");
        return passed;
    }
}
