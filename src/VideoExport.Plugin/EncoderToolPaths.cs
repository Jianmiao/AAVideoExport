using System;
using System.IO;

namespace AAVideoExport.Plugin;

/// <summary>Resolves encoder tools relative to the installed mod, never the working directory.</summary>
internal static class EncoderToolPaths
{
    internal static string Resolve(string? configuredPath, string fileName, string modDirectory) =>
        Resolve(configuredPath, fileName, modDirectory, File.Exists);

    // The injected existence check keeps path selection independently testable.
    internal static string Resolve(string? configuredPath, string fileName, string modDirectory,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) ||
            fileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            throw new ArgumentException("Encoder filename must be a plain filename.", nameof(fileName));
        if (string.IsNullOrWhiteSpace(modDirectory) || !Path.IsPathFullyQualified(modDirectory))
            throw new ArgumentException("The installed mod directory must be absolute.", nameof(modDirectory));

        var configured = configuredPath?.Trim();
        if (string.IsNullOrEmpty(configured) || string.Equals(configured, fileName, StringComparison.OrdinalIgnoreCase))
        {
            var bundled = Path.GetFullPath(Path.Combine(modDirectory, "tools", fileName));
            return fileExists(bundled) ? bundled : fileName;
        }

        // Explicit absolute paths remain authoritative, including when missing:
        // report that failure when used instead of silently selecting another tool.
        if (Path.IsPathFullyQualified(configured)) return configured;
        if (Path.IsPathRooted(configured))
            throw new ArgumentException("Encoder paths must be fully absolute or relative to the mod directory.", nameof(configuredPath));
        return Path.GetFullPath(Path.Combine(modDirectory, configured));
    }
}
