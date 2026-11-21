using AAVideoExport.Plugin;

int passed = 0, failed = 0;
string story = new('a', 64);
var asset = new CharacterVoiceAsset("aavt/voice/fixture", new string('b', 64), new string('c', 64), 22050, 2, 22050);

Run("attached backend without story binding cannot prepare or begin a pass", () =>
{
    foreach (string operation in new[] { "PreparePass", "BeginPass", "ValidateFrame" })
    {
        var binding = new CharacterVoiceStoryBinding();
        Throws("voice_story_unbound", () => binding.RequireBound(operation));
        Check(!binding.IsBound, "an unbound operation created authorization");
    }
});
Run("story binding is immutable and cannot be replaced after preflight", () =>
{
    var ids = new[] { asset.Identifier };
    var binding = new CharacterVoiceStoryBinding(); binding.BindStory(story, ids);
    ids[0] = "aavt/voice/changed";
    Equal(asset.Identifier, binding.RequireBound("PumpPreload").Identifiers.Single());
    Throws("voice_story_rebound", () => binding.BindStory(new string('d', 64), new[] { asset.Identifier }));
    Throws("voice_binding_faulted", () => binding.RequireBound("BeginPass"));
});
Run("preload-before-bind faults the retained scope instead of allowing a late rescue", () =>
{
    var binding = new CharacterVoiceStoryBinding();
    Throws("voice_story_unbound", () => binding.RequireBound("PumpPreload"));
    Throws("voice_binding_faulted", () => binding.BindStory(story, new[] { asset.Identifier }));
});
Run("frozen zero-owned-voice story binds as an explicit absence scope", () =>
{
    var binding = new CharacterVoiceStoryBinding(); binding.BindStory(story, Array.Empty<string>());
    Check(binding.RequireBound("BeginPass").NoOwnedVoiceExpected, "empty frozen manifest lost its explicit absence scope");
});
Run("explicit no-owned-voice preparation and replay seal equal empty-event receipts", () =>
{
    var preparation = new CharacterVoiceSessionTrace(story, 30, 48000, "voice=1", Array.Empty<CharacterVoiceAsset>(), noOwnedVoiceExpected: true);
    var receipt = preparation.Seal(P(120, 192000));
    Check(receipt.NoOwnedVoiceExpected && receipt.Events.Count == 0, "no-voice receipt was not scoped explicitly");
    var replay = new CharacterVoiceSessionTrace(story, 30, 48000, "voice=1", Array.Empty<CharacterVoiceAsset>(), receipt, noOwnedVoiceExpected: true);
    Equal(receipt.TraceSha256, replay.Seal(P(120, 192000)).TraceSha256);
});
Run("an actual owned request invalidates a no-owned-voice receipt immediately", () =>
{
    var trace = new CharacterVoiceSessionTrace(story, 30, 48000, "voice=1", Array.Empty<CharacterVoiceAsset>(), noOwnedVoiceExpected: true);
    Throws("voice_unexpected_request", () => trace.Request(1, asset.Identifier, P(10, 16000), .1));
    Throws("voice_session_faulted", () => trace.Seal(P(120, 192000)));
});
Run("an unscoped empty manifest cannot silently authorize no-voice capture", () =>
{
    Throws("voice_asset_invalid", () => new CharacterVoiceSessionTrace(story, 30, 48000, "voice=1", Array.Empty<CharacterVoiceAsset>()));
});
Run("output commit requires successful preparation and matching replay on the retained scope", () =>
{
    var commit = new CharacterVoiceCommitState();
    Throws("voice_commit_incomplete", commit.SealForCommit);
    commit = new CharacterVoiceCommitState(); var receipt = CommitReceipt();
    commit.BeginPass(null); commit.CompletePass(receipt);
    Throws("voice_commit_incomplete", commit.SealForCommit);
    commit = new CharacterVoiceCommitState();
    Throws("voice_commit_order", () => commit.BeginPass(receipt));
});
Run("matching two-pass voice and explicit no-owned-voice commits validate on workers", () =>
{
    foreach (bool silentScope in new[] { false, true })
    {
        using var commit = ReadyCommit(silentScope);
        int checks = 0;
        Task.Run(() =>
        {
            commit.ValidateForCommit(() => Interlocked.Increment(ref checks));
            using var scope = commit.AcquireCommitScope(() => { Interlocked.Increment(ref checks); return new EmptyLease(); });
        }).GetAwaiter().GetResult();
        Equal(2, checks);
    }
});
Run("a mismatched replay or a third pass cannot replace publication proof", () =>
{
    var commit = new CharacterVoiceCommitState(); var preparation = CommitReceipt();
    commit.BeginPass(null); commit.CompletePass(preparation); commit.BeginPass(preparation);
    var different = New(); Speak(different, 1000, 0, 0, 0, 31, 49152, 34, 54272);
    Throws("voice_commit_mismatch", () => commit.CompletePass(different.Seal(P(41, 65600))));
    Throws("voice_commit_faulted", () => commit.BeginPass(null));
    using var complete = ReadyCommit();
    Throws("voice_commit_order", () => complete.BeginPass(null));
});
Run("fault or disposal before publication cannot be re-armed", () =>
{
    using var failedCommit = ReadyCommit(); failedCommit.Fail(new InvalidOperationException("native failure"));
    Throws("voice_commit_faulted", () => failedCommit.ValidateForCommit(() => throw new Exception("must not run")));
    Throws("voice_commit_faulted", failedCommit.SealForCommit);
    var disposedCommit = ReadyCommit(); disposedCommit.Dispose();
    Throws("voice_commit_disposed", () => disposedCommit.AcquireCommitScope(() => new EmptyLease()));
});
Run("publication lease serializes a concurrent native fault and disposal", () =>
{
    foreach (bool dispose in new[] { false, true })
    {
        var commit = ReadyCommit(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var mutationStarted = new ManualResetEventSlim(); using var mutationFinished = new ManualResetEventSlim();
        var publisher = Task.Run(() => { using var lease = commit.AcquireCommitScope(() => new EmptyLease()); entered.Set(); Check(release.Wait(3000), "release signal missing"); });
        Check(entered.Wait(3000), "publication scope did not acquire");
        var invalidator = Task.Run(() => { mutationStarted.Set(); if (dispose) commit.Dispose(); else commit.Fail(new Exception("fault")); mutationFinished.Set(); });
        Check(mutationStarted.Wait(3000), "invalidation did not start");
        try { Check(!mutationFinished.Wait(100), "fault/disposal crossed an active publication lease"); }
        finally { release.Set(); }
        Task.WaitAll(new[] { publisher, invalidator }, 3000);
        Check(mutationFinished.IsSet, "invalidation remained blocked after publication scope ended");
        Throws(dispose ? "voice_commit_disposed" : "voice_commit_faulted", () => commit.ValidateForCommit(() => { }));
    }
});
Run("real pinned voice files reject changes and stay read-locked during publication", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "aave-voice-commit-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory); string path = Path.Combine(directory, "voice.wav");
    byte[] original = { 1, 3, 5, 7 }; File.WriteAllBytes(path, original);
    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original));
    var pins = new[] { new CharacterVoiceCommitFilePin(path, hash) };
    try
    {
        using var commit = ReadyCommit();
        using (commit.AcquireCommitScope(() => CharacterVoiceCommitFiles.Acquire(pins)))
        {
            bool blocked = false; try { using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); } catch (IOException) { blocked = true; }
            Check(blocked, "voice file remained writable inside publication scope");
        }
        File.WriteAllBytes(path, new byte[] { 2, 4, 6, 8 });
        Throws("voice_commit_file_changed", () => commit.ValidateForCommit(() => { using var lease = CharacterVoiceCommitFiles.Acquire(pins); }));
        File.WriteAllBytes(path, original);
        Throws("voice_commit_faulted", () => commit.ValidateForCommit(() => { using var lease = CharacterVoiceCommitFiles.Acquire(pins); }));
    }
    finally { File.Delete(path); Directory.Delete(directory); }
});

