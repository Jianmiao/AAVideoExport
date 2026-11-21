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
        // These are private implementation dependencies shipped by AAVE. They
        // do not represent independently loaded Mods or story-time behavior.
        "SharpGen", "Vortice", "HarmonyDTFAssembly", "Il2CppTrampolineDelegates",
        "MonoMod.RuntimeDetour", "MonoMod.Utils.Cil"
    };
    private static readonly string[] GeneratedInfrastructurePrefixes =
    {
        "HarmonyDTFAssembly", "Il2CppTrampolineDelegates", "MonoMod.RuntimeDetour", "MonoMod.Utils.Cil"
    };

    internal static StrictCapabilitySnapshot Capture(StaticInertScope? staticInertScope = null, CharacterVoiceSessionAdapter? voiceScope = null,
        RetainedRecipeScope? recipeScope = null, FrameworkGeneratedAssemblyScope? frameworkScope = null)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var names = assemblies.Select(assembly => assembly.GetName().Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool moreEffectsLoaded = names.Contains("Rukari.MoreEffects");
        var modules = new List<StrictCapability>();
        foreach (Assembly assembly in assemblies)
        {
            string name = assembly.GetName().Name ?? "";
            if (!IsModAssembly(assembly, name)) continue;
            modules.Add(CreateCapability(assembly, name, moreEffectsLoaded, staticInertScope, voiceScope, recipeScope, frameworkScope));
        }
        return StrictCapabilitySnapshot.Create(modules);
    }

    private static bool IsModAssembly(Assembly assembly, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        // A dynamic or pathless module cannot hide behind an infrastructure
        // name. Only held proofs for the actual cached bridge/holder objects
        // can later admit these bounded generated modules.
        if (assembly.IsDynamic) return true;
        // Azurite.Core is the shared implementation library of the Azurite
        // plugin, not a second independently authored Mod. Its owner is
        // fingerprinted and admitted separately below.
        if (string.Equals(name, "Azurite.Core", StringComparison.OrdinalIgnoreCase)) return false;
        string location = "";
        try { location = assembly.Location ?? ""; }
        catch (NotSupportedException) { /* dynamic assemblies have no path */ }
        if (location.Length == 0) return true;
        string normalized = location.Replace('/', '\\');
        bool ownAavePath = normalized.IndexOf("\\mods\\AAVideoExport\\", StringComparison.OrdinalIgnoreCase) >= 0;
        if (ownAavePath && (name.StartsWith("AAVideoExport", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("SharpGen", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Vortice", StringComparison.OrdinalIgnoreCase))) return false;
        if (string.Equals(name, "AAVideoExport.NativePath.Tests", StringComparison.OrdinalIgnoreCase)) return false;
        bool inModRoot = normalized.IndexOf("\\BepInEx\\plugins\\", StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.IndexOf("\\mods\\", StringComparison.OrdinalIgnoreCase) >= 0;
        // An explicit plugin attribute or install-root path wins over a broad
        // infrastructure name prefix, so a future Mod cannot hide as
        // UnitySomething/SystemSomething by name alone.
        try
        {
            if (assembly.GetCustomAttributesData().Any(attribute =>
                string.Equals(attribute.AttributeType.FullName, "BepInEx.BepInPlugin", StringComparison.Ordinal))) return true;
        }
        catch (Exception) { /* fall through to the explicit install-root check */ }
        if (GeneratedInfrastructurePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
        if (location.Length == 0)
        {
            // A non-infrastructure dynamic assembly has no stable path or
            // payload hash. Keep it in the inventory as Opaque instead of
            // letting a future Mod bypass strict admission with a
            // Unity/System-looking dynamic name.
            return true;
        }
        if (inModRoot) return true;
        if (InfrastructurePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || string.Equals(name, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) return false;
        return false;
    }

    private static StrictCapability CreateCapability(Assembly assembly, string name, bool moreEffectsLoaded,
        StaticInertScope? staticInertScope, CharacterVoiceSessionAdapter? voiceScope, RetainedRecipeScope? recipeScope,
        FrameworkGeneratedAssemblyScope? frameworkScope)
    {
        bool pathless;
        try { pathless = string.IsNullOrWhiteSpace(assembly.Location); } catch (NotSupportedException) { pathless = true; }
        if (assembly.IsDynamic || pathless)
        {
            if (frameworkScope != null)
            {
                try
                {
                    // This validates the complete pinned template/cache graph
                    // then compares captured Assembly references. Names and
                    // matching normalized metadata never authorize a fake.
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
                return qualified; // Stable policy SHA, never anonymous MVID or scope token.
            return new StrictCapability(name, Fingerprint(assembly), StrictCapabilityLevel.Opaque,
                new[] { "dynamic-or-pathless-assembly-without-held-generated-code-proof" });
        }
        var (level, evidence) = Classify(assembly, name, moreEffectsLoaded, staticInertScope, voiceScope);
        return new StrictCapability(name, Fingerprint(assembly), level, evidence);
    }

    private static (StrictCapabilityLevel Level, IReadOnlyList<string> Evidence) Classify(
        Assembly assembly, string name, bool moreEffectsLoaded, StaticInertScope? staticInertScope, CharacterVoiceSessionAdapter? voiceScope)
    {
        if (StaticInertModEvidence.IsSupportedName(name))
        {
            try
            {
                if (staticInertScope == null) throw new InvalidOperationException("No retained empty-Mod acquisition scope.");
                return (StrictCapabilityLevel.Observed, staticInertScope.CaptureProof(assembly));
            }
            catch (Exception error) { return (StrictCapabilityLevel.Rejected, new[] { "static-inert-retained-proof-unavailable", error.Message }); }
        }
        if (ControlledBackgroundVideoAdapter.TryClassify(assembly, out var videoLevel, out var videoEvidence))
            return (videoLevel, videoEvidence);
        if (MultilingualCaptureAdapter.TryClassify(assembly, out var translationLevel, out var translationEvidence))
            return (translationLevel, translationEvidence);
        if (RuntimeModBoundaries.TryClassify(assembly, out var dependencyLevel, out var dependencyEvidence))
            return (dependencyLevel, dependencyEvidence);
        if (RetainedMenuPlaybackBoundary.TryClassify(assembly, out var menuLevel, out var menuEvidence))
            return (menuLevel, menuEvidence);
        if (string.Equals(name, "Rukari.MoreEffects", StringComparison.OrdinalIgnoreCase))
            return MoreEffectsCompatibility.FullClockCoverage && string.Equals(MoreEffectsCompatibility.Status, "full", StringComparison.OrdinalIgnoreCase)
                ? (StrictCapabilityLevel.Certified, new[] { "MoreEffectsCompatibility.full" })
                : (StrictCapabilityLevel.Rejected, new[] { "MoreEffectsCompatibility." + MoreEffectsCompatibility.Status, MoreEffectsCompatibility.FailureReason });
        if (string.Equals(name, "FlandreScarletPortrait", StringComparison.OrdinalIgnoreCase))
            return FlandrePortraitClock.IsReady && string.Equals(FlandrePortraitClock.Status, "full", StringComparison.OrdinalIgnoreCase)
                ? (StrictCapabilityLevel.Certified, new[] { "FlandrePortraitClock.full" })
                : (StrictCapabilityLevel.Rejected, new[] { "FlandrePortraitClock." + FlandrePortraitClock.Status, FlandrePortraitClock.FailureReason });
        if (string.Equals(name, "FlandreScarletBackground", StringComparison.OrdinalIgnoreCase))
            return (StrictCapabilityLevel.Rejected, new[] { "dynamic-video-player-unadapted", "native-decode-boundary-unproven" });
        if (string.Equals(name, "AAMultilingual", StringComparison.OrdinalIgnoreCase))
            return (StrictCapabilityLevel.Observed, new[] { "RenderControlV1.v1", "AAMultilingual.pause-boundary.native" });
        if (string.Equals(name, "AASkipIntro", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string proof = AASkipIntroExportBoundary.CaptureActiveProof();
                return (StrictCapabilityLevel.Observed, new[] { "AASkipIntroExportBoundary.retained.v1", proof, "cache-reuse-disabled" });
            }
            catch (Exception error) { return (StrictCapabilityLevel.Rejected, new[] { "startup-retained-proof-unavailable", error.Message }); }
        }
        if (string.Equals(name, "Azurite", StringComparison.OrdinalIgnoreCase))
            return (StrictCapabilityLevel.Observed, new[] { "RenderControlV1.v1" });
        if (moreEffectsLoaded && name.StartsWith("AzureArchive.VideoTools.", StringComparison.OrdinalIgnoreCase))
            return MoreEffectsCompatibility.FullClockCoverage && string.Equals(MoreEffectsCompatibility.Status, "full", StringComparison.OrdinalIgnoreCase)
                ? (StrictCapabilityLevel.Certified, new[] { "MoreEffectsCompatibility.full.transitive" })
                : (StrictCapabilityLevel.Opaque, new[] { "MoreEffectsCompatibility.transitive-core-without-certified-owner" });
        if (string.Equals(name, "Rukari.CharacterVoice", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (voiceScope == null) throw new InvalidOperationException("No held native voice controller was attached before the snapshot.");
                voiceScope.ValidateController();
                CharacterVoiceDelayClock.ValidateBinding();
                return (StrictCapabilityLevel.Observed, new[] { CharacterVoiceSessionAdapter.ControllerEvidenceMarker,
                    "exact-version-mvid-sha256", "same-held-controller-through-publication", "cache-reuse-disabled" });
            }
            catch (Exception error) { return (StrictCapabilityLevel.Rejected, new[] { "held-voice-controller-unavailable", error.Message }); }
        }
        // Generic fallback for modules with no observable time, patch,
        // behaviour, render/audio/video, game, or native surface.
        if (GenericStaticModPolicy.TryClassify(assembly, out var passiveEvidence))
            return (StrictCapabilityLevel.Observed, passiveEvidence);
        return (StrictCapabilityLevel.Opaque, new[] { "assembly-not-yet-certified" });
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
                try
                {
                    payloadHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location)));
                }
                catch (IOException) { payloadHash = "unreadable"; }
                catch (UnauthorizedAccessException) { payloadHash = "unreadable"; }
            }
            else if (string.Equals(location, "dynamic", StringComparison.Ordinal))
                payloadHash = "dynamic";
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
