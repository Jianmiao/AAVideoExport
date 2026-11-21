using System.Diagnostics;
using System.Reflection;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Positive effect test: the original Mod must translate labels while AAVE
// owns rendering. An inactive Mod or a pause-only boundary cannot pass.
public sealed class MultilingualCaptureChecks : MonoBehaviour
{
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private bool done;
    public MultilingualCaptureChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (done) return;
        if (!RenderControlV1.IsReady || SceneManager.GetActiveScene().name != "CatalogScene")
        {
            if (wall.Elapsed.TotalSeconds > 45) { done = true; Plugin.Report("PATH-CHECK RESULT 0 passed; 1 failed"); }
            return;
        }
        done = true;
        Run();
    }

    [HideFromIl2Cpp]
    private static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        IDisposable? lease = null;
        GameObject? root = null;
        Type? adapter = null;
        bool passed = false;
        try
        {
            var mod = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "AAMultilingual");
            var plugin = mod.GetType("AAMultilingual.Plugin", true)!;
            var current = plugin.GetProperty("Current", flags)!.GetValue(null)!;
            string locale = (string)plugin.GetProperty("LocaleId", flags)!.GetValue(current)!;
            if (locale != "zh-CN") throw new InvalidOperationException("Probe needs existing zh-CN locale; no configuration is rewritten.");
            var refresh = mod.GetType("AAMultilingual.SystemUiTranslator", true)!.GetMethod("RefreshAll", flags)!;
            root = new GameObject("Controlled translation probe");
            root.SetActive(false);
            var parent = root.transform;
            foreach (string part in new[] { "FrontUI", "Top", "AutoBtn", "AutoBtn_Enabled" })
            {
                var child = new GameObject(part);
                child.transform.SetParent(parent, false);
                parent = child.transform;
            }
            var labelObject = new GameObject("Label_Menu_Locale (1)");
            labelObject.transform.SetParent(parent, false);
            var label = labelObject.AddComponent<UILabel>();
            var template = Object.FindObjectsOfType<UILabel>(true).FirstOrDefault(item => item != label && item.trueTypeFont != null);
            if (template != null) label.trueTypeFont = template.trueTypeFont;
            label.width = 180; label.height = 40; label.fontSize = 24;
            label.text = "AUTO";
            refresh.Invoke(null, new object[] { locale });
            if (label.text != "自动") throw new InvalidOperationException("Original Mod did not translate AUTO before capture: " + label.text);
            lease = (IDisposable)typeof(RenderControlV1).GetMethod("Acquire", flags)!.Invoke(null, null)!;
            label.text = "AUTO";
            adapter = typeof(AAVideoExport.Plugin.Plugin).Assembly.GetType("AAVideoExport.Plugin.MultilingualCaptureAdapter");
            adapter?.GetMethod("BeginExport", flags)!.Invoke(null, null);
            adapter?.GetMethod("Pump", flags)!.Invoke(null, new object[] { 0L });
            Plugin.Report($"PATH-CHECK TRANSLATION owned={RenderControlV1.IsRenderingOwned}; locale={locale}; actual={label.text}; expected=自动; adapter_present={adapter != null}");
            if (label.text != "自动") throw new InvalidOperationException("Translation effect disappeared during export ownership.");
            var lateObject = new GameObject("NotYetBound");
            lateObject.SetActive(false);
            lateObject.transform.SetParent(parent, false);
            var lateLabel = lateObject.AddComponent<UILabel>();
            lateLabel.width = 180; lateLabel.height = 40; lateLabel.fontSize = 24;
            if (template != null) lateLabel.trueTypeFont = template.trueTypeFont;
            lateLabel.text = "AUTO";
            lateObject.name = "Label_Menu_Locale (1)";
            // Model the real native enable callback after the frame's first
            // pump. OnEnable only queues translation, it does not change text.
            mod.GetType("AAMultilingual.SystemUiTranslator", true)!.GetMethod("LabelEnabledPostfix", flags)!.Invoke(null, new object[] { lateLabel });
            adapter!.GetMethod("Pump", flags)!.Invoke(null, new object[] { 0L });
            if (lateLabel.text != "自动") throw new InvalidOperationException("Late native enable remained untranslated in the same frame.");
            label.text = "AUTO";
            adapter!.GetMethod("Pump", flags)!.Invoke(null, new object[] { 1L });
            if (label.text != "自动" || label.maxLineCount != 1 || label.overflowMethod != UILabel.Overflow.ShrinkContent)
                throw new InvalidOperationException("Translated text/layout not preserved on the next frame.");
            adapter.GetMethod("EndExport", flags)!.Invoke(null, null);
            // The outer render lease is still held between the two passes.
            label.text = "AUTO";
            adapter.GetMethod("BeginExport", flags)!.Invoke(null, null);
            adapter.GetMethod("Pump", flags)!.Invoke(null, new object[] { 0L });
            if (label.text != "自动") throw new InvalidOperationException("Translation did not resume in a fresh pass.");
            adapter.GetMethod("EndExport", flags)!.Invoke(null, null);
            lease.Dispose(); lease = null;
            label.text = "AUTO";
            refresh.Invoke(null, new object[] { locale });
            if (RenderControlV1.IsRenderingOwned || label.text != "自动")
                throw new InvalidOperationException("Original translation scheduling or rendering ownership was not restored.");
            passed = true;
            Plugin.Report("PATH-CHECK PASS multilingual-effect-preserved-before-during-retry-after");
        }
        catch (Exception error) { Plugin.Report("PATH-CHECK FAIL multilingual-effect: " + error); }
        finally
        {
            try { adapter?.GetMethod("EndExport", flags)?.Invoke(null, null); lease?.Dispose(); if (root != null) Object.Destroy(root); }
            catch (Exception error) { passed = false; Plugin.Report("PATH-CHECK FAIL multilingual-cleanup: " + error); }
        }
        Plugin.Report($"PATH-CHECK RESULT {(passed ? 1 : 0)} passed; {(passed ? 0 : 1)} failed");
    }
}
