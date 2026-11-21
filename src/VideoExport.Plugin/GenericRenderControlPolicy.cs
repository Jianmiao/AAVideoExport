using System.Reflection;
using System.Security.Cryptography;
using AAVideoExport.Core;
using Mono.Cecil;

namespace AAVideoExport.Plugin;

// Generic renderer coordination participation. A Mod is admitted as a render
// participant only when its metadata names the public RenderControlV1 contract.
// A render owner that merely uses Camera/RenderTexture but does not participate
// stays opaque and is rejected by the strict boundary.
internal static class GenericRenderControlPolicy
{
    internal const string Marker = "RenderControlV1.participant.v1";

    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    {
        level = StrictCapabilityLevel.Rejected;
        evidence = Array.Empty<string>();
        string location;
        try { location = assembly.Location ?? ""; }
        catch (NotSupportedException) { return false; }
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location)) return false;
        using var module = ModuleDefinition.ReadModule(location, new ReaderParameters { ReadingMode = ReadingMode.Immediate });
        bool participant = module.GetTypeReferences().Any(type =>
            string.Equals(type.Namespace, "AAVideoExport.Integration", StringComparison.Ordinal)
            && string.Equals(type.Name, "RenderControlV1", StringComparison.Ordinal));
        if (!participant) return false;
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location))).ToLowerInvariant();
        level = StrictCapabilityLevel.Observed;
        evidence = new[] { Marker, "protocol=" + global::AAVideoExport.Integration.RenderControlV1.ProtocolVersion, "sha256=" + sha, "cache-reuse-disabled" };
        return true;
    }
}
