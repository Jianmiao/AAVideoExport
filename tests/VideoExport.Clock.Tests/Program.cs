using AAVideoExport.Plugin;
using UnityEngine;



// Exercise the production clock against explicit time/readback boundaries.
// These tests cannot prove Harmony intercepts Unity's native coroutine calls.
var tests = new (string Name, Action Run)[]
{
    ("native authored 8100ms wait completes at243 frames instead of rounding float seconds upward", () =>
    {
        Start();
        var player = new Test();
        var host = new ExportHost(player) { Capturing = true, CaptureFps = 30 };
        ExportHost.Current = host;
        var animation = new ScenarioAnimation.WaitAnimation(8100 / 1000f, player);
        RememberParsedWaits(player, "#wait;8100", animation);
        var iterator = new ScenarioAnimation.WaitAnimation._WaitTask_d__4 { __4__this = animation,
            delayInSeconds = animation.delayInSeconds, __2__current = new WaitForSeconds(animation.delayInSeconds) };
        InvokeScaledWaitPostfix(iterator, true);
        var converted = iterator.__2__current as WaitForSecondsRealtime
            ?? throw new Exception("Owned scaled native wait was not converted.");
        host.CapturedFrame = 242; Check(Poll(converted));
        host.CapturedFrame = 243; Check(!Poll(converted));
    }),
    ("authored integer wait retains milliseconds beyond float integer precision", () =>
    {
        Start();
        var player = new Test();
        var host = new ExportHost(player) { Capturing = true, CaptureFps = 1000, CapturedFrame = 700 };
        ExportHost.Current = host;
        var animation = new ScenarioAnimation.WaitAnimation(16777217 / 1000f, player);
        RememberParsedWaits(player, "#WAIT;16777217", animation);
        var converted = ConvertWait(animation);
        host.CapturedFrame = 16777916; Check(Poll(converted));
        host.CapturedFrame = 16777917; Check(!Poll(converted));
    }),
    ("integer millisecond waits ceil fractional frames and retain one native yield for zero", () =>
    {
        Start();
        foreach (var sample in new (long Milliseconds, int Fps, long Frames)[]
        {
            (0, 30, 1), (1, 30, 1), (100, 30, 3), (333, 30, 10), (334, 30, 11),
            (8100, 30, 243), (8101, 30, 244), (8100, 60, 486), (2147483700, 30, 64424511),
            (100000000000000000, 60, 6000000000000000)
        })
        {
            long frame = 500;
            var wait = new WaitForSecondsRealtime(sample.Milliseconds / 1000f);
            NativeExportClock.RegisterFrameWaitMilliseconds(wait, sample.Milliseconds, frame, sample.Fps, () => frame);
            frame = 500 + sample.Frames - 1; Check(Poll(wait));
            frame++; Check(!Poll(wait));
        }
        Check(NativeExportClock.ActiveWaitCount == 0);
    }),
    ("native wait changed by another hook keeps its actual yielded duration", () =>
    {
        Start();
        var player = new Test();
        var host = new ExportHost(player) { Capturing = true, CaptureFps = 30 };
        ExportHost.Current = host;
        var animation = new ScenarioAnimation.WaitAnimation(8.1f, player);
        RememberParsedWaits(player, "#wait;8100", animation);
        var iterator = new ScenarioAnimation.WaitAnimation._WaitTask_d__4 { __4__this = animation,
            delayInSeconds = 8.2f, __2__current = new WaitForSeconds(8.2f) };
        InvokeScaledWaitPostfix(iterator, true);
        var converted = (WaitForSecondsRealtime)iterator.__2__current!;
        host.CapturedFrame = 243; Check(Poll(converted));
        host.CapturedFrame = 246; Check(!Poll(converted));
    }),
    ("mismatched authored and native wait lists never assign an unrelated integer duration", () =>
    {
        Start();
        var player = new Test();
        var host = new ExportHost(player) { Capturing = true, CaptureFps = 30 };
        ExportHost.Current = host;
        var animation = new ScenarioAnimation.WaitAnimation(8.1f, player);
        RememberParsedWaits(player, "#wait;8100\n#wait;8101", animation);
        var converted = ConvertWait(animation);
        host.CapturedFrame = 243; Check(Poll(converted));
        host.CapturedFrame = 244; Check(!Poll(converted));
    }),
    ("distinct integer durations sharing one native float are not rebound by list order", () =>
    {
        Start();
        var player = new Test();
        var host = new ExportHost(player) { Capturing = true, CaptureFps = 1000 };
        ExportHost.Current = host;
        var first = new ScenarioAnimation.WaitAnimation(16777216 / 1000f, player);
        var second = new ScenarioAnimation.WaitAnimation(16777217 / 1000f, player);
        Check(first.delayInSeconds == second.delayInSeconds);
        RememberParsedWaits(player, "#wait;16777216\n#wait;16777217", first, second);
        var converted = ConvertWait(first);
        host.CapturedFrame = 16777216; Check(Poll(converted));
        host.CapturedFrame = 16777217; Check(!Poll(converted));
    }),
    ("concrete row binding preserves ordered overlapping native authored waits", () =>
    {
        Start();
        var player = new Test();
        var host = new ExportHost(player) { Capturing = true, CaptureFps = 30 };
        ExportHost.Current = host;
        var first = new ScenarioAnimation.WaitAnimation(8.1f, player);
        var second = new ScenarioAnimation.WaitAnimation(8.101f, player);
        RememberParsedWaits(player, "#wait;8100\r\n#wait;8101", first, second);
        var secondWait = ConvertWait(second);
        var firstWait = ConvertWait(first);
        host.CapturedFrame = 243; Check(!Poll(firstWait)); Check(Poll(secondWait));
        host.CapturedFrame = 244; Check(!Poll(secondWait));
    }),
    ("authored bindings do not cross capture restoration", () =>
    {
        Start();
        var player = new Test();
        var host = new ExportHost(player) { Capturing = true, CaptureFps = 30 };
        ExportHost.Current = host;
        var animation = new ScenarioAnimation.WaitAnimation(8.1f, player);
        RememberParsedWaits(player, "#wait;8100", animation);
        NativeExportClock.Restore();
        Start();
        var converted = ConvertWait(animation);
        host.CapturedFrame = 243; Check(Poll(converted));
        host.CapturedFrame = 244; Check(!Poll(converted));
    }),
    ("foreign owners and finished iterators retain the native yield object", () =>
    {
        Start();
        var player = new Test();
        ExportHost.Current = new ExportHost(player) { Capturing = true, CaptureFps = 30 };
        var foreign = new Test();
        var animation = new ScenarioAnimation.WaitAnimation(8.1f, foreign);
        RememberParsedWaits(foreign, "#wait;8100", animation);
        var original = new WaitForSeconds(8.1f);
        var iterator = new ScenarioAnimation.WaitAnimation._WaitTask_d__4 { __4__this = animation,
            delayInSeconds = 8.1f, __2__current = original };
        InvokeScaledWaitPostfix(iterator, true);
        Check(ReferenceEquals(original, iterator.__2__current));
        iterator.__4__this = new ScenarioAnimation.WaitAnimation(8.1f, player);
        InvokeScaledWaitPostfix(iterator, false);
        Check(ReferenceEquals(original, iterator.__2__current));
        Check(NativeExportClock.RegisteredWaitCount == 0);
    }),
    ("authored integer wait cancellation preserves its exact remaining frame duration", () =>
    {
        Start();
        long frame = 100;
        var wait = new WaitForSecondsRealtime(8.1f);
        NativeExportClock.RegisterFrameWaitMilliseconds(wait, 8100, 0, 30, () => frame);
        Time.realtimeSinceStartup = 1000;
        NativeExportClock.Restore();
        Equal(1000 + 143f / 30, wait.m_WaitUntilTime);
        Check(NativeExportClock.ActiveWaitCount == 0);
    }),
    ("converted scaled waits end on exact authored frames independent of float time origin", () =>
    {
        StartAt(12345);
        long frame = 360;
        var wait = new WaitForSecondsRealtime(2);
        NativeExportClock.RegisterFrameWait(wait, 2, frame, 30, () => frame);
        Advance(1e8f);
        frame = 419; Check(Poll(wait));
        Time.realtimeSinceStartup = 9e8f;
        frame = 420; Check(!Poll(wait));
        Check(NativeExportClock.ActiveWaitCount == 0);
    }),
    ("authored tenth-second waits consume three frames at30fps", () =>
    {
        Start();
        long frame = 0;
        var wait = new WaitForSecondsRealtime(.1f);
        NativeExportClock.RegisterFrameWait(wait, .1f, 0, 30, () => frame);
        frame = 2; Check(Poll(wait));
        frame = 3; Check(!Poll(wait));
    }),
    ("converted scaled wait cancellation preserves remaining seconds", () =>
    {
        Start();
        long frame = 30;
        var wait = new WaitForSecondsRealtime(2);
        NativeExportClock.RegisterFrameWait(wait, 2, 0, 30, () => frame);
        Time.realtimeSinceStartup = 1000;
        NativeExportClock.Restore();
        Equal(1001, wait.m_WaitUntilTime);
        Check(!NativeExportClock.TryKeepWaiting(wait, out _));
    }),
    ("a pre-existing wait adopted after a wall stall retains its video deadline", () =>
    {
        StartAt(315);
        var old = new WaitForSecondsRealtime(10) { m_WaitUntilTime = 321 };
        Time.realtimeSinceStartup = 500;
        Advance(105);
        Check(Poll(old));
        Advance(106);
        Check(!Poll(old));
    }),
    ("waits outside an export retain native ownership", () =>
    {
        var old = new WaitForSecondsRealtime(10) { m_WaitUntilTime = 321 };
        Check(!NativeExportClock.TryKeepWaiting(old, out _));
        Equal(321, old.m_WaitUntilTime);
        Equal(10, old.waitTime);
    }),
    ("first poll adopts an existing native deadline without restarting it", () =>
    {
        StartAt(315);
        var old = new WaitForSecondsRealtime(10) { m_WaitUntilTime = 321 };
        Check(Poll(old));
        Check(NativeExportClock.RegisteredWaitCount == 1);
        Time.realtimeSinceStartup = 500;
        Advance(105);
        Check(Poll(old));
        NativeExportClock.Restore();
        Equal(501, old.m_WaitUntilTime);
    }),
    ("expired native waits complete immediately on adoption", () =>
    {
        StartAt(500);
        var old = new WaitForSecondsRealtime(10) { m_WaitUntilTime = 499 };
        Check(!Poll(old));
        Equal(-1, old.m_WaitUntilTime);
        Check(Poll(old));
        Advance(110);
        Check(!Poll(old));
    }),
    ("wait starts on first poll, not construction", () =>
    {
        Start();
        var wait = new WaitForSecondsRealtime(2);
        Check(NativeExportClock.RegisteredWaitCount == 0);
        Advance(105);
        Check(Poll(wait));
        Advance(106);
        Check(Poll(wait));
        Advance(107);
        Check(!Poll(wait));
    }),
    ("wall time and readback delay do not advance the video timer", () =>
    {
        Start();
        var wait = new WaitForSecondsRealtime(2);
        Check(Poll(wait));
        Time.realtimeSinceStartup = 10000;
        Advance(100.5f);
        Check(Poll(wait));
        Equal(100.5f, NativeExportClock.Now);
        Equal(1f / 60, NativeExportClock.Delta);
        Advance(102);
        Check(!Poll(wait));
    }),
    ("all getters in a Unity frame observe the same time", () =>
    {
        Start();
        Equal(100, NativeExportClock.Now);
        ClockInput.Now = 101;
        Equal(100, NativeExportClock.Now);
        Time.frameCount++;
        Equal(101, NativeExportClock.Now);
        ClockInput.Now = 102;
        Equal(101, NativeExportClock.Now);
    }),
    ("late adoption does not restart an already expired video wait", () =>
    {
        StartAt(315);
        var old = new WaitForSecondsRealtime(10) { m_WaitUntilTime = 321 };
        Time.realtimeSinceStartup = 316;
        Advance(110);
        Check(!Poll(old));
        Equal(-1, old.m_WaitUntilTime);
    }),
    ("changing duration does not restart an active wait", () =>
    {
        Start();
        var wait = new WaitForSecondsRealtime(2);
        Check(Poll(wait));
        wait.waitTime = 100;
        Advance(102);
        Check(!Poll(wait));
    }),
    ("completed and explicitly reset instances can be reused", () =>
    {
        Start();
        var wait = new WaitForSecondsRealtime(1);
        Check(Poll(wait));
        Advance(101);
        Check(!Poll(wait));
        Check(NativeExportClock.ActiveWaitCount == 0);
        Check(Poll(wait));
        Advance(102);
        Check(!Poll(wait));
        Check(NativeExportClock.ActiveWaitCount == 0);
        wait.waitTime = 5;
        Check(Poll(wait));
        Advance(103);
        wait.Reset();
        Check(Poll(wait));
        Advance(107);
        Check(Poll(wait));
        Advance(108);
        Check(!Poll(wait));
    }),
    ("cancel preserves remaining duration and releases the session", () =>
    {
        Start();
        var pending = new WaitForSecondsRealtime(10);
        var untouched = new WaitForSecondsRealtime(5);
        var completed = new WaitForSecondsRealtime(0);
        Check(Poll(pending));
        Check(!Poll(completed));
        Advance(105);
        Time.realtimeSinceStartup = 1000;
        NativeExportClock.Restore();
        Equal(1005, pending.m_WaitUntilTime);
        Equal(-1, untouched.m_WaitUntilTime);
        Equal(-1, completed.m_WaitUntilTime);
        Check(!NativeExportClock.IsActive);
        Check(!NativeExportClock.TryKeepWaiting(pending, out _));
        StartAt(1000);
        Check(NativeExportClock.RegisteredWaitCount == 0);
        Check(Poll(pending));
        Check(NativeExportClock.RegisteredWaitCount == 1);
        Advance(105);
        Check(!Poll(pending));
    }),
    ("overlapping capture scopes cannot steal pending deadlines", () =>
    {
        Start();
        var pending = new WaitForSecondsRealtime(10);
        Check(Poll(pending));
        bool rejected = false;
        try { NativeExportClock.Begin(200, 1f / 30, () => 200); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected);
        Advance(105);
        Check(Poll(pending));
        Advance(110);
        Check(!Poll(pending));
    }),
    ("long exports release completed waits and retain only pending deadlines", () =>
    {
        Start();
        var pending = new WaitForSecondsRealtime(1000);
        Check(Poll(pending));
        for (int i = 0; i < 500; i++)
        {
            var completed = new WaitForSecondsRealtime(0);
            Check(!Poll(completed));
            Check(NativeExportClock.ActiveWaitCount == 1);
        }
        Check(NativeExportClock.RegisteredWaitCount == 501);
        Check(NativeExportClock.PeakActiveWaitCount == 2);
        NativeExportClock.Restore();
        Check(NativeExportClock.ActiveWaitCount == 0);
    })
};

