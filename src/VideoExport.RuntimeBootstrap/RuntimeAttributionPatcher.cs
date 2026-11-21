using BepInEx.Preloader.Core.Patching;

namespace AAVideoExport.RuntimeBootstrap;

// The installed host's BasePatcher.Initialize is invoked by AssemblyPatcher.PatchAndLoad
// before the IL2CPP chainloader initializes ordinary plugins. No assembly rewrite is needed.
[PatcherPluginInfo("com.aavideoexport.runtime-attribution", "AAVE Runtime Attribution", "0.1.0")]
public sealed class RuntimeAttributionPatcher : BasePatcher
{
    public override void Initialize()
    {
#if AAVE_DEVELOPMENT_AUDIT
        StartupConfigSnapshot.Capture(BepInEx.Paths.GameRootPath);
        StartupTrace.Write("Patcher.Initialize.enter");
        // An optional diagnostic observer must never prevent the host from starting.
        try
        {
            StartupTrace.Write("Patcher.v1.before");
            RuntimeAttributionV1.InitializeFromEnvironment();
            StartupTrace.Write("Patcher.v1.after");
            StartupTrace.Write("Patcher.v2.before");
            RuntimeRecipeV2.InitializeFromEnvironment();
            StartupTrace.Write("Patcher.v2.after");
            if (RuntimeAttributionV1.WasRequested)
                Log.LogInfo(RuntimeAttributionV1.IsInstalled
                    ? "Opt-in DynamicMethod observation installed; stack evidence does not authorize strict export."
                    : "Opt-in DynamicMethod observation unavailable; see RuntimeAttributionV1 diagnostics.");
        }
        catch (Exception ex)
        {
            StartupTrace.Write("Patcher.Initialize.error", ex.GetType().FullName ?? "Exception");
            try { Log.LogWarning("Optional runtime observation failed: " + ex.GetType().Name); }
            catch { /* Logging must not turn an optional observer into a startup failure. */ }
        }
        StartupTrace.Write("Patcher.Initialize.complete");
#endif
    }

    // BasePatcher.Finalizer is an end-of-preloader callback, not process shutdown.
    // Hooks intentionally remain installed for DynamicMethods created by later plugins.
}
