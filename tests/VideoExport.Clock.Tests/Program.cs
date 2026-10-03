using AAVideoExport.Plugin;
using UnityEngine;

// Exercise the production clock against explicit time/readback boundaries.
// These tests cannot prove Harmony intercepts Unity's native coroutine calls.
var tests = new (string Name, Action Run)[]
{
    ("waits outside an export retain native ownership", () =>
    {
        var old = new WaitForSecondsRealtime(10) { m_WaitUntilTime = 321 };
        Check(!NativeExportClock.TryKeepWaiting(old, out _));
        Equal(321, old.m_WaitUntilTime);
        Equal(10, old.waitTime);
    }),
    ("first poll adopts an existing native deadline without restarting it", () =>
    {
        Start();
        Time.realtimeSinceStartup = 315;
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
        Start();
        Time.realtimeSinceStartup = 500;
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
        Start();
        Time.realtimeSinceStartup = 1000;
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
    finally { NativeExportClock.Restore(); }
}
Console.WriteLine($"{passed}/{tests.Length} passed (clock logic; native hook dispatch requires AA acceptance)");

static void Start()
{
    Time.frameCount++;
    Time.realtimeSinceStartup = 0;
    ClockInput.Now = 100;
    NativeExportClock.Begin(100, 1f / 60, () => ClockInput.Now);
}
static void Advance(float now) { ClockInput.Now = now; Time.frameCount++; }
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
    internal sealed class WaitForSecondsRealtime
    {
        private static long _next;
        public IntPtr Pointer { get; } = new IntPtr(++_next);
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
