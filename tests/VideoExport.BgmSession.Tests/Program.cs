using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using UnityEngine;

int passed = 0, failed = 0;
void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
void TestCase(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
    finally { ExportHost.Current = null; ScenarioResourceManager.Instance = null; }
}
TestCase("different native callback delays disappear before authored frame zero", () =>
{
    foreach (int delay in new[] { 2, 3 })
    {
        using var f = new Fixture(12, 12, 0);
        Check(!f.Pump(), "Capture became ready before original native asset callbacks.");
        Check(f.Manager.Loads == 3, "Two occurrences require two playback references and one retained pin.");
        for (int i = 0; i < delay; i++) { Time.realtimeSinceStartup++; Check(!f.Pump(), "Pending native load advanced readiness."); }
        f.Manager.CompleteAll(); Check(f.Pump(), "Loaded native clips did not release preparation.");
        f.Begin(); AudioClip? delivered = null;
        Check(!f.Dispatch(f.Manager.GetBGM(12), clip => delivered = clip), "Authored request fell back to delayed native loading.");
        Check(ReferenceEquals(delivered, f.Clip), "Callback did not retain the actual native clip identity.");
        Check(f.Manager.Loads == 3 && f.Manager.Pending.Count == 0, "Authored callback started a fresh asynchronous load.");
        f.Manager.ReleaseAsset(delivered!);
    }
});
TestCase("native load references transfer once and unused reservations release once", () =>
{
    var f = new Fixture(12, 12); f.Ready(); f.Begin();
    int callbacks = 0;
    Check(!f.Dispatch(f.Manager.GetBGM(12), clip => { callbacks++; f.Manager.ReleaseAsset(clip); }), "First callback was deferred.");
    Check(!f.Dispatch(f.Manager.GetBGM(12), clip => { callbacks++; f.Manager.ReleaseAsset(clip); }), "Second callback was deferred.");
    f.Dispose(); f.Dispose();
    Check(callbacks == 2 && f.Manager.References[f.Clip.Pointer] == 0 && f.Manager.Releases == 3, "Reference transfer or retained-pin cleanup was unbalanced.");
});
TestCase("transfer precedes original callback including its native rejection release", () =>
{
    var f = new Fixture(12); f.Ready(); f.Begin();
    f.Dispatch(f.Manager.GetBGM(12), clip => { f.Manager.ReleaseAsset(clip); throw new InvalidOperationException("original callback rejected"); });
    f.Dispose();
    Check(f.Faults.Count == 1 && f.Manager.References[f.Clip.Pointer] == 0, "A callback rejection doubled release or lost its fault.");
});
TestCase("cancelled pending native completions release their own references without publishing", () =>
{
    var f = new Fixture(12, 12); Check(!f.Pump(), "Fixture is not pending."); f.Dispose(); f.Manager.CompleteAll();
    Check(f.Manager.References[f.Clip.Pointer] == 0 && f.Manager.Releases == 3 && f.Faults.Count == 0, "Stale callback leaked references or mutated the cancelled session.");
});
TestCase("partially completed cancellation releases each arriving reference exactly once", () =>
{
    var f = new Fixture(12, 12); f.Pump(); f.Manager.CompleteNext(); f.Dispose(); f.Dispose();
    Check(f.Manager.Releases == 1, "Cancellation released a pending load or retained a completed one.");
    f.Manager.CompleteAll(); f.Dispose();
    Check(f.Manager.References[f.Clip.Pointer] == 0 && f.Manager.Releases == 3, "Mixed completed/pending cleanup lost reference ownership.");
});
TestCase("ambiguous native release failure stays owned without risking a second release", () =>
{
    foreach (bool afterEffect in new[] { false, true })
    {
        var f = new Fixture(12); f.Ready(); f.Manager.ReleaseFailuresRemaining = 1;
        f.Manager.ReleaseThrowsAfterEffect = afterEffect;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { f.Dispose(); throw new Exception("Native release failure was swallowed."); }
            catch (AggregateException) { }
            Check(f.CleanupPending && f.CleanupFailure != null,
                "Host may discard an ambiguous failed native release or pretend cleanup completed.");
        }
        Check(f.Manager.ReleaseAttempts == 2 && f.Manager.References[f.Clip.Pointer] == (afterEffect ? 0 : 1),
            "Cleanup retried an ambiguous side effect or abandoned the independent safe reference.");
        Check(f.CleanupFaults.Count == 1, "Ambiguous native cleanup failure was not reported exactly once.");
    }
});
TestCase("late cancelled builtin release failure reaches the host once even after disposal", () =>
{
    var f = new Fixture(12); f.Pump(); f.Dispose();
    f.Manager.ReleaseFailuresRemaining = 1; f.Manager.CompleteAll();
    Check(f.CleanupFaults.Count == 1 && f.CleanupFailure != null,
        "Late native release failure disappeared after the host discarded the pending builtin scope.");
    try { f.Dispose(); throw new Exception("Ambiguous late release was forgotten."); }
    catch (AggregateException) { }
    Check(f.CleanupFaults.Count == 1 && f.Manager.ReleaseAttempts == 2,
        "Late cleanup reported twice or retried an ambiguous native release.");
});
TestCase("settled custom cache publication cannot hide an ambiguous native release failure", () =>
{
    var f = new Fixture(12, 47);
    f.Manager.Infos[47] = new() { id = 47, path = "custom/47", isOverride = true };
    f.Manager.Clips[47] = new();
    f.Pump(); f.Manager.CompleteNext(); f.Manager.CompleteNext();
    f.Manager.ReleaseFailuresRemaining = 1;
    try { f.Dispose(); throw new Exception("Native release failure was swallowed."); }
    catch (AggregateException) { }
    Check(f.CleanupPending, "Pending custom callback was dropped after a native release failure.");
    try { f.Dispose(); throw new Exception("Ambiguous release failure was forgotten."); }
    catch (AggregateException) { }
    f.Manager.CompleteAll();
    Check(f.CleanupPending && f.CleanupFailure != null && f.Manager.CustomReleases == 0 && f.Manager.ReleaseAttempts == 2,
        "Custom completion hid failed reference ownership or released the native cache-owned clip.");
});
TestCase("old native callbacks cannot publish into a later active export", () =>
{
    var old = new Fixture(12); old.Pump(); old.Dispose();
    using var fresh = new Fixture(12); fresh.Ready(); fresh.Begin(); old.Manager.CompleteAll();
    AudioClip? delivered = null; fresh.Dispatch(fresh.Manager.GetBGM(12), clip => delivered = clip);
    Check(ReferenceEquals(delivered, fresh.Clip) && old.Manager.References[old.Clip.Pointer] == 0 && fresh.Faults.Count == 0, "A cancelled generation affected a later export.");
    fresh.Manager.ReleaseAsset(delivered!);
});
TestCase("clip decoding completes before readiness even after asset callback", () =>
{
    using var f = new Fixture(12); f.Clip.CompleteLoad = false; f.Pump(); f.Manager.CompleteAll();
    Check(!f.Pump() && f.Clip.LoadCalls == 1, "Unloaded or loading clip was published, or decoded repeatedly.");
    f.Clip.loadState = AudioDataLoadState.Loaded; Check(f.Pump(), "Native decoded clip did not become ready.");
});
TestCase("failed native decoding reports a functional preparation failure", () =>
{
    using var f = new Fixture(12); f.Clip.LoadSucceeds = false; f.Pump(); f.Manager.CompleteAll();
    f.Reject("bgm_preload_failed", () => f.Pump()); Check(f.Faults.Count == 1, "Decode failure was not retained.");
});
TestCase("wall timeout cannot be hidden by a frozen authored frame", () =>
{
    using var f = new Fixture(12); f.Pump(); Time.realtimeSinceStartup += 121;
    f.Reject("bgm_preload_timeout", () => f.Pump());
});
TestCase("voice warmup before the first BGM request does not consume its load timeout", () =>
{
    using var f = new Fixture(12); Time.realtimeSinceStartup += 500;
    Check(!f.Pump(), "New BGM loading unexpectedly completed.");
    f.Manager.CompleteAll(); Check(f.Pump(), "Earlier voice warmup incorrectly timed out BGM loading.");
});
TestCase("fresh BGMInfo retains authored gain offsets transitions and loop fields", () =>
{
    using var f = new Fixture(12); f.Ready(); f.Begin(); var info = f.Manager.GetBGM(12);
    info.volume = .35f; info.offset = 2; info.loopInTime = 3; info.loopOutTime = 9; info.transitionTime = .75f;
    f.Dispatch(info, clip => { Check(ReferenceEquals(clip, f.Clip), "Native source changed."); f.Manager.ReleaseAsset(clip); });
    Check(info.volume == .35f && info.offset == 2 && info.loopInTime == 3 && info.loopOutTime == 9 && info.transitionTime == .75f, "Preparation rewrote authored audio parameters.");
});
TestCase("missing native BGM descriptor preserves native no-load commands with an explicit warning", () =>
{
    const long unavailable = 3758162025;
    using var f = new Fixture(12, unavailable, unavailable, 0, -1);
    f.Ready(); f.Begin(); AudioClip? delivered = null;
    Check(f.Manager.Loads == 2 && f.Faults.Count == 0,
        "An unresolved native command blocked the story or invented an asset load/reference.");
    Check(f.Logs.Count(message => message.Contains(unavailable.ToString()) && message.Contains("unavailable")) == 1,
        "Missing native music was silently hidden or reported once per repeated row.");
    Check(!f.Dispatch(f.Manager.GetBGM(12), clip => delivered = clip) && ReferenceEquals(delivered, f.Clip),
        "Preserving a native no-load command changed an available track's original callback.");
    f.Manager.ReleaseAsset(delivered!);
});
TestCase("story containing only unresolved native BGM commands needs no invented clip", () =>
{
    using var f = new Fixture(3758162025, 3758162025);
    Check(f.Pump(), "A native no-load story waited for a clip that GetBGM does not supply.");
    f.Begin(); f.End(); f.Dispose();
    Check(f.Manager.Loads == 0 && f.Manager.ReleaseAttempts == 0 && !f.CleanupPending && f.Faults.Count == 0,
        "Native no-load commands were converted into resource ownership or an export failure.");
});
TestCase("previously unavailable native BGM cannot silently become a late asynchronous capture load", () =>
{
    const long unavailable = 3758162025;
    using var f = new Fixture(unavailable); Check(f.Pump(), "Native null descriptor was rejected."); f.Begin();
    f.Manager.Infos[unavailable] = new() { id = unavailable, path = "newly-present/clip" };
    int callbacks = 0;
    Check(!f.Dispatch(f.Manager.GetBGM(unavailable), _ => callbacks++), "Newly present native source escaped to asynchronous capture.");
    Check(callbacks == 0 && f.Manager.Loads == 0 && f.Faults.OfType<ExportException>().Single().Code == "bgm_not_prepared",
        "An unavailable-to-available source change was silently accepted.");
});
TestCase("malformed available descriptor is still rejected instead of treated as native no-load", () =>
{
    using var f = new Fixture(12); f.Manager.Infos[12].path = "";
    f.Reject("bgm_preload_failed", () => f.Pump());
    Check(f.Manager.Loads == 0 && f.Faults.Count == 1, "A malformed available source was silently dropped.");
});
TestCase("changed native load path cannot silently use an old ready clip", () =>
{
    using var f = new Fixture(12); f.Ready(); f.Begin(); var info = f.Manager.GetBGM(12); info.path = "another/clip";
    int callbacks = 0; Check(!f.Dispatch(info, _ => callbacks++), "Changed path fell back to asynchronous capture.");
    Check(callbacks == 0 && f.Faults.OfType<ExportException>().Single().Code == "bgm_source_changed", "Changed source was not rejected.");
});
TestCase("exhausted authored reservations cannot drift into asynchronous capture", () =>
{
    using var f = new Fixture(12); f.Ready(); f.Begin(); f.Dispatch(f.Manager.GetBGM(12), clip => f.Manager.ReleaseAsset(clip));
    int callbacks = 0; Check(!f.Dispatch(f.Manager.GetBGM(12), _ => callbacks++), "Unexpected dynamic request entered asynchronous loading.");
    Check(callbacks == 0 && f.Faults.OfType<ExportException>().Single().Code == "bgm_not_prepared", "Exhaustion consumed the retained pin.");
});
TestCase("inactive or foreign native resources keep their original loading path", () =>
{
    using var f = new Fixture(12); f.Ready();
    Check(f.Dispatch(f.Manager.GetBGM(12), _ => throw new Exception("inactive delivery")), "Inactive session intercepted loading.");
    f.Begin(); var foreign = new ScenarioResourceManager();
    Check(f.Dispatch(f.Manager.GetBGM(12), _ => throw new Exception("foreign delivery"), foreign), "Unowned resource manager was intercepted.");
    f.End(); Check(f.Dispatch(f.Manager.GetBGM(12), _ => throw new Exception("ended delivery")), "Ended session retained a playback hook.");
});
TestCase("custom override stays on its ready native cache path without addressable release", () =>
{
    var f = new Fixture(12, 12); f.Manager.Infos[12].isOverride = true; f.Ready(); f.Begin();
    Check(f.Manager.Loads == 1, "Custom cache hit was treated as a new owned load reference.");
    var info = f.Manager.GetBGM(12); AudioClip? delivered = null;
    Check(f.Dispatch(info, _ => throw new Exception("custom callback replaced")), "Adapter replaced native custom cache delivery.");
    f.Manager.GetBgmClipAsync(info, new(clip => delivered = clip));
    f.Dispose(); Check(ReferenceEquals(delivered, f.Clip) && f.Manager.CustomReleases == 0 && f.Manager.Releases == 0, "Adapter released or replaced a cache-owned custom clip.");
});
TestCase("cancelled custom load retains a cleanup barrier until native cache publication returns", () =>
{
    var f = new Fixture(12); f.Manager.Infos[12].isOverride = true; f.Pump(); f.Dispose();
    Check(f.CleanupPending, "Next story could load before the cancelled original custom callback settles.");
    f.Manager.CompleteAll();
    Check(!f.CleanupPending && ReferenceEquals(f.Manager._loadedCustomBgms[12], f.Clip) && f.Manager.CustomReleases == 0,
        "Cleanup did not respect the original callback-then-cache-publication boundary.");
});
TestCase("custom native failure without callback reports terminal preload failure", () =>
{
    var f = new Fixture(12); f.Manager.Infos[12].isOverride = true; f.Pump();
    f.Manager.CompleteNext(customSuccess: false);
    f.Reject("bgm_preload_failed", () => f.Pump());
    Check(f.Faults.Count == 1, "Native custom request failure was not surfaced once.");
    f.Dispose(); Check(!f.CleanupPending, "A completed failed native request left permanent cleanup pending.");
});
TestCase("cancelled custom native failure settles only after actual request completion", () =>
{
    var f = new Fixture(12); f.Manager.Infos[12].isOverride = true; f.Pump(); f.Dispose();
    Time.realtimeSinceStartup += 1000;
    Check(f.CleanupPending, "Elapsed time discarded a request that could still mutate the native cache.");
    f.Manager.CompleteNext(customSuccess: false);
    Check(!f.CleanupPending && f.Faults.Count == 0, "Completed cancelled custom failure did not safely settle.");
});
TestCase("custom success remains pending until the original callback returns and cache publishes", () =>
{
    var f = new Fixture(12); f.Manager.Infos[12].isOverride = true; f.Pump();
    f.Manager.BeforeCustomCachePublish = () =>
    {
        f.Dispose();
        Check(f.CleanupPending && !f.Manager._loadedCustomBgms.ContainsKey(12),
            "Cleanup passed the native callback-before-cache-publication boundary.");
    };
    f.Manager.CompleteAll();
    Check(!f.CleanupPending && ReferenceEquals(f.Manager._loadedCustomBgms[12], f.Clip),
        "Original native completion did not settle its owned success publication.");
});
TestCase("unrelated native custom completion cannot settle an owned pending request", () =>
{
    var f = new Fixture(12); f.Manager.Infos[12].isOverride = true; f.Pump(); f.Dispose();
    ScenarioResourceManager.ObserveCustomCompletion(new()
    {
        field_Public___c__DisplayClass100_0_0 = new()
        {
            info = f.Manager.GetBGM(12), __4__this = f.Manager,
            callback = new(_ => throw new Exception("Unrelated callback must not run."))
        },
        req = new() { isDone = true, result = UnityEngine.Networking.UnityWebRequest.Result.ConnectionError }
    });
    Check(f.CleanupPending && f.Faults.Count == 0, "An unrelated request changed the owned native completion barrier.");
    f.Manager.CompleteNext(customSuccess: false);
    Check(!f.CleanupPending, "The matching original callback identity did not settle its own request.");
});
TestCase("exception leaving original custom completion is preserved after its mutation boundary", () =>
{
    var f = new Fixture(12); f.Manager.Infos[12].isOverride = true; f.Pump();
    var original = new InvalidOperationException("Original native cache publication failed.");
    f.Manager.BeforeCustomCachePublish = () => throw original;
    try { f.Manager.CompleteAll(); throw new Exception("Original native exception was suppressed."); }
    catch (InvalidOperationException caught) when (ReferenceEquals(caught, original)) { }
    Check(f.Faults.Count == 1 && ReferenceEquals(f.Faults[0], original), "Original native completion fault was lost or replaced.");
    f.Dispose();
    Check(!f.CleanupPending && f.Manager.CustomReleases == 0, "Finished native mutation was left pending or cache ownership was changed.");
});
TestCase("failed delegate conversion does not invent a dispatched custom request", () =>
{
    var f = new Fixture(12); f.Manager.Infos[12].isOverride = true;
    Il2CppInterop.Runtime.DelegateSupport.FailNext = true;
    try { f.Pump(); throw new Exception("Delegate conversion failure was hidden."); }
    catch (InvalidOperationException) { }
    f.Dispose();
    Check(!f.CleanupPending && f.Manager.Loads == 0 && f.Faults.Count == 1,
        "A known pre-dispatch failure left a native request barrier with no native request.");
});
TestCase("entered native dispatch exception reports unknown cleanup instead of guessing whether work started", () =>
{
    foreach (bool afterSchedule in new[] { false, true })
    {
        var f = new Fixture(12); f.Manager.Infos[12].isOverride = true;
        f.Manager.DispatchThrowsBeforeSchedule = !afterSchedule;
        f.Manager.DispatchThrowsAfterSchedule = afterSchedule;
        try { f.Pump(); throw new Exception("Native dispatch exception was hidden."); }
        catch (InvalidOperationException) { }
        f.Dispose();
        Check(f.CleanupPending && f.CleanupFailure != null && f.CleanupFaults.Count == 1,
            "Ambiguous entered-dispatch failure was presented as normal cleanup waiting or ready.");
        Check(f.Manager.Loads == 1 && f.Manager.Pending.Count == (afterSchedule ? 1 : 0),
            "Unknown native work was cancelled or repeated after dispatch failed.");
        if (afterSchedule) f.Manager.CompleteNext(customSuccess: false);
        Check(f.CleanupPending && f.CleanupFaults.Count == 1 && f.Manager.ReleaseAttempts == 0,
            "Later native completion hid the explicit ambiguous-operation failure or invented a release.");
    }
});
TestCase("null native callback conversion fails before dispatch or custom ownership registration", () =>
{
    var f = new Fixture(12); f.Manager.Infos[12].isOverride = true;
    Il2CppInterop.Runtime.DelegateSupport.ReturnNullNext = true;
    f.Reject("bgm_preload_failed", () => f.Pump());
    f.Dispose();
    Check(!f.CleanupPending && f.CleanupFailure == null && f.Manager.Loads == 0,
        "Missing converted callback entered native loading or retained an imaginary request.");
});
TestCase("effective menu builtin selection is prepared without mutating authored IDs", () =>
{
    MenuSelectionFixture.Set("Builtin", 47, true);
    try
    {
        using var f = new Fixture(12, 12);
        f.Manager.Infos[47] = new() { id = 47, path = "bgms/Theme_47", name = "Theme_47" }; f.Manager.Clips[47] = f.Clip;
        f.Ready(); f.Begin(); AudioClip? delivered = null;
        Check(!f.Dispatch(f.Manager.GetBGM(47), clip => delivered = clip) && ReferenceEquals(delivered, f.Clip), "Original builtin remap was not covered by preparation.");
        Check(f.Manager.Loads == 3 && f.Manager.Pending.Count == 0, "Remapped source introduced an arbitrary reservation budget.");
        f.Manager.ReleaseAsset(delivered!);
    }
    finally { MenuSelectionFixture.Set("None", 0, false); }
});
TestCase("disabled or custom menu selection preserves native authored builtin loading", () =>
{
    foreach (var selection in new[] { ("Builtin", false), ("Custom", true) })
    {
        MenuSelectionFixture.Set(selection.Item1, 47, selection.Item2);
        try
        {
            using var f = new Fixture(12); f.Ready(); f.Begin(); AudioClip? delivered = null;
            Check(!f.Dispatch(f.Manager.GetBGM(12), clip => delivered = clip) && ReferenceEquals(delivered, f.Clip), "Preparation changed a custom or disabled original Mod route.");
            f.Manager.ReleaseAsset(delivered!);
        }
        finally { MenuSelectionFixture.Set("None", 0, false); }
    }
});
Console.WriteLine($"RESULT {passed} passed; {failed} failed; original native BGM resource ownership and authored callback timing");
return failed == 0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly Type adapter = typeof(Fixture).Assembly.GetType("AAVideoExport.Plugin.NativeBgmSessionAdapter")
        ?? throw new InvalidOperationException("Native BGM readiness adapter is missing.");
    private readonly object scope;
    public readonly ScenarioResourceManager Manager = new();
    public readonly AudioClip Clip = new();
    public readonly List<Exception> Faults = new();
    public readonly List<Exception> CleanupFaults = new();
    public readonly List<string> Logs = new();
    public readonly Test Player = new();
    public Fixture(params long[] ids)
    {
        Time.realtimeSinceStartup = 100; ScenarioResourceManager.Instance = Manager;
        Manager.Infos[12] = new() { id = 12 }; Manager.Clips[12] = Clip;
        var prepare = adapter.GetMethod("Prepare", Flags)!;
        object?[] args = { new GenericScenarioExcelTable(ids), (Action<Exception>)Faults.Add, (Action<string>)Logs.Add };
        if (prepare.GetParameters().Length == 4) args = args.Append((Action<Exception>)CleanupFaults.Add).ToArray();
        scope = Invoke(prepare, null, args)!;
    }
    private static object? Invoke(MethodInfo method, object? target, params object?[] args)
    {
        try { return method.Invoke(target, args); } catch (TargetInvocationException e) { throw e.InnerException!; }
    }
    public bool Pump() => (bool)Invoke(adapter.GetMethod("PumpPreload", Flags)!, scope)!;
    public bool CleanupPending => (bool)(adapter.GetProperty("CleanupPending", Flags)
        ?? throw new MissingMemberException("Native custom loading cleanup barrier is missing.")).GetValue(scope)!;
    public Exception? CleanupFailure => (Exception?)(adapter.GetProperty("CleanupFailure", Flags)
        ?? throw new MissingMemberException("Ambiguous native cleanup fault must remain observable.")).GetValue(scope);
    public void Ready() { Pump(); Manager.CompleteAll(); if (!Pump()) throw new Exception("Fixture preload incomplete."); }
    public void Begin() { ExportHost.Current = new(Player); Invoke(adapter.GetMethod("BeginPass", Flags)!, scope, Player); }
    public void End() => Invoke(adapter.GetMethod("EndPass", Flags)!, scope);
    public bool Dispatch(AudioManager.BGMInfo info, Action<AudioClip> callback, ScenarioResourceManager? manager = null) =>
        (bool)Invoke(adapter.GetMethod("AllowOriginalLoad", Flags)!, null, manager ?? Manager, info, new Il2CppSystem.Action<AudioClip>(callback))!;
    public void Dispose() => ((IDisposable)scope).Dispose();
    public void Reject(string code, Action action)
    {
        try { action(); } catch (ExportException e) when (e.Code == code) { return; }
        throw new Exception("Expected " + code);
    }
}

