using System.Reflection.Emit;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FixtureProducer;

public static class Factory
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object[] BuildWithConstructor(int variant, int offset)
    {
        var types = new[] { typeof(int) };
        var method = variant switch
        {
            0 => new DynamicMethod("Overload", typeof(int), types),
            1 => new DynamicMethod("Overload", typeof(int), types, true),
            2 => new DynamicMethod("Overload", typeof(int), types, typeof(Factory).Module),
            3 => new DynamicMethod("Overload", typeof(int), types, typeof(Factory).Module, true),
            4 => new DynamicMethod("Overload", MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Standard,
                typeof(int), types, typeof(Factory).Module, true),
            5 => new DynamicMethod("Overload", typeof(int), types, typeof(Factory)),
            6 => new DynamicMethod("Overload", typeof(int), types, typeof(Factory), true),
            7 => new DynamicMethod("Overload", MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Standard,
                typeof(int), types, typeof(Factory), true),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        return Complete(method, offset);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object[] BuildBound(int offset)
    {
        var method = new DynamicMethod("Bound", typeof(int), new[] { typeof(object), typeof(int) });
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, typeof(int));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
        return new object[] { method, method.CreateDelegate(typeof(Func<int, int>), offset) };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static DynamicMethod BuildWithoutDelegate(int offset)
    {
        var method = new DynamicMethod("DirectInvoke", typeof(int), new[] { typeof(int) });
        Emit(method, offset);
        return method;
    }

    // Matching method/holder names deliberately make assembly-level attribution insufficient.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object[] Build(int offset)
    {
        var method = new DynamicMethod("SameName", typeof(int), new[] { typeof(int) });
        return Complete(method, offset);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object[] Complete(DynamicMethod method, int offset)
    {
        Emit(method, offset);
        return new object[] { method, method.CreateDelegate(typeof(Func<int, int>)) };
    }

    private static void Emit(DynamicMethod method, int offset)
    {
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, offset);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
    }
}
