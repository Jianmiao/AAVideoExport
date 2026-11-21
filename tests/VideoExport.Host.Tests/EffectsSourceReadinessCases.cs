using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using AAVideoExport.Integration;

internal static partial class StrictHostRegression
{
    internal static readonly (string Name, Action Run)[] EffectsSourceReadinessCases =
    {
        ("cancelled token interrupts effects source waiting before another native probe", () => WithPendingEffects(() =>
        {
            using var fixture = new Fixture(begin: false); fixture.StartStrict();
            int attempts = RuntimeModBoundaries.SourcePreparations;
            fixture.Get<CancellationTokenSource>("_cancellation").Cancel();
            fixture.Host.Update(); fixture.Cleanup();
            Check(fixture.Session.Cancelled && !fixture.Host.Capturing && fixture.Player.AdvanceCalls == 0
                && RuntimeModBoundaries.SourcePreparations == attempts && !RenderControlV1.IsRenderingOwned,
                "cancelled token continued native source probing or retained its capture ownership");
        })),
        ("replay waits for original effect readiness without reacquiring its frozen source identity", () => WithPendingEffects(() =>
        {
            RuntimeModBoundaries.BeforeSourcePreparation = null;
            using var fixture = new Fixture(begin: false); fixture.StartStrict();
            var input = fixture.Get<EffectsSourceInput>("_effectsSourceInput");
            string identity = fixture.Get<string>("_strictIdentity");
            fixture.Prime(); fixture.Frame(1600);
            RuntimeModBoundaries.BeforeSourceRevalidation = () => throw new ExportException("strict_effects_source_pending", "fixture original replay index is loading");
            fixture.FinishPreparationAndStartReplay(expectCapture: false);
            Check(!fixture.Host.Capturing && fixture.Player.AdvanceCalls == 0 && !fixture.Session.Cancelled,
                "replay started an authored row before original index readiness");
            fixture.Player.AdvanceScenario(); fixture.Host.Update();
            Check(fixture.Player.AdvanceCalls == 0 && ReferenceEquals(input, fixture.Get<EffectsSourceInput>("_effectsSourceInput"))
                && identity == fixture.Get<string>("_strictIdentity") && RuntimeModBoundaries.SourcePreparations == 1,
                "replay pending changed the retained input proof or released the native first row");
            RuntimeModBoundaries.BeforeSourceRevalidation = null; fixture.Host.Update();
            Check(fixture.Host.Capturing && !fixture.Get<bool>("_strictPreparing") && fixture.Player.AdvanceCalls == 0,
                "ready replay did not reenter its normal mixer primer");
            fixture.Prime();
        })),
        ("effects index changed after mixer priming refuses authored frame zero", () => WithPendingEffects(() =>
        {
            RuntimeModBoundaries.BeforeSourcePreparation = null;
            using var fixture = new Fixture(begin: false); fixture.StartStrict();
            fixture.Frame(0); fixture.Frame(1600);
            Check(fixture.Get<bool>("_strictStartReady") && fixture.Player.AdvanceCalls == 0, "fixture did not reach the first authored advance boundary");
            EffectsSourceFake.BeforeValidation = () => throw new ExportException("strict_effects_source_unavailable", "fixture original index changed after acquisition");
            fixture.Host.Update(); fixture.Cleanup();
            Check(fixture.Session.Cancelled && fixture.Player.AdvanceCalls == 0 && fixture.Captured == 0
                && !fixture.Host.Capturing && !RenderControlV1.IsRenderingOwned,
                "first authored AdvanceScenario executed after the original effect index changed");
        })),
        ("effects index pending keeps initial native advance blocked until a later native update is ready", () => WithPendingEffects(() =>
        {
            using var fixture = new Fixture(begin: false); fixture.StartStrict();
            CheckSourceWait(fixture);
            int attempts = RuntimeModBoundaries.SourcePreparations;
            fixture.Player.AdvanceScenario();
            Check(!fixture.Host.AllowStrictScenarioAdvance(fixture.Player, null), "authored advance bypassed the pending source gate while capture was not active");
            fixture.Host.Update();
            CheckSourceWait(fixture);
            Check(RuntimeModBoundaries.SourcePreparations == attempts + 1, "pending source was not retried once on native Update");
            RuntimeModBoundaries.BeforeSourcePreparation = null;
            fixture.Host.Update();
            Check(fixture.Host.Capturing && fixture.Player.AdvanceCalls == 0 && fixture.Captured == 0
                && CharacterVoiceDelayClock.Active, "ready source did not enter the normal offline primer without authored work");
            attempts = RuntimeModBoundaries.SourcePreparations;
            fixture.Player.AdvanceScenario(); fixture.Host.Update();
            Check(RuntimeModBoundaries.SourcePreparations == attempts && fixture.Player.AdvanceCalls == 0,
                "ready source was reacquired or native first row escaped its mixer primer");
            fixture.Prime();
        })),
        ("effects index wait cancellation releases ownership and cannot resume on late readiness", () => WithPendingEffects(() =>
        {
            using var fixture = new Fixture(begin: false); fixture.StartStrict();
            CheckSourceWait(fixture);
            int attempts = RuntimeModBoundaries.SourcePreparations;
            fixture.Call("Cancel"); fixture.Cleanup();
            RuntimeModBoundaries.BeforeSourcePreparation = null; fixture.Host.Update();
            Check(fixture.Session.Cancelled && !fixture.Host.Capturing && fixture.Captured == 0 && fixture.Player.AdvanceCalls == 0
                && fixture.Player.EndCalls == 1 && !RenderControlV1.IsRenderingOwned && !fixture.Host.RetainedCleanupPending,
                "cancelled source wait leaked its player/controller/ownership or started authored capture");
            Check(RuntimeModBoundaries.SourcePreparations == attempts, "late readiness retried a cancelled export");
        })),
        ("effects index wait has one bounded native deadline across repeated pending probes", () => WithPendingEffects(() =>
        {
            UnityEngine.Time.realtimeSinceStartup = 500;
            using var fixture = new Fixture(begin: false); fixture.StartStrict();
            UnityEngine.Time.realtimeSinceStartup = 619;
            fixture.Host.Update(); CheckSourceWait(fixture);
            int attempts = RuntimeModBoundaries.SourcePreparations;
            UnityEngine.Time.realtimeSinceStartup = 620;
            fixture.Host.Update(); fixture.Cleanup();
            Check(fixture.Session.Cancelled && !fixture.Host.Capturing && fixture.Player.AdvanceCalls == 0
                && UnityEngine.Debug.Messages.Any(m => m.Contains("strict_effects_source_timeout", StringComparison.Ordinal))
                && RuntimeModBoundaries.SourcePreparations == attempts && !RenderControlV1.IsRenderingOwned,
                "pending source reset its deadline, retried after expiry, or escaped normal failure cleanup");
        })),
        ("effects source permanent failure after pending fails once without offline capture", () => WithPendingEffects(() =>
        {
            using var fixture = new Fixture(begin: false); fixture.StartStrict();
            RuntimeModBoundaries.BeforeSourcePreparation = () => throw new ExportException("strict_effects_source_unavailable", "fixture original index rejected input");
            fixture.Host.Update(); fixture.Cleanup();
            Check(fixture.Session.Cancelled && !fixture.Host.Capturing && fixture.Player.AdvanceCalls == 0 && fixture.Captured == 0
                && UnityEngine.Debug.Messages.Any(m => m.Contains("strict_effects_source_unavailable", StringComparison.Ordinal)),
                "permanent source failure was treated as pending or admitted authored work");
            int attempts = RuntimeModBoundaries.SourcePreparations;
            fixture.Host.Update();
            Check(RuntimeModBoundaries.SourcePreparations == attempts && !RenderControlV1.IsRenderingOwned,
                "terminal source failure kept retrying or held render ownership");
        })),
        ("retained fault interrupts effects source waiting before any further source probe", () => WithPendingEffects(() =>
        {
            using var fixture = new Fixture(begin: false); fixture.StartStrict();
            int attempts = RuntimeModBoundaries.SourcePreparations;
            fixture.Host.RetainedBoundaryFailed(new ExportException("strict_retained_boundary_invalid", "fixture retained fault during source wait"));
            fixture.Host.Update(); fixture.Cleanup();
            Check(fixture.Session.Cancelled && !fixture.Host.Capturing && fixture.Player.AdvanceCalls == 0
                && RuntimeModBoundaries.SourcePreparations == attempts && !fixture.Host.RetainedCleanupPending,
                "source wait hid a retained fault or retried source after the fault");
        })),
        ("native voice preload transitions into source waiting without releasing the first row", () => WithPendingEffects(() =>
        {
            CharacterVoiceSessionAdapter.Loaded = true; CharacterVoiceSessionAdapter.PreloadReady = false;
            using var fixture = new Fixture(begin: false); fixture.StartStrict();
            Check(RuntimeModBoundaries.SourcePreparations == 0 && fixture.Player.AdvanceCalls == 0, "source preparation ran before native voice preload finished");
            CharacterVoiceSessionAdapter.PreloadReady = true;
            fixture.Host.Update(); CheckSourceWait(fixture);
            Check(RuntimeModBoundaries.SourcePreparations == 1 && CharacterVoiceSessionAdapter.Arms == 0,
                "voice-to-source transition retried synchronously or armed an offline voice clock");
            RuntimeModBoundaries.BeforeSourcePreparation = null; fixture.Host.Update();
            Check(fixture.Host.Capturing && CharacterVoiceSessionAdapter.Prepares == 1 && CharacterVoiceSessionAdapter.Arms == 0,
                "source readiness restarted voice loading or skipped its mixer primer");
            fixture.Prime();
        })),
        ("changed frozen input during source waiting cannot be absorbed by a later ready index", () => WithPendingEffects(() =>
        {
            using var fixture = new Fixture(begin: false); fixture.StartStrict();
            int attempts = RuntimeModBoundaries.SourcePreparations;
            UserSettings.Instance!.data.masterVolume = .25f;
            RuntimeModBoundaries.BeforeSourcePreparation = null;
            fixture.Host.Update(); fixture.Cleanup();
            Check(fixture.Session.Cancelled && !fixture.Host.Capturing && fixture.Player.AdvanceCalls == 0
                && RuntimeModBoundaries.SourcePreparations == attempts
                && UnityEngine.Debug.Messages.Any(m => m.Contains("timeline_source_changed", StringComparison.Ordinal)),
                "source readiness silently refreshed the frozen settings identity");
        })),
        ("ending the native player while its effects index is pending cancels the held export", () => WithPendingEffects(() =>
        {
            using var fixture = new Fixture(begin: false); fixture.StartStrict();
            Check(!fixture.Host.BeforePlayerEnd(fixture.Player), "player end escaped the pending source owner");
            fixture.Cleanup();
            Check(fixture.Session.Cancelled && !fixture.Host.Capturing && fixture.Player.AdvanceCalls == 0
                && !RenderControlV1.IsRenderingOwned && !fixture.Host.RetainedCleanupPending,
                "ended source wait retained a cancellable operation or render ownership");
        }))
    };

