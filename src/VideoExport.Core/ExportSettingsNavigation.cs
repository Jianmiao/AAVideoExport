namespace AAVideoExport.Core;

// Navigation owns presentation state only. ExportOptions and uncommitted text
// remain with the panel. Advanced expands in the same scrolling settings list.
public sealed class ExportSettingsNavigation
{
    public bool IsAdvanced { get; private set; }
    public float ScrollOffset { get; private set; }

    public void ToggleAdvanced() => IsAdvanced = !IsAdvanced;

    public void OpenAdvanced() => IsAdvanced = true;

    public bool Back()
    {
        if (!IsAdvanced) return false;
        IsAdvanced = false;
        return true;
    }

    public void ScrollTo(float offset, ExportPanelLayout layout) =>
        ScrollOffset = layout.ClampScroll(float.IsFinite(offset) ? offset : 0);
}
