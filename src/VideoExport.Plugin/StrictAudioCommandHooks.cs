#if AAVE_DEVELOPMENT_AUDIT
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace AAVideoExport.Plugin;

/// <summary>
/// Records mutating AudioSource commands into the strict sandbox trace. The
/// original Unity call still runs; virtualization/replay is a later adapter
/// stage. Reads are intentionally excluded because polling is not an authored
/// audio event and would make the trace depend on callback frequency.
/// </summary>
internal static class StrictAudioCommandHooks
{
    internal static void Record(AudioSource source, string operation, params string[] arguments)
    {
        var host = ExportHost.Current;
        if (!Plugin.StrictSandboxAudioCommandTracingEnabled || host == null || !host.Capturing || source == null) return;
        string target = source.Pointer.ToString();
        string values = operation + "|" + string.Join("|", arguments);
        host.RecordStrictSandboxCommand("AudioSource", operation, target, Hash(values));
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    internal static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    internal static string Clip(AudioClip? clip) => clip == null ? "null" : clip.Pointer.ToString();
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.Play), new Type[] { })]
internal static class StrictAudioPlayHook
{
    private static void Prefix(AudioSource __instance) => StrictAudioCommandHooks.Record(__instance, "Play");
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), new[] { typeof(AudioClip) })]
internal static class StrictAudioPlayOneShotHook
{
    private static void Prefix(AudioSource __instance, AudioClip clip) => StrictAudioCommandHooks.Record(__instance, "PlayOneShot", StrictAudioCommandHooks.Clip(clip));
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayDelayed))]
internal static class StrictAudioPlayDelayedHook
{
    private static void Prefix(AudioSource __instance, float delay) => StrictAudioCommandHooks.Record(__instance, "PlayDelayed", StrictAudioCommandHooks.Number(delay));
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayScheduled))]
internal static class StrictAudioPlayScheduledHook
{
    private static void Prefix(AudioSource __instance, double time) => StrictAudioCommandHooks.Record(__instance, "PlayScheduled", StrictAudioCommandHooks.Number(time));
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.SetScheduledStartTime))]
internal static class StrictAudioSetScheduledStartHook
{
    private static void Prefix(AudioSource __instance, double time) => StrictAudioCommandHooks.Record(__instance, "SetScheduledStartTime", StrictAudioCommandHooks.Number(time));
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.SetScheduledEndTime))]
internal static class StrictAudioSetScheduledEndHook
{
    private static void Prefix(AudioSource __instance, double time) => StrictAudioCommandHooks.Record(__instance, "SetScheduledEndTime", StrictAudioCommandHooks.Number(time));
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.Stop), new Type[] { })]
internal static class StrictAudioStopHook
{
    private static void Prefix(AudioSource __instance) => StrictAudioCommandHooks.Record(__instance, "Stop");
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.Pause), new Type[] { })]
internal static class StrictAudioPauseHook
{
    private static void Prefix(AudioSource __instance) => StrictAudioCommandHooks.Record(__instance, "Pause");
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.UnPause), new Type[] { })]
internal static class StrictAudioUnPauseHook
{
    private static void Prefix(AudioSource __instance) => StrictAudioCommandHooks.Record(__instance, "UnPause");
}

[HarmonyPatch(typeof(AudioSource), "set_timeSamples")]
internal static class StrictAudioTimeSamplesHook
{
    private static void Prefix(AudioSource __instance, int value) => StrictAudioCommandHooks.Record(__instance, "SetTimeSamples", value.ToString(CultureInfo.InvariantCulture));
}

[HarmonyPatch(typeof(AudioSource), "set_volume")]
internal static class StrictAudioVolumeHook
{
    private static void Prefix(AudioSource __instance, float value) => StrictAudioCommandHooks.Record(__instance, "SetVolume", StrictAudioCommandHooks.Number(value));
}

[HarmonyPatch(typeof(AudioSource), "set_pitch")]
internal static class StrictAudioPitchHook
{
    private static void Prefix(AudioSource __instance, float value) => StrictAudioCommandHooks.Record(__instance, "SetPitch", StrictAudioCommandHooks.Number(value));
}
#endif