Run("preparation and replay ignore different native request sequence numbers", () =>
{
    var preparation = New(); Speak(preparation, 41, 0, 0, 0, 31, 49152, 34, 54272);
    var receipt = preparation.Seal(P(40, 64000));
    var replay = New(receipt: receipt); Speak(replay, 9001, 0, 0, 0, 31, 49152, 34, 54272);
    var compared = replay.Seal(P(40, 64000));
    Equal(receipt.TraceSha256, compared.TraceSha256);
    Check(receipt.Events.All(item => item.SourceId == "story/" + story + "/character-voice-channel"), "unstable source identity");
    Equal(4, receipt.Events.Count);
});
Run("repeated identical voice requests retain distinct local ordinals", () =>
{
    var trace = New(); Speak(trace, 3, 2, 0, 0, 31, 49152, 34, 54272);
    Speak(trace, 8, 2, 34, 54272, 65, 103424, 68, 108544);
    var receipt = trace.Seal(P(70, 112000));
    Equal(8, receipt.Events.Count); Equal(0, receipt.Events[0].RequestOrdinal); Equal(1, receipt.Events[4].RequestOrdinal);
});
foreach (int fps in new[] { 24, 30, 60 })
    Run($"0.1 authored seconds preserves exact first eligible frame at {fps} FPS", () =>
    {
        var trace = New(fps); trace.Request(1, asset.Identifier, P(5, 1000), .1);
        trace.NativeStart(1, P(5, 1000), asset.AssetId, .5f, 1f); trace.NativeEnd(1, P(20, 30000));
        long due = 20 + (fps + 9) / 10;
        trace.DelayComplete(1, P(due, 36000)); trace.Seal(P(due + 1, 40000));
    });
