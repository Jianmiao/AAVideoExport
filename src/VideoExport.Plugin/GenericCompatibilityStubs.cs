using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using HarmonyLib;
using UnityEngine;

namespace AAVideoExport.Plugin;

// 0.3.0 removes the shipped per-Mod adapters from production. These types keep
// the host's optional-resource seams explicit without naming or fingerprinting
// any third-party Mod. A feature that has no generic implementation is absent,
// not silently keyed to an assembly identity.
internal static class AASkipIntroExportBoundary
{
    internal static void Initialize(Harmony harmony) { }
    internal static IDisposable? TryAcquire() => null;
    internal static string CaptureActiveProof() => throw new InvalidOperationException("No generic startup proof is available.");
    internal static void ValidateActive() { }
    internal static void ValidateForCommit() { }
    internal static IDisposable AcquireCommitScope() => new EmptyScope();
    internal static void ReleaseAbsentSnapshot() { }
    internal static void Shutdown() { }
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
    internal static bool IsReady => false;
    internal static string Status => "absent";
    internal static string FailureReason => "no generic portrait clock";
    internal static void Initialize(Harmony harmony, Action<string> log) { }
    internal static void TryInstall() { }
    internal static void EnsureStrictExportAllowed() { }
    internal static void BeginExport(Func<double> time) { }
    internal static void EndExport() { }
    internal static bool Shutdown() => true;
}

internal static class CharacterVoiceDelayClock
{
    internal static void Initialize(Harmony harmony, Action<string> log, Action<Exception> fault) { }
    internal static void TryInstall() { }
    internal static void ValidateBinding() { }
    internal static void BeginExport(Func<long> frame, int fps) { }
    internal static void EndExport() { }
    internal static bool Shutdown() => true;
    internal static void Reset() { }
}

public readonly record struct CharacterVoicePosition(long Frame, long Sample, int StoryFrame);
public sealed record CharacterVoiceReceipt { }

internal sealed class CharacterVoiceSessionAdapter : IDisposable
{
    internal const string ControllerEvidenceMarker = "GenericCharacterVoiceSession.v1";
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
    internal static void Initialize(Harmony harmony, Action<string> log) { }
    internal static void TryInstall() { }
    internal static void ValidateActive() { }
    internal static string PlaybackSettingsIdentity() => "background=generic";
    internal static void BeginExport(Func<long> frame, int fps, string directory, string ffmpeg, string ffprobe) { }
    internal static void Pump(long frame) { }
    internal static void EndExport() { }
    internal static bool Shutdown() => true;
    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    { level = StrictCapabilityLevel.Rejected; evidence = Array.Empty<string>(); return false; }
}

internal static class MoreEffectsCompatibility
{
    internal static bool FullClockCoverage => true;
    internal static string Status => "generic";
    internal static string FailureReason => "no generic effects adapter";
    internal static void Initialize(Harmony harmony, Action<string> log) { }
    internal static void TryInstall() { }
    internal static void EnsureOwnEffectsAllowed() { }
    internal static void BeginExport(Func<double> time) { }
    internal static void EndExport() { }
    internal static bool Shutdown() => true;
}

internal static class MultilingualCaptureAdapter
{
    internal static void Initialize(Harmony harmony, Action<string> log) { }
    internal static void TryInstall() { }
    internal static void ValidateActive() { }
    internal static string PlaybackSettingsIdentity() => "translation=generic";
    internal static void BeginExport() { }
    internal static void Pump(long frame) { }
    internal static void EndExport() { }
    internal static bool Shutdown() => true;
    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    { level = StrictCapabilityLevel.Rejected; evidence = Array.Empty<string>(); return false; }
}

internal static class RuntimeModBoundaries
{
    internal static void Initialize(Harmony harmony, Action<string> log, string configRoot) { }
    internal static void TryInstall() { }
    internal static void ValidateActive() { }
    internal static bool Shutdown() => true;
    internal static void PrepareExportDependencies() { }
    internal static void BeginExport() { }
    internal static void EndExport() { }
    internal static string PlaybackSettingsIdentity() => "effects=generic";
    internal static string PlaybackSettingsWithoutEffectsSource() => PlaybackSettingsIdentity();
    internal static bool RequiresEffectsSourcePreparation() => false;
    internal static EffectsSourceInput? PrepareEffectsSourceInput(string story, string hash) => null;
    internal static void RevalidateEffectsSourceInput(EffectsSourceInput input) { }
    internal static void ReleaseEffectsSourceInput(EffectsSourceInput? input) { }
    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    { level = StrictCapabilityLevel.Rejected; evidence = Array.Empty<string>(); return false; }
}

internal sealed class NativeBgmSessionAdapter : IDisposable
{
    internal static NativeBgmSessionAdapter Prepare(object story, Action<Exception> fault, Action<string> log, Action<Exception> cleanupFault) => new();
    internal bool PumpPreload() => true;
    internal void BeginPass(object player) { }
    internal void EndPass() { }
    internal bool CleanupPending => false;
    internal Exception? CleanupFailure => null;
    public void Dispose() { }
    internal static void Reset() { }
}

internal static class RetainedMenuPlaybackBoundary
{
    internal static void Initialize(Harmony harmony, Action<string> log, Action<Exception> fault, string configRoot) { }
    internal static void TryInstall() { }
    internal static void ValidateActive() { }
    internal static void ValidateForCommit() { }
    internal static bool TryAcquireRetained(out IDisposable? lease) { lease = new EmptyScope(); return true; }
    internal static void BeginPlayback() { }
    internal static void EndPlayback() { }
    internal static IDisposable AcquireCommitScope() => new EmptyScope();
    internal static string PlaybackSettingsIdentity() => "menu=generic";
    internal static bool Shutdown() => true;
    internal static void Reset() { }
    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    { level = StrictCapabilityLevel.Rejected; evidence = Array.Empty<string>(); return false; }
    private sealed class EmptyScope : IDisposable { public void Dispose() { } }
}
