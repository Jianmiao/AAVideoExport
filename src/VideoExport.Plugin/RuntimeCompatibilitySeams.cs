using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using HarmonyLib;
using UnityEngine;

namespace AAVideoExport.Plugin;

// 1.0.0 keeps these legacy type names as source-compatible seams only.
// Actual behavior is supplied by the four generic runtime planes. No seam may
// reject an installed Mod because its identity is not one of the old adapters.
// Every remaining lifecycle method emits a plane-scoped observation instead of
// being an empty body; none of them can gate an export.

internal static class RuntimeSeamTrace
{
    internal static void Emit(string name, string action, params (string Key, object? Value)[] values)
    {
        var payload = new Dictionary<string, object?> { ["action"] = action };
        foreach (var pair in values) payload[pair.Key] = pair.Value;
        RuntimeVirtualizationHost.Trace(name, payload);
    }
}

internal static class AASkipIntroExportBoundary
{
    private static IDisposable? _lease;
    internal static void Initialize(Harmony harmony) => RuntimeScenePlane.Probe();
    internal static IDisposable? TryAcquire()
    {
        _lease ??= RuntimeScenePlane.AcquireStartupLease();
        return _lease;
    }
    internal static string CaptureActiveProof() => "RVL.scene.startup.v1";
    internal static void ValidateActive() => RuntimeScenePlane.MarkStartupReached("active");
    internal static void ValidateForCommit() => RuntimeScenePlane.MarkStartupReached("commit");
    internal static IDisposable AcquireCommitScope() => RuntimeScenePlane.AcquireStartupCommitScope();
    internal static void ReleaseAbsentSnapshot() { _lease = null; }
    internal static void Shutdown() { _lease = null; RuntimeScenePlane.Reset(); }
}

internal static class StaticInertModEvidence
{
    internal const string Scope = "GenericStaticModPolicy.static-inert.v1";
    internal sealed record Receipt(string PayloadSha256, string ModuleName)
    {
        internal bool TryValidate(out string reason) { reason = "no generic static-inert receipt"; return false; }
        internal bool TryValidateFrame(out string reason) { reason = "no generic static-inert receipt"; return false; }
    }
    internal static bool IsSupportedName(string name) => false;
    internal static Assembly[] LoadedSupportedAssemblies() => Array.Empty<Assembly>();
    internal static bool TryCreateReceipt(Assembly assembly, out Receipt? receipt, out string reason)
    { receipt = null; reason = "no generic static-inert receipt"; return false; }
    internal static long ImageReadCount => 0;
    internal static void Reset() => RuntimeSeamTrace.Emit("static_inert", "reset");
}

internal static class FlandrePortraitClock
{
    private static bool _active;
    internal static bool IsReady => true;
    internal static string Status => "RVL.clock-plane.v1";
    internal static string FailureReason => "";
    internal static void Initialize(Harmony harmony, Action<string> log) => RuntimeVirtualizationHost.Probe("clock", RuntimePlaneState.Probed, "generic portrait clock");
    internal static void TryInstall() => RuntimeSeamTrace.Emit("clock_portrait", "install");
    internal static void EnsureStrictExportAllowed() => RuntimeSeamTrace.Emit("clock_portrait", "allowed");
    internal static void BeginExport(Func<double> time)
    {
        _active = true;
        RuntimeVirtualizationHost.Trace("clock_portrait", new Dictionary<string, object?>
        {
            ["action"] = "begin",
            ["time"] = time()
        });
    }
    internal static void EndExport()
    {
        if (!_active) return;
        _active = false;
        RuntimeVirtualizationHost.Trace("clock_portrait", new Dictionary<string, object?> { ["action"] = "end" });
    }
    internal static bool Shutdown() => true;
}

