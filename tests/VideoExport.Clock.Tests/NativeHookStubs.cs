// Portable boundary doubles mirror the exact AA 101 interop properties.
// They exercise production hook/clock logic, not native Harmony dispatch.
namespace Il2CppSystem
{
    internal class Object
    {
        private static long next;
        public IntPtr Pointer { get; } = new(++next);
        public T? TryCast<T>() where T : class => this as T;
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    internal sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type type, string method) { }
    }
    internal sealed class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    internal static class Priority { public const int Last = 0; }
}

namespace FlatData
{
    internal class IScenarioScriptExcel : Il2CppSystem.Object
    {
        public string ScriptKr => throw new InvalidOperationException("Unsafe native interface getter dispatch.");
    }
}

internal sealed class GenericScenarioExcel : FlatData.IScenarioScriptExcel
{
    public new string ScriptKr { get; set; } = "";
    public Type GetIl2CppType() => typeof(GenericScenarioExcel);
}

namespace Utils
{
    internal static class ScenarioUtil
    {
        public static void ParseScript() => throw new NotSupportedException("Native original is not simulated.");
        internal sealed class ScriptInfo
        {
            public List<Il2CppSystem.Object> scenarioAnimations = new();
        }
    }
}

namespace UnityEngine
{
    internal sealed class WaitForSeconds : Il2CppSystem.Object
    {
        public float m_Seconds;
        public WaitForSeconds(float seconds) => m_Seconds = seconds;
    }
}

internal sealed class Test : Il2CppSystem.Object
{
    public sealed class _DelayedAdvance_d__82
    {
        public Test __4__this { get; set; } = null!;
        public Il2CppSystem.Object? __2__current { get; set; }
        public bool MoveNext() => throw new NotSupportedException("Native original is not simulated.");
    }
}

namespace ScenarioAnimation
{
    internal sealed class WaitAnimation : Il2CppSystem.Object
    {
        public readonly Test owner;
        public float delayInSeconds;
        public WaitAnimation(float seconds, Test player) { delayInSeconds = seconds; owner = player; }
        public sealed class _WaitTask_d__4
        {
            public WaitAnimation __4__this { get; set; } = null!;
            public float delayInSeconds { get; set; }
            public Il2CppSystem.Object? __2__current { get; set; }
            public bool MoveNext() => throw new NotSupportedException("Native original is not simulated.");
        }
    }
}

namespace AAVideoExport.Plugin
{
    internal sealed class ExportHost
    {
        private readonly Test player;
        internal ExportHost(Test player) => this.player = player;
        internal static ExportHost? Current;
        internal bool Capturing;
        internal long CapturedFrame;
        internal int CaptureFps;
        internal bool OwnsCapturePlayer(Test candidate) => candidate.Pointer == player.Pointer;
    }
}
