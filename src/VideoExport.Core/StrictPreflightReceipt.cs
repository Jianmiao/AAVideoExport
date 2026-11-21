using System.Security.Cryptography;
using System.Text.Json;

namespace AAVideoExport.Core;

/// <summary>Immutable identity and integrity metadata for a headless strict preflight.</summary>
public sealed record StrictPreflightReceipt(
    int Version,
    string Identity,
    string SourceSha256,
    string HostIdentity,
    string ModFingerprint,
    string SettingsFingerprint,
    long FrameCount,
    int Fps,
    int SampleRate,
    int Channels,
    long AudioSampleFrames,
    string FrameTraceSha256,
    string PcmSha256,
    string TranscriptSha256)
{
    public const int CurrentVersion = 1;

    public static StrictPreflightReceipt Create(string identity, string sourceSha256, string hostIdentity,
        string modFingerprint, string settingsFingerprint, long frameCount, int fps, int sampleRate, int channels,
        string frameTraceSha256, string pcmSha256, string transcriptSha256, long audioSampleFrames)
    {
        if (frameCount <= 0 || fps <= 0 || sampleRate <= 0 || audioSampleFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (channels is not (1 or 2 or 4 or 5 or 6 or 8)) throw new ArgumentOutOfRangeException(nameof(channels));
        foreach (var (value, name) in new[]
        {
            (identity, nameof(identity)), (sourceSha256, nameof(sourceSha256)), (hostIdentity, nameof(hostIdentity)),
            (modFingerprint, nameof(modFingerprint)), (settingsFingerprint, nameof(settingsFingerprint)),
            (frameTraceSha256, nameof(frameTraceSha256)), (pcmSha256, nameof(pcmSha256)), (transcriptSha256, nameof(transcriptSha256))
        }) NormalizeHash(value, name);
        return new StrictPreflightReceipt(CurrentVersion, identity.ToLowerInvariant(), sourceSha256.ToLowerInvariant(),
            hostIdentity.ToLowerInvariant(), modFingerprint.ToLowerInvariant(), settingsFingerprint.ToLowerInvariant(),
            frameCount, fps, sampleRate, channels, audioSampleFrames, frameTraceSha256.ToLowerInvariant(),
            pcmSha256.ToLowerInvariant(), transcriptSha256.ToLowerInvariant());
    }

    internal void Validate(string expectedIdentity)
    {
        NormalizeHash(expectedIdentity, nameof(expectedIdentity));
        if (Version != CurrentVersion || !string.Equals(Identity, expectedIdentity, StringComparison.OrdinalIgnoreCase))
            throw new ExportException("preflight_cache_mismatch", "Strict preflight cache identity or protocol version differs.");
        if (FrameCount <= 0 || Fps <= 0 || SampleRate <= 0 || Channels is not (1 or 2 or 4 or 5 or 6 or 8) || AudioSampleFrames < 0)
            throw new ExportException("preflight_cache_invalid", "Strict preflight cache contains invalid timeline dimensions.");
        foreach (var (value, name) in new[]
        {
            (Identity, nameof(Identity)), (SourceSha256, nameof(SourceSha256)), (HostIdentity, nameof(HostIdentity)),
            (ModFingerprint, nameof(ModFingerprint)), (SettingsFingerprint, nameof(SettingsFingerprint)),
            (FrameTraceSha256, nameof(FrameTraceSha256)), (PcmSha256, nameof(PcmSha256)), (TranscriptSha256, nameof(TranscriptSha256))
        }) NormalizeHash(value, name);
    }

    private static void NormalizeHash(string? value, string name)
    {
        if (value == null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("A canonical SHA-256 hexadecimal digest is required.", name);
    }
}

public sealed record StrictPreflightCacheFiles(string DirectoryPath, string ManifestPath, string PcmPath, string TranscriptPath);

/// <summary>Publishes and reads a complete preflight as one immutable directory.</summary>
public sealed class StrictPreflightCache
{
    private readonly string root;
    private readonly string identity;
    public StrictPreflightCacheFiles PublishedFiles { get; }

    public StrictPreflightCache(string rootDirectory, string identity)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("Cache root is required.", nameof(rootDirectory));
        StrictPreflightReceipt.Create(identity, identity, identity, identity, identity, 1, 1, 8000, 1, identity, identity, identity, 0);
        root = Path.GetFullPath(rootDirectory);
        this.identity = identity.ToLowerInvariant();
        string directory = Path.Combine(root, this.identity);
        PublishedFiles = new StrictPreflightCacheFiles(directory, Path.Combine(directory, "manifest.json"),
            Path.Combine(directory, "audio.f32le"), Path.Combine(directory, "transcript.bin"));
    }

    public bool TryRead(string expectedIdentity, out StrictPreflightReceipt receipt, out StrictPreflightCacheFiles files)
    {
        receipt = null!;
        files = PublishedFiles;
        if (!string.Equals(expectedIdentity, identity, StringComparison.OrdinalIgnoreCase) || !File.Exists(files.ManifestPath)
            || !File.Exists(files.PcmPath) || !File.Exists(files.TranscriptPath)) return false;
        try
        {
            using var stream = new FileStream(files.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length <= 0 || stream.Length > 16384) return false;
            receipt = JsonSerializer.Deserialize<StrictPreflightReceipt>(stream) ?? throw new InvalidDataException();
            receipt.Validate(expectedIdentity);
            if (!string.Equals(HashFile(files.PcmPath), receipt.PcmSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(HashFile(files.TranscriptPath), receipt.TranscriptSha256, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (ArgumentException) { return false; }
        catch (ExportException) { return false; }
    }

    public void Publish(StrictPreflightReceipt receipt, string pcmSourcePath, string transcriptSourcePath)
    {
        receipt.Validate(identity);
        if (!File.Exists(pcmSourcePath) || !File.Exists(transcriptSourcePath))
            throw new ExportException("preflight_cache_invalid", "Strict preflight source artifacts are missing.");
        if (!string.Equals(HashFile(pcmSourcePath), receipt.PcmSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(HashFile(transcriptSourcePath), receipt.TranscriptSha256, StringComparison.OrdinalIgnoreCase))
            throw new ExportException("preflight_cache_invalid", "Strict preflight source artifact hashes do not match its receipt.");
        if (TryRead(identity, out var existing, out _))
        {
            if (!existing.Equals(receipt)) throw new ExportException("preflight_cache_mismatch", "An immutable strict preflight cache already exists with a different receipt.");
            return;
        }
        Directory.CreateDirectory(root);
        string staging = Path.Combine(root, "." + identity + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(staging);
            CopyCreate(pcmSourcePath, Path.Combine(staging, "audio.f32le"));
            CopyCreate(transcriptSourcePath, Path.Combine(staging, "transcript.bin"));
            using (var stream = new FileStream(Path.Combine(staging, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, receipt);
                stream.Flush(flushToDisk: true);
            }
            try { Directory.Move(staging, PublishedFiles.DirectoryPath); }
            catch (IOException)
            {
                if (!TryRead(identity, out var winner, out _)) throw;
                if (!winner.Equals(receipt)) throw new ExportException("preflight_cache_mismatch", "A concurrent strict preflight cache winner differs.");
            }
        }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
    }

    private static void CopyCreate(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
    }
}