internal static class CharacterVoiceDelayClock
{
    internal static void Initialize(Harmony harmony, Action<string> log, Action<Exception> fault) =>
        RuntimeVirtualizationHost.Probe("audio", RuntimePlaneState.Probed, "generic character voice delay clock");
    internal static void TryInstall() => RuntimeSeamTrace.Emit("clock_voice", "install");
    internal static void ValidateBinding() => RuntimeSeamTrace.Emit("clock_voice", "validate_binding");
    internal static void BeginExport(Func<long> frame, int fps)
    {
        RuntimeVirtualizationHost.Trace("clock_voice", new Dictionary<string, object?>
        {
            ["action"] = "begin",
            ["frame"] = frame(),
            ["fps"] = fps
        });
    }
    internal static void EndExport() =>
        RuntimeVirtualizationHost.Trace("clock_voice", new Dictionary<string, object?> { ["action"] = "end" });
    internal static bool Shutdown() => true;
    internal static void Reset() => RuntimeSeamTrace.Emit("clock_voice", "reset");
}

public readonly record struct CharacterVoicePosition(long Frame, long Sample, int StoryFrame);
public sealed record CharacterVoiceReceipt
{
    internal string Marker => "RVL.audio.voice.v1";
}

internal sealed class CharacterVoiceSessionAdapter : IDisposable
{
    internal const string ControllerEvidenceMarker = "GenericCharacterVoiceSession.v1";
    // The final mixer already contains every voice source that the game or a
    // Mod plays. The old adapter is therefore an optional observation seam,
    // not a prerequisite for exporting voice.
    internal static bool IsProviderLoaded => false;
    internal static CharacterVoiceSessionAdapter Attach(Action<Exception> fault, Action<string> log) => new();
    internal static string[] CollectKnownIdentifiers(object story) => Array.Empty<string>();
    internal void ValidateController() => RuntimeSeamTrace.Emit("voice_session", "validate_controller");
    internal void BindStory(string key, string[] identifiers) =>
        RuntimeSeamTrace.Emit("voice_session", "bind_story", ("key", key), ("identifiers", identifiers.Length));
    internal void PreparePass() => RuntimeSeamTrace.Emit("voice_session", "prepare_pass");
    internal bool PumpPreload()
    {
        RuntimeSeamTrace.Emit("voice_session", "pump_preload", ("armed", IsArmed));
        return true;
    }
    internal bool IsArmed => false;
    internal void BeginPass(object player, int fps, int sampleRate, Func<CharacterVoicePosition> position, CharacterVoiceReceipt? receipt)
    {
        CharacterVoicePosition at;
        try { at = position(); }
        catch { at = new CharacterVoicePosition(-1, -1, -1); }
        RuntimeSeamTrace.Emit("voice_session", "begin_pass",
            ("frame", at.Frame), ("sample", at.Sample), ("story_frame", at.StoryFrame),
            ("fps", fps), ("sample_rate", sampleRate), ("receipt", receipt != null));
    }
    internal void ValidateFrame() => RuntimeSeamTrace.Emit("voice_session", "validate_frame");
    internal void CompletePass(CharacterVoicePosition position) =>
        RuntimeSeamTrace.Emit("voice_session", "complete_pass", ("frame", position.Frame), ("sample", position.Sample));
    internal CharacterVoiceReceipt SealPass(CharacterVoicePosition position)
    {
        RuntimeSeamTrace.Emit("voice_session", "seal_pass", ("frame", position.Frame), ("sample", position.Sample));
        return new CharacterVoiceReceipt();
    }
    internal void SealForCommit() => RuntimeSeamTrace.Emit("voice_session", "seal_for_commit");
    internal void EndPass() => RuntimeSeamTrace.Emit("voice_session", "end_pass");
    internal void ValidateForCommit() => RuntimeSeamTrace.Emit("voice_session", "validate_for_commit");
    internal IDisposable AcquireCommitScope() => RuntimeAudioPlane.AcquireCommitScope();
    internal void Reset() => RuntimeSeamTrace.Emit("voice_session", "reset");
    public void Dispose() => RuntimeSeamTrace.Emit("voice_session", "dispose");
}

