using System.IO;
using HarmonyLib;

namespace AAVideoExport.Plugin;

/// <summary>
/// A story background cue is the authored, deterministic point at which a
/// background resource becomes current. Record its base name at that cue so
/// the cache's video switch can be scheduled on the export clock instead of
/// racing the presenter's transition animation.
/// </summary>
[HarmonyPatch(typeof(BackgroundPresenter.Layer), "Set")]
internal static class StoryBackgroundCue
{
    private static void Postfix(BackgroundPresenter.Layer __instance, BackgroundPresenter.Content content)
    {
        try
        {
            if (content == null || content.Resource == null) return;
            string path = "";
            string name = "";
            try { path = content.Resource.FullPath ?? ""; } catch { }
            try { name = content.Resource.Name ?? ""; } catch { }
            string baseName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(baseName))
                baseName = Path.GetFileNameWithoutExtension(name);
            bool cached = DeterministicVideoCache.HasBaseName(baseName);
            DeterministicVideoCache.ApplyBaseCue(cached ? baseName : "", NativeExportClock.Frame);
            if (string.Equals(System.Environment.GetEnvironmentVariable("AAVE_TRACE_LAYER"), "1", System.StringComparison.Ordinal))
                Plugin.LogPerformance($"layer-cue export_frame={NativeExportClock.Frame} base={baseName} cached={cached}");
        }
        catch { }
    }
}
