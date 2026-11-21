using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine.Video;

namespace AAVideoExport.NativePath.Tests;

// Independent test reader. Run44 proved the generated Boolean result can stay
// true while the exact native function returns zero. Acceptance therefore uses
// the checked native byte; generated values remain in diagnostic evidence.
// This does not call the production adapter reader or change native state.
internal static class NativeVideoBooleanProbe
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static bool ReadState(VideoPlayer player, string state)
    {
        if (state is not ("isPrepared" or "isPlaying" or "isPaused" or "isLooping")) throw new ArgumentOutOfRangeException(nameof(state));
        IntPtr native = UnityEngine.Object.MarshalledUnityObject.MarshalNotNull(player);
        if (native == IntPtr.Zero) throw new InvalidOperationException("Native marshalled VideoPlayer pointer is zero.");
        return NativeByte(state, native).Raw == 1;
    }

    internal static object Capture(VideoPlayer player)
    {
        try
        {
            IntPtr native = UnityEngine.Object.MarshalledUnityObject.MarshalNotNull(player);
            if (native == IntPtr.Zero) throw new InvalidOperationException("Native marshalled VideoPlayer pointer is zero.");
            return new
            {
                NativePointer = native.ToInt64(),
                Prepared = Read("isPrepared", native, player.isPrepared),
                Playing = Read("isPlaying", native, player.isPlaying),
                Paused = Read("isPaused", native, player.isPaused),
                Looping = Read("isLooping", native, player.isLooping),
                Scope = "Independent checked native-byte acceptance; generated Boolean values are diagnostic only after run44 proved their ABI mismatch."
            };
        }
        catch (Exception error) { return new { UnsupportedDiagnostic = error.GetBaseException().Message }; }
    }

    private static object Read(string state, IntPtr native, bool generated)
    {
        var value = NativeByte(state, native);
        return new { Generated = generated, RawByte = value.Raw, Function = value.Function.ToInt64(), SameFunction = true,
            ValidNativeBooleanByte = true };
    }

    private static unsafe (byte Raw, IntPtr Function) NativeByte(string state, IntPtr native)
    {
        string getter = "get_" + state + "_Injected";
        IntPtr function = IL2CPP.il2cpp_resolve_icall("UnityEngine.Video.VideoPlayer::" + getter);
        if (function == IntPtr.Zero) throw new MissingMethodException("Pinned native getter could not resolve: " + getter);
        var generatedDelegate = (Delegate)(typeof(VideoPlayer).GetField(getter + "DelegateField", Flags)!
            .GetValue(null) ?? throw new InvalidOperationException("Pinned generated delegate is missing."));
        IntPtr generatedFunction = Marshal.GetFunctionPointerForDelegate(generatedDelegate);
        if (generatedFunction != function) throw new InvalidOperationException("Native and generated getter addresses differ: " + getter);
        byte raw = ((delegate* unmanaged[Cdecl]<IntPtr, byte>)function)(native);
        if (raw is not (0 or 1)) throw new InvalidOperationException("Native getter returned a non-Boolean byte: " + getter);
        return (raw, function);
    }
}
