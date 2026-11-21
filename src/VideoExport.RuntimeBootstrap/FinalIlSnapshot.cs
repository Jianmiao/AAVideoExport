using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;

namespace AAVideoExport.RuntimeBootstrap;

// Fixed .NET 6.0.7 layout, qualified by InteropRecipePolicy's CoreLib pin.
// This reads the code the runtime will consume; it neither emits nor detours Emit.
internal sealed class FinalIlSnapshot
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly WeakReference<DynamicMethod> method;
    private readonly WeakReference<ILGenerator> generator;
    private readonly WeakReference<object> scope;
    private readonly byte[] code;
    private readonly Type returnType;
    private readonly Type[] parameterTypes;
    private readonly bool initLocals;
    private WeakReference<object>? resolver;
    private WeakReference<object>? methodHandle;
    private int stackSize;
    internal ConstructorInfo Constructor { get; }
    internal string RawIlSha256 { get; }
    internal bool Materialized { get; private set; }

    private FinalIlSnapshot(DynamicMethod source, ILGenerator il, object dynamicScope, byte[] bytes, ConstructorInfo constructor)
    {
        method = new(source); generator = new(il); scope = new(dynamicScope); code = bytes;
        Constructor = constructor; returnType = source.ReturnType;
        parameterTypes = source.GetParameters().Select(p => p.ParameterType).ToArray();
        initLocals = source.InitLocals;
        RawIlSha256 = Convert.ToHexString(SHA256.HashData(code));
    }

    internal static FinalIlSnapshot CaptureBeforeMaterialization(DynamicMethod source, int maximumInstructions)
    {
        InteropRecipePolicy.Require(maximumInstructions >= 3, "Final IL instruction capacity overflow.");
        RequireTypedUnmaterialized(source);
        var il = Field(typeof(DynamicMethod), "m_ilGenerator", source) as ILGenerator
            ?? throw new NotSupportedException("Actual DynamicMethod has no existing typed generator.");
        InteropRecipePolicy.Require(il.GetType().FullName == "System.Reflection.Emit.DynamicILGenerator", "Unknown actual generator implementation.");
        InteropRecipePolicy.Require(ReferenceEquals(Field(typeof(ILGenerator), "m_methodBuilder", il), source), "Generator belongs to another DynamicMethod.");
        var bytes = ReadGenerator(il);
        var dynamicScope = Field(il.GetType(), "m_scope", il) ?? throw new NotSupportedException("Actual dynamic token scope is missing.");
        var constructor = ResolveConstructor(dynamicScope, bytes);
        InteropRecipePolicy.Require(source.IsStatic && source.CallingConvention == CallingConventions.Standard
            && source.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(IntPtr) }), "Actual dynamic input signature differs from static IntPtr.");
        InteropRecipePolicy.Require(source.ReturnType == constructor.DeclaringType, "Actual final IL constructor differs from the dynamic return type.");
        return new(source, il, dynamicScope, bytes, constructor);
    }

    internal void ValidateBeforeMaterialization()
    {
        var source = Source(); RequireTypedUnmaterialized(source); ValidateGenerator(source);
    }

    internal void ConfirmReturnedDelegate(DynamicMethod source, Delegate result)
    {
        InteropRecipePolicy.Require(ReferenceEquals(Source(), source), "Returned delegate is associated with another DynamicMethod.");
        InteropRecipePolicy.Require(result.Method.GetType().FullName == "System.Reflection.Emit.DynamicMethod+RTDynamicMethod"
            && ReferenceEquals(Field(result.Method.GetType(), "m_owner", result.Method), source), "Returned delegate Method has a different actual DynamicMethod owner.");
        ValidateGenerator(source);
        var actualResolver = Field(typeof(DynamicMethod), "m_resolver", source) ?? throw new NotSupportedException("Runtime did not retain its actual materialized resolver.");
        var actualHandle = Field(typeof(DynamicMethod), "m_methodHandle", source) ?? throw new NotSupportedException("Runtime did not materialize the actual method handle.");
        ValidateResolver(source, actualResolver);
        resolver = new(actualResolver); methodHandle = new(actualHandle);
        stackSize = (int)Field(actualResolver.GetType(), "m_stackSize", actualResolver)!;
        Materialized = true;
    }

    internal void ValidateCurrent()
    {
        var source = Source(); ValidateGenerator(source);
        InteropRecipePolicy.Require(Materialized && resolver != null && resolver.TryGetTarget(out _)
            && methodHandle != null && methodHandle.TryGetTarget(out _), "Actual materialized receipt is unavailable.");
        resolver!.TryGetTarget(out var expectedResolver); methodHandle!.TryGetTarget(out var expectedHandle);
        InteropRecipePolicy.Require(ReferenceEquals(Field(typeof(DynamicMethod), "m_resolver", source), expectedResolver)
            && ReferenceEquals(Field(typeof(DynamicMethod), "m_methodHandle", source), expectedHandle), "Materialized resolver or method handle identity changed.");
        ValidateResolver(source, expectedResolver!);
        InteropRecipePolicy.Require((int)Field(expectedResolver!.GetType(), "m_stackSize", expectedResolver)! == stackSize, "Materialized stack metadata changed.");
    }

    private void ValidateGenerator(DynamicMethod source)
    {
        InteropRecipePolicy.Require(Field(typeof(DynamicMethod), "m_DynamicILInfo", source) == null, "Raw DynamicILInfo is outside the typed final IL policy.");
        InteropRecipePolicy.Require(source.ReturnType == returnType && source.InitLocals == initLocals
            && source.CallingConvention == CallingConventions.Standard
            && source.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameterTypes), "Dynamic method signature or initialization flags changed.");
        InteropRecipePolicy.Require(generator.TryGetTarget(out var il) && scope.TryGetTarget(out _), "Final IL source metadata is unavailable.");
        scope.TryGetTarget(out var expectedScope);
        InteropRecipePolicy.Require(ReferenceEquals(Field(typeof(DynamicMethod), "m_ilGenerator", source), il)
            && ReferenceEquals(Field(typeof(ILGenerator), "m_methodBuilder", il!), source), "Actual typed generator identity changed.");
        InteropRecipePolicy.Require(ReadGenerator(il!).SequenceEqual(code), "Final IL generator bytes changed after the pre-materialization snapshot.");
        InteropRecipePolicy.Require(ReferenceEquals(Field(il!.GetType(), "m_scope", il), expectedScope), "Actual dynamic token scope identity changed.");
        RequireSameConstructor(ResolveConstructor(expectedScope!, code), Constructor);
    }

    private void ValidateResolver(DynamicMethod source, object actual)
    {
        InteropRecipePolicy.Require(actual.GetType().FullName == "System.Reflection.Emit.DynamicResolver", "Unknown materialized resolver implementation.");
        InteropRecipePolicy.Require(ReferenceEquals(Field(actual.GetType(), "m_method", actual), source), "Resolver belongs to another actual DynamicMethod.");
        var materializedCode = Field(actual.GetType(), "m_code", actual) as byte[];
        InteropRecipePolicy.Require(materializedCode != null && materializedCode.SequenceEqual(code), "Actual materialized resolver code differs from the pre-materialization snapshot.");
        scope.TryGetTarget(out var expectedScope);
        InteropRecipePolicy.Require(expectedScope != null && ReferenceEquals(Field(actual.GetType(), "m_scope", actual), expectedScope), "Resolver token scope differs from the pre-materialization scope.");
        RequireSameConstructor(ResolveConstructor(expectedScope!, materializedCode!), Constructor);
        InteropRecipePolicy.Require(Field(actual.GetType(), "m_exceptions", actual) == null && Field(actual.GetType(), "m_exceptionHeader", actual) == null, "Materialized resolver contains exception handling.");
        var locals = Field(actual.GetType(), "m_localSignature", actual) as byte[];
        InteropRecipePolicy.Require(locals != null && locals.SequenceEqual(new byte[] { 7, 0, 0 }), "Materialized resolver has unexpected local signature metadata.");
    }

    private DynamicMethod Source() => method.TryGetTarget(out var value) ? value : throw new NotSupportedException("Actual DynamicMethod is no longer available.");
    private static void RequireTypedUnmaterialized(DynamicMethod source)
    {
        InteropRecipePolicy.Require(Field(typeof(DynamicMethod), "m_DynamicILInfo", source) == null, "Raw DynamicILInfo is outside the typed final IL policy.");
        InteropRecipePolicy.Require(Field(typeof(DynamicMethod), "m_methodHandle", source) == null && Field(typeof(DynamicMethod), "m_resolver", source) == null,
            "Actual method was already materialized without this final IL receipt.");
    }
    private static byte[] ReadGenerator(ILGenerator source)
    {
        int length = (int)Field(typeof(ILGenerator), "m_length", source)!;
        var buffer = Field(typeof(ILGenerator), "m_ILStream", source) as byte[];
        InteropRecipePolicy.Require(length == 7 && buffer != null && buffer.Length >= 7 && buffer[0] == 0x02 && buffer[1] == 0x73 && buffer[6] == 0x2a,
            "Actual final IL is not exactly ldarg.0; newobj; ret.");
        foreach (string name in new[] { "m_localCount", "m_exceptionCount", "m_fixupCount", "m_currExcStackCount", "m_labelCount", "m_RelocFixupCount" })
            InteropRecipePolicy.Require((int)Field(typeof(ILGenerator), name, source)! == 0, "Final IL has unsupported locals, exceptions, labels or fixups: " + name);
        return buffer!.AsSpan(0, length).ToArray();
    }
    private static ConstructorInfo ResolveConstructor(object dynamicScope, byte[] bytes)
    {
        InteropRecipePolicy.Require(dynamicScope.GetType().FullName == "System.Reflection.Emit.DynamicScope", "Unknown dynamic token scope implementation.");
        var tokens = Field(dynamicScope.GetType(), "m_tokens", dynamicScope) as IList ?? throw new NotSupportedException("Dynamic token table is missing.");
        uint operand = BitConverter.ToUInt32(bytes, 2); int index = (int)(operand & 0x00ffffff);
        InteropRecipePolicy.Require((operand & 0xff000000) == 0x06000000 && index > 0 && index < tokens.Count, "Final IL constructor token is outside the actual method-token scope.");
        object? value = tokens[index]; MethodBase? resolved;
        if (value is RuntimeMethodHandle handle) resolved = MethodBase.GetMethodFromHandle(handle);
        else if (value?.GetType().FullName == "System.Reflection.Emit.GenericMethodInfo")
        {
            var h = (RuntimeMethodHandle)Field(value.GetType(), "m_methodHandle", value)!;
            var c = (RuntimeTypeHandle)Field(value.GetType(), "m_context", value)!;
            resolved = MethodBase.GetMethodFromHandle(h, c);
        }
        else throw new NotSupportedException("Unknown actual constructor token representation.");
        InteropRecipePolicy.Require(resolved is ConstructorInfo constructor && !constructor.IsStatic && constructor.IsPublic
            && constructor.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(IntPtr) }), "Actual final IL token is not a public IntPtr constructor.");
        return (ConstructorInfo)resolved!;
    }
    private static void RequireSameConstructor(ConstructorInfo actual, ConstructorInfo expected) => InteropRecipePolicy.Require(
        actual.Module == expected.Module && actual.MetadataToken == expected.MetadataToken && actual.DeclaringType == expected.DeclaringType
        && actual.MethodHandle.Equals(expected.MethodHandle), "Actual constructor token semantics changed after the pre-materialization snapshot.");
    private static object? Field(Type type, string name, object owner) => (type.GetField(name, Fields)
        ?? throw new NotSupportedException("Inspected runtime field is unavailable: " + type.FullName + "." + name)).GetValue(owner);
}
