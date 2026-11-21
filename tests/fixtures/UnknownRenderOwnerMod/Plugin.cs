using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace UnknownRenderOwnerMod;

// Render-ownership fixture shell. It does not take over rendering at runtime;
// its type references are enough for the generic policy to reject it as non-
// passive. A later real fixture must implement the actual takeover.
[BepInPlugin(Guid, "Unknown Render Owner Mod", "0.1.0")]
[BepInProcess("AzureArchive.exe")]
public sealed class UnknownRenderOwnerPlugin : BasePlugin
{
    public const string Guid = "aave.fixture.unknown-render-owner";
    public static UnityEngine.Camera OwnerCamera { get; set; }
    public static UnityEngine.RenderTexture OwnerTarget { get; set; }

    public override void Load() => Log.LogInfo("UnknownRenderOwnerMod loaded; render-owner shell");
}
