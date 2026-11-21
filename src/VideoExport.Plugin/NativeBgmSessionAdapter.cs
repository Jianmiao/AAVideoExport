using System.Reflection;
using AAVideoExport.Core;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Networking;

namespace AAVideoExport.Plugin;

// Resource preparation only. The original AudioManager callback/coroutine owns
// playback, source selection, volume, offsets, fades and native loop restarts.
// A built-in native load acquires one Addressables reference. Reserve those
// actual loads before the authored clock starts and transfer one per callback;
// retaining a managed AudioClip wrapper alone would not preserve that contract.
internal sealed class NativeBgmSessionAdapter : IDisposable
{
    private static NativeBgmSessionAdapter? active;
    private static readonly Dictionary<IntPtr, CustomLoad> customLoads = new();
    private readonly ScenarioResourceManager resources;
    private readonly Dictionary<long, int> occurrences;
    private readonly Dictionary<long, Entry> entries = new();
    private readonly Action<Exception> fault;
    private readonly Action<string> log;
    private readonly Action<Exception>? cleanupFault;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private double started;
    private Test? player;
    private Exception? failure;
    private Exception? cleanupFailure;
    private bool requested, ready, passStarted, disposed, faultReported;
    private int generation;

    private NativeBgmSessionAdapter(GenericScenarioExcelTable story, Action<Exception> fault, Action<string> log,
        Action<Exception>? cleanupFault)
    {
        this.fault = fault ?? throw new ArgumentNullException(nameof(fault));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.cleanupFault = cleanupFault;
        resources = ScenarioResourceManager.Instance
            ?? throw new ExportException("bgm_preload_failed", "The native BGM resource manager is unavailable.");
        occurrences = CollectOccurrences(story);
    }

    internal static NativeBgmSessionAdapter Prepare(GenericScenarioExcelTable story, Action<Exception> fault, Action<string> log,
        Action<Exception>? cleanupFailure = null)
    {
        if (!ExportBuildProfile.SinglePassEnabled)
            throw new ExportException("invalid_state", "Native BGM preparation belongs to the single-pass export path.");
        return new(story, fault, log, cleanupFailure);
    }

    // A native custom miss inserts its cache entry after our callback returns.
    // The host must retain this disposed scope and block the next story load
    // until a later main-thread update observes that those callbacks settled.
    // Built-in late completions release their own references and cannot replace
    // the native custom cache, so they do not require this story-load barrier.
    internal bool CleanupPending => disposed && (cleanupFailure != null || entries.Values.Any(entry =>
        entry.Reservations.Any(reservation => reservation.CustomPending || reservation.OwnsReference)));
    internal Exception? CleanupFailure => cleanupFailure;

