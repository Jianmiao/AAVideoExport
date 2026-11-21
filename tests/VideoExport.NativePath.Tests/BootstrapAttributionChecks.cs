using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Native launch acceptance of early observation only. No authorization or
// whole-process coverage is inferred from creator stacks or shared holders.
public sealed class BootstrapAttributionChecks : MonoBehaviour
{
    private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private Type? api;
    private IDisposable? render;
    private Func<int, int>? first, late;
    private long firstId;
    private int stage, updates, passed, failed;
    private bool finished;
    private object? azurite, azuriteHost, fpsController;
    private int beforeFps, beforeVsync, beforeTier, duringFps, duringVsync, postUpdates;

    public BootstrapAttributionChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (finished) return;
        try
        {
            if (wall.Elapsed.TotalSeconds > 75) throw new TimeoutException("Early observer native acceptance timed out.");
            if (stage == 0)
            {
                if (!RenderControlV1.IsReady || RenderControlV1.IsRenderingOwned || SceneManager.GetActiveScene().name != "CatalogScene") return;
                // Measure a settled, actually bound consumer. Catalog scene
                // entry alone can precede Azurite's initial FPS acquisition.
                if (!TryReadyAzurite()) return;
                beforeFps = Application.targetFrameRate; beforeVsync = QualitySettings.vSyncCount;
                beforeTier = UserSettings.Instance.fpsTier;
                api = AppDomain.CurrentDomain.GetAssemblies()
                    .SingleOrDefault(a => a.GetName().Name == "AAVideoExport.RuntimeBootstrap")
                    ?.GetType("AAVideoExport.RuntimeBootstrap.RuntimeAttributionV1", false)
                    ?? throw new InvalidOperationException("Early observer is not installed in this AA process.");
                Check((int)api.GetProperty("ProtocolVersion", PublicStatic)!.GetValue(null)! == 1
                    && (bool)api.GetProperty("IsInstalled", PublicStatic)!.GetValue(null)!, "early observer v1 is active inside AA");
                string start = Snapshot("bootstrap-start.json");
                using (var json = JsonDocument.Parse(start))
                {
                    var root = json.RootElement;
                    Check(!root.GetProperty("preexistingCoverage").GetBoolean() && !root.GetProperty("allCodeCoverage").GetBoolean()
                        && !root.GetProperty("permitsStrictExport").GetBoolean(), "coverage limits remain explicit");
                    Check(root.GetProperty("targets").EnumerateArray().All(t => t.GetProperty("installed").GetBoolean()), "all declared observation targets installed");
                    var markers = root.GetProperty("markers").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToArray();
                    Check(markers.Any(m => m == "AAVE.Plugin.Load.enter") && markers.Any(m => m == "AAVE.Plugin.Load.ready"),
                        "AAVE load markers were captured by the earlier observer");
                    Check(!root.GetProperty("startAssemblies").EnumerateArray()
                        .Any(a => new AssemblyName(a.GetString()!).Name == "AAVideoExport"),
                        "observer coverage starts before the ordinary AAVE assembly loads");
                }
                Mark("native.bootstrap.acceptance.begin");
                first = Emit(7);
                firstId = CheckAttribution(first);
                for (int i = -32; i <= 32; i++) CheckValue(first(i), i + 7);
                Check(true, "first real native-process delegate preserves 65 outputs");
                render = (IDisposable)typeof(RenderControlV1).GetMethod("Acquire", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
                duringFps = Application.targetFrameRate; duringVsync = QualitySettings.vSyncCount;
                Check(ReadBool(azurite!, "_exportSuspended") && ReadBool(fpsController!, "_exportSuspended")
                    && !ReadBool(fpsController!, "IsMappingApplied") && ReadBool(azuriteHost!, "PublicRenderingBlocked"),
                    "bound Azurite consumer suspends its FPS mapping during ownership");
                stage = 1;
                return;
            }
            if (stage == 1)
            {
                if (Application.targetFrameRate != duringFps || QualitySettings.vSyncCount != duringVsync)
                    throw new InvalidOperationException("Rendering settings changed during the observed ownership window.");
                if (++updates < 15) return;
                int loads = 0;
                AssemblyLoadEventHandler onLoad = (_, _) => loads++;
                AppDomain.CurrentDomain.AssemblyLoad += onLoad;
                try { late = Emit(13); }
                finally { AppDomain.CurrentDomain.AssemblyLoad -= onLoad; }
                Check(loads == 0, "late same-holder emission causes no AssemblyLoad event");
                long lateId = CheckAttribution(late);
                Check(lateId != firstId, "same-name methods retain distinct identities");
                for (int i = -32; i <= 32; i++) CheckValue(late(i), i + 13);
                Check(true, "late real native-process delegate preserves 65 outputs");
                render!.Dispose(); render = null;
                Check(RenderControlV1.IsReady && !RenderControlV1.IsRenderingOwned, "provider render lease released with observer enabled");
                stage = 2;
                return;
            }
            if (stage == 2)
            {
                if (++postUpdates < 15 || !ReadBool(fpsController!, "IsMappingApplied")) return;
                Check(ReadBool(fpsController!, "IsOperational") && !ReadBool(azurite!, "_exportSuspended")
                    && !ReadBool(fpsController!, "_exportSuspended") && ReadBool(azuriteHost!, "HasPublicRenderControl"),
                    "Azurite resumes its bound FPS controller across post-release updates");
                Check(Application.targetFrameRate == beforeFps && QualitySettings.vSyncCount == beforeVsync
                    && UserSettings.Instance.fpsTier == beforeTier, "native FPS VSync and selected tier return to the settled baseline");
                File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "azurite-handoff.json"), JsonSerializer.Serialize(new
                {
                    Before = new { targetFrameRate = beforeFps, vSyncCount = beforeVsync, tier = beforeTier },
                    During = new { targetFrameRate = duringFps, vSyncCount = duringVsync },
                    After = new { targetFrameRate = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount, tier = UserSettings.Instance.fpsTier },
                    NativeUpdatesWhileOwned = updates, NativeUpdatesAfterRelease = postUpdates,
                    HostSupported = ReadBool(azuriteHost!, "HostSupported"), Bound = ReadBool(azuriteHost!, "HasPublicRenderControl"),
                    Scope = "Settled Catalog ownership diagnostic. Early-before-initial-mapping handoff and full export remain separate acceptance."
                }, new JsonSerializerOptions { WriteIndented = true }));
                Mark("native.bootstrap.acceptance.end");
                Snapshot("bootstrap-final.json");
                Finish();
            }
        }
        catch (Exception error)
        {
            failed++;
            Plugin.Report("PATH-CHECK FAIL bootstrap-attribution: " + error);
            Finish();
        }
    }

    [HideFromIl2Cpp]
    private bool TryReadyAzurite()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Azurite")
            ?? throw new InvalidOperationException("This native handoff acceptance requires the actual Azurite consumer.");
        azurite = assembly.GetType("Azurite.Plugin", true)!.GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        if (azurite == null || UserSettings.Instance == null || !UserSettings.Instance.isReady
            || ScenarioResourceManager.Instance == null || !ScenarioResourceManager.Instance.AreDbsLoaded || ScenarioResourceManager.Instance.Preloading) return false;
        azuriteHost = azurite.GetType().GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(azurite);
        fpsController = azurite.GetType().GetField("_fps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(azurite);
        return azuriteHost != null && fpsController != null && ReadBool(azuriteHost, "HostSupported")
            && ReadBool(azuriteHost, "HasPublicRenderControl") && ReadBool(fpsController, "IsOperational")
            && ReadBool(fpsController, "IsMappingApplied");
    }
    [HideFromIl2Cpp]
    private static bool ReadBool(object owner, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var property = owner.GetType().GetProperty(name, flags);
        object? value = property != null ? property.GetValue(owner) : owner.GetType().GetField(name, flags)?.GetValue(owner);
        return value is bool boolean ? boolean : throw new MissingMemberException(owner.GetType().FullName, name);
    }

    [HideFromIl2Cpp]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<int, int> Emit(int add)
    {
        var method = new DynamicMethod("AAVE.NativeSameHolder", typeof(int), new[] { typeof(int) });
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, add); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ret);
        return (Func<int, int>)method.CreateDelegate(typeof(Func<int, int>));
    }

    [HideFromIl2Cpp]
    private long CheckAttribution(Delegate callback)
    {
        var text = (string)api!.GetMethod("GetAttributionJson", PublicStatic)!.Invoke(null, new object[] { callback })!;
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var record = root.GetProperty("record");
        Check(root.GetProperty("status").GetString() == "OBSERVED"
            && record.GetProperty("constructorEvents").GetInt32() > 0
            && record.GetProperty("createDelegateEvents").GetInt32() > 0
            && record.GetProperty("creatorAssembly").GetString() == typeof(BootstrapAttributionChecks).Assembly.GetName().Name,
            "returned delegate maps to its observed constructor and producer");
        return record.GetProperty("id").GetInt64();
    }
    [HideFromIl2Cpp]
    private string Snapshot(string file)
    {
        string result = (string)api!.GetMethod("GetSnapshotJson", PublicStatic)!.Invoke(null, null)!;
        File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, file), result);
        return result;
    }
    [HideFromIl2Cpp]
    private void Mark(string marker) => api!.GetMethod("Mark", PublicStatic)!.Invoke(null, new object[] { marker });
    [HideFromIl2Cpp]
    private static void CheckValue(int actual, int expected) { if (actual != expected) throw new InvalidOperationException("Observer changed delegate output."); }
    [HideFromIl2Cpp]
    private void Check(bool valid, string label)
    {
        if (!valid) throw new InvalidOperationException(label);
        passed++; Plugin.Report("PATH-CHECK PASS bootstrap-attribution " + label);
    }
    [HideFromIl2Cpp]
    private void Finish()
    {
        if (finished) return;
        finished = true;
        try { render?.Dispose(); render = null; }
        catch (Exception error) { failed++; Plugin.Report("PATH-CHECK FAIL bootstrap-attribution cleanup: " + error); }
        Plugin.Report($"PATH-CHECK RESULT {passed} passed; {failed} failed");
    }
}
