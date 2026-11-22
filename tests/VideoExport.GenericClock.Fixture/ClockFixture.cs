using System.Diagnostics;

namespace VideoExport.GenericClock.Fixture;

// Synthetic managed Mod-shaped caller. The bridge must discover these clock
// calls by IL shape, not by assembly name. DateTime and Environment are not
// globally patchable on IL2CPP, so they exercise per-call-site rewriting.
public static class ClockFixture
{
    public static long ReadOnce() => Stopwatch.GetTimestamp();

    public static long ReadTwice() => Stopwatch.GetTimestamp() + Stopwatch.GetTimestamp();

    public static long ReadBranch(bool first) => first ? Stopwatch.GetTimestamp() : Stopwatch.GetTimestamp();

    public static long ReadUtcNowTicks() => System.DateTime.UtcNow.Ticks;

    public static int ReadTickCount() => System.Environment.TickCount;

    public static long ReadTickCount64() => System.Environment.TickCount64;
}