    internal bool PumpPreload()
    {
        RequireThread();
        if (disposed) throw new ObjectDisposedException(nameof(NativeBgmSessionAdapter));
        ThrowFailure();
        if (ScenarioResourceManager.Instance?.Pointer != resources.Pointer)
            throw Fail("bgm_source_changed", "The native BGM resource manager changed during preparation.");
        if (!requested)
        {
            started = Time.realtimeSinceStartup;
            requested = true;
            try
            {
                foreach (var pair in occurrences)
                {
                    var info = resources.GetBGM(pair.Key);
                    if (info == null)
                    {
                        // Native PlayBGM returns without requesting a clip when
                        // GetBGM returns null; SetBGM still applies its original
                        // fade/stop behavior. Keep that command in the story and
                        // retain the native missing-source warning. This does
                        // not excuse an available descriptor's load/decode error.
                        log($"native BGM unavailable (id={pair.Key}): resolver returned no descriptor; original AA command/no-load behavior retained. Missing music is not restored.");
                        continue;
                    }
                    if (info.id != pair.Key || !info.isOverride && string.IsNullOrWhiteSpace(info.path))
                        throw new ExportException("bgm_preload_failed", "The native BGM source is incomplete.");
                    var entry = new Entry(info); entries.Add(pair.Key, entry);
                    // Native override hits are cache-owned and do not acquire
                    // Addressables references. Never release them as reserves.
                    int count = info.isOverride ? 1 : checked(pair.Value + 1);
                    for (int index = 0; index < count; index++)
                    {
                        var reservation = new Reservation(!info.isOverride, pin: index == 0);
                        entry.Reservations.Add(reservation);
                        int token = generation;
                        reservation.Callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AudioClip>>(
                            (Action<AudioClip>)(clip => AcceptLoaded(reservation, token, clip)))
                            ?? throw new ExportException("bgm_preload_failed", "The native BGM callback could not be created before loading.");
                        if (info.isOverride)
                        {
                            customLoads.Add(reservation.Callback.Pointer, new(this, reservation, info.Pointer));
                            // Entering native dispatch may schedule work before
                            // throwing; only its real completion can settle it.
                            reservation.CustomPending = true;
                        }
                        try { resources.GetBgmClipAsync(info, reservation.Callback); }
                        catch (Exception error)
                        {
                            // Native dispatch starts the request before wiring
                            // its completion delegate. A throw cannot prove that
                            // no native work remains, even without a callback.
                            RecordCleanupFailure(error);
                            throw;
                        }
                        // A native custom cache hit calls us synchronously and
                        // publishes no later mutation. Async misses settle at
                        // their original completion boundary below instead.
                        if (info.isOverride && reservation.Completed)
                            SettleCustomLoad(reservation);
                    }
                }
            }
            catch (Exception error) { RetainFailure(error); }
        }
        ThrowFailure();
        bool complete = true;
        var visited = new HashSet<IntPtr>();
        foreach (var entry in entries.Values)
        foreach (var reservation in entry.Reservations)
        {
            var clip = reservation.Clip;
            if (clip == null) { complete = false; continue; }
            if (!visited.Add(clip.Pointer)) continue;
            if (clip.loadState == AudioDataLoadState.Unloaded)
            {
                if (!clip.LoadAudioData()) throw Fail("bgm_preload_failed", "The native BGM clip could not load its actual audio data.");
            }
            if (clip.loadState == AudioDataLoadState.Failed)
                throw Fail("bgm_preload_failed", "The native BGM clip failed to decode.");
            if (clip.loadState != AudioDataLoadState.Loaded) { complete = false; continue; }
            if (clip.samples <= 0 || clip.frequency <= 0 || clip.channels <= 0)
                throw Fail("bgm_preload_failed", "The native BGM clip has invalid decoded audio dimensions.");
        }
        if (!complete && Time.realtimeSinceStartup - started >= 120)
            throw Fail("bgm_preload_timeout", "Native BGM preparation exceeded 120 seconds before authored capture.");
        if (complete && !ready) log($"AA Video Export: native BGM resources ready; sources={entries.Count}; original playback callbacks retained.");
        ready = complete;
        return complete;
    }

    internal void BeginPass(Test ownedPlayer)
    {
        RequireThread();
        if (passStarted || active != null || ownedPlayer == null || ExportHost.Current?.OwnsCapturePlayer(ownedPlayer) != true)
            throw new ExportException("invalid_state", "Native BGM playback requires one owned capture player.");
        if (!PumpPreload()) throw Fail("bgm_not_prepared", "Authored playback cannot begin before native BGM readiness.");
        player = ownedPlayer; passStarted = true; active = this;
    }

    internal void EndPass()
    {
        RequireThread();
        if (ReferenceEquals(active, this)) active = null;
        player = null;
    }

    // True preserves the native loader. False means the original callback has
    // received its ready native load, or the owning export was explicitly failed.
    internal static bool AllowOriginalLoad(ScenarioResourceManager manager, AudioManager.BGMInfo info,
        Il2CppSystem.Action<AudioClip> callback)
    {
        var session = active;
        if (session == null || session.disposed || session.player == null || manager.Pointer != session.resources.Pointer
            || ExportHost.Current?.OwnsCapturePlayer(session.player) != true) return true;
        try
        {
            session.RequireThread(); session.ThrowFailure();
            if (info == null || !session.entries.TryGetValue(info.id, out var entry))
                throw new ExportException("bgm_not_prepared", "An authored native BGM source was not prepared for this story.");
            if (info.isOverride != entry.IsOverride || !string.Equals(info.path, entry.Path, StringComparison.Ordinal))
                throw new ExportException("bgm_source_changed", "The native BGM load source changed after preparation.");
            if (callback == null) throw new ExportException("bgm_preload_failed", "The original native BGM callback is missing.");
            var retained = entry.Reservations[0].Clip;
            if (retained == null || retained.loadState != AudioDataLoadState.Loaded)
                throw new ExportException("bgm_not_prepared", "The retained native BGM clip is no longer ready.");
            if (entry.IsOverride)
            {
                // Original override cache hits already complete synchronously;
                // preserve that cache's own lifetime and delivery semantics.
                if (!manager._loadedCustomBgms.TryGetValue(info.id, out var cached) || cached == null || cached.Pointer != retained.Pointer)
                    throw new ExportException("bgm_source_changed", "The prepared native custom BGM cache entry changed.");
                return true;
            }
            var reservation = entry.Reservations.FirstOrDefault(value => !value.Pin && value.OwnsReference);
            if (reservation?.Clip == null || reservation.Clip.loadState != AudioDataLoadState.Loaded)
                throw new ExportException("bgm_not_prepared", "The story requested more native BGM loads than its prepared authored occurrences.");
            // The original callback can synchronously reject/release the clip.
            // Transfer first so callback failure or export cancellation cannot
            // release that same native reference for a second time.
            reservation.OwnsReference = false;
            callback.Invoke(reservation.Clip);
            return false;
        }
        catch (Exception error)
        {
            session.RetainFailure(error); session.ReportFailure();
            return false;
        }
    }

