using System;
using System.Diagnostics;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace UnknownClockMod;

// Managed clock fixture. It deliberately reads wall/Unity time from a live
// scenario callback so a generic clock implementation has an observable
// event to synchronize, not merely a static call site.
[BepInPlugin(Guid, "Unknown Clock Mod", "0.1.0")]
[BepInProcess("AzureArchive.exe")]
public sealed class UnknownClockPlugin : BasePlugin
{
    public const string Guid = "aave.fixture.unknown-clock";

    private static Harmony _harmony;
    private static long _events;
    private static UnknownClockPlugin _self;

    public override void Load()
    {
        _self = this;
        _harmony = new Harmony(Guid);
        var target = AccessTools.Method(typeof(Test), "Update");
        if (target == null)
        {
            Log.LogError("UnknownClockMod: Test.Update not found");
            return;
        }
        _harmony.Patch(target, postfix: new HarmonyMethod(typeof(UnknownClockPlugin), nameof(AfterAdvance)));
        Log.LogInfo("UnknownClockMod loaded; live scenario clock probe installed");
    }

    private static void AfterAdvance()
    {
        long index = _events++;
        if (index % 15 != 0) return;
        try
        {
            _self.Log.LogInfo($"UnknownClockMod event frame={index} unityFrame={UnityEngine.Time.frameCount} wall={ReadWallTimestamp()} unity={ReadUnityRealtime():F6}");
        }
        catch (Exception error)
        {
            _self.Log.LogError("UnknownClockMod probe failed: " + error);
        }
    }

    public static long ReadWallTimestamp() => Stopwatch.GetTimestamp();

    public static float ReadUnityRealtime() => UnityEngine.Time.realtimeSinceStartup;
}
