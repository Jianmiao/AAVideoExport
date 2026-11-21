namespace AAVideoExport.Core;

/// <summary>Validates a remembered folder without probing or creating user directories.</summary>
public static class OutputDirectoryPreference
{
    public static string Resolve(string? savedDirectory) => TryNormalize(savedDirectory, out var directory)
        ? directory
        : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    /// <summary>
    /// Keep a Windows path literal when displaying it in NGUI. UIInput.Start
    /// decodes backslash-n even after a programmatic value assignment. This
    /// spelling is also suitable for storage; Resolve returns a native path.
    /// </summary>
    public static string ToPortableValue(string directory) => OperatingSystem.IsWindows()
        ? directory.Replace('\\', '/')
        : directory;

    public static bool TryNormalize(string? value, out string directory)
    {
        directory = "";
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => c < 32 || "<>\"|?*".Contains(c))) return false;
        var candidate = value.Trim();
        if (!Path.IsPathFullyQualified(candidate)) return false;
        if (OperatingSystem.IsWindows() && (candidate.IndexOf(':', 2) >= 0 || candidate.StartsWith(@"\\.\", StringComparison.Ordinal))) return false;
        try
        {
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
