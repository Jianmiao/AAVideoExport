using AAVideoExport.Core;

var passed = 0;
var failed = 0;
Run("maps segments to exact DSP sample timestamps", () =>
{
    const double epoch = 1000;
    var scheduler = new StrictAudioSegmentScheduler(epoch, 48000);
    var result = scheduler.Schedule(new[] { new StrictAudioSegment("a", "clip", 4800, 9600, 0, false) });
    Equal(1, result.Count); Check(Math.Abs(result[0].DspStartSeconds - 1000.1) < 1e-12, "wrong DSP start");
    Check(Math.Abs(result[0].DspEndSeconds - 1000.2) < 1e-12, "wrong DSP end");
});
Run("allows overlapping OneShot but rejects non-OneShot overlap", () =>
{
    var scheduler = new StrictAudioSegmentScheduler(10, 48000);
    var baseSegment = new StrictAudioSegment("a", "clip", 0, 100, 0, false);
    var oneShot = new StrictAudioSegment("a", "sfx", 50, 150, 0, true);
    Equal(2, scheduler.Schedule(new[] { baseSegment, oneShot }).Count);
    Throws("audio_segment_overlap", () => scheduler.Schedule(new[] { baseSegment, new StrictAudioSegment("a", "clip2", 50, 150, 0, false) }));
});
Run("rejects invalid intervals and precision loss", () =>
{
    var scheduler = new StrictAudioSegmentScheduler(1, 48000);
    Throws("audio_segment_invalid", () => scheduler.Schedule(new[] { new StrictAudioSegment("a", "clip", 10, 10, 0, false) }));
    Throws("audio_clock_precision", () => new StrictAudioSegmentScheduler(1e15, 48000));
});
Run("keeps command output deterministic", () =>
{
    var scheduler = new StrictAudioSegmentScheduler(42, 44100);
    var segments = new[] { new StrictAudioSegment("a", "clip", 1837, 3675, 0, false), new StrictAudioSegment("a", "sfx", 2000, 2100, 0, true) };
    var first = scheduler.Schedule(segments); var second = scheduler.Schedule(segments);
    Check(first.Select(x => x.DspStartSeconds).SequenceEqual(second.Select(x => x.DspStartSeconds)), "schedule changed between identical runs");
});
Run("preserves volume and pitch on scheduled segments", () =>
{
    var scheduler = new StrictAudioSegmentScheduler(5, 48000);
    var segment = new StrictAudioSegment("a", "clip", 100, 500, 0, false, .35f, 1.25f);
    var scheduled = scheduler.Schedule(new[] { segment })[0];
    Equal(.35f, scheduled.Segment.Volume); Equal(1.25f, scheduled.Segment.Pitch);
});
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

void Run(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); } }
void Throws(string code, Action action) { try { action(); } catch (ExportException error) { if (error.Code == code) return; throw; } catch (ArgumentOutOfRangeException) when (code == "audio_clock_precision") { return; } throw new Exception("Expected " + code + "."); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
void Check(bool value, string message) { if (!value) throw new Exception(message); }
