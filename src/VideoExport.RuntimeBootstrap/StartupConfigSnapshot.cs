using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AAVideoExport.RuntimeBootstrap;

// One read-only active-profile input snapshot before ordinary Mod loading.
// No hooks, file writes, broad profile enumeration or unrelated values.
public static class StartupConfigSnapshot
{
    public const string Schema = "aave.startup-menu-config.v1";
    public static int ProtocolVersion => 1;
    private static int attempted;
    private static StartupMenuConfig? snapshot;
    internal static void Capture(string hostRoot)
    {
        if (Interlocked.CompareExchange(ref attempted, 1, 0) != 0) return;
        snapshot = Inspect(hostRoot, () => AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "MenuBgmSelector"));
    }
    public static string GetSnapshotJson(string absoluteConfigPath)
    {
        StartupMenuConfig value = snapshot ?? StartupMenuConfig.Unavailable("Startup capture was not attempted.");
        try
        {
            if (!Path.IsPathFullyQualified(absoluteConfigPath) || !string.Equals(Path.GetFullPath(absoluteConfigPath), value.ConfigPath, StringComparison.OrdinalIgnoreCase))
                value = StartupMenuConfig.Unavailable("Requested config is not the captured active-profile path.");
        }
        catch (Exception error) { value = StartupMenuConfig.Unavailable(error.Message); }
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteString("schema", Schema); writer.WriteBoolean("available", value.Available);
            writer.WriteBoolean("beforeMenuAssemblyLoad", value.BeforeMenuAssemblyLoad); writer.WriteString("reason", value.Reason);
            writer.WriteString("hostRoot", value.HostRoot); writer.WriteString("activeProfile", value.ActiveProfile);
            writer.WriteString("configPath", value.ConfigPath); writer.WriteBoolean("exists", value.Exists);
            writer.WriteString("rawSha256", value.RawSha256); writer.WriteBoolean("hasDisplayName", value.HasDisplayName);
            writer.WriteString("displayName", value.DisplayName); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
    internal static StartupMenuConfig Inspect(string hostRoot, Func<bool> menuLoaded)
    {
        try
        {
            if (menuLoaded()) return StartupMenuConfig.Unavailable("MenuBgmSelector was already loaded.");
            string root = Path.GetFullPath(hostRoot); SafePath(root);
            string active = Path.Combine(root, "ActiveProfile.txt"); SafePath(active);
            if (!File.Exists(active) || new FileInfo(active).Length > 4096) throw new InvalidDataException("ActiveProfile.txt is missing or unbounded.");
            string[] lines = File.ReadAllLines(active, new UTF8Encoding(false, true));
            if (lines.Length != 1) throw new InvalidDataException("Active profile is ambiguous.");
            string profile = lines[0];
            if (string.IsNullOrWhiteSpace(profile) || profile != profile.Trim() || profile is "." or ".." || profile.EndsWith('.')
                || Path.IsPathRooted(profile) || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile.Contains('/') || profile.Contains('\\'))
                throw new InvalidDataException("Active profile name is unsafe.");
            string cfg = Path.GetFullPath(Path.Combine(root, "profiles", profile, "configs", "foxxlight.azurearchive.menubgmselector.cfg"));
            SafePath(cfg);
            bool exists = File.Exists(cfg); string hash = "", name = ""; bool hasName = false;
            if (exists)
            {
                using var stream = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 65536) throw new InvalidDataException("Menu configuration exceeds the bounded snapshot size.");
                var data = new byte[(int)stream.Length]; stream.ReadExactlyCompat(data);
                hash = Convert.ToHexString(SHA256.HashData(data));
                (hasName, name) = ParseName(new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF'));
            }
            if (menuLoaded()) return StartupMenuConfig.Unavailable("MenuBgmSelector loaded during startup capture.");
            return new(true, true, "", root, profile, cfg, exists, hash, hasName, name);
        }
        catch (Exception error) { return StartupMenuConfig.Unavailable(error.GetType().Name + ": " + error.Message); }
    }
    internal static (bool Found, string Name) ParseName(string text)
    {
        string section = "", name = ""; bool found = false;
        using var lines = new StringReader(text); string? line;
        while ((line = lines.ReadLine()) != null)
        {
            string value = line.Trim(); if (value.Length == 0 || value.StartsWith('#')) continue;
            if (value.StartsWith('['))
            {
                if (!value.EndsWith(']')) throw new InvalidDataException("Ambiguous config section.");
                section = value[1..^1].Trim(); continue;
            }
            int equals = value.IndexOf('='); if (equals <= 0 || section != "Name" || value[..equals].Trim() != "DisplayName") continue;
            if (found) throw new InvalidDataException("Duplicate Name.DisplayName is ambiguous.");
            found = true; name = value[(equals + 1)..].Trim();
            if (name.Contains('\\') || name.Contains('"') || name.Any(char.IsControl))
                throw new InvalidDataException("Escaped/control display-name syntax is outside this bounded input proof.");
        }
        return (found, name);
    }
    private static void SafePath(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Startup config path crosses a reparse point.");
    }
    private static void ReadExactlyCompat(this Stream stream, byte[] bytes)
    {
        int offset = 0; while (offset < bytes.Length) { int count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
    }
}
internal sealed record StartupMenuConfig(bool Available, bool BeforeMenuAssemblyLoad, string Reason, string HostRoot, string ActiveProfile,
    string ConfigPath, bool Exists, string RawSha256, bool HasDisplayName, string DisplayName)
{
    internal static StartupMenuConfig Unavailable(string reason) => new(false, false, reason, "", "", "", false, "", false, "");
}
