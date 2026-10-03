namespace AAVideoExport.Core;

/// <summary>
/// Keeps the export canvas explicit when a user chooses an aspect ratio or
/// enters dimensions directly.  The selected width is retained and only the
/// height is recomputed for a named ratio, so the UI never silently stretches
/// an image or changes the requested pixel density.
/// </summary>
public static class OutputDimensions
{
    private const int MaxAxis = 7680;
    private const int MaxShortAxis = 4320;

    public static string AspectKey(int width, int height, int sourceWidth, int sourceHeight)
    {
        RequirePositive(width, height);
        if (SameRatio(width, height, 16, 9)) return "16:9";
        if (SameRatio(width, height, 9, 16)) return "9:16";
        if (SameRatio(width, height, 4, 3)) return "4:3";
        if (SameRatio(width, height, 1, 1)) return "1:1";
        if (SameRatio(width, height, 16, 10)) return "16:10";
        if (sourceWidth > 0 && sourceHeight > 0 && SameRatio(width, height, sourceWidth, sourceHeight))
            return "native";
        return "custom";
    }

    public static string RatioLabel(int width, int height)
    {
        RequirePositive(width, height);
        var key = AspectKey(width, height, 0, 0);
        if (key != "custom") return key == "native" ? "custom" : key;
        int divisor = GreatestCommonDivisor(width, height);
        return $"{width / divisor}:{height / divisor}";
    }

    /// <summary>
    /// Applies a named ratio while preserving the current width.  The
    /// <c>native</c>/<c>original</c> ratio uses the source dimensions.  A
    /// <c>custom</c> ratio leaves both manually entered dimensions untouched.
    /// </summary>
    public static (int Width, int Height) ApplyAspect(
        int width, int height, string ratio, int sourceWidth, int sourceHeight)
    {
        RequirePositive(width, height);
        if (string.IsNullOrWhiteSpace(ratio))
            throw Invalid("Aspect ratio is required.");

        string key = ratio.Trim().ToLowerInvariant() switch
        {
            "16:9" => "16:9",
            "9:16" => "9:16",
            "4:3" => "4:3",
            "1:1" => "1:1",
            "16:10" => "16:10",
            "native" or "original" or "source" => "native",
            "custom" => "custom",
            _ => throw Invalid($"Unsupported aspect ratio '{ratio}'.")
        };

        if (key == "custom")
        {
            ValidateTarget(width, height);
            return (width, height);
        }
        (int Numerator, int Denominator) target = key switch
        {
            "16:9" => (16, 9),
            "9:16" => (9, 16),
            "4:3" => (4, 3),
            "1:1" => (1, 1),
            "16:10" => (16, 10),
            _ when sourceWidth > 0 && sourceHeight > 0 => (sourceWidth, sourceHeight),
            _ => throw Invalid("Native aspect ratio needs positive source dimensions.")
        };
        int nextHeight = RoundToEven((long)width * target.Denominator, target.Numerator);
        ValidateTarget(width, nextHeight);
        return (width, nextHeight);
    }

    private static bool SameRatio(int leftWidth, int leftHeight, int rightWidth, int rightHeight) =>
        (long)leftWidth * rightHeight == (long)leftHeight * rightWidth;

    private static void RequirePositive(int width, int height)
    {
        if (width <= 0 || height <= 0) throw Invalid("Output dimensions must be positive.");
    }

    private static void ValidateTarget(int width, int height)
    {
        if (width < 16 || height < 16 || width > MaxAxis || height > MaxAxis ||
            Math.Min(width, height) > MaxShortAxis || width % 2 != 0 || height % 2 != 0)
            throw Invalid("Output dimensions must be even and within 7680 × 4320.");
    }

    private static int RoundToEven(long numerator, int denominator)
    {
        long value = (numerator + denominator / 2) / denominator;
        if (value < 2) value = 2;
        return checked((int)(value % 2 == 0 ? value : value + 1));
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return Math.Abs(left);
    }

    private static ExportException Invalid(string message) => new("invalid_settings", message);
}
