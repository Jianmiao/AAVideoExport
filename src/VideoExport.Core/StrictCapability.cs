namespace AAVideoExport.Core;

public enum StrictCapabilityLevel
{
    Certified,
    Observed,
    Opaque,
    Rejected
}

public sealed record StrictCapability
{
    public StrictCapability(string moduleId, string fingerprint, StrictCapabilityLevel level, IReadOnlyList<string>? evidence)
    {
        if (string.IsNullOrWhiteSpace(moduleId)) throw new ArgumentException("A module id is required.", nameof(moduleId));
        if (fingerprint is null || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("A canonical SHA-256 fingerprint is required.", nameof(fingerprint));
        ModuleId = moduleId; Fingerprint = fingerprint.ToLowerInvariant(); Level = level; Evidence = evidence ?? Array.Empty<string>();
    }

    public string ModuleId { get; }
    public string Fingerprint { get; }
    public StrictCapabilityLevel Level { get; }
    public IReadOnlyList<string> Evidence { get; }
}

public sealed record StrictCapabilitySnapshot(
    string Fingerprint,
    IReadOnlyList<StrictCapability> Modules,
    bool CanReusePreflight)
{
    private static readonly string[] CompleteFrameworkEvidence =
    {
        "FrameworkGeneratedAssemblyScope.cached-bridges.v1", "policy=net607.monomod22731.cached-framework-bridges.v1",
        "evidenceKind=PINNED_CURRENT_FRAMEWORK_BRIDGE_METADATA", "actual-cached-assembly-reference", "bounded-framework-bridges-only",
        "preexistingCoverage=false", "allCodeCoverage=false", "permitsStrictExport=false", "cache-reuse-disabled"
    };
    /// <summary>
    /// Indicates whether the current loaded Mod set has an explicit strict
    /// export boundary. Certified adapters cover their own timing paths. The
    /// Azurite observation is admitted because RenderControlV1 coordinates
    /// renderer ownership without changing the story timeline. All other
    /// observed, opaque, and rejected modules remain fail-closed.
    /// </summary>
    public bool CanAdmitStrictExport => Modules.All(IsStrictlyAdmissible);

    /// <summary>Modules that prevent a strict export from starting.</summary>
    public IReadOnlyList<StrictCapability> BlockingModules =>
        Modules.Where(module => !IsStrictlyAdmissible(module)).ToArray();

    /// <summary>
    /// Fails before story loading or RenderControlV1 acquisition when a loaded
    /// module is not covered by a certified adapter or an explicit, harmless
    /// renderer-only observation boundary.
    /// </summary>
    public void EnsureStrictExportAllowed()
    {
        var blocked = BlockingModules;
        if (blocked.Count == 0) return;

        string details = string.Join("; ", blocked.Select(module =>
        {
            string evidence = module.Evidence.Count == 0
                ? "no evidence"
                : string.Join(", ", module.Evidence.Where(value => !string.IsNullOrWhiteSpace(value)));
            return module.ModuleId + " [" + module.Level + "]: " + evidence;
        }));
        throw new ExportException("strict_capability_unsupported",
            "Strict export requires every loaded Mod to have a certified timing boundary. Blocked modules: " + details);
    }

    private static bool IsStrictlyAdmissible(StrictCapability module)
    {
        if (module.Level == StrictCapabilityLevel.Certified) return true;
        if (module.Level != StrictCapabilityLevel.Observed) return false;
        if (string.Equals(module.ModuleId, "Azurite", StringComparison.OrdinalIgnoreCase)) return true;
        if (module.ModuleId is "ExampleCore" or "ExampleDependent" or "ExampleStandalone")
            return module.Evidence.Contains("StaticInertModEvidence.empty-load.v1", StringComparer.Ordinal)
                && module.Evidence.Contains("StaticInertScope.retained.v1", StringComparer.Ordinal);
        if (IsAdditionalFrameworkDeclaration(module.ModuleId))
            return HasCompleteFrameworkEvidence(module);
        string? required = module.ModuleId switch
        {
            "FlandreScarletBackground" => "ControlledBackgroundVideoAdapter.pts-frames.v1",
            "AAMultilingual" => "MultilingualCaptureAdapter.frame-owned.v1",
            "Rukari.Lib" or "Rukari.Lib.Runtime" => "RuntimeModBoundaries.rukari-idle.v1",
            "AzureArchive.VideoTools.Core" or "AzureArchive.VideoTools.Formats" => "RuntimeModBoundaries.effects-dependency.v1",
            "AASkipIntro" => "AASkipIntroExportBoundary.retained.v1",
            "MenuBgmSelector" => "RetainedMenuPlaybackBoundary.menu-bgm.v1",
            "AAMenuPrototype" => "RetainedMenuPlaybackBoundary.menu-scene.v1",
            "SfxFolderMod" => "RetainedMenuPlaybackBoundary.sfx-map.v1",
            "Rukari.CharacterVoice" => "CharacterVoiceSessionAdapter.held-controller.v1",
            "Anonymously Hosted DynamicMethods Assembly" => "RetainedRecipeScope.interop-initializer-final-il.v2",
            "MonoMod.Utils.Cil.ILGeneratorProxy" or "MonoMod.RuntimeDetour.Runtime.NETCore3+Helpers" => "FrameworkGeneratedAssemblyScope.cached-bridges.v1",
            _ => null
        };
        return required != null && module.Evidence.Contains(required, StringComparer.Ordinal);
    }

    // These names only select the expected evidence contract. Inventory must
    // first obtain it from a live proof of actual pinned producer/cache objects.
    private static bool IsAdditionalFrameworkDeclaration(string name)
    {
        if (name is "Il2CppTrampolineDelegates" or "MonoMod.Utils.GetManagedSizeHelper") return true;
        const string prefix = "HarmonyDTFAssembly";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string suffix = name[prefix.Length..];
        return int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
            && number is > 0 and <= 4096 && suffix == number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool HasCompleteFrameworkEvidence(StrictCapability module) =>
        module.Level == StrictCapabilityLevel.Observed && module.Evidence.Count == CompleteFrameworkEvidence.Length
        && CompleteFrameworkEvidence.All(evidence => module.Evidence.Contains(evidence, StringComparer.Ordinal));

    private static bool IsNormalizedFrameworkObservation(StrictCapability module) =>
        (module.ModuleId is "MonoMod.Utils.Cil.ILGeneratorProxy" or "MonoMod.RuntimeDetour.Runtime.NETCore3+Helpers"
            || IsAdditionalFrameworkDeclaration(module.ModuleId)) && HasCompleteFrameworkEvidence(module);

    public static StrictCapabilitySnapshot Create(IEnumerable<StrictCapability> modules)
    {
        var ordered = (modules ?? throw new ArgumentNullException(nameof(modules)))
            .OrderBy(module => module.ModuleId, StringComparer.Ordinal)
            .ThenBy(module => module.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var identities = new List<string>();
        var frameworkIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in ordered)
        {
            // Modules remains the complete audit list. Only complete current
            // held-framework observations share a stable policy identity: the
            // immutable declaration grammar does not change when its actual
            // pinned producer adds another checked runtime delegate type.
            bool framework = IsNormalizedFrameworkObservation(module);
            var parts = new[] { framework ? "held-framework-policy" : "module", framework ? "" : module.ModuleId,
                module.Fingerprint, module.Level.ToString() }.Concat(module.Evidence.OrderBy(value => value, StringComparer.Ordinal));
            string identity = string.Concat(parts.Select(value => value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value));
            if (!framework || frameworkIdentities.Add(identity)) identities.Add(identity);
        }
        // Sort the normalized identities themselves. Dynamic ordinal names
        // must not move a shared policy record relative to unrelated modules.
        foreach (string identity in identities.OrderBy(value => value, StringComparer.Ordinal))
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(identity.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + identity));
        string fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new StrictCapabilitySnapshot(fingerprint, ordered,
            ordered.Length > 0 && ordered.All(module => module.Level == StrictCapabilityLevel.Certified));
    }
}

public sealed record StrictSandboxCommand
{
    public StrictSandboxCommand(long sequence, long frameIndex, long virtualTimeTicks, string moduleFingerprint,
        string apiFamily, string operation, string targetId, string argumentsSha256, long? rawSampleStart, long? rawSampleFrames)
    {
        if (sequence < 0 || frameIndex < 0 || virtualTimeTicks < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (moduleFingerprint is null || moduleFingerprint.Length != 64 || !moduleFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("A canonical module fingerprint is required.", nameof(moduleFingerprint));
        if (string.IsNullOrWhiteSpace(apiFamily) || string.IsNullOrWhiteSpace(operation) || string.IsNullOrWhiteSpace(targetId))
            throw new ArgumentException("Sandbox command identity fields are required.");
        if (argumentsSha256 is null || argumentsSha256.Length != 64 || !argumentsSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A canonical arguments hash is required.", nameof(argumentsSha256));
        if (rawSampleStart is < 0 || rawSampleFrames is < 0) throw new ArgumentOutOfRangeException(nameof(rawSampleStart));
        Sequence = sequence; FrameIndex = frameIndex; VirtualTimeTicks = virtualTimeTicks;
        ModuleFingerprint = moduleFingerprint.ToLowerInvariant(); ApiFamily = apiFamily; Operation = operation; TargetId = targetId;
        ArgumentsSha256 = argumentsSha256.ToLowerInvariant(); RawSampleStart = rawSampleStart; RawSampleFrames = rawSampleFrames;
    }

    public long Sequence { get; }
    public long FrameIndex { get; }
    public long VirtualTimeTicks { get; }
    public string ModuleFingerprint { get; }
    public string ApiFamily { get; }
    public string Operation { get; }
    public string TargetId { get; }
    public string ArgumentsSha256 { get; }
    public long? RawSampleStart { get; }
    public long? RawSampleFrames { get; }
}

/// <summary>Append-only command trace with exact preparation/replay matching.</summary>
public sealed class StrictSandboxTrace
{
    private readonly List<StrictSandboxCommand> commands = new();
    private readonly IReadOnlyList<StrictSandboxCommand> commandView;
    private int replayIndex;
    private Phase phase = Phase.Preparing;

    public StrictSandboxTrace() => commandView = commands.AsReadOnly();

    public IReadOnlyList<StrictSandboxCommand> Commands => commandView;
    /// <summary>Sequence for the next append or replay expectation; unavailable after completion.</summary>
    public long NextSequence => phase switch
    {
        Phase.Preparing => commands.Count,
        Phase.Replaying => replayIndex,
        _ => throw new ExportException("invalid_state", "A completed sandbox trace has no next command sequence.")
    };
    public string TraceSha256 => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join("\n", commands.Select(Format))))).ToLowerInvariant();

