using System.Diagnostics;
using System.Reflection;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Positive original-Mod callback test. Only isolated synthetic assets are used.
public sealed class ControlledBackgroundChecks : MonoBehaviour
{
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private bool done;
    public ControlledBackgroundChecks(IntPtr pointer) : base(pointer) { }
    public void Update()
    {
        if (done) return;
        if (!RenderControlV1.IsReady || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "CatalogScene")
        {
            if (wall.Elapsed.TotalSeconds > 50) { done = true; Plugin.Report("PATH-CHECK RESULT 0 passed; 1 failed"); }
            return;
        }
        done = true; Run();
    }
    [HideFromIl2Cpp]
    private static void Run()
    {
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        IDisposable? lease = null;
        GameObject? owner = null;
        Texture2D? still = null;
        Type? adapter = null;
        bool success = false;
        try
        {
            string assets = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_BACKGROUND_ASSETS") ?? "";
            if (!Path.IsPathFullyQualified(assets) || !File.Exists(Path.Combine(assets, "controlled-bg.mkv")))
                throw new InvalidOperationException("Explicit isolated background fixture assets are required.");
            var mod = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "FlandreScarletBackground");
            var overlay = mod.GetType("FlandreScarletBackground.Background.FLVideoOverlay", true)!;
            var originalSet = overlay.GetMethod("OnLayerSet", members)!;
            adapter = typeof(AAVideoExport.Plugin.Plugin).Assembly.GetType("AAVideoExport.Plugin.ControlledBackgroundVideoAdapter");
            if (adapter == null) throw new InvalidOperationException("Original dynamic background has no AAVE frame-controlled backend.");
            owner = new GameObject("Controlled background native fixture");
            owner.SetActive(false);
            var image = owner.AddComponent<UITexture>();
            still = new Texture2D(8, 4, TextureFormat.RGBA32, false);
            image.mainTexture = still;
            var layer = new BackgroundPresenter.Layer(image, 0, null);
            var resource = new BackgroundResource(0, "controlled-bg", ScenarioBGType.Image,
                ScenarioResourceManager.ResourceType.LocalCustom, path: "controlled-bg.png", root: assets, spine: null);
            if (Path.GetFullPath(resource.FullPath) != Path.GetFullPath(Path.Combine(assets, "controlled-bg.png")))
                throw new InvalidOperationException("Native BackgroundResource path did not resolve to fixture assets.");
            var content = new BackgroundPresenter.Content { Resource = resource, Image = still };
            lease = (IDisposable)typeof(RenderControlV1).GetMethod("Acquire", members)!.Invoke(null, null)!;
            long frame = 0;
            var provider = typeof(AAVideoExport.Plugin.Plugin);
            string ffmpeg = (string)provider.GetField("FfmpegPath", members)!.GetValue(null)!;
            string ffprobe = (string)provider.GetField("FfprobePath", members)!.GetValue(null)!;
            adapter.GetMethod("BeginExport", members)!.Invoke(null, new object[]
                { (Func<long>)(() => frame), 30, Path.Combine(Plugin.DiagnosticsRoot, "frame-store"), ffmpeg, ffprobe });
            originalSet.Invoke(null, new object[] { layer, content });
            foreach (var point in new[] { (0L, 0), (3L, 1), (6L, 2), (9L, 3), (12L, 0) })
            {
                frame = point.Item1;
                adapter.GetMethod("Pump", members)!.Invoke(null, new object[] { frame });
                CheckFrame(image.mainTexture, assets, point.Item2);
                if (frame == 6)
                {
                    // Rebinding the same source must retain its playhead.
                    originalSet.Invoke(null, new object[] { layer, content });
                    adapter.GetMethod("Pump", members)!.Invoke(null, new object[] { frame });
                    CheckFrame(image.mainTexture, assets, 2);
                    // Native UI can rebuild the image while retaining the same
                    // layer/content; the playhead must migrate to the new image.
                    var replacementObject = new GameObject("Replacement native background image");
                    replacementObject.transform.SetParent(owner.transform, false);
                    var replacement = replacementObject.AddComponent<UITexture>();
                    replacement.mainTexture = still;
                    layer.Image = replacement;
                    adapter.GetMethod("Pump", members)!.Invoke(null, new object[] { frame });
                    CheckFrame(replacement.mainTexture, assets, 2);
                    if (image.mainTexture == null || image.mainTexture.Pointer != still.Pointer)
                        throw new InvalidOperationException("Old native image kept an owned video texture.");
                    image = replacement;
                }
            }
            // Native cross-fade geometry/alpha stay owned by the original
            // Layer API while each layer keeps its own source playhead.
            var overlayObject = new GameObject("Second native background layer");
            overlayObject.transform.SetParent(owner.transform, false);
            var overlayImage = overlayObject.AddComponent<UITexture>();
            overlayImage.mainTexture = still;
            overlayImage.alpha = .35f;
            var overlayLayer = new BackgroundPresenter.Layer(overlayImage, 1, null);
            var secondResource = new BackgroundResource(1, "controlled-bg-2", ScenarioBGType.Image,
                ScenarioResourceManager.ResourceType.LocalCustom, path: "controlled-bg-2.png", root: assets, spine: null);
            var secondContent = new BackgroundPresenter.Content { Resource = secondResource, Image = still };
            frame = 15;
            originalSet.Invoke(null, new object[] { overlayLayer, secondContent });
            adapter.GetMethod("Pump", members)!.Invoke(null, new object[] { frame });
            CheckFrame(image.mainTexture, assets, 1);
            CheckFrame(overlayImage.mainTexture, assets, 3);
            if (Math.Abs(overlayImage.alpha - .35f) > .0001f) throw new InvalidOperationException("Native transition alpha changed.");
            frame = 18;
            adapter.GetMethod("Pump", members)!.Invoke(null, new object[] { frame });
            CheckFrame(image.mainTexture, assets, 2);
            CheckFrame(overlayImage.mainTexture, assets, 2);
            var staticResource = new BackgroundResource(2, "static-only", ScenarioBGType.Image,
                ScenarioResourceManager.ResourceType.LocalCustom, path: "static-only.png", root: assets, spine: null);
            originalSet.Invoke(null, new object[] { overlayLayer, new BackgroundPresenter.Content { Resource = staticResource, Image = still } });
            adapter.GetMethod("Pump", members)!.Invoke(null, new object[] { frame });
            if (overlayImage.mainTexture.Pointer != still.Pointer) throw new InvalidOperationException("Static switch retained the old dynamic video.");
            // A second pass starts from frame zero, with original native texture
            // ownership restored between passes.
            adapter.GetMethod("EndExport", members)!.Invoke(null, null);
            if (image.mainTexture == null || image.mainTexture.Pointer != still.Pointer)
                throw new InvalidOperationException("Native layer texture was not restored.");
            frame = 0;
            adapter.GetMethod("BeginExport", members)!.Invoke(null, new object[]
                { (Func<long>)(() => frame), 30, Path.Combine(Plugin.DiagnosticsRoot, "frame-store-retry"), ffmpeg, ffprobe });
            originalSet.Invoke(null, new object[] { layer, content });
            adapter.GetMethod("Pump", members)!.Invoke(null, new object[] { 0L });
            CheckFrame(image.mainTexture, assets, 0);
            adapter.GetMethod("EndExport", members)!.Invoke(null, null);
            lease.Dispose(); lease = null;
            if (RenderControlV1.IsRenderingOwned) throw new InvalidOperationException("Background capture leaked ownership.");
            success = true;
            Plugin.Report("PATH-CHECK PASS original-background-callback-first-frame-loop-rebind-retry-restore");
        }
        catch (Exception error) { Plugin.Report("PATH-CHECK FAIL controlled-background: " + error); }
        finally
        {
            try { adapter?.GetMethod("EndExport", members)?.Invoke(null, null); lease?.Dispose(); if (owner != null) Object.Destroy(owner); if (still != null) Object.Destroy(still); }
            catch (Exception error) { success = false; Plugin.Report("PATH-CHECK FAIL background-cleanup: " + error); }
        }
        Plugin.Report($"PATH-CHECK RESULT {(success ? 1 : 0)} passed; {(success ? 0 : 1)} failed");
    }
    [HideFromIl2Cpp]
    private static void CheckFrame(Texture texture, string assets, int index)
    {
        var decoded = texture.TryCast<Texture2D>() ?? throw new InvalidOperationException("Controlled texture is not Texture2D.");
        var pixels = decoded.GetPixels32();
        byte[] original = File.ReadAllBytes(Path.Combine(assets, $"frame-{index}.rgba"));
        if (decoded.width != 8 || decoded.height != 4 || pixels.Length != 32)
            throw new InvalidOperationException("Controlled frame dimensions changed.");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 8; x++)
        {
            var actual = pixels[y * 8 + x];
            int expected = ((3 - y) * 8 + x) * 4; // FFmpeg top-down -> Unity bottom-up.
            if (actual.r != original[expected] || actual.g != original[expected + 1] ||
                actual.b != original[expected + 2] || actual.a != original[expected + 3])
                throw new InvalidOperationException($"Native texture pixel mismatch: frame={index}, x={x}, y={y}.");
        }
    }
}
