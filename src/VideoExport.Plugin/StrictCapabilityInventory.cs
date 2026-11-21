using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

/// <summary>Builds a main-thread snapshot of loaded Mod capability evidence.</summary>
internal static class StrictCapabilityInventory
{
    private static int _assemblyGeneration;

    static StrictCapabilityInventory()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, _) =>
            System.Threading.Interlocked.Increment(ref _assemblyGeneration);
    }

    internal static int AssemblyGeneration => System.Threading.Volatile.Read(ref _assemblyGeneration);

    private static readonly string[] InfrastructurePrefixes =
    {
        "System", "Microsoft", "netstandard", "mscorlib", "UnityEngine", "Unity", "Il2Cpp", "BepInEx",
        "0Harmony", "Harmony", "FlatData", "AAVideoExport", "Mono", "mscorlib",
        "SharpGen", "Vortice", "HarmonyDTFAssembly", "Il2CppTrampolineDelegates",
        "MonoMod.RuntimeDetour", "MonoMod.Utils.Cil"
    };

    internal static StrictCapabilitySnapshot Capture(StaticInertScope? staticInertScope = null, CharacterVoiceSessionAdapter? voiceScope = null,
        RetainedRecipeScope? recipeScope = null, FrameworkGeneratedAssemblyScope? frameworkScope = null)
    {
        var modules = new List<StrictCapability>();
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = assembly.GetName().Name ?? "";
            if (!IsModAssembly(assembly, name)) continue;
            modules.Add(CreateCapability(assembly, name, staticInertScope, voiceScope, recipeScope, frameworkScope));
        }
        return StrictCapabilitySnapshot.Create(modules);
    }

    internal static bool IsModAssembly(Assembly assembly, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        // The exporter's own test/support assemblies are not third-party Mods
        // even when BepInEx loads them from a plugins folder.
        if (name.StartsWith("AAVideoExport", StringComparison.OrdinalIgnoreCase)) return false;
        if (assembly.IsDynamic) return true;
        string location = "";
        try { location = assembly.Location ?? ""; }
        catch (NotSupportedException) { }
        if (location.Length == 0) return true;
        string normalized = location.Replace('/', '\\');
        bool ownAavePath = normalized.IndexOf("\\mods\\AAVideoExport\\", StringComparison.OrdinalIgnoreCase) >= 0;
        if (ownAavePath) return false;
        bool inModRoot = normalized.IndexOf("\\BepInEx\\plugins\\", StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.IndexOf("\\mods\\", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!inModRoot) return false;
        // A managed file placed in a Mod root is a Mod only when it carries the
        // BepInEx plugin contract. Dependency libraries (for example a renderer
        // helper shipped beside a plugin) are payload, not independent behavior.
        try
        {
            if (assembly.GetCustomAttributesData().Any(attribute =>
                string.Equals(attribute.AttributeType.FullName, "BepInEx.BepInPlugin", StringComparison.Ordinal))) return true;
            // BepInEx permits the plugin contract on the plugin type rather
            // than the assembly. Treat either placement as a Mod; otherwise a
            // live clock plugin can hide from admission entirely.
            if (assembly.GetTypes().Any(type => type.GetCustomAttributesData().Any(attribute =>
                string.Equals(attribute.AttributeType.FullName, "BepInEx.BepInPlugin", StringComparison.Ordinal)))) return true;
        }
        catch (Exception) { }
        return false;
    }

    private static StrictCapability CreateCapability(Assembly assembly, string name, StaticInertScope? staticInertScope,
        CharacterVoiceSessionAdapter? voiceScope, RetainedRecipeScope? recipeScope, FrameworkGeneratedAssemblyScope? frameworkScope)
    {
        bool pathless;
        try { pathless = string.IsNullOrWhiteSpace(assembly.Location); } catch (NotSupportedException) { pathless = true; }
        if (assembly.IsDynamic || pathless)
        {
            if (frameworkScope != null)
            {
                try
                {
                    if (frameworkScope.AuthorizesAssembly(assembly))
                        return new StrictCapability(name, frameworkScope.IdentitySha256, StrictCapabilityLevel.Observed,
                            new[] { FrameworkGeneratedAssemblyScope.Marker, "policy=" + FrameworkGeneratedAssemblyScope.PolicyId,
                                "evidenceKind=" + FrameworkGeneratedAssemblyScope.EvidenceKind, "actual-cached-assembly-reference",
                                "bounded-framework-bridges-only", "preexistingCoverage=false", "allCodeCoverage=false",
                                "permitsStrictExport=false", "cache-reuse-disabled" });
                }
                catch (Exception error)
                {
                    return new StrictCapability(name, Fingerprint(assembly), StrictCapabilityLevel.Rejected,
                        new[] { "held-framework-bridge-proof-invalid", error.Message });
                }
            }
            if (recipeScope != null && recipeScope.TryClassifyHolder(assembly, out var qualified) && qualified != null)
                return qualified;
            return new StrictCapability(name, Fingerprint(assembly), StrictCapabilityLevel.Opaque,
                new[] { "dynamic-or-pathless-assembly-without-held-generated-code-proof" });
        }
        var (level, evidence) = Classify(assembly, staticInertScope, voiceScope);
        return new StrictCapability(name, Fingerprint(assembly), level, evidence);
    }

    private static (StrictCapabilityLevel Level, IReadOnlyList<string> Evidence) Classify(
        Assembly assembly, StaticInertScope? staticInertScope, CharacterVoiceSessionAdapter? voiceScope)
    {
        // Behavior-derived, in order of observable surface:
        // 1. managed clock call sites covered by the generic call-point bridge;
        // 2. an explicit RenderControlV1 participant;
        // 3. a static inert module with no behavior surface;
        // 4. otherwise opaque and blocked.
        if (GenericClockPolicy.TryClassify(assembly, out var clockLevel, out var clockEvidence))
            return (clockLevel, clockEvidence);
        if (GenericRenderControlPolicy.TryClassify(assembly, out var renderLevel, out var renderEvidence))
            return (renderLevel, renderEvidence);
        if (GenericStaticModPolicy.TryClassify(assembly, out var passiveEvidence))
            return (StrictCapabilityLevel.Observed, passiveEvidence);
        return (StrictCapabilityLevel.Opaque, new[] { "assembly-has-no-generic-timing-or-render-boundary" });
    }

    private static string Fingerprint(Assembly assembly)
    {
        try
        {
            string module;
            try { module = assembly.ManifestModule.ModuleVersionId.ToString(); }
            catch (NotSupportedException) { module = "dynamic"; }
            string location;
            try { location = assembly.Location ?? ""; }
            catch (NotSupportedException) { location = "dynamic"; }
            string payloadHash = "unavailable";
            if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
            {
                try { payloadHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location))); }
                catch (IOException) { payloadHash = "unreadable"; }
                catch (UnauthorizedAccessException) { payloadHash = "unreadable"; }
            }
            else if (string.Equals(location, "dynamic", StringComparison.Ordinal)) payloadHash = "dynamic";
            string value = (assembly.GetName().Name ?? "") + "|" + (assembly.GetName().Version?.ToString() ?? "") + "|" +
                module + "|" + location + "|" + payloadHash;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        }
        catch (NotSupportedException)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dynamic-assembly"))).ToLowerInvariant();
        }
    }
}