Run("zero user voice gain is recorded without manufacturing audible output", () =>
{
    var trace = New(); trace.Request(1, asset.Identifier, P(0, 0), 0);
    trace.NativeStart(1, P(0, 0), asset.AssetId, 0, 1); trace.NativeEnd(1, P(30, 48000));
    trace.DelayComplete(1, P(30, 48000)); var receipt = trace.Seal(P(31, 49600));
    Equal(0f, receipt.Events.Single(item => item.Operation == "NativeStart").Volume!.Value);
});
Run("duplicate native start permanently invalidates the session", () =>
{
    var trace = New(); trace.Request(1, asset.Identifier, P(0, 0), .1); trace.NativeStart(1, P(0, 0), asset.AssetId, 1, 1);
    Throws("voice_event_order", () => trace.NativeStart(1, P(0, 0), asset.AssetId, 1, 1));
    Throws("voice_session_faulted", () => trace.Seal(P(100, 160000)));
});
Run("missing start cannot be hidden by an end event", () =>
{
    var trace = New(); trace.Request(1, asset.Identifier, P(0, 0), .1);
    Throws("voice_event_order", () => trace.NativeEnd(1, P(20, 30000)));
});
Run("duplicate end and premature completion fail closed", () =>
{
    var trace = New(); trace.Request(1, asset.Identifier, P(0, 0), .1); trace.NativeStart(1, P(0, 0), asset.AssetId, 1, 1);
    trace.NativeEnd(1, P(20, 30000)); Throws("voice_event_order", () => trace.NativeEnd(1, P(20, 30000)));
    trace = New(); trace.Request(1, asset.Identifier, P(0, 0), .1); trace.NativeStart(1, P(0, 0), asset.AssetId, 1, 1);
    trace.NativeEnd(1, P(20, 30000)); Throws("voice_delay_boundary", () => trace.DelayComplete(1, P(22, 34000)));
});
Run("unknown voice and stale runtime sequence cannot acquire a receipt", () =>
{
    var trace = New(); Throws("voice_asset_unknown", () => trace.Request(1, "aavt/voice/unlisted", P(0, 0), .1));
    trace = New(); trace.Request(4, asset.Identifier, P(0, 0), .1);
    Throws("voice_request_identity", () => trace.NativeStart(3, P(0, 0), asset.AssetId, 1, 1));
});
Run("runtime watchdog or cancellation never supplies a successful end", () =>
{
    var trace = New(); trace.Request(1, asset.Identifier, P(0, 0), .1); trace.NativeStart(1, P(0, 0), asset.AssetId, 1, 1);
    trace.Abort("playback-timeout"); Throws("voice_session_faulted", () => trace.Seal(P(100, 160000)));
    var fresh = New(); Speak(fresh, 8, 0, 0, 0, 31, 49152, 34, 54272); fresh.Seal(P(40, 64000));
});
Run("uncompleted request and empty trace cannot seal", () =>
{
    var trace = New(); Throws("voice_session_empty", () => trace.Seal(P(1, 1600)));
    trace = New(); trace.Request(1, asset.Identifier, P(0, 0), .1);
    Throws("voice_session_incomplete", () => trace.Seal(P(100, 160000)));
});
Run("event frame and relative sample coordinates cannot move backward", () =>
{
    var trace = New(); trace.Request(1, asset.Identifier, P(2, 3200), .1);
    Throws("voice_position_invalid", () => trace.NativeStart(1, P(2, 3199), asset.AssetId, 1, 1));
});
Run("changed native clip identity or gain during playback is rejected", () =>
{
    var trace = New(); trace.Request(1, asset.Identifier, P(0, 0), .1);
    Throws("voice_clip_identity", () => trace.NativeStart(1, P(0, 0), new string('d', 64), 1, 1));
    trace = New(); trace.Request(1, asset.Identifier, P(0, 0), .1); trace.NativeStart(1, P(0, 0), asset.AssetId, .5f, 1);
    Throws("voice_playback_changed", () => trace.ValidatePlayback(1, asset.AssetId, .6f, 1));
});
Run("replay rejects one sample or frame of native event drift", () =>
{
    var prep = New(); Speak(prep, 1, 0, 0, 0, 31, 49152, 34, 54272); var receipt = prep.Seal(P(40, 64000));
    var replay = New(receipt: receipt); replay.Request(100, asset.Identifier, P(0, 0), .1);
    Throws("voice_replay_mismatch", () => replay.NativeStart(100, P(0, 1), asset.AssetId, 1, 1));
    replay = New(receipt: receipt); replay.Request(100, asset.Identifier, P(0, 0), .1);
    Throws("voice_replay_mismatch", () => replay.NativeStart(100, P(1, 0), asset.AssetId, 1, 1));
});
Run("replay rejects changed manifest or playback settings before events", () =>
{
    var prep = New(); Speak(prep, 1, 0, 0, 0, 31, 49152, 34, 54272); var receipt = prep.Seal(P(40, 64000));
    Throws("voice_replay_scope", () => New(receipt: receipt, settings: "voice=0"));
    var different = asset with { FileSha256 = new string('e', 64) };
    Throws("voice_replay_scope", () => new CharacterVoiceSessionTrace(story, 30, 48000, "voice=1", new[] { different }, receipt));
});
Run("replay cannot omit or add a completed native voice request", () =>
{
    var preparation = New(); Speak(preparation, 1, 0, 0, 0, 31, 49152, 34, 54272);
    Speak(preparation, 2, 0, 34, 54272, 65, 103424, 68, 108544);
    var receipt = preparation.Seal(P(70, 112000));
    var replay = New(receipt: receipt); Speak(replay, 200, 0, 0, 0, 31, 49152, 34, 54272);
    Throws("voice_replay_mismatch", () => replay.Seal(P(70, 112000)));
    preparation = New(); Speak(preparation, 1, 0, 0, 0, 31, 49152, 34, 54272); receipt = preparation.Seal(P(40, 64000));
    replay = New(receipt: receipt); Speak(replay, 300, 0, 0, 0, 31, 49152, 34, 54272);
    Throws("voice_replay_mismatch", () => replay.Request(301, asset.Identifier, P(35, 56000), .1));
});
Run("a late completed delay fails even if prep and replay could repeat it", () =>
{
    var trace = New(); trace.Request(1, asset.Identifier, P(0, 0), .1); trace.NativeStart(1, P(0, 0), asset.AssetId, 1, 1);
    trace.NativeEnd(1, P(31, 49152)); Throws("voice_delay_boundary", () => trace.DelayComplete(1, P(35, 56000)));
});
Run("receipt collections cannot be modified through caller arrays", () =>
{
    var inputs = new[] { asset };
    var trace = new CharacterVoiceSessionTrace(story, 30, 48000, "voice=1", inputs);
    inputs[0] = asset with { FileSha256 = new string('f', 64) };
    Speak(trace, 1, 0, 0, 0, 31, 49152, 34, 54272); var receipt = trace.Seal(P(40, 64000));
    Equal(asset.AssetId, receipt.Assets[0].AssetId);
    Check(receipt.Events is not CharacterVoiceEvent[], "receipt exposed a mutable event array");
});
Run("invalid frame rate, identifier and nonfinite gain are rejected", () =>
{
    Throws("voice_scope_invalid", () => New(0));
    Throws("voice_asset_invalid", () => new CharacterVoiceSessionTrace(story, 30, 48000, "voice=1", new[] { asset with { Identifier = "aavt/voice/../bad" } }));
    var trace = New(); trace.Request(1, asset.Identifier, P(0, 0), .1);
    Throws("voice_playback_changed", () => trace.NativeStart(1, P(0, 0), asset.AssetId, float.NaN, 1));
});

