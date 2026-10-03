using HarmonyLib;
using UnityEngine;

namespace AAVideoExport.Plugin;

[HarmonyPatch(typeof(Test), nameof(Test.Start))]
internal static class PlaybackLifecycleHook
{
    static void Prefix(Test __instance) => ExportHost.Current?.MarkNewPlayback(__instance);
}

[HarmonyPatch(typeof(Test), nameof(Test.AdvanceScenario))]
internal static class StartCaptureHook
{
    // Start at the first actual scenario advance, after AA's loading phase.
    // Editor previews pass preview=true and never consume an armed export.
    static void Prefix(Test __instance, bool __0)
    {
        if (!__0) ExportHost.Current?.BeforePlayerStart(__instance);
    }
}

[HarmonyPatch(typeof(UnityEngine.Camera), "FireOnPostRender")]
internal static class BuiltinCaptureHook
{
    static void Postfix(UnityEngine.Camera __0) => ExportHost.Current?.AfterCamera(__0);
}

// Every hook is inert outside an explicitly started export.
[HarmonyPatch(typeof(Test), nameof(Test.End))]
internal static class EndCaptureHook
{
    static bool Prefix(Test __instance) => ExportHost.Current?.BeforePlayerEnd(__instance) ?? true;
}

[HarmonyPatch(typeof(Test), nameof(Test.OnTouchAreaClicked))]
internal static class TouchCaptureHook
{
    static bool Prefix() => ExportHost.Current?.AllowTouch ?? true;
}

[HarmonyPatch(typeof(RealTime), "get_deltaTime")]
internal static class FixedUiDeltaHook
{
    static bool Prefix(ref float __result)
    {
        if (!NativeExportClock.IsActive) return true;
        __result = NativeExportClock.Delta;
        return false;
    }
}

[HarmonyPatch(typeof(RealTime), "get_time")]
internal static class FixedUiTimeHook
{
    static bool Prefix(ref float __result)
    {
        if (!NativeExportClock.IsActive) return true;
        __result = NativeExportClock.Now;
        return false;
    }
}

[HarmonyPatch(typeof(Time), "get_unscaledTime")]
internal static class FixedUnscaledTimeHook
{
    static bool Prefix(ref float __result)
    {
        if (!NativeExportClock.IsActive) return true;
        __result = NativeExportClock.ReadTime();
        return false;
    }
}

[HarmonyPatch(typeof(Time), "get_unscaledDeltaTime")]
internal static class FixedUnscaledDeltaHook
{
    static bool Prefix(ref float __result)
    {
        if (!NativeExportClock.IsActive) return true;
        __result = NativeExportClock.ReadDelta();
        return false;
    }
}

[HarmonyPatch(typeof(WaitForSecondsRealtime), "get_keepWaiting")]
internal static class FixedRealtimeWaitHook
{
    static bool Prefix(WaitForSecondsRealtime __instance, ref bool __result)
    {
        if (!NativeExportClock.TryKeepWaiting(__instance, out var waiting)) return true;
        __result = waiting;
        return false;
    }
}

[HarmonyPatch(typeof(WaitForSecondsRealtime), nameof(WaitForSecondsRealtime.Reset))]
internal static class ResetRealtimeWaitHook
{
    static void Postfix(WaitForSecondsRealtime __instance) => NativeExportClock.Reset(__instance);
}

[HarmonyPatch(typeof(ScenarioResourceManager), nameof(ScenarioResourceManager.LoadGenericScenario))]
internal static class ProjectTitleHook
{
    static void Prefix(string __0)
    {
        if (!string.IsNullOrWhiteSpace(__0)) ExportHost.LoadedStoryTitle = Path.GetFileNameWithoutExtension(__0);
    }
}
