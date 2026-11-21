using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Integration;
using AAVideoExport.Plugin;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Native boundary probe, not an ExportHost/movie admission test. The installed
// Runtime owns every Gate transition, native preload, callback and voice source.
// This fixture only controls the committed capture counter and reads evidence.
public sealed class CharacterVoiceChecks : MonoBehaviour
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Rate = 48000, Channels = 2;
    private const double Delay = .1;
    private static Harmony? sceneHooks;
    private static CharacterVoiceChecks? active;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly List<object> events = new();
    private readonly List<object> attempts = new();
    private readonly List<float> pcm = new();
    private readonly List<(AudioSource Source, bool Mute)> muted = new();
    private Type? runtime, adapter;
    private object? gate, azurite, azuriteHost, fpsController;
    private AudioManager? audio;
    private AudioSource? voice;
    private Test? storyPlayer;
    private IDisposable? ownership;
    private Il2CppSystem.Action? nativeStarted;
    private string identifier = "", resource = "", resourceHash = "", failure = "";
    private string story = "", storyHash = "";
    private string? callbackFailure;
    private int fps = 30, stage, passed, attempt, callbackCount, nativeStarts, nativeEnds, previousPhase;
    private int beforeFps, beforeVsync, beforeTier, oldFps, oldVsync, oldInterval, postUpdates, requestPcmStart;
    private float oldDelta, oldScale;
    private long committed, requestSequence, delayStart, dueFrame, reads;
    private double phaseStarted, pauseStarted, resumeWallDeadline, cancelRemainder;
    private double resourceReadyDeadline = -1, nextResourceReadyLog, storyLoadDeadline;
    private double selectionDeadline;
    private double renderRestoreStarted, nextRenderRestoreLog;
    private int selectionSettleFrames;
    private int rowAtCancel, blockedNativeAdvances;
    private long zeroSampleRenderTicks;
    private bool renderer, adapterActive, snapshots, oldBackground, finished, rawWasPlaying;
    private bool subscribed, beforeAcquireSeen, afterReleaseSeen, releaseSawRestoredClock, storyStarted, resourcesReadyLogged;
    private bool selectionRequested, sourceGroupExpanded, nativeLoadIssued;
    private bool autoSnapshot, originalAuto, restoreMonitoring, restoredGateCompleted, restoredNativeProgressed;
    private bool expectSilent;

    public CharacterVoiceChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (finished) return;
        try
        {
            if (wall.Elapsed.TotalSeconds > 110) throw new TimeoutException("CharacterVoice native probe exceeded 110 seconds.");
            if (Volatile.Read(ref callbackFailure) is { } callbackError) throw new InvalidOperationException(callbackError);
            if (stage == 0) { WaitForCatalog(); return; }
            if (stage == 10) { SelectNativeStory(); return; }
            if (stage == 1)
            {
                if (story.Length != 0 && !storyStarted)
                {
                    ReportResourceReadiness("waiting-for-native-player");
                    if (wall.Elapsed.TotalSeconds >= storyLoadDeadline)
                        throw new TimeoutException("The single native CoLoadSave attempt did not produce a started player within 45 seconds; it was not retried.");
                    return;
                }
                if (ScenarioResourceManager.Instance.Preloading || !TryReadyAzurite()) { postUpdates = 0; return; }
                if (++postUpdates < 5) return;
                PrepareVoiceResources(); BeginCapture(); StartVoice(); stage = 2; return;
            }
            if (stage == 6) { VerifyRestored(); return; }
            RequireLiveRequest();
            Observe();
            if (stage == 2)
            {
                if (Phase() == 7) { EnterDelay(); return; }
                if (wall.Elapsed.TotalSeconds - phaseStarted > 20 || committed > 1200)
                    throw new TimeoutException("Native voice did not reach audible completion: " + GateText("LastReason"));
                if (!CommitNativeFrame()) return;
                Observe();
                if (Phase() == 7) EnterDelay();
                return;
            }
            if (stage == 3)
            {
                // No rendered/committed frame while the main thread continues
                // normal Updates and the real provider continues polling.
                Invoke(runtime!, "TickOnMainThread");
                CheckDelayHeld();
                if (wall.Elapsed.TotalSeconds - pauseStarted < Delay + .2) return;
                Check(true, "wall time beyond authored delay cannot advance a stopped capture");
                stage = 4;
            }
            if (stage == 4)
            {
                if (!CommitNativeFrame()) return;
                Observe();
                if (attempt == 2)
                {
                    Check(Phase() == 7 && committed < dueFrame, "cached voice retains remaining delay before capture cancellation");
                    CancelCapture(); stage = 6; return;
                }
                if (committed < dueFrame) { CheckDelayHeld(); return; }
                Check(committed == dueFrame && Phase() == 4,
                    "authored 0.1 seconds completes on the first eligible committed frame at " + fps + " FPS");
                Check(GateText("LastReason") == "post-playback-delay-finished", "completion comes from the original Gate delay branch");
                RecordAttempt("frame-cutoff");
                StartVoice(); stage = 2;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    [HideFromIl2Cpp]
    private void WaitForCatalog()
    {
        if (!RenderControlV1.IsReady || RenderControlV1.IsRenderingOwned || SceneManager.GetActiveScene().name != "CatalogScene") return;
        if (UserSettings.Instance == null || !UserSettings.Instance.isReady) return;
        if (!WaitForNativeResources()) return;
        if (!TryReadyAzurite()) return;
        expectSilent = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_VOICE_EXPECT_SILENT") == "1";
        identifier = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_VOICE_IDENTIFIER") ?? "aavt/voice/aave-native-voice";
        if (Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_VOICE_FPS") is { Length: > 0 } value
            && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out fps) || fps is not (24 or 30 or 60)))
            throw new InvalidOperationException("AA_VIDEOEXPORT_TEST_VOICE_FPS must be 24, 30 or 60.");
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Rukari.CharacterVoice").ToArray();
        Check(loaded.Length == 1, "one actual CharacterVoice assembly is loaded");
        runtime = loaded[0].GetType("Rukari.CharacterVoice.Runtime.VoicePlaybackRuntime", true)!;
        gate = runtime.GetField("Gate", Static)!.GetValue(null)!;
        adapter = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.CharacterVoiceDelayClock", true)!;
        Check(Read<bool>(runtime, null, "_installed") && !Read<bool>(runtime, null, "_pollingSuspended")
            && Read<int>(runtime, null, "_mainThreadId") == Environment.CurrentManagedThreadId,
            "actual CharacterVoice runtime is installed and polling on this Unity main thread");
        Check(Read<bool>(adapter, null, "IsReady"), "production adapter is already bound by Plugin lifecycle: " + Read<string>(adapter, null, "Status"));
        Check(Math.Abs(Convert.ToDouble(Read<object>(runtime, null, "PostPlaybackDelaySeconds")) - Delay) < .000001,
            "isolated profile has authored PostPlaybackDelaySeconds = 0.1; fixture does not change Mod settings");
        Check(!Read<bool>(gate.GetType(), gate, "Active"), "no preexisting owned voice request is interrupted");
        beforeFps = Application.targetFrameRate; beforeVsync = QualitySettings.vSyncCount; beforeTier = UserSettings.Instance.fpsTier;
        events.Add(new { kind = "loaded", assembly = loaded[0].Location, version = loaded[0].GetName().Version?.ToString(),
            mvid = loaded[0].ManifestModule.ModuleVersionId, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(loaded[0].Location))),
            identifier, resource, resourceHash, fps, thread = Environment.CurrentManagedThreadId,
            azuriteHostSupported = ReadBool(azuriteHost!, "HostSupported"), azuriteBound = ReadBool(azuriteHost!, "HasPublicRenderControl") });
        RecordPlayer("catalog-before-playback");
        string? requestedStory = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_STORY");
        if (!string.IsNullOrWhiteSpace(requestedStory)) { BeginNativeStorySelection(requestedStory); return; }
        stage = 1; postUpdates = 0;
    }

    // Keep this readiness predicate identical to StoryExportChecks. A loaded
    // Excel database alone does not make CharacterNameLookup safe to enter.
    [HideFromIl2Cpp]
    private static bool NativeResourcesReady()
    {
        try
        {
            var resources = ScenarioResourceManager.Instance;
            var characters = CharacterManager.Instance;
            return resources != null && resources.AreDbsLoaded && resources.ExcelDb != null
                && resources.ScenarioCharacterNameQuery != null && !resources.Preloading
                && characters != null && !characters.Preloading && characters.characterHashDict != null
                && characters.characterHashDict.ByteBuffer != null && characters.characterHashDict.DataListLength > 0;
        }
        catch { return false; }
    }

    [HideFromIl2Cpp]
    private bool WaitForNativeResources()
    {
        if (NativeResourcesReady())
        {
            if (!resourcesReadyLogged) { ReportResourceReadiness("ready-before-CoLoadSave", force: true); resourcesReadyLogged = true; }
            return true;
        }
        if (resourceReadyDeadline < 0) resourceReadyDeadline = wall.Elapsed.TotalSeconds + 45;
        ReportResourceReadiness("waiting-before-CoLoadSave");
        if (wall.Elapsed.TotalSeconds >= resourceReadyDeadline)
            throw new TimeoutException("Native resource and character-name tables did not become ready within 45 seconds; CoLoadSave was not started.");
        return false;
    }

    [HideFromIl2Cpp]
    private void ReportResourceReadiness(string phase, bool force = false)
    {
        if (!force && wall.Elapsed.TotalSeconds < nextResourceReadyLog) return;
        nextResourceReadyLog = wall.Elapsed.TotalSeconds + 5;
        var evidence = new
        {
            kind = "resource-readiness", phase, wallSeconds = wall.Elapsed.TotalSeconds,
            resourceManager = DiagnosticRead(() => ScenarioResourceManager.Instance != null),
            databasesLoaded = DiagnosticRead(() => ScenarioResourceManager.Instance?.AreDbsLoaded),
            excelDbPresent = DiagnosticRead(() => ScenarioResourceManager.Instance?.ExcelDb != null),
            characterNameQueryPresent = DiagnosticRead(() => ScenarioResourceManager.Instance?.ScenarioCharacterNameQuery != null),
            resourcesPreloading = DiagnosticRead(() => ScenarioResourceManager.Instance?.Preloading),
            characterManager = DiagnosticRead(() => CharacterManager.Instance != null),
            charactersPreloading = DiagnosticRead(() => CharacterManager.Instance?.Preloading),
            characterDictionaryPresent = DiagnosticRead(() => CharacterManager.Instance?.characterHashDict != null),
            characterDictionaryBufferPresent = DiagnosticRead(() => CharacterManager.Instance?.characterHashDict?.ByteBuffer != null),
            characterRows = DiagnosticRead(() => CharacterManager.Instance?.characterHashDict?.DataListLength)
        };
        events.Add(evidence);
        Plugin.Report("PATH-CHECK CHARACTER-VOICE READINESS " + JsonSerializer.Serialize(evidence));
    }

    [HideFromIl2Cpp]
    private static object? DiagnosticRead(Func<object?> read)
    {
        try { return read(); }
        catch (Exception error) { return "unavailable:" + error.GetType().Name; }
    }

    [HideFromIl2Cpp]
    private void BeginNativeStorySelection(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || Path.GetExtension(path) != ".aas")
            throw new InvalidOperationException("Voice owner setup requires an explicit existing absolute .aas file.");
        story = Path.GetFullPath(path);
        string resolved = ScenarioResourceManager.GetPersistentFilePath("saves", Path.GetFileNameWithoutExtension(story), ".aas");
        Check(string.Equals(story, Path.GetFullPath(resolved), StringComparison.OrdinalIgnoreCase), "explicit voice-owner story matches native workspace resolution");
        storyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(story)));
        selectionDeadline = wall.Elapsed.TotalSeconds + 15;
        RecordStoryContext("before-native-row-selection", null);
        stage = 10;
    }

    [HideFromIl2Cpp]
    private void SelectNativeStory()
    {
        if (wall.Elapsed.TotalSeconds >= selectionDeadline)
        {
            RecordStoryContext("native-row-selection-timeout", null);
            throw new TimeoutException("The exact native story selection did not establish its active card within 15 seconds; CoLoadSave was not started.");
        }
        if (SceneManager.GetActiveScene().name != "CatalogScene")
            throw new InvalidOperationException("Native scene changed before the selected story load was issued.");
        if (!selectionRequested)
        {
            var row = NativeRows().FirstOrDefault(item => item.infoBlock != null && SamePath(item.infoBlock.path, story))
                ?? throw new InvalidOperationException("The requested voice-owner story has no matching native catalog row; arbitrary coroutine owners are not allowed.");
            if (!row.gameObject.activeInHierarchy)
            {
                var group = ParentGroup(row);
                if (sourceGroupExpanded) return;
                if (group == null || !group.gameObject.activeInHierarchy || group.expanded)
                    throw new InvalidOperationException("The exact story row is hidden outside an active collapsed native group.");
                sourceGroupExpanded = true; group.Expand();
                Plugin.Report("PATH-CHECK CHARACTER-VOICE SELECT expanded-native-source-group source=" + story);
                return;
            }
            selectionRequested = true;
            bool accepted = row.Select(false);
            events.Add(new { kind = "native-row-selected", story, rowPath = row.infoBlock.path, accepted,
                rowActive = row.gameObject.activeInHierarchy, mode = Catalog.mode.ToString() });
            Plugin.Report("PATH-CHECK CHARACTER-VOICE SELECT source=" + story + "; native_select_accepted=" + accepted);
            if (!accepted) throw new InvalidOperationException("Native catalog rejected the exact voice-owner story row.");
            return;
        }
        if (++selectionSettleFrames < 2 || !NativeResourcesReady()) return;
        var card = Object.FindObjectsOfType<CatalogFileInfo>().FirstOrDefault(item => item != null
            && item.gameObject.activeInHierarchy && item.block != null && SamePath(item.block.path, story)
            && item.controlPanel != null && item.controlPanel.activeInHierarchy);
        if (card == null) return;
        RecordStoryContext("selected-before-native-load", card);
        var resources = ScenarioResourceManager.Instance;
        Check(resources.GlobalOverrideData?.CharacterOverrides != null && resources.LocalOverrideData?.CharacterOverrides != null,
            "native row selection established both global and story-local character override datasets");
        LoadSelectedNativeStory(card);
        stage = 1; postUpdates = 0;
    }

    [HideFromIl2Cpp]
    private static CatalogFileItem[] NativeRows()
    {
        var rows = new List<CatalogFileItem>();
        foreach (var item in Resources.FindObjectsOfTypeAll<CatalogFileItem>())
            if (item != null && item.gameObject.scene.name == "CatalogScene") rows.Add(item);
        var items = Catalog.Instance?.items;
        if (items != null)
            for (int index = 0; index < items.Count; index++)
                if (items[index] != null && !rows.Any(row => row.Pointer == items[index].Pointer)) rows.Add(items[index]);
        return rows.OrderByDescending(row => row.gameObject.activeInHierarchy)
            .ThenByDescending(row => ParentGroup(row)?.gameObject.activeInHierarchy == true).ToArray();
    }

    [HideFromIl2Cpp]
    private static EpisodeGroup? ParentGroup(CatalogFileItem item)
    {
        for (var parent = item.transform.parent; parent != null; parent = parent.parent)
        {
            var group = parent.GetComponent<EpisodeGroup>();
            if (group != null) return group;
        }
        return null;
    }

    [HideFromIl2Cpp]
    private static bool SamePath(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    [HideFromIl2Cpp]
    private void RecordStoryContext(string point, CatalogFileInfo? card)
    {
        var resources = ScenarioResourceManager.Instance;
        var evidence = new
        {
            kind = "story-selection-context", point, story,
            cardPath = card?.block?.path, cardActive = card != null && card.gameObject.activeInHierarchy,
            persistentProjectPath = PersistentData.projectPath,
            globalLoaded = resources.GlobalOverrideData != null, localLoaded = resources.LocalOverrideData != null,
            globalBasePath = resources.GlobalOverrideData?.basePath, localBasePath = resources.LocalOverrideData?.basePath,
            globalCharacterCount = resources.GlobalOverrideData?.CharacterOverrides?.Count,
            localCharacterCount = resources.LocalOverrideData?.CharacterOverrides?.Count,
            globalSoundCount = resources.GlobalOverrideData?.SoundOverrides?.Count, localSoundCount = resources.LocalOverrideData?.SoundOverrides?.Count,
            activeCards = Object.FindObjectsOfType<CatalogFileInfo>().Where(item => item != null && item.gameObject.activeInHierarchy)
                .Select(item => new { path = item.block?.path, activeControlPanel = item.controlPanel != null && item.controlPanel.activeInHierarchy }).ToArray()
        };
        events.Add(evidence); Plugin.Report("PATH-CHECK CHARACTER-VOICE STORY-CONTEXT " + JsonSerializer.Serialize(evidence));
    }

    [HideFromIl2Cpp]
    private void LoadSelectedNativeStory(CatalogFileInfo card)
    {
        Check(!nativeLoadIssued && NativeResourcesReady(), "complete native context is ready before the single CoLoadSave attempt");
        Check(card.block != null && SamePath(card.block.path, story) && card.gameObject.activeInHierarchy,
            "CoLoadSave uses the exact selected active native story card");
        var table = ScenarioResourceManager.Instance.LoadGenericScenario(Path.GetFileNameWithoutExtension(story));
        Check(table != null && table.ByteBuffer != null && table.DataListLength > 0, "native scenario loader supplies the voice-owner story");
        active = this;
        sceneHooks = new Harmony("halocue.aa.videoexport.character-voice-scene-fixture");
        sceneHooks.Patch(AccessTools.Method(typeof(Test), nameof(Test.Start)), postfix: new HarmonyMethod(typeof(CharacterVoiceChecks), nameof(AfterNativeStoryStart)));
        sceneHooks.Patch(AccessTools.Method(typeof(Test), nameof(Test.AdvanceScenario)),
            prefix: new HarmonyMethod(typeof(CharacterVoiceChecks), nameof(BeforeNativeAdvance)),
            postfix: new HarmonyMethod(typeof(CharacterVoiceChecks), nameof(AfterNativeAdvance)));
        // This is the same native loader sequence used by ExportHost. Do not
        // substitute a Test object, assign its Singleton, or suppress Start.
        PersistentData.saveData = table!;
        PersistentData.projectPath = story;
        events.Add(new { kind = "native-story-load", story, storyHash, rows = table!.DataListLength, loaderCard = card.block?.path });
        storyLoadDeadline = wall.Elapsed.TotalSeconds + 45;
        nativeLoadIssued = true;
        card.StartCoroutine(card.CoLoadSave());
    }

    [HideFromIl2Cpp]
    private static void AfterNativeStoryStart(Test __instance, bool __runOriginal)
    {
        var fixture = active;
        if (fixture == null || __instance.previewMode) return;
        try
        {
            if (!__runOriginal) throw new InvalidOperationException("Native Test.Start was suppressed by another hook.");
            fixture.storyPlayer = __instance;
            // Pause through the native UI/control methods after all normal
            // player initialization has executed. Voice policy stays intact.
            fixture.originalAuto = __instance.IsAutoEnabled; fixture.autoSnapshot = true;
            if (__instance.autoToggle != null) __instance.autoToggle.Deactivate();
            if (__instance.IsAutoEnabled) __instance.ToggleAuto();
            fixture.storyStarted = true;
            fixture.RecordPlayer("native-start-completed");
        }
        catch (Exception error) { Interlocked.CompareExchange(ref fixture.callbackFailure, error.Message, null); }
    }

    [HideFromIl2Cpp]
    private static void BeforeNativeAdvance(out double __state) => __state = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    [HideFromIl2Cpp]
    private static void AfterNativeAdvance(Test __instance, bool __runOriginal, double __state)
    {
        var fixture = active;
        if (fixture?.storyPlayer == null || __instance.Pointer != fixture.storyPlayer.Pointer || fixture.finished) return;
        try
        {
            if (!__runOriginal) { fixture.blockedNativeAdvances++; return; }
            if (!fixture.restoreMonitoring || __instance.cur == fixture.rowAtCancel) return;
            bool due = __state >= fixture.resumeWallDeadline;
            fixture.events.Add(new { kind = "native-advance-after-cancel", invocationWall = __state,
                deadline = fixture.resumeWallDeadline, previousRow = fixture.rowAtCancel, row = __instance.cur,
                allowedAfterDeadline = due, gateSequence = fixture.GateLong("RequestSequence"), gatePhase = fixture.Phase(),
                gateReason = fixture.GateText("LastReason") });
            if (!due)
                Interlocked.CompareExchange(ref fixture.callbackFailure, "Native story advanced before the restored voice delay deadline.", null);
            else fixture.restoredNativeProgressed = true;
        }
        catch (Exception error) { Interlocked.CompareExchange(ref fixture.callbackFailure, error.Message, null); }
    }

    [HideFromIl2Cpp]
    private void RecordPlayer(string point)
    {
        var player = Test.Instance;
        var evidence = new
        {
            kind = "native-player", point, activeScene = SceneManager.GetActiveScene().name,
            singletonPresent = player != null, singletonPreview = player == null ? (bool?)null : player.previewMode,
            singletonPointer = player?.Pointer.ToString(), singletonScene = player == null ? "" : player.gameObject.scene.name,
            singletonActive = player != null && player.gameObject.activeInHierarchy, singletonEnabled = player != null && player.enabled,
            singletonAuto = player != null && player.IsAutoEnabled,
            players = Resources.FindObjectsOfTypeAll<Test>().Where(item => item != null).Select(item => new { pointer = item.Pointer.ToString(),
                scene = item.gameObject.scene.name, active = item.gameObject.activeInHierarchy, item.enabled, item.previewMode }).ToArray()
        };
        events.Add(evidence); Plugin.Report("PATH-CHECK CHARACTER-VOICE PLAYER " + JsonSerializer.Serialize(evidence));
    }

    [HideFromIl2Cpp]
    private void PrepareVoiceResources()
    {
        var resources = ScenarioResourceManager.Instance;
        // Catalog readiness does not imply the player has initialized either
        // override dataset. The original native resolver dereferences both
        // before CharacterVoice's postfix can bridge an owned identifier.
        string isolatedRoot = Path.Combine(Plugin.DiagnosticsRoot, "native-voice-resource-context");
        var before = new { globalLoaded = resources.GlobalOverrideData != null, localLoaded = resources.LocalOverrideData != null };
        if (resources.GlobalOverrideData == null) resources.LoadGlobalResourceOverrides();
        if (resources.LocalOverrideData == null)
        {
            Directory.CreateDirectory(isolatedRoot);
            resources.LoadLocalResourceOverrides(isolatedRoot);
        }
        var diagnostic = new
        {
            kind = "resource-context", before, isolatedRoot,
            persistentDataPath = Application.persistentDataPath,
            nativeOverridesPath = ScenarioResourceManager.GetPersistentFilePath("overrides", "", ""),
            globalLoaded = resources.GlobalOverrideData != null, localLoaded = resources.LocalOverrideData != null,
            globalBasePath = resources.GlobalOverrideData?.basePath, localBasePath = resources.LocalOverrideData?.basePath,
            globalSoundCount = resources.GlobalOverrideData?.SoundOverrides?.Count,
            localSoundCount = resources.LocalOverrideData?.SoundOverrides?.Count,
            globalVoiceCount = resources.GlobalOverrideData?.VoiceOverrides?.Count,
            localVoiceCount = resources.LocalOverrideData?.VoiceOverrides?.Count
        };
        events.Add(diagnostic);
        Plugin.Report("PATH-CHECK CHARACTER-VOICE RESOURCE " + JsonSerializer.Serialize(diagnostic));
        Check(resources.GlobalOverrideData?.VoiceOverrides != null && resources.LocalOverrideData?.VoiceOverrides != null
            && resources.GlobalOverrideData.SoundOverrides != null && resources.LocalOverrideData.SoundOverrides != null,
            "normal native resource loaders initialize both override datasets before resolving voice");
        var decode = runtime!.Assembly.GetType("Rukari.CharacterVoice.Core.VoiceDirectivePolicy", true)!
            .GetMethod("TryDecodeNativeIdentifier", Static)!;
        object?[] args = { identifier, null };
        Check((bool)decode.Invoke(null, args)!, "installed CharacterVoice policy accepts the supplied native identifier");
        string key = (string)args[1]!;
        bool soundFound = resources.TryGetSoundOverridePath(key, out string soundPath);
        bool voiceFound = resources.TryGetVoiceOverridePath(identifier, out resource);
        if (voiceFound && File.Exists(resource)) resourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(resource)));
        var resolution = new { kind = "resource-resolution", identifier, key, soundFound, soundPath,
            soundFileExists = File.Exists(soundPath), voiceFound, voicePath = resource, voiceFileExists = File.Exists(resource),
            resourceHash, matchingGlobalEntries = MatchingSounds(resources.GlobalOverrideData, key),
            matchingLocalEntries = MatchingSounds(resources.LocalOverrideData, key) };
        events.Add(resolution);
        Plugin.Report("PATH-CHECK CHARACTER-VOICE RESOLVE " + JsonSerializer.Serialize(resolution));
        Check(voiceFound && File.Exists(resource), "native CharacterVoice resolver finds the explicitly selected existing voice");
    }

    [HideFromIl2Cpp]
    private static string[] MatchingSounds(ResourceOverrideData? data, string key)
    {
        var matches = new List<string>();
        if (data == null || data.SoundOverrides == null) return matches.ToArray();
        foreach (string path in data.SoundOverrides)
            if (path.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(path);
                if (matches.Count == 12) break;
            }
        return matches.ToArray();
    }

    [HideFromIl2Cpp]
    private void BeginCapture()
    {
        RecordPlayer("before-capture");
        Check(Test.Instance != null, "real native Test singleton exists; supply -StoryPath for full CoLoadSave owner setup");
        Check(!Test.Instance!.previewMode, "real native Test owner is non-preview; preview does not qualify for authored delay");
        if (story.Length != 0)
            Check(storyStarted && storyPlayer != null && Test.Instance.Pointer == storyPlayer.Pointer && !Test.Instance.IsAutoEnabled,
                "normal native Test.Start established this story owner with AUTO paused");
        beforeFps = Application.targetFrameRate; beforeVsync = QualitySettings.vSyncCount; beforeTier = UserSettings.Instance.fpsTier;
        audio = AudioManager.Instance;
        Check(audio != null && audio.voiceSource != null, "real native AudioManager voice source is available");
        voice = audio!.voiceSource;
        RecordAudioRoute("before-capture");
        var userVolumes = UserSettings.Instance.data ?? throw new InvalidOperationException("Native user volume settings are unavailable.");
        var nativeVolumes = audio.volumeInfo ?? throw new InvalidOperationException("Native AudioManager volume settings are unavailable.");
        Check(userVolumes.masterVolume > 0 && nativeVolumes.masterVolume > 0,
            "native probe has nonzero user and native master levels without overriding settings");
        Check(expectSilent
                ? userVolumes.voiceVolume == 0 && nativeVolumes.voiceVolume == 0
                : userVolumes.voiceVolume > 0 && nativeVolumes.voiceVolume > 0,
            expectSilent ? "explicit silent case uses the actual zero user/native voice level"
                : "audible case requires nonzero user/native voice levels; user mute settings are not overridden");
        Check(!NativePlaying(), "native voice source is idle before the request");
        Check(AudioSettings.outputSampleRate == Rate && AudioSettings.speakerMode == AudioSpeakerMode.Stereo,
            "existing native mixer is 48000 Hz stereo");
        RenderControlV1.BeforeAcquire += BeforeAcquire;
        RenderControlV1.AfterRelease += AfterRelease; subscribed = true;
        ownership = (IDisposable)Invoke(typeof(RenderControlV1), "Acquire")!;
        Check(beforeAcquireSeen && RenderControlV1.ProtocolVersion == 1
            && ReadBool(azurite!, "_exportSuspended") && ReadBool(fpsController!, "_exportSuspended")
            && !ReadBool(fpsController!, "IsMappingApplied") && ReadBool(azuriteHost!, "PublicRenderingBlocked"),
            "bound Azurite restores and suspends before capture settings are acquired");
        oldDelta = Time.captureDeltaTime; oldScale = Time.timeScale; oldFps = Application.targetFrameRate;
        oldVsync = QualitySettings.vSyncCount; oldInterval = OnDemandRendering.renderFrameInterval; oldBackground = Application.runInBackground; snapshots = true;
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; OnDemandRendering.renderFrameInterval = 1;
        Application.runInBackground = true; Time.timeScale = 1; Time.captureFramerate = fps;
        foreach (var source in Object.FindObjectsOfType<AudioSource>(true))
        {
            if (source == null || source.Pointer == voice!.Pointer) continue;
            muted.Add((source, source.mute)); source.mute = true;
        }
        Check(!voice!.mute && (expectSilent || voice.volume > 0) && !AudioListener.pause && AudioListener.volume > 0,
            "voice source and global listener controls permit nonzero output without fixture overrides");
        RecordAudioRoute("capture-configured-after-other-sources-muted");
        adapter!.GetMethod("BeginExport", Static, null, new[] { typeof(Func<long>), typeof(int) }, null)!
            .Invoke(null, new object[] { (Func<long>)(() => committed), fps });
        adapterActive = true;
        Check(Read<string>(adapter, null, "Status") == "capturing", "production adapter owns this committed-frame clock");
        if (!AudioRenderer.Start()) throw new InvalidOperationException("Native AudioRenderer did not start.");
        renderer = true;
    }

    [HideFromIl2Cpp]
    private void StartVoice()
    {
        attempt++;
        bool cached = ScenarioResourceManager.Instance._loadedCustomVoices.ContainsKey(identifier);
        Check(cached == (attempt == 2), attempt == 1 ? "first request is cold without clearing native caches" : "second request reuses the actual native voice cache");
        callbackCount = nativeStarts = nativeEnds = 0; rawWasPlaying = false;
        requestPcmStart = pcm.Count; previousPhase = 0; phaseStarted = wall.Elapsed.TotalSeconds;
        var method = typeof(AudioManager).GetMethod("SetVoice", Instance, null, new[] { typeof(string), typeof(Il2CppSystem.Action) }, null)
            ?? throw new NotSupportedException("This probe requires the native onStarted callback signature.");
        int callbackAttempt = attempt, callbackThread = Environment.CurrentManagedThreadId;
        var started = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() =>
        {
            // A late callback belongs to its original request. Never attribute
            // it to a newer request or write the evidence List off main thread.
            if (Environment.CurrentManagedThreadId != callbackThread || attempt != callbackAttempt || finished)
            {
                Interlocked.CompareExchange(ref callbackFailure, "Native start callback arrived outside its live main-thread request.", null);
                return;
            }
            callbackCount++;
            events.Add(new { kind = "native-start-callback", attempt = callbackAttempt, callbackCount, frame = committed, wall = wall.Elapsed.TotalSeconds,
                thread = Environment.CurrentManagedThreadId });
            RecordAudioRoute("native-start-callback");
        })) ?? throw new InvalidOperationException("Native start callback conversion failed.");
        nativeStarted = started; // Retain the real native delegate through this request.
        method.Invoke(audio, new object[] { identifier, started });
        requestSequence = GateLong("RequestSequence");
        Check(GateText("VoiceIdentifier") == identifier && Phase() is 1 or 2 or 3,
            "native SetVoice entered the installed Runtime Gate");
        // The authored wait is AUTO-only. Turn on the real native control only
        // after the request exists, so real AdvanceScenario calls are held by
        // CharacterVoice's own policy rather than by a test-owned suppressor.
        if (!Test.Instance.IsAutoEnabled)
        {
            if (Test.Instance.autoToggle == null) throw new InvalidOperationException("Native AUTO control is absent.");
            Test.Instance.autoToggle.Toggle();
        }
        Check(Test.Instance.IsAutoEnabled, "native AUTO is enabled and the original voice policy owns advancement");
        Observe();
    }

    [HideFromIl2Cpp]
    private void Observe()
    {
        RequireLiveRequest();
        bool playing = NativePlaying();
        if (playing && !rawWasPlaying) { nativeStarts++; RecordAudioRoute("first-native-playing"); }
        if (!playing && rawWasPlaying) nativeEnds++;
        rawWasPlaying = playing;
        int phase = Phase();
        if (phase != previousPhase)
        {
            events.Add(new { kind = "gate", attempt, sequence = requestSequence, phase, previousPhase, frame = committed,
                pcmFrames = pcm.Count / Channels, nativePlaying = playing, callbackCount, nativeStarts, nativeEnds,
                wall = wall.Elapsed.TotalSeconds, reason = GateText("LastReason"), sourceCursor = voice!.timeSamples });
            previousPhase = phase;
        }
        if (phase is 5 or 6) throw new InvalidOperationException("Original voice Gate failed/canceled: " + GateText("LastReason"));
    }

    [HideFromIl2Cpp]
    private void EnterDelay()
    {
        RecordAudioRoute("audio-finished");
        Check(callbackCount == 1 && nativeStarts == 1 && nativeEnds == 1 && !NativePlaying(),
            "real native callback and playing observations show exactly one voice start and end");
        if (expectSilent)
        {
            var actualVoice = AudioManager.Instance.voiceSource;
            Check(actualVoice != null && actualVoice.clip != null, "silent case still used a real loaded native voice clip");
            object decoded = ClipEvidence(actualVoice!.clip);
            Check(Read<bool>(decoded.GetType(), decoded, "getDataSucceeded") && Read<long>(decoded.GetType(), decoded, "nonzero") > 0
                && Read<float>(decoded.GetType(), decoded, "peak") > .00001f,
                "silent output is proven against non-silent decoded native clip content");
            Check(UserSettings.Instance.data.voiceVolume == 0 && AudioManager.Instance.volumeInfo.voiceVolume == 0
                && actualVoice.volume == 0 && !actualVoice.mute,
                "original native voice setting keeps source volume zero without fixture muting");
            Check(pcm.Count > requestPcmStart && pcm.Skip(requestPcmStart).All(sample => sample == 0f),
                "explicit user-muted case captures exactly silent native PCM");
        }
        else
            Check(pcm.Skip(requestPcmStart).Any(sample => Math.Abs(sample) > .00001f),
                "native AudioRenderer captured non-silent PCM from the owned voice");
        Check(Read<double>(gate!.GetType(), gate, "PostPlaybackDelaySeconds") == Delay,
            "original Gate snapshots the configured authored delay");
        delayStart = committed; dueFrame = checked(delayStart + (fps + 9) / 10);
        double deadline = Read<double>(gate.GetType(), gate, "_deadline");
        Check(Math.Abs(deadline - (committed / (double)fps + Delay)) < 1e-10,
            "audio completion creates a frame-domain delay from committed frames divided by FPS");
        pauseStarted = wall.Elapsed.TotalSeconds; stage = 3;
    }

    [HideFromIl2Cpp]
    private void RecordAudioRoute(string point)
    {
        // Observational only. In particular, do not create a listener, alter
        // source/mixer settings, force decoding, or substitute fixture audio.
        try
        {
            var currentManager = AudioManager.Instance;
            var currentVoice = currentManager?.voiceSource;
            var listeners = Object.FindObjectsOfType<AudioListener>(true).Where(listener => listener != null).ToArray();
            var sources = Object.FindObjectsOfType<AudioSource>(true).Where(source => source != null).ToArray();
            AudioSettings.GetDSPBufferSize(out int dspBlock, out int dspBlocks);
            var evidence = new
            {
                kind = "audio-route", point, attempt, frame = committed, pcmFrames = pcm.Count / Channels,
                rendererStarted = renderer, captureDelta = Time.captureDeltaTime, dspTime = AudioSettings.dspTime,
                dspBlock, dspBlocks, outputRate = AudioSettings.outputSampleRate, speakerMode = AudioSettings.speakerMode.ToString(),
                listenerPause = AudioListener.pause, listenerVolume = AudioListener.volume,
                userVolumes = DiagnosticRead(() => new { master = UserSettings.Instance?.data?.masterVolume,
                    voice = UserSettings.Instance?.data?.voiceVolume, bgm = UserSettings.Instance?.data?.bgmVolume,
                    sound = UserSettings.Instance?.data?.soundEffectVolume }),
                nativeVolumes = DiagnosticRead(() => new { master = currentManager?.volumeInfo?.masterVolume,
                    voice = currentManager?.volumeInfo?.voiceVolume, bgm = currentManager?.volumeInfo?.bgmVolume,
                    sound = currentManager?.volumeInfo?.soundVolume }),
                enabledActiveListeners = listeners.Count(listener => listener.enabled && listener.gameObject.activeInHierarchy),
                originalManagerPointer = audio?.Pointer.ToString(), currentManagerPointer = currentManager?.Pointer.ToString(),
                originalVoicePointer = voice?.Pointer.ToString(), currentVoicePointer = currentVoice?.Pointer.ToString(),
                listeners = listeners.Select(listener => new { pointer = listener.Pointer.ToString(), listener.name, listener.enabled,
                    active = listener.gameObject.activeInHierarchy, scene = listener.gameObject.scene.name,
                    position = Position(listener.transform.position) }).ToArray(),
                sources = sources.Select(source => DiagnosticRead(() => AudioSourceEvidence(source))).ToArray(),
                decodedCurrentVoice = DiagnosticRead(() => ClipEvidence(currentVoice?.clip)),
                fixtureMuted = muted.Select(saved => new { pointer = saved.Source == null ? null : saved.Source.Pointer.ToString(),
                    originalMute = saved.Mute, currentMute = saved.Source == null ? (bool?)null : saved.Source.mute }).ToArray()
            };
            events.Add(evidence);
            Plugin.Report("PATH-CHECK CHARACTER-VOICE AUDIO " + JsonSerializer.Serialize(evidence));
        }
        catch (Exception error)
        {
            var evidence = new { kind = "audio-route-diagnostic-error", point, error = error.ToString() };
            events.Add(evidence);
            Plugin.Report("PATH-CHECK CHARACTER-VOICE AUDIO " + JsonSerializer.Serialize(evidence));
        }
    }

    [HideFromIl2Cpp]
    private object AudioSourceEvidence(AudioSource source)
    {
        var clip = source.clip;
        var mixer = source.outputAudioMixerGroup;
        return new
        {
            pointer = source.Pointer.ToString(), source.name, scene = source.gameObject.scene.name,
            active = source.gameObject.activeInHierarchy, source.enabled, source.isPlaying, source.isVirtual,
            source.mute, source.volume, source.pitch, source.loop, source.spatialBlend, source.minDistance, source.maxDistance,
            rolloff = source.rolloffMode.ToString(), position = Position(source.transform.position),
            source.ignoreListenerPause, source.ignoreListenerVolume, source.bypassEffects, source.bypassListenerEffects,
            source.bypassReverbZones, source.priority,
            clipPointer = clip?.Pointer.ToString(), clipName = clip?.name,
            clipLoadState = clip == null ? null : clip.loadState.ToString(),
            clipSamples = clip == null ? (int?)null : clip.samples, cursor = clip == null ? (int?)null : source.timeSamples,
            mixerPointer = mixer?.Pointer.ToString(), mixerName = mixer?.name, audioMixerName = mixer?.audioMixer?.name,
            isOriginalVoice = voice != null && source.Pointer == voice.Pointer,
            isCurrentVoice = AudioManager.Instance?.voiceSource != null && source.Pointer == AudioManager.Instance.voiceSource.Pointer,
            inFixtureMuteList = muted.Any(saved => saved.Source != null && saved.Source.Pointer == source.Pointer)
        };
    }

    [HideFromIl2Cpp]
    private static object Position(Vector3 point) => new { point.x, point.y, point.z };

    [HideFromIl2Cpp]
    private static object ClipEvidence(AudioClip? clip)
    {
        if (clip == null) return new { present = false };
        int frames = Math.Min(clip.samples, checked(Rate * 5));
        int count = checked(frames * clip.channels);
        var values = new Il2CppStructArray<float>(new float[count]);
        bool read = count > 0 && clip.GetData(values, 0);
        float peak = 0; long nonzero = 0, nonfinite = 0;
        if (read)
            for (int index = 0; index < count; index++)
            {
                float sample = values[index];
                if (!float.IsFinite(sample)) { nonfinite++; continue; }
                peak = Math.Max(peak, Math.Abs(sample));
                if (sample != 0) nonzero++;
            }
        return new
        {
            present = true, pointer = clip.Pointer.ToString(), clip.name, clip.samples, clip.channels, clip.frequency,
            loadState = clip.loadState.ToString(), loadType = clip.loadType.ToString(), clip.preloadAudioData,
            getDataSucceeded = read, inspectedFrames = frames, inspectedValues = count, peak, nonzero, nonfinite,
            truncated = frames != clip.samples
        };
    }

    [HideFromIl2Cpp]
    private void CheckDelayHeld()
    {
        if (Phase() != 7 || !Read<bool>(gate!.GetType(), gate, "HoldNativeVoiceWait"))
            throw new InvalidOperationException("Authored delay released without its required committed frames.");
        double deadline = Read<double>(gate.GetType(), gate, "_deadline");
        Invoke(runtime!, "TickOnMainThread"); Invoke(runtime!, "TickOnMainThread");
        if (Phase() != 7 || Read<double>(gate.GetType(), gate, "_deadline") != deadline)
            throw new InvalidOperationException("Repeated same-frame native ticks changed the authored wait.");
    }

    private unsafe bool CommitNativeFrame()
    {
        int frames = AudioRenderer.GetSampleCountForCaptureFrame();
        if (frames < 0 || frames > Rate) throw new InvalidOperationException("Native renderer advertised an invalid capture size.");
        // Match NativeCaptureScope/Unity Recorder: render every mixer tick,
        // including zero availability, or Unity's accumulator cannot advance.
        // A successful empty render commits a capture tick, never silent PCM.
        int values = checked(frames * Channels);
        var block = Enumerable.Repeat(float.NaN, checked(values + 64)).ToArray();
        double dspBefore = AudioSettings.dspTime;
        fixed (float* pointer = block)
            if (!AudioRenderer.Internal_AudioRenderer_Render(values == 0 ? null : pointer, values)) throw new InvalidOperationException("Native PCM render failed.");
        double dspAfter = AudioSettings.dspTime;
        if (block.Take(values).Any(sample => !float.IsFinite(sample)) || block.Skip(values).Any(sample => !float.IsNaN(sample)))
            throw new InvalidOperationException("Native PCM render left unwritten/nonfinite samples or changed its buffer guard.");
        if (!double.IsFinite(dspBefore) || !double.IsFinite(dspAfter)
            || (frames == 0 ? dspAfter != dspBefore : dspAfter <= dspBefore || Math.Abs((dspAfter - dspBefore) * Rate - frames) >= .25))
            throw new InvalidOperationException("Native DSP progress does not match the captured PCM interval.");
        if (frames == 0) zeroSampleRenderTicks++;
        events.Add(new { kind = "pcm", attempt, frame = committed, advertisedFrames = frames,
            pcmStartFrame = pcm.Count / Channels, dspBefore, dspAfter, emptyRenderTick = frames == 0 });
        pcm.AddRange(block.Take(values));
        // Publish progress only after the actual native block is retained.
        committed++;
        Invoke(runtime!, "TickOnMainThread");
        return true;
    }

    [HideFromIl2Cpp]
    private void CancelCapture()
    {
        cancelRemainder = Delay - (committed - delayStart) / (double)fps;
        double before = Convert.ToDouble(Invoke(runtime!, "Now"));
        reads = Read<long>(adapter!, null, "CaptureReads");
        Invoke(adapter!, "EndExport"); adapterActive = false;
        double after = Convert.ToDouble(Invoke(runtime!, "Now"));
        resumeWallDeadline = Read<double>(gate!.GetType(), gate, "_deadline");
        Check(GateLong("RequestSequence") == requestSequence && Phase() == 7,
            "ending capture preserves the actual live voice request and delay");
        Check(resumeWallDeadline >= before + cancelRemainder - .000001
            && resumeWallDeadline <= after + cancelRemainder + .000001,
            "cancellation restores the exact authored remainder into wall-clock coordinates");
        Check(reads > 0, "native runtime comparisons actually traversed production adapter prefixes");
        rowAtCancel = Test.Instance.cur; restoreMonitoring = true;
        RecordAttempt("cancel-restore");
        RestoreCapture();
        Check(afterReleaseSeen && releaseSawRestoredClock && !RenderControlV1.IsRenderingOwned && RenderControlV1.IsReady,
            "clock and Unity settings restore before RenderControlV1 AfterRelease");
        postUpdates = 0; renderRestoreStarted = wall.Elapsed.TotalSeconds; nextRenderRestoreLog = 0;
    }

    [HideFromIl2Cpp]
    private void VerifyRestored()
    {
        double now = Convert.ToDouble(Invoke(runtime!, "Now"));
        long sequence = GateLong("RequestSequence");
        if (now < resumeWallDeadline)
        {
            if (sequence != requestSequence || Phase() != 7 || Test.Instance.cur != rowAtCancel)
                throw new InvalidOperationException("Restored authored request or story advanced before its wall deadline.");
            Invoke(runtime!, "TickOnMainThread");
            return;
        }
        if (sequence == requestSequence)
        {
            Invoke(runtime!, "TickOnMainThread");
            if (Phase() == 4 && GateText("LastReason") == "post-playback-delay-finished") restoredGateCompleted = true;
        }
        // Native AUTO may legally enter the next story row/request before this
        // observer's next Update. Accept only an observed original advance at
        // or after the exact restored deadline, never an unexplained sequence.
        if (!restoredGateCompleted && !restoredNativeProgressed)
        {
            if (now > resumeWallDeadline + .5)
                throw new InvalidOperationException("No natural Gate completion or post-deadline native advancement was observed after restoration.");
            return;
        }
        postUpdates++;
        if (!WaitForRenderRestore()) return;
        Check(restoredGateCompleted || restoredNativeProgressed,
            "restored delay completes in wall time or permits observed native AUTO advancement only after its deadline");
        Check(ReadBool(fpsController!, "IsOperational") && ReadBool(fpsController!, "IsMappingApplied")
            && !ReadBool(azurite!, "_exportSuspended") && !ReadBool(fpsController!, "_exportSuspended")
            && ReadBool(azuriteHost!, "HasPublicRenderControl"), "Azurite resumes the actually bound FPS controller");
        Check(Application.targetFrameRate == beforeFps && QualitySettings.vSyncCount == beforeVsync && UserSettings.Instance.fpsTier == beforeTier,
            "settled native FPS VSync and user tier return to baseline");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(resource))) == resourceHash, "input voice resource remains unchanged");
        if (story.Length != 0) Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(story))) == storyHash, "native owner story bytes remain unchanged");
        Finish();
    }

    [HideFromIl2Cpp]
    private bool WaitForRenderRestore()
    {
        double elapsed = wall.Elapsed.TotalSeconds - renderRestoreStarted;
        bool operational = ReadBool(fpsController!, "IsOperational"), mapped = ReadBool(fpsController!, "IsMappingApplied");
        bool pluginSuspended = ReadBool(azurite!, "_exportSuspended"), controllerSuspended = ReadBool(fpsController!, "_exportSuspended");
        bool supported = ReadBool(azuriteHost!, "HostSupported"), bound = ReadBool(azuriteHost!, "HasPublicRenderControl");
        int target = Application.targetFrameRate, vsync = QualitySettings.vSyncCount, tier = UserSettings.Instance.fpsTier;
        bool ready = RenderControlV1.IsReady, owned = RenderControlV1.IsRenderingOwned;
        bool restored = operational && mapped && !pluginSuspended && !controllerSuspended && supported && bound
            && target == beforeFps && vsync == beforeVsync && tier == beforeTier && ready && !owned;
        if (restored || elapsed >= 5 || wall.Elapsed.TotalSeconds >= nextRenderRestoreLog)
        {
            nextRenderRestoreLog = wall.Elapsed.TotalSeconds + .25;
            var evidence = new { kind = "render-restore-wait", elapsedSeconds = elapsed, postUpdates, restored,
                operational, mapped, pluginSuspended, controllerSuspended, supported, bound,
                targetFrameRate = target, vSyncCount = vsync, tier, expectedFps = beforeFps, expectedVSync = beforeVsync,
                expectedTier = beforeTier, providerReady = ready, renderOwned = owned };
            events.Add(evidence); Plugin.Report("PATH-CHECK CHARACTER-VOICE RESTORE " + JsonSerializer.Serialize(evidence));
        }
        if (!restored && elapsed >= 5)
            throw new TimeoutException("Azurite binding/mapping or native FPS/VSync/tier did not restore within 5 seconds; no apply operation was forced.");
        return restored;
    }

    [HideFromIl2Cpp]
    private void RecordAttempt(string outcome) => attempts.Add(new { attempt, outcome, expectSilent, requestSequence, callbackCount, nativeStarts, nativeEnds,
        delayStartFrame = delayStart, firstEligibleFrame = dueFrame, lastCommittedFrame = committed,
        pcmStartFrame = requestPcmStart / Channels, pcmEndFrame = pcm.Count / Channels, cancelRemainder,
        peak = pcm.Skip(requestPcmStart).Select(Math.Abs).DefaultIfEmpty(0).Max() });

    [HideFromIl2Cpp]
    private bool TryReadyAzurite()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Azurite")
            ?? throw new InvalidOperationException("Actual Azurite consumer must remain selected for this native probe.");
        azurite = assembly.GetType("Azurite.Plugin", true)!.GetField("_current", Static)!.GetValue(null);
        if (azurite == null) return false;
        azuriteHost = azurite.GetType().GetField("_host", Instance)!.GetValue(azurite);
        fpsController = azurite.GetType().GetField("_fps", Instance)!.GetValue(azurite);
        return azuriteHost != null && fpsController != null && ReadBool(azuriteHost, "HostSupported")
            && ReadBool(azuriteHost, "HasPublicRenderControl") && ReadBool(fpsController, "IsOperational") && ReadBool(fpsController, "IsMappingApplied");
    }

    [HideFromIl2Cpp]
    private void BeforeAcquire() { beforeAcquireSeen = true; events.Add(new { kind = "BeforeAcquire", thread = Environment.CurrentManagedThreadId }); }
    [HideFromIl2Cpp]
    private void AfterRelease()
    {
        afterReleaseSeen = true;
        releaseSawRestoredClock = !adapterActive && !renderer && Time.captureDeltaTime == oldDelta
            && Read<string>(adapter!, null, "Status") != "capturing";
        events.Add(new { kind = "AfterRelease", clockRestored = releaseSawRestoredClock, thread = Environment.CurrentManagedThreadId });
    }

    [HideFromIl2Cpp]
    private void RequireLiveRequest()
    {
        if (Volatile.Read(ref callbackFailure) is { } callbackError) throw new InvalidOperationException(callbackError);
        if (!ReferenceEquals(gate, runtime!.GetField("Gate", Static)!.GetValue(null)) || GateLong("RequestSequence") != requestSequence)
            throw new InvalidOperationException($"Live Runtime Gate/request changed: expected={requestSequence}; actual={GateLong("RequestSequence")}; phase={Phase()}; reason={GateText("LastReason")}; row={Test.Instance?.cur}; auto={Test.Instance?.IsAutoEnabled}.");
    }
    [HideFromIl2Cpp]
    private bool NativePlaying() => (bool)runtime!.GetMethod("ReadNativePlaying", Static)!.Invoke(null, new object[] { audio! })!;
    [HideFromIl2Cpp]
    private int Phase() => Convert.ToInt32(Read<object>(gate!.GetType(), gate, "Phase"));
    [HideFromIl2Cpp]
    private long GateLong(string name) => Read<long>(gate!.GetType(), gate, name);
    [HideFromIl2Cpp]
    private string GateText(string name) => Read<object?>(gate!.GetType(), gate, name)?.ToString() ?? "";
    [HideFromIl2Cpp]
    private static T Read<T>(Type type, object? owner, string name)
    {
        var flags = owner == null ? Static : Instance;
        var property = type.GetProperty(name, flags);
        return (T)(property != null ? property.GetValue(owner) : type.GetField(name, flags)!.GetValue(owner))!;
    }
    [HideFromIl2Cpp]
    private static bool ReadBool(object owner, string name) => Read<bool>(owner.GetType(), owner, name);
    [HideFromIl2Cpp]
    private static object? Invoke(Type type, string name) => type.GetMethod(name, Static, null, Type.EmptyTypes, null)!.Invoke(null, null);
    [HideFromIl2Cpp]
    private void Check(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException(name);
        passed++; Plugin.Report("PATH-CHECK PASS character-voice " + name);
    }

    [HideFromIl2Cpp]
    private void RestoreCapture()
    {
        if (adapterActive) { Invoke(adapter!, "EndExport"); adapterActive = false; }
        if (renderer) { if (!AudioRenderer.Stop()) throw new InvalidOperationException("AudioRenderer.Stop failed."); renderer = false; }
        foreach (var saved in muted) if (saved.Source != null) saved.Source.mute = saved.Mute;
        muted.Clear();
        if (snapshots)
        {
            Time.captureDeltaTime = oldDelta; Time.timeScale = oldScale; Application.targetFrameRate = oldFps;
            QualitySettings.vSyncCount = oldVsync; OnDemandRendering.renderFrameInterval = oldInterval; Application.runInBackground = oldBackground;
            snapshots = false;
        }
        ownership?.Dispose(); ownership = null;
    }

    [HideFromIl2Cpp]
    private void Fail(Exception error)
    {
        while (error is TargetInvocationException wrapped && wrapped.InnerException != null) error = wrapped.InnerException;
        failure = error.GetType().Name + ": " + error.Message;
        Plugin.Report("PATH-CHECK FAIL character-voice: " + error); Finish();
    }

    [HideFromIl2Cpp]
    private void Finish()
    {
        if (finished) return;
        finished = true;
        bool cleanupOk = true;
        try
        {
            if (adapter != null) reads = Read<long>(adapter, null, "CaptureReads");
            RestoreCapture();
        }
        catch (Exception error)
        {
            cleanupOk = false; failure += "; cleanup: " + error.Message;
            Plugin.Report("PATH-CHECK FAIL character-voice cleanup: " + error);
            try { Invoke(typeof(RenderControlV1), "RestorationFailed"); } catch { }
        }
        if (subscribed) { RenderControlV1.BeforeAcquire -= BeforeAcquire; RenderControlV1.AfterRelease -= AfterRelease; subscribed = false; }
        // Stop only this requested voice via its original runtime cleanup path
        // if a failed probe left it active; never overwrite its Gate or caches.
        if (runtime != null && gate != null && requestSequence != 0 && GateLong("RequestSequence") == requestSequence
            && Read<bool>(gate.GetType(), gate, "Active"))
        {
            try { runtime.GetMethod("ResetOnMainThread", Static)!.Invoke(null, new object[] { "native-character-voice-fixture-finished" }); }
            catch (Exception error) { cleanupOk = false; failure += "; native request cleanup: " + error.Message; }
        }
        try
        {
            sceneHooks?.UnpatchSelf(); sceneHooks = null; active = null;
            if (storyPlayer != null)
            {
                if (autoSnapshot && storyPlayer.IsAutoEnabled != originalAuto && storyPlayer.autoToggle != null) storyPlayer.autoToggle.Toggle();
                storyPlayer.End();
            }
        }
        catch (Exception error) { cleanupOk = false; failure += "; scene cleanup: " + error.Message; }
        try
        {
            byte[] bytes = new byte[pcm.Count * sizeof(float)]; Buffer.BlockCopy(pcm.ToArray(), 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(Plugin.DiagnosticsRoot, "character-voice-pcm.f32le"), bytes);
            File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "character-voice-evidence.json"), JsonSerializer.Serialize(new
            {
                Schema = 1, Passed = failure.Length == 0 && cleanupOk, Failure = failure, CleanupOk = cleanupOk,
                ExpectedPcmOutcome = expectSilent ? "silent-user-voice-level-zero" : "audible",
                Rate, Channels, Fps = fps, AuthoredDelaySeconds = Delay, CommittedFrames = committed,
                PcmFrames = pcm.Count / Channels, PcmSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                ZeroSampleRenderTicks = zeroSampleRenderTicks,
                Attempts = attempts, Events = events, AdapterCaptureReads = reads,
                BlockedNativeAdvances = blockedNativeAdvances, RestoredGateCompletionObserved = restoredGateCompleted,
                PostDeadlineNativeAdvanceObserved = restoredNativeProgressed,
                RenderOwnedAfterCleanup = RenderControlV1.IsRenderingOwned, ProviderReadyAfterCleanup = RenderControlV1.IsReady,
                Scope = "Actual installed CharacterVoice Runtime/Gate, native SetVoice/preload/callback, AudioRenderer PCM, committed capture-frame delay and cancellation/Azurite boundary. This probe does not enter ExportHost, certify cold-load frame identity or produce/authorize a strict movie."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { cleanupOk = false; failure += "; evidence: " + error.Message; }
        Plugin.Report($"PATH-CHECK RESULT {passed} passed; {(failure.Length == 0 && cleanupOk ? 0 : 1)} failed");
    }

    public void OnDestroy() { if (!finished) { failure = "Fixture destroyed before completion."; Finish(); } }
}
