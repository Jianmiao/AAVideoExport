// Supplied by the user's Unity native export research package (2026-10-01).
// Original C# 5 source retained; nullable annotations are not part of that source.
#nullable disable
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

/// <summary>
/// Synchronous bulk copy from an actual Il2CppStructArray&lt;byte&gt; returned by
/// the initialized IL2CPP runtime. C# 5; no UnityEngine reference or header offsets.
/// Call on the capture thread before destroying/replacing the source texture.
/// </summary>
public static class Il2CppBulkCopy
{
    // This nested explicit initializer caches metadata only, never raw objects,
    // native handles or data pointers. Reflection does not run array type cctors.
    private static class ArrayMetadata
    {
        internal static readonly Type ByteArrayType;
        internal static readonly MethodInfo StartGetter;

        static ArrayMetadata()
        {
            Type baseType = typeof(Il2CppArrayBase);
            Type definition = baseType.Assembly.GetType(
                "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1", true);
            ByteArrayType = definition.MakeGenericType(typeof(byte));
            PropertyInfo property = baseType.GetProperty("ArrayStartPointer",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            MethodInfo getter = property == null ? null : property.GetGetMethod(true);
            if (getter == null || getter.IsStatic || getter.ReturnType != typeof(IntPtr) ||
                getter.DeclaringType != baseType || getter.GetParameters().Length != 0 ||
                property.GetIndexParameters().Length != 0)
            {
                throw new MissingMethodException(baseType.FullName, "get_ArrayStartPointer");
            }
            StartGetter = getter;
        }
    }

    /// <summary>
    /// Copies exactly expectedLength bytes into dest starting at destOffset.
    /// Source must have the exact Il2CppStructArray&lt;byte&gt; runtime type.
    /// The caller supplies a checked expected byte count (e.g. RGBA32 without mipmaps).
    /// Returns only after managed bytes are independent of the source array.
    /// </summary>
    public static void Copy(object raw, byte[] dest, int destOffset, int expectedLength)
    {
        try
        {
            if (raw == null) throw new ArgumentNullException("raw");
            ValidateDestination(dest, destOffset, expectedLength);
            ValidateSourceType(raw.GetType());
            CopyNative((Il2CppArrayBase)raw, dest, destOffset, expectedLength);
        }
        finally
        {
            // Protect the wrapper's own native GC handle until the copy/cleanup ends,
            // including exceptional exits. This is not a substitute for native pinning.
            GC.KeepAlive(raw);
        }
    }

    // NoInlining keeps the native stage out of the rejection-only synthetic paths.
    // Tests must not create an IL2CPP wrapper or call this method outside Unity.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyNative(Il2CppArrayBase array, byte[] dest,
                                   int destOffset, int expectedLength)
    {
        int length = array.Length;
        ValidateArrayLength(length, expectedLength);
        if (length == 0) return; // No data-pointer lookup, pin or dereference for empty data.

        IntPtr objectPointer = array.Pointer; // Array object, NOT pixel/data start.
        if (objectPointer == IntPtr.Zero)
            throw new InvalidOperationException("The native array object is null.");

        IntPtr pinnedHandle = IL2CPP.il2cpp_gchandle_new(objectPointer, true);
        if (pinnedHandle == IntPtr.Zero)
            throw new InvalidOperationException("The native pinned array handle is null.");
        try
        {
            IntPtr pinnedTarget = IL2CPP.il2cpp_gchandle_get_target(pinnedHandle);
            if (pinnedTarget == IntPtr.Zero || pinnedTarget != array.Pointer)
                throw new InvalidOperationException("Native pinned array target mismatch.");
            ValidateNativeLengths(IL2CPP.il2cpp_array_length(pinnedTarget),
                IL2CPP.il2cpp_array_get_byte_length(pinnedTarget), expectedLength);

            // Only use the runtime's getter. Do NOT fall back to Pointer + 32,
            // object_unbox, Texture2D.Pointer or a native GPU texture handle.
            IntPtr data = (IntPtr)GetArrayStartGetter().Invoke(array, null);
            CopyTrustedData(data, dest, destOffset, length, expectedLength);
        }
        finally
        {
            IL2CPP.il2cpp_gchandle_free(pinnedHandle);
        }
    }

    internal static void ValidateSourceType(Type type)
    {
        if (type != ArrayMetadata.ByteArrayType)
            throw new ArgumentException("Source must be exactly Il2CppStructArray<byte>.", "raw");
    }

    internal static MethodInfo GetArrayStartGetter()
    {
        return ArrayMetadata.StartGetter;
    }

    internal static void ValidateDestination(byte[] dest, int destOffset, int expectedLength)
    {
        if (dest == null) throw new ArgumentNullException("dest");
        if (expectedLength < 0) throw new ArgumentOutOfRangeException("expectedLength");
        if (destOffset < 0 || destOffset > dest.Length)
            throw new ArgumentOutOfRangeException("destOffset");
        // Subtraction avoids overflowing destOffset + expectedLength.
        if (expectedLength > dest.Length - destOffset)
            throw new ArgumentException("Destination has insufficient space.", "dest");
    }

    internal static void ValidateArrayLength(int length, int expectedLength)
    {
        if (expectedLength < 0) throw new ArgumentOutOfRangeException("expectedLength");
        if (length < 0) throw new ArgumentOutOfRangeException("length");
        if (length != expectedLength)
            throw new ArgumentException("Native byte-array length differs from expectedLength.", "raw");
    }

    internal static void ValidateNativeLengths(uint elementCount, uint byteCount, int expectedLength)
    {
        if (expectedLength < 0) throw new ArgumentOutOfRangeException("expectedLength");
        if (elementCount != (uint)expectedLength || byteCount != (uint)expectedLength)
            throw new InvalidOperationException("Native element/byte lengths are inconsistent.");
    }

    internal static void ValidateDataRange(IntPtr data, int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException("length");
        if (length == 0) return;
        if (data == IntPtr.Zero)
            throw new InvalidOperationException("The native array data pointer is null.");
        ulong address = IntPtr.Size == 8
            ? unchecked((ulong)data.ToInt64()) : unchecked((uint)data.ToInt32());
        ulong maxAddress = IntPtr.Size == 8 ? ulong.MaxValue : uint.MaxValue;
        if ((ulong)length > maxAddress - address)
            throw new OverflowException("Native array data address range overflow.");
    }

    // Internal seam for synthetic Marshal.Copy tests with memory allocated by the
    // test itself. It does not discover/validate arbitrary external native memory.
    internal static void CopyTrustedData(IntPtr data, byte[] dest, int destOffset,
                                         int length, int expectedLength)
    {
        ValidateDestination(dest, destOffset, expectedLength);
        ValidateArrayLength(length, expectedLength);
        ValidateDataRange(data, length);
        if (length != 0) Marshal.Copy(data, dest, destOffset, length);
    }
}
