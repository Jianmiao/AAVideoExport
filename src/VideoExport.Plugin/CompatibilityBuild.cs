using System.Reflection;
using System.Security.Cryptography;
using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

internal sealed record CompatibilityBuild(string Name, Version Version, Guid Module, string Sha256)
{
    internal void Validate(Assembly assembly)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled)
        {
            // Binding a specific supported interface needs its name/version,
            // not a provenance audit of the binary or generated module.
            var identity = assembly.GetName();
            if (identity.Name != Name || identity.Version != Version)
                throw new NotSupportedException(Name + " 的接口版本不受当前计时适配支持。");
            return;
        }
        if (assembly.GetName().Name != Name || assembly.GetName().Version != Version
            || assembly.ManifestModule.ModuleVersionId != Module || string.IsNullOrEmpty(assembly.Location)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))), Sha256, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(Name + " 的版本或构建与已检查的安装包不一致。");
    }
}
