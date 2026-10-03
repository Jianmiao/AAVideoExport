using System.Globalization;

namespace AAVideoExport.Core;

// UI values are decimal Mbps; encoding and existing settings remain in Kbps.
public static class VideoBitrate
{
    public const int RecommendedKbps = 4000;
    public const int MinimumKbps = 100;
    public const int MaximumKbps = 500000;
    public const int SliderMaximumKbps = 50000;

    public static string FormatMbps(int kbps) => (kbps / 1000m).ToString("0.0##", CultureInfo.InvariantCulture);

    public static bool TryParseMbps(string? text, out int kbps)
    {
        kbps = 0;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite |
            NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out decimal mbps)
            || mbps < MinimumKbps / 1000m || mbps > MaximumKbps / 1000m) return false;
        decimal rate = mbps * 1000m;
        if (rate != decimal.Truncate(rate)) return false;
        kbps = (int)rate;
        return true;
    }

    public static float SliderPosition(int kbps) => Math.Clamp(
        (kbps - MinimumKbps) / (float)(SliderMaximumKbps - MinimumKbps), 0, 1);

    public static int FromSlider(float position) => MinimumKbps + (int)Math.Round(
        Math.Clamp(position, 0, 1) * (SliderMaximumKbps - MinimumKbps) / 100d,
        MidpointRounding.AwayFromZero) * 100;
}
