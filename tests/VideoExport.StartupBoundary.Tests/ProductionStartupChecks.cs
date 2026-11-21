using System.Reflection;
using HarmonyLib;

internal static class ProductionStartupChecks
{
    internal static int Run(Harmony controller, Harmony original, MethodInfo awake, MethodInfo postfix)
    {
        var traps = new Harmony("aave.production.startup-audit-traps");
        try
        {
            foreach (string name in new[] { "ValidateIdentity", "ValidateBoundAssembly", "EnsureMonitor", "ValidatePatchState" })
                traps.Patch(Api.Method(name), prefix: new HarmonyMethod(typeof(ProductionStartupChecks).GetMethod(nameof(Audit))!));
            Api.Call("Initialize", controller); original.Patch(awake, postfix: new HarmonyMethod(postfix));
            using (var lease = (IDisposable)Api.Call("AcquireExecutionBoundary")!)
            {
                Api.Call("ValidateActive"); var splash = new CreatorSplash(); splash.Awake();
                if (!splash.NativeAwakeRan || !splash.InputRestored || splash.gameObject.activeSelf) throw new Exception("Original startup effects were suppressed.");
                Api.Call("ValidateActive"); Reject(() => Api.Call("CaptureActiveProof")); Reject(() => Api.Call("AcquireCommitScope"));
            }
            if (Api.Get<bool>("IsActive")) throw new Exception("Functional startup lease was not released.");
            Api.Call("Shutdown");
            Console.WriteLine("PASS production startup acquire/native skip/release without audit graph or identity calls"); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL production startup: " + error.GetBaseException().Message); return 1; }
        finally { try { Api.Call("Shutdown"); } finally { traps.UnpatchSelf(); original.UnpatchSelf(); controller.UnpatchSelf(); } }
    }
    public static void Audit() => throw new InvalidOperationException("STARTUP AUDIT ENTRY REACHED IN PRODUCTION");
    private static void Reject(Action run) { try { run(); } catch (InvalidOperationException) { return; } throw new Exception("Production returned an audit proof."); }
}