// The real optional-Mod lookup is exercised against its documented public
// surface, without installing that Mod or replacing the adapter resolver.
static class MenuSelectionFixture
{
    private static Type? store, setting;
    internal static void Set(string kind, long id, bool enabled)
    {
        if (store == null)
        {
            var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MenuBgmSelector"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("selection-fixture");
            var type = module.DefineType("AzureArchive.MenuBgmSelector.BgmStore", TypeAttributes.Public);
            var field = type.DefineField("Value", typeof(object), FieldAttributes.Public | FieldAttributes.Static);
            var getter = type.DefineMethod("get_Current", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName, typeof(object), Type.EmptyTypes);
            var il = getter.GetILGenerator(); il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, field); il.Emit(System.Reflection.Emit.OpCodes.Ret);
            var property = type.DefineProperty("Current", PropertyAttributes.None, typeof(object), null); property.SetGetMethod(getter); store = type.CreateType()!;
            type = module.DefineType("AzureArchive.MenuBgmSelector.GameLobbySetting", TypeAttributes.Public);
            field = type.DefineField("Enabled", typeof(bool), FieldAttributes.Public | FieldAttributes.Static);
            var method = type.DefineMethod("IsLobbyBgmEnabled", MethodAttributes.Public | MethodAttributes.Static, typeof(bool), Type.EmptyTypes);
            il = method.GetILGenerator(); il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, field); il.Emit(System.Reflection.Emit.OpCodes.Ret); setting = type.CreateType()!;
        }
        store.GetField("Value")!.SetValue(null, new Selection { Kind = kind, Id = id });
        setting!.GetField("Enabled")!.SetValue(null, enabled);
    }
    public sealed class Selection { public string Kind = "None"; public long Id; }
}
