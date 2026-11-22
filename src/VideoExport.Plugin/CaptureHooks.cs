using HarmonyLib;
using FlatData;
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
    static bool Prefix(Test __instance, bool __0, IScenarioScriptExcel __1)
    {
        if (__0) return true;
        var host = ExportHost.Current;
        if (host == null) return true;
        if (!host.BeforePlayerStart(__instance)) return false;
        bool allowed = host.AllowStrictScenarioAdvance(__instance, __1);
        if (allowed && AAVideoExport.Core.ExportBuildProfile.DevelopmentAuditEnabled && Plugin.StrictSandboxStoryCommandTracingEnabled)
            return host.RecordStrictStoryCommand(__instance, "AdvanceScenario");
        return allowed;
    }
}

[HarmonyPatch(typeof(UnityEngine.Camera), "FireOnPostRender")]
internal static class BuiltinCaptureHook
{
    static void Postfix(UnityEngine.Camera __0) => ExportHost.Current?.AfterCamera(__0);
}

[HarmonyPatch(typeof(UnityEngine.Camera), "FireOnPreCull")]
internal static class BuiltinControlledModPump
{
    static void Postfix() => ExportHost.Current?.BeforeFrameRender();
}

// Every hook is inert outside an explicitly started export.
[HarmonyPatch(typeof(Test), nameof(Test.End))]
internal static class EndCaptureHook
{
    static bool Prefix(Test __instance)
    {
        var host = ExportHost.Current;
        if (host == null) return true;
        if (AAVideoExport.Core.ExportBuildProfile.DevelopmentAuditEnabled && Plugin.StrictSandboxStoryCommandTracingEnabled
            && !host.RecordStrictStoryCommand(__instance, "End", once: true)) return false;
        return host.BeforePlayerEnd(__instance);
    }
}

[HarmonyPatch(typeof(Test), nameof(Test.OnTouchAreaClicked))]
internal static class TouchCaptureHook
{
    static bool Prefix() => ExportHost.Current?.AllowTouch ?? true;
}

// These callbacks draw AA's click particles and drag trail independently of
// Test.OnTouchAreaClicked. Reject only visual feedback while exporting; the
// export panel can still receive clicks (including Cancel).
[HarmonyPatch(typeof(TouchEffectCreator), nameof(TouchEffectCreator.OnPressed))]
internal static class PointerPressCaptureHook
{
    static bool Prefix() => !(ExportHost.Current?.Capturing ?? false);
}

[HarmonyPatch(typeof(TouchEffectCreator), nameof(TouchEffectCreator.OnDragged))]
internal static class PointerDragCaptureHook
{
    static bool Prefix() => !(ExportHost.Current?.Capturing ?? false);
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

[HarmonyPatch(typeof(Time), "get_time")]
internal static class FixedScaledTimeHook
{
    static bool Prefix(ref float __result)
    {
        if (!NativeExportClock.IsActive) return true;
        __result = NativeExportClock.Now;
        return false;
    }
}

[HarmonyPatch(typeof(Time), "get_deltaTime")]
internal static class FixedScaledDeltaHook
{
    static bool Prefix(ref float __result)
    {
        if (!NativeExportClock.IsActive) return true;
        __result = NativeExportClock.Delta;
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
