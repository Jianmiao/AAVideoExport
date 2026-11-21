using System.Collections;
using System.Diagnostics;
using System.Reflection;
using AAVideoExport.Plugin;
using AAVideoExport.Integration;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Native proof/positive-callback fixture, not a completed movie test. It keeps
// all selected Mods loaded and lets original menu effects continue normally.
public sealed class RetainedMenuPlaybackChecks : MonoBehaviour
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private Type? boundary;
    private IDisposable? lease;
    private object? menu;
    private int frames, passed, failed;
    private double firstTrack;
    private string fingerprint = "";
    private bool done;
    public RetainedMenuPlaybackChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (done) return;
        try
        {
            if (wall.Elapsed.TotalSeconds > 90) throw new TimeoutException("Retained menu native proof timed out.");
            if (lease == null)
            {
                if (!RenderControlV1.IsReady || RenderControlV1.IsRenderingOwned || SceneManager.GetActiveScene().name != "CatalogScene") return;
                boundary ??= typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.RetainedMenuPlaybackBoundary", true)!;
                var beauty = Assembly("AAMenuPrototype");
                menu = beauty.GetType("AAMenuPrototype.MenuEntryHooks", true)!.GetField("Owner", Flags)!.GetValue(null);
                if (menu == null || Field(menu, "_animation") == null || Field(menu, "_menu") == null) return;
                object?[] acquisition = { null };
                if (Invoke("TryAcquireRetained", acquisition) is not true) return;
                lease = acquisition[0] as IDisposable ?? throw new InvalidOperationException("Native retained acquisition returned no lease.");
                fingerprint = (string)Invoke("CaptureActiveProof")!; firstTrack = TrackTime(menu);
                CheckOriginalBgm(); CheckOriginalSfx();
                return;
            }
            Invoke("ValidateActive");
            if (++frames < 30) return;
            Check((string)Invoke("CaptureActiveProof")! == fingerprint, "retained input/patch identity changed during ordinary Catalog Updates");
            Pass("retained-menu-live-proof-30-updates");
            Check(TrackTime(menu!) != firstTrack && Field(menu!, "_done") is false, "original menu animation did not continue while proof was retained");
            Pass("retained-menu-original-animation-preserved");
            CheckLatePatch();
            lease.Dispose(); lease = null;
            Check(boundary!.GetProperty("IsHeld", Flags)!.GetValue(null) is false, "retained menu input ownership leaked after release");
            Pass("retained-menu-release-restored");
            Finish();
        }
        catch (Exception error) { failed++; Plugin.Report("PATH-CHECK FAIL retained-menu: " + error.GetBaseException()); Finish(); }
    }
    [HideFromIl2Cpp]
    private void CheckOriginalBgm()
    {
        var assembly = Assembly("MenuBgmSelector");
        var store = assembly.GetType("AzureArchive.MenuBgmSelector.BgmStore", true)!;
        object selection = store.GetProperty("Current", Flags)!.GetValue(null)!;
        string kind = Field(selection, "Kind")!.ToString()!; long selected = (long)Field(selection, "Id")!;
        bool enabled = (bool)assembly.GetType("AzureArchive.MenuBgmSelector.GameLobbySetting", true)!.GetMethod("IsLobbyBgmEnabled", Flags)!.Invoke(null, null)!;
        long expected = enabled && kind == "Builtin" && selected > 0 ? selected : 12L;
        object?[] args = { 12L, "retained-native-proof" };
        assembly.GetType("AzureArchive.MenuBgmSelector.BgmPatches", true)!.GetMethod("HandleId", Flags)!.Invoke(null, args);
        Check((long)args[0]! == expected, "original native lobby-id selection was bypassed or changed");
        Pass("retained-menu-original-bgm-id-callback kind=" + kind);
    }
    [HideFromIl2Cpp]
    private void CheckOriginalSfx()
    {
        var data = Assembly("SfxFolderMod").GetType("AzureArchive.SfxFolderMod.SfxData", true)!;
        var map = (IDictionary)data.GetField("_byKey", Flags)!.GetValue(null)!;
        Check(map.Count > 0, "native Sfx map is empty");
        object key = map.Keys.Cast<object>().First(); object entry = map[key]!;
        string expected = (string)Field(entry, "Cn")!;
        string actual = (string)data.GetMethod("ChineseOf", Flags)!.Invoke(null, new[] { key.ToString() })!;
        Check(actual == expected, "original Sfx lookup was bypassed or lost its translated value");
        Pass("retained-menu-original-sfx-lookup entries=" + map.Count);
    }
    [HideFromIl2Cpp]
    private void CheckLatePatch()
    {
        var target = Assembly("MenuBgmSelector").GetType("AzureArchive.MenuBgmSelector.BgmPatches", true)!.GetMethod("HandleId", Flags)!;
        var foreign = new Harmony("halocue.aave.retained-menu.native-mutation");
        bool rejected = false;
        try
        {
            foreign.Patch(target, prefix: new HarmonyMethod(typeof(RetainedMenuPlaybackChecks), nameof(NoopPrefix)));
            try { Invoke("ValidateActive"); } catch { rejected = true; }
        }
        finally { foreign.UnpatchSelf(); }
        Check(rejected, "late native managed patch mutation retained valid proof");
        bool healed = true; try { Invoke("CaptureActiveProof"); } catch { healed = false; }
        Check(!healed, "removing the mutation silently healed the same retained proof");
        Pass("retained-menu-late-patch-failure-latched");
    }
    public static void NoopPrefix() { }
    [HideFromIl2Cpp]
    private object? Invoke(string name, params object?[] arguments)
    {
        try { return boundary!.GetMethod(name, Flags)!.Invoke(null, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    [HideFromIl2Cpp]
    private static double TrackTime(object owner)
    {
        object animation = Field(owner, "_animation")!;
        object state = animation.GetType().GetProperty("AnimationState", Flags)!.GetValue(animation)!;
        object entry = state.GetType().GetMethod("GetCurrent", Flags, null, new[] { typeof(int) }, null)!.Invoke(state, new object[] { 1 })!;
        return Convert.ToDouble(entry.GetType().GetProperty("TrackTime", Flags)!.GetValue(entry));
    }
    private static Assembly Assembly(string name) => AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == name);
    private static object? Field(object value, string name) => value.GetType().GetField(name, Flags)!.GetValue(value);
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private void Pass(string name) { passed++; Plugin.Report("PATH-CHECK PASS " + name); }
    [HideFromIl2Cpp]
    private void Finish()
    {
        if (done) return; done = true;
        try { lease?.Dispose(); lease = null; } catch (Exception error) { failed++; Plugin.Report("PATH-CHECK FAIL retained-menu-cleanup: " + error.GetBaseException()); }
        Plugin.Report($"PATH-CHECK RESULT {passed} passed; {failed} failed");
    }
    public void OnDestroy() { if (!done) { failed++; Finish(); } }
}
