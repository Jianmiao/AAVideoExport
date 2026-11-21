using System.Text.Json;

namespace AAVideoExport.Core;

/// <summary>
/// Remembers the first fully verified export for a caller-supplied source and
/// settings identity. Later exports must have the same frame count, story frame
/// trace, raw PCM and observed event/voice/background fingerprints. Runtime
/// outputs belong to this receipt, never to the caller's input identity.
/// Existing receipts are immutable, including under races.
///
/// Identity completeness is the caller's responsibility. Resources omitted from
/// that identity can change without selecting a new baseline; observable frame
/// or PCM changes are then correctly rejected, not silently accepted as a new
/// version. This class does not claim that an unchanged receipt proves identical
/// pixels or accounts for every third-party Mod or external resource.
/// </summary>
public sealed class RepeatExportBaseline
{
    private const int Protocol = 2;
    private readonly string directory;
    private readonly string identity;
    private readonly string path;

    public RepeatExportBaseline(string directory, string identity)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A baseline storage directory is required.", nameof(directory));
        this.directory = Path.GetFullPath(directory);
        this.identity = NormalizeHash(identity, nameof(identity));
        path = Path.Combine(this.directory, this.identity + ".json");
    }

    /// <summary>Read-only check. Calling it before completion never records a baseline.</summary>
    public void Validate(long frames, string frameTraceSha256, string pcmSha256,
        string eventTraceSha256, string voiceReceiptSha256, string backgroundCommandsSha256)
    {
        var candidate = Candidate(frames, frameTraceSha256, pcmSha256, eventTraceSha256, voiceReceiptSha256, backgroundCommandsSha256);
        var baseline = ReadExisting();
        if (baseline != null) Compare(baseline, candidate);
    }

    /// <summary>
    /// Call only after the final media passes all checks. A private sibling file
    /// is flushed before an atomic, non-overwriting rename publishes the receipt.
    /// A concurrent winner must match; it is never replaced.
    /// </summary>
    public void Commit(long frames, string frameTraceSha256, string pcmSha256,
        string eventTraceSha256, string voiceReceiptSha256, string backgroundCommandsSha256)
    {
        var candidate = Candidate(frames, frameTraceSha256, pcmSha256, eventTraceSha256, voiceReceiptSha256, backgroundCommandsSha256);
        var baseline = ReadExisting();
        if (baseline != null)
        {
            Compare(baseline, candidate);
            return;
        }
        string staging = Path.Combine(directory, identity + "." + Guid.NewGuid().ToString("N") + ".tmp");
        bool ownsStaging = false;
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsStaging = true;
                JsonSerializer.Serialize(stream, candidate);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(staging, path, overwrite: false);
                ownsStaging = false;
            }
            catch (IOException)
            {
                // Same-directory Move is atomic: another committer can expose
                // a full receipt, never its partial JSON. Other IO failures must
                // still fail if no valid concurrent winner exists.
                var winner = ReadExisting();
                if (winner == null) throw;
                Compare(winner, candidate);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw ExportException.FromIo(error, "Saving repeat-export baseline"); }
        finally
        {
            if (ownsStaging)
            {
                try { File.Delete(staging); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private Receipt Candidate(long frames, string trace, string pcm, string events, string voice, string background)
    {
        if (frames <= 0) throw new ArgumentOutOfRangeException(nameof(frames));
        return new Receipt(Protocol, identity, frames, NormalizeHash(trace, nameof(trace)), NormalizeHash(pcm, nameof(pcm)),
            NormalizeHash(events, nameof(events)), NormalizeHash(voice, nameof(voice)), NormalizeHash(background, nameof(background)));
    }

    private Receipt? ReadExisting()
    {
        try
        {
            // Publication uses rename. On Windows the rename path can briefly
            // hold DELETE access even when it discovers an existing target;
            // allowing that access prevents readers from conflicting with the
            // non-overwriting publication check. Writers are still excluded.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is <= 0 or > 4096)
                throw Invalid("Receipt size is invalid.");
            var receipt = JsonSerializer.Deserialize<Receipt>(stream);
            if (receipt != null && receipt.Version != Protocol)
                throw Invalid($"Receipt protocol {receipt.Version} does not contain the required protocol {Protocol} output proof. Legacy receipts are not upgraded, replaced, or treated as absent.");
            if (receipt == null || receipt.Identity != identity || receipt.Frames <= 0 ||
                !IsCanonicalHash(receipt.FrameTraceSha256) || !IsCanonicalHash(receipt.PcmSha256)
                || !IsCanonicalHash(receipt.EventTraceSha256) || !IsCanonicalHash(receipt.VoiceReceiptSha256)
                || !IsCanonicalHash(receipt.BackgroundCommandsSha256))
                throw Invalid("Receipt identity, format or timeline fields are invalid.");
            return receipt;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException error) { throw Invalid("Receipt JSON is incomplete or invalid.", error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw ExportException.FromIo(error, "Reading repeat-export baseline"); }
    }

    private static void Compare(Receipt expected, Receipt actual)
    {
        if (expected.Frames != actual.Frames || expected.FrameTraceSha256 != actual.FrameTraceSha256 || expected.PcmSha256 != actual.PcmSha256
            || expected.EventTraceSha256 != actual.EventTraceSha256 || expected.VoiceReceiptSha256 != actual.VoiceReceiptSha256
            || expected.BackgroundCommandsSha256 != actual.BackgroundCommandsSha256)
            throw new ExportException("timeline_repeat_mismatch",
                $"Repeat export differs for source identity {actual.Identity}: expected_frames={expected.Frames}, actual_frames={actual.Frames}, expected_trace_sha256={expected.FrameTraceSha256}, actual_trace_sha256={actual.FrameTraceSha256}, expected_pcm_sha256={expected.PcmSha256}, actual_pcm_sha256={actual.PcmSha256}, expected_events_sha256={expected.EventTraceSha256}, actual_events_sha256={actual.EventTraceSha256}, expected_voice_sha256={expected.VoiceReceiptSha256}, actual_voice_sha256={actual.VoiceReceiptSha256}, expected_background_sha256={expected.BackgroundCommandsSha256}, actual_background_sha256={actual.BackgroundCommandsSha256}. The successful baseline was preserved.");
    }

    private ExportException Invalid(string message, Exception? inner = null) =>
        new("baseline_invalid", $"Repeat-export baseline {identity} is unreadable or incompatible: {message} The existing receipt was preserved.", inner);

    private static string NormalizeHash(string hash, string parameter)
    {
        if (hash == null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException("A 64-character SHA-256 hexadecimal digest is required.", parameter);
        return hash.ToLowerInvariant();
    }

    private static bool IsCanonicalHash(string? hash) => hash != null && hash.Length == 64 &&
        hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record Receipt(int Version, string Identity, long Frames, string FrameTraceSha256, string PcmSha256,
        string EventTraceSha256, string VoiceReceiptSha256, string BackgroundCommandsSha256);
}
