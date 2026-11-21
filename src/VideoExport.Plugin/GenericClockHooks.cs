using HarmonyLib;
using UnityEngine;

namespace AAVideoExport.Plugin;

// Generic Unity clock-family coverage. The patches are active only while a
// capture clock is running, so every authoring Mod that reads the same engine
// clock sees the same virtual timeline without a Mod-specific branch.
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
