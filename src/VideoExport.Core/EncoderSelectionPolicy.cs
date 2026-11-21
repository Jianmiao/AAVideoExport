namespace AAVideoExport.Core;

/// <summary>Chooses only sample-tested encoders in the user's explicit encoding mode.</summary>
public static class EncoderSelectionPolicy
{
    public static IReadOnlyList<EncoderCapability> GetAvailable(IEnumerable<EncoderCapability> candidates,
        string encodingMode, string? codec = null)
    {
        ValidateMode(encodingMode);
        bool hardware = encodingMode == "hardware";
        return Array.AsReadOnly(candidates.Where(candidate => candidate.Available && candidate.Hardware == hardware &&
                (codec is null || candidate.Codec == codec))
            .GroupBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal).ToArray());
    }

    public static string Select(ExportOptions options, IEnumerable<EncoderCapability> candidates, string? gpuName = null)
    {
        options.Validate();
        if (options.EncodingMode == "hardware") return HardwareEncoderPolicy.Select(options, candidates, gpuName);
        if (options.Encoder != "auto" && EncoderCatalog.All.Any(candidate => candidate.Name == options.Encoder && candidate.Hardware))
            throw new ExportException("software_encoder_required", "CPU software encoding requires a software encoder that passed its sample encode.");
        var available = GetAvailable(candidates, "software", options.Codec);
        if (options.Encoder != "auto")
            return available.FirstOrDefault(candidate => candidate.Name == options.Encoder)?.Name
                ?? throw new ExportException("software_encoder_unavailable", "The selected software encoder is unavailable or failed its sample encode.");
        // Prefer SVT to AOM for interactive export speed if both AV1 encoders passed.
        return available.OrderBy(candidate => candidate.Name == "libsvtav1" ? 0 : 1).FirstOrDefault()?.Name
            ?? throw new ExportException("software_encoder_unavailable", "No working software encoder is available for " + options.Codec + ". Install an FFmpeg build containing the requested software encoder.");
    }

    private static void ValidateMode(string encodingMode)
    {
        if (encodingMode is not ("hardware" or "software"))
            throw new ExportException("invalid_settings", "Encoding mode must be hardware or software.");
    }
}
