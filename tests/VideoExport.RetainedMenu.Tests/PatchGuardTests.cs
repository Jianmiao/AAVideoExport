using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MonoMod.Cil;
using AAVideoExport.Plugin;

internal static class PatchGuardTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo First = typeof(PatchGuardTests).GetMethod(nameof(Target), Flags)!;
    private static readonly MethodInfo Second = typeof(PatchGuardTests).GetMethod(nameof(Target2), Flags)!;
    private static readonly MethodInfo Before = typeof(PatchGuardTests).GetMethod(nameof(Prefix), Flags)!;
    private static int effects, observations;
    internal static void Run(Action<string, Action> test)
    {
        test("failed later binding rollback preserves earlier observer guards", () => With((owner, foreign, guard) =>
        {
            guard.Add(First, Before); int first = guard.Count; guard.Add(Second, Before);
            Check(guard.RollbackFrom(first), "new binding rollback failed");
            Check(Harmony.GetPatchInfo(First)?.Owners.Contains(owner.Id) == true && Harmony.GetPatchInfo(Second)?.Owners.Contains(owner.Id) != true,
                "rollback removed a previously installed observer or retained the failed new binding");
            guard.Validate();
        }));
        test("retained guard preserves original effect and detects late patch-graph mutation", () => With((owner, foreign, guard) =>
        {
            guard.Add(First, Before); effects = observations = 0; Target(); guard.Validate();
            Check(effects == 1 && observations == 1, "observer replaced the original effect");
            string graph = RetainedPatchGuard.Graph(new[] { First });
            foreign.Patch(First, finalizer: new HarmonyMethod(typeof(PatchGuardTests), nameof(Finalizer)));
            Check(RetainedPatchGuard.Graph(new[] { First }) != graph, "full patch graph omitted finalizer mutation"); Reject(guard.Validate);
        }));
        test("retained guard refuses preexisting and late IL manipulators without removing foreign owner", () => With((owner, foreign, guard) =>
        {
            foreign.Patch(First, ilmanipulator: new HarmonyMethod(typeof(PatchGuardTests), nameof(Manipulate)));
            Reject(() => guard.Add(First, Before)); Check(Harmony.GetPatchInfo(First)!.Owners.Contains(foreign.Id), "foreign IL manipulator was removed");
            foreign.UnpatchSelf(); guard.Add(First, Before);
            foreign.Patch(First, ilmanipulator: new HarmonyMethod(typeof(PatchGuardTests), nameof(Manipulate))); Reject(guard.Validate);
        }));
        test("retained guard rejects identical prefix under the wrong owner", () => With((owner, foreign, guard) =>
        {
            guard.Add(First, Before); owner.Unpatch(First, HarmonyPatchType.Prefix, owner.Id); foreign.Patch(First, prefix: new HarmonyMethod(Before)); Reject(guard.Validate);
        }));
        test("retained guard partial cleanup remains retryable and preserves another owner", () => With((owner, foreign, guard) =>
        {
            guard.Add(First, Before); guard.Add(Second, Before); foreign.Patch(First, prefix: new HarmonyMethod(Before));
            using (Inject(Second)) Check(!guard.RemoveAll(), "partial unpatch failure was accepted");
            Check(Harmony.GetPatchInfo(First)?.Owners.Contains(foreign.Id) == true, "cleanup removed another owner's prefix");
            Check(guard.RemoveAll() && Harmony.GetPatchInfo(Second)?.Owners.Contains(owner.Id) != true, "pending cleanup could not retry");
        }));
    }
    private static void With(Action<Harmony, Harmony, RetainedPatchGuard> action)
    {
        var owner = new Harmony("aave.test.menu-guards"); var foreign = new Harmony("aave.test.menu-foreign"); var guard = new RetainedPatchGuard(owner);
        try { action(owner, foreign, guard); }
        finally { UnpatchFailure.Target = null; owner.UnpatchSelf(); foreign.UnpatchSelf(); }
    }
    private static IDisposable Inject(MethodBase target)
    {
        var injector = new Harmony("aave.test.menu-unpatch-failure");
        injector.Patch(typeof(PatchProcessor).GetMethod(nameof(PatchProcessor.Unpatch), new[] { typeof(HarmonyPatchType), typeof(string) })!,
            prefix: new HarmonyMethod(typeof(UnpatchFailure), nameof(UnpatchFailure.Before)));
        UnpatchFailure.Target = target; return new Cleanup(() => { UnpatchFailure.Target = null; injector.UnpatchSelf(); });
    }
    private sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Target() => effects++;
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Target2() => effects++;
    public static void Prefix() => observations++;
    public static Exception? Finalizer(Exception? __exception) => __exception;
    public static void Manipulate(ILContext context) { }
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Reject(Action action) { try { action(); } catch { return; } throw new Exception("unsafe patch graph was accepted"); }
    private static class UnpatchFailure
    {
        internal static MethodBase? Target;
        public static void Before(PatchProcessor __instance)
        {
            object? original = typeof(PatchProcessor).GetField("original", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(__instance);
            if (Target != null && Equals(Target, original)) throw new InvalidOperationException("fixture unpatch failure");
        }
    }
}
