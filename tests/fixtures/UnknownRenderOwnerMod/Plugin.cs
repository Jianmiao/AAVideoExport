using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace UnknownRenderOwnerMod;

// A real render-ownership fixture: it creates an enabled camera that owns a
// render texture and writes a solid magenta frame after the normal display
// camera. No AAVE-side Mod identity is involved; the exporter must discover the
// camera only from its targetTexture ownership.
[BepInPlugin(Guid, "Unknown Render Owner Mod", "0.2.0")]
[BepInProcess("AzureArchive.exe")]
public sealed class UnknownRenderOwnerPlugin : BasePlugin
{
    public const string Guid = "aave.fixture.unknown-render-owner";
    public static Camera OwnerCamera { get; private set; } = null!;
    public static RenderTexture OwnerTarget { get; private set; } = null!;

    public override void Load()
    {
        var gameObject = new GameObject("UnknownRenderOwnerCamera");
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        OwnerCamera = gameObject.AddComponent<Camera>();
        OwnerCamera.depth = 100f;
        OwnerCamera.clearFlags = CameraClearFlags.SolidColor;
        OwnerCamera.backgroundColor = Color.magenta;
        OwnerCamera.cullingMask = 0;
        OwnerTarget = new RenderTexture(320, 180, 24);
        OwnerTarget.name = "UnknownRenderOwnerTarget";
        OwnerCamera.targetTexture = OwnerTarget;
        OwnerCamera.enabled = true;
        Log.LogInfo("UnknownRenderOwnerMod loaded; offscreen camera owns " + OwnerTarget.name
            + "; depth=" + OwnerCamera.depth
            + "; clear=" + OwnerCamera.clearFlags
            + "; culling=" + OwnerCamera.cullingMask
            + "; enabled=" + OwnerCamera.enabled);
    }
}
