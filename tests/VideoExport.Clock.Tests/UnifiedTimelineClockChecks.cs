using System.Reflection;
using AAVideoExport.Plugin;
using UnityEngine;

// Source-linked NativeExportClock behavior. Only the Unity host time fields are
// stand-ins; no source text or invented timeline implementation is asserted.
internal static class UnifiedTimelineClockChecks
{
    private const BindingFlags Api = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Clock = typeof(NativeExportClock);

    internal static void Run()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("priming and repeated held updates contribute zero authored delta", () =>
            {
                long raw = 0; bool advancing = false;
                Begin(30, () => raw, () => advancing);
                double integrated = 0;
                for (int i = 0; i < 20; i++)
                {
                    Time.frameCount++; Time.realtimeSinceStartup += 10;
                    integrated += NativeExportClock.Delta;
                    Near(0, Frame()); Near(0, Seconds()); Near(0, NativeExportClock.Now);
                }
                Near(0, integrated);
            }),
            ("resume publishes frame zero once after a priming read in the same Unity frame", () =>
            {
                long raw = 0; bool advancing = false;
                Begin(30, () => raw, () => advancing);
                Near(0, NativeExportClock.Delta);
                advancing = true; Resume();
                Near(0, Frame()); Near(0, Seconds()); Near(1d / 30, NativeExportClock.Delta);
                Resume(); Near(1d / 30, NativeExportClock.Delta);
                Time.frameCount++; Near(0, NativeExportClock.Delta);
                Resume(); Near(0, NativeExportClock.Delta);
            }),
            ("all consumers see one snapshot despite capture committing during the Unity frame", () =>
            {
                long raw = 0; bool advancing = true; int reads = 0;
                Begin(60, () => { reads++; return raw; }, () => advancing); Resume();
                int baselineReads = reads;
                raw = 1;
                Near(0, Frame()); Near(0, Seconds()); Near(0, NativeExportClock.ReadTime());
                Near(1d / 60, NativeExportClock.ReadDelta()); Check(reads == baselineReads, "mid-frame providers were repolled");
                Time.frameCount++;
                Near(1d / 60, Seconds()); Near(1, Frame()); Near(1d / 60, NativeExportClock.Now);
                Near(1d / 60, NativeExportClock.Delta); Check(reads == baselineReads + 1, "new frame read more than once");
                raw = 2; Near(1, Frame()); Near(1d / 60, Seconds());
            }),
            ("held and drain frames freeze absolute time as well as delta", () =>
            {
                long raw = 0; bool advancing = true;
                Begin(30, () => raw, () => advancing); Resume();
                raw = 1; advancing = false; Time.frameCount++;
                Near(0, Frame()); Near(0, Seconds()); Near(0, NativeExportClock.Delta);
                for (int i = 0; i < 3; i++) { Time.frameCount++; Near(0, NativeExportClock.Delta); }
                advancing = true; Resume();
                Near(1, Frame()); Near(1d / 30, Seconds()); Near(1d / 30, NativeExportClock.Delta);
                Time.frameCount++; Near(0, NativeExportClock.Delta);
            }),
            ("many getter reads do not consume the one delta shared by separate animations", () =>
            {
                long raw = 0; bool advancing = true;
                Begin(24, () => raw, () => advancing); Resume();
                double animationA = 0, animationB = 0;
                for (int frame = 0; frame < 24; frame++)
                {
                    if (frame != 0) { raw = frame; Time.frameCount++; }
                    animationA += NativeExportClock.Delta;
                    animationB += NativeExportClock.ReadDelta();
                    Near(frame / 24d, Seconds());
                    for (int read = 0; read < 5; read++) Near(1d / 24, NativeExportClock.Delta);
                }
                Near(1, animationA); Near(1, animationB);
                Time.frameCount++; Near(0, NativeExportClock.Delta);
            }),
            ("integer native waits cannot expire from a mid-frame raw capture increment", () =>
            {
                long raw = 0; bool advancing = true;
                Begin(30, () => raw, () => advancing); Resume();
                var wait = new WaitForSecondsRealtime(.1f);
                NativeExportClock.RegisterFrameWaitMilliseconds(wait, 100, Frame(), 30, Frame);
                Check(Poll(wait), "wait did not begin");
                raw = 3; Check(Poll(wait), "raw counter bypassed shared snapshot");
                Time.frameCount++; Check(!Poll(wait), "wait did not complete at its shared frame boundary");
            }),
            ("cancel restores a pending wait using frozen logical position and real wall time", () =>
            {
                long raw = 0; bool advancing = true;
                Begin(30, () => raw, () => advancing); Resume();
                var wait = new WaitForSecondsRealtime(1);
                NativeExportClock.RegisterFrameWaitMilliseconds(wait, 1000, Frame(), 30, Frame);
                raw = 10; Time.frameCount++; Near(10, Frame());
                raw = 11; advancing = false; Time.frameCount++; Near(10, Frame());
                Time.realtimeSinceStartup = 1000; NativeExportClock.Restore();
                Near(1000 + 20d / 30, wait.m_WaitUntilTime, .0001);
                Check(!NativeExportClock.IsActive && !TimelineActive(), "restore retained virtual ownership");
            }),
            ("native scaled wait conversion takes its start and deadline from the shared frame", () =>
            {
                var player = new Test();
                var host = new ExportHost(player) { Capturing = true, CaptureFps = 30 };
                ExportHost.Current = host;
                Begin(30, () => host.CapturedFrame, () => true); Resume();
                host.CapturedFrame = 1; // End-of-render commit in this same Unity frame.
                var animation = new ScenarioAnimation.WaitAnimation(.1f, player);
                var iterator = new ScenarioAnimation.WaitAnimation._WaitTask_d__4
                {
                    __4__this = animation, delayInSeconds = .1f,
                    __2__current = new WaitForSeconds(.1f)
                };
                typeof(NativeScaledWaitHooks).GetMethod("Postfix", Api)!.Invoke(null, new object[] { iterator, true });
                var wait = (WaitForSecondsRealtime)iterator.__2__current!;
                host.CapturedFrame = 3;
                Check(Poll(wait), "wait observed an unlatched mid-frame commit");
                Time.frameCount++;
                Check(!Poll(wait), "converted wait did not start at shared frame zero");
            }),
            ("legacy begin keeps constant delta and existing per-Unity-frame timestamp semantics", () =>
            {
                float now = 100;
                NativeExportClock.Begin(100, 1f / 60, () => now);
                Check(!TimelineActive(), "legacy begin enabled timeline mode");
                Time.frameCount++; Near(100, NativeExportClock.Now); Near(1d / 60, NativeExportClock.Delta);
                now = 101; Near(100, NativeExportClock.Now);
                Time.frameCount++; Near(101, NativeExportClock.Now); Near(1d / 60, NativeExportClock.Delta);
            }),
            ("legacy delta reads preserve the original independent timestamp latch", () =>
            {
                float now = 100;
                NativeExportClock.Begin(100, 1f / 60, () => now);
                Time.frameCount++; Near(1d / 60, NativeExportClock.Delta);
                now = 101; Near(101, NativeExportClock.Now);
            })
        };
        int passed = 0;
        foreach (var test in tests)
        {
            try { test.Run(); Console.WriteLine("PASS unified clock " + test.Name); passed++; }
            finally { NativeExportClock.Restore(); ExportHost.Current = null; }
        }
        Console.WriteLine($"{passed}/{tests.Length} unified clock behavior groups passed");
    }

    private static void Begin(int fps, Func<long> readFrame, Func<bool> advancing)
    {
        Time.frameCount++;
        Call("BeginTimeline", fps, readFrame, advancing);
        Check(TimelineActive(), "timeline mode did not become active");
    }
    private static void Resume() => Call("ResumeTimelineFrame");
    private static long Frame() => Read<long>("Frame");
    private static double Seconds() => Read<double>("ElapsedSeconds");
    private static bool TimelineActive() => Read<bool>("IsTimelineActive");
    private static T Read<T>(string name) => (T)(Clock.GetProperty(name, Api)
        ?? throw new InvalidOperationException("Missing shared timeline property: " + name)).GetValue(null)!;
    private static void Call(string name, params object[] arguments)
    {
        var method = Clock.GetMethod(name, Api) ?? throw new InvalidOperationException("Missing shared timeline API: " + name);
        try { method.Invoke(null, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException!; }
    }
    private static bool Poll(WaitForSecondsRealtime wait)
    {
        Check(NativeExportClock.TryKeepWaiting(wait, out bool waiting), "native wait was not owned");
        return waiting;
    }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, double tolerance = 0.000001)
    {
        if (Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException($"Expected {expected:R}, got {actual:R}.");
    }
}
