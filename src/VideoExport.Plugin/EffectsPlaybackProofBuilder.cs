using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

// Freezes the result of the original playback compiler/binder. Editor mapping
// warning flags are neither changed nor promoted into playback authorization.
internal static class EffectsPlaybackProofBuilder
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static EffectsSourceBinding WithRequiredProof(EffectsSourceBinding binding, Func<EffectsPlaybackProof> capture) =>
        binding.Mapped && binding.SelectedSceneTrusted && (!binding.ArchivePairFresh || !binding.PairingTrusted)
            ? binding with { PlaybackProof = capture() } : binding;

    internal static EffectsPlaybackProof Create(object index, string expectedPlaybackPathKey, string expectedPlaybackSha256,
        int expectedRecordCount, string workspaceSource, IReadOnlyList<string> continueIdentities, string optionsIdentity,
        IReadOnlyList<EffectsSourceFile> inputs, IReadOnlyList<string> absentInputs)
    {
        if (workspaceSource != "none" && (!workspaceSource.StartsWith("embedded-environment:", StringComparison.Ordinal)
            || !int.TryParse(workspaceSource["embedded-environment:".Length..], out int commands) || commands <= 0))
            throw Failure("The effective playback proof does not cover an external workspace source.");
        if (string.IsNullOrWhiteSpace(optionsIdentity) || continueIdentities == null || inputs == null || absentInputs == null)
            throw Failure("The original effective playback proof is incomplete.");
        var continues = continueIdentities.Select(Hash).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var files = inputs.Select(file => file == null || file.Length <= 0 ? throw Failure("An effective playback input is incomplete.")
            : file with { FullPath = FullPath(file.FullPath), Sha256 = Hash(file.Sha256) }).OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var absent = absentInputs.Select(FullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string path in absent)
            if (File.Exists(path) || Directory.Exists(path)) throw Failure("An uncovered effective playback dependency appeared: " + path);
        string indexIdentity = IndexIdentity(index, expectedPlaybackPathKey, expectedPlaybackSha256, expectedRecordCount);
        string identity = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            Version = 1, Kind = "original-embedded-playback-index-v1", Index = indexIdentity, WorkspaceSource = workspaceSource,
            ContinueIdentities = continues, Options = optionsIdentity,
            Inputs = files.Select(file => new { Path = file.FullPath.ToUpperInvariant(), file.Sha256, file.Length }),
            AbsentInputs = absent.Select(path => path.ToUpperInvariant())
        })));
        return new(identity, Array.AsReadOnly(files), Array.AsReadOnly(absent));
    }

    internal static string IndexIdentity(object index, string expectedPlaybackPathKey, string expectedPlaybackSha256, int expectedRecordCount)
    {
        if (index == null || expectedRecordCount <= 0 || expectedRecordCount > 100000)
            throw Failure("The original playback index is absent or outside the retained record bound.");
        if (Hash((string?)Property(index, "PlaybackPathKey") ?? "") != Hash(expectedPlaybackPathKey)
            || Hash((string?)Property(index, "PlaybackRevisionSha256") ?? "") != Hash(expectedPlaybackSha256)
            || Convert.ToInt32(Property(index, "RecordCount")) != expectedRecordCount)
            throw Failure("The original playback index belongs to a different exact archive.");
        var method = index.GetType().GetMethod("ObserveCommandBatchAtRow", Flags, null, new[] { typeof(int) }, null)
            ?? throw Failure("Original playback index row observation is unavailable.");
        var rows = new List<JsonElement>(expectedRecordCount);
        int count = 0;
        for (int row = 0; row < expectedRecordCount; row++)
        {
            object result = method.Invoke(index, new object[] { row }) ?? throw Failure("Original playback row observation returned no result.");
            if (Property(result, "Success") is not true || Property(result, "Value") is not { } value)
                throw Failure("Original playback index could not prove a concrete row.");
            if (Convert.ToInt32(Property(value, "PlaybackRecordIndex") ?? -1) != row)
                throw Failure("Original playback index returned a different concrete row.");
            if (Property(value, "Batch") is { } batch)
            {
                if (Convert.ToInt32(Property(batch, "PlaybackRecordIndex")) != row)
                    throw Failure("Original playback command batch belongs to a different row.");
                count++;
            }
            rows.Add(JsonSerializer.SerializeToElement(value, value.GetType()));
        }
        if (count != Convert.ToInt32(Property(index, "BatchCount")))
            throw Failure("Original playback index batch count differs from its concrete rows.");
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            Version = 1, PlaybackPathKey = Hash(expectedPlaybackPathKey), PlaybackSha256 = Hash(expectedPlaybackSha256),
            RecordCount = expectedRecordCount, BatchCount = count, Rows = rows
        })));
    }

    internal static string OptionsIdentity(object options, string projectionKey, bool embeddedReady) => JsonSerializer.Serialize(new {
        ProjectionKey = projectionKey, EmbeddedReady = embeddedReady,
        Values = options.GetType().GetProperties(Flags).Where(property => property.GetMethod?.IsPublic == true)
            .OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => new {
                property.Name, Value = JsonSerializer.SerializeToElement(property.GetValue(options), property.PropertyType)
            }).ToArray()
    });
    private static object? Property(object value, string name) => (value.GetType().GetProperty(name, Flags)
        ?? throw Failure("The inspected original playback proof shape changed: " + name)).GetValue(value);
    private static string Hash(string value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit)
        ? value.ToUpperInvariant() : throw Failure("An effective playback source digest is missing or malformed.");
    private static string FullPath(string value) => !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
        ? Path.GetFullPath(value) : throw Failure("An effective playback dependency path is not absolute.");
    private static ExportException Failure(string reason) => new("strict_effects_source_unavailable", reason);
}