int passed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); passed++; }
    finally { NativeExportClock.Restore(); ExportHost.Current = null; }
}
Console.WriteLine($"{passed}/{tests.Length} passed (clock logic; native hook dispatch requires AA acceptance)");
UnifiedTimelineClockChecks.Run();

static void Start() => StartAt(0);
static void StartAt(float realtime)
{
    Time.frameCount++;
    Time.realtimeSinceStartup = realtime;
    ClockInput.Now = 100;
    NativeExportClock.Begin(100, 1f / 60, () => ClockInput.Now);
}
static void Advance(float now) { ClockInput.Now = now; Time.frameCount++; }
static void InvokeScaledWaitPostfix(object iterator, bool result) =>
    typeof(NativeScaledWaitHooks).GetMethod("Postfix", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(null, new object[] { iterator, result });
static void RememberParsedWaits(Test player, string script, params ScenarioAnimation.WaitAnimation[] animations)
{
    var parsed = new Utils.ScenarioUtil.ScriptInfo();
    parsed.scenarioAnimations.AddRange(animations);
    typeof(NativeAuthoredWaitHooks).GetMethod("Postfix", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(null, new object[] { new GenericScenarioExcel { ScriptKr = script }, player, parsed });
}
static WaitForSecondsRealtime ConvertWait(ScenarioAnimation.WaitAnimation animation)
{
    var iterator = new ScenarioAnimation.WaitAnimation._WaitTask_d__4 { __4__this = animation,
        delayInSeconds = animation.delayInSeconds, __2__current = new WaitForSeconds(animation.delayInSeconds) };
    InvokeScaledWaitPostfix(iterator, true);
    return (WaitForSecondsRealtime)iterator.__2__current!;
}
static bool Poll(WaitForSecondsRealtime wait)
{
    Check(NativeExportClock.TryKeepWaiting(wait, out bool waiting));
    return waiting;
}
static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
static void Equal(float expected, float actual)
{
    if (Math.Abs(expected - actual) > 0.0001f) throw new Exception($"Expected {expected}, actual {actual}.");
}
internal static class ClockInput { public static float Now; }

namespace UnityEngine
{
    internal static class Time
    {
        public static int frameCount;
        public static float realtimeSinceStartup;
    }
    internal sealed class WaitForSecondsRealtime : Il2CppSystem.Object
    {
        public float waitTime;
        public float m_WaitUntilTime = -1;
        public WaitForSecondsRealtime(float seconds) => waitTime = seconds;
        public void Reset()
        {
            m_WaitUntilTime = -1;
            // Stand in for the Reset postfix; native dispatch is not tested here.
            NativeExportClock.Reset(this);
        }
    }
    internal static class Debug
    {
        public static void LogWarning(string message) => Console.Error.WriteLine(message);
    }
}
