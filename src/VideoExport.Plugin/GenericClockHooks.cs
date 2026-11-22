using HarmonyLib;
using UnityEngine;

namespace AAVideoExport.Plugin;

// Generic Unity clock-family coverage. Time.time/delta/unscaled hooks already
// live in CaptureHooks; this adds the missing realtime read. Managed
// DateTime/Environment call sites are rewritten per caller by
// GenericStopwatchBridge. All hooks are active only during capture.
[HarmonyPatch(typeof(Time), "get_realtimeSinceStartup")]
internal static class FixedRealtimeSinceStartupHook
{
    static bool Prefix(ref float __result)
    {
        if (!NativeExportClock.IsActive) return true;
        __result = NativeExportClock.Now;
        return false;
    }
}
