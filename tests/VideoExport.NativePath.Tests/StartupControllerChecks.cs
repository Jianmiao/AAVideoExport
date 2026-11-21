using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Integration;
using AAVideoExport.Plugin;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Exercises the real retained controller and real installed Harmony patch
// graph across native Updates. This is adapter acceptance, not a movie-export
// or arbitrary-Mod compatibility claim.
public sealed class StartupControllerChecks : MonoBehaviour
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Boundary = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.AASkipIntroExportBoundary", true)!;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private IDisposable? startup, rendering;
    private Harmony? conflict;
    private int stage, frames, passed, failed;
    private string fingerprint = "", modHash = "", modPath = "";
    private bool finished, invalidated;

    public StartupControllerChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (finished) return;
        try
        {
            if (wall.Elapsed.TotalSeconds > 50) throw new TimeoutException("Retained startup controller probe timed out.");
            if (stage == 0)
            {
                if (!RenderControlV1.IsReady || RenderControlV1.IsRenderingOwned || SceneManager.GetActiveScene().name != "CatalogScene") return;
                var mod = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "AASkipIntro");
                modPath = mod.Location;
                modHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modPath)));
                startup = Invoke("TryAcquire") as IDisposable ?? throw new InvalidOperationException("Exact installed Mod produced no retained lease.");
                fingerprint = (string)Boundary.GetProperty("BuildFingerprint", Static)!.GetValue(null)!;
                Check(Active() && fingerprint.Contains(modHash, StringComparison.OrdinalIgnoreCase), "exact-build retained acquisition");
                rendering = (IDisposable)typeof(RenderControlV1).GetMethod("Acquire", Static)!.Invoke(null, null)!;
                stage = 1;
                return;
            }
            if (stage == 1)
            {
                Invoke("ValidateActive");
                if (!Active() || !RenderControlV1.IsRenderingOwned) throw new InvalidOperationException("A retained lease disappeared before native Update completion.");
                if (++frames < 30) return;
                Check(true, "controller stays valid across 30 native updates");
                Task.Run(() => Invoke("ValidateForCommit")).GetAwaiter().GetResult();
                Check(true, "worker commit check accepts live proof without Unity access");
                Task.Run(() =>
                {
                    using var scope = (IDisposable)Invoke("AcquireCommitScope")!;
                    Invoke("ValidateForCommit");
                }).GetAwaiter().GetResult();
                Check(true, "worker publication scope validates and releases on its owning thread");
                startup!.Dispose(); startup = null;
                Check(!Active(), "normal scope release clears active proof");
                Check(RejectsCommit(), "worker rejects use of released proof");
                rendering!.Dispose(); rendering = null;
                Check(RenderControlV1.IsReady && !RenderControlV1.IsRenderingOwned, "render ownership restored after controlled scope");
                stage = 2;
                return;
            }
            if (stage == 2)
            {
                startup = Invoke("TryAcquire") as IDisposable ?? throw new InvalidOperationException("Second retained scope could not acquire.");
                var awake = typeof(CreatorSplash).GetMethod("Awake", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                conflict = new Harmony("halocue.aa.videoexport.startup-controller-negative-test");
                conflict.Patch(awake, postfix: new HarmonyMethod(typeof(StartupControllerChecks).GetMethod(nameof(UnexpectedPostfix), Static)!));
                bool rejected = false;
                try { Invoke("ValidateActive"); }
                catch (Exception error) when (Unwrap(error) is NotSupportedException or InvalidOperationException) { rejected = true; }
                invalidated = rejected;
                Check(rejected && RejectsCommit(), "changed real patch graph invalidates the retained worker proof");
                bool scopeRejected = false;
                try
                {
                    Task.Run(() => { using var scope = (IDisposable)Invoke("AcquireCommitScope")!; }).GetAwaiter().GetResult();
                }
                catch (Exception error) when (Unwrap(error) is InvalidOperationException) { scopeRejected = true; }
                Check(scopeRejected, "invalidated real boundary cannot acquire a publication scope");
                // Remove only our own no-op test patch. The real Mod's native
                // effect and its installed bytes are never changed.
                conflict.UnpatchSelf(); conflict = null;
                try { startup!.Dispose(); }
                catch (Exception error) when (invalidated && Unwrap(error) is InvalidOperationException) { }
                startup = null;
                Check(!Active(), "invalidated scope still releases its own lease");
                Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modPath))) == modHash, "installed Mod bytes remain unchanged");
                Finish();
            }
        }
        catch (Exception error)
        {
            failed++;
            Plugin.Report("PATH-CHECK FAIL startup-controller: " + Unwrap(error));
            Finish();
        }
    }

    [HideFromIl2Cpp]
    private static object? Invoke(string method) => Boundary.GetMethod(method, Static)!.Invoke(null, null);
    [HideFromIl2Cpp]
    private static bool Active() => (bool)Boundary.GetProperty("IsActive", Static)!.GetValue(null)!;
    [HideFromIl2Cpp]
    private static Exception Unwrap(Exception e) => e is TargetInvocationException { InnerException: not null } t ? Unwrap(t.InnerException!) : e;
    [HideFromIl2Cpp]
    private static bool RejectsCommit()
    {
        try { Task.Run(() => Invoke("ValidateForCommit")).GetAwaiter().GetResult(); return false; }
        catch (Exception error) when (Unwrap(error) is InvalidOperationException) { return true; }
    }
    [HideFromIl2Cpp]
    private static void UnexpectedPostfix() { }
    [HideFromIl2Cpp]
    private void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        passed++;
        Plugin.Report("PATH-CHECK PASS startup-controller " + label);
    }
    [HideFromIl2Cpp]
    private void Finish()
    {
        if (finished) return;
        finished = true;
        try { conflict?.UnpatchSelf(); } catch (Exception e) { failed++; Plugin.Report("PATH-CHECK FAIL startup-controller patch cleanup: " + e); }
        try { startup?.Dispose(); }
        catch (Exception e) { if (!invalidated) { failed++; Plugin.Report("PATH-CHECK FAIL startup-controller scope cleanup: " + e); } }
        try { rendering?.Dispose(); } catch (Exception e) { failed++; Plugin.Report("PATH-CHECK FAIL startup-controller render cleanup: " + e); }
        try
        {
            File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "startup-controller-result.json"), JsonSerializer.Serialize(new
            {
                Mode = "startup-controller", Passed = passed, Failed = failed, NativeUpdates = frames,
                ExactModHash = modHash, Fingerprint = fingerprint, ActiveAfterCleanup = Active(),
                RenderOwnedAfterCleanup = RenderControlV1.IsRenderingOwned, ReadyAfterCleanup = RenderControlV1.IsReady,
                Scope = "Retained exact-build controller and real native patch graph only; no movie was exported."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { failed++; Plugin.Report("PATH-CHECK FAIL startup-controller evidence: " + e); }
        Plugin.Report($"PATH-CHECK RESULT {passed} passed; {failed} failed");
    }
}
