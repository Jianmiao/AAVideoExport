using System.Reflection;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Diagnostic-only boundary probe: no menu/window/audio/configuration mutation.
public sealed class AAMenuPrototypeBoundaryChecks : MonoBehaviour
{
    private const double WaitSeconds = 0.35;
    private readonly System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
    private IDisposable? lease; private Snapshot? before, during, after; private double wait; private int stage; private bool finished;
    public AAMenuPrototypeBoundaryChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (finished) return;
        try
        {
            if (wall.Elapsed.TotalSeconds > 45) throw new TimeoutException("AAMenuPrototype boundary probe timed out.");
            if (stage == 0)
            {
                if (!RenderControlV1.IsReady || RenderControlV1.IsRenderingOwned || SceneManager.GetActiveScene().name != "CatalogScene") return;
                before = Capture("before"); stage = 1;
            }
            if (stage == 1)
            {
                lease = (IDisposable)typeof(RenderControlV1).GetMethod("Acquire", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
                if (!RenderControlV1.IsRenderingOwned) throw new InvalidOperationException("RenderControlV1 ownership was not acquired.");
                during = Capture("during"); wait = 0; stage = 2;
            }
            if (stage == 2)
            {
                wait += Math.Max(0, Time.unscaledDeltaTime); if (wait < WaitSeconds) return;
                (lease ?? throw new InvalidOperationException("AAMenuPrototype lease disappeared before release.")).Dispose(); lease = null;
                if (RenderControlV1.IsRenderingOwned) throw new InvalidOperationException("RenderControlV1 ownership remained active after release.");
                after = Capture("after"); ReportSamples(); Finish(true);
            }
        }
        catch (Exception error) { Plugin.Report("PATH-CHECK FAIL aamenu-boundary: " + Describe(error)); Finish(false); }
    }

