using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using AAVideoExport.Core;
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
Test("installed More Effects 1.6.7 assembly identity is explicitly recognized", () =>
{
    Check(MoreEffectsCompatibility.IsSupportedBuild(
        new Version(1, 6, 7, 0), Guid.Parse("7faa0394-68bb-4307-9736-80ceb3c87b57")),
        "active 1.6.7 build should be recognized");
    Check(!MoreEffectsCompatibility.IsSupportedBuild(
        new Version(1, 6, 7, 0), Guid.Parse("c7e07c8f-69a8-4ed7-8c66-c6052711cf27")),
        "unknown build identity must fail closed");
});
Test("strict mod admission allows absence or full coverage and rejects incomplete builds", () =>
{
    void Allows(bool loaded, bool full, string status, string reason) =>
        StrictModAdmission.EnsureCompatible(loaded, full, status, reason);
    ExportException Rejects(bool loaded, bool full, string status, string reason)
    {
        try { Allows(loaded, full, status, reason); }
        catch (ExportException error) { return error; }
        throw new Exception("Expected strict clock coverage rejection");
    }

    Allows(false, false, "stopped", "");
    Allows(true, true, "full", "");
    foreach (string status in new[] { "partial", "unsupported", "character-only" })
    {
        var error = Rejects(true, false, status, "fixture reason");
        Check(error.Code == "strict_mod_clock_unsupported", "wrong strict admission error code");
        Check(error.Message.Contains(status, StringComparison.Ordinal) && error.Message.Contains("fixture reason", StringComparison.Ordinal),
            "strict admission must preserve status diagnostics");
        Check(ExportErrorText.Describe(error).Contains("逐帧对齐", StringComparison.Ordinal), "strict admission must have localized user text");
    }
    var inconsistent = Rejects(true, true, "unsupported", "stale coverage flag");
    Check(inconsistent.Code == "strict_mod_clock_unsupported", "explicit unsupported status must override stale coverage flag");
});
Test("exact Character Voice 1.6.8 identity is recognized as unsupported for strict timing", () =>
{
    Guid exact = Guid.Parse("3c455a03-837f-40d5-a719-281df650be68");
    Check(!CharacterVoiceCompatibility.IsLoaded, "the fixture must begin without Character Voice");
    Check(CharacterVoiceCompatibility.IsExactUnsupportedBuild(new Version(1, 6, 8, 0), exact),
        "active Character Voice 1.6.8 build should be recognized");
    Check(!CharacterVoiceCompatibility.IsExactUnsupportedBuild(new Version(1, 6, 8, 0), Guid.NewGuid()),
        "same-version Character Voice rebuild must not be rejected without exact identity");
    Check(!CharacterVoiceCompatibility.IsExactUnsupportedBuild(new Version(9, 9, 9, 0), exact),
        "unknown Character Voice version must not be rejected by name alone");
    try
    {
        CharacterVoiceCompatibility.ThrowIfExactUnsupported(true, "1.6.8", "fixture reason");
        throw new Exception("Expected exact Character Voice strict rejection");
    }
    catch (ExportException error)
    {
        Check(error.Code == "strict_character_voice_clock_unsupported", "wrong Character Voice strict error code");
        Check(error.Message.Contains("1.6.8", StringComparison.Ordinal) && error.Message.Contains("fixture reason", StringComparison.Ordinal),
            "Character Voice strict rejection lost exact build diagnostics");
        Check(ExportErrorText.Describe(error).Contains("人物配音支持", StringComparison.Ordinal), "Character Voice rejection must name the Mod in user text");
    }
    CharacterVoiceCompatibility.ThrowIfExactUnsupported(false, "1.6.8", "");
});
Test("supported build diagnostics report the accepted assembly version", () =>
{
    string message = MoreEffectsCompatibility.AdaptationEnabledMessage(new Version(1, 6, 7, 0));
    Check(message.Contains("1.6.7", StringComparison.Ordinal), "success diagnostics must identify the accepted build");
    Check(!message.Contains("1.4.1", StringComparison.Ordinal), "success diagnostics must not report an older build");
    Check(message.Contains("CharacterPresetRuntime", StringComparison.Ordinal), "character diagnostics must name their exact scope");
    Check(MoreEffectsCompatibility.ActionTimelineEnabledMessage(new Version(1, 6, 7, 0)).Contains("PlayerActionTimelineRuntime", StringComparison.Ordinal),
        "action diagnostics must name their exact scope");
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

Test("action timeline Now patch is narrow and follows the video clock", () =>
{
    var harmony = new Harmony("aa.action-timeline.regression.fixture");
    var now = typeof(ActionTimelineRuntimeFixture).GetMethod(nameof(ActionTimelineRuntimeFixture.Now), BindingFlags.Static | BindingFlags.NonPublic)!;
    MoreEffectsCompatibility.BindActionTimelineAndPatch(harmony, typeof(ActionTimelineRuntimeFixture), now, typeof(ActionTimelineRuntimeFixture));
    try
    {
        Check(MoreEffectsCompatibility.ActionTimelineClockInstalled, "action timeline adapter was not marked ready");
        Check(Harmony.GetPatchInfo(now)!.Prefixes.Any(p => p.owner == harmony.Id), "Now was not patched by the adapter");
        double video = 0;
        MoreEffectsCompatibility.BeginExport(() => video);
        ActionTimelineRuntimeFixture.Arm(3);
        video = 2;
        Check(ActionTimelineRuntimeFixture.Tick() == ActionTimelineStatus.Running, "gate advanced on the wrong video time");
        video = 3;
        Check(ActionTimelineRuntimeFixture.Tick() == ActionTimelineStatus.Completed, "gate did not advance at the planned video time");
        MoreEffectsCompatibility.EndExport();
    }
    finally
    {
        MoreEffectsCompatibility.Shutdown();
        harmony.UnpatchSelf();
    }
});

Test("action timeline gate remains continuous after export cancellation", () =>
{
    var harmony = new Harmony("aa.action-timeline.continuity.fixture");
    var now = typeof(ActionTimelineRuntimeFixture).GetMethod(nameof(ActionTimelineRuntimeFixture.Now), BindingFlags.Static | BindingFlags.NonPublic)!;
    MoreEffectsCompatibility.BindActionTimelineAndPatch(harmony, typeof(ActionTimelineRuntimeFixture), now, typeof(ActionTimelineRuntimeFixture));
    try
    {
        double video = 0;
        MoreEffectsCompatibility.BeginExport(() => video);
        ActionTimelineRuntimeFixture.Arm(5);
        video = 1;
        MoreEffectsCompatibility.EndExport();
        double wall = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        Check(Math.Abs(ActionTimelineRuntimeFixture.DueAt - wall - 4) < 0.15, "gate deadline was not rebased to wall time");
        Check(ActionTimelineRuntimeFixture.Tick() == ActionTimelineStatus.Running, "rebased gate completed before its remaining duration");
        ActionTimelineRuntimeFixture.Cancel();
        Check(ActionTimelineRuntimeFixture.Tick() == ActionTimelineStatus.Cancelled, "gate cancellation did not remain cancelled");
    }
    finally
    {
        MoreEffectsCompatibility.Shutdown();
        harmony.UnpatchSelf();
    }
});

Test("queued actions retain remaining time and elapsed origin after cancellation and a second export", () =>
{
    var harmony = new Harmony("aa.action-queue.continuity.fixture");
    var now = typeof(ActionTimelineRuntimeFixture).GetMethod(nameof(ActionTimelineRuntimeFixture.Now), BindingFlags.Static | BindingFlags.NonPublic)!;
    MoreEffectsCompatibility.BindActionTimelineAndPatch(harmony, typeof(ActionTimelineRuntimeFixture), now, typeof(ActionTimelineRuntimeFixture));
    try
    {
        double video = 0;
        MoreEffectsCompatibility.BeginExport(() => video);
        ActionTimelineRuntimeFixture.Enqueue(10, 2, 5);
        video = 1;
        MoreEffectsCompatibility.EndExport();
        Near(ActionTimelineRuntimeFixture.Elapsed(10), 1, 0.15);
        Check(ActionTimelineRuntimeFixture.TakeDue().Count == 0, "pending action fired during cancellation");

        video = 100;
        MoreEffectsCompatibility.BeginExport(() => video);
        video = 101.1;
        var due = ActionTimelineRuntimeFixture.TakeDue();
        Check(due.Count == 1 && due[0].Sequence == 10 && due[0].Index == 0, "first queued action lost its remaining delay");
        video = 104.1;
        due = ActionTimelineRuntimeFixture.TakeDue();
        Check(due.Count == 1 && due[0].Index == 1, "second queued action lost its remaining delay");
        MoreEffectsCompatibility.EndExport();
    }
    finally
    {
        ActionTimelineRuntimeFixture.ClearQueue();
        MoreEffectsCompatibility.Shutdown();
        harmony.UnpatchSelf();
    }
});

Test("invalid queued time rejects restoration before character or gate state changes", () =>
{
    var harmony = new Harmony("aa.action-queue.invalid.fixture");
    var now = typeof(ActionTimelineRuntimeFixture).GetMethod(nameof(ActionTimelineRuntimeFixture.Now), BindingFlags.Static | BindingFlags.NonPublic)!;
    var action = new ActionRuntime();
    MoreEffectsCompatibility.BindAndPatch(harmony, typeof(ActionRuntime), begin, tick);
    MoreEffectsCompatibility.BindActionTimelineAndPatch(harmony, typeof(ActionTimelineRuntimeFixture), now, typeof(ActionTimelineRuntimeFixture));
    try
    {
        double video = 0;
        MoreEffectsCompatibility.BeginExport(() => video);
        action.Begin("invalid", "invalid", 1);
        ActionTimelineRuntimeFixture.Arm(5);
        ActionTimelineRuntimeFixture.Enqueue(20, double.NaN);
        long started = action.StartedAt(1);
        double due = ActionTimelineRuntimeFixture.DueAt;
        video = 1;
        try { MoreEffectsCompatibility.EndExport(); throw new Exception("invalid queue was silently rebased"); }
        catch (InvalidOperationException) { }
        Check(action.StartedAt(1) == started, "character start changed before validation completed");
        Near(ActionTimelineRuntimeFixture.DueAt, due);
        Near(ActionTimelineRuntimeFixture.Elapsed(20), 1, 0.15);
        Check(!MoreEffectsCompatibility.FullClockCoverage && MoreEffectsCompatibility.Status == "restoration-failed",
            "failed restoration remained eligible for strict export");
        bool refused = false;
        try { MoreEffectsCompatibility.BeginExport(() => video); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "a failed restoration allowed another capture to start");
        ActionTimelineRuntimeFixture.ClearQueue();
        MoreEffectsCompatibility.EndExport();
        action.Render(1);
        Near(action.Elapsed(1), 1, 0.15);
    }
    finally
    {
        ActionTimelineRuntimeFixture.ClearQueue();
        MoreEffectsCompatibility.Shutdown();
        harmony.UnpatchSelf();
    }
});

Test("failed queue write rolls back every attempted character gate and queue change", () =>
{
    var harmony = new Harmony("aa.action-queue.rollback.fixture");
    var now = typeof(ActionTimelineRuntimeFixture).GetMethod(nameof(ActionTimelineRuntimeFixture.Now), BindingFlags.Static | BindingFlags.NonPublic)!;
    var action = new ActionRuntime();
    MoreEffectsCompatibility.BindAndPatch(harmony, typeof(ActionRuntime), begin, tick);
    MoreEffectsCompatibility.BindActionTimelineAndPatch(harmony, typeof(ActionTimelineRuntimeFixture), now, typeof(ActionTimelineRuntimeFixture));
    var adapterField = typeof(MoreEffectsCompatibility).GetField("_queueDueAtField", BindingFlags.Static | BindingFlags.NonPublic)!;
    var originalField = (FieldInfo)adapterField.GetValue(null)!;
    try
    {
        double video = 0;
        MoreEffectsCompatibility.BeginExport(() => video);
        action.Begin("rollback", "rollback", 1);
        ActionTimelineRuntimeFixture.Arm(5);
        ActionTimelineRuntimeFixture.Enqueue(30, 2, 5);
        long started = action.StartedAt(1);
        double due = ActionTimelineRuntimeFixture.DueAt;
        double[] pending = ActionTimelineRuntimeFixture.QueueDeadlines();
        adapterField.SetValue(null, new FailAfterWriteFieldInfo(originalField, 2));
        video = 1;
        try { MoreEffectsCompatibility.EndExport(); throw new Exception("injected write failure was swallowed"); }
        catch (AggregateException error) { Check(error.InnerExceptions.Any(e => e is InjectedWriteException), "wrong rollback failure"); }
        Check(action.StartedAt(1) == started, "character start was not rolled back");
        Near(ActionTimelineRuntimeFixture.DueAt, due);
        Check(ActionTimelineRuntimeFixture.QueueDeadlines().SequenceEqual(pending), "queue deadlines were not rolled back");
        Near(ActionTimelineRuntimeFixture.Elapsed(30), 1, 0.15);
        Check(MoreEffectsCompatibility.Status == "restoration-failed", "write failure did not close strict admission");

        adapterField.SetValue(null, originalField);
        MoreEffectsCompatibility.EndExport();
        action.Render(1);
        Near(action.Elapsed(1), 1, 0.15);
        Near(ActionTimelineRuntimeFixture.Elapsed(30), 1, 0.15);
        Near(ActionTimelineRuntimeFixture.QueueDeadlines()[0] - ActionTimelineRuntimeFixture.Now(), 1, 0.15);
    }
    finally
    {
        adapterField.SetValue(null, originalField);
        ActionTimelineRuntimeFixture.ClearQueue();
        MoreEffectsCompatibility.Shutdown();
        harmony.UnpatchSelf();
    }
});

Test("missing queue restoration shape installs no action clock prefix", () =>
{
    var harmony = new Harmony("aa.action-queue.shape.fixture");
    var now = typeof(ActionTimelineRuntimeFixture).GetMethod(nameof(ActionTimelineRuntimeFixture.Now), BindingFlags.Static | BindingFlags.NonPublic)!;
    try
    {
        try
        {
            MoreEffectsCompatibility.BindActionTimelineAndPatch(harmony, typeof(ActionTimelineRuntimeFixture), now, typeof(MissingQueueFixture));
            throw new Exception("accepted a queue without restoration metadata");
        }
        catch (MissingFieldException) { }
        Check(!MoreEffectsCompatibility.ActionTimelineClockInstalled, "incomplete queue binding became ready");
        Check(Harmony.GetPatchInfo(now)?.Prefixes.Any(p => p.owner == harmony.Id) != true, "failed binding left an active clock prefix");
    }
    finally { MoreEffectsCompatibility.Shutdown(); harmony.UnpatchSelf(); }
});

Test("Now methods with extra behavior are rejected before patching", () =>
{
    var harmony = new Harmony("aa.action-now.shape.fixture");
    var now = typeof(ChangedActionNowFixture).GetMethod(nameof(ChangedActionNowFixture.Now), BindingFlags.Static | BindingFlags.NonPublic)!;
    try
    {
        try
        {
            MoreEffectsCompatibility.BindActionTimelineAndPatch(harmony, typeof(ChangedActionNowFixture), now, typeof(ActionTimelineRuntimeFixture));
            throw new Exception("accepted a changed Now method");
        }
        catch (NotSupportedException) { }
        Check(!MoreEffectsCompatibility.ActionTimelineClockInstalled, "changed Now became ready");
        Check(Harmony.GetPatchInfo(now)?.Prefixes.Any(p => p.owner == harmony.Id) != true, "changed Now was patched");
    }
    finally { MoreEffectsCompatibility.Shutdown(); harmony.UnpatchSelf(); }
});

Test("action timeline shape changes fail closed", () =>
{
    var harmony = new Harmony("aa.action-timeline.shape.fixture");
    var now = typeof(BadActionTimelineRuntimeFixture).GetMethod(nameof(BadActionTimelineRuntimeFixture.Now), BindingFlags.Static | BindingFlags.NonPublic)!;
    try
    {
        try { MoreEffectsCompatibility.BindActionTimelineAndPatch(harmony, typeof(BadActionTimelineRuntimeFixture), now, typeof(ActionTimelineRuntimeFixture)); throw new Exception("accepted malformed gate"); }
        catch (MissingFieldException) { }
        catch (NotSupportedException) { }
        Check(!MoreEffectsCompatibility.ActionTimelineClockInstalled, "malformed action timeline was marked ready");
    }
    finally
    {
        MoreEffectsCompatibility.Shutdown();
        harmony.UnpatchSelf();
    }
});

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
Test("runtime admission inspects all exact Character Voice assemblies without rejecting unknown builds", () =>
{
    CharacterVoiceCompatibility.EnsureStrictExportAllowed();
    LoadIdentityFixture("Rukari.CharacterVoice", new Version(1, 6, 8, 0), Guid.NewGuid());
    Check(CharacterVoiceCompatibility.IsLoaded, "runtime detector did not see the named assembly");
    CharacterVoiceCompatibility.EnsureStrictExportAllowed();
    LoadIdentityFixture("Rukari.CharacterVoice", new Version(1, 6, 8, 0), Guid.Parse("3c455a03-837f-40d5-a719-281df650be68"));
    try { CharacterVoiceCompatibility.EnsureStrictExportAllowed(); throw new Exception("exact loaded build bypassed strict admission"); }
    catch (ExportException error) { Check(error.Code == "strict_character_voice_clock_unsupported", "wrong runtime admission error"); }
});
Test("runtime More Effects load state rejects a detected unsupported build", () =>
{
    Check(!MoreEffectsCompatibility.IsLoaded, "fixture unexpectedly began with More Effects loaded");
    LoadIdentityFixture("Rukari.MoreEffects", new Version(9, 9, 9, 0), Guid.NewGuid());
    var runtimeHarmony = new Harmony("aa.admission.runtime.fixture");
    try
    {
        MoreEffectsCompatibility.Initialize(runtimeHarmony, _ => { });
        Check(MoreEffectsCompatibility.IsLoaded && MoreEffectsCompatibility.Status == "unsupported" && !MoreEffectsCompatibility.FullClockCoverage,
            "unsupported loaded assembly did not produce fail-closed coverage state");
        try { MoreEffectsCompatibility.EnsureStrictExportAllowed(); throw new Exception("unsupported runtime build bypassed strict admission"); }
        catch (ExportException error) { Check(error.Code == "strict_mod_clock_unsupported", "wrong More Effects runtime admission error"); }
    }
    finally { MoreEffectsCompatibility.Shutdown(); runtimeHarmony.UnpatchSelf(); }
});

Console.WriteLine($"{passed} compatibility checks passed. Native AA scene rendering still requires separate acceptance.");

static void LoadIdentityFixture(string name, Version version, Guid module)
{
    using var assembly = Mono.Cecil.AssemblyDefinition.CreateAssembly(new Mono.Cecil.AssemblyNameDefinition(name, version), name + ".dll", Mono.Cecil.ModuleKind.Dll);
    assembly.MainModule.Mvid = module;
    using var bytes = new MemoryStream();
    assembly.Write(bytes);
    Assembly.Load(bytes.ToArray());
}

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
    internal long StartedAt(int slot) => _active[slot].StartedAt;
}

internal enum ActionTimelineStatus { Idle, AwaitingDispatch, Running, Completed, DispatchTimeout, CompletionTimeout, Cancelled }

internal sealed class ActionTimelineGateFixture
{
    public ActionTimelineStatus Status { get; private set; } = ActionTimelineStatus.Idle;
    public double DueAt { get; private set; }
    private double _dispatchBy;

    internal void Arm(double now, double duration)
    {
        Status = ActionTimelineStatus.Running;
        DueAt = now + duration;
        _dispatchBy = now + duration + 10;
    }

    internal ActionTimelineStatus Tick(double now)
    {
        if (Status == ActionTimelineStatus.Running && now >= DueAt) Status = ActionTimelineStatus.Completed;
        return Status;
    }

    internal void Cancel() => Status = ActionTimelineStatus.Cancelled;
}

internal static class ActionTimelineRuntimeFixture
{
    private static readonly ActionTimelineGateFixture Gate = new();
    private static readonly ActionQueueFixture SegmentQueue = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    internal static void Arm(double duration) => Gate.Arm(Now(), duration);
    internal static ActionTimelineStatus Tick() => Gate.Tick(Now());
    internal static void Cancel() => Gate.Cancel();
    internal static double DueAt => Gate.DueAt;
    internal static void Enqueue(long sequence, params double[] delays) => SegmentQueue.Enqueue(sequence, Now(), delays);
    internal static IReadOnlyList<ActionQueuedSegmentFixture> TakeDue() => SegmentQueue.TakeDue(Now());
    internal static double Elapsed(long sequence) => SegmentQueue.Elapsed(sequence, Now());
    internal static void ClearQueue() => SegmentQueue.Clear();
    internal static double[] QueueDeadlines() => SegmentQueue.Deadlines();
}

internal sealed record ActionQueuedSegmentFixture(long Sequence, int Index, double DueAt);

internal sealed class ActionQueueFixture
{
    private readonly object _gate = new();
    private readonly List<ActionQueuedSegmentFixture> _pending = new();
    private readonly Dictionary<long, double> _origins = new();

    internal void Enqueue(long sequence, double now, double[] delays)
    {
        lock (_gate)
        {
            _origins[sequence] = now;
            for (int index = 0; index < delays.Length; index++)
                _pending.Add(new ActionQueuedSegmentFixture(sequence, index, now + delays[index]));
        }
    }

    internal IReadOnlyList<ActionQueuedSegmentFixture> TakeDue(double now)
    {
        lock (_gate)
        {
            var due = _pending.Where(segment => segment.DueAt <= now).ToArray();
            _pending.RemoveAll(segment => segment.DueAt <= now);
            return due;
        }
    }

    internal double Elapsed(long sequence, double now) { lock (_gate) return now - _origins[sequence]; }
    internal void Clear() { lock (_gate) { _pending.Clear(); _origins.Clear(); } }
    internal double[] Deadlines() { lock (_gate) return _pending.Select(segment => segment.DueAt).ToArray(); }
}

internal static class MissingQueueFixture { }

internal static class ChangedActionNowFixture
{
    private static readonly ActionTimelineGateFixture Gate = new();
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency + 1;
}

internal sealed class InjectedWriteException : Exception { }

// Fault injection is confined to the field-write boundary. The production
// transaction and all actual managed state still execute unchanged.
internal sealed class FailAfterWriteFieldInfo : FieldInfo
{
    private readonly FieldInfo _field;
    private int _untilFailure;
    internal FailAfterWriteFieldInfo(FieldInfo field, int untilFailure) { _field = field; _untilFailure = untilFailure; }
    public override FieldAttributes Attributes => _field.Attributes;
    public override RuntimeFieldHandle FieldHandle => _field.FieldHandle;
    public override Type FieldType => _field.FieldType;
    public override Type? DeclaringType => _field.DeclaringType;
    public override string Name => _field.Name;
    public override Type? ReflectedType => _field.ReflectedType;
    public override object? GetValue(object? obj) => _field.GetValue(obj);
    public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, System.Globalization.CultureInfo? culture)
    {
        _field.SetValue(obj, value, invokeAttr, binder, culture);
        if (--_untilFailure == 0) throw new InjectedWriteException();
    }
    public override object[] GetCustomAttributes(bool inherit) => _field.GetCustomAttributes(inherit);
    public override object[] GetCustomAttributes(Type attributeType, bool inherit) => _field.GetCustomAttributes(attributeType, inherit);
    public override bool IsDefined(Type attributeType, bool inherit) => _field.IsDefined(attributeType, inherit);
}

internal sealed class BadActionTimelineGateFixture
{
    public double DueAt { get; private set; }
}

internal static class BadActionTimelineRuntimeFixture
{
    private static readonly BadActionTimelineGateFixture Gate = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
