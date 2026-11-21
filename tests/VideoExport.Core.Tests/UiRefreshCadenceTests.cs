using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestUiRefreshCadence()
    {
        await Test("progress presentation stays bounded while the exporter runs at 600 fps", () =>
        {
            var cadence = new UiRefreshCadence();
            int refreshes = 0;
            for (int frame = 0; frame < 6000; frame++)
                if (cadence.ShouldRefresh(frame / 600d)) refreshes++;
            Check(refreshes >= 98 && refreshes <= 100, "10 seconds should present about 100 times, not 6000: " + refreshes);
            return Task.CompletedTask;
        });
        await Test("cancel confirmation and completion present immediately between regular refreshes", () =>
        {
            var cadence = new UiRefreshCadence();
            var flow = new ExportUiFlow();
            flow.ObserveBusy(true);
            Check(cadence.ShouldRefresh(0), "initial progress");
            Check(!cadence.ShouldRefresh(.001), "no ordinary refresh is due");
            flow.AskToCancel();
            cadence.Invalidate();
            Check(cadence.ShouldRefresh(.002), "confirmation must not wait for the periodic update");
            Equal(ExportUiPhase.ConfirmCancel, flow.Phase, "native click is handled before presentation");
            flow.KeepExporting();
            cadence.Invalidate();
            Check(cadence.ShouldRefresh(.003), "continue immediately dismisses confirmation");
            flow.ObserveBusy(false);
            cadence.Invalidate();
            Check(cadence.ShouldRefresh(.004), "completion immediately restores settings");
            Check(!cadence.ShouldRefresh(.005), "forced updates must not turn into continuous refresh");
            return Task.CompletedTask;
        });
        await Test("visibility and window resize invalidate once without depending on export frame count", () =>
        {
            var cadence = new UiRefreshCadence();
            Check(cadence.ShouldRefresh(100), "first visible frame");
            cadence.Invalidate();
            cadence.Invalidate();
            Check(cadence.ShouldRefresh(100.01), "resize/reopen is immediate");
            Check(!cadence.ShouldRefresh(100.01), "multiple invalidations coalesce");
            Check(cadence.ShouldRefresh(110), "a delayed Unity frame still presents");
            Check(!cadence.ShouldRefresh(110.001), "no catch-up presentation burst");
            Check(cadence.ShouldRefresh(0), "new wall-clock epoch does not stall refresh");
            return Task.CompletedTask;
        });
    }
}
