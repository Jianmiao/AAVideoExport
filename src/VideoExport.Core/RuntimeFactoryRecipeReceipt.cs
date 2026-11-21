using System.Security.Cryptography;
using System.Text.Json;

namespace AAVideoExport.Core;

/// <summary>A finite factory-scope terminal proof, not a general code or movie-success certificate.</summary>
public sealed class RuntimeFactoryRecipeReceipt
{
    public const string Schema = "aave.verified-runtime-recipes.v2";
    public const string PolicyId = "aa101.interop-initializer.intptr.final-il.v2";
    public const string EvidenceKind = "PRE_MATERIALIZATION_FINAL_IL";
    public string PolicySha256 { get; }
    public string RecipesSha256 { get; }
    public RuntimeFactoryRecipeReceipt(string policySha256, string recipesSha256)
        : this(PolicyId, EvidenceKind, policySha256, recipesSha256) { }
    public RuntimeFactoryRecipeReceipt(string policy, string evidenceKind, string policySha256, string recipesSha256)
    {
        if (policy != PolicyId || evidenceKind != EvidenceKind) throw new ArgumentException("Unsupported runtime recipe policy/evidence kind.");
        PolicySha256 = Normalize(policySha256); RecipesSha256 = Normalize(recipesSha256);
    }

    internal RuntimeFactoryRecipeSidecar Stage(string directory, string stagedMovie, string movieOutput,
        long frames, int fps, int width, int height, string sourceIdentity)
    {
        if (frames <= 0 || fps <= 0 || width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(frames));
        string receiptId = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(directory, "runtime-recipes.json"), destination = movieOutput + ".runtime-recipes.json";
        string movieHash; long movieBytes;
        using (var movie = new FileStream(stagedMovie, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var hash = SHA256.Create()) { movieBytes = movie.Length; movieHash = Convert.ToHexString(hash.ComputeHash(movie)).ToLowerInvariant(); }
        using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // Explicit scalar writing avoids creating a new serializer-emitted
            // accessor after recipe collection has closed.
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject(); writer.WriteString("schema", Schema); writer.WriteString("receiptId", receiptId);
                writer.WriteString("movieFileName", Path.GetFileName(movieOutput)); writer.WriteString("movieSha256", movieHash);
                writer.WriteNumber("movieBytes", movieBytes); writer.WriteNumber("frames", frames); writer.WriteNumber("fps", fps);
                writer.WriteNumber("width", width); writer.WriteNumber("height", height); writer.WriteString("sourceIdentity", sourceIdentity);
                writer.WriteString("policy", PolicyId); writer.WriteString("evidenceKind", EvidenceKind); writer.WriteString("policySha256", PolicySha256);
                writer.WriteString("recipesSha256", RecipesSha256); writer.WriteString("scope", "finite-interop-factory-only");
                writer.WriteBoolean("knownHostBaseline", true); writer.WriteBoolean("preexistingCoverage", false);
                writer.WriteBoolean("allCodeCoverage", false); writer.WriteBoolean("permitsStrictExport", false);
                writer.WriteString("consumerRequirement", "Verify associated movie exists and matches movieSha256; this sidecar alone is not publication proof.");
                writer.WriteEndObject(); writer.Flush();
            }
            output.Flush(flushToDisk: true);
        }
        return new RuntimeFactoryRecipeSidecar(staging, destination, receiptId);
    }

    /// <summary>Requires the associated movie to exist with the recorded bytes/hash; never trusts the sidecar alone.</summary>
    public static RuntimeFactoryRecipeReceipt VerifyPublished(string receiptPath)
    {
        using var stream = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 65536) throw Invalid("Runtime receipt size is invalid.");
        using var json = JsonDocument.Parse(stream); var root = json.RootElement;
        if (root.GetProperty("schema").GetString() != Schema || root.GetProperty("policy").GetString() != PolicyId
            || root.GetProperty("evidenceKind").GetString() != EvidenceKind
            || root.GetProperty("scope").GetString() != "finite-interop-factory-only" || !root.GetProperty("knownHostBaseline").GetBoolean()
            || root.GetProperty("preexistingCoverage").GetBoolean() || root.GetProperty("allCodeCoverage").GetBoolean()
            || root.GetProperty("permitsStrictExport").GetBoolean()) throw Invalid("Runtime receipt claims exceed the factory-only contract.");
        string fileName = root.GetProperty("movieFileName").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName) || Path.GetFileName(fileName) != fileName)
            throw Invalid("Runtime receipt movie association is not a basename.");
        string moviePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(receiptPath))!, fileName);
        string expected = Normalize(root.GetProperty("movieSha256").GetString() ?? "");
        if (!File.Exists(moviePath)) throw Invalid("The associated movie was not published: " + moviePath);
        using var movie = new FileStream(moviePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (movie.Length != root.GetProperty("movieBytes").GetInt64()) throw Invalid("Associated movie length differs from its runtime receipt.");
        using var hash = SHA256.Create();
        if (Convert.ToHexString(hash.ComputeHash(movie)).ToLowerInvariant() != expected) throw Invalid("Associated movie SHA-256 differs from its runtime receipt.");
        return new RuntimeFactoryRecipeReceipt(root.GetProperty("policySha256").GetString() ?? "", root.GetProperty("recipesSha256").GetString() ?? "");
    }
    private static string Normalize(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit)
        ? value.ToLowerInvariant() : throw new ArgumentException("A SHA-256 digest is required.");
    private static ExportException Invalid(string message) => new("runtime_receipt_invalid", message);
}

internal sealed class RuntimeFactoryRecipeSidecar(string staging, string destination, string receiptId)
{
    private bool moved;
    internal string Destination => destination;
    internal void Publish() { File.Move(staging, destination, overwrite: false); moved = true; }
    internal bool TryRollback(out string? residual)
    {
        residual = null; if (!moved) return true;
        try
        {
            if (!File.Exists(destination)) { moved = false; return true; }
            using (var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length is <= 0 or > 65536) throw new IOException("Sidecar ownership can no longer be established.");
                using var json = JsonDocument.Parse(stream);
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("receiptId", out var owner)
                    || owner.ValueKind != JsonValueKind.String || owner.GetString() != receiptId)
                    throw new IOException("Sidecar was replaced by another owner; it was preserved.");
            }
            File.Delete(destination); moved = false; return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { residual = destination + " — " + error.Message; return false; }
    }
}
