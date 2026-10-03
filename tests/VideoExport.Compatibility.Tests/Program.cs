using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using AAVideoExport.Plugin;
using HarmonyLib;

int passed = 0;
void Test(string name, Action run) { run(); Console.WriteLine("PASS " + name); passed++; }
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Near(double actual, double expected, double tolerance = 0.00001)
    => Check(Math.Abs(actual - expected) <= tolerance, $"Expected {expected}, got {actual}");

Test("ordinary timestamps stay on wall time", () =>
{
    long wall = 1000;
    var clock = new AnimationTimestampClock(() => wall, 1000);
    Near(clock.Read(), 1000);
    wall += 1000;
    Near(clock.Read(), 2000);
});
foreach (double wallRate in new[] { 0.1, 1.0, 2.0 })
    Test($"video animation is independent of wall rate {wallRate}", () =>
    {
        long wall = 100000;
        double video = 0;
        var clock = new AnimationTimestampClock(() => wall, 30000);
        clock.Begin(() => video);
        long start = clock.Read();
        for (int frame = 1; frame <= 300; frame++)
        {
            wall += (long)(1000 * wallRate);
            video = frame / 30.0;
            Near((clock.Read() - start) / 30000.0, video);
        }
        long end = clock.Read();
        clock.End();
        Near(clock.Read(), end);
        wall += 30000;
        Near(clock.Read(), end + 30000);
        clock.ClearRebasedOffset();
        Near(clock.Read(), wall);
    });
Test("existing action continuity survives capture entry and cancellation", () =>
{
    long wall = 10000;
    double video = 0;
    var clock = new AnimationTimestampClock(() => wall, 1000);
    long actionStart = clock.Read();
    wall += 250;
    clock.Begin(() => video);
    video = 0.5; wall += 50;
    Near(clock.Read() - actionStart, 750);
    clock.End();
    actionStart -= clock.Offset;
    clock.ClearRebasedOffset();
    Near(clock.Read() - actionStart, 750);
    wall += 100;
    Near(clock.Read() - actionStart, 850);
    video = 10;
    clock.Begin(() => video);
    video += 0.5;
    Near(clock.Read() - actionStart, 1350);
    clock.End();
});
Test("invalid clock cannot start a session", () =>
{
    var clock = new AnimationTimestampClock(() => 10, 1000);
    try { clock.Begin(() => double.NaN); throw new Exception("Accepted invalid clock"); }
    catch (ArgumentOutOfRangeException) { }
    Check(!clock.Active, "invalid Begin must not arm the clock");
});
Test("unknown IL fails closed", () =>
{
    try { MoreEffectsCompatibility.Rewrite(new[] { new CodeInstruction(OpCodes.Ret) }).ToList(); throw new Exception("Accepted unknown body"); }
    catch (NotSupportedException) { }
});

