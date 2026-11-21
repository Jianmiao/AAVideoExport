using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using HarmonyLib;

Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES", "1");
Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION", "1");
if (ExportBuildProfile.DevelopmentAuditEnabled) throw new Exception("Production controller suite requires Release or Debug; environment flags must not enable auditing.");
int passed = 0, failed = 0;
void Run(string name, Action run)
{
    try { run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Reject(Action run) { try { run(); } catch (InvalidOperationException) { return; } throw new Exception("Production returned an audit proof."); }
Run("production menu acquire playback roundtrip and release never invoke identity auditing", () =>
{
    var auditTrap = new Harmony("aave.production-controller.audit-trap");
    var identity = typeof(RetainedMenuPlaybackBoundary).GetMethod("ReadIdentity", BindingFlags.Static | BindingFlags.NonPublic)!;
    auditTrap.Patch(identity, prefix: new HarmonyMethod(typeof(Traps).GetMethod(nameof(Traps.Audit))!));
    var graph = typeof(RetainedMenuPlaybackBoundary).GetNestedType("Binding", BindingFlags.NonPublic)!.GetMethod("OriginalGraph", BindingFlags.Instance | BindingFlags.NonPublic)!;
    auditTrap.Patch(graph, prefix: new HarmonyMethod(typeof(Traps).GetMethod(nameof(Traps.Audit))!));
    auditTrap.Patch(typeof(RetainedPatchGuard).GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!, prefix: new HarmonyMethod(typeof(Traps).GetMethod(nameof(Traps.Audit))!));
    try
    {
        RetainedMenuPlaybackBoundary.Initialize(new Harmony("aave.production-controller.menu"), _ => { }, error => throw error, Path.GetTempPath());
        Check(RetainedMenuPlaybackBoundary.TryAcquireRetained(out var lease) && lease != null, "Functional resource lease was not acquired.");
        using (lease)
        {
            RetainedMenuPlaybackBoundary.BeginPlayback(); RetainedMenuPlaybackBoundary.ValidateActive(); RetainedMenuPlaybackBoundary.EndPlayback();
            RetainedMenuPlaybackBoundary.BeginPlayback(); RetainedMenuPlaybackBoundary.ValidateActive(); RetainedMenuPlaybackBoundary.EndPlayback();
            Reject(() => RetainedMenuPlaybackBoundary.CaptureActiveProof()); Reject(RetainedMenuPlaybackBoundary.ValidateForCommit);
            Reject(() => RetainedMenuPlaybackBoundary.AcquireCommitScope());
        }
        Check(!RetainedMenuPlaybackBoundary.IsHeld && RetainedMenuPlaybackBoundary.Shutdown(), "Functional menu lease did not release.");
    }
    finally { RetainedMenuPlaybackBoundary.Shutdown(); auditTrap.UnpatchSelf(); }
});
Run("production voice story binding accepts the selected path without manufacturing a digest", () =>
{
    var binding = new CharacterVoiceStoryBinding(); const string path = @"E:\selected-story.aas";
    binding.BindStory(path, new[] { "aavt/voice/test" });
    Check(binding.RequireBound("production").StoryKey == path, "Execution story key was replaced by an audit digest.");
    Reject(() => { _ = binding.RequireBound("production").StorySha256; });
});
Run("functional menu resources run the original exit and retain failed cleanup for retry", () =>
{
    bool nativeIdle = false; int exits = 0, faults = 0;
    var controller = new RetainedPlaybackController(() => Check(nativeIdle, "Native resources remained active."), _ => faults++);
    var once = new ResourceProbe(); var retry = new ResourceProbe { FailOnce = true };
    var lease = controller.Acquire(new[] { once, retry });
    controller.BeginPlayback(() => { exits++; nativeIdle = true; }); controller.ValidateActive(); controller.EndPlayback();
    nativeIdle = false; controller.BeginPlayback(() => { exits++; nativeIdle = true; }); controller.ValidateActive(); controller.EndPlayback();
    try { lease.Dispose(); throw new Exception("Expected real resource cleanup failure."); } catch (AggregateException) { }
    Check(controller.IsHeld && faults == 1 && once.Calls == 1 && retry.Calls == 1, "Failed cleanup lost resources or duplicated release.");
    lease.Dispose(); Check(!controller.IsHeld && once.Calls == 1 && retry.Calls == 2 && exits == 2, "Retry did not settle actual resource ownership.");
});
Run("production voice controller prepares starts completes and cancels without provenance or PCM receipts", ProductionVoiceChecks.Run);
Console.WriteLine($"RESULT {passed} passed; {failed} failed; production controllers with audit traps");
return failed == 0 ? 0 : 1;
public static class Traps { public static void Audit() => throw new InvalidOperationException("AUDIT ENTRY REACHED IN PRODUCTION"); }
sealed class ResourceProbe : IDisposable
{ internal int Calls; internal bool FailOnce; public void Dispose() { Calls++; if (FailOnce) { FailOnce = false; throw new IOException("actual cleanup failed"); } } }
