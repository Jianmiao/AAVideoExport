using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ScenarioAnimation;

namespace AAVideoExport.Plugin;

// Preserve AA's original iterator and completion effects; only translate its
// native scaled yield object into a wait on the owned integer frame clock.
[HarmonyPatch]
internal static class NativeScaledWaitHooks
{
    // Retain the concrete native wrapper with its pointer, so a pending
    // authored duration cannot be assigned to a later object at that address.
    private static readonly Dictionary<IntPtr, (WaitAnimation Animation, long Milliseconds)> AuthoredWaits = new();

    internal static void ClearAuthoredWaits() => AuthoredWaits.Clear();

    internal static void RememberAuthoredWaits(string script, Test player, Utils.ScenarioUtil.ScriptInfo parsed)
    {
        var authored = new List<long>();
        foreach (string line in script.Split('\n'))
        {
            string[] fields = line.Split(';');
            if (!string.Equals(fields[0], "#wait", StringComparison.OrdinalIgnoreCase)) continue;
            if (fields.Length < 2 || !long.TryParse(fields[1], out long milliseconds) || milliseconds < 0) return;
            authored.Add(milliseconds);
        }
        if (authored.Count == 0 || parsed.scenarioAnimations == null) return;
        var native = new List<WaitAnimation>();
        foreach (var animation in parsed.scenarioAnimations)
        {
            var wait = animation?.TryCast<WaitAnimation>();
            if (wait == null) continue;
            AuthoredWaits.Remove(wait.Pointer);
            native.Add(wait);
        }
        // Observe the original result, including other Mods' changes. Only
        // bind an unambiguous, ordered match; never rewrite an animation.
        if (authored.Count != native.Count) return;
        var nativeDurations = new Dictionary<float, long>();
        for (int i = 0; i < native.Count; i++)
        {
            float seconds = (float)authored[i] / 1000f;
            if (native[i].owner == null || native[i].owner.Pointer != player.Pointer
                || native[i].delayInSeconds != seconds) return;
            // Once two distinct authored durations collapse to one float,
            // another Mod could reorder them without a detectable mismatch.
            if (nativeDurations.TryGetValue(seconds, out long prior) && prior != authored[i]) return;
            nativeDurations[seconds] = authored[i];
        }
        for (int i = 0; i < native.Count; i++)
            AuthoredWaits[native[i].Pointer] = (native[i], authored[i]);
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var owner in new[] { typeof(WaitAnimation), typeof(Test) })
        foreach (var nested in owner.GetNestedTypes(flags))
        {
            if (!nested.Name.Contains("_WaitTask_d__") && !nested.Name.Contains("_DelayedAdvance_d__")) continue;
            yield return nested.GetMethod("MoveNext", flags | BindingFlags.Instance)
                ?? throw new MissingMethodException(nested.FullName, "MoveNext");
        }
    }

    private static void Postfix(object __instance, bool __result)
    {
        var host = ExportHost.Current;
        if (!__result || host == null || !host.Capturing || !NativeExportClock.IsActive) return;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = __instance.GetType();
        object? owner = type.GetProperty("__4__this", flags)?.GetValue(__instance);
        Test? player = owner is WaitAnimation animation ? animation.owner : owner as Test;
        if (player == null || !host.OwnsCapturePlayer(player)) return;
        var current = type.GetProperty("__2__current", flags);
        var yielded = current?.GetValue(__instance) as Il2CppSystem.Object;
        var scaled = yielded?.TryCast<WaitForSeconds>();
        if (scaled == null) return;
        var converted = new WaitForSecondsRealtime(scaled.m_Seconds);
        // A capture callback may have committed the raw count earlier in this
        // Unity frame. Authored waits share the same published start/deadline
        // as animation and voice; legacy exports retain their original reader.
        Func<long> readFrame = NativeExportClock.IsTimelineActive
            ? () => NativeExportClock.Frame : () => host.CapturedFrame;
        long frame = readFrame();
        if (owner is WaitAnimation authoredAnimation
            && AuthoredWaits.Remove(authoredAnimation.Pointer, out var authored)
            && scaled.m_Seconds == (float)authored.Milliseconds / 1000f)
            NativeExportClock.RegisterFrameWaitMilliseconds(converted, authored.Milliseconds, frame, host.CaptureFps, readFrame);
        else
            NativeExportClock.RegisterFrameWait(converted, scaled.m_Seconds, frame, host.CaptureFps, readFrame);
        current!.SetValue(__instance, converted);
    }
}

[HarmonyPatch(typeof(Utils.ScenarioUtil), nameof(Utils.ScenarioUtil.ParseScript))]
internal static class NativeAuthoredWaitHooks
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(FlatData.IScenarioScriptExcel scenario, Test owner, Utils.ScenarioUtil.ScriptInfo __result)
    {
        var host = ExportHost.Current;
        if (host == null || !host.Capturing || !NativeExportClock.IsActive
            || owner == null || !host.OwnsCapturePlayer(owner) || __result == null) return;
        // AA 101 interface getter dispatch is not a safe route for this
        // boxed table row. Use only the verified concrete Generic row.
        var concrete = scenario?.TryCast<GenericScenarioExcel>();
        if (concrete == null || concrete.GetIl2CppType().FullName != "GenericScenarioExcel") return;
        NativeScaledWaitHooks.RememberAuthoredWaits(concrete.ScriptKr ?? "", owner, __result);
    }
}
