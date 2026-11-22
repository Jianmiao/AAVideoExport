using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using HarmonyLib;
using UnityEngine;

namespace AAVideoExport.Plugin;

// 0.3.0 removed the shipped per-Mod adapters. These names remain as host seams,
// but their behavior is now supplied by the four generic runtime planes. No
// seam is allowed to reject an installed Mod merely because its identity is not
// one of the old certified adapters.
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
    internal static IDisposable AcquireCommitScope() => new EmptyScope();
    internal static void ReleaseAbsentSnapshot() { _lease = null; }
    internal static void Shutdown() { _lease = null; RuntimeScenePlane.Reset(); }
    private sealed class EmptyScope : IDisposable { public void Dispose() { } }
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
    internal static void Reset() { }
}

internal static class FlandrePortraitClock
{
    private static bool _active;
    internal static bool IsReady => true;
    internal static string Status => "RVL.clock-plane.v1";
    internal static string FailureReason => "";
    internal static void Initialize(Harmony harmony, Action<string> log) => RuntimeVirtualizationHost.Probe("clock", RuntimePlaneState.Probed, "generic portrait clock");
    internal static void TryInstall() { }
    internal static void EnsureStrictExportAllowed() { }
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
    internal static void Initialize(Harmony harmony, Action<string> log, Action<Exception> fault) { }
    internal static void TryInstall() { }
    internal static void ValidateBinding() { }
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
    internal static void Reset() { }
}

public readonly record struct CharacterVoicePosition(long Frame, long Sample, int StoryFrame);
public sealed record CharacterVoiceReceipt { }

internal sealed class CharacterVoiceSessionAdapter : IDisposable
{
    internal const string ControllerEvidenceMarker = "GenericCharacterVoiceSession.v1";
    // The final mixer already contains every voice source that the game or a
    // Mod plays. The old adapter is therefore an optional observation seam,
    // not a prerequisite for exporting voice.
    internal static bool IsProviderLoaded => false;
    internal static CharacterVoiceSessionAdapter Attach(Action<Exception> fault, Action<string> log) => new();
    internal static string[] CollectKnownIdentifiers(object story) => Array.Empty<string>();
    internal void ValidateController() { }
    internal void BindStory(string key, string[] identifiers) { }
    internal void PreparePass() { }
    internal bool PumpPreload() => true;
    internal bool IsArmed => false;
    internal void BeginPass(object player, int fps, int sampleRate, Func<CharacterVoicePosition> position, CharacterVoiceReceipt? receipt) { }
    internal void ValidateFrame() { }
    internal void CompletePass(CharacterVoicePosition position) { }
    internal CharacterVoiceReceipt SealPass(CharacterVoicePosition position) => new();
    internal void SealForCommit() { }
    internal void EndPass() { }
    internal void ValidateForCommit() { }
    internal IDisposable AcquireCommitScope() => new EmptyScope();
    internal void Reset() { }
    public void Dispose() { }
    private sealed class EmptyScope : IDisposable { public void Dispose() { } }
}

internal static class CharacterVoiceCompatibility
{
    internal static void EnsureStrictExportAllowed() { }
}

internal readonly record struct ControlledBackgroundCommand(long Frame, long Pts, string Op, long Value, string Path, string SourceSha256, long Duration);
internal static class ControlledBackgroundVideoAdapter
{
    internal static readonly ControlledBackgroundCommand[] CommandTrace = Array.Empty<ControlledBackgroundCommand>();
    private static long _lastFrame = -1;
    internal static void Initialize(Harmony harmony, Action<string> log) => RuntimeScenePlane.Probe();
    internal static void TryInstall() { }
    internal static void ValidateActive() { }
    internal static string PlaybackSettingsIdentity()
    {
        int players = 0;
        foreach (var player in UnityEngine.Object.FindObjectsOfType<UnityEngine.Video.VideoPlayer>(true))
            if (player != null && player.isActiveAndEnabled) players++;
        return "background-generic=" + players;
    }
    internal static void BeginExport(Func<long> frame, int fps, string directory, string ffmpeg, string ffprobe)
    {
        _lastFrame = -1;
        RuntimeVirtualizationHost.Trace("background_begin", new Dictionary<string, object?>
        {
            ["frame"] = frame(),
            ["fps"] = fps,
            ["players"] = PlaybackSettingsIdentity()
        });
    }
    internal static void Pump(long frame)
    {
        if (frame == _lastFrame || frame % 30 != 0) return;
        _lastFrame = frame;
        int playing = 0;
        foreach (var player in UnityEngine.Object.FindObjectsOfType<UnityEngine.Video.VideoPlayer>(true))
            if (player != null && player.isActiveAndEnabled && player.isPlaying) playing++;
        RuntimeVirtualizationHost.Trace("background_frame", new Dictionary<string, object?>
        {
            ["frame"] = frame,
            ["playing_players"] = playing
        });
    }
    internal static void EndExport() =>
        RuntimeVirtualizationHost.Trace("background_end", new Dictionary<string, object?> { ["action"] = "end" });
    internal static bool Shutdown() => true;
    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    {
        level = StrictCapabilityLevel.Observed;
        evidence = new[] { "RVL.render-plane.v1", "generic-scene-capture" };
        return true;
    }
}

