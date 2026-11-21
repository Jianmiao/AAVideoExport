using System.Diagnostics;
using System.Reflection;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Diagnostic-only startup boundary check. It does not certify the Mod's
// cold-start behavior or strict story timing; it verifies the post-start
// boundary that must hold before an export can acquire render ownership.
public sealed class AASkipIntroBoundaryChecks : MonoBehaviour
{
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private IDisposable? lease;
    private bool finished;

    public AASkipIntroBoundaryChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (finished) return;
        try
        {
            if (wall.Elapsed.TotalSeconds > 45)
                throw new TimeoutException("AASkipIntro boundary probe timed out.");
            if (!RenderControlV1.IsReady || RenderControlV1.IsRenderingOwned ||
                SceneManager.GetActiveScene().name != "CatalogScene") return;

            int activeSplash = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                .Count(component => component != null && component.GetType().Name == "CreatorSplash" &&
                    component.gameObject != null && component.gameObject.activeInHierarchy);
            lease = (IDisposable)typeof(RenderControlV1)
                .GetMethod("Acquire", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null)!;
            bool owned = RenderControlV1.IsRenderingOwned;
            lease.Dispose(); lease = null;
            if (activeSplash != 0 || !owned || RenderControlV1.IsRenderingOwned)
                throw new InvalidOperationException($"AASkipIntro post-start boundary differed: active_creator_splash={activeSplash}; owned_during={owned}; owned_after={RenderControlV1.IsRenderingOwned}.");
            Plugin.Report($"PATH-CHECK PASS aaskipintro-boundary active_creator_splash={activeSplash}; ownership_restored=true");
            Finish(true);
        }
        catch (Exception error)
        {
            Plugin.Report("PATH-CHECK FAIL aaskipintro-boundary: " + error);
            Finish(false);
        }
    }

    [HideFromIl2Cpp]
    private void Finish(bool passed)
    {
        if (finished) return;
        finished = true;
        try { lease?.Dispose(); } catch (Exception error) { passed = false; Plugin.Report("PATH-CHECK AASkipIntro cleanup: " + error); }
        lease = null;
        Plugin.Report($"PATH-CHECK RESULT {(passed ? 1 : 0)} passed; {(passed ? 0 : 1)} failed");
    }

    public void OnDestroy() { Finish(false); }
}
