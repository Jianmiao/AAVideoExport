using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using AAVideoExport.Plugin;
using VideoExport.GenericClock.Fixture;

var harmony = new Harmony("aave.generic.clock.tests");
GenericStopwatchBridge.Initialize(harmony, _ => { });

var discovered = GenericStopwatchBridge.DiscoverClockCallers(typeof(ClockFixture).Assembly);
Check(discovered.Count == 6, "expected six caller methods");
Check(Count(discovered, "ReadOnce") == 1, "ReadOnce call count");
Check(Count(discovered, "ReadTwice") == 2, "ReadTwice call count");
Check(Count(discovered, "ReadBranch") == 2, "ReadBranch call count");
Check(Count(discovered, "ReadUtcNowTicks") == 1, "ReadUtcNowTicks call count");
Check(Count(discovered, "ReadTickCount") == 1, "ReadTickCount call count");
Check(Count(discovered, "ReadTickCount64") == 1, "ReadTickCount64 call count");

int installed = GenericStopwatchBridge.PatchAssembly(typeof(ClockFixture).Assembly);
Check(installed == 6, "all six callers installed");
Check(GenericStopwatchBridge.PatchedMethods.Count == 6, "patched method count");

double video = 0;
GenericStopwatchBridge.BeginExport(() => video);
long origin = ClockFixture.ReadOnce();
long doubleRead = ClockFixture.ReadTwice();
Equal(origin * 2, doubleRead);
long branchA = ClockFixture.ReadBranch(true);
long branchB = ClockFixture.ReadBranch(false);
Equal(origin, branchA);
Equal(origin, branchB);

video = 0.5;
long half = ClockFixture.ReadOnce();
Check(half - origin >= Stopwatch.Frequency / 2 - Stopwatch.Frequency / 1000, "half-second virtual delta lower bound");
Check(half - origin <= Stopwatch.Frequency / 2 + Stopwatch.Frequency / 1000, "half-second virtual delta upper bound");
long utcHalf = ClockFixture.ReadUtcNowTicks();
Check(utcHalf - EpochTicks() >= TimeSpan.TicksPerSecond / 2 - 1000, "half-second DateTime lower bound");
Check(utcHalf - EpochTicks() <= TimeSpan.TicksPerSecond / 2 + 1000, "half-second DateTime upper bound");
Check(ClockFixture.ReadTickCount() == 500, "half-second TickCount");
Check(ClockFixture.ReadTickCount64() == 500, "half-second TickCount64");
long frozen = ClockFixture.ReadOnce();
Equal(half, frozen);

video = 1.0;
long one = ClockFixture.ReadOnce();
Check(one - origin >= Stopwatch.Frequency - Stopwatch.Frequency / 1000, "one-second virtual delta lower bound");
Check(one - origin <= Stopwatch.Frequency + Stopwatch.Frequency / 1000, "one-second virtual delta upper bound");
Check(GenericStopwatchBridge.CaptureReadCount >= 7, "capture reads counted");

GenericStopwatchBridge.EndExport();
Check(!GenericStopwatchBridge.Active, "bridge ended");

Console.WriteLine("GENERIC CLOCK TESTS: PASS (" + installed + " methods, " + discovered.Count + " discovered)");
return;

static int Count(IReadOnlyList<(MethodInfo Method, Dictionary<string, int> Calls)> values, string name) =>
    values.Where(v => v.Method.Name == name).Sum(v => v.Calls.Values.Sum());

static long EpochTicks() => new System.DateTime(2000, 1, 1, 0, 0, 0, System.DateTimeKind.Utc).Ticks;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
}

static void Equal(long expected, long actual)
{
    if (expected != actual) throw new Exception($"FAILED: expected {expected}, actual {actual}");
}
