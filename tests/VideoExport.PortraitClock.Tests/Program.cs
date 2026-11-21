using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AAVideoExport.Plugin;
using HarmonyLib;

var harmony = new Harmony("aave.portrait.clock.tests");
var tests = new (string Name, Action Run)[]
{
    ("exact version, MVID and hash are all required", () =>
    {
        var version = new Version(5, 2, 0, 0);
        var module = new Guid("984e19fc-d376-4133-bb4b-dcf8e30eb031");
        const string hash = "6DB74B5B48F13E2E9C81D650172A3561A41BD526EE1A0BF2D29604CCD93A2EB8";
        Check(FlandrePortraitClock.IsSupportedBuild(version, module, hash));
        Check(!FlandrePortraitClock.IsSupportedBuild(new Version(5, 2, 1, 0), module, hash));
        Check(!FlandrePortraitClock.IsSupportedBuild(version, Guid.NewGuid(), hash));
        Check(!FlandrePortraitClock.IsSupportedBuild(version, module, new string('0', 64)));
    }),
    ("absent optional mod permits export and initialization is reversible", () =>
    {
        FlandrePortraitClock.Initialize(harmony, _ => { });
        Check(!FlandrePortraitClock.IsLoaded && !FlandrePortraitClock.IsReady);
        FlandrePortraitClock.EnsureStrictExportAllowed();
        Check(FlandrePortraitClock.Shutdown());
    }),
    ("clock follows frames while real time advances independently", () =>
    {
        double wall = 100, video = 0;
        var clock = new PortraitRealtimeClock(() => wall);
        clock.Begin(() => video);
        wall = 400;
        Equal(100, clock.Read());
        video = 0.5;
        Equal(100.5, clock.Read());
        clock.End();
        Equal(100.5, clock.Read());
        wall = 400.25;
        Equal(100.75, clock.Read());
    }),
    ("pending animation survives cancellation and a second export without rewriting locals", () =>
    {
        double wall = 100, video = 0;
        var clock = new PortraitRealtimeClock(() => wall);
        double pendingAnimationStart = clock.Read();
        clock.Begin(() => video);
        video = 0.25;
        wall = 900;
        Equal(0.25, clock.Read() - pendingAnimationStart);
        clock.End();
        Equal(0.25, clock.Read() - pendingAnimationStart);
        wall += 0.25;
        Equal(0.5, clock.Read() - pendingAnimationStart);
        video = 50;
        clock.Begin(() => video);
        video += 0.25;
        wall += 400;
        Equal(0.75, clock.Read() - pendingAnimationStart);
        clock.End();
        wall += 0.25;
        Equal(1, clock.Read() - pendingAnimationStart);
    }),
    ("backwards and nonfinite video time are rejected, preserving last sample on end", () =>
    {
        double wall = 50, video = 0;
        var clock = new PortraitRealtimeClock(() => wall);
        clock.Begin(() => video);
        video = 2;
        Equal(52, clock.Read());
        video = 1;
        Throws<InvalidOperationException>(() => clock.Read());
        video = double.NaN;
        Throws<InvalidOperationException>(() => clock.Read());
        wall = 100;
        Throws<InvalidOperationException>(() => clock.End());
        Check(!clock.Active);
        Equal(52, clock.Read());
        wall = 101;
        Equal(53, clock.Read());
    }),
    ("duplicate begin preserves the first export", () =>
    {
        double video = 0;
        var clock = new PortraitRealtimeClock(() => 10);
        clock.Begin(() => video);
        Throws<InvalidOperationException>(() => clock.Begin(() => 200));
        video = 1;
        Equal(11, clock.Read());
    }),
    ("transpiler preserves labels and exception blocks and never mutates original IL", () =>
    {
        var source = new CodeInstruction(OpCodes.Call, Wall());
        source.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        var dm = new DynamicMethod("labels", typeof(void), Type.EmptyTypes);
        source.labels.Add(dm.GetILGenerator().DefineLabel());
        var rewritten = FlandrePortraitClock.RewriteCalls(new[] { source }, 1).Single();
        Check(Equals(source.operand, Wall()));
        Check(((MethodInfo)rewritten.operand).DeclaringType == typeof(FlandrePortraitClock));
        Check(rewritten.labels.SequenceEqual(source.labels) && rewritten.blocks.SequenceEqual(source.blocks));
    }),
    ("wrong call count and indirect references fail before any patch", () =>
    {
        Throws<NotSupportedException>(() => FlandrePortraitClock.RewriteCalls(new[] { new CodeInstruction(OpCodes.Call, Wall()) }, 2).ToList());
        Throws<NotSupportedException>(() => FlandrePortraitClock.RewriteCalls(new[] { new CodeInstruction(OpCodes.Ldftn, Wall()) }, 1).ToList());
        var targets = Targets();
        targets[targets.Keys.Last()] = 3;
        Throws<NotSupportedException>(() => FlandrePortraitClock.PatchValidatedTargets(harmony, targets));
        foreach (var method in targets.Keys) Check(Harmony.GetPatchInfo(method)?.Transpilers.Any() != true);
    }),
    ("a conflict on the last target rejects the entire patch set", () =>
    {
        var targets = Targets();
        var foreign = new Harmony("aave.portrait.foreign.fixture");
        var last = targets.Keys.Last();
        try
        {
            foreign.Patch(last, prefix: new HarmonyMethod(typeof(ProgramHelpers).GetMethod(nameof(ProgramHelpers.ForeignPrefix))));
            Throws<NotSupportedException>(() => FlandrePortraitClock.PatchValidatedTargets(harmony, targets));
            foreach (var method in targets.Keys) Check(Harmony.GetPatchInfo(method)?.Transpilers.Any() != true);
            Check(Harmony.GetPatchInfo(last)!.Prefixes.Single().owner == foreign.Id);
        }
        finally { foreign.UnpatchSelf(); }
    }),
    ("real managed Harmony patches cover both async state machines and leave unrelated reads raw", () =>
    {
        FlandrePortraitClock.Initialize(harmony, _ => { });
        var targets = Targets();
        FlandrePortraitClock.PatchValidatedTargets(harmony, targets);
        foreach (var target in targets)
        {
            var instructions = PatchProcessor.GetCurrentInstructions(target.Key);
            Check(instructions.Count(i => i.opcode == OpCodes.Call && i.operand is MethodInfo m && m.DeclaringType == typeof(FlandrePortraitClock)) == target.Value);
        }
        UnityEngine.Time.Now = 200;
        Equal(200, Fixture.Advance());
        Equal(200, Fixture.Enter());
        Equal(200, Fixture.Apply());
        Equal(200, Fixture.UnrelatedRead());
        Check(Harmony.GetPatchInfo(Wall()) == null);
        Check(FlandrePortraitClock.Shutdown());
        foreach (var target in targets.Keys) Check(Harmony.GetPatchInfo(target)?.Transpilers.Any() != true);
    }),
    ("live patched async continuation retains its timestamp after cancellation and next export", () =>
    {
        FlandrePortraitClock.Initialize(harmony, _ => { });
        FlandrePortraitClock.PatchValidatedTargets(harmony, Targets());
        // Access the same runtime clock behind the replacement getter; identity
        // admission is tested separately, because this is a synthetic assembly.
        var clock = (PortraitRealtimeClock)typeof(FlandrePortraitClock).GetField("Clock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        double video = 0;
        UnityEngine.Time.Now = 200;
        var gateA = new TaskCompletionSource<bool>();
        var gateB = new TaskCompletionSource<bool>();
        var pendingA = Fixture.Animate(gateA.Task);
        var pendingB = Fixture.Run(gateB.Task);
        clock.Begin(() => video);
        video = 0.25;
        UnityEngine.Time.Now = 500;
        Equal(200.25, Fixture.Advance());
        Equal(500, Fixture.UnrelatedRead());
        FlandrePortraitClock.EndExport();
        UnityEngine.Time.Now = 500.25f;
        Equal(200.5, Fixture.Enter());
        gateA.SetResult(true);
        Equal(0.5, pendingA.GetAwaiter().GetResult());
        video = 10;
        clock.Begin(() => video);
        video = 10.25;
        UnityEngine.Time.Now = 900;
        Equal(200.75, Fixture.Apply());
        FlandrePortraitClock.EndExport();
        UnityEngine.Time.Now = 900.25f;
        gateB.SetResult(true);
        Equal(1, pendingB.GetAwaiter().GetResult());
        Check(!FlandrePortraitClock.Shutdown());
        Check(Harmony.GetPatchInfo(Targets().Keys.First())!.Transpilers.Any());
        // Offset is intentionally persistent; the isolated process ends here.
    })
};

int passed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); passed++; }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + test.Name + "\n" + error); Environment.ExitCode = 1; break; }
}
if (Environment.ExitCode == 0 && args.Length != 0)
{
    try { VerifyInstalledBinary(args[0]); Console.WriteLine("PASS installed Portrait binary: 5 exact methods / 7 call sites, both async state machines included"); passed++; }
    catch (Exception error) { Console.Error.WriteLine("FAIL installed binary audit\n" + error); Environment.ExitCode = 1; }
}
Console.WriteLine($"Portrait clock tests: {passed}/{tests.Length + (args.Length != 0 ? 1 : 0)} passed.");