internal static class CharacterVoiceCompatibility
{
    internal static void EnsureStrictExportAllowed() => RuntimeSeamTrace.Emit("voice_compatibility", "allowed");
}

internal static class MoreEffectsCompatibility
{
    internal static bool FullClockCoverage => true;
    internal static string Status => "RVL.clock-plane.v1";
    internal static string FailureReason => "";
    internal static void Initialize(Harmony harmony, Action<string> log) => RuntimeVirtualizationHost.Probe("clock", RuntimePlaneState.Probed, "generic effects clock");
    internal static void TryInstall() => RuntimeSeamTrace.Emit("effects_clock", "install");
    internal static void EnsureOwnEffectsAllowed() => RuntimeSeamTrace.Emit("effects_clock", "allowed");
    internal static void BeginExport(Func<double> time) =>
        RuntimeVirtualizationHost.Trace("effects_clock", new Dictionary<string, object?> { ["action"] = "begin", ["time"] = time() });
    internal static void EndExport() =>
        RuntimeVirtualizationHost.Trace("effects_clock", new Dictionary<string, object?> { ["action"] = "end" });
    internal static bool Shutdown() => true;
}

internal static class MultilingualCaptureAdapter
{
    internal static void Initialize(Harmony harmony, Action<string> log) => RuntimeScenePlane.Probe();
    internal static void TryInstall() => RuntimeScenePlane.Probe();
    internal static void ValidateActive() => RuntimeSeamTrace.Emit("multilingual", "validate_active");
    internal static string PlaybackSettingsIdentity() => "translation-generic=canvas";
    internal static void BeginExport() =>
        RuntimeVirtualizationHost.Trace("multilingual_begin", new Dictionary<string, object?> { ["action"] = "begin" });
    internal static void Pump(long frame)
    {
        if (frame % 30 == 0)
            RuntimeVirtualizationHost.Trace("multilingual_frame", new Dictionary<string, object?> { ["frame"] = frame });
    }
    internal static void EndExport() =>
        RuntimeVirtualizationHost.Trace("multilingual_end", new Dictionary<string, object?> { ["action"] = "end" });
    internal static bool Shutdown() => true;
    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    {
        level = StrictCapabilityLevel.Observed;
        evidence = new[] { "RVL.scene-plane.v1", "generic-canvas-capture" };
        return true;
    }
}

internal static class RuntimeModBoundaries
{
    private static EffectsSourceInput? _prepared;
    private static bool _active;
    internal static void Initialize(Harmony harmony, Action<string> log, string configRoot) =>
        RuntimeSeamTrace.Emit("effects_source", "initialize", ("config_root", configRoot));
    internal static void TryInstall() => RuntimeSeamTrace.Emit("effects_source", "install");
    internal static void ValidateActive() => RuntimeSeamTrace.Emit("effects_source", "validate_active");
    internal static bool Shutdown() => true;
    internal static void PrepareExportDependencies() => RuntimeSeamTrace.Emit("effects_source", "prepare_dependencies");
    internal static void BeginExport()
    {
        _active = true;
        RuntimeVirtualizationHost.Trace("effects_source", new Dictionary<string, object?> { ["action"] = "begin" });
    }
    internal static void EndExport()
    {
        _active = false;
        RuntimeVirtualizationHost.Trace("effects_source", new Dictionary<string, object?> { ["action"] = "end" });
    }
    internal static string PlaybackSettingsIdentity() => PlaybackSettingsIdentityCore(includeProject: true);
    internal static string PlaybackSettingsWithoutEffectsSource() => PlaybackSettingsIdentityCore(includeProject: false);
    private static string PlaybackSettingsIdentityCore(bool includeProject)
    {
        string state = "effects-source=" + (GenericEffectsSourceExecution.IsRequired() ? "active" : "absent");
        if (includeProject && _prepared != null) state += "|project=" + _prepared.ProjectPath;
        return state;
    }
    internal static bool RequiresEffectsSourcePreparation() => GenericEffectsSourceExecution.IsRequired();
    internal static EffectsSourceInput? PrepareEffectsSourceInput(string story, string hash)
    {
        if (_active || _prepared != null) throw new InvalidOperationException("Effects input acquisition requires an idle unbound export.");
        var input = EffectsSourceInput.AcquireExecution(new GenericEffectsSourceExecution(), story);
        _prepared = input;
        return input;
    }
    internal static void RevalidateEffectsSourceInput(EffectsSourceInput input)
    {
        if (!ReferenceEquals(input, _prepared)) throw new InvalidOperationException("The held effects source input was replaced.");
        input.RevalidateForPass();
    }
    internal static void ReleaseEffectsSourceInput(EffectsSourceInput? input)
    {
        if (input == null) return;
        input.Dispose();
        if (ReferenceEquals(input, _prepared)) _prepared = null;
    }
    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    {
        level = StrictCapabilityLevel.Observed;
        evidence = new[] { "RVL.effects-source.v1", "generic-type-contract" };
        return true;
    }
}

