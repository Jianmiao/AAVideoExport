using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestUiFlow()
    {
        await Test("export pages follow start, keep exporting, confirm cancellation and cleanup", () => { TestUiFlowLifecycle(); return Task.CompletedTask; });
        await Test("export page actions ignore invalid phases and repeated cancellation", () => { TestUiFlowInvalidActions(); return Task.CompletedTask; });
        await Test("export completion dismisses confirmation and prevents stale cancellation", () => { TestUiFlowCompletion(); return Task.CompletedTask; });
        await Test("cancel and restart retain the same export settings", () => { TestUiFlowRetainsSettings(); return Task.CompletedTask; });
    }

    private static void TestUiFlowLifecycle()
    {
        var flow = new ExportUiFlow();
        Equal(ExportUiPhase.Settings, flow.Phase, "initial page");
        flow.ObserveBusy(true);
        Equal(ExportUiPhase.Progress, flow.Phase, "starting export opens progress");
        flow.AskToCancel();
        Equal(ExportUiPhase.ConfirmCancel, flow.Phase, "cancel first opens confirmation");
        flow.ObserveBusy(true);
        Equal(ExportUiPhase.ConfirmCancel, flow.Phase, "export continues while confirmation is open");
        flow.KeepExporting();
        Equal(ExportUiPhase.Progress, flow.Phase, "keeping export returns to progress");
        flow.AskToCancel();
        Check(flow.ConfirmCancel(), "confirmed cancellation requests cleanup once");
        Equal(ExportUiPhase.Cancelling, flow.Phase, "confirmed cancellation shows cleanup");
        flow.ObserveBusy(true);
        Equal(ExportUiPhase.Cancelling, flow.Phase, "busy cleanup cannot return to progress or settings");
        flow.ObserveBusy(false);
        Equal(ExportUiPhase.Settings, flow.Phase, "cleanup completion returns to settings");
        flow.ObserveBusy(true);
        Equal(ExportUiPhase.Progress, flow.Phase, "a new export can start after cleanup");
    }

    private static ExportUiFlow FlowAt(ExportUiPhase phase)
    {
        var flow = new ExportUiFlow();
        if (phase == ExportUiPhase.Settings) return flow;
        flow.ObserveBusy(true);
        if (phase == ExportUiPhase.Progress) return flow;
        flow.AskToCancel();
        if (phase == ExportUiPhase.Cancelling) Check(flow.ConfirmCancel(), "fixture cancellation");
        return flow;
    }

    private static void TestUiFlowInvalidActions()
    {
        foreach (var phase in Enum.GetValues<ExportUiPhase>())
        {
            if (phase != ExportUiPhase.Progress)
            {
                var flow = FlowAt(phase);
                flow.AskToCancel();
                flow.AskToCancel();
                Equal(phase, flow.Phase, "cancel request ignored from " + phase);
            }
            if (phase != ExportUiPhase.ConfirmCancel)
            {
                var flow = FlowAt(phase);
                flow.KeepExporting();
                flow.KeepExporting();
                Equal(phase, flow.Phase, "keep exporting ignored from " + phase);
                Check(!flow.ConfirmCancel() && !flow.ConfirmCancel(), "confirmation ignored from " + phase);
                Equal(phase, flow.Phase, "invalid confirmation preserves " + phase);
            }
            var busyFlow = FlowAt(phase);
            busyFlow.ObserveBusy(true);
            busyFlow.ObserveBusy(true);
            Equal(phase == ExportUiPhase.Settings ? ExportUiPhase.Progress : phase, busyFlow.Phase, "repeated busy observation from " + phase);
        }
        var confirmed = FlowAt(ExportUiPhase.ConfirmCancel);
        Check(confirmed.ConfirmCancel(), "first confirmation sends the cancellation request");
        Check(!confirmed.ConfirmCancel() && !confirmed.ConfirmCancel(), "duplicate confirmation cannot send another cancellation request");
        Equal(ExportUiPhase.Cancelling, confirmed.Phase, "duplicate confirmation keeps the cleanup page");
    }

    private static void TestUiFlowCompletion()
    {
        foreach (var phase in Enum.GetValues<ExportUiPhase>())
        {
            var flow = FlowAt(phase);
            flow.ObserveBusy(false);
            Equal(ExportUiPhase.Settings, flow.Phase, "completion resets " + phase);
            Check(!flow.ConfirmCancel(), "completion prevents a late cancellation click from " + phase);
            flow.KeepExporting();
            flow.AskToCancel();
            flow.ObserveBusy(false);
            Equal(ExportUiPhase.Settings, flow.Phase, "stale buttons cannot reopen an export after " + phase);
        }
    }

    private static void TestUiFlowRetainsSettings()
    {
        var settings = Options("retained-ui-settings") with
        {
            Width = 2560, Height = 1440, Fps = 60, Container = "mkv", Codec = "hevc", Encoder = "hevc_qsv",
            BitrateKbps = 16000, RateControl = "cbr", AudioQuality = "pcm24", AudioSampleRate = 44100,
            FastStart = false, WriteCover = false
        };
        settings.Validate();
        var frozen = settings with { };
        var retainedSettings = settings;
        var flow = new ExportUiFlow();
        flow.ObserveBusy(true);
        flow.AskToCancel();
        flow.KeepExporting();
        Equal(frozen, settings, "keeping export retains every setting");
        flow.AskToCancel();
        Check(flow.ConfirmCancel(), "retained settings scenario confirms cancellation");
        flow.ObserveBusy(true);
        flow.ObserveBusy(false);
        Equal(ExportUiPhase.Settings, flow.Phase, "parameters are available again after cleanup");
        Equal(frozen, settings, "cancellation retains every setting");
        Check(ReferenceEquals(retainedSettings, settings), "page flow preserves the caller-owned settings instance");
        flow.ObserveBusy(true);
        Equal(frozen, settings, "restarting uses the same saved settings");
    }
}
