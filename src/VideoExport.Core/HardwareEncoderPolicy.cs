namespace AAVideoExport.Core;

public static class HardwareEncoderPolicy
{
    /// <summary>Returns distinct, sorted hardware encoders that passed their sample encode.</summary>
    public static IReadOnlyList<EncoderCapability> GetAvailable(IEnumerable<EncoderCapability> candidates, string? codec = null) =>
        Array.AsReadOnly(candidates.Where(candidate => candidate.Available && candidate.Hardware && (codec is null || candidate.Codec == codec))
            .GroupBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal).ToArray());

    public static string Select(ExportOptions options, IEnumerable<EncoderCapability> candidates, string? gpuName)
    {
        options.Validate();
        if (options.Encoder != "auto" && EncoderCatalog.All.Any(candidate => candidate.Name == options.Encoder && !candidate.Hardware))
            throw new ExportException("hardware_encoder_required", "Video export requires a hardware encoder that passed its sample encode.");

        var available = GetAvailable(candidates, options.Codec);
        if (options.Encoder != "auto")
            return available.FirstOrDefault(candidate => candidate.Name == options.Encoder)?.Name
                ?? throw new ExportException("hardware_encoder_unavailable", "The selected hardware encoder is unavailable or failed its sample encode.");

        string gpu = gpuName ?? "";
        string preferred = gpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) || gpu.Contains("Arc", StringComparison.OrdinalIgnoreCase) ? "_qsv"
            : gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "_nvenc"
            : gpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpu.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "_amf" : "";
        return available.OrderBy(candidate => preferred.Length > 0 && candidate.Name.EndsWith(preferred, StringComparison.Ordinal) ? 0 : 1)
            .FirstOrDefault()?.Name
            ?? throw new ExportException("hardware_encoder_unavailable", "No working hardware encoder is available for " + options.Codec + ".");
    }
}
