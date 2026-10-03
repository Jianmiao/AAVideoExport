using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestPlaybackLaunchGate()
    {
        await Test("one export click loads its frozen story once after preparation", () => { TestOneClickLaunch(); return Task.CompletedTask; });
        await Test("a synchronous native start is accepted during the load callback", () => { TestSynchronousPlaybackStart(); return Task.CompletedTask; });
        await Test("cancelling preparation prevents late results from opening a story", () => { TestCancelledPreparation(); return Task.CompletedTask; });
        await Test("cancelling a load rejects late native starts including same-story retries", () => { TestCancelledNativeLoad(); return Task.CompletedTask; });
        await Test("loader failure preserves its error and permits a fresh export", () => { TestNativeLoadFailure(); return Task.CompletedTask; });
        await Test("missing selections and duplicate starts cannot change the chosen story", () => { TestLaunchValidation(); return Task.CompletedTask; });
        await Test("a stalled native load fails at the deadline and can be retried", () => { TestLaunchTimeout(); return Task.CompletedTask; });
        await Test("an old loader failure cannot cancel a newer reentrant launch", () => { TestReentrantLaunchFailure(); return Task.CompletedTask; });
    }

    private static void TestOneClickLaunch()
    {
        var gate = new PlaybackLaunchGate();
        var loaded = new List<string>();
        string selection = "story/original.aas";
        var ticket = gate.Begin(selection);
        selection = "story/changed-by-user.aas";
        Check(!gate.IsWaiting && loaded.Count == 0, "preparation must not load prematurely");
        Check(!gate.AcceptPlayback(ticket, gate.StoryKey), "unrelated native events before preparation are ignored");
        Check(gate.CompletePreparation(ticket, key =>
        {
            Check(gate.IsWaiting, "load callback already has a start gate");
            loaded.Add(key);
        }), "successful preparation launches without another user action");
        Equal("story/original.aas", loaded.Single(), "the clicked source, not a later selection, is loaded");
        Check(!gate.CompletePreparation(ticket, loaded.Add), "duplicate preparation is discarded");
        Check(!gate.AcceptPlayback(ticket, selection), "another story cannot become the export source");
        Check(gate.IsWaiting, "wrong story leaves the selected source pending");
        Check(gate.AcceptPlayback(ticket, "story/original.aas"), "selected native story begins capture");
        Check(!gate.IsWaiting, "native start completes launch");
        Check(!gate.AcceptPlayback(ticket, "story/original.aas"), "repeated native hooks cannot begin another capture");
        Check(!gate.CompletePreparation(ticket, loaded.Add), "late duplicate cannot relaunch after capture begins");
        Equal(1, loaded.Count, "exactly one native load");
    }

    private static void TestSynchronousPlaybackStart()
    {
        var gate = new PlaybackLaunchGate();
        var ticket = gate.Begin("story/immediate.aas");
        int captures = 0;
        Check(gate.CompletePreparation(ticket, key =>
        {
            if (gate.AcceptPlayback(ticket, key)) captures++;
            if (gate.AcceptPlayback(ticket, key)) captures++;
        }), "synchronous start completes the preparation handoff");
        Equal(1, captures, "start hooks initialize capture once");
        Check(!gate.IsWaiting, "synchronous native callback leaves no phantom wait");
        gate.CheckTimeout(500);
    }

    private static void TestCancelledPreparation()
    {
        var gate = new PlaybackLaunchGate();
        var oldTicket = gate.Begin("story/cancelled.aas");
        gate.Cancel();
        var newTicket = gate.Begin("story/retry.aas");
        int loads = 0;
        Check(!gate.CompletePreparation(oldTicket, _ => loads++), "late cancelled preparation is discarded");
        Equal(0, loads, "cancelled result must not mutate native playback");
        Equal("story/retry.aas", gate.StoryKey, "late result cannot replace the retry source");
        Check(gate.CompletePreparation(newTicket, _ => loads++), "fresh preparation remains valid");
        Equal(1, loads, "only the fresh export loads");
    }

    private static void TestCancelledNativeLoad()
    {
        var gate = new PlaybackLaunchGate();
        const string story = "story/same-source.aas";
        var oldTicket = gate.Begin(story);
        gate.CompletePreparation(oldTicket, _ => { });
        gate.Cancel();
        Check(!gate.IsWaiting && gate.StoryKey == "", "cancel leaves no pending source");
        Check(!gate.AcceptPlayback(oldTicket, story), "cancel rejects late native start");
        var newTicket = gate.Begin(story);
        gate.CompletePreparation(newTicket, _ => { });
        Check(!gate.AcceptPlayback(oldTicket, story), "same source does not make an old attempt eligible");
        Check(gate.AcceptPlayback(newTicket, story), "new attempt for the same source is eligible");
    }

    private static void TestNativeLoadFailure()
    {
        var gate = new PlaybackLaunchGate();
        var failedTicket = gate.Begin("story/broken.aas");
        var expected = new IOException("fixture load failure");
        try
        {
            gate.CompletePreparation(failedTicket, _ => throw expected);
            throw new InvalidOperationException("load failure was swallowed");
        }
        catch (IOException error) { Check(ReferenceEquals(expected, error), "the host receives the original failure"); }
        Check(!gate.IsWaiting, "failed load cannot remain stuck waiting");
        Check(!gate.AcceptPlayback(failedTicket, "story/broken.aas"), "failed attempt cannot later capture");
        var retry = gate.Begin("story/repaired.aas");
        Check(gate.CompletePreparation(retry, _ => { }), "failure permits a new export");
        Check(gate.AcceptPlayback(retry, "story/repaired.aas"), "retry reaches native capture");
    }

    private static void TestLaunchValidation()
    {
        var gate = new PlaybackLaunchGate();
        ThrowsCode("story_not_selected", () => gate.Begin(" \t"), "missing source");
        var ticket = gate.Begin("story/selected.aas");
        ThrowsCode("export_already_started", () => gate.Begin("story/other.aas"), "double click during preparation");
        Equal("story/selected.aas", gate.StoryKey, "double click cannot replace source");
        gate.CompletePreparation(ticket, _ => { });
        ThrowsCode("export_already_started", () => gate.Begin("story/other.aas"), "double click during load");
        Check(gate.AcceptPlayback(ticket, "story/selected.aas"), "original source remains eligible");
    }

    private static void TestLaunchTimeout()
    {
        var gate = new PlaybackLaunchGate();
        var ticket = gate.Begin("story/slow.aas");
        gate.CheckTimeout(500); // Encoder preparation has a separate timeout policy.
        gate.CompletePreparation(ticket, _ => { });
        gate.CheckTimeout(119.9);
        Check(gate.IsWaiting, "a loading story has its full startup budget");
        ThrowsCode("playback_start_timeout", () => gate.CheckTimeout(120), "stalled native loader");
        Check(!gate.IsWaiting, "timed-out launch cannot wait indefinitely");
        Check(!gate.AcceptPlayback(ticket, "story/slow.aas"), "timeout rejects a late native start");
        gate.CheckTimeout(121);
        var retry = gate.Begin("story/retry.aas");
        gate.CompletePreparation(retry, _ => { });
        Check(gate.AcceptPlayback(retry, "story/retry.aas"), "timeout permits retry");
    }

    private static void TestReentrantLaunchFailure()
    {
        var gate = new PlaybackLaunchGate();
        var first = gate.Begin("story/first.aas");
        long second = 0;
        try
        {
            gate.CompletePreparation(first, key =>
            {
                Check(gate.AcceptPlayback(first, key), "first callback accepts its player");
                second = gate.Begin("story/second.aas");
                throw new IOException("old loader failed after a new launch");
            });
        }
        catch (IOException) { }
        Equal("story/second.aas", gate.StoryKey, "failure only invalidates its own generation");
        Check(gate.CompletePreparation(second, _ => { }), "new preparation remains usable");
        Check(gate.AcceptPlayback(second, "story/second.aas"), "new native player remains eligible");
    }
}