internal sealed class NativeBgmSessionAdapter : IDisposable
{
    private static NativeBgmSessionAdapter? _current;
    internal static NativeBgmSessionAdapter Prepare(object story, Action<Exception> fault, Action<string> log, Action<Exception> cleanupFault)
    {
        _current = new NativeBgmSessionAdapter();
        RuntimeSeamTrace.Emit("bgm", "prepare");
        return _current;
    }
    internal bool PumpPreload()
    {
        RuntimeSeamTrace.Emit("bgm", "pump_preload",
            ("sources", RuntimeAudioPlane.SourceCount), ("playing", RuntimeAudioPlane.PlayingCount));
        return true;
    }
    internal void BeginPass(object player) =>
        RuntimeVirtualizationHost.Trace("bgm", new Dictionary<string, object?> { ["action"] = "begin" });
    internal void EndPass() =>
        RuntimeVirtualizationHost.Trace("bgm", new Dictionary<string, object?> { ["action"] = "end" });
    internal bool CleanupPending => false;
    internal Exception? CleanupFailure => null;
    public void Dispose() => RuntimeSeamTrace.Emit("bgm", "dispose");
    internal static void Reset()
    {
        _current = null;
        RuntimeSeamTrace.Emit("bgm", "reset");
    }
}

internal static class RetainedMenuPlaybackBoundary
{
    private static IDisposable? _lease;
    internal static void Initialize(Harmony harmony, Action<string> log, Action<Exception> fault, string configRoot) => RuntimeScenePlane.Probe();
    internal static void TryInstall() => RuntimeScenePlane.Probe();
    internal static void ValidateActive() => RuntimeSeamTrace.Emit("menu_boundary", "validate_active");
    internal static void ValidateForCommit() => RuntimeSeamTrace.Emit("menu_boundary", "validate_for_commit");
    internal static bool TryAcquireRetained(out IDisposable? lease)
    {
        _lease ??= RuntimeScenePlane.AcquireMenuLease();
        lease = _lease;
        return true;
    }
    internal static void BeginPlayback() => RuntimeScenePlane.MenuPlayback("begin-playback");
    internal static void EndPlayback() => RuntimeScenePlane.MenuPlayback("end-playback");
    internal static IDisposable AcquireCommitScope() => RuntimeScenePlane.AcquireMenuCommitScope();
    internal static string PlaybackSettingsIdentity() => "menu-generic=retained";
    internal static bool Shutdown() { _lease = null; return true; }
    internal static void Reset() { _lease = null; }
    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    {
        level = StrictCapabilityLevel.Observed;
        evidence = new[] { "RVL.scene-plane.v1", "generic-menu-boundary" };
        return true;
    }
}
