using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.NativePath.Tests;

// Acceptance-only observation. Production creates no replacement audit object.
internal static class NativeAuditOutputContract
{
    internal static bool IsSinglePass(Assembly core) =>
        core.GetType("AAVideoExport.Core.ExportBuildProfile", false)?.GetProperty("SinglePassEnabled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;

    internal static bool IsDevelopmentAudit(Assembly core)
    {
        var profile = core.GetType("AAVideoExport.Core.ExportBuildProfile", false);
        // Old binaries predate build separation and are never mislabeled as
        // production. They require the explicit development-audit runner.
        return profile == null || (bool)(profile.GetProperty("DevelopmentAuditEnabled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidDataException("Core build mode is unreadable."));
    }
    internal static RuntimeFactoryRecipeReceipt? VerifyRecipeOutput(bool developmentAudit, ExportResult result)
    {
        string expected = result.OutputPath + ".runtime-recipes.json";
        if (!developmentAudit)
        {
            if (result.RuntimeRecipeReceiptPath != null || File.Exists(expected) || Directory.Exists(expected))
                throw new InvalidDataException("Production export produced or claimed an audit recipe sidecar.");
            return null;
        }
        string path = result.RuntimeRecipeReceiptPath ?? throw new InvalidDataException("Development audit export has no actual runtime recipe receipt.");
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime recipe receipt belongs to a different output.");
        return RuntimeFactoryRecipeReceipt.VerifyPublished(path);
    }
    internal static string InputIdentity(string sourceSha256, ExportOptions options) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Scope = "test-owned-source-options-identity", SourceSha256 = sourceSha256,
            Options = options with { Title = "", OutputDirectory = "" } })));
    internal static Dictionary<string, string> SnapshotAuditDirectory(string directory)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["@directory-exists"] = Directory.Exists(directory) ? "1" : "0" };
        if (!Directory.Exists(directory)) return files;
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path); using var stream = File.OpenRead(path); using var hash = SHA256.Create();
            files.Add(Path.GetRelativePath(directory, path), info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + Convert.ToHexString(hash.ComputeHash(stream)));
        }
        return files;
    }
    internal static void RequireAuditDirectoryUnchanged(string directory, Dictionary<string, string> expected)
    {
        var actual = SnapshotAuditDirectory(directory);
        if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out string? value) || value != pair.Value))
            throw new InvalidDataException("Production export created, removed or changed development timeline/baseline audit files.");
    }
}
