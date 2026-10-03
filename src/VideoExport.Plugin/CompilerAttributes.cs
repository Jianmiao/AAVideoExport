// This particular IL2CPP binding set exports an empty NullableAttribute type.
// Supply compiler-only metadata locally; no binding or native binary is modified.
#pragma warning disable CS0436
namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
internal sealed class NullableAttribute : Attribute
{
    public readonly byte[] NullableFlags;
    public NullableAttribute(byte value) => NullableFlags = new[] { value };
    public NullableAttribute(byte[] values) => NullableFlags = values;
}

[AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
internal sealed class NullableContextAttribute : Attribute
{
    public readonly byte Flag;
    public NullableContextAttribute(byte value) => Flag = value;
}
