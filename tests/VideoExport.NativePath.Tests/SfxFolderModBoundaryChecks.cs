using System.Reflection;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Diagnostic-only SfxFolderMod boundary probe. It intentionally does not open
// SoundExplorer, invoke the Mod's search/filter methods, change any setting, or
// play/stop an AudioSource. The probe only reads the loaded Mod state and Unity
// object counters around a short RenderControlV1 ownership lease.
public sealed class SfxFolderModBoundaryChecks : MonoBehaviour
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

    public SfxFolderModBoundaryChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (finished) return;
        try
        {
            if (wall.Elapsed.TotalSeconds > 45)
                throw new TimeoutException("SfxFolderMod boundary probe timed out.");
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
            Plugin.Report("PATH-CHECK FAIL sfxfolder-boundary: " + Describe(error));
            Finish(false);
        }
    }

    [HideFromIl2Cpp]
    private static Snapshot Capture(string phase)
    {
        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, "SfxFolderMod", StringComparison.OrdinalIgnoreCase));
        if (assembly == null)
            throw new InvalidOperationException("SfxFolderMod assembly is not loaded.");
        Type? data = FindType(assembly, "AzureArchive.SfxFolderMod.SfxData");
        Type? patches = FindType(assembly, "AzureArchive.SfxFolderMod.SfxPatches");
        Type? search = FindType(assembly, "AzureArchive.SfxFolderMod.SfxSearch");
        int itemCount = ReadIntProperty(data, "ItemCount");
        bool dataLoaded = ReadStaticBool(data, "_loaded");
        bool harmonyInstalled = ReadStaticObject(patches, "_h") != null;
        bool explorerBound = ReadStaticObject(search, "Explorer") != null;
        AudioSource[] sources = Resources.FindObjectsOfTypeAll<AudioSource>();
        int activeSources = sources.Count(source => source != null && source.gameObject != null && source.gameObject.activeInHierarchy);
        int playingSources = sources.Count(source => source != null && source.isPlaying);
        int threadCount = GetThreadCount();
        return new Snapshot(phase, assembly.GetName().Name ?? "<unnamed>",
            assembly.GetName().Version?.ToString() ?? "<unknown>", assembly.Location,
            dataLoaded, itemCount, harmonyInstalled, explorerBound, threadCount,
            sources.Length, activeSources, playingSources);
    }

    [HideFromIl2Cpp]
    private static Type? FindType(Assembly assembly, string fullName)
    {
        try { return assembly.GetType(fullName, throwOnError: false, ignoreCase: false); }
        catch { return null; }
    }

    [HideFromIl2Cpp]
    private static object? ReadStaticObject(Type? type, string name)
    {
        if (type == null) return null;
        try { return type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null); }
        catch { return null; }
    }

    [HideFromIl2Cpp]
    private static bool ReadStaticBool(Type? type, string name) => ReadStaticObject(type, name) is bool value && value;

    [HideFromIl2Cpp]
    private static int ReadIntProperty(Type? type, string name)
    {
        if (type == null) return -1;
        try { return (int)(type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ?? -1); }
        catch { return -1; }
    }

    [HideFromIl2Cpp]
    private static int GetThreadCount()
    {
        try { using var process = System.Diagnostics.Process.GetCurrentProcess(); return process.Threads.Count; }
        catch { return -1; }
    }

    [HideFromIl2Cpp]
    private void ReportSamples()
    {
        if (before == null || during == null || after == null)
            throw new InvalidOperationException("SfxFolderMod snapshots were incomplete.");
        if (!before.SameObservableState(during) || !during.SameObservableState(after))
            throw new InvalidOperationException("SfxFolderMod changed observable runtime state during ownership.");
        Plugin.Report("PATH-CHECK PASS sfxfolder-boundary " + before.Format() + "; " + during.Format() + "; " + after.Format());
        Plugin.Report("PATH-CHECK sfxfolder-boundary scope=read-only; sound-explorer-open=false; search-invoked=false; audio-command=false; writes=false; arbitrary-thread-ownership=not-provable");
    }

    [HideFromIl2Cpp]
    private void Finish(bool passed)
    {
        if (finished) return;
        finished = true;
        try { lease?.Dispose(); } catch (Exception error) { passed = false; Plugin.Report("PATH-CHECK sfxfolder cleanup: " + Describe(error)); }
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

    private sealed record Snapshot(string Phase, string AssemblyName, string AssemblyVersion,
        string AssemblyLocation, bool DataLoaded, int ItemCount, bool HarmonyInstalled,
        bool ExplorerBound, int ProcessThreadCount, int AudioSourceCount,
        int ActiveAudioSourceCount, int PlayingAudioSourceCount)
    {
        public bool SameObservableState(Snapshot other) =>
            AssemblyName == other.AssemblyName && AssemblyVersion == other.AssemblyVersion &&
            DataLoaded == other.DataLoaded && ItemCount == other.ItemCount &&
            HarmonyInstalled == other.HarmonyInstalled && ExplorerBound == other.ExplorerBound &&
            ProcessThreadCount == other.ProcessThreadCount &&
            AudioSourceCount == other.AudioSourceCount && ActiveAudioSourceCount == other.ActiveAudioSourceCount &&
            PlayingAudioSourceCount == other.PlayingAudioSourceCount;

        public string Format() => $"phase={Phase}; assembly={AssemblyName}; version={AssemblyVersion}; " +
            $"assembly_location={AssemblyLocation}; data_loaded={DataLoaded}; item_count={ItemCount}; " +
            $"harmony_installed={HarmonyInstalled}; explorer_bound={ExplorerBound}; " +
            $"process_threads={ProcessThreadCount}; audio_sources={AudioSourceCount}; " +
            $"active_audio_sources={ActiveAudioSourceCount}; playing_audio_sources={PlayingAudioSourceCount}";
    }
}