    private static void WithPendingEffects(Action run)
    {
        float priorRealtime = UnityEngine.Time.realtimeSinceStartup;
        RuntimeModBoundaries.SourcePreparationRequired = true;
        RuntimeModBoundaries.SourcePreparations = RuntimeModBoundaries.SourceRevalidations = 0;
        RuntimeModBoundaries.BeforeSourcePreparation = () => throw new ExportException("strict_effects_source_pending", "fixture original native index is still loading");
        try { run(); }
        finally
        {
            RuntimeModBoundaries.BeforeSourcePreparation = null;
            RuntimeModBoundaries.BeforeSourceRevalidation = null;
            EffectsSourceFake.BeforeValidation = null;
            RuntimeModBoundaries.SourcePreparationRequired = false;
            RuntimeModBoundaries.SourcePreparations = RuntimeModBoundaries.SourceRevalidations = 0;
            UnityEngine.Time.realtimeSinceStartup = priorRealtime;
        }
    }

    private static void CheckSourceWait(Fixture fixture)
    {
        bool busy = (bool)typeof(ExportHost).GetProperty("Busy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Host)!;
        Check(busy && !fixture.Session.Cancelled && !fixture.Host.Capturing && fixture.Captured == 0
            && fixture.Player.AdvanceCalls == 0 && fixture.Get<NativeCaptureScope?>("_native") == null
            && fixture.Get<FramePipeline?>("_frames") == null && !CharacterVoiceDelayClock.Active && RenderControlV1.IsRenderingOwned,
            "pending effects source did not hold a busy cancellable export before native capture, authored rows and offline clocks");
    }
}
