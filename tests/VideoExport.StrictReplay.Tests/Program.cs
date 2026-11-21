using AAVideoExport.Core;

var passed = 0;
var failed = 0;
Run("equivalent PCM with different callback splits is accepted", () => WithLedger(ledger =>
{
    ledger.PrepareFrame(9, false, -1, new[] { .25f, .5f }, 2);
    ledger.PrepareFrame(10, true, 1, new[] { .75f, 1f, .125f, .375f }, 4);
    ledger.BeginReplay();
    ledger.ValidateFrame(9, false, -1, Array.Empty<float>(), 0);
    ledger.ValidateFrame(10, true, 1, new[] { .25f, .5f, .75f, 1f, .125f, .375f }, 6);
    ledger.CompleteReplay();
    Equal(2L, ledger.TotalFrames);
    Equal(2L, ledger.ReplayFrames);
    Equal(3L, ledger.PreparationSampleFrames);
    Equal(3L, ledger.ReplaySampleFrames);
    Check(ledger.PreparationPcmSha256.Length == 64, "a complete PCM receipt is available");
    Check(ledger.FrameTraceSha256.Length == 64, "a complete frame trace receipt is available");
}));
Run("changed dialogue at the same frame is rejected", () => WithLedger(ledger =>
{
    ledger.PrepareFrame(9, false, -1, new[] { .25f, .5f }, 2);
    ledger.BeginReplay();
    Throws("timeline_replay_mismatch", () => ledger.ValidateFrame(10, false, -1, new[] { .25f, .5f }, 2));
}));
Run("changed choice presence is rejected", () => WithLedger(ledger =>
{
    ledger.PrepareFrame(9, true, 2, new[] { .25f, .5f }, 2);
    ledger.BeginReplay();
    Throws("timeline_replay_mismatch", () => ledger.ValidateFrame(9, false, 2, new[] { .25f, .5f }, 2));
}));
Run("changed default choice is rejected", () => WithLedger(ledger =>
{
    ledger.PrepareFrame(9, true, 2, new[] { .25f, .5f }, 2);
    ledger.BeginReplay();
    Throws("timeline_replay_mismatch", () => ledger.ValidateFrame(9, true, 1, new[] { .25f, .5f }, 2));
}));
Run("same sample count with changed PCM is rejected", () => WithLedger(ledger =>
{
    ledger.PrepareFrame(9, false, -1, new[] { .25f, .5f }, 2);
    ledger.BeginReplay();
    ledger.ValidateFrame(9, false, -1, new[] { .25f, .5001f }, 2);
    Throws("audio_replay_mismatch", ledger.CompleteReplay);
}));
Run("missing PCM is rejected at completion", () => WithLedger(ledger =>
{
    ledger.PrepareFrame(9, false, -1, new[] { .25f, .5f }, 2);
    ledger.BeginReplay();
    ledger.ValidateFrame(9, false, -1, Array.Empty<float>(), 0);
    Throws("audio_replay_mismatch", ledger.CompleteReplay);
}));
Run("extra PCM is rejected at completion", () => WithLedger(ledger =>
{
    ledger.PrepareFrame(9, false, -1, new[] { .25f, .5f }, 2);
    ledger.BeginReplay();
    ledger.ValidateFrame(9, false, -1, new[] { .25f, .5f, .25f, .5f }, 4);
    Throws("audio_replay_mismatch", ledger.CompleteReplay);
}));
Run("missing replay frame cannot finalize", () => WithLedger(ledger =>
{
    ledger.PrepareFrame(9, false, -1, Array.Empty<float>(), 0);
    ledger.PrepareFrame(10, false, -1, Array.Empty<float>(), 0);
    ledger.BeginReplay();
    ledger.ValidateFrame(9, false, -1, Array.Empty<float>(), 0);
    Throws("timeline_replay_mismatch", ledger.CompleteReplay);
}));
Run("extra replay frame cannot be accepted", () => WithLedger(ledger =>
{
    ledger.PrepareFrame(9, false, -1, Array.Empty<float>(), 0);
    ledger.BeginReplay();
    ledger.ValidateFrame(9, false, -1, Array.Empty<float>(), 0);
    Throws("timeline_replay_mismatch", () => ledger.ValidateFrame(9, false, -1, Array.Empty<float>(), 0));
}));
Run("non-finite and incomplete-channel PCM cannot change the ledger", () => WithLedger(ledger =>
{
    Throws("invalid_audio", () => ledger.PrepareFrame(1, false, -1, new[] { 1f }, 1));
    Throws("invalid_audio", () => ledger.PrepareFrame(1, false, -1, new[] { 1f, float.NaN }, 2));
    Equal(0L, ledger.TotalFrames);
    Equal(0L, ledger.PreparationSampleFrames);
    ledger.PrepareFrame(2, false, -1, new[] { 1f, .5f }, 2);
    ledger.BeginReplay();
    ledger.ValidateFrame(2, false, -1, new[] { 1f, .5f }, 2);
    ledger.CompleteReplay();
}));
Run("illegal phase transitions are rejected", () => WithLedger(ledger =>
{
    Throws("invalid_state", () => ledger.ValidateFrame(1, false, -1, Array.Empty<float>(), 0));
    Throws("invalid_state", ledger.CompleteReplay);
    ledger.PrepareFrame(2, false, -1, Array.Empty<float>(), 0);
    ledger.BeginReplay();
    Throws("invalid_state", () => ledger.PrepareFrame(2, false, -1, Array.Empty<float>(), 0));
    Throws("invalid_state", ledger.BeginReplay);
    ledger.ValidateFrame(2, false, -1, Array.Empty<float>(), 0);
    ledger.CompleteReplay();
    Throws("invalid_state", () => ledger.ValidateFrame(2, false, -1, Array.Empty<float>(), 0));
}));
Run("ledger owns only its unique transcript", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "aave-replay-existing-" + Guid.NewGuid().ToString("N"));
    File.WriteAllText(path, "keep me");
    try
    {
        try { using var _ = new StrictReplayLedger(path, 2); throw new Exception("Existing transcript accepted"); }
        catch (IOException) { }
        Equal("keep me", File.ReadAllText(path));
    }
    finally { File.Delete(path); }
});
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

void Run(string name, Action run)
{
    try { run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
static void WithLedger(Action<StrictReplayLedger> action)
{
    string path = Path.Combine(Path.GetTempPath(), "aave-replay-test-" + Guid.NewGuid().ToString("N"));
    using (var ledger = new StrictReplayLedger(path, 2)) action(ledger);
    Check(!File.Exists(path), "private transcript is removed on disposal");
}
static void Throws(string code, Action action)
{
    try { action(); }
    catch (ExportException error) { Equal(code, error.Code); return; }
    throw new Exception("Expected " + code + ".");
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}.");
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