internal static class MoreEffectsCompatibility
{
    internal static bool FullClockCoverage => true;
    internal static string Status => "RVL.clock-plane.v1";
    internal static string FailureReason => "";
    internal static void Initialize(Harmony harmony, Action<string> log) => RuntimeVirtualizationHost.Probe("clock", RuntimePlaneState.Probed, "generic effects clock");
    internal static void TryInstall() { }
    internal static void EnsureOwnEffectsAllowed() { }
    internal static void BeginExport(Func<double> time) =>
        RuntimeVirtualizationHost.Trace("effects_clock", new Dictionary<string, object?> { ["action"] = "begin", ["time"] = time() });
    internal static void EndExport() =>
        RuntimeVirtualizationHost.Trace("effects_clock", new Dictionary<string, object?> { ["action"] = "end" });
    internal static bool Shutdown() => true;
}

internal static class MultilingualCaptureAdapter
{
    internal static void Initialize(Harmony harmony, Action<string> log) => RuntimeScenePlane.Probe();
    internal static void TryInstall() { }
    internal static void ValidateActive() { }
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
    internal static void Initialize(Harmony harmony, Action<string> log, string configRoot) { }
    internal static void TryInstall() { }
    internal static void ValidateActive() { }
    internal static bool Shutdown() => true;
    internal static void PrepareExportDependencies() { }
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
    internal static NativeBgmSessionAdapter Prepare(object story, Action<Exception> fault, Action<string> log, Action<Exception> cleanupFault) => new();
    internal bool PumpPreload() => true;
    internal void BeginPass(object player) =>
        RuntimeVirtualizationHost.Trace("bgm", new Dictionary<string, object?> { ["action"] = "begin" });
    internal void EndPass() =>
        RuntimeVirtualizationHost.Trace("bgm", new Dictionary<string, object?> { ["action"] = "end" });
    internal bool CleanupPending => false;
    internal Exception? CleanupFailure => null;
    public void Dispose() { }
    internal static void Reset() { }
}

internal static class RetainedMenuPlaybackBoundary
{
    private static IDisposable? _lease;
    internal static void Initialize(Harmony harmony, Action<string> log, Action<Exception> fault, string configRoot) => RuntimeScenePlane.Probe();
    internal static void TryInstall() { }
    internal static void ValidateActive() { }
    internal static void ValidateForCommit() { }
    internal static bool TryAcquireRetained(out IDisposable? lease)
    {
        _lease ??= RuntimeScenePlane.AcquireMenuLease();
        lease = _lease;
        return true;
    }
    internal static void BeginPlayback() => RuntimeScenePlane.MenuPlayback("begin-playback");
    internal static void EndPlayback() => RuntimeScenePlane.MenuPlayback("end-playback");
    internal static IDisposable AcquireCommitScope() => new EmptyScope();
    internal static string PlaybackSettingsIdentity() => "menu-generic=retained";
    internal static bool Shutdown() { _lease = null; return true; }
    internal static void Reset() { _lease = null; }
    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    {
        level = StrictCapabilityLevel.Observed;
        evidence = new[] { "RVL.scene-plane.v1", "generic-menu-boundary" };
        return true;
    }
    private sealed class EmptyScope : IDisposable { public void Dispose() { } }
}