static MethodInfo Wall() => typeof(UnityEngine.Time).GetProperty(nameof(UnityEngine.Time.realtimeSinceStartup))!.GetMethod!;
static Dictionary<MethodInfo, int> Targets()
{
    var result = new Dictionary<MethodInfo, int>();
    foreach (string name in new[] { nameof(Fixture.Advance), nameof(Fixture.Enter), nameof(Fixture.Apply) })
        result.Add(typeof(Fixture).GetMethod(name)!, 1);
    foreach (string name in new[] { nameof(Fixture.Animate), nameof(Fixture.Run) })
    {
        var type = typeof(Fixture).GetMethod(name)!.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        result.Add(type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!, 2);
    }
    return result;
}
static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
static void Equal(double expected, double actual) { if (Math.Abs(expected - actual) > 0.00001) throw new Exception($"Expected {expected}, got {actual}."); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static void VerifyInstalledBinary(string path)
{
    using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(path);
    string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    Check(FlandrePortraitClock.IsSupportedBuild(assembly.Name.Version, assembly.MainModule.Mvid, hash));
    var expected = new Dictionary<string, int[]>
    {
        ["System.Void FlandreScarletPortrait.FLRuntime::AdvancePostfix(Test,System.Boolean,System.Object)"] = new[] { 0x19 },
        ["System.Void FlandreScarletPortrait.FLRuntime::EnterNodeCore(Test,System.Int32,System.Object,System.String)"] = new[] { 0x93 },
        ["System.Void FlandreScarletPortrait.FLSlots::ApplyOne(Test,System.Int32,FlandreScarletPortrait.FLSlots/SlotState)"] = new[] { 0x167 },
        ["System.Void FlandreScarletPortrait.FLCommands/<Animate>d__10::MoveNext()"] = new[] { 0x44, 0x4e },
        ["System.Void FlandreScarletPortrait.FLCommands/<Run>d__0::MoveNext()"] = new[] { 0xac, 0x128 }
    };
    var found = new Dictionary<string, int[]>();
    void Visit(IEnumerable<Mono.Cecil.TypeDefinition> types)
    {
        foreach (var type in types)
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var references = method.Body.Instructions.Where(i => i.Operand is Mono.Cecil.MethodReference reference
                    && reference.DeclaringType.FullName == "UnityEngine.Time" && reference.Name == "get_realtimeSinceStartup").ToArray();
                if (references.Length == 0) continue;
                Check(references.All(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Call));
                found.Add(method.FullName, references.Select(i => i.Offset).ToArray());
            }
            Visit(type.NestedTypes);
        }
    }
    Visit(assembly.MainModule.Types);
    Check(found.Count == expected.Count);
    foreach (var pair in expected) Check(found.TryGetValue(pair.Key, out var offsets) && offsets.SequenceEqual(pair.Value));
}

