using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Runs the authored fixture through ordinary native playback first. It does
// not bypass ExportHost admission or represent a successful exported movie.
public sealed class ControlledStoryPlaybackChecks : MonoBehaviour
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static ControlledStoryPlaybackChecks? current;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly List<object> events = new();
    private readonly HashSet<int> rows = new();
    private readonly HashSet<string> presets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> videos = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> starts = new(), ends = new();
    private Harmony? hooks;
    private Type? voiceRuntime;
    private object? gate;
    private Test? player;
    private string story = "", hash = "";
    private int stage, settle, requests, expectedRows;
    private int autoHandlerCalls, lastObservedRow = int.MinValue;
    private ScenarioAnimation.TitleAnimation? observedTitle;
    private ScenarioAnimation.TitleAnimation._TitleTask_d__8? observedTitleTask;
    private long titleMoveNextCalls, titlePredicate0Calls, titlePredicate1Calls;
    private long mainBackgroundRenderCalls, overlapBackgroundRenderCalls;
    private bool? titlePredicate0Result, titlePredicate1Result;
    private int lastTitleState = int.MinValue;
    private string? lastTitleYield;
    private double nextTitleTaskReport;
    private double nextStateReport;
    private int timeoutSeconds = 120;
    private bool timeoutConfigured;
    private bool selected, loaded, ended, done;
    private Exception? callbackFailure;
    public ControlledStoryPlaybackChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (done) return;
        try
        {
            if (!timeoutConfigured)
            {
                timeoutConfigured = true;
                string? configured = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_TIMEOUT_SECONDS");
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    if (!int.TryParse(configured, out int seconds))
                        throw new InvalidOperationException("AA_VIDEOEXPORT_TEST_TIMEOUT_SECONDS must be an integer.");
                    timeoutSeconds = Math.Clamp(seconds, 30, 120);
                }
            }
            if (callbackFailure != null) throw callbackFailure;
            if (wall.Elapsed.TotalSeconds > timeoutSeconds) throw new TimeoutException("Ordinary mixed-story playback did not complete; stage=" + stage + "; timeoutSeconds=" + timeoutSeconds);
            if (stage == 0) { Initialize(); return; }
            if (stage == 1) { SelectAndLoad(); return; }
            if (stage == 2)
            {
                if (player != null && !ended)
                {
                    if (player.cur != lastObservedRow || wall.Elapsed.TotalSeconds >= nextStateReport)
                    {
                        Snapshot(player, player.cur != lastObservedRow ? "row-change" : "waiting");
                        lastObservedRow = player.cur; nextStateReport = wall.Elapsed.TotalSeconds + 2;
                    }
                    rows.Add(player.cur);
                    foreach (var video in Object.FindObjectsOfType<VideoPlayer>(true))
                        if (video != null && video.isPlaying && video.texture != null && video.url != null
                            && video.url.Contains("controlled-bg", StringComparison.OrdinalIgnoreCase))
                            videos.Add(Path.GetFileName(video.url));
                }
                if (ended && SceneManager.GetActiveScene().name == "CatalogScene")
                {
                    if (requests != 3 || starts.Count != 3 || ends.Count != 3)
                        throw new InvalidOperationException($"Authored voice execution mismatch: requests={requests}, starts={starts.Count}, ends={ends.Count}.");
                    foreach (string kind in new[] { "sway", "spin", "headbutt", "squash" })
                        if (!presets.Contains(kind)) throw new InvalidOperationException("Original MoreEffects preset did not execute: " + kind);
                    if (videos.Count < 2) throw new InvalidOperationException("Both authored dynamic backgrounds must actually play through native VideoPlayer.");
                    if (Enumerable.Range(0, expectedRows).Any(row => !rows.Contains(row))) throw new InvalidOperationException("Native playback omitted an authored row.");
                    if (Hash(story) != hash) throw new InvalidOperationException("Authored input changed while playing.");
                    Finish(null);
                }
            }
        }
        catch (Exception error) { Finish(error); }
    }

    [HideFromIl2Cpp]
    private void Initialize()
    {
        if (SceneManager.GetActiveScene().name != "CatalogScene" || !ResourcesReady()) return;
        story = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_STORY") ?? "";
        string root = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_FIXTURE_WORKSPACE") ?? "";
        if (!Path.IsPathFullyQualified(story) || !File.Exists(story) || !File.Exists(Path.Combine(root, "controlled-story-recipe.json")))
            throw new InvalidOperationException("Use the independent authored mixed-story fixture.");
        hash = Hash(story);
        using var recipe = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "controlled-story-recipe.json")));
        if (recipe.RootElement.GetProperty("StorySha256").GetString() != hash) throw new InvalidOperationException("Fixture recipe and story differ.");
        expectedRows = recipe.RootElement.TryGetProperty("CompiledRows", out var compiledRows)
            ? compiledRows.GetArrayLength() : recipe.RootElement.GetProperty("Rows").GetArrayLength();
        if (expectedRows < 7) throw new InvalidOperationException("The mixed fixture must retain all authored effect rows.");
        if (!Same(story, ScenarioResourceManager.GetPersistentFilePath("saves", Path.GetFileNameWithoutExtension(story), ".aas")))
            throw new InvalidOperationException("Fixture must use the actual independent native workspace.");
        var voiceAssembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Rukari.CharacterVoice");
        voiceRuntime = voiceAssembly.GetType("Rukari.CharacterVoice.Runtime.VoicePlaybackRuntime", true)!;
        gate = voiceRuntime.GetField("Gate", Flags)!.GetValue(null)!;
        var effects = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Rukari.MoreEffects");
        var presetType = effects.GetType("AzureArchive.VideoTools.Runtime.CharacterPresetRuntime", true)!;
        current = this; hooks = new Harmony("halocue.aa.videoexport.mixed-story-baseline");
        RecordTitlePredicateFieldBinding();
        hooks.Patch(AccessTools.Method(typeof(Test), nameof(Test.Start)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterStart)));
        hooks.Patch(AccessTools.Method(typeof(Test), nameof(Test.ToggleAuto)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterToggleAuto)));
        hooks.Patch(AccessTools.Method(typeof(Test), nameof(Test.OnReady)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterReady)));
        hooks.Patch(AccessTools.Method(typeof(Test), nameof(Test.OnAnimStop)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterAnimationStop)));
        hooks.Patch(AccessTools.Method(typeof(Test), nameof(Test.AdvanceScenario)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterAdvance)));
        hooks.Patch(AccessTools.Method(typeof(ScenarioAnimation.TitleAnimation), nameof(ScenarioAnimation.TitleAnimation.Start)), prefix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(BeforeTitleStart)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterTitleStart)));
        hooks.Patch(AccessTools.Method(typeof(ScenarioAnimation.TitleAnimation), nameof(ScenarioAnimation.TitleAnimation.Complete)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterTitleComplete)));
        // These are the actual generated IL2CPP state-machine and callback
        // methods. Observers never call MoveNext, evaluate a predicate, change
        // a yield, or complete the title on the native player's behalf.
        hooks.Patch(AccessTools.Method(typeof(ScenarioAnimation.TitleAnimation._TitleTask_d__8), nameof(ScenarioAnimation.TitleAnimation._TitleTask_d__8.MoveNext)), prefix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(BeforeTitleMoveNext)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterTitleMoveNext)));
        hooks.Patch(AccessTools.Method(typeof(ScenarioAnimation.TitleAnimation), nameof(ScenarioAnimation.TitleAnimation._TitleTask_b__8_0)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterTitlePredicate0)));
        hooks.Patch(AccessTools.Method(typeof(ScenarioAnimation.TitleAnimation), nameof(ScenarioAnimation.TitleAnimation._TitleTask_b__8_1)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterTitlePredicate1)));
        hooks.Patch(AccessTools.Method(typeof(ScenarioAnimation.TitleAnimation), nameof(ScenarioAnimation.TitleAnimation.__ctor_b__5_0)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterTitleCompletionDelegate)));
        hooks.Patch(AccessTools.Method(typeof(PlayerSettings.TitleSettings), nameof(PlayerSettings.TitleSettings.Awake)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterTitleSettingsAwake)));
        hooks.Patch(AccessTools.Method(typeof(BackgroundPresenter.Layer), nameof(BackgroundPresenter.Layer.OnImageRendered)), postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterBackgroundLayerRendered)));
        hooks.Patch(AccessTools.Method(typeof(Test), nameof(Test.End)), prefix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(BeforeEnd)));
        hooks.Patch(voiceRuntime.GetMethod("SetVoicePrefix", Flags)!, postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterVoiceRequest)));
        hooks.Patch(voiceRuntime.GetMethod("ObserveOwnedPlayback", Flags)!, postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterVoiceObservation)));
        hooks.Patch(presetType.GetMethod("Begin", Flags)!, postfix: new HarmonyMethod(typeof(ControlledStoryPlaybackChecks), nameof(AfterPreset)));
        stage = 1;
    }

    [HideFromIl2Cpp]
    private void SelectAndLoad()
    {
        if (!ResourcesReady()) return;
        var all = Resources.FindObjectsOfTypeAll<CatalogFileItem>();
        var row = all.FirstOrDefault(item => item != null && item.gameObject.scene.name == "CatalogScene" && Same(item.infoBlock?.path, story));
        if (row == null) return;
        if (!row.gameObject.activeInHierarchy)
        {
            for (var parent = row.transform.parent; parent != null; parent = parent.parent)
            {
                var group = parent.GetComponent<EpisodeGroup>();
                if (group == null) continue;
                if (!group.expanded) group.Expand();
                return;
            }
            return;
        }
        if (!selected) { selected = true; row.Select(false); return; }
        var card = Object.FindObjectsOfType<CatalogFileInfo>().FirstOrDefault(item => item != null
            && item.gameObject.activeInHierarchy && item.controlPanel != null && item.controlPanel.activeInHierarchy && Same(item.block?.path, story));
        if (card == null || ++settle < 2 || loaded) return;
        PersistentData.saveData = ScenarioResourceManager.Instance.LoadGenericScenario(Path.GetFileNameWithoutExtension(story));
        if (PersistentData.saveData == null || PersistentData.saveData.DataListLength != expectedRows) throw new InvalidOperationException("Native frozen fixture has the wrong row count.");
        PersistentData.projectPath = story; loaded = true;
        card.StartCoroutine(card.CoLoadSave()); stage = 2;
    }

    [HideFromIl2Cpp]
    private static void AfterStart(Test __instance)
    {
        var run = current;
        if (run == null || !run.loaded || __instance.previewMode || !Same(PersistentData.projectPath, run.story)) return;
        run.player = __instance;
        run.Snapshot(__instance, "start-before-native-auto-handler");
        // Use the native player handler, which owns AUTO scheduling and
        // selection notification. Toggling only the widget is not its contract.
        if (!__instance.auto) __instance.ToggleAuto();
        run.Snapshot(__instance, "start-after-native-auto-handler");
        if (!__instance.auto || !__instance.IsAutoEnabled)
            run.callbackFailure = new InvalidOperationException("Native Test.ToggleAuto did not enable both raw auto and IsAutoEnabled for ordinary playback.");
        run.events.Add(new { kind = "start", wall = run.wall.Elapsed.TotalSeconds, rawAuto = __instance.auto, auto = __instance.IsAutoEnabled, run.autoHandlerCalls });
    }
    [HideFromIl2Cpp]
    private static void BeforeEnd(Test __instance)
    {
        var run = current;
        if (run?.player == null || run.player.Pointer != __instance.Pointer) return;
        run.ended = true; run.events.Add(new { kind = "end", wall = run.wall.Elapsed.TotalSeconds, row = __instance.cur });
    }
    [HideFromIl2Cpp]
    private static void AfterToggleAuto(Test __instance, bool __runOriginal)
    {
        var run = For(__instance); if (run == null) return;
        if (__runOriginal) run.autoHandlerCalls++;
        run.Snapshot(__instance, "native-ToggleAuto-original=" + __runOriginal);
    }
    [HideFromIl2Cpp]
    private static void AfterReady(Test __instance) => For(__instance)?.Snapshot(__instance, "native-OnReady");
    [HideFromIl2Cpp]
    private static void AfterAnimationStop(Test __instance) => For(__instance)?.Snapshot(__instance, "native-OnAnimStop");
    [HideFromIl2Cpp]
    private static void AfterAdvance(Test __instance, bool __runOriginal)
    {
        var run = For(__instance); if (run == null) return;
        if (__runOriginal && __instance.cur >= 0) run.rows.Add(__instance.cur);
        run.Snapshot(__instance, "native-Advance-original=" + __runOriginal);
    }
    [HideFromIl2Cpp]
    private static void BeforeTitleStart(ScenarioAnimation.TitleAnimation __instance)
    {
        var run = For(__instance.owner); if (run == null) return;
        run.observedTitle = __instance;
        run.Snapshot(__instance.owner, "native-title-before-start");
    }
    [HideFromIl2Cpp]
    private static void AfterTitleStart(ScenarioAnimation.TitleAnimation __instance) => For(__instance.owner)?.Snapshot(__instance.owner, "native-title-start");
    [HideFromIl2Cpp]
    private static void AfterTitleComplete(ScenarioAnimation.TitleAnimation __instance) => For(__instance.owner)?.Snapshot(__instance.owner, "native-title-complete");
    [HideFromIl2Cpp]
    private static void BeforeTitleMoveNext(ScenarioAnimation.TitleAnimation._TitleTask_d__8 __instance, out int __state)
    {
        __state = int.MinValue;
        var run = For(__instance.__4__this?.owner); if (run == null) return;
        try
        {
            run.observedTitle = __instance.__4__this;
            run.observedTitleTask = __instance;
            __state = __instance.__1__state;
        }
        catch (Exception error) { run.RecordDiagnosticFailure("title-MoveNext-prefix", error); }
    }
    [HideFromIl2Cpp]
    private static void AfterTitleMoveNext(ScenarioAnimation.TitleAnimation._TitleTask_d__8 __instance, int __state, bool __result, bool __runOriginal)
    {
        var animation = __instance.__4__this;
        var run = For(animation?.owner); if (run == null || animation == null) return;
        try
        {
            run.titleMoveNextCalls++;
            int state = __instance.__1__state;
            string? yielded = __instance.__2__current?.GetIl2CppType().FullName;
            bool changed = state != run.lastTitleState || yielded != run.lastTitleYield;
            run.lastTitleState = state; run.lastTitleYield = yielded;
            if (changed || run.titleMoveNextCalls <= 16 || run.wall.Elapsed.TotalSeconds >= run.nextTitleTaskReport)
            {
                run.Record(new { kind = "title-move-next", wall = run.wall.Elapsed.TotalSeconds, frame = Time.frameCount,
                    before = __state, after = state, returned = __result, original = __runOriginal, yielded, calls = run.titleMoveNextCalls });
                run.Snapshot(animation.owner, "native-title-MoveNext");
                run.nextTitleTaskReport = run.wall.Elapsed.TotalSeconds + 2;
            }
        }
        catch (Exception error) { run.RecordDiagnosticFailure("title-MoveNext-postfix", error); }
    }
    [HideFromIl2Cpp]
    private static void AfterTitlePredicate0(ScenarioAnimation.TitleAnimation __instance, bool __result, bool __runOriginal)
        => For(__instance.owner)?.RecordTitlePredicate(__instance, 0, __result, __runOriginal);
    [HideFromIl2Cpp]
    private static void AfterTitlePredicate1(ScenarioAnimation.TitleAnimation __instance, bool __result, bool __runOriginal)
        => For(__instance.owner)?.RecordTitlePredicate(__instance, 1, __result, __runOriginal);
    [HideFromIl2Cpp]
    private void RecordTitlePredicate(ScenarioAnimation.TitleAnimation animation, int index, bool result, bool original)
    {
        try
        {
            bool changed;
            if (index == 0) { titlePredicate0Calls++; changed = titlePredicate0Result != result; titlePredicate0Result = result; }
            else { titlePredicate1Calls++; changed = titlePredicate1Result != result; titlePredicate1Result = result; }
            if (!changed) return;
            Record(new { kind = "title-predicate", index, result, original, wall = wall.Elapsed.TotalSeconds, frame = Time.frameCount });
            Snapshot(animation.owner, "native-title-predicate-" + index);
        }
        catch (Exception error) { RecordDiagnosticFailure("title-predicate-" + index, error); }
    }
    [HideFromIl2Cpp]
    private static void AfterTitleCompletionDelegate(ScenarioAnimation.TitleAnimation __instance, bool __runOriginal)
        => For(__instance.owner)?.Snapshot(__instance.owner, "native-title-completion-delegate-original=" + __runOriginal);
    [HideFromIl2Cpp]
    private static void AfterTitleSettingsAwake(PlayerSettings.TitleSettings __instance, bool __runOriginal)
    {
        var run = current; if (run == null || !run.loaded) return;
        try
        {
            run.Record(new { kind = "title-settings-awake", original = __runOriginal, wall = run.wall.Elapsed.TotalSeconds,
                frame = Time.frameCount, settings = TitleSettingsState(__instance) });
        }
        catch (Exception error) { run.RecordDiagnosticFailure("title-settings-Awake", error); }
    }
    [HideFromIl2Cpp]
    private static void AfterBackgroundLayerRendered(BackgroundPresenter.Layer __instance, Material __0, bool __runOriginal)
    {
        var run = current;
        var owner = run?.player ?? run?.observedTitle?.owner;
        if (run == null || owner == null || For(owner) == null) return;
        try
        {
            // Read the initialized field; calling Test.Backgrounds here could
            // create/initialize a presenter and change the path under test.
            var presenter = owner.backgroundPresenter;
            if (presenter == null) return;
            bool main = presenter.main != null && presenter.main.Pointer == __instance.Pointer;
            bool overlap = presenter.overlap != null && presenter.overlap.Pointer == __instance.Pointer;
            if (!main && !overlap) return;
            long count = main ? ++run.mainBackgroundRenderCalls : ++run.overlapBackgroundRenderCalls;
            if (count > 3) return;
            run.Record(new { kind = "background-layer-rendered", layer = main ? "main" : "overlap", original = __runOriginal,
                count, row = owner.cur, frame = Time.frameCount, wall = run.wall.Elapsed.TotalSeconds, owner.isBackgroundLatest,
                material = __0?.name, state = BackgroundLayerState(__instance) });
        }
        catch (Exception error) { run.RecordDiagnosticFailure("background-layer-rendered", error); }
    }
    [HideFromIl2Cpp]
    private static ControlledStoryPlaybackChecks? For(Test? instance)
    {
        var run = current;
        if (run == null || !run.loaded || instance == null || instance.previewMode || !Same(PersistentData.projectPath, run.story)) return null;
        return run.player == null || run.player.Pointer == instance.Pointer ? run : null;
    }

    [HideFromIl2Cpp]
    private void Snapshot(Test instance, string point)
    {
        try
        {
            var animations = new List<object>();
            var list = instance.currentAnims;
            if (list != null)
                for (int index = 0; index < list.Count; index++)
                {
                    var animation = list[index];
                    if (animation == null) continue;
                    animations.Add(new { index, type = animation.GetIl2CppType().FullName, animation.isStarted, animation.hasCompleted,
                        animation.isCancelled, animation.isTransparent, taskPresent = animation.currTask != null });
                }
            var title = PlayerSettings.TitleSettings.instance;
            var evidence = new
            {
                kind = "player-state", point, wall = wall.Elapsed.TotalSeconds, row = instance.cur,
                rawAuto = instance.auto, isAutoEnabled = instance.IsAutoEnabled, instance.ready, instance.hasDialog,
                instance.isBackgroundLatest,
                locked = instance.locked.ToString(), instance.stoppedAnimCount, animationCount = list?.Count,
                delayedAdvanceTaskPresent = instance.delayedAdvanceTask != null, instance.autoDelayInSeconds,
                instance.enabled, active = instance.gameObject.activeInHierarchy, autoHandlerCalls,
                selectionAuto = Read(() => instance.selectionManager?.autoModeEnabled),
                autoWidgetA = Read(() => instance.autoToggle?.a?.activeSelf), autoWidgetB = Read(() => instance.autoToggle?.b?.activeSelf),
                time = Time.time, unscaled = Time.unscaledTime, delta = Time.deltaTime, unscaledDelta = Time.unscaledDeltaTime,
                timeScale = Time.timeScale, captureDelta = Time.captureDeltaTime, frame = Time.frameCount,
                background = Application.runInBackground, focused = Application.isFocused,
                batchMode = Application.isBatchMode, renderFrameInterval = OnDemandRendering.renderFrameInterval,
                willCurrentFrameRender = OnDemandRendering.willCurrentFrameRender,
                targetFrameRate = Application.targetFrameRate, vSync = QualitySettings.vSyncCount,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(), cameras = Read(CameraState),
                backgroundRender = Read(() => BackgroundState(instance)),
                titlePresent = title != null, titleRootActive = Read(() => title?.titleRoot?.activeInHierarchy),
                titleAnimationPlaying = Read(() => title?.titleAnimation?.isPlaying),
                titleAnimationEnabled = Read(() => title?.titleAnimation?.enabled),
                titleClip = Read(() => title?.titleAnimation?.clip?.name), titleClipLength = Read(() => title?.titleAnimation?.clip?.length),
                titleAlphaEnabled = Read(() => title?.tweenAlpha?.enabled), titleAlphaFactor = Read(() => title?.tweenAlpha?.tweenFactor),
                titleAlphaIgnoreScale = Read(() => title?.tweenAlpha?.ignoreTimeScale), titleMaskActive = Read(() => title?.preTitleMask?.activeInHierarchy),
                titleTask = Read(TitleTaskState), observedTitleSettings = Read(() => TitleSettingsState(observedTitle?.settings)),
                titleSettingsMatchSingleton = Read(() => observedTitle?.settings != null && title != null && observedTitle.settings.Pointer == title.Pointer),
                animations
            };
            Record(evidence);
        }
        catch (Exception error)
        {
            events.Add(new { kind = "state-read-failed", point, error = error.ToString() });
            Plugin.Report("PATH-CHECK MIXED-PLAYER diagnostic=" + point + "; error=" + error.Message);
        }
    }
    [HideFromIl2Cpp]
    private object TitleTaskState() => new
    {
        present = observedTitleTask != null, calls = titleMoveNextCalls,
        state = observedTitleTask == null ? (int?)null : observedTitleTask.__1__state,
        yielded = observedTitleTask?.__2__current?.GetIl2CppType().FullName,
        titlePredicate0Calls, titlePredicate0Result, titlePredicate1Calls, titlePredicate1Result,
        observedTitle?.isStarted, observedTitle?.hasCompleted, observedTitle?.isCancelled
    };
    [HideFromIl2Cpp]
    private static object? TitleSettingsState(PlayerSettings.TitleSettings? title)
    {
        if (title == null) return null;
        return new
        {
            settingsObject = Read(() => ObjectState(title.gameObject)), title.enabled,
            root = Read(() => ObjectState(title.titleRoot)), mask = Read(() => ObjectState(title.preTitleMask)),
            animationObject = Read(() => ObjectState(title.titleAnimation?.gameObject)),
            animationPlaying = Read(() => title.titleAnimation?.isPlaying), animationEnabled = Read(() => title.titleAnimation?.enabled),
            animationClip = Read(() => title.titleAnimation?.clip?.name), animationClipLength = Read(() => title.titleAnimation?.clip?.length),
            alphaObject = Read(() => ObjectState(title.tweenAlpha?.gameObject)), alphaEnabled = Read(() => title.tweenAlpha?.enabled),
            alphaFactor = Read(() => title.tweenAlpha?.tweenFactor), alphaStarted = Read(() => title.tweenAlpha?.mStarted),
            alphaStartTime = Read(() => title.tweenAlpha?.mStartTime), alphaDuration = Read(() => title.tweenAlpha?.duration),
            alphaIgnoreTimeScale = Read(() => title.tweenAlpha?.ignoreTimeScale), alphaFinishedCount = Read(() => title.tweenAlpha?.onFinished?.Count)
        };
    }
    [HideFromIl2Cpp]
    private static object? ObjectState(GameObject? target)
    {
        if (target == null) return null;
        var hierarchy = new List<object>();
        for (Transform? item = target.transform; item != null && hierarchy.Count < 12; item = item.parent)
            hierarchy.Add(new { item.name, item.gameObject.activeSelf, item.gameObject.activeInHierarchy });
        return new { target.name, target.activeSelf, target.activeInHierarchy, scene = target.scene.name, hierarchy };
    }
    [HideFromIl2Cpp]
    private static object CameraState()
    {
        var cameras = new List<object>();
        foreach (var camera in Object.FindObjectsOfType<Camera>(true))
            if (camera != null) cameras.Add(new { camera.name, camera.enabled, active = camera.gameObject.activeInHierarchy,
                cameraType = camera.cameraType.ToString(), camera.depth, camera.cullingMask, camera.targetDisplay,
                targetTexture = camera.targetTexture?.name, scene = camera.gameObject.scene.name });
        return new { activeCount = Camera.allCamerasCount, cameras };
    }
    [HideFromIl2Cpp]
    private object BackgroundState(Test owner)
    {
        var presenter = owner.backgroundPresenter;
        return new
        {
            owner.isBackgroundLatest, mainBackgroundRenderCalls, overlapBackgroundRenderCalls,
            presenterPointer = presenter?.Pointer.ToString("X"),
            originalMain = Read(() => BackgroundImageState(owner.background)),
            originalOverlap = Read(() => BackgroundImageState(owner.subBackground)),
            main = Read(() => BackgroundLayerState(presenter?.main)), overlap = Read(() => BackgroundLayerState(presenter?.overlap)),
            mainMatchesOriginal = Read(() => presenter?.main?.Image != null && owner.background != null && presenter.main.Image.Pointer == owner.background.Pointer),
            overlapMatchesOriginal = Read(() => presenter?.overlap?.Image != null && owner.subBackground != null && presenter.overlap.Image.Pointer == owner.subBackground.Pointer)
        };
    }
    [HideFromIl2Cpp]
    private static object? BackgroundLayerState(BackgroundPresenter.Layer? layer)
    {
        if (layer == null) return null;
        return new
        {
            pointer = layer.Pointer.ToString("X"), layer.Alpha, layer.Queue,
            renderedPresent = layer.Rendered != null, renderedPointer = layer.Rendered?.Pointer.ToString("X"),
            image = Read(() => BackgroundImageState(layer.Image)),
            contentPointer = layer.Content?.Pointer.ToString("X"), contentImage = Read(() => layer.Content?.Image?.name),
            panel = Read(() => ObjectState(layer.Panel?.gameObject)), panelAlpha = Read(() => layer.Panel?.alpha)
        };
    }
    [HideFromIl2Cpp]
    private static object? BackgroundImageState(UITexture? image)
    {
        if (image == null) return null;
        var draw = image.drawCall;
        return new
        {
            pointer = image.Pointer.ToString("X"), image.enabled, image.alpha, image.isVisible,
            image.mIsVisibleByAlpha, image.mIsVisibleByPanel, image.mLastAlpha,
            sceneObject = Read(() => ObjectState(image.gameObject)),
            onRenderPresent = image.mOnRender != null, onRenderPointer = image.mOnRender?.Pointer.ToString("X"),
            texture = Read(() => image.mainTexture?.name), texturePointer = Read(() => image.mainTexture?.Pointer.ToString("X")),
            drawCallPresent = draw != null, drawCallPointer = draw?.Pointer.ToString("X"),
            drawCallActive = Read(() => draw?.gameObject.activeInHierarchy), drawCallEnabled = Read(() => draw?.enabled),
            drawCallOnRenderPresent = Read(() => draw?.onRender != null), drawCallWidgetCount = Read(() => draw?.widgetCount),
            rendererEnabled = Read(() => draw?.mRenderer?.enabled), rendererVisible = Read(() => draw?.mRenderer?.isVisible)
        };
    }
    [HideFromIl2Cpp]
    private void Record(object evidence) { events.Add(evidence); Plugin.Report("PATH-CHECK MIXED-PLAYER " + JsonSerializer.Serialize(evidence)); }
    [HideFromIl2Cpp]
    private void RecordDiagnosticFailure(string point, Exception error)
        => Record(new { kind = "diagnostic-read-failed", point, error = error.ToString() });
    [HideFromIl2Cpp]
    private void RecordTitlePredicateFieldBinding()
    {
        try
        {
            // The pinned AA 101 GameAssembly predicate0 at RVA 0x00889400
            // reads [TitleAnimation + 0x28], then returns its byte at +0x138.
            // Resolve those offsets through the actual loaded IL2CPP metadata;
            // do not infer names from managed/native declaration ordering.
            var ownerField = typeof(ScenarioAnimation.ScenarioAnimation).GetField("NativeFieldInfoPtr_owner", Flags)
                ?? throw new MissingFieldException("Native ScenarioAnimation.owner metadata field is missing.");
            var ownerPointer = (IntPtr)ownerField.GetValue(null)!;
            var matches = new List<object>();
            foreach (var field in typeof(Test).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
            {
                const string prefix = "NativeFieldInfoPtr_";
                if (!field.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var pointer = (IntPtr)field.GetValue(null)!;
                uint offset = IL2CPP.il2cpp_field_get_offset(pointer);
                if (offset != 0x138) continue;
                string name = field.Name.Substring(prefix.Length);
                matches.Add(new { name, offset, managedType = typeof(Test).GetProperty(name, Flags)?.PropertyType.FullName });
            }
            Record(new { kind = "title-predicate-field-binding", nativePredicateRva = "0x00889400",
                ownerField = "ScenarioAnimation.ScenarioAnimation.owner", ownerOffset = IL2CPP.il2cpp_field_get_offset(ownerPointer),
                dependencyOffset = 0x138, matches,
                meaning = "Read-only field metadata resolution for the actual installed predicate body; no predicate invocation or field write." });
        }
        catch (Exception error) { RecordDiagnosticFailure("title-predicate-field-binding", error); }
    }
    [HideFromIl2Cpp]
    private static object? Read(Func<object?> read) { try { return read(); } catch (Exception error) { return "unavailable:" + error.GetType().Name; } }
    [HideFromIl2Cpp]
    private static void AfterVoiceRequest(string __0)
    {
        var run = current;
        if (run?.player == null || !__0.StartsWith("aavt/voice/", StringComparison.Ordinal)) return;
        run.requests++; run.events.Add(new { kind = "voice-request", voice = __0, row = run.player.cur, wall = run.wall.Elapsed.TotalSeconds });
    }
    [HideFromIl2Cpp]
    private static void AfterVoiceObservation()
    {
        var run = current;
        if (run?.player == null || run.gate == null) return;
        try
        {
            var type = run.gate.GetType();
            long sequence = (long)type.GetProperty("RequestSequence")!.GetValue(run.gate)!;
            int phase = Convert.ToInt32(type.GetProperty("Phase")!.GetValue(run.gate));
            if (phase == 3 && run.starts.Add(sequence)) run.events.Add(new { kind = "voice-start", sequence, row = run.player.cur, wall = run.wall.Elapsed.TotalSeconds });
            if (phase == 7 && run.ends.Add(sequence)) run.events.Add(new { kind = "voice-end", sequence, row = run.player.cur, wall = run.wall.Elapsed.TotalSeconds });
        }
        catch (Exception error) { run.callbackFailure = error; }
    }
    [HideFromIl2Cpp]
    private static void AfterPreset(string __1, object __result)
    {
        var run = current;
        if (run?.player == null) return;
        try
        {
            bool success = __result.GetType().GetProperty("Success")?.GetValue(__result) is true
                && __result.GetType().GetProperty("Value")?.GetValue(__result) is true;
            if (!success) throw new InvalidOperationException("Original preset call rejected: " + __1);
            string kind = __1.Split(';')[2]; run.presets.Add(kind);
            run.events.Add(new { kind = "preset", directive = __1, row = run.player.cur, wall = run.wall.Elapsed.TotalSeconds });
        }
        catch (Exception error) { run.callbackFailure = error; }
    }
    [HideFromIl2Cpp]
    private static bool ResourcesReady()
    {
        var r = ScenarioResourceManager.Instance; var c = CharacterManager.Instance;
        return r != null && r.AreDbsLoaded && r.ExcelDb != null && r.ScenarioCharacterNameQuery != null && !r.Preloading
            && c != null && !c.Preloading && c.characterHashDict?.ByteBuffer != null && c.characterHashDict.DataListLength > 0;
    }
    [HideFromIl2Cpp]
    private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    [HideFromIl2Cpp]
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    [HideFromIl2Cpp]
    private void Finish(Exception? error)
    {
        if (done) return; done = true;
        try { hooks?.UnpatchSelf(); }
        catch (Exception cleanup) { error = error == null ? cleanup : new AggregateException(error, cleanup); }
        current = null;
        File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "controlled-story-playback.json"), JsonSerializer.Serialize(new
        {
            Passed = error == null, Error = error?.ToString(), Story = story, StorySha256 = hash,
            VoiceRequests = requests, VoiceStarts = starts.Count, VoiceEnds = ends.Count,
            Presets = presets, NativePlayingVideos = videos, Rows = rows.OrderBy(x => x).ToArray(), Events = events,
            NativeAutoHandlerCalls = autoHandlerCalls,
            TimeoutSeconds = timeoutSeconds,
            Scope = "Ordinary native playback validation of authored inputs; no ExportHost admission override, no movie capture or encoding."
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (error != null) Plugin.Report("PATH-CHECK FAIL controlled-story-playback: " + error);
        Plugin.Report(error == null ? "PATH-CHECK RESULT 1 passed; 0 failed" : "PATH-CHECK RESULT 0 passed; 1 failed");
    }
}
