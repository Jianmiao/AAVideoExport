using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AAVideoExport.Integration;
using AAVideoExport.Plugin;
using UnityEngine;
using UnityEngine.Rendering;

namespace AAVideoExport.NativePath.Tests;

// Observes the actual installed Azurite consumer and production Host leases.
// No call applies a tier, changes an FPS setting or acquires a test lease.
internal sealed class StoryRenderControlEvidence : IDisposable
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly List<object> events = new();
    private readonly List<string> errors = new();
    private readonly List<object> restoredAttempts = new();
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly string scope;
    private object? plugin, host, controller;
    private int attempt, thread, acquired, released, complete;
    private int baselineFps, baselineVsync, baselineTier, baselineInterval;
    private float baselineDelta, baselineScale;
    private bool baselineBackground, observedOwned, attemptComplete;
    private int acquiredInterval;
    private float acquiredDelta, acquiredScale;
    private bool acquiredBackground, acquisitionBaselineCaptured;
    private double restoreStart = -1;

    internal StoryRenderControlEvidence(string? observedScope = null)
    {
        scope = observedScope ?? "Actual installed Azurite consumer and production Host capture plus normal idle export-panel close. Observe-only v1 lifecycle; the runner closes the modal through Visible=false after Host work and scopes release.";
        RenderControlV1.BeforeAcquire += BeforeAcquire;
        RenderControlV1.AfterRelease += AfterRelease;
    }

    internal void BeginAttempt(int number)
    {
        Require(number > attempt && (attempt == 0 || attemptComplete), "A new attempt started before the prior render lease restored.");
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Azurite");
        plugin = assembly.GetType("Azurite.Plugin", true)!.GetField("_current", Fields)!.GetValue(null)
            ?? throw new InvalidOperationException("The actual Azurite consumer is not initialized.");
        host = Read(plugin, "_host") ?? throw new InvalidOperationException("Azurite has no actual host binding.");
        controller = Read(plugin, "_fps") ?? throw new InvalidOperationException("Azurite has no FPS controller.");
        Require(RenderControlV1.ProtocolVersion == 1 && RenderControlV1.IsReady && !RenderControlV1.IsRenderingOwned
            && Bool(host, "HostSupported") && Bool(host, "HasPublicRenderControl")
            && Bool(controller, "IsOperational") && Bool(controller, "IsMappingApplied")
            && !Bool(plugin, "_exportSuspended") && !Bool(controller, "_exportSuspended"),
            "Actual Azurite host recognition, public v1 binding and active FPS mapping are required before export.");
        attempt = number; thread = Environment.CurrentManagedThreadId;
        acquired = released = 0; observedOwned = attemptComplete = acquisitionBaselineCaptured = false; restoreStart = -1;
        baselineFps = Application.targetFrameRate; baselineVsync = QualitySettings.vSyncCount;
        baselineTier = UserSettings.Instance.fpsTier; baselineDelta = Time.captureDeltaTime;
        baselineScale = Time.timeScale; baselineInterval = OnDemandRendering.renderFrameInterval;
        baselineBackground = Application.runInBackground;
        Record("before-export");
    }

    internal static bool IsIdleConsumerReady()
    {
        if (!RenderControlV1.IsReady || RenderControlV1.IsRenderingOwned) return false;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Azurite");
        var current = assembly?.GetType("Azurite.Plugin", true)!.GetField("_current", Fields)!.GetValue(null);
        if (current == null) return false;
        var binding = Read(current, "_host"); var fps = Read(current, "_fps");
        return binding != null && fps != null && Bool(binding, "HostSupported") && Bool(binding, "HasPublicRenderControl")
            && Bool(fps, "IsOperational") && Bool(fps, "IsMappingApplied")
            && !Bool(current, "_exportSuspended") && !Bool(fps, "_exportSuspended");
    }

    internal void ObserveOwned()
    {
        CheckErrors();
        if (attempt == 0 || !RenderControlV1.IsRenderingOwned) return;
        Require(acquired > released && Bool(plugin!, "_exportSuspended") && Bool(controller!, "_exportSuspended")
            && !Bool(controller!, "IsMappingApplied") && Bool(host!, "PublicRenderingBlocked")
            && Bool(host!, "HasPublicRenderControl"), "Azurite did not stay suspended under actual Host render ownership.");
        if (!observedOwned) { observedOwned = true; Record("owned-export"); }
    }

    internal bool WaitForRestoration(string outcome = "completed")
    {
        CheckErrors();
        Require(outcome is "completed" or "cancelled", "Render restoration needs an explicit completed or cancelled outcome.");
        return ObserveRestoration(outcome, requireOwnedWork: true);
    }

    internal bool WaitForFailureRestoration()
    {
        // A refusal before acquisition is a valid no-lease failure path. Keep
        // observer errors in the final report, but still observe real cleanup.
        if (attempt == 0) return !RenderControlV1.IsRenderingOwned;
        return ObserveRestoration("failed", requireOwnedWork: false);
    }

    private bool ObserveRestoration(string outcome, bool requireOwnedWork)
    {
        if (attemptComplete) return true;
        if (restoreStart < 0) restoreStart = wall.Elapsed.TotalSeconds;
        bool restored = acquired == released && (!requireOwnedWork || acquired > 0 && observedOwned)
            && RenderControlV1.IsReady && !RenderControlV1.IsRenderingOwned
            && Bool(host!, "HostSupported") && Bool(host!, "HasPublicRenderControl")
            && Bool(controller!, "IsOperational") && Bool(controller!, "IsMappingApplied")
            && !Bool(plugin!, "_exportSuspended") && !Bool(controller!, "_exportSuspended")
            && Application.targetFrameRate == baselineFps && QualitySettings.vSyncCount == baselineVsync
            && UserSettings.Instance.fpsTier == baselineTier && ClockRestored(settled: true);
        if (restored)
        {
            attemptComplete = true; complete++; Record("settled-restoration");
            restoredAttempts.Add(new { attempt, outcome, acquired, released, observedOwned }); return true;
        }
        if (wall.Elapsed.TotalSeconds - restoreStart >= 5)
        {
            Record("restoration-timeout");
            throw new TimeoutException("Actual Azurite mapping, FPS/VSync/tier and Unity clock failed to restore after Host cleanup.");
        }
        return false;
    }

    private void BeforeAcquire() => Guard(() =>
    {
        Require(attempt > 0 && Environment.CurrentManagedThreadId == thread, "Render acquisition occurred outside the observed main-thread attempt.");
        Require(Bool(plugin!, "_exportSuspended") && Bool(controller!, "_exportSuspended")
            && !Bool(controller!, "IsMappingApplied") && Bool(host!, "HasPublicRenderControl"),
            "Azurite did not restore and suspend its mapping before the exporter snapshot.");
        // Azurite's participant ran first. This is the baseline the production
        // NativeCaptureScope sees, which can differ from settled mapped FPS.
        acquiredDelta = Time.captureDeltaTime; acquiredScale = Time.timeScale;
        acquiredInterval = OnDemandRendering.renderFrameInterval; acquiredBackground = Application.runInBackground;
        acquisitionBaselineCaptured = true;
        acquired++; Record("before-acquire");
    });

    private void AfterRelease() => Guard(() =>
    {
        Require(Environment.CurrentManagedThreadId == thread && acquired > released, "Render release is unbalanced or off the main thread.");
        Require(!RenderControlV1.IsRenderingOwned && acquisitionBaselineCaptured && ClockRestored(settled: false), "Unity clock was not restored before the public AfterRelease event.");
        released++; Record("after-release");
    });

    private bool ClockRestored(bool settled)
    {
        var clock = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeExportClock", true)!;
        return !(bool)clock.GetProperty("IsActive", Fields)!.GetValue(null)!
            && Time.captureDeltaTime == (settled ? baselineDelta : acquiredDelta)
            && Time.timeScale == (settled ? baselineScale : acquiredScale)
            && OnDemandRendering.renderFrameInterval == (settled ? baselineInterval : acquiredInterval)
            && Application.runInBackground == (settled ? baselineBackground : acquiredBackground);
    }

    internal bool Finish(int expectedAttempts)
    {
        Dispose();
        bool passed = errors.Count == 0 && complete == expectedAttempts;
        File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "story-render-control-evidence.json"), JsonSerializer.Serialize(new
        {
            Passed = passed, RestoredAttempts = complete, ExpectedTotalAttempts = expectedAttempts,
            AttemptOutcomes = restoredAttempts, Events = events, Errors = errors,
            Scope = scope
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private void Record(string kind) => events.Add(new
    {
        kind, attempt, acquired, released, thread = Environment.CurrentManagedThreadId,
        fps = Application.targetFrameRate, vSync = QualitySettings.vSyncCount, tier = UserSettings.Instance.fpsTier,
        baselineFps, baselineVsync, baselineTier, baselineInterval, acquiredInterval,
        baselineDelta, acquiredDelta, baselineScale, acquiredScale, baselineBackground, acquiredBackground,
        captureDelta = Time.captureDeltaTime, timeScale = Time.timeScale, renderInterval = OnDemandRendering.renderFrameInterval,
        mapped = Bool(controller!, "IsMappingApplied"), operational = Bool(controller!, "IsOperational"),
        pluginSuspended = Bool(plugin!, "_exportSuspended"), controllerSuspended = Bool(controller!, "_exportSuspended"),
        supported = Bool(host!, "HostSupported"), bound = Bool(host!, "HasPublicRenderControl"),
        renderOwned = RenderControlV1.IsRenderingOwned, providerReady = RenderControlV1.IsReady
    });
    private static object? Read(object value, string name) => value.GetType().GetProperty(name, Fields)?.GetValue(value)
        ?? value.GetType().GetField(name, Fields)!.GetValue(value);
    private static bool Bool(object value, string name) => (bool)Read(value, name)!;
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private void Guard(Action action) { try { action(); } catch (Exception error) { errors.Add(error.GetBaseException().Message); } }
    private void CheckErrors() { if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors)); }
    public void Dispose()
    {
        RenderControlV1.BeforeAcquire -= BeforeAcquire;
        RenderControlV1.AfterRelease -= AfterRelease;
    }
}
