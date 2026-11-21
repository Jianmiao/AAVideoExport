using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// External acceptance only: one exact fixture GetBGM result is replaced with a
// missing owned file. The native loader, UnityWebRequest, completion body and
// production adapter callbacks all run unchanged. Never ship this test plugin.
public sealed class NativeBgmLifecycleChecks : MonoBehaviour
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const long FixtureBgmId = 12;
    private const string FixtureHash = "78D2B7D2C2DDDD87145E5C301703F7B04AE45A55AFB19236150084C1FED3EF22";
    private static NativeBgmLifecycleChecks? active;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly List<object> events = new();
    private readonly List<Attempt> attempts = new();
    private readonly Dictionary<IntPtr, Request> requests = new();
    private ScenarioResourceManager? resources;
    private GenericScenarioExcelTable? table;
    private AudioManager.BGMInfo? builtin;
    private Type? adapter;
    private Harmony? hooks;
    private Attempt? current, dispatching;
    private Il2CppSystem.Action<AudioClip>? unrelatedCallback;
    private bool injecting, unrelatedDispatch, finished;
    private string story = "", initialCache = "", observerFailure = "", phase = "native-readiness";
    private int stage, passed, unrelatedCallbacks;
    private double phaseStarted, nextReadinessLog;

    public NativeBgmLifecycleChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (finished) return;
        try
        {
            if (wall.Elapsed.TotalSeconds > 150) throw new TimeoutException("Native BGM lifecycle probe exceeded 150 seconds in " + phase + ".");
            if (observerFailure.Length != 0) throw new InvalidOperationException(observerFailure);
            switch (stage)
            {
                case 0: WaitForNativeResources(); break;
                case 1: WaitForFailure(); break;
                case 2: WaitForCancelledFailure(); break;
                case 3: WaitForBuiltinRecovery(); break;
                case 4: WaitForCancelledSuccess(); break;
            }
        }
        catch (Exception error) { Finish(Unwrap(error)); }
    }

    [HideFromIl2Cpp]
    private void WaitForNativeResources()
    {
        resources = ScenarioResourceManager.Instance;
        var host = typeof(ExportHost).GetField("Current", Members)?.GetValue(null);
        bool ready = SceneManager.GetActiveScene().name == "CatalogScene" && UserSettings.Instance?.data != null
            && resources != null && resources.AreDbsLoaded && resources.ExcelDb != null
            && resources.ScenarioCharacterNameQuery != null && !resources.Preloading
            && host != null && host.GetType().GetProperty("Busy", Members)?.GetValue(host) is false;
        if (!ready)
        {
            if (wall.Elapsed.TotalSeconds >= nextReadinessLog)
            {
                nextReadinessLog = wall.Elapsed.TotalSeconds + 5;
                Record("readiness", new { Scene = SceneManager.GetActiveScene().name, Databases = resources?.AreDbsLoaded,
                    Preloading = resources?.Preloading, Host = host != null });
            }
            return;
        }
        ValidateFixture();
        adapter = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeBgmSessionAdapter", true)!;
        builtin = resources!.GetBGM(FixtureBgmId);
        Require(builtin != null && builtin.id == FixtureBgmId && !builtin.isOverride && !string.IsNullOrWhiteSpace(builtin.path),
            "The frozen fixture must resolve BGM 12 to an actual built-in track before fault injection.");
        Require(!resources._loadedCustomBgms.ContainsKey(FixtureBgmId), "The controlled built-in id already has a native custom cache entry.");
        initialCache = CacheSnapshot();
        active = this;
        hooks = new Harmony("halocue.aa.videoexport.native-bgm-lifecycle-fixture");
        hooks.Patch(AccessTools.Method(typeof(ScenarioResourceManager), nameof(ScenarioResourceManager.GetBGM)),
            prefix: new HarmonyMethod(typeof(NativeBgmLifecycleChecks), nameof(OverrideExactFixtureInfo)) { priority = Priority.Last });
        hooks.Patch(AccessTools.Method(typeof(ScenarioResourceManager), nameof(ScenarioResourceManager.GetBgmClipAsync)),
            prefix: new HarmonyMethod(typeof(NativeBgmLifecycleChecks), nameof(BeforeDispatch)),
            postfix: new HarmonyMethod(typeof(NativeBgmLifecycleChecks), nameof(AfterDispatch)));
        hooks.Patch(AccessTools.Method(typeof(ScenarioResourceManager.__c__DisplayClass100_1),
                nameof(ScenarioResourceManager.__c__DisplayClass100_1._GetBgmClipAsync_b__1)),
            prefix: new HarmonyMethod(typeof(NativeBgmLifecycleChecks), nameof(BeforeCompletion)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(NativeBgmLifecycleChecks), nameof(AfterCompletion)) { priority = Priority.Last });
        hooks.Patch(AccessTools.Method(adapter, "AcceptLoaded"),
            prefix: new HarmonyMethod(typeof(NativeBgmLifecycleChecks), nameof(BeforeActualAdapterCallback)),
            postfix: new HarmonyMethod(typeof(NativeBgmLifecycleChecks), nameof(AfterActualAdapterCallback)));
        hooks.Patch(AccessTools.Method(typeof(ScenarioResourceManager), nameof(ScenarioResourceManager.ReleaseAsset)),
            prefix: new HarmonyMethod(typeof(NativeBgmLifecycleChecks), nameof(BeforeNativeRelease)));
        StartAttempt("native-missing-file", custom: true);
        Require(!Pump(current!), "The actual missing-file request unexpectedly became ready synchronously.");
        Require(OwnedRequests(current!).Count == 1, "The scoped override did not start exactly one native custom request; check menu BGM selection.");
        SetPhase("native-missing-file-terminal", 1);
    }

    [HideFromIl2Cpp]
    private void ValidateFixture()
    {
        story = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_STORY") ?? "";
        string root = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_FIXTURE_WORKSPACE") ?? "";
        Require(Path.IsPathFullyQualified(root) && Path.IsPathFullyQualified(story) && File.Exists(story),
            "Use the guarded runner with an explicit marked fixture workspace and frozen story path.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "aave-fixture-workspace.json")));
        Require(marker.RootElement.GetProperty("Owner").GetString() == "AAVE acceptance", "The workspace is not an independent acceptance fixture.");
        Require(SamePath(story, Path.Combine(root, "data", "saves", "AAVE-OfficialAuthoring-20261030.aas")),
            "This probe requires the independent frozen official-authoring fixture.");
        Require(SamePath(story, ScenarioResourceManager.GetPersistentFilePath("saves", Path.GetFileNameWithoutExtension(story), ".aas")),
            "The explicit frozen story does not match the active native workspace.");
        Require(HashFile(story) == FixtureHash, "The frozen official-authoring fixture bytes changed.");
        table = resources!.LoadGenericScenario(Path.GetFileNameWithoutExtension(story));
        Require(table != null && table.ByteBuffer != null && table.DataListLength > 0, "AA's native scenario loader did not produce the real fixture table.");
        var ids = new HashSet<long>();
        for (int i = 0; i < table!.DataListLength; i++)
        {
            var row = table.DataList(i)?.Cast<GenericScenarioExcel>() ?? throw new InvalidOperationException("The frozen native table has a missing row.");
            if (row.BGMId > 0) ids.Add(row.BGMId);
        }
        Require(ids.SetEquals(new[] { FixtureBgmId }), "The frozen fixture does not contain only authored BGM 12.");
        Record("native-fixture-loaded", new { StorySha256 = FixtureHash, Rows = table.DataListLength, AuthoredBgmIds = ids.ToArray() });
    }

    [HideFromIl2Cpp]
    private void StartAttempt(string name, bool custom, bool success = false, AudioManager.BGMInfo? reuseInfo = null)
    {
        string owned = Path.Combine(Plugin.DiagnosticsRoot, "native-bgm-lifecycle-owned", name);
        Directory.CreateDirectory(owned);
        string path = Path.Combine(owned, success ? "owned-valid-pcm.wav" : "intentionally-missing.wav");
        Require(!File.Exists(path), "The test-owned resource already exists.");
        if (success && reuseInfo == null) WriteOwnedWav(path);
        var attempt = new Attempt(name, reuseInfo ?? (custom ? new AudioManager.BGMInfo {
            id = builtin!.id, name = builtin.name, path = path, isOverride = true,
            volume = builtin.volume, loopInTime = builtin.loopInTime, loopOutTime = builtin.loopOutTime,
            transitionTime = builtin.transitionTime, offset = builtin.offset, shouldNotFadeOut = builtin.shouldNotFadeOut
        } : null), success);
        attempt.Session = adapter!.GetMethod("Prepare", Members)!.Invoke(null, new object?[] {
            table!, (Action<Exception>)(error => { attempt.Faults.Add(Unwrap(error)); }),
            (Action<string>)(message => Plugin.Report("PATH-CHECK BGM-LIFECYCLE ADAPTER " + message)),
            (Action<Exception>)(error => { attempt.CleanupFaults.Add(Unwrap(error)); })
        })!;
        attempts.Add(attempt); current = attempt;
        Record("attempt-start", new { Name = name, Custom = custom, Success = success, CacheHit = reuseInfo != null, InfoPointer = attempt.Info?.Pointer.ToString() });
    }

    [HideFromIl2Cpp]
    private bool Pump(Attempt attempt)
    {
        dispatching = attempt; injecting = attempt.Info != null;
        try { return (bool)adapter!.GetMethod("PumpPreload", Members)!.Invoke(attempt.Session, null)!; }
        finally { dispatching = null; injecting = false; }
    }

    [HideFromIl2Cpp]
    private void WaitForFailure()
    {
        RequirePhaseDeadline(25);
        var attempt = current!;
        Exception? failure = null;
        try { Require(!Pump(attempt), "Missing native BGM unexpectedly became ready."); }
        catch (Exception error) { failure = Unwrap(error); }
        if (failure == null) return;
        Require(failure is ExportException export && export.Code == "bgm_preload_failed", "Native terminal failure was not recoverable bgm_preload_failed: " + failure.Message);
        RequireNativeFailure(attempt);
        Require(attempt.Faults.Count == 1 && attempt.CleanupFaults.Count == 0, "Recoverable failure fault/cleanup callbacks were incorrect.");
        Dispose(attempt);
        Require(!Pending(attempt) && CleanupFailure(attempt) == null, "A settled native failure left permanent CleanupPending.");
        CheckCache();
        Pass("actual-native-failure-is-recoverable-and-disposal-settles");

        StartAttempt("cancelled-native-missing-file", custom: true);
        attempt = current!;
        // Same manager and same BGMInfo instance as the upcoming owned load, but
        // a different retained callback. This is a real native failure request.
        unrelatedCallback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AudioClip>>((Action<AudioClip>)(_ => unrelatedCallbacks++));
        dispatching = attempt; unrelatedDispatch = true;
        try { resources!.GetBgmClipAsync(attempt.Info!, unrelatedCallback); }
        finally { dispatching = null; unrelatedDispatch = false; }
        Require(!Pump(attempt), "The cancellation request unexpectedly became ready synchronously.");
        Require(OwnedRequests(attempt).Count == 1 && OwnedRequests(attempt).All(request => request.TerminalCount == 0),
            "Cancellation did not occur while the actual owned native request was pending.");
        Dispose(attempt);
        Require(Pending(attempt), "Disposing an in-flight native custom request did not retain its cleanup barrier.");
        Record("cancel-pending", new { Snapshot = State(attempt), OwnedCallbacks = OwnedRequests(attempt).Select(r => r.Callback.ToString()).ToArray() });
        SetPhase("cancelled-native-terminal", 2);
    }

    [HideFromIl2Cpp]
    private void WaitForCancelledFailure()
    {
        RequirePhaseDeadline(25);
        var attempt = current!;
        bool ownTerminal = OwnedRequests(attempt).Any(request => request.TerminalCount != 0);
        if (!ownTerminal) { Require(Pending(attempt), "The cancellation barrier cleared before the actual native completion."); return; }
        var unrelated = requests.Values.Single(request => request.Attempt == attempt && request.Unrelated);
        if (unrelated.TerminalCount == 0) return;
        RequireNativeFailure(attempt);
        Require(unrelated.TerminalCount == 1 && unrelated.OriginalRan && unrelated.Result != UnityWebRequest.Result.Success.ToString()
            && unrelatedCallbacks == 0, "The unrelated native failure did not execute its original no-user-callback branch.");
        Require(unrelated.UnrelatedStateUnchanged, "The adapter adopted an unrelated native completion with the same BGMInfo.");
        Require(!Pending(attempt) && CleanupFailure(attempt) == null, "Cancellation left permanent CleanupPending after the real native terminal callback.");
        Require(attempt.Faults.Count == 0 && attempt.CleanupFaults.Count == 0, "A disposed recoverable request reported a late active/cleanup fault.");
        Dispose(attempt); CheckCache();
        Pass("cancelled-native-failure-clears-after-real-completion");
        Pass("same-info-unrelated-native-completion-is-not-adopted");
        StartAttempt("fresh-builtin-after-failures", custom: false);
        SetPhase("fresh-builtin-readiness", 3);
    }

    [HideFromIl2Cpp]
    private void WaitForBuiltinRecovery()
    {
        RequirePhaseDeadline(45);
        var attempt = current!;
        if (!Pump(attempt)) return;
        var native = OwnedRequests(attempt);
        Require(native.Count > 0 && native.All(request => request.OriginalRan && !request.Override), "Fresh preparation did not use actual original built-in native loads.");
        var clips = Reservations(attempt).Select(reservation => Field(reservation, "Clip") as AudioClip).ToArray();
        Require(clips.Length > 0 && clips.All(clip => clip != null && clip.loadState == AudioDataLoadState.Loaded
            && clip.samples > 0 && clip.frequency > 0 && clip.channels > 0), "Fresh built-in preparation lacks actual decoded native clips.");
        Record("fresh-builtin-ready", new { Requests = native.Count, Clips = clips.Select(clip => new { Pointer = clip!.Pointer.ToString(), clip.samples, clip.frequency, clip.channels }).ToArray() });
        Require(attempt.Faults.Count == 0 && attempt.CleanupFaults.Count == 0, "Fresh real built-in preparation reported a fault.");
        Dispose(attempt);
        Require(!Pending(attempt) && CleanupFailure(attempt) == null, "Fresh built-in preparation did not release all owned references.");
        CheckCache();
        Require(HashFile(story) == FixtureHash, "The frozen scenario changed during the lifecycle probe.");
        Pass("fresh-real-builtin-preparation-recovers-and-releases");
        Pass("missing-file-requests-preserve-native-custom-cache-and-story");
        StartAttempt("cancelled-native-valid-wav", custom: true, success: true);
        attempt = current!;
        Require(!Pump(attempt), "The owned uncached WAV unexpectedly became ready synchronously.");
        Require(OwnedRequests(attempt).Count == 1 && OwnedRequests(attempt).All(request => request.TerminalCount == 0),
            "The valid-file cancellation did not occur during a genuine native request.");
        Dispose(attempt);
        Require(Pending(attempt), "The uncached successful request did not retain a cancellation barrier.");
        SetPhase("cancelled-native-success-terminal", 4);
    }

    [HideFromIl2Cpp]
    private void WaitForCancelledSuccess()
    {
        RequirePhaseDeadline(25);
        var attempt = current!;
        var request = OwnedRequests(attempt).Single();
        if (request.TerminalCount == 0) { Require(Pending(attempt), "The successful-request barrier cleared before its native completion."); return; }
        Require(request.OriginalRan && request.TerminalCount == 1 && request.Result == UnityWebRequest.Result.Success.ToString(),
            "The owned WAV did not complete through the actual native success branch.");
        Require(attempt.Callbacks == 1 && attempt.CallbackClip != IntPtr.Zero && !attempt.CacheBeforeCallback
            && attempt.PendingBeforeCallback && attempt.PendingAfterCallback,
            "The actual successful callback did not retain the barrier until native cache publication.");
        Require(request.CachePublishedAtTerminal && !Pending(attempt) && CleanupFailure(attempt) == null,
            "The successful native request did not publish its cache entry before clearing CleanupPending.");
        Require(attempt.Faults.Count == 0 && attempt.CleanupFaults.Count == 0 && attempt.CacheClipReleaseCalls == 0,
            "Cancelled native custom success reported a fault or released a cache-owned clip.");
        Require(resources!._loadedCustomBgms.TryGetValue(FixtureBgmId, out var cached) && cached != null
            && cached.Pointer == attempt.CallbackClip && cached.samples > 0 && cached.frequency == 48000 && cached.channels == 1,
            "The actual native cache does not own the decoded test WAV.");
        CheckOriginalCachePreserved(); Dispose(attempt);
        Pass("cancelled-native-success-publishes-cache-before-clearing-barrier");

        StartAttempt("fresh-native-custom-cache-hit", custom: true, success: true, reuseInfo: attempt.Info);
        var cacheHit = current!;
        Require(Pump(cacheHit), "Native cache-hit preparation did not complete synchronously with the decoded clip.");
        var cachedRequest = OwnedRequests(cacheHit).Single();
        Require(cachedRequest.OriginalRan && cachedRequest.Override && cachedRequest.TerminalCount == 0
            && cacheHit.Callbacks == 1 && cacheHit.CacheBeforeCallback && cacheHit.CallbackClip == attempt.CallbackClip,
            "The fresh adapter did not consume the actual original native cache-hit callback.");
        Dispose(cacheHit);
        Require(!Pending(cacheHit) && CleanupFailure(cacheHit) == null && cacheHit.Faults.Count == 0
            && cacheHit.CleanupFaults.Count == 0 && cacheHit.CacheClipReleaseCalls == 0 && attempt.CacheClipReleaseCalls == 0,
            "Disposing native cache-backed scopes released or faulted the cache-owned clip.");
        Require(resources._loadedCustomBgms.TryGetValue(FixtureBgmId, out var retained) && retained != null
            && retained.Pointer == attempt.CallbackClip && retained.samples > 0,
            "Disposing the native cache-backed scopes changed or destroyed the actual cache clip.");
        CheckOriginalCachePreserved();
        Pass("fresh-native-cache-hit-preserves-cache-owned-clip-on-disposal");
        Finish(null);
    }

    [HideFromIl2Cpp]
    private static void WriteOwnedWav(string path)
    {
        const int samples = 4800, bytes = samples * 2;
        using var output = new BinaryWriter(File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        output.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); output.Write(36 + bytes);
        output.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); output.Write(16);
        output.Write((short)1); output.Write((short)1); output.Write(48000); output.Write(96000);
        output.Write((short)2); output.Write((short)16); output.Write(System.Text.Encoding.ASCII.GetBytes("data")); output.Write(bytes);
        for (int i = 0; i < samples; i++) output.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 1000));
    }

    [HideFromIl2Cpp]
    private static bool OverrideExactFixtureInfo(ScenarioResourceManager __instance, long __0, ref AudioManager.BGMInfo __result)
    {
        var probe = active;
        if (probe == null || !probe.injecting || __instance.Pointer != probe.resources!.Pointer || __0 != FixtureBgmId) return true;
        __result = probe.dispatching!.Info!;
        return false;
    }

    [HideFromIl2Cpp]
    private static void BeforeDispatch(ScenarioResourceManager __instance, AudioManager.BGMInfo __0,
        Il2CppSystem.Action<AudioClip> __1, out Request? __state)
    {
        __state = null;
        var probe = active;
        if (probe?.dispatching == null || __instance.Pointer != probe.resources!.Pointer) return;
        try
        {
            __state = new Request(probe.dispatching, __0, __1, probe.unrelatedDispatch);
            probe.requests.Add(__1.Pointer, __state);
        }
        catch (Exception error) { probe.observerFailure = "Dispatch observation: " + error.Message; }
    }

    [HideFromIl2Cpp]
    private static void AfterDispatch(bool __runOriginal, Request? __state)
    {
        if (__state != null) __state.OriginalRan = __runOriginal;
    }

    [HideFromIl2Cpp]
    private static void BeforeCompletion(ScenarioResourceManager.__c__DisplayClass100_1 __instance, out Completion? __state)
    {
        __state = null;
        var probe = active;
        if (probe == null) return;
        try
        {
            var context = __instance.field_Public___c__DisplayClass100_0_0;
            if (context?.callback == null || !probe.requests.TryGetValue(context.callback.Pointer, out var request)) return;
            Require(context.__4__this?.Pointer == probe.resources!.Pointer && context.info?.Pointer == request.Info,
                "Actual native completion context differs from its dispatched request.");
            var native = __instance.req;
            Require(native != null && native.isDone, "Actual native completion arrived without a terminal UnityWebRequest.");
            __state = new Completion(request, probe.State(request.Attempt), probe.CacheSnapshot());
            request.RequestPointer = native!.Pointer.ToString(); request.Result = native.result.ToString();
            request.Error = native.error ?? ""; request.Url = native.url; request.TerminalCount++;
        }
        catch (Exception error) { probe.observerFailure = "Completion entry observation: " + error.Message; }
    }

    [HideFromIl2Cpp]
    private static Exception? AfterCompletion(Exception? __exception, Completion? __state)
    {
        var probe = active;
        if (probe == null || __state == null) return __exception;
        try
        {
            var request = __state.Request;
            request.NativeError = __exception?.GetType().Name + (__exception == null ? "" : ": " + __exception.Message);
            request.UnrelatedStateUnchanged = !request.Unrelated || __state.Before == probe.State(request.Attempt);
            if (!request.Attempt.Success)
                Require(__state.CacheBefore == probe.CacheSnapshot(), "A failed missing-file native completion changed the custom BGM cache.");
            else
            {
                request.CachePublishedAtTerminal = probe.resources!._loadedCustomBgms.TryGetValue(FixtureBgmId, out var cached)
                    && cached != null && cached.Pointer == request.Attempt.CallbackClip;
                Require(request.CachePublishedAtTerminal, "The successful native completion did not publish its callback clip into the cache.");
                probe.CheckOriginalCachePreserved();
            }
            probe.Record("actual-native-terminal", new { request.Attempt.Name, request.Unrelated, Callback = request.Callback.ToString(),
                request.RequestPointer, request.Result, request.Error, request.TerminalCount, request.UnrelatedStateUnchanged, request.CachePublishedAtTerminal,
                AdapterBefore = __state.Before, AdapterAfter = probe.State(request.Attempt) });
        }
        catch (Exception error) { probe.observerFailure = "Completion exit observation: " + error.Message; }
        return __exception;
    }

    [HideFromIl2Cpp]
    private static void BeforeActualAdapterCallback(object __instance, AudioClip __2, out Attempt? __state)
    {
        __state = null;
        var probe = active;
        if (probe == null) return;
        try
        {
            var attempt = probe.attempts.FirstOrDefault(value => ReferenceEquals(value.Session, __instance) && value.Info != null);
            if (attempt == null) return;
            __state = attempt; attempt.Callbacks++; attempt.CallbackClip = __2?.Pointer ?? IntPtr.Zero;
            attempt.CacheBeforeCallback = probe.resources!._loadedCustomBgms.ContainsKey(FixtureBgmId);
            attempt.PendingBeforeCallback = probe.Pending(attempt);
        }
        catch (Exception error) { probe.observerFailure = "Actual callback entry observation: " + error.Message; }
    }

    [HideFromIl2Cpp]
    private static void AfterActualAdapterCallback(Attempt? __state)
    {
        var probe = active;
        if (probe == null || __state == null) return;
        try
        {
            __state.PendingAfterCallback = probe.Pending(__state);
            probe.Record("actual-adapter-callback", new { __state.Name, __state.Callbacks, Clip = __state.CallbackClip.ToString(),
                __state.CacheBeforeCallback, CacheAfterCallback = probe.resources!._loadedCustomBgms.ContainsKey(FixtureBgmId),
                __state.PendingBeforeCallback, __state.PendingAfterCallback });
        }
        catch (Exception error) { probe.observerFailure = "Actual callback exit observation: " + error.Message; }
    }

    [HideFromIl2Cpp]
    private static void BeforeNativeRelease(ScenarioResourceManager __instance, UnityEngine.Object __0)
    {
        var probe = active;
        if (probe == null || __0 == null || __instance.Pointer != probe.resources!.Pointer) return;
        foreach (var attempt in probe.attempts)
            if (attempt.Info != null && attempt.CallbackClip == __0.Pointer) attempt.CacheClipReleaseCalls++;
    }

    [HideFromIl2Cpp]
    private void RequireNativeFailure(Attempt attempt)
    {
        var native = OwnedRequests(attempt);
        Require(native.Count == 1 && native.All(request => request.OriginalRan && request.Override && request.TerminalCount == 1
            && request.Result != UnityWebRequest.Result.Success.ToString()), "No exact original native request failure proves this result.");
        Require(attempt.Callbacks == 0, "The failing native request unexpectedly invoked its adapter AudioClip callback.");
        Require(native.All(request => SamePath(new Uri(request.Url).LocalPath, attempt.Info!.path)), "The failed actual request did not target the owned missing file.");
    }

    [HideFromIl2Cpp]
    private List<Request> OwnedRequests(Attempt attempt) => requests.Values.Where(request => request.Attempt == attempt && !request.Unrelated).ToList();
    [HideFromIl2Cpp]
    private static object? Field(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value);
    [HideFromIl2Cpp]
    private static List<object> Reservations(Attempt attempt)
    {
        var result = new List<object>();
        foreach (DictionaryEntry pair in (IDictionary)Field(attempt.Session!, "entries")!)
            foreach (var reservation in (IEnumerable)Field(pair.Value!, "Reservations")!) result.Add(reservation);
        return result;
    }
    [HideFromIl2Cpp]
    private string State(Attempt attempt) => JsonSerializer.Serialize(new { CleanupPending = Pending(attempt),
        CleanupFailure = CleanupFailure(attempt)?.GetType().Name, Failure = (Field(attempt.Session!, "failure") as Exception)?.Message,
        Faults = attempt.Faults.Count, CleanupFaults = attempt.CleanupFaults.Count,
        Reservations = Reservations(attempt).Select(value => new { Completed = Field(value, "Completed"),
            Pending = Field(value, "CustomPending"), Owned = Field(value, "OwnsReference"), Clip = (Field(value, "Clip") as AudioClip)?.Pointer.ToString() }).ToArray() });
    [HideFromIl2Cpp]
    private string CacheSnapshot()
    {
        var entries = new SortedDictionary<long, string>();
        foreach (var pair in resources!._loadedCustomBgms) entries.Add(pair.Key, pair.Value?.Pointer.ToString() ?? "null");
        return JsonSerializer.Serialize(entries);
    }
    [HideFromIl2Cpp]
    private void CheckCache() => Require(CacheSnapshot() == initialCache && !resources!._loadedCustomBgms.ContainsKey(FixtureBgmId),
        "The missing-file fixture changed native cache membership or clip identity.");
    [HideFromIl2Cpp]
    private void CheckOriginalCachePreserved()
    {
        var original = JsonSerializer.Deserialize<SortedDictionary<long, string>>(initialCache)!;
        var currentCache = JsonSerializer.Deserialize<SortedDictionary<long, string>>(CacheSnapshot())!;
        Require(currentCache.Remove(FixtureBgmId) && JsonSerializer.Serialize(currentCache) == JsonSerializer.Serialize(original),
            "Native success modified cache entries beyond the one test-owned fixture id.");
    }
    [HideFromIl2Cpp]
    private bool Pending(Attempt attempt) => (bool)adapter!.GetProperty("CleanupPending", Members)!.GetValue(attempt.Session)!;
    [HideFromIl2Cpp]
    private Exception? CleanupFailure(Attempt attempt) => adapter!.GetProperty("CleanupFailure", Members)!.GetValue(attempt.Session) as Exception;
    [HideFromIl2Cpp]
    private static void Dispose(Attempt attempt) { ((IDisposable)attempt.Session!).Dispose(); attempt.Disposed = true; }
    [HideFromIl2Cpp]
    private void SetPhase(string value, int nextStage) { phase = value; stage = nextStage; phaseStarted = wall.Elapsed.TotalSeconds; Record("phase", new { Phase = phase }); }
    [HideFromIl2Cpp]
    private void RequirePhaseDeadline(double seconds) => Require(wall.Elapsed.TotalSeconds - phaseStarted < seconds, "Timed out observing actual native state in " + phase + ".");
    [HideFromIl2Cpp]
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    [HideFromIl2Cpp]
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    [HideFromIl2Cpp]
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [HideFromIl2Cpp]
    private static Exception Unwrap(Exception error) { while (error is TargetInvocationException invocation && invocation.InnerException is { } inner) error = inner; return error; }
    [HideFromIl2Cpp]
    private void Pass(string name) { passed++; Plugin.Report("PATH-CHECK PASS bgm-lifecycle-" + name); }
    [HideFromIl2Cpp]
    private void Record(string kind, object detail)
    {
        var record = new { Kind = kind, Seconds = wall.Elapsed.TotalSeconds, UnityFrame = Time.frameCount, Thread = Environment.CurrentManagedThreadId, Detail = detail };
        events.Add(record); Plugin.Report("PATH-CHECK BGM-LIFECYCLE " + JsonSerializer.Serialize(record));
    }

    [HideFromIl2Cpp]
    private void Finish(Exception? failure)
    {
        if (finished) return;
        finished = true;
        var cleanup = new List<string>();
        foreach (var attempt in attempts)
            try { Dispose(attempt); }
            catch (Exception error) { cleanup.Add(Unwrap(error).Message); }
        try { hooks?.UnpatchSelf(); }
        catch (Exception error) { cleanup.Add(error.Message); }
        active = null;
        if (failure == null && cleanup.Count != 0) failure = new InvalidOperationException(string.Join("; ", cleanup));
        var evidence = new { Schema = 1, Scope = "External actual native BGM failure/cancellation/recovery probe; no rendered-story or performance claim",
            Passed = failure == null, PassedChecks = passed, Failure = failure?.ToString(), Phase = phase, Seconds = wall.Elapsed.TotalSeconds,
            Story = story, StorySha256 = File.Exists(story) ? HashFile(story) : "", InitialCustomCache = initialCache,
            FinalCustomCache = Diagnostic(() => CacheSnapshot()), CleanupErrors = cleanup,
            Attempts = attempts.Select(attempt => new { attempt.Name, attempt.Disposed, attempt.Success, attempt.Callbacks,
                CallbackClip = attempt.CallbackClip.ToString(), attempt.CacheBeforeCallback, attempt.PendingBeforeCallback,
                attempt.PendingAfterCallback, attempt.CacheClipReleaseCalls, State = Diagnostic(() => State(attempt)),
                Faults = attempt.Faults.Select(error => new { Type = error.GetType().Name, Code = (error as ExportException)?.Code, error.Message }).ToArray() }).ToArray(),
            Requests = requests.Values.Select(request => new { request.Attempt.Name, request.Unrelated, InfoPointer = request.Info.ToString(),
                CallbackPointer = request.Callback.ToString(), request.Override, request.OriginalRan, request.TerminalCount, request.RequestPointer,
                request.Url, request.Result, request.Error, request.NativeError, request.UnrelatedStateUnchanged, request.CachePublishedAtTerminal }).ToArray(),
            UnrelatedUserCallbacks = unrelatedCallbacks, Events = events };
        File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "native-bgm-lifecycle.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        if (failure != null) Plugin.Report("PATH-CHECK FAIL bgm-lifecycle: " + failure.Message);
        Plugin.Report($"PATH-CHECK RESULT {passed} passed; {(failure == null ? 0 : 1)} failed");
    }

    [HideFromIl2Cpp]
    private static string Diagnostic(Func<string> read)
    {
        try { return read(); }
        catch (Exception error) { return "unavailable: " + Unwrap(error).Message; }
    }

    private sealed class Attempt(string name, AudioManager.BGMInfo? info, bool success)
    {
        internal readonly string Name = name;
        internal readonly AudioManager.BGMInfo? Info = info;
        internal readonly bool Success = success;
        internal readonly List<Exception> Faults = new(), CleanupFaults = new();
        internal object? Session;
        internal bool Disposed, CacheBeforeCallback, PendingBeforeCallback, PendingAfterCallback;
        internal int Callbacks, CacheClipReleaseCalls;
        internal IntPtr CallbackClip;
    }
    private sealed class Request(Attempt attempt, AudioManager.BGMInfo info, Il2CppSystem.Action<AudioClip> callback, bool unrelated)
    {
        internal readonly Attempt Attempt = attempt;
        internal readonly IntPtr Info = info.Pointer, Callback = callback.Pointer;
        internal readonly bool Override = info.isOverride, Unrelated = unrelated;
        internal readonly Il2CppSystem.Action<AudioClip> RetainedCallback = callback;
        internal bool OriginalRan, UnrelatedStateUnchanged, CachePublishedAtTerminal;
        internal int TerminalCount;
        internal string RequestPointer = "", Result = "", Error = "", Url = "", NativeError = "";
    }
    private sealed record Completion(Request Request, string Before, string CacheBefore);
}
