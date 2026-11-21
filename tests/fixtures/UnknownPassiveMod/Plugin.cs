using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace UnknownPassiveMod;

// Deliberately passive: no Harmony, no MonoBehaviour callbacks, no clock,
// render, audio, or video API. It must not require any AAVE code change.
[BepInPlugin(Guid, "Unknown Passive Mod", "0.1.0")]
[BepInProcess("AzureArchive.exe")]
public sealed class UnknownPassivePlugin : BasePlugin
{
    public const string Guid = "aave.fixture.unknown-passive";
    public override void Load() => Log.LogInfo("UnknownPassiveMod loaded; no game behavior");
}
