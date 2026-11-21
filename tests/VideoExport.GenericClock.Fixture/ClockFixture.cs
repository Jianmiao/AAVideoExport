using System.Diagnostics;

namespace VideoExport.GenericClock.Fixture;

// Synthetic managed Mod-shaped caller. The bridge must discover these
// Stopwatch.GetTimestamp calls by IL shape, not by assembly name.
public static class ClockFixture
{
    public static long ReadOnce() => Stopwatch.GetTimestamp();

    public static long ReadTwice() => Stopwatch.GetTimestamp() + Stopwatch.GetTimestamp();

    public static long ReadBranch(bool first) => first ? Stopwatch.GetTimestamp() : Stopwatch.GetTimestamp();
}