internal static class ProgramHelpers { public static void ForeignPrefix() { } }
internal static class Fixture
{
    [MethodImpl(MethodImplOptions.NoInlining)] public static float Advance() => UnityEngine.Time.realtimeSinceStartup;
    [MethodImpl(MethodImplOptions.NoInlining)] public static float Enter() => UnityEngine.Time.realtimeSinceStartup;
    [MethodImpl(MethodImplOptions.NoInlining)] public static float Apply() => UnityEngine.Time.realtimeSinceStartup;
    [MethodImpl(MethodImplOptions.NoInlining)] public static float UnrelatedRead() => UnityEngine.Time.realtimeSinceStartup;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<float> Animate(Task gate)
    {
        float start = UnityEngine.Time.realtimeSinceStartup;
        await gate;
        return UnityEngine.Time.realtimeSinceStartup - start;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<float> Run(Task gate)
    {
        float start = UnityEngine.Time.realtimeSinceStartup;
        await gate;
        return UnityEngine.Time.realtimeSinceStartup - start;
    }
}
namespace UnityEngine
{
    internal static class Time
    {
        internal static float Now;
        public static float realtimeSinceStartup { [MethodImpl(MethodImplOptions.NoInlining)] get => Now; }
    }
}
namespace AAVideoExport.Core
{
    internal sealed class ExportException : Exception
    {
        internal string Code { get; }
        internal ExportException(string code, string message) : base(message) => Code = code;
    }
}
