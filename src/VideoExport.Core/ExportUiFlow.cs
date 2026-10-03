namespace AAVideoExport.Core;

public enum ExportUiPhase
{
    Settings,
    Progress,
    ConfirmCancel,
    Cancelling
}

public sealed class ExportUiFlow
{
    public ExportUiPhase Phase { get; private set; } = ExportUiPhase.Settings;

    public void ObserveBusy(bool busy)
    {
        if (!busy) Phase = ExportUiPhase.Settings;
        else if (Phase == ExportUiPhase.Settings) Phase = ExportUiPhase.Progress;
    }

    public void AskToCancel()
    {
        if (Phase == ExportUiPhase.Progress) Phase = ExportUiPhase.ConfirmCancel;
    }

    public void KeepExporting()
    {
        if (Phase == ExportUiPhase.ConfirmCancel) Phase = ExportUiPhase.Progress;
    }

    public bool ConfirmCancel()
    {
        if (Phase != ExportUiPhase.ConfirmCancel) return false;
        Phase = ExportUiPhase.Cancelling;
        return true;
    }
}