    public void Append(StrictSandboxCommand command)
    {
        Require(Phase.Preparing);
        if (command.Sequence != commands.Count)
            throw new ExportException("sandbox_trace_order", "Sandbox command sequence must be zero-based and contiguous.");
        if (commands.Count > 0)
        {
            var previous = commands[^1];
            if (command.FrameIndex < previous.FrameIndex || command.VirtualTimeTicks < previous.VirtualTimeTicks)
                throw new ExportException("sandbox_trace_order", "Sandbox commands must have contiguous sequence and nondecreasing frame/time order.");
        }
        commands.Add(command);
    }

    public void BeginReplay() { Require(Phase.Preparing); replayIndex = 0; phase = Phase.Replaying; }

    public void Expect(StrictSandboxCommand command)
    {
        Require(Phase.Replaying);
        if (replayIndex >= commands.Count || !commands[replayIndex].Equals(command))
            throw new ExportException("sandbox_replay_divergence", $"Sandbox command differs at sequence {replayIndex}.");
        replayIndex++;
    }

    public void CompleteReplay()
    {
        Require(Phase.Replaying);
        if (replayIndex != commands.Count)
            throw new ExportException("sandbox_replay_divergence", $"Sandbox replay ended at {replayIndex}; expected {commands.Count} commands.");
        phase = Phase.Complete;
    }

    private static string Format(StrictSandboxCommand command) => string.Join("|", command.Sequence, command.FrameIndex,
        command.VirtualTimeTicks, command.ModuleFingerprint, command.ApiFamily, command.Operation, command.TargetId,
        command.ArgumentsSha256, command.RawSampleStart?.ToString() ?? "", command.RawSampleFrames?.ToString() ?? "");

    private void Require(Phase expected)
    {
        if (phase != expected) throw new ExportException("invalid_state", $"Sandbox trace expects {expected}; actual state is {phase}.");
    }

    private enum Phase { Preparing, Replaying, Complete }
}
