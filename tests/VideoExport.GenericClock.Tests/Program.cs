using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using AAVideoExport.Plugin;
using VideoExport.GenericClock.Fixture;

var harmony = new Harmony("aave.generic.clock.tests");
GenericStopwatchBridge.Initialize(harmony, _ => { });

var discovered = GenericStopwatchBridge.DiscoverTimestampCallers(typeof(ClockFixture).Assembly);
Check(discovered.Count == 3, "expected three caller methods");
Check(Count(discovered, "ReadOnce") == 1, "ReadOnce call count");
Check(Count(discovered, "ReadTwice") == 2, "ReadTwice call count");
Check(Count(discovered, "ReadBranch") == 2, "ReadBranch call count");

int installed = GenericStopwatchBridge.PatchAssembly(typeof(ClockFixture).Assembly);
Check(installed == 3, "all three callers installed");
Check(GenericStopwatchBridge.PatchedMethods.Count == 3, "patched method count");

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

static int Count(IReadOnlyList<(MethodInfo Method, int Calls)> values, string name) =>
    values.Where(v => v.Method.Name == name).Sum(v => v.Calls);

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
}

static void Equal(long expected, long actual)
{
    if (expected != actual) throw new Exception($"FAILED: expected {expected}, actual {actual}");
}
