using System.Reflection;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Diagnostic-only MenuBgmSelector boundary probe. It deliberately does not
// open the selector, choose a file, switch BGM, start playback, or write any
// file/configuration. The process/thread sample is observational only: it
// cannot prove ownership of arbitrary threads created by another Mod.
public sealed class MenuBgmSelectorBoundaryChecks : MonoBehaviour
{
    private const double WaitSeconds = 0.35;
    private readonly System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
    private IDisposable? lease;
    private Snapshot? before;
    private Snapshot? during;
    private Snapshot? after;
    private double wait;
    private int stage;
    private bool finished;

    public MenuBgmSelectorBoundaryChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (finished) return;
        try
        {
            if (wall.Elapsed.TotalSeconds > 45)
                throw new TimeoutException("MenuBgmSelector boundary probe timed out.");

            if (stage == 0)
            {
                if (!RenderControlV1.IsReady || RenderControlV1.IsRenderingOwned ||
                    SceneManager.GetActiveScene().name != "CatalogScene") return;
                before = Capture("before");
                stage = 1;
            }

            if (stage == 1)
            {
                lease = (IDisposable)typeof(RenderControlV1)
                    .GetMethod("Acquire", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, null)!;
                if (!RenderControlV1.IsRenderingOwned)
                    throw new InvalidOperationException("RenderControlV1 ownership was not acquired.");
                during = Capture("during");
                wait = 0;
                stage = 2;
            }

            if (stage == 2)
            {
                wait += Math.Max(0, Time.unscaledDeltaTime);
                if (wait < WaitSeconds) return;
                (lease ?? throw new InvalidOperationException("RenderControlV1 lease disappeared before release.")).Dispose();
                lease = null;
                if (RenderControlV1.IsRenderingOwned)
                    throw new InvalidOperationException("RenderControlV1 ownership remained active after release.");
                after = Capture("after");
                ReportSamples();
                Finish(true);
            }
        }
        catch (Exception error)
        {
            Plugin.Report("PATH-CHECK FAIL menubgm-boundary: " + Describe(error));
            Finish(false);
        }
    }

    [HideFromIl2Cpp]
    private static Snapshot Capture(string phase)
    {
        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, "MenuBgmSelector", StringComparison.OrdinalIgnoreCase));
        if (assembly == null)
            throw new InvalidOperationException("MenuBgmSelector assembly is not loaded.");

        string[] loadedTypes = GetTypeNames(assembly);
        AudioSource[] sources = Resources.FindObjectsOfTypeAll<AudioSource>();
        int activeSources = sources.Count(source => source != null && source.gameObject != null && source.gameObject.activeInHierarchy);
        int playingSources = sources.Count(source => source != null && source.isPlaying);
        int threadCount = GetThreadCount();
        return new Snapshot(
            phase,
            assembly.GetName().Name ?? "<unnamed>",
            assembly.GetName().Version?.ToString() ?? "<unknown>",
            assembly.Location,
            loadedTypes.Length,
            string.Join("|", loadedTypes.Take(48)),
            threadCount,
            sources.Length,
            activeSources,
            playingSources);
    }

    [HideFromIl2Cpp]
    private static string[] GetTypeNames(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes().Select(type => type.FullName ?? type.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }
        catch (ReflectionTypeLoadException error)
        {
            return error.Types.Where(type => type != null)
                .Select(type => type!.FullName ?? type.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }
    }

    [HideFromIl2Cpp]
    private static int GetThreadCount()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.Threads.Count;
        }
        catch { return -1; }
    }

    [HideFromIl2Cpp]
    private void ReportSamples()
    {
        if (before == null || during == null || after == null)
            throw new InvalidOperationException("MenuBgmSelector snapshots were incomplete.");
        Plugin.Report("PATH-CHECK PASS menubgm-boundary " + before.Format() + "; " + during.Format() + "; " + after.Format());
        Plugin.Report("PATH-CHECK menubgm-boundary scope=read-only; file-dialog=false; bgm-switch=false; playback-start=false; writes=false; arbitrary-thread-ownership=not-provable");
    }

    [HideFromIl2Cpp]
    private void Finish(bool passed)
    {
        if (finished) return;
        finished = true;
        try { lease?.Dispose(); } catch (Exception error) { passed = false; Plugin.Report("PATH-CHECK menubgm cleanup: " + Describe(error)); }
        lease = null;
        Plugin.Report($"PATH-CHECK RESULT {(passed ? 1 : 0)} passed; {(passed ? 0 : 1)} failed");
    }

    public void OnDestroy() { Finish(false); }

    [HideFromIl2Cpp]
    private static string Describe(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error.GetType().Name + ": " + error.Message.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private sealed record Snapshot(
        string Phase,
        string AssemblyName,
        string AssemblyVersion,
        string AssemblyLocation,
        int LoadedTypeCount,
        string LoadedTypes,
        int ProcessThreadCount,
        int AudioSourceCount,
        int ActiveAudioSourceCount,
        int PlayingAudioSourceCount)
    {
        public string Format() =>
            $"phase={Phase}; assembly={AssemblyName}; version={AssemblyVersion}; " +
            $"assembly_location={AssemblyLocation}; loaded_type_count={LoadedTypeCount}; " +
            $"loaded_types={LoadedTypes}; process_threads={ProcessThreadCount}; " +
            $"audio_sources={AudioSourceCount}; active_audio_sources={ActiveAudioSourceCount}; " +
            $"playing_audio_sources={PlayingAudioSourceCount}";
    }
}
