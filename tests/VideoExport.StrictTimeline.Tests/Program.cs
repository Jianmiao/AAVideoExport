using AAVideoExport.Core;

var tests = new (string Name, Action Run)[]
{
    ("end before current frame capture includes that frame once", () =>
    {
        var fence = new ExportEndFence();
        Equal(-1, fence.FrameCount);
        Check(!fence.IsComplete(4), "unobserved End cannot finish a pass");
        Equal(5, fence.ObserveEnd(4, 15, 14));
        Check(!fence.IsComplete(4), "must render End's boundary frame");
        Check(fence.IsComplete(5), "complete exactly at fence");
    }),
    ("end after current frame capture does not append another frame", () =>
    {
        var fence = new ExportEndFence();
        Equal(5, fence.ObserveEnd(5, 15, 15));
        Check(fence.IsComplete(5), "post-render End must finish without frame 6");
    }),
    ("equivalent End callbacks before and after render seal identical plans", () =>
    {
        var before = new ExportEndFence();
        var after = new ExportEndFence();
        Equal(before.ObserveEnd(99, 100, 99), after.ObserveEnd(100, 100, 100));
    }),
    ("duplicate End signals never move the existing fence", () =>
    {
        var fence = new ExportEndFence();
        Equal(12, fence.ObserveEnd(11, 22, 21));
        Equal(12, fence.ObserveEnd(12, 22, 22));
        Equal(12, fence.ObserveEnd(12, 23, 22));
    }),
    ("first-frame End still creates a one-frame export", () =>
    {
        var fence = new ExportEndFence();
        Equal(1, fence.ObserveEnd(0, 20, -1));
        Check(fence.IsComplete(1), "zero-length exports are not invented");
    }),
    ("capturing after sealed end fails instead of extending duration", () =>
    {
        var fence = new ExportEndFence();
        fence.ObserveEnd(5, 15, 15);
        Code("video_timeline_mismatch", () => fence.IsComplete(6));
    }),
    ("end fence refuses impossible counters", () =>
    {
        Throws<ArgumentOutOfRangeException>(() => new ExportEndFence().ObserveEnd(-1, 0, -1));
        Throws<ArgumentOutOfRangeException>(() => new ExportEndFence().IsComplete(-1));
        Throws<OverflowException>(() => new ExportEndFence().ObserveEnd(long.MaxValue, 1, 0));
    }),
    ("sealed plan binds source identity and capture clock", () =>
    {
        var plan = new StrictFramePlan("source + route + playback-settings fingerprint", 601, 60, 48000, 2);
        Equal(601, plan.FrameCount);
        Equal(480800, plan.AudioSampleFrameCount);
        Check(plan.DurationSeconds == 601d / 60, "duration comes from sealed frame count");
        plan.ValidateCaptureIdentity(plan.SourceIdentity, 60, 48000, 2);
        Code("timeline_source_changed", () => plan.ValidateCaptureIdentity("other source", 60, 48000, 2));
        Code("timeline_source_changed", () => plan.ValidateCaptureIdentity(plan.SourceIdentity, 30, 48000, 2));
        Code("timeline_source_changed", () => plan.ValidateCaptureIdentity(plan.SourceIdentity, 60, 44100, 2));
        Code("timeline_source_changed", () => plan.ValidateCaptureIdentity(plan.SourceIdentity, 60, 48000, 1));
    }),
    ("sealed plan rejects shorter longer and out-of-range video", () =>
    {
        var plan = new StrictFramePlan("source", 10, 60, 48000, 2);
        for (int i = 0; i < 10; i++) plan.ValidateNextVideoFrame(i);
        Code("video_timeline_mismatch", () => plan.ValidateNextVideoFrame(-1));
        Code("video_timeline_mismatch", () => plan.ValidateNextVideoFrame(10));
        plan.ValidateVideoEnd(10);
        plan.ValidateVideoComplete(10);
        Code("video_timeline_mismatch", () => plan.ValidateVideoEnd(9));
        Code("video_timeline_mismatch", () => plan.ValidateVideoEnd(11));
        Code("video_timeline_mismatch", () => plan.ValidateVideoComplete(9));
        Code("video_timeline_mismatch", () => plan.ValidateVideoComplete(11));
    }),
    ("sealed plan refuses a single missing or excess audio sample", () =>
    {
        var plan = new StrictFramePlan("source", 1, 24, 44100, 2);
        Equal(1837, plan.AudioSampleFrameCount);
        plan.ValidateAudioComplete(1837);
        Code("audio_timeline_mismatch", () => plan.ValidateAudioComplete(1836));
        Code("audio_timeline_mismatch", () => plan.ValidateAudioComplete(1838));
    }),
    ("rational sample count is exact for every supported frame and sample rate", () =>
    {
        foreach (int fps in new[] { 24, 25, 30, 50, 60 })
            foreach (int rate in new[] { 44100, 48000 })
                foreach (long frames in new[] { 1L, 11L, 6001L, 2592000L })
                {
                    var plan = new StrictFramePlan("source", frames, fps, rate, 2);
                    Equal((long)((decimal)frames * rate / fps), plan.AudioSampleFrameCount);
                    plan.ValidateVideoComplete(frames);
                }
    }),
    ("invalid plans fail before either pass begins", () =>
    {
        Throws<ArgumentException>(() => new StrictFramePlan("", 1, 60, 48000, 2));
        Throws<ArgumentException>(() => new StrictFramePlan("  ", 1, 60, 48000, 2));
        Throws<ArgumentOutOfRangeException>(() => new StrictFramePlan("source", 0, 60, 48000, 2));
        Throws<ArgumentOutOfRangeException>(() => new StrictFramePlan("source", -1, 60, 48000, 2));
        Throws<ArgumentOutOfRangeException>(() => new StrictFramePlan("source", 1, 0, 48000, 2));
        Throws<ArgumentOutOfRangeException>(() => new StrictFramePlan("source", 1, 60, 0, 2));
        Throws<ArgumentOutOfRangeException>(() => new StrictFramePlan("source", 1, 60, 48000, 0));
        Throws<OverflowException>(() => new StrictFramePlan("source", long.MaxValue, 60, 48000, 2));
    })
};

int failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"RESULT: {tests.Length - failed}/{tests.Length} strict timeline tests passed; no AA playback executed.");
return failed == 0 ? 0 : 1;

static void Equal(long expected, long actual)
{
    if (expected != actual) throw new Exception($"Expected {expected}; actual {actual}.");
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Code(string expected, Action action)
{
    try { action(); }
    catch (ExportException error) when (error.Code == expected) { return; }
    throw new Exception("Expected ExportException: " + expected);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