Console.WriteLine($"RESULT {passed} passed; {failed} failed");
return failed == 0 ? 0 : 1;

CharacterVoiceSessionTrace New(int fps = 30, CharacterVoiceReceipt? receipt = null, string settings = "voice=1") => new(story, fps, 48000, settings, new[] { asset }, receipt);
CharacterVoiceReceipt CommitReceipt(bool noOwned = false, CharacterVoiceReceipt? expected = null)
{
    if (noOwned) return new CharacterVoiceSessionTrace(story, 30, 48000, "voice=1", Array.Empty<CharacterVoiceAsset>(), expected, true).Seal(P(40, 64000));
    var trace = New(receipt: expected); Speak(trace, expected == null ? 1 : 200, 0, 0, 0, 31, 49152, 34, 54272); return trace.Seal(P(40, 64000));
}
CharacterVoiceCommitState ReadyCommit(bool noOwned = false)
{
    var commit = new CharacterVoiceCommitState(); var preparation = CommitReceipt(noOwned);
    commit.BeginPass(null); commit.CompletePass(preparation); commit.BeginPass(preparation);
    commit.CompletePass(CommitReceipt(noOwned, preparation)); commit.SealForCommit(); return commit;
}
CharacterVoicePosition P(long frame, long sample, int row = 0) => new(frame, sample, row);
void Speak(CharacterVoiceSessionTrace trace, long sequence, int row, long startFrame, long startSample, long endFrame, long endSample, long completeFrame, long completeSample)
{
    trace.Request(sequence, asset.Identifier, P(startFrame, startSample, row), .1);
    trace.NativeStart(sequence, P(startFrame, startSample, row), asset.AssetId, 1, 1);
    trace.NativeEnd(sequence, P(endFrame, endSample, row)); trace.DelayComplete(sequence, P(completeFrame, completeSample, row));
}
void Run(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); } }
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, actual {actual}"); }
void Throws(string code, Action action)
{
    try { action(); } catch (CharacterVoiceTraceException error) { Equal(code, error.Code); return; }
    throw new Exception("Expected " + code);
}
sealed class EmptyLease : IDisposable { public void Dispose() { } }
