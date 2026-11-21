using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using AAVideoExport.Plugin;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using MonoMod.Utils.Cil;

internal static class HarmonyMetadataReceiptChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static int Run()
    {
        int passed = 0, failed = 0;
        void Test(string name, Action run)
        {
            try { run(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
        }
        _ = new Harmony("aave.harmony-metadata.tests.warm"); _ = ILGeneratorShim.ProxyType; _ = DetourHelper.Runtime;
        Type[] arguments = { typeof(int), typeof(long), typeof(double), typeof(float), typeof(short), typeof(byte), typeof(uint), typeof(ulong), typeof(ushort), typeof(sbyte), typeof(bool), typeof(char), typeof(string), typeof(object), typeof(IntPtr), typeof(UIntPtr) };
        var methods = arguments.SelectMany(left => arguments.Select(right => typeof(Target<>).MakeGenericType(typeof(Marker<,>).MakeGenericType(left, right)).GetMethod(nameof(Target<int>.Invoke))!)).Take(250).ToArray();
        var types = methods.Select(method => DelegateTypeFactory.instance.CreateDelegateType(method, CallingConvention.Cdecl)).ToArray();
        Test("250 actual Harmony declarations retain identity while reducing repeated semantic-allocation cost", () =>
        {
            using var scope = FrameworkGeneratedAssemblyScope.Acquire(); string identity = scope.IdentitySha256;
            for (int i = 0; i < 3; i++) scope.Validate();
            long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 5; i++) scope.Validate();
            long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 5;
            Check(perCall < 1800000, "Repeated unchanged declaration validation allocated " + perCall + " bytes per callback; expected less than 1,800,000.");
            Check(scope.IdentitySha256 == identity && new[] { types[0], types[125], types[^1] }.All(type => scope.AuthorizesAssembly(type.Assembly)), "Actual source identity/authorization drifted.");
            using var next = FrameworkGeneratedAssemblyScope.Acquire(); Check(next.IdentitySha256 == identity, "Local metadata receipt changed repeatable scope identity.");
        });
        Test("late actual Harmony declaration receives its own checked receipt and preserves scope identity", () =>
        {
            using var scope = FrameworkGeneratedAssemblyScope.Acquire(); string identity = scope.IdentitySha256;
            var late = DelegateTypeFactory.instance.CreateDelegateType(typeof(Target<Guid>).GetMethod(nameof(Target<Guid>.Invoke))!, CallingConvention.Cdecl);
            scope.Validate(); Check(scope.AuthorizesAssembly(late.Assembly) && scope.IdentitySha256 == identity, "Late declaration was skipped or changed policy identity.");
        });
        Test("same-count live cache entry replacement remains rejected and latched", () =>
        {
            using var scope = FrameworkGeneratedAssemblyScope.Acquire(); var entries = Entries(methods[0]); object original = entries[0]!;
            object replacement = typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(original, null)!;
            entries[0] = replacement; try { Reject(() => scope.Validate()); } finally { entries[0] = original; }
            Reject(() => scope.Validate());
        });
        Test("live calling convention change cannot reuse unchanged delegate metadata", () =>
        {
            using var scope = FrameworkGeneratedAssemblyScope.Acquire(); object entry = Entries(methods[0])[0]!;
            var field = entry.GetType().GetField("callingConvention", Hidden)!; object? original = field.GetValue(entry);
            field.SetValue(entry, CallingConvention.StdCall); try { Reject(() => scope.Validate()); } finally { field.SetValue(entry, original); }
            Reject(() => scope.Validate());
        });
        Test("removing a retained source binding cannot be hidden by receipt reuse", () =>
        {
            using var scope = FrameworkGeneratedAssemblyScope.Acquire(); var cache = Cache(); object original = cache[methods[0]]!;
            cache.Remove(methods[0]); try { Reject(() => scope.Validate()); } finally { cache.Add(methods[0], original); }
            Reject(() => scope.Validate());
        });
        Test("every callback compares the real retained metadata copy and restored bytes cannot heal a failed scope", () =>
        {
            using var scope = FrameworkGeneratedAssemblyScope.Acquire(); var receipts = Receipts(scope); object receipt = receipts[types[0]]!;
            var metadata = (byte[])receipt.GetType().GetField("metadata", Hidden)!.GetValue(receipt)!;
            byte original = metadata[0]; metadata[0] ^= 1;
            try { Reject(() => scope.Validate(), "metadata bytes changed"); } finally { metadata[0] = original; }
            Reject(() => scope.Validate());
        });
        Test("same-shape different actual Type cannot borrow another declaration receipt", () =>
        {
            Check(types[0] != types[1] && types[0].GetMethod("Invoke")!.ReturnType == types[1].GetMethod("Invoke")!.ReturnType,
                "Same-shape actual declaration fixture is invalid.");
            using var scope = FrameworkGeneratedAssemblyScope.Acquire(); var receipts = Receipts(scope); object original = receipts[types[1]]!;
            receipts[types[1]] = receipts[types[0]]!;
            try { Reject(() => scope.Validate(), "receipt identity"); } finally { receipts[types[1]] = original; }
            Reject(() => scope.Validate());
        });
        Test("commit rechecks full semantics instead of trusting a corrupted retained semantic graph", () =>
        {
            using var scope = FrameworkGeneratedAssemblyScope.Acquire(); var receipts = Receipts(scope); object receipt = receipts[types[0]]!;
            var graph = receipt.GetType().GetField("graph", Hidden)!; string original = (string)graph.GetValue(receipt)!;
            graph.SetValue(receipt, original + "-changed");
            // Keep the local expected graph consistent, so only the original
            // full inspector at commit can distinguish this corrupted proof.
            object additional = typeof(FrameworkGeneratedAssemblyScope).GetField("additional", Hidden)!.GetValue(scope)!;
            var declarations = (IDictionary)additional.GetType().GetField("declarations", Hidden)!.GetValue(additional)!;
            declarations[types[0]] = original + "-changed";
            try { Reject(() => { using var guard = scope.EnterCommitScope(); }, "semantic"); }
            finally { graph.SetValue(receipt, original); declarations[types[0]] = original; }
            Reject(() => scope.Validate());
        });
        Test("bounded receipt capacity leaves late declarations on complete semantic validation", () =>
        {
            using var scope = FrameworkGeneratedAssemblyScope.Acquire();
            object additional = typeof(FrameworkGeneratedAssemblyScope).GetField("additional", Hidden)!.GetValue(scope)!;
            additional.GetType().GetField("harmonyMetadataBytes", Hidden)!.SetValue(additional, 8 * 1024 * 1024);
            var source = typeof(Target<DateTime>).GetMethod(nameof(Target<DateTime>.Invoke))!;
            var late = DelegateTypeFactory.instance.CreateDelegateType(source, CallingConvention.Cdecl);
            Check(!Receipts(scope).Contains(late), "Exhausted receipt budget retained another image.");
            scope.Validate(); object entry = Entries(source)[0]!; var convention = entry.GetType().GetField("callingConvention", Hidden)!;
            object? original = convention.GetValue(entry); convention.SetValue(entry, CallingConvention.StdCall);
            try { Reject(() => scope.Validate(), "calling convention"); } finally { convention.SetValue(entry, original); }
            Reject(() => scope.Validate());
        });
        Console.WriteLine($"RESULT {passed} passed; {failed} failed; original Harmony declaration metadata receipts; runtime={Environment.Version}");
        return failed == 0 ? 0 : 1;
    }
    internal static int Fallback()
    {
        Check(System.Reflection.Metadata.MetadataUpdater.IsSupported, "Fallback fixture requires DOTNET_MODIFIABLE_ASSEMBLIES=debug.");
        _ = new Harmony("aave.harmony-metadata.tests.fallback"); _ = ILGeneratorShim.ProxyType; _ = DetourHelper.Runtime;
        var source = typeof(Target<TimeSpan>).GetMethod(nameof(Target<TimeSpan>.Invoke))!;
        var type = DelegateTypeFactory.instance.CreateDelegateType(source, CallingConvention.Cdecl);
        using var scope = FrameworkGeneratedAssemblyScope.Acquire(); scope.Validate();
        Check(!Receipts(scope).Contains(type), "Hot-reload-capable process incorrectly reused raw metadata receipts.");
        object entry = Entries(source)[0]!; var convention = entry.GetType().GetField("callingConvention", Hidden)!;
        object? original = convention.GetValue(entry); convention.SetValue(entry, CallingConvention.StdCall);
        try { Reject(() => scope.Validate(), "calling convention"); } finally { convention.SetValue(entry, original); }
        Reject(() => scope.Validate());
        Console.WriteLine("PASS hot-reload-supported process retains full semantic validation and failure latching; runtime=" + Environment.Version);
        return 0;
    }
    private static IDictionary Cache() => (IDictionary)typeof(DelegateTypeFactory).GetField("TypeCache", Hidden)!.GetValue(null)!;
    private static IList Entries(MethodInfo method) => (IList)Cache()[method]!;
    private static IDictionary Receipts(FrameworkGeneratedAssemblyScope scope)
    {
        object additional = typeof(FrameworkGeneratedAssemblyScope).GetField("additional", Hidden)!.GetValue(scope)!;
        var field = additional.GetType().GetField("harmonyMetadata", Hidden) ?? throw new Exception("No retained raw-metadata receipt exists for actual Harmony declarations.");
        return (IDictionary)field.GetValue(additional)!;
    }
    private static void Reject(Action run, string message = "")
    {
        try { run(); }
        catch (InvalidOperationException error) when (message.Length == 0 || error.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Expected retained proof refusal: " + message);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public sealed class Marker<T1, T2> { }
    public static class Target<T> { public static IntPtr Invoke(IntPtr value) => value; }
}
