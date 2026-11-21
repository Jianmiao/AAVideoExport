using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MonoMod.Cil;
using AAVideoExport.Plugin;
using AAVideoExport.Integration;

// Execute the production patch validation/unpatch/Shutdown paths against real
// Harmony-managed fixture targets. No native object or Flandre runtime starts.
internal static class BackgroundPatchLifecycleTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Adapter = typeof(ControlledBackgroundVideoAdapter);
    private static readonly MethodInfo Layer = typeof(BackgroundPatchLifecycleTests).GetMethod(nameof(LayerTarget), Flags)!;
    private static readonly MethodInfo Tick = typeof(BackgroundPatchLifecycleTests).GetMethod(nameof(TickTarget), Flags)!;
    private static readonly MethodInfo SetPrefix = Adapter.GetMethod("BeforeLayerSet", Flags)!;
    private static readonly MethodInfo TickPrefix = Adapter.GetMethod("BeforeTick", Flags)!;

    internal static void Run(Action<string, Action> test)
    {
        test("background release retains its pending native snapshot and shutdown hooks until completion", () => WithFixture((owned, foreign) =>
        {
            RenderControlV1.BeginHostInitialization(); RenderControlV1.CompleteHostInitialization();
            var lease = RenderControlV1.Acquire();
            var snapshotType = Adapter.GetNestedType("NativeSnapshot", BindingFlags.NonPublic)!;
            object sentinel = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(snapshotType);
            var interceptor = new Harmony("aave.test.background-pending-release");
            interceptor.Patch(snapshotType.GetMethod("Restore", BindingFlags.Instance | BindingFlags.NonPublic)!,
                prefix: new HarmonyMethod(typeof(BackgroundPatchLifecycleTests), nameof(ObserveDeferredRestore)));
            DeferredRestoreAttempts = 0; Set("snapshot", sentinel);
            try
            {
                lease.Dispose();
                Check(DeferredRestoreAttempts == 1 && ReferenceEquals(Get("snapshot"), sentinel) && !RenderControlV1.IsRenderingOwned,
                    "AfterRelease discarded native state before asynchronous restoration completed");
                Check(!ControlledBackgroundVideoAdapter.IsReady && Invoke("Shutdown") is false && ReferenceEquals(Get("hooks"), owned),
                    "pending native restoration was advertised ready or detached from its shutdown owner");
            }
            finally
            {
                Set("snapshot", null); interceptor.UnpatchSelf(); lease.Dispose(); RenderControlV1.BeginShutdown();
            }
        }));
        test("background deferred restore never resumes native playback while the outer lease remains owned", () => WithFixture((owned, foreign) =>
        {
            RenderControlV1.BeginHostInitialization(); RenderControlV1.CompleteHostInitialization();
            var lease = RenderControlV1.Acquire();
            var snapshotType = Adapter.GetNestedType("NativeSnapshot", BindingFlags.NonPublic)!;
            object sentinel = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(snapshotType);
            var interceptor = new Harmony("aave.test.background-deferred-restore-entry");
            interceptor.Patch(snapshotType.GetMethod("Restore", BindingFlags.Instance | BindingFlags.NonPublic)!,
                prefix: new HarmonyMethod(typeof(BackgroundPatchLifecycleTests), nameof(ObserveDeferredRestore)));
            DeferredRestoreAttempts = 0; Set("snapshot", sentinel);
            try
            {
                Invoke("RestoreDeferredNativeState");
                Check(DeferredRestoreAttempts == 0 && ReferenceEquals(Get("snapshot"), sentinel),
                    "deferred restore resumed the decoder or discarded its snapshot before the outer lease released");
            }
            finally
            {
                Set("snapshot", null); interceptor.UnpatchSelf(); lease.Dispose(); RenderControlV1.BeginShutdown();
            }
        }));
        test("background blocks a new outer lease while native restoration remains pending", () => WithFixture((owned, foreign) =>
        {
            RenderControlV1.BeginHostInitialization(); RenderControlV1.CompleteHostInitialization();
            var snapshotType = Adapter.GetNestedType("NativeSnapshot", BindingFlags.NonPublic)!;
            object sentinel = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(snapshotType);
            var interceptor = new Harmony("aave.test.background-pending-acquire");
            interceptor.Patch(snapshotType.GetMethod("Restore", BindingFlags.Instance | BindingFlags.NonPublic)!,
                prefix: new HarmonyMethod(typeof(BackgroundPatchLifecycleTests), nameof(ObserveDeferredRestore)));
            Set("snapshot", sentinel);
            IDisposable? unexpected = null;
            try
            {
                bool rejected = false;
                try { unexpected = RenderControlV1.Acquire(); }
                catch (AggregateException error)
                { rejected = error.InnerExceptions.Any(item => item.Message.Contains("background", StringComparison.OrdinalIgnoreCase)); }
                Check(rejected, "a new outer lease started before the native background restoration completed");
            }
            finally
            {
                Set("snapshot", null); interceptor.UnpatchSelf(); unexpected?.Dispose(); RenderControlV1.BeginShutdown();
            }
        }));
        test("background keeps original callbacks quarantined until native restoration completes", () => WithFixture((owned, foreign) =>
        {
            var snapshotType = Adapter.GetNestedType("NativeSnapshot", BindingFlags.NonPublic)!;
            object sentinel = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(snapshotType);
            Set("snapshot", sentinel);
            try
            {
                Check(Invoke("BeforeLayerSet", null, null) is false && Invoke("BeforeTick", new object?[] { null }) is false,
                    "original background callbacks escaped before the retained native snapshot completed restoration");
            }
            finally { Set("snapshot", null); }
        }));
        test("background shutdown does not resume deferred native state while the outer lease remains owned", () => WithFixture((owned, foreign) =>
        {
            RenderControlV1.BeginHostInitialization(); RenderControlV1.CompleteHostInitialization();
            var lease = RenderControlV1.Acquire();
            var snapshotType = Adapter.GetNestedType("NativeSnapshot", BindingFlags.NonPublic)!;
            // Only the existence of retained native state matters to this
            // guard. Intercept Restore so no Unity/native field can execute.
            object sentinel = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(snapshotType);
            var interceptor = new Harmony("aave.test.background-deferred-restore");
            interceptor.Patch(snapshotType.GetMethod("Restore", BindingFlags.Instance | BindingFlags.NonPublic)!,
                prefix: new HarmonyMethod(typeof(BackgroundPatchLifecycleTests), nameof(ObserveDeferredRestore)));
            DeferredRestoreAttempts = 0; Set("snapshot", sentinel);
            try
            {
                Check(Invoke("Shutdown") is false, "shutdown ignored the still-owned native snapshot");
                Check(DeferredRestoreAttempts == 0 && RenderControlV1.IsRenderingOwned && ReferenceEquals(Get("hooks"), owned),
                    "shutdown resumed a quarantined decoder or discarded its ownership while the host lease remained active");
            }
            finally
            {
                Set("snapshot", null); interceptor.UnpatchSelf(); lease.Dispose(); RenderControlV1.BeginShutdown();
            }
            Check(Invoke("Shutdown") is true, "shutdown did not recover after deferred state and lease were restored");
        }));
        test("background rejects preexisting IL manipulator without removing its owner", () => WithFixture((owned, foreign) =>
        {
            foreign.Patch(Layer, ilmanipulator: new HarmonyMethod(typeof(BackgroundPatchLifecycleTests), nameof(Manipulate)));
            ExpectConflict(() => Invoke("EnsurePatch", Layer, null));
            Check(Harmony.GetPatchInfo(Layer)!.Owners.Contains(foreign.Id), "foreign manipulator was removed by validation");
        }, install: false));
        test("background rejects a late IL manipulator on an owned callback", () => WithFixture((owned, foreign) =>
        {
            foreign.Patch(Tick, ilmanipulator: new HarmonyMethod(typeof(BackgroundPatchLifecycleTests), nameof(Manipulate)));
            ExpectConflict(() => Invoke("EnsurePatch", Tick, TickPrefix));
        }));
        test("background rejects the expected prefix installed by a different owner", () => WithFixture((owned, foreign) =>
        {
            foreign.Patch(Layer, prefix: new HarmonyMethod(SetPrefix));
            ExpectConflict(() => Invoke("EnsurePatch", Layer, SetPrefix));
        }, install: false));
        test("background failed unpatch retains hooks and retries remaining targets", () => WithFixture((owned, foreign) =>
        {
            using (InjectUnpatch(Tick, skip: false))
            {
                Check(Invoke("Shutdown") is false, "shutdown did not report pending unpatch failure");
                Check(ReferenceEquals(Get("hooks"), owned), "failed shutdown discarded retry ownership");
                Check(!ControlledBackgroundVideoAdapter.IsReady, "partial shutdown still advertises readiness");
                Check(Harmony.GetPatchInfo(Tick)?.Owners.Contains(owned.Id) == true, "failure injection did not retain second owned target");
            }
            Check(Invoke("Shutdown") is true, "shutdown could not retry after the injected failure cleared");
            Check(Harmony.GetPatchInfo(Layer)?.Owners.Contains(owned.Id) != true
                && Harmony.GetPatchInfo(Tick)?.Owners.Contains(owned.Id) != true, "retry left an owned callback installed");
            Check(Get("hooks") == null, "successful shutdown retained live ownership");
        }));
        test("background verifies actual removal when unpatch silently leaves a prefix", () => WithFixture((owned, foreign) =>
        {
            using (InjectUnpatch(Tick, skip: true))
            {
                Check(Invoke("Shutdown") is false, "shutdown accepted a prefix still installed after unpatch returned");
                Check(ReferenceEquals(Get("hooks"), owned), "silent unpatch retained no retry owner");
            }
            Check(Invoke("Shutdown") is true, "silent removal failure was not retryable");
        }));
        test("background shutdown removes only its own prefix owner", () => WithFixture((owned, foreign) =>
        {
            foreign.Patch(Layer, prefix: new HarmonyMethod(SetPrefix));
            object? result = Invoke("Shutdown");
            Check(Harmony.GetPatchInfo(Layer)?.Owners.Contains(foreign.Id) == true, "shutdown removed another owner's identical prefix");
            Check(result is true, "fully removed owned prefixes did not complete shutdown");
        }));
    }

    private static void WithFixture(Action<Harmony, Harmony> action, bool install = true)
    {
        var owned = new Harmony("aave.test.background-owned");
        var foreign = new Harmony("aave.test.background-foreign");
        Set("restorationFailed", false); Set("inspected", null); Set("hooks", null);
        ControlledBackgroundVideoAdapter.Initialize(owned, _ => { });
        Set("onLayerSet", Layer); Set("tick", Tick);
        Set("inspected", typeof(BackgroundPatchLifecycleTests).Assembly);
        if (install)
        {
            owned.Patch(Layer, prefix: new HarmonyMethod(SetPrefix));
            owned.Patch(Tick, prefix: new HarmonyMethod(TickPrefix));
        }
        try { action(owned, foreign); }
        finally
        {
            // Test-only cleanup is outside the asserted production path.
            UnpatchFailure.Target = null;
            owned.UnpatchSelf(); foreign.UnpatchSelf();
            Invoke("Shutdown");
            Set("restorationFailed", false); Set("hooks", null); Set("inspected", null);
        }
    }

    private static IDisposable InjectUnpatch(MethodInfo target, bool skip)
    {
        var injector = new Harmony("aave.test.background-unpatch-injection");
        var method = typeof(PatchProcessor).GetMethod(nameof(PatchProcessor.Unpatch), new[] { typeof(HarmonyPatchType), typeof(string) })!;
        injector.Patch(method, prefix: new HarmonyMethod(typeof(UnpatchFailure), nameof(UnpatchFailure.Before)));
        // The old implementation uses the MethodInfo overload. Instrument both
        // forms so the red run proves its swallowed failure, not an API mismatch.
        var byMethod = typeof(PatchProcessor).GetMethod(nameof(PatchProcessor.Unpatch), new[] { typeof(MethodInfo) })!;
        injector.Patch(byMethod, prefix: new HarmonyMethod(typeof(UnpatchFailure), nameof(UnpatchFailure.Before)));
        UnpatchFailure.Target = target; UnpatchFailure.Skip = skip;
        return new Cleanup(() => { UnpatchFailure.Target = null; injector.UnpatchSelf(); });
    }
    private sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }
    private static object? Get(string name) => Adapter.GetField(name, Flags)!.GetValue(null);
    private static void Set(string name, object? value) => Adapter.GetField(name, Flags)!.SetValue(null, value);
    private static object? Invoke(string name, params object?[] values)
    {
        try { return Adapter.GetMethod(name, Flags)!.Invoke(null, values); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void ExpectConflict(Action action)
    {
        try { action(); } catch (NotSupportedException) { return; }
        throw new Exception("callback patch conflict was accepted");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void LayerTarget(BackgroundPresenter.Layer layer, BackgroundPresenter.Content content) { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void TickTarget(BackgroundPresenter presenter) { }
    public static void Manipulate(ILContext context) { }
    private static int DeferredRestoreAttempts;
    public static bool ObserveDeferredRestore() { DeferredRestoreAttempts++; return false; }

    private static class UnpatchFailure
    {
        internal static MethodBase? Target;
        internal static bool Skip;
        public static bool Before(PatchProcessor __instance)
        {
            var original = typeof(PatchProcessor).GetField("original", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(__instance);
            if (!Equals(original, Target) || Target == null) return true;
            if (Skip) return false;
            throw new InvalidOperationException("fixture background unpatch failure");
        }
    }
}
