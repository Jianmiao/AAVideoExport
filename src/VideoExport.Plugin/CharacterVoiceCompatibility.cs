using System.Reflection;
using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

// Legacy no-scope entry point. Character Voice requires the actual held native
// session controller plus authored-delay binding in ExportHost; a caller that
// lacks that context must still reject this inspected unsupported-by-itself build.
internal static class CharacterVoiceCompatibility
{
    private static readonly Version UnsupportedVersion = new(1, 6, 8, 0);
    private static readonly Guid UnsupportedModule = new("3c455a03-837f-40d5-a719-281df650be68");

    internal static bool IsLoaded => LoadedAssemblies().Any();

    internal static bool IsExactUnsupportedBuild(Version version, Guid moduleVersionId) =>
        version == UnsupportedVersion && moduleVersionId == UnsupportedModule;

    private static IEnumerable<Assembly> LoadedAssemblies() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(candidate => candidate.GetName().Name == "Rukari.CharacterVoice");

    internal static void EnsureStrictExportAllowed()
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        foreach (var assembly in LoadedAssemblies())
        {
            var version = assembly.GetName().Version ?? new Version(0, 0);
            if (!IsExactUnsupportedBuild(version, assembly.ManifestModule.ModuleVersionId)) continue;
            ThrowIfExactUnsupported(true, version.ToString(),
                "VoicePlaybackRuntime 的独立播放门控尚未接入导出帧时钟。");
        }
    }

    internal static void ThrowIfExactUnsupported(bool exactBuildLoaded, string version, string reason)
    {
        if (!exactBuildLoaded) return;
        string displayVersion = string.IsNullOrWhiteSpace(version) ? "未知版本" : version.Trim();
        string detail = string.IsNullOrWhiteSpace(reason) ? "没有可用的完整时钟适配。" : reason.Trim();
        throw new ExportException(
            "strict_character_voice_clock_unsupported",
            $"严格逐帧导出无法保证人物配音支持 {displayVersion} 的播放门控逐帧对齐。{detail}");
    }
}
