using System.Collections;
using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using Il2CppInterop.Runtime.Attributes;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Exercise production NGUI controls, not a UI mock; no desktop input or story export.
public sealed class SettingsChecks : MonoBehaviour
{
    private NativeExportPanel? _panel;
    private int _stage;
    private float _after;
    private float _shellHeight, _shellWidth;
    private UIInput? _bitrate;
    private RenderTexture? _surface;
    private static Harmony? _fixtureHooks;
    private const int FixtureWidth = 2560, FixtureHeight = 1334;
    public SettingsChecks(IntPtr pointer) : base(pointer) { }

    [HideFromIl2Cpp]
    internal static void InstallViewportFixture()
    {
        // A hidden/minimized Windows client can be 215x1. Exercise the same
        // production native UI at the reference dimensions without resizing
        // the user's window or sending any desktop input.
        _fixtureHooks = new Harmony("halocue.aa.videoexport.settingsviewportfixture");
        _fixtureHooks.Patch(AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.width)), prefix: new HarmonyMethod(typeof(SettingsChecks), nameof(WidthPrefix)));
        _fixtureHooks.Patch(AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.height)), prefix: new HarmonyMethod(typeof(SettingsChecks), nameof(HeightPrefix)));
    }
    [HideFromIl2Cpp]
    private static bool WidthPrefix(ref int __result) { __result = FixtureWidth; return false; }
    [HideFromIl2Cpp]
    private static bool HeightPrefix(ref int __result) { __result = FixtureHeight; return false; }

    public void Update()
    {
        if (_stage == 99 || Time.realtimeSinceStartup < 12f) return;
        try
        {
            _panel?.Tick();
            if (Time.realtimeSinceStartup < _after) return;
            if (_stage == 0)
            {
                _panel = new NativeExportPanel
                {
                    Options = new ExportOptions { Title = "单页导出设置验收", Width = 1280, Height = 720,
                        OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                        Fps = 60, BitrateKbps = 6000, AudioQuality = "aac320", AudioSampleRate = 44100 }, Visible = true
                };
                _panel.Draw("单页导出设置验收", "Intel", "功能验收", false, 0, 0, null,
                    new[] { new EncoderCapability("h264_qsv", "h264", true, true, "UI fixture") });
                Call("SetResolution", "720");
                _panel.Tick();
                Next(1);
                return;
            }
            if (Field<GameObject?>("_root") == null)
            {
                if (Time.realtimeSinceStartup > 30) throw new InvalidOperationException("Panel hierarchy unavailable");
                return;
            }
            if (_surface == null)
            {
                _surface = new RenderTexture(FixtureWidth, FixtureHeight, 24, RenderTextureFormat.ARGB32);
                _surface.Create();
                var camera = Field<Camera>("_nativeCamera");
                camera.targetTexture = _surface;
                camera.aspect = FixtureWidth / (float)FixtureHeight;
                Next(_stage);
                return;
            }
            CheckGeometry();
            CheckUnified();
            if (_stage == 1)
            {
                if (Field<GameObject>("_advancedSection").activeInHierarchy) throw new InvalidOperationException("Advanced must start collapsed");
                SavePanel("unified-collapsed.png");
                Click("高级设置", true);
                Next(2); return;
            }
            if (_stage == 2)
            {
                Check(Field<GameObject>("_advancedSection").activeInHierarchy, "Advanced expands in place");
                Check(Field<ExportSettingsNavigation>("_settingsNavigation").ScrollOffset == 0, "Expand should not jump scroll");
                SavePanel("unified-expanded-top.png");
                Call("ScrollSettings", float.MaxValue);
                Next(3); return;
            }
            if (_stage == 3)
            {
                CheckGeometry();
                foreach (var field in Field<IList>("_fields"))
                {
                    var read = (Func<string>)field!.GetType().GetProperty("Read")!.GetValue(field)!;
                    if (read() == "6.0") { _bitrate = (UIInput)field.GetType().GetProperty("Input")!.GetValue(field)!; break; }
                }
                Check(_bitrate != null && _bitrate.enabled, "Bottom scroll reveals bitrate field");
                _bitrate!.value = "8.";
                Call("PollInputs");
                Check(Field<string>("_bitrate") == "8.", "Pending decimal is retained");
                Check(_panel!.Options.BitrateKbps == 8000, "Bitrate model updated");
                SavePanel("unified-expanded-bottom.png");
                // Return to the entry header and use the same button to collapse.
                var geometry = Field<ExportPanelLayout>("_settingsGeometry");
                float offset = geometry.AdvancedY - ExportPanelLayout.ContentTop;
                var nav = Field<ExportSettingsNavigation>("_settingsNavigation");
                Call("ScrollSettings", offset - nav.ScrollOffset);
                Next(4); return;
            }
            if (_stage == 4)
            {
                Click("收起高级设置", true);
                Next(5); return;
            }
            if (_stage == 5)
            {
                Check(!Field<GameObject>("_advancedSection").activeInHierarchy && !_bitrate!.gameObject.activeInHierarchy,
                    "Collapse hides only advanced controls");
                Check(_panel!.Options.BitrateKbps == 8000 && Field<string>("_bitrate") == "8.", "Collapse retains manual bitrate draft");
                Check(Field<ExportSettingsNavigation>("_settingsNavigation").ScrollOffset == 0, "Collapse clamps obsolete scroll");
                Click("开启超分辨率", true);
                Check(_panel.Options.SuperResolutionEnabled, "720p toggle remains available");
                Next(6); return;
            }
            if (_stage == 6)
            {
                var plan = _panel!.Options.GetCanvasLayout();
                Check(plan.CaptureWidth == 854 && plan.CaptureHeight == 480 && plan.OutputWidth == 1280 && plan.OutputHeight == 720,
                    "720p proxy plan unchanged");
                SavePanel("unified-sr720.png");
                Call("ScrollSettings", float.MaxValue);
                Next(7); return;
            }
            if (_stage == 7)
            {
                // Actual dropdown callback: choose an audio option in the shared page.
                Click("AAC 320 Kbps", false);
                Next(8); return;
            }
            if (_stage == 8)
            {
                SavePanel("unified-audio-dropdown.png");
                ChoosePopup("AAC 128 Kbps");
                Check(_panel!.Options.AudioQuality == "aac128", "Audio selection updates on the unified page");
                Click("高级设置", true);
                Next(9); return;
            }
            if (_stage == 9)
            {
                Check(Field<string>("_bitrate") == "8." && _panel!.Options.BitrateKbps == 8000, "Reopen retains bitrate");
                Check(_panel!.Options.AudioQuality == "aac128" && _panel.Options.AudioSampleRate == 44100 && _panel.Options.Fps == 60,
                    "Expand/collapse retains audio and video settings");
                _panel.Dispose(); _panel = null;
                CleanupViewportFixture();
                Plugin.Report("PATH-CHECK PASS unified-video-audio-without-tabs");
                Plugin.Report("PATH-CHECK PASS accordion-scroll-draft-retention-and-audio-selection");
                Plugin.Report("PATH-CHECK PASS reference-proportion-and-720p-regression");
                Plugin.Report("PATH-CHECK RESULT 3 passed; 0 failed");
                _stage = 99;
            }
        }
        catch (Exception error)
        {
            _stage = 99;
            try { _panel?.Dispose(); } catch { }
            CleanupViewportFixture();
            var cause = error is TargetInvocationException e ? e.InnerException : error;
            Plugin.Report("PATH-CHECK FAIL unified settings: " + cause?.GetType().Name + " " + cause?.Message);
            Plugin.Report("PATH-CHECK RESULT 0 passed; 1 failed");
        }
    }

    [HideFromIl2Cpp]
    private void Next(int stage) { _stage = stage; _after = Time.realtimeSinceStartup + .5f; }
    [HideFromIl2Cpp]
    private void CleanupViewportFixture()
    {
        _fixtureHooks?.UnpatchSelf();
        if (_surface != null) { _surface.Release(); Object.Destroy(_surface); _surface = null; }
    }
    [HideFromIl2Cpp]
    private void CheckUnified()
    {
        Check(Field<GameObject>("_outputSection").activeInHierarchy && Field<GameObject>("_audioSection").activeInHierarchy,
            "Both output and audio sections remain in the same page");
        foreach (var entry in Field<IList>("_buttons"))
        {
            var read = (Func<string>)entry!.GetType().GetProperty("Text")!.GetValue(entry)!;
            Check(read() is not ("画面与画质" or "格式与音频" or "‹  返回导出设置"), "Removed tabs/back page must not exist");
        }
    }
    [HideFromIl2Cpp]
    private void Click(string text, bool partial)
    {
        foreach (var entry in Field<IList>("_buttons"))
        {
            var t = entry!.GetType();
            var read = (Func<string>)t.GetProperty("Text")!.GetValue(entry)!;
            if (!(partial ? read().Contains(text) : read() == text)) continue;
            var background = (UITexture)t.GetProperty("Background")!.GetValue(entry)!;
            if (!background.gameObject.activeInHierarchy) continue;
            Check(((BoxCollider)t.GetProperty("Collider")!.GetValue(entry)!).enabled, "Requested button is outside active viewport: " + text);
            UIEventListener.Get(background.gameObject).onClick.Invoke(background.gameObject);
            return;
        }
        throw new InvalidOperationException("Button missing: " + text);
    }
    [HideFromIl2Cpp]
    private void ChoosePopup(string title)
    {
        foreach (var row in Field<IList>("_popupRows"))
        {
            var type = row!.GetType();
            var label = (UILabel)type.GetProperty("Title")!.GetValue(row)!;
            if (label.text != title) continue;
            var background = (UITexture)type.GetProperty("Background")!.GetValue(row)!;
            UIEventListener.Get(background.gameObject).onClick.Invoke(background.gameObject);
            return;
        }
        throw new InvalidOperationException("Audio dropdown item not present");
    }
    [HideFromIl2Cpp]
    private void CheckGeometry()
    {
        var camera = Field<Camera>("_nativeCamera");
        var corners = Field<UITexture>("_paperSurface").worldCorners;
        float bottom = 1, top = 0, left = 1, right = 0;
        foreach (var corner in corners)
        {
            var p = camera.WorldToViewportPoint(corner);
            bottom = Math.Min(bottom, p.y); top = Math.Max(top, p.y);
            left = Math.Min(left, p.x); right = Math.Max(right, p.x);
        }
        float height = top-bottom, width=right-left;
        float ratio = width * Screen.width / (height * Screen.height);
        Check(Math.Abs(ratio - 980f / 800f) < .01f, "Modal ratio must match reference");
        Check(height <= .901f && width <= .901f && (height > .89f || width > .89f), $"Modal must fit 90% with equal scale: width={width:F3}, height={height:F3}");
        if (_shellHeight != 0) Check(Math.Abs(height-_shellHeight)<.001f && Math.Abs(width-_shellWidth)<.001f, "Expansion must not resize modal");
        _shellHeight=height; _shellWidth=width;
    }
    [HideFromIl2Cpp]
    private void SavePanel(string filename)
    {
        var camera=Field<Camera>("_nativeCamera");
        var target=new RenderTexture(Screen.width,Screen.height,24,RenderTextureFormat.ARGB32);
        var oldTarget=camera.targetTexture; var oldActive=RenderTexture.active; Texture2D? image=null;
        try
        {
            target.Create(); camera.targetTexture=target; camera.Render(); RenderTexture.active=target;
            image=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);
            image.ReadPixels(new Rect(0,0,target.width,target.height),0,0); image.Apply();
            string dir=Path.Combine(Plugin.DiagnosticsRoot,"single-page-export");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir,filename),ImageConversion.EncodeToPNG(image).ToArray());
        }
        finally
        {
            camera.targetTexture=oldTarget; RenderTexture.active=oldActive;
            if(image!=null)Object.Destroy(image); target.Release(); Object.Destroy(target);
        }
    }
    [HideFromIl2Cpp]
    private T Field<T>(string name)=>(T)typeof(NativeExportPanel).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(_panel)!;
    [HideFromIl2Cpp]
    private void Call(string name,params object[] args)=>typeof(NativeExportPanel).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(_panel,args);
    [HideFromIl2Cpp]
    private static void Check(bool valid,string message){if(!valid)throw new InvalidOperationException(message);}
}