var runtime = new ActionRuntime();
runtime.Begin("normal", "normal", 1);
runtime.Render(1);
Near(runtime.Elapsed(1), 0, 0.1);
var harmony = new Harmony("aa.compatibility.regression.fixture");
var type = typeof(ActionRuntime);
var begin = type.GetMethod(nameof(ActionRuntime.Begin), BindingFlags.Instance | BindingFlags.Public)!;
var tick = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!;
MoreEffectsCompatibility.BindAndPatch(harmony, type, begin, tick);
try
{
    Test("real Harmony patches both animation methods", () =>
    {
        Check(Harmony.GetPatchInfo(begin)!.Transpilers.Any(p => p.owner == harmony.Id), "Begin not patched");
        Check(Harmony.GetPatchInfo(tick)!.Transpilers.Any(p => p.owner == harmony.Id), "Tick not patched");
    });
    double video = 0;
    MoreEffectsCompatibility.BeginExport(() => video);
    runtime.Begin("capture", "capture", 2);
    long wallStart = Stopwatch.GetTimestamp();
    video = 1;
    runtime.Render(2);
    double sampled = runtime.Elapsed(2);
    Test("patched action advances one second while wall clock barely advances", () =>
    {
        Near(sampled, 1);
        Check((Stopwatch.GetTimestamp() - wallStart) / (double)Stopwatch.Frequency < 0.5, "wall time was changed");
    });
    Test("paused video clock keeps animation unchanged", () =>
    {
        runtime.Render(2);
        Near(runtime.Elapsed(2), sampled);
    });
    MoreEffectsCompatibility.EndExport();
    Test("cancel rebases readonly action start and clears synthetic offset", () =>
    {
        runtime.Render(2);
        Near(runtime.Elapsed(2), sampled, 0.1);
        Near((MoreEffectsCompatibility.ReadTimestamp() - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency, 0, 0.1);
    });
    Test("new ordinary actions after export retain native timing", () =>
    {
        runtime.Begin("after", "after", 3);
        runtime.Render(3);
        Near(runtime.Elapsed(3), 0, 0.1);
    });
    Test("second export does not inherit first-export elapsed time", () =>
    {
        video = 100;
        MoreEffectsCompatibility.BeginExport(() => video);
        runtime.Begin("again", "again", 4);
        video += 0.25;
        runtime.Render(4);
        Near(runtime.Elapsed(4), 0.25);
        MoreEffectsCompatibility.EndExport();
    });
    Test("unloading returns live actions to unpatched wall-time methods", () =>
    {
        runtime.Render(4);
        double before = runtime.Elapsed(4);
        Check(MoreEffectsCompatibility.Shutdown(), "clock restore failed");
        harmony.UnpatchSelf();
        runtime.Render(4);
        Near(runtime.Elapsed(4), before, 0.1);
    });
}
finally { MoreEffectsCompatibility.Shutdown(); harmony.UnpatchSelf(); }

if (args.Length != 0)
{
    // Sample all four actual supplied evaluators: no Unity engine is simulated here.
    var core = Assembly.LoadFrom(Path.GetFullPath(args[0]));
    var commandType = core.GetType("AzureArchive.VideoTools.Core.Characters.CharacterPresetCommand", true)!;
    var kindType = core.GetType("AzureArchive.VideoTools.Core.Characters.CharacterPresetKind", true)!;
    var sample = core.GetType("AzureArchive.VideoTools.Core.Characters.CharacterPresetEvaluator", true)!.GetMethod("Sample")!;
    foreach (string kind in new[] { "Sway", "Spin", "Headbutt", "Squash" })
        Test("supplied preset evaluator reaches completion: " + kind, () =>
        {
            var command = Activator.CreateInstance(commandType, 1, Enum.Parse(kindType, kind), kind == "Squash" ? 0.1f : 10f, 1f, 1, 1, 15f, 30f, 1000)!;
            var frame = sample.Invoke(null, new[] { command, (object)1.0 })!;
            Check((bool)frame.GetType().GetProperty("Completed")!.GetValue(frame)!, "preset must finish at video second 1");
            var early = sample.Invoke(null, new[] { command, (object)0.1 })!;
            Check(!(bool)early.GetType().GetProperty("Completed")!.GetValue(early)!, "wall-time sample would still be incomplete");
        });
}
Console.WriteLine($"{passed} compatibility checks passed. Native AA scene rendering still requires separate acceptance.");

internal sealed class ActionRuntime
{
    private sealed class ActionState
    {
        internal long StartedAt { get; }
        internal double Elapsed;
        internal ActionState(long startedAt) => StartedAt = startedAt;
    }
    private readonly Dictionary<int, ActionState> _active = new();
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Begin(string scene, string command, long slot)
    {
        _active[(int)slot] = new ActionState(Stopwatch.GetTimestamp());
        return true;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Tick(int slot, bool apply)
    {
        if (apply && _active.TryGetValue(slot, out var state))
            state.Elapsed = (Stopwatch.GetTimestamp() - state.StartedAt) / (double)Stopwatch.Frequency;
    }
    internal void Render(int slot) => Tick(slot, true);
    internal double Elapsed(int slot) => _active[slot].Elapsed;
}
