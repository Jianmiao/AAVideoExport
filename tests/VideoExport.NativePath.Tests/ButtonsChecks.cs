using System.Reflection;
using AAVideoExport.Core;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Exercises the built-in playback scene with Test.Start disabled. No user
// project or story is loaded. RenderTextures capture only local test evidence.
public sealed class ButtonsChecks : MonoBehaviour
{
    private static Harmony? _hooks;
    private AsyncOperation? _load;
    private int _stage, _settleFrames, _passed, _failed;
    private float _started;
    public ButtonsChecks(IntPtr pointer) : base(pointer) { }

    [HideFromIl2Cpp]
    internal static void InstallHooks()
    {
        _hooks = new Harmony("halocue.aa.videoexport.nativebuttonschecks");
        _hooks.Patch(AccessTools.Method(typeof(Test), nameof(Test.Start)),
            prefix: new HarmonyMethod(typeof(ButtonsChecks), nameof(SkipStoryStart)));
    }

    [HideFromIl2Cpp]
    private static bool SkipStoryStart(Test __instance)
    {
        Plugin.Report("PATH-CHECK Test.Start scene=" + __instance.gameObject.scene.name);
        if (__instance.gameObject.scene.buildIndex != 4) return true;
        __instance.enabled = false;
        return false;
    }

    public void Update()
    {
        if (_stage == 3) return;
        try
        {
            if (_stage == 0)
            {
                // Let the startup scene finish its own scene switch first;
                // otherwise that switch unloads an additive fixture mid-test.
                if (Time.realtimeSinceStartup < 15f) return;
                Plugin.Report("PATH-CHECK initial-scene=" + SceneManager.GetActiveScene().name);
                _started = Time.realtimeSinceStartup;
                _load = SceneManager.LoadSceneAsync(4, LoadSceneMode.Additive);
                _stage = 1;
                return;
            }
            if (_stage == 1)
            {
                if (_load == null || !_load.isDone) return;
                if (++_settleFrames < 4) return;
                _stage = 2;
                return;
            }
            var players = Resources.FindObjectsOfTypeAll<Test>().ToArray();
            Plugin.Report("PATH-CHECK fixture-scenes=" + SceneManager.sceneCount + " test-objects=" + players.Length);
            for (int i = 0; i < SceneManager.sceneCount; i++)
                Plugin.Report("PATH-CHECK loaded-scene=" + SceneManager.GetSceneAt(i).name + " index=" + SceneManager.GetSceneAt(i).buildIndex);
            foreach (var root in SceneManager.GetSceneByBuildIndex(4).GetRootGameObjects())
                Plugin.Report("PATH-CHECK root=" + root.name + " components=" + string.Join(",", root.GetComponents<MonoBehaviour>().Select(c => c.GetIl2CppType().Name)));
            var scene = SceneManager.GetSceneByBuildIndex(4);
            var player = players.FirstOrDefault(p => p.gameObject.scene.buildIndex == 4);
            if (player == null)
            {
                // Catalog owns AA singletons; an additive UI fixture can have
                // its Test singleton removed by native Awake. Bind an inactive
                // holder to the actual serialized controls without starting a story.
                var holder = new GameObject("AA export viewport test holder");
                holder.SetActive(false);
                SceneManager.MoveGameObjectToScene(holder, scene);
                player = holder.AddComponent<Test>();
                player.enabled = false;
                var uiRoot = scene.GetRootGameObjects().First(o => o.name == "UI Root");
                var top = uiRoot.transform.Find("FrontUI/Top");
                player.menu = top.gameObject;
                player.menuBtn = top.Find("MenuButton_Normal").GetComponent<UI.MenuButton>();
                player.autoToggle = top.Find("AutoBtn").GetComponent<UI.MXToggle>();
            }
            if (ScenarioResourceManager.Instance != null && ScenarioResourceManager.Instance.Preloading)
            {
                if (Time.realtimeSinceStartup - _started > 20) throw new InvalidOperationException("Preloading did not finish");
                return;
            }
            _stage = 3;
            RunCase(player, false);
            RunCase(player, true);
            Plugin.Report($"PATH-CHECK RESULT {_passed} passed; {_failed} failed");
        }
        catch (Exception error)
        {
            _stage = 3;
            var cause = error is TargetInvocationException invocation ? invocation.InnerException : error;
            Plugin.Report("PATH-CHECK FAIL native-buttons-setup: " + cause?.GetType().Name + " " + cause?.Message);
            Plugin.Report($"PATH-CHECK RESULT {_passed} passed; {_failed + 1} failed");
        }
    }

    [HideFromIl2Cpp]
    private void RunCase(Test player, bool show)
    {
        string evidenceRoot = Path.Combine(Plugin.DiagnosticsRoot, "native-buttons");
        IDisposable? scope = null;
        var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
        target.Create();
        var oldActive = RenderTexture.active;
        Texture2D? image = null;
        try
        {
            if (player.menu == null || player.menuBtn == null || player.autoToggle == null || player.menuBtn.menuPopup == null)
                throw new InvalidOperationException("Native scene controls missing");
            Plugin.Report("PATH-CHECK native-hierarchy top=" + player.menu.name +
                " popup=" + player.menuBtn.menuPopup.name +
                " menu_child=" + player.menuBtn.transform.IsChildOf(player.menu.transform) +
                " auto_child=" + player.autoToggle.transform.IsChildOf(player.menu.transform));
            var options = new ExportOptions { Width = 1920, Height = 1080, SourceWidth = Screen.width, SourceHeight = Screen.height, CanvasMode = "viewport" };
            var type = typeof(AAVideoExport.Plugin.ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeCaptureScope", true)!;
            scope = (IDisposable)Activator.CreateInstance(type, player, options, target, show, false)!;
            type.GetMethod("RenderFrame")!.Invoke(scope, new object[] { target });
            image = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
            image.Apply();
            Directory.CreateDirectory(evidenceRoot);
            File.WriteAllBytes(Path.Combine(evidenceRoot, show ? "checked.png" : "unchecked.png"), ImageConversion.EncodeToPNG(image).ToArray());
            bool menu = player.menuBtn.gameObject.activeInHierarchy;
            bool auto = player.autoToggle.gameObject.activeInHierarchy;
            bool popup = player.menuBtn.menuPopup.activeInHierarchy;
            Plugin.Report($"PATH-CHECK native-show={show} menu_visible={menu} auto_visible={auto} popup_visible={popup}");
            if (menu != show || auto != show || popup) throw new InvalidOperationException("Captured control hierarchy does not match option");
            _passed++;
            Plugin.Report("PATH-CHECK PASS native-buttons-" + show);
        }
        catch (Exception error)
        {
            _failed++;
            var cause = error is TargetInvocationException invocation ? invocation.InnerException : error;
            Plugin.Report("PATH-CHECK FAIL native-buttons-" + show + ": " + cause?.GetType().Name);
        }
        finally
        {
            scope?.Dispose();
            RenderTexture.active = oldActive;
            if (image != null) Object.Destroy(image);
            target.Release();
            Object.Destroy(target);
        }
    }
}