    private void AcceptLoaded(Reservation reservation, int token, AudioClip clip)
    {
        if (Environment.CurrentManagedThreadId != thread)
        {
            RetainFailure(new ExportException("bgm_preload_failed", "The native BGM loader completed outside its owned main thread."));
            return;
        }
        if (reservation.Completed)
        {
            RetainFailure(new ExportException("bgm_preload_failed", "The native BGM loader completed one request more than once."));
            return;
        }
        reservation.Completed = true; reservation.Clip = clip;
        reservation.OwnsReference = reservation.NativeReference && clip != null;
        if (disposed || token != generation)
        {
            try { Release(reservation); }
            catch (Exception error) { log("AA Video Export: late native BGM load cleanup failed: " + error.Message); }
            return;
        }
        if (clip == null) RetainFailure(new ExportException("bgm_preload_failed", "The native BGM loader returned no clip."));
    }

    // This exact native completion owns the custom request. Its failure branch
    // never invokes the supplied callback; its success branch writes the cache
    // only after that callback returns. Observe the completed invocation, not a
    // timeout or a scan of unrelated UnityWebRequests.
    internal static void ObserveCustomCompletion(ScenarioResourceManager.__c__DisplayClass100_1 completion,
        Exception? nativeError)
    {
        var context = completion.field_Public___c__DisplayClass100_0_0;
        var callback = context?.callback;
        if (callback == null || !customLoads.TryGetValue(callback.Pointer, out var load)) return;
        var session = load.Session;
        try
        {
            session.RequireThread();
            if (context!.__4__this?.Pointer != session.resources.Pointer || context.info?.Pointer != load.Info)
                throw new ExportException("bgm_source_changed", "The owned native custom BGM completion changed its resource context.");
            var request = completion.req;
            if (request == null || !request.isDone)
                throw new ExportException("bgm_preload_failed", "Native custom BGM completion arrived before its request completed.");
            // Native invocation has finished. No cache publication remains
            // behind this barrier, even when native loading did not call us.
            session.SettleCustomLoad(load.Reservation);
            if (nativeError != null) session.RetainFailure(nativeError);
            else if (request.result != UnityWebRequest.Result.Success || !load.Reservation.Completed)
                session.RetainFailure(new ExportException("bgm_preload_failed", "The native custom BGM request completed without a playable clip."));
            load.Reservation.Completed = true;
            session.ReportFailure();
        }
        catch (Exception error) { session.RetainFailure(error); session.ReportFailure(); }
    }

    private void SettleCustomLoad(Reservation reservation)
    {
        reservation.CustomPending = false;
        if (reservation.Callback != null) customLoads.Remove(reservation.Callback.Pointer);
    }

