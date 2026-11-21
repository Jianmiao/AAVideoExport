using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using AAVideoExport.RuntimeBootstrap;

internal static class FinalIlSnapshotChecks
{
    private const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;
    internal static void Run(Action<string, Action> test)
    {
        test("final IL parser confirms actual code resolver and returned owner without invoking native constructors", () =>
        {
            var (method, _) = Build(typeof(WrapperA)); var receipt = FinalIlSnapshot.CaptureBeforeMaterialization(method, 3);
            receipt.ValidateBeforeMaterialization(); var value = Materialize(method); receipt.ConfirmReturnedDelegate(method, value); receipt.ValidateCurrent();
            Check(receipt.Materialized && receipt.Constructor.DeclaringType == typeof(WrapperA) && receipt.RawIlSha256.Length == 64, "Missing actual final code receipt");
        });
        test("final IL parser resolves the actual closed generic constructor token", () =>
        {
            var (method, _) = Build(typeof(GenericWrapper<int>)); var receipt = FinalIlSnapshot.CaptureBeforeMaterialization(method, 3);
            receipt.ConfirmReturnedDelegate(method, Materialize(method)); receipt.ValidateCurrent();
            Check(receipt.Constructor.DeclaringType == typeof(GenericWrapper<int>), "Generic context was not resolved");
        });
        test("final IL parser rejects a missing actual generator", () => Reject(() => FinalIlSnapshot.CaptureBeforeMaterialization(
            new DynamicMethod("missing-generator", typeof(WrapperA), new[] { typeof(IntPtr) }), 3), "no existing typed generator"));
        test("final IL parser rejects noncanonical bytes independently of factory provenance", () =>
        {
            var (method, il) = Build(typeof(WrapperA)); Bytes(il)[0] = 0x00;
            Reject(() => FinalIlSnapshot.CaptureBeforeMaterialization(method, 3), "not exactly");
        });
        test("final IL parser rejects a nonmethod token and invalid token index", () =>
        {
            var (method, il) = Build(typeof(WrapperA)); var bytes = Bytes(il); bytes[5] = 0x04;
            Reject(() => FinalIlSnapshot.CaptureBeforeMaterialization(method, 3), "method-token scope");
            bytes[5] = 0x06; bytes[2] = 0xff;
            Reject(() => FinalIlSnapshot.CaptureBeforeMaterialization(method, 3), "method-token scope");
        });
        test("final IL parser rejects a real token resolving to a method instead of constructor", () =>
        {
            var (method, il) = Build(typeof(WrapperA)); var tokens = Tokens(il); tokens[Index(il)] = typeof(object).GetMethod(nameof(object.ToString))!.MethodHandle;
            Reject(() => FinalIlSnapshot.CaptureBeforeMaterialization(method, 3), "not a public IntPtr constructor");
        });
        test("final IL parser rejects unknown token representation", () =>
        {
            var (method, il) = Build(typeof(WrapperA)); Tokens(il)[Index(il)] = "not-a-runtime-handle";
            Reject(() => FinalIlSnapshot.CaptureBeforeMaterialization(method, 3), "Unknown actual constructor token");
        });
        test("final IL parser rejects an actual constructor with a different return type", () =>
        {
            var (method, il) = Build(typeof(WrapperA)); Tokens(il)[Index(il)] = typeof(WrapperB).GetConstructor(new[] { typeof(IntPtr) })!.MethodHandle;
            Reject(() => FinalIlSnapshot.CaptureBeforeMaterialization(method, 3), "differs from the dynamic return type");
        });
        test("final IL receipt detects changed generic token context without changing bytes or token index", () =>
        {
            var (method, il) = Build(typeof(GenericWrapper<int>)); var receipt = FinalIlSnapshot.CaptureBeforeMaterialization(method, 3);
            var (_, other) = Build(typeof(GenericWrapper<string>)); Tokens(il)[Index(il)] = Tokens(other)[Index(other)];
            Reject(receipt.ValidateBeforeMaterialization, "constructor token semantics changed");
        });
        test("final IL parser rejects typed generator plus DynamicILInfo because runtime prioritizes raw code", () =>
        {
            var (method, _) = Build(typeof(WrapperA)); _ = method.GetDynamicILInfo();
            Reject(() => FinalIlSnapshot.CaptureBeforeMaterialization(method, 3), "Raw DynamicILInfo");
        });
        test("private pre-materialization cannot borrow a later final IL receipt", () =>
        {
            var (method, _) = Build(typeof(WrapperA));
            typeof(DynamicMethod).GetMethod("GetMethodDescriptor", Fields)!.Invoke(method, null);
            Reject(() => FinalIlSnapshot.CaptureBeforeMaterialization(method, 3), "already materialized");
        });
        test("actual resolver must match prevalidated code even when the generator is restored", () =>
        {
            var (method, il) = Build(typeof(WrapperA)); var receipt = FinalIlSnapshot.CaptureBeforeMaterialization(method, 3);
            il.Emit(OpCodes.Nop); var result = Materialize(method);
            typeof(ILGenerator).GetField("m_length", Fields)!.SetValue(il, 7);
            Reject(() => receipt.ConfirmReturnedDelegate(method, result), "materialized resolver code differs");
            Check(!receipt.Materialized, "Mismatched actual resolver was confirmed");
        });
        test("actual returned delegate must belong to the exact checked DynamicMethod", () =>
        {
            var (method, _) = Build(typeof(WrapperA)); var receipt = FinalIlSnapshot.CaptureBeforeMaterialization(method, 3); _ = Materialize(method);
            var (other, _) = Build(typeof(WrapperA)); var unrelated = Materialize(other);
            Reject(() => receipt.ConfirmReturnedDelegate(method, unrelated), "different actual DynamicMethod owner");
        });
        test("actual resolver mutation cannot be hidden by an unchanged generator", () =>
        {
            var (method, _) = Build(typeof(WrapperA)); var receipt = FinalIlSnapshot.CaptureBeforeMaterialization(method, 3);
            receipt.ConfirmReturnedDelegate(method, Materialize(method)); var resolver = typeof(DynamicMethod).GetField("m_resolver", Fields)!.GetValue(method)!;
            var code = (byte[])resolver.GetType().GetField("m_code", Fields)!.GetValue(resolver)!; code[0] = 0x00;
            Reject(receipt.ValidateCurrent, "materialized resolver code differs");
        });
        test("late generator mutation is refused and never rewrites the frozen receipt hash", () =>
        {
            var (method, il) = Build(typeof(WrapperA)); var receipt = FinalIlSnapshot.CaptureBeforeMaterialization(method, 3);
            receipt.ConfirmReturnedDelegate(method, Materialize(method)); string original = receipt.RawIlSha256; il.Emit(OpCodes.Nop);
            Reject(receipt.ValidateCurrent, "not exactly"); Check(receipt.RawIlSha256 == original, "Frozen receipt hash was replaced");
        });
        test("late locals and signature changes fail closed", () =>
        {
            var (method, il) = Build(typeof(WrapperA)); var receipt = FinalIlSnapshot.CaptureBeforeMaterialization(method, 3);
            receipt.ConfirmReturnedDelegate(method, Materialize(method)); _ = il.DeclareLocal(typeof(int)); Reject(receipt.ValidateCurrent, "unsupported locals");
            var (second, _) = Build(typeof(WrapperA)); var other = FinalIlSnapshot.CaptureBeforeMaterialization(second, 3);
            other.ConfirmReturnedDelegate(second, Materialize(second)); second.InitLocals = !second.InitLocals; Reject(other.ValidateCurrent, "signature or initialization flags changed");
        });
        test("final IL parse capacity below three instructions fails closed", () =>
        {
            var (method, _) = Build(typeof(WrapperA)); Reject(() => FinalIlSnapshot.CaptureBeforeMaterialization(method, 2), "capacity overflow");
        });
    }
    private static (DynamicMethod Method, ILGenerator Generator) Build(Type returned)
    {
        var method = new DynamicMethod("final-il-parser-fixture", returned, new[] { typeof(IntPtr) }); var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Newobj, returned.GetConstructor(new[] { typeof(IntPtr) })!); il.Emit(OpCodes.Ret); return (method, il);
    }
    private static Delegate Materialize(DynamicMethod method) => method.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(IntPtr), method.ReturnType));
    private static byte[] Bytes(ILGenerator il) => (byte[])typeof(ILGenerator).GetField("m_ILStream", Fields)!.GetValue(il)!;
    private static int Index(ILGenerator il) => BitConverter.ToInt32(Bytes(il), 2) & 0x00ffffff;
    private static IList Tokens(ILGenerator il)
    { var scope = il.GetType().GetField("m_scope", Fields)!.GetValue(il)!; return (IList)scope.GetType().GetField("m_tokens", Fields)!.GetValue(scope)!; }
    private static void Reject(Action action, string fragment)
    {
        try { action(); } catch (Exception error) { Check(error.GetBaseException().Message.Contains(fragment, StringComparison.Ordinal), "Wrong rejection: " + error); return; }
        throw new Exception("Expected final IL rejection: " + fragment);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
