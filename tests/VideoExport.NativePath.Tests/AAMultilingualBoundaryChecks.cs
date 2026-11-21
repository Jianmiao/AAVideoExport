using System.Diagnostics;
using System.Reflection;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Diagnostic boundary probe only. It proves that the installed
// AAMultilingual build observes the AAVE render lease and pauses its own UI
// refresh path while ownership is active. It does not certify story timing.
public sealed class AAMultilingualBoundaryChecks : MonoBehaviour
{
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private IDisposable? lease;
    private int stage;
    private bool finished;

    public AAMultilingualBoundaryChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (finished) return;
        try
        {
            if (wall.Elapsed.TotalSeconds > 45)
                throw new TimeoutException("AAMultilingual boundary probe timed out.");
            if (stage == 0)
            {
                if (!RenderControlV1.IsReady || RenderControlV1.IsRenderingOwned ||
                    SceneManager.GetActiveScene().name != "CatalogScene") return;
                stage = 1;
            }
            if (stage == 1)
            {
                var readPaused = BindPausedReader();
                bool before = readPaused();
                lease = (IDisposable)typeof(RenderControlV1)
                    .GetMethod("Acquire", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, null)!;
                bool during = readPaused();
                lease.Dispose(); lease = null;
                bool after = readPaused();
                if (before || !during || after || RenderControlV1.IsRenderingOwned)
                    throw new InvalidOperationException($"AAMultilingual pause boundary differed: before={before}; during={during}; after={after}; owned={RenderControlV1.IsRenderingOwned}.");
                Plugin.Report("PATH-CHECK PASS aamultilingual-boundary before=false; during=true; after=false; ownership-restored=true");
                Finish(true);
            }
        }
        catch (Exception error)
        {
            Plugin.Report("PATH-CHECK FAIL aamultilingual-boundary: " + error);
            Finish(false);
        }
    }

    [HideFromIl2Cpp]
    private static Func<bool> BindPausedReader()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .SingleOrDefault(candidate => candidate.GetName().Name == "AAMultilingual")
            ?? throw new InvalidOperationException("AAMultilingual assembly is not loaded.");
        var plugin = assembly.GetType("AAMultilingual.Plugin", throwOnError: true)!;
        var current = plugin.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null) ?? throw new InvalidOperationException("AAMultilingual.Plugin.Current is unavailable.");
        var property = plugin.GetProperty("IsTranslationPaused", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException("AAMultilingual.Plugin", "IsTranslationPaused");
        if (property.PropertyType != typeof(bool) || property.GetMethod == null)
            throw new InvalidOperationException("AAMultilingual pause property has an unexpected signature.");
        return (Func<bool>)property.GetMethod.CreateDelegate(typeof(Func<bool>), current);
    }

    [HideFromIl2Cpp]
    private void Finish(bool passed)
    {
        if (finished) return;
        finished = true;
        try { lease?.Dispose(); } catch (Exception error) { passed = false; Plugin.Report("PATH-CHECK boundary cleanup: " + error); }
        lease = null;
        Plugin.Report($"PATH-CHECK RESULT {(passed ? 1 : 0)} passed; {(passed ? 0 : 1)} failed");
    }

    public void OnDestroy() { Finish(false); }
}