    public void Dispose()
    {
        RequireThread();
        if (!disposed) { disposed = true; generation++; }
        EndPass();
        List<Exception>? failures = null;
        foreach (var entry in entries.Values)
        foreach (var reservation in entry.Reservations)
        {
            try { Release(reservation); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures != null) throw new AggregateException("Native BGM preparation references could not all be released.", failures);
    }

    private void Release(Reservation reservation)
    {
        if (!reservation.OwnsReference || reservation.Clip == null) return;
        if (reservation.ReleaseFailure != null) throw reservation.ReleaseFailure;
        try
        {
            resources.ReleaseAsset(reservation.Clip);
            reservation.OwnsReference = false;
        }
        catch (Exception error)
        {
            // Native ReleaseAsset can throw after Addressables.Release has
            // already decremented its reference. Never retry an ambiguous
            // side effect; retain the fault and require host recovery instead.
            reservation.ReleaseFailure = error;
            RecordCleanupFailure(error);
            throw;
        }
    }
    private void RecordCleanupFailure(Exception error)
    {
        if (Interlocked.CompareExchange(ref cleanupFailure, error, null) != null) return;
        // Native loader callbacks and Dispose both enforce this scope's main
        // thread. Late cancelled builtin callbacks must still reach the host.
        cleanupFault?.Invoke(error);
    }
    private void RequireThread()
    {
        if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Native BGM preparation requires the owned main thread.");
    }
    private Exception Fail(string code, string message) { RetainFailure(new ExportException(code, message)); ReportFailure(); return failure!; }
    private void RetainFailure(Exception error) => Interlocked.CompareExchange(ref failure, error, null);
    private void ReportFailure()
    {
        if (faultReported || failure == null || disposed) return;
        faultReported = true; fault(failure);
    }
    private void ThrowFailure() { if (failure != null) { ReportFailure(); throw failure; } }

    private static Dictionary<long, int> CollectOccurrences(GenericScenarioExcelTable story)
    {
        if (story == null || story.ByteBuffer == null || story.DataListLength <= 0)
            throw new ArgumentException("The actual selected native story table is required.", nameof(story));
        var result = new Dictionary<long, int>();
        for (int index = 0; index < story.DataListLength; index++)
        {
            var row = (story.DataList(index) ?? throw new ExportException("bgm_preload_failed", "The selected native story contains a missing row.")).Cast<GenericScenarioExcel>();
            if (row.GetIl2CppType().FullName != "GenericScenarioExcel")
                throw new ExportException("bgm_preload_failed", "Native BGM preparation requires concrete selected story rows.");
            long id = row.BGMId; if (id <= 0) continue;
            id = EffectiveMenuBgmId(id);
            result[id] = checked(result.GetValueOrDefault(id) + 1);
        }
        return result;
    }

    private static long EffectiveMenuBgmId(long authoredId)
    {
        if (authoredId != 12) return authoredId;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => value.GetName().Name == "MenuBgmSelector");
        if (assembly == null) return authoredId;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var store = assembly.GetType("AzureArchive.MenuBgmSelector.BgmStore", throwOnError: true)!;
        var selected = store.GetProperty("Current", flags)?.GetValue(null);
        if (selected == null) return authoredId;
        object? Read(string name) => selected.GetType().GetField(name, flags)?.GetValue(selected)
            ?? selected.GetType().GetProperty(name, flags)?.GetValue(selected);
        if (Read("Kind")?.ToString() != "Builtin") return authoredId;
        long id = Convert.ToInt64(Read("Id")); if (id <= 0 || id == authoredId) return authoredId;
        var settings = assembly.GetType("AzureArchive.MenuBgmSelector.GameLobbySetting", throwOnError: true)!;
        var enabled = settings.GetMethod("IsLobbyBgmEnabled", flags) ?? throw new MissingMethodException(settings.FullName, "IsLobbyBgmEnabled");
        return enabled.Invoke(null, null) is true ? id : authoredId;
    }

    private sealed class Entry(AudioManager.BGMInfo info)
    {
        internal readonly string Path = info.path;
        internal readonly bool IsOverride = info.isOverride;
        internal readonly List<Reservation> Reservations = new();
    }
    private sealed record CustomLoad(NativeBgmSessionAdapter Session, Reservation Reservation, IntPtr Info);
    private sealed class Reservation(bool nativeReference, bool pin)
    {
        internal readonly bool NativeReference = nativeReference, Pin = pin;
        internal Il2CppSystem.Action<AudioClip>? Callback;
        internal AudioClip? Clip;
        internal bool Completed, OwnsReference, CustomPending;
        internal Exception? ReleaseFailure;
    }
}

[HarmonyPatch(typeof(ScenarioResourceManager.__c__DisplayClass100_1), nameof(ScenarioResourceManager.__c__DisplayClass100_1._GetBgmClipAsync_b__1))]
internal static class NativeCustomBgmCompletionHook
{
    private static Exception? Finalizer(ScenarioResourceManager.__c__DisplayClass100_1 __instance, Exception? __exception)
    {
        NativeBgmSessionAdapter.ObserveCustomCompletion(__instance, __exception);
        return __exception;
    }
}

[HarmonyPatch(typeof(ScenarioResourceManager), nameof(ScenarioResourceManager.GetBgmClipAsync))]
internal static class NativePreparedBgmHook
{
    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(ScenarioResourceManager __instance, AudioManager.BGMInfo __0, Il2CppSystem.Action<AudioClip> __1) =>
        NativeBgmSessionAdapter.AllowOriginalLoad(__instance, __0, __1);
}
