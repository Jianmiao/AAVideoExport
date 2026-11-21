using System.Diagnostics;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace UnknownClockMod;

// Managed clock fixture. It is intentionally not a MonoBehaviour: the policy
// must reject it because it reads wall/Unity time, not because of its name.
[BepInPlugin(Guid, "Unknown Clock Mod", "0.1.0")]
[BepInProcess("AzureArchive.exe")]
public sealed class UnknownClockPlugin : BasePlugin
{
    public const string Guid = "aave.fixture.unknown-clock";

    public override void Load() => Log.LogInfo("UnknownClockMod loaded; clock probe only");

    public static long ReadWallTimestamp() => Stopwatch.GetTimestamp();

    public static float ReadUnityRealtime() => UnityEngine.Time.realtimeSinceStartup;
}
