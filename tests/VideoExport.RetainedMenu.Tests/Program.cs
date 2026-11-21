using AAVideoExport.Plugin;

int passed = 0, failed = 0;
void Test(string name, Action run)
{
    try { run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
Test("retained identity survives catalog roundtrip and native cleanup runs without suppressing effects", () =>
{
    string identity = "selection+mapping"; bool nativeIdle = false; int exits = 0;
    var proof = new RetainedPlaybackProof(() => identity, () => Check(nativeIdle, "menu state still active"), _ => { });
    using var lease = proof.Acquire();
    proof.BeginPlayback(() => { nativeIdle = true; exits++; }); proof.ValidateActive(); proof.EndPlayback();
    nativeIdle = false; proof.ValidateActive(); // Original Catalog effects continue between passes.
    proof.BeginPlayback(() => { nativeIdle = true; exits++; }); proof.ValidateActive();
    Check(exits == 2 && proof.IsHeld && proof.IsPlayback && proof.CaptureActiveProof().Length != 0, "retained pass ownership was lost");
    proof.EndPlayback(); proof.ValidateForCommit();
});
Test("late selection mapping or patch identity mutation invalidates active and worker commit proof", () =>
{
    string identity = "same"; int faults = 0;
    var proof = new RetainedPlaybackProof(() => identity, () => { }, _ => faults++);
    using var lease = proof.Acquire(); proof.BeginPlayback(() => { }); identity = "changed";
    Reject(proof.ValidateActive); Reject(proof.ValidateForCommit); Reject(() => proof.CaptureActiveProof());
    Check(faults == 1, "fault callback was missed or repeated");
});
Test("playback scene or pending resource mutation fails before publication", () =>
{
    bool idle = true; var proof = new RetainedPlaybackProof(() => "stable", () => Check(idle, "unexpected live activity"), _ => { });
    using var lease = proof.Acquire(); proof.BeginPlayback(() => { }); idle = false;
    Reject(proof.ValidateActive); Reject(() => proof.AcquireCommitScope());
});
Test("explicit user mutation faults capture synchronously but original action remains executable", () =>
{
    bool cancelled = false; int originalEffects = 0;
    var proof = new RetainedPlaybackProof(() => "stable", () => { }, _ => cancelled = true);
    using var lease = proof.Acquire(); proof.BeginPlayback(() => { });
    proof.Mutation("open selector"); originalEffects++;
    Check(cancelled && originalEffects == 1, "original effect was suppressed or capture was not cancelled");
    Reject(proof.ValidateForCommit);
});
Test("a specific background config write waits and resumes exactly once after release", () =>
{
    var proof = new RetainedPlaybackProof(() => "stable", () => { }, _ => { });
    var lease = proof.Acquire(); int writes = 0;
    using var entered = new ManualResetEventSlim();
    var worker = Task.Run(() => { entered.Set(); proof.BeforeWrite(); try { Interlocked.Increment(ref writes); } finally { proof.AfterWrite(); } });
    entered.Wait(); Check(!worker.Wait(50) && writes == 0, "worker wrote while capture retained ownership");
    lease.Dispose(); Check(worker.Wait(TimeSpan.FromSeconds(2)) && writes == 1, "original write was dropped or did not resume");
});
Test("faulted main-thread mutation releases retained read locks before the original user write", () =>
{
    var resource = new DisposeProbe(); var proof = new RetainedPlaybackProof(() => "stable", () => { }, _ => { });
    using var lease = proof.Acquire(new[] { resource }); proof.BeginPlayback(() => { }); proof.Mutation("save current choice");
    Check(resource.Calls == 1 && proof.IsHeld, "input lock would block the original action or discarded the invalid retained proof");
    Reject(proof.ValidateForCommit);
});
Test("already executing observed write prevents acquisition until it finishes", () =>
{
    var proof = new RetainedPlaybackProof(() => "stable", () => { }, _ => { });
    proof.BeforeWrite(); Reject(() => proof.Acquire()); proof.AfterWrite();
    using var lease = proof.Acquire(); Check(proof.IsHeld, "proof did not recover after the original write completed");
});
Test("partial retained resource cleanup remains owned and retries only pending work", () =>
{
    var once = new DisposeProbe(); var retry = new DisposeProbe { FailOnce = true };
    var proof = new RetainedPlaybackProof(() => "stable", () => { }, _ => { });
    var lease = proof.Acquire(new[] { once, retry }); Reject(lease.Dispose);
    Check(proof.IsHeld && once.Calls == 1 && retry.Calls == 1, "partial cleanup discarded ownership");
    lease.Dispose(); Check(!proof.IsHeld && once.Calls == 1 && retry.Calls == 2, "cleanup retry repeated settled work or lost pending resource");
});
Test("commit scope serializes a late mutation without deadlocking its original action", () =>
{
    var proof = new RetainedPlaybackProof(() => "stable", () => { }, _ => { });
    using var lease = proof.Acquire();
    using var scope = proof.AcquireCommitScope();
    var worker = Task.Run(() => proof.Mutation("late config mutation"));
    Check(!worker.Wait(50), "mutation crossed the active publication commit scope");
    scope.Dispose(); Check(worker.Wait(TimeSpan.FromSeconds(2)), "mutation did not resume after commit scope");
    Reject(proof.ValidateForCommit);
});
Test("released proof never authorizes a later export", () =>
{
    var proof = new RetainedPlaybackProof(() => "stable", () => { }, _ => { });
    proof.Acquire().Dispose(); Reject(proof.ValidateForCommit); Reject(() => proof.CaptureActiveProof());
});
PatchGuardTests.Run(Test);
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Reject(Action action) { try { action(); } catch { return; } throw new Exception("invalid boundary was accepted"); }
sealed class DisposeProbe : IDisposable { internal int Calls; internal bool FailOnce; public void Dispose() { Calls++; if (FailOnce) { FailOnce = false; throw new IOException("fixture cleanup failure"); } } }
