namespace AAVideoExport.Core;

// The shell retains its original proportions while the settings scroll inside it.
// All section coordinates are measured from the top of the modal in design pixels.
public sealed record ExportPanelLayout
{
    public const int PanelWidth = 980;
    public const int DefaultPanelHeight = 800;
    public const int MinimumPanelHeight = DefaultPanelHeight;
    public const int ContentTop = 240;
    public const int FooterHeight = 120;
    public const int ContentFooterGap = 14;
    public const int AdvancedHeaderHeight = 44;
    public const int AdvancedBodyHeight = 278;
    public int OutputY { get; init; } = ContentTop;
    public int OutputHeight { get; init; }
    public int SuperResolutionY { get; init; }
    public int SuperResolutionHeight { get; init; }
    public int AudioY { get; init; }
    public int AudioHeight { get; init; }
    public int AdvancedY { get; init; }
    public int AdvancedBodyY { get; init; }
    public int AdvancedHeight { get; init; }
    public int FooterY { get; init; }
    public int PanelHeight { get; init; }
    public int ContentHeight { get; init; }
    public int ViewportHeight => FooterY - ContentTop - ContentFooterGap;
    public int MaximumScroll => Math.Max(0, ContentHeight - ViewportHeight);
    public bool ShowCustomDimensions { get; init; }
    public bool ShowSuperResolutionControls { get; init; }
    public bool ShowSharpness { get; init; }
    public bool ShowAdvanced { get; init; }
    public bool ShowCustomBitrate { get; init; }

    public static ExportPanelLayout Create(bool customDimensions, bool superResolution,
        bool rcasModel, bool advanced, bool customBitrate, int panelHeight = DefaultPanelHeight)
    {
        bool sharpness = superResolution && rcasModel;
        int outputHeight = 168 + (customDimensions ? 64 : 0);
        int superY = ContentTop + outputHeight + 10;
        int superHeight = superResolution ? 164 + (sharpness ? 62 : 0) : 60;
        int audioY = superY + superHeight + 10;
        const int audioHeight = 172;
        int advancedY = audioY + audioHeight + 10;
        int advancedHeight = AdvancedHeaderHeight + (advanced ? AdvancedBodyHeight : 0);
        return new ExportPanelLayout
        {
            OutputHeight = outputHeight, SuperResolutionY = superY, SuperResolutionHeight = superHeight,
            AudioY = audioY, AudioHeight = audioHeight,
            AdvancedY = advancedY, AdvancedBodyY = advancedY + AdvancedHeaderHeight, AdvancedHeight = advancedHeight,
            FooterY = DefaultPanelHeight - FooterHeight, PanelHeight = DefaultPanelHeight,
            ContentHeight = advancedY + advancedHeight - ContentTop,
            ShowCustomDimensions = customDimensions, ShowSuperResolutionControls = superResolution, ShowSharpness = sharpness,
            ShowAdvanced = advanced, ShowCustomBitrate = advanced
        };
    }

    public static int HeightForViewport(float availableWidth, float availableHeight) => DefaultPanelHeight;

    public float FitScale(float availableWidth, float availableHeight) =>
        Math.Max(.001f, Math.Min(availableWidth * .90f / PanelWidth, availableHeight * .90f / DefaultPanelHeight));

    public float ClampScroll(float offset) => Math.Clamp(offset, 0, MaximumScroll);
}