    [HideFromIl2Cpp]
    private static Snapshot Capture(string phase)
    {
        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, "AAMenuPrototype", StringComparison.OrdinalIgnoreCase));
        if (assembly == null) throw new InvalidOperationException("AAMenuPrototype assembly is not loaded.");
        string[] typeNames = GetTypeNames(assembly);
        bool pluginType = typeNames.Contains("AAMenuPrototype.Plugin", StringComparer.Ordinal), menuType = typeNames.Contains("AAMenuPrototype.MenuBehaviour", StringComparer.Ordinal), hooksType = typeNames.Contains("AAMenuPrototype.MenuEntryHooks", StringComparer.Ordinal);
        if (!pluginType || !menuType || !hooksType) throw new InvalidOperationException($"AAMenuPrototype required types missing: plugin={pluginType}; menu={menuType}; hooks={hooksType}.");
        int menus = 0, activeMenus = 0, enabledMenus = 0, hooks = 0, activeHooks = 0;
        foreach (MonoBehaviour component in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (component == null) continue; Type type; try { type = component.GetType(); } catch { continue; }
            if (!string.Equals(type.Assembly.GetName().Name, "AAMenuPrototype", StringComparison.OrdinalIgnoreCase)) continue;
            bool active = component.gameObject != null && component.gameObject.activeInHierarchy;
            if (string.Equals(type.FullName, "AAMenuPrototype.MenuBehaviour", StringComparison.Ordinal)) { menus++; if (active) activeMenus++; if (component.enabled) enabledMenus++; }
            else if (string.Equals(type.FullName, "AAMenuPrototype.MenuEntryHooks", StringComparison.Ordinal)) { hooks++; if (active) activeHooks++; }
        }
        AudioSource[] sources = Resources.FindObjectsOfTypeAll<AudioSource>();
        int activeSources = sources.Count(source => source != null && source.gameObject != null && source.gameObject.activeInHierarchy), playingSources = sources.Count(source => source != null && source.isPlaying);
        return new Snapshot(phase, assembly.GetName().Name ?? "<unnamed>", assembly.GetName().Version?.ToString() ?? "<unknown>", assembly.Location, typeNames.Length, pluginType, menuType, hooksType, menus, activeMenus, enabledMenus, hooks, activeHooks, GetThreadCount(), sources.Length, activeSources, playingSources);
    }
    [HideFromIl2Cpp] private static string[] GetTypeNames(Assembly assembly)
    {
        try { return assembly.GetTypes().Select(type => type.FullName ?? type.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray(); }
        catch (ReflectionTypeLoadException error) { return error.Types.Where(type => type != null).Select(type => type!.FullName ?? type.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray(); }
    }
    [HideFromIl2Cpp] private static int GetThreadCount() { try { using var process = System.Diagnostics.Process.GetCurrentProcess(); return process.Threads.Count; } catch { return -1; } }
    [HideFromIl2Cpp] private void ReportSamples()
    {
        if (before == null || during == null || after == null) throw new InvalidOperationException("AAMenuPrototype snapshots were incomplete.");
        if (!before.SameObservableState(during) || !during.SameObservableState(after)) throw new InvalidOperationException("AAMenuPrototype changed observable runtime state during ownership.");
        Plugin.Report("PATH-CHECK PASS aamenu-boundary " + before.Format() + "; " + during.Format() + "; " + after.Format());
        Plugin.Report("PATH-CHECK aamenu-boundary scope=read-only; menu-open=false; window-api=false; audio-command=false; playback-start=false; writes=false; arbitrary-thread-ownership=not-provable");
    }
    [HideFromIl2Cpp] private void Finish(bool passed) { if (finished) return; finished = true; try { lease?.Dispose(); } catch (Exception error) { passed = false; Plugin.Report("PATH-CHECK aamenu cleanup: " + Describe(error)); } lease = null; Plugin.Report($"PATH-CHECK RESULT {(passed ? 1 : 0)} passed; {(passed ? 0 : 1)} failed"); }
    public void OnDestroy() { Finish(false); }
    [HideFromIl2Cpp] private static string Describe(Exception error) { while (error is TargetInvocationException invocation && invocation.InnerException != null) error = invocation.InnerException; return error.GetType().Name + ": " + error.Message.Replace("\r", "\\r").Replace("\n", "\\n"); }

    private sealed record Snapshot(string Phase, string AssemblyName, string AssemblyVersion, string AssemblyLocation, int LoadedTypeCount, bool PluginType, bool MenuType, bool HooksType, int MenuComponentCount, int ActiveMenuComponentCount, int EnabledMenuComponentCount, int HookComponentCount, int ActiveHookComponentCount, int ProcessThreadCount, int AudioSourceCount, int ActiveAudioSourceCount, int PlayingAudioSourceCount)
    {
        public bool SameObservableState(Snapshot other) => AssemblyName == other.AssemblyName && AssemblyVersion == other.AssemblyVersion && LoadedTypeCount == other.LoadedTypeCount && PluginType == other.PluginType && MenuType == other.MenuType && HooksType == other.HooksType && MenuComponentCount == other.MenuComponentCount && ActiveMenuComponentCount == other.ActiveMenuComponentCount && EnabledMenuComponentCount == other.EnabledMenuComponentCount && HookComponentCount == other.HookComponentCount && ActiveHookComponentCount == other.ActiveHookComponentCount && ProcessThreadCount == other.ProcessThreadCount && AudioSourceCount == other.AudioSourceCount && ActiveAudioSourceCount == other.ActiveAudioSourceCount && PlayingAudioSourceCount == other.PlayingAudioSourceCount;
        public string Format() => $"phase={Phase}; assembly={AssemblyName}; version={AssemblyVersion}; assembly_location={AssemblyLocation}; loaded_type_count={LoadedTypeCount}; plugin_type={PluginType}; menu_type={MenuType}; hooks_type={HooksType}; menu_components={MenuComponentCount}; active_menu_components={ActiveMenuComponentCount}; enabled_menu_components={EnabledMenuComponentCount}; hook_components={HookComponentCount}; active_hook_components={ActiveHookComponentCount}; process_threads={ProcessThreadCount}; audio_sources={AudioSourceCount}; active_audio_sources={ActiveAudioSourceCount}; playing_audio_sources={PlayingAudioSourceCount}";
    }
}
