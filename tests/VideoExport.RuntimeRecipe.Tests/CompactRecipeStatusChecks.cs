using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using AAVideoExport.RuntimeBootstrap;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

internal static class CompactRecipeStatusChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo ApiObserver = typeof(RuntimeRecipeV2).GetField("observer", Hidden)!;
    private static readonly Type[] Wrappers = { typeof(StatusWrapperA), typeof(StatusWrapperB) };
    private static MethodInfo CompactMethod => typeof(RuntimeRecipeV2).GetMethod("ValidateScopeStatusJson", BindingFlags.Public | BindingFlags.Static,
        null, new[] { typeof(string) }, null) ?? throw new Exception("Compact live scope status endpoint is missing.");

    internal static int Run()
    {
        int passed = 0, failed = 0;
        void Test(string name, Action run)
        {
            try { run(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
        }
        Test("compact status executes the real validator without returning diagnostic recipe rows", () =>
        {
            _ = CompactMethod;
            using var context = new Context(Wrappers); _ = context.Fresh(typeof(StatusWrapperA));
            var scope = context.Observer.AcquireScope(); Check(scope.Valid, scope.Reason);
            Both(scope.Token, true);
            using var full = JsonDocument.Parse(RuntimeRecipeV2.ValidateScopeJson(scope.Token));
            Check(full.RootElement.GetProperty("recipes").GetArrayLength() == 1, "Full public diagnostic endpoint lost its real recipe.");
        });
        Test("compact and full status reject unknown and released scopes identically", () =>
        {
            using var context = new Context(Wrappers); _ = context.Fresh(typeof(StatusWrapperA));
            Both("unknown", false); var scope = context.Observer.AcquireScope(); context.Observer.ReleaseScope(scope.Token); Both(scope.Token, false);
        });
        Test("compact and full status preserve seal hash and publication token consumption", () =>
        {
            using var context = new Context(Wrappers); _ = context.Fresh(typeof(StatusWrapperA));
            var scope = context.Observer.AcquireScope(); var sealedScope = context.Observer.SealScope(scope.Token);
            Check(sealedScope.Valid && sealedScope.Sealed && sealedScope.RecipesSha256.Length == 64, sealedScope.Reason);
            Both(scope.Token, true);
            using (context.Observer.EnterCommitScope(scope.Token)) { Both(scope.Token, true); }
            Both(scope.Token, false);
        });
        Test("compact status does not authorize an unsealed publication", () =>
        {
            using var context = new Context(Wrappers); _ = context.Fresh(typeof(StatusWrapperA));
            var scope = context.Observer.AcquireScope(); Both(scope.Token, true);
            using var commit = JsonDocument.Parse(RuntimeRecipeV2.ValidateScopeForCommitJson(scope.Token));
            Check(!commit.RootElement.GetProperty("valid").GetBoolean(), "Unsealed scope authorized publication.");
            Both(scope.Token, false);
        });
        Test("compact first detects direct materialized resolver mutation without a generation change", () =>
        {
            using var context = new Context(Wrappers); var value = context.Fresh(typeof(StatusWrapperA));
            var scope = context.Observer.AcquireScope(); var method = Dynamic(value);
            object resolver = typeof(DynamicMethod).GetField("m_resolver", Hidden)!.GetValue(method)!;
            var bytes = (byte[])resolver.GetType().GetField("m_code", Hidden)!.GetValue(resolver)!;
            byte original = bytes[0]; bytes[0] = 0x00;
            try { Both(scope.Token, false); } finally { bytes[0] = original; }
            Both(scope.Token, false);
        });
        Test("compact first detects generator mutation and restoring bytes cannot heal the scope", () =>
        {
            using var context = new Context(Wrappers); var value = context.Fresh(typeof(StatusWrapperA));
            var scope = context.Observer.AcquireScope(); var method = Dynamic(value);
            var il = (ILGenerator)typeof(DynamicMethod).GetField("m_ilGenerator", Hidden)!.GetValue(method)!;
            var length = typeof(ILGenerator).GetField("m_length", Hidden)!; int original = (int)length.GetValue(il)!;
            il.Emit(OpCodes.Nop); Both(scope.Token, false); length.SetValue(il, original); Both(scope.Token, false);
        });
        Test("compact first detects initializer flags changed without a generation notification", () =>
        {
            using var context = new Context(Wrappers); var value = context.Fresh(typeof(StatusWrapperA));
            var scope = context.Observer.AcquireScope(); var method = Dynamic(value); bool original = method.InitLocals;
            method.InitLocals = !original; try { Both(scope.Token, false); } finally { method.InitLocals = original; }
            Both(scope.Token, false);
        });
        Test("compact first detects transient constructor patch history after unpatch", () =>
        {
            using var context = new Context(Wrappers); _ = context.Fresh(typeof(StatusWrapperA));
            var scope = context.Observer.AcquireScope(); var patcher = new Harmony("aave.recipe.compact.transient");
            try { patcher.Patch(typeof(StatusWrapperA).GetConstructor(new[] { typeof(IntPtr) })!, prefix: new HarmonyMethod(typeof(CompactRecipeStatusChecks).GetMethod(nameof(Prefix))!)); }
            finally { patcher.UnpatchSelf(); }
            Both(scope.Token, false);
        });
        Test("compact first detects actual cached delegate replacement", () =>
        {
            using var context = new Context(Wrappers); _ = context.Fresh(typeof(StatusWrapperA));
            var scope = context.Observer.AcquireScope(); var field = Store(typeof(StatusWrapperA)).GetField("_initializer", Hidden)!; object? original = field.GetValue(null);
            field.SetValue(null, new Func<IntPtr, StatusWrapperA>(_ => null!));
            try { Both(scope.Token, false); } finally { field.SetValue(null, original); }
            Both(scope.Token, false);
        });
        Test("compact and full status reject new anonymous generation after sealing", () =>
        {
            using var context = new Context(Wrappers); _ = context.Fresh(typeof(StatusWrapperA));
            var scope = context.Observer.AcquireScope(); Check(context.Observer.SealScope(scope.Token).Valid, "Seal failed.");
            _ = context.Fresh(typeof(StatusWrapperB)); Both(scope.Token, false);
        });
        Console.WriteLine($"RESULT {passed} passed; {failed} failed; compact/full real-observer differential; runtime={Environment.Version}");
        return failed == 0 ? 0 : 1;
    }

    private static void Both(string token, bool valid)
    {
        // Compact runs first: a missing compact check must not borrow the full
        // validator's latched failure. Both calls exercise the actual observer.
        using var compact = JsonDocument.Parse((string)CompactMethod.Invoke(null, new object[] { token })!);
        using var full = JsonDocument.Parse(RuntimeRecipeV2.ValidateScopeJson(token));
        Check(compact.RootElement.GetProperty("valid").GetBoolean() == valid && full.RootElement.GetProperty("valid").GetBoolean() == valid,
            "Compact/full acceptance differs from the expected real state.");
        foreach (var property in full.RootElement.EnumerateObject().Where(property => property.Name != "recipes"))
            Check(compact.RootElement.GetProperty(property.Name).GetRawText() == property.Value.GetRawText(), "Scope envelope differs: " + property.Name);
        Check(compact.RootElement.GetProperty("recipes").GetArrayLength() == 0, "Compact status constructed diagnostic recipe rows.");
    }

    internal static int Benchmark()
    {
        _ = CompactMethod;
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        var types = new[] { typeof(byte), typeof(int), typeof(long) }.SelectMany(type => Enumerable.Range(1, 32)
            .Select(rank => typeof(StatusGenericWrapper<>).MakeGenericType(type.MakeArrayType(rank)))).ToArray();
        var full = typeof(RuntimeRecipeV2).GetMethod(nameof(RuntimeRecipeV2.ValidateScopeJson))!; var compact = CompactMethod;
        foreach (int count in new[] { 1, 8, 32, 96 })
        {
            using var context = new Context(types); foreach (Type type in types.Take(count)) _ = context.Fresh(type);
            var scope = context.Observer.AcquireScope(); Check(scope.Valid && scope.Recipes.Count == count, scope.Reason);
            void Transport(MethodInfo method)
            {
                string json = (string)method.Invoke(null, new object[] { scope.Token })!;
                using var client = JsonDocument.Parse(json); using var retained = JsonDocument.Parse(json);
                Check(client.RootElement.GetProperty("valid").GetBoolean() && retained.RootElement.GetProperty("token").GetString() == scope.Token,
                    "Benchmark validation unexpectedly failed.");
            }
            for (int i = 0; i < 4; i++) { Transport(full); Transport(compact); }
            int iterations = count >= 32 ? 12 : 30;
            (long Bytes, double Ms) Measure(MethodInfo method)
            {
                long before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++) Transport(method);
                timer.Stop(); return ((GC.GetAllocatedBytesForCurrentThread() - before) / iterations, timer.Elapsed.TotalMilliseconds / iterations);
            }
            var a = Measure(full); var b = Measure(compact); var c = Measure(compact); var d = Measure(full);
            Console.WriteLine(JsonSerializer.Serialize(new { Recipes = count, IterationsPerSample = iterations,
                FullBytesPerCall = (a.Bytes + d.Bytes) / 2, CompactBytesPerCall = (b.Bytes + c.Bytes) / 2,
                FullMillisecondsPerCall = (a.Ms + d.Ms) / 2, CompactMillisecondsPerCall = (b.Ms + c.Ms) / 2,
                Scope = "Real observer validation plus public JSON transport and two envelope parses; independent managed process, not native export throughput." }));
        }
        return 0;
    }

    private sealed class Context : IDisposable
    {
        internal readonly RuntimeRecipeObserver Observer;
        private readonly object? previous;
        internal Context(Type[] types)
        {
            Observer = new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(types)); Observer.Install(); Check(Observer.IsInstalled, Observer.Failure);
            previous = ApiObserver.GetValue(null); ApiObserver.SetValue(null, Observer);
        }
        internal Delegate Fresh(Type type)
        {
            Type store = Store(type); store.GetField("_initializer", Hidden)!.SetValue(null, null);
            return (Delegate)store.GetProperty("Initializer", Hidden)!.GetValue(null)!;
        }
        public void Dispose() { ApiObserver.SetValue(null, previous); Observer.Dispose(); }
    }
    private static Type Store(Type type) => typeof(Il2CppObjectBase).GetNestedType("InitializerStore`1", BindingFlags.NonPublic)!.MakeGenericType(type);
    private static DynamicMethod Dynamic(Delegate value) => (DynamicMethod)value.Method.GetType().GetField("m_owner", Hidden)!.GetValue(value.Method)!;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Prefix() { }
    public sealed class StatusWrapperA : Il2CppObjectBase { public StatusWrapperA(IntPtr pointer) : base(pointer) { } }
    public sealed class StatusWrapperB : Il2CppObjectBase { public StatusWrapperB(IntPtr pointer) : base(pointer) { } }
    public sealed class StatusGenericWrapper<T> : Il2CppObjectBase { public StatusGenericWrapper(IntPtr pointer) : base(pointer) { } }
}
