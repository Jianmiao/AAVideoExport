using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AAVideoExport.Core;

/// <summary>
/// Proves that preparation and video replay followed the same frame-level story
/// state and produced exactly the same ordered PCM. Mixer callback boundaries
/// may differ: only complete PCM totals and content are compared at the end.
/// The state transcript is disk backed (20 bytes per frame), with fixed RAM use.
/// Calls must be serialized by the capture owner.
/// </summary>
public sealed class StrictReplayLedger : IDisposable
{
    private const int StateBytes = 12;
    private const int RecordBytes = StateBytes + sizeof(long);
    private readonly string path;
    private readonly int channels;
    private readonly FileStream transcript;
    private readonly IncrementalHash preparationHash;
    private readonly IncrementalHash replayHash;
    private readonly IncrementalHash traceHash;
    private readonly bool deleteOnDispose;
    private Phase phase;

    public StrictReplayLedger(string path, int channels)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A unique transcript path is required.", nameof(path));
        if (channels is not (1 or 2 or 4 or 5 or 6 or 8)) throw new ArgumentOutOfRangeException(nameof(channels));
        this.path = Path.GetFullPath(path);
        this.channels = channels;
        transcript = new FileStream(this.path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        preparationHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        replayHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        traceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        deleteOnDispose = true;
    }

    public long TotalFrames { get; private set; }
    public long ReplayFrames { get; private set; }
    public long PreparationSampleFrames { get; private set; }
    public long ReplaySampleFrames { get; private set; }
    public string PreparationPcmSha256 { get; private set; } = "";
    public string ReplayPcmSha256 { get; private set; } = "";
    public string FrameTraceSha256 { get; private set; } = "";
    public string StoragePath => path;

    public void CopyTranscriptTo(string destination)
    {
        if (phase is not (Phase.Replaying or Phase.Complete)) throw new ExportException("invalid_state", "Strict transcript must be sealed before it can be copied to a cache.");
        transcript.Flush(flushToDisk: true);
        long position = transcript.Position;
        try
        {
            transcript.Position = 0;
            using var output = new FileStream(Path.GetFullPath(destination), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
            transcript.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        finally { transcript.Position = position; }
    }

    /// <summary>Opens a cache-owned transcript in replay mode without deleting it on dispose.</summary>
    public static StrictReplayLedger OpenSealed(string path, int channels, long frameCount, long sampleFrames,
        string expectedTraceSha256, string expectedPcmSha256)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A cached transcript path is required.", nameof(path));
        if (frameCount <= 0 || sampleFrames < 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        ValidateHash(expectedTraceSha256, nameof(expectedTraceSha256));
        ValidateHash(expectedPcmSha256, nameof(expectedPcmSha256));
        FileStream? stream = null;
        try
        {
            stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.SequentialScan);
            if (stream.Length != checked(frameCount * RecordBytes))
                throw new ExportException("preflight_cache_invalid", "Cached strict transcript length differs from its fixed frame count.");
            var ledger = new StrictReplayLedger(stream, Path.GetFullPath(path), channels, frameCount, sampleFrames,
                expectedTraceSha256.ToLowerInvariant(), expectedPcmSha256.ToLowerInvariant());
            stream = null;
            return ledger;
        }
        finally { stream?.Dispose(); }
    }

    private StrictReplayLedger(FileStream transcript, string path, int channels, long frameCount, long sampleFrames,
        string traceSha256, string pcmSha256)
    {
        this.path = path;
        this.channels = channels;
        this.transcript = transcript;
        preparationHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        replayHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        traceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        deleteOnDispose = false;
        TotalFrames = frameCount;
        PreparationSampleFrames = sampleFrames;
        FrameTraceSha256 = traceSha256;
        PreparationPcmSha256 = pcmSha256;
        phase = Phase.Replaying;
    }

    public void PrepareFrame(int row, bool hasSelection, int defaultSelectionIndex, float[] pcm, int length)
    {
        RequirePhase(Phase.Preparing);
        ValidatePcm(pcm, 0, length);
        long samples = checked(PreparationSampleFrames + length / channels);
        PrepareStateCore(row, hasSelection, defaultSelectionIndex, samples);
        preparationHash.AppendData(MemoryMarshal.AsBytes(pcm.AsSpan(0, length)));
        PreparationSampleFrames = samples;
    }

    /// <summary>Records visual state even when its scheduled mixer PCM arrives later.</summary>
    public void PrepareState(int row, bool hasSelection, int defaultSelectionIndex)
    {
        RequirePhase(Phase.Preparing);
        PrepareStateCore(row, hasSelection, defaultSelectionIndex, PreparationSampleFrames);
    }

    private void PrepareStateCore(int row, bool hasSelection, int defaultSelectionIndex, long samples)
    {
        Span<byte> record = stackalloc byte[RecordBytes];
        WriteState(record, row, hasSelection, defaultSelectionIndex);
        BinaryPrimitives.WriteInt64LittleEndian(record[StateBytes..], samples);
        try { transcript.Write(record); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { phase = Phase.Failed; throw ExportException.FromIo(error, "Writing strict replay transcript"); }
        traceHash.AppendData(record[..StateBytes]);
        TotalFrames++;
    }

    public void AppendPreparationPcm(float[] pcm, int offset, int length)
    {
        RequirePhase(Phase.Preparing);
        ValidatePcm(pcm, offset, length);
        long samples = checked(PreparationSampleFrames + length / channels);
        preparationHash.AppendData(MemoryMarshal.AsBytes(pcm.AsSpan(offset, length)));
        PreparationSampleFrames = samples;
    }

    public void BeginReplay()
    {
        RequirePhase(Phase.Preparing);
        if (transcript.Length != checked(TotalFrames * RecordBytes))
            throw Failure("timeline_replay_mismatch", "The preparation transcript length does not match its frame count.");
        try
        {
            transcript.Flush(flushToDisk: true);
            transcript.Position = 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { phase = Phase.Failed; throw ExportException.FromIo(error, "Sealing strict replay transcript"); }
        PreparationPcmSha256 = Digest(preparationHash);
        FrameTraceSha256 = Digest(traceHash);
        phase = Phase.Replaying;
    }

    public void ValidateFrame(int row, bool hasSelection, int defaultSelectionIndex, float[] pcm, int length)
    {
        RequirePhase(Phase.Replaying);
        ValidatePcm(pcm, 0, length);
        long samples = checked(ReplaySampleFrames + length / channels);
        ValidateStateCore(row, hasSelection, defaultSelectionIndex, samples);
        replayHash.AppendData(MemoryMarshal.AsBytes(pcm.AsSpan(0, length)));
        ReplaySampleFrames = samples;
    }

    public void ValidateState(int row, bool hasSelection, int defaultSelectionIndex)
    {
        RequirePhase(Phase.Replaying);
        ValidateStateCore(row, hasSelection, defaultSelectionIndex, ReplaySampleFrames);
    }

    private void ValidateStateCore(int row, bool hasSelection, int defaultSelectionIndex, long samples)
    {
        if (ReplayFrames >= TotalFrames)
            throw Failure("timeline_replay_mismatch", $"Replay contains an extra frame: frame={ReplayFrames}, prepared_frames={TotalFrames}.");
        Span<byte> expected = stackalloc byte[RecordBytes];
        int read = 0;
        try
        {
            while (read < RecordBytes)
            {
                int count = transcript.Read(expected[read..]);
                if (count == 0) throw Failure("timeline_replay_mismatch", $"The preparation transcript ended inside frame {ReplayFrames}.");
                read += count;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { phase = Phase.Failed; throw ExportException.FromIo(error, "Reading strict replay transcript"); }
        Span<byte> actual = stackalloc byte[StateBytes];
        WriteState(actual, row, hasSelection, defaultSelectionIndex);
        if (!expected[..StateBytes].SequenceEqual(actual))
            throw Failure("timeline_replay_mismatch",
                $"Story state differs on frame {ReplayFrames}: expected_row={BinaryPrimitives.ReadInt32LittleEndian(expected)}, actual_row={row}, expected_selection={BinaryPrimitives.ReadInt32LittleEndian(expected[4..])}, actual_selection={(hasSelection ? 1 : 0)}, expected_choice={BinaryPrimitives.ReadInt32LittleEndian(expected[8..])}, actual_choice={defaultSelectionIndex}, prepared_cumulative_samples={BinaryPrimitives.ReadInt64LittleEndian(expected[StateBytes..])}, replay_cumulative_samples={samples}.");
        ReplayFrames++;
    }

    /// <summary>May receive drained PCM after the last replay state, before completion.</summary>
    public void AppendReplayPcm(float[] pcm, int offset, int length)
    {
        RequirePhase(Phase.Replaying);
        ValidatePcm(pcm, offset, length);
        long samples = checked(ReplaySampleFrames + length / channels);
        replayHash.AppendData(MemoryMarshal.AsBytes(pcm.AsSpan(offset, length)));
        ReplaySampleFrames = samples;
    }

    public void CompleteReplay()
    {
        RequirePhase(Phase.Replaying);
        if (ReplayFrames != TotalFrames || transcript.Position != transcript.Length)
            throw Failure("timeline_replay_mismatch", $"Replay ended before the fixed story trace: prepared_frames={TotalFrames}, replay_frames={ReplayFrames}.");
        ReplayPcmSha256 = Digest(replayHash);
        if (ReplaySampleFrames != PreparationSampleFrames || !string.Equals(PreparationPcmSha256, ReplayPcmSha256, StringComparison.Ordinal))
            throw Failure("audio_replay_mismatch",
                $"Raw mixer PCM differs between preparation and replay: prepared_sample_frames={PreparationSampleFrames}, replay_sample_frames={ReplaySampleFrames}, prepared_sha256={PreparationPcmSha256}, replay_sha256={ReplayPcmSha256}.");
        phase = Phase.Complete;
    }

    private void ValidatePcm(float[] pcm, int offset, int length)
    {
        if (pcm is null || offset < 0 || length < 0 || offset > pcm.Length - length
            || offset % channels != 0 || length % channels != 0)
            throw new ExportException("invalid_audio", "Replay audio must contain complete interleaved capture-channel sample frames.");
        for (int index = offset; index < offset + length; index++)
            if (!float.IsFinite(pcm[index])) throw new ExportException("invalid_audio", "Replay audio samples must be finite numbers.");
    }

    private static void WriteState(Span<byte> bytes, int row, bool hasSelection, int defaultSelectionIndex)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes, row);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], hasSelection ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], defaultSelectionIndex);
    }

    private void RequirePhase(Phase required)
    {
        if (phase == Phase.Disposed) throw new ObjectDisposedException(nameof(StrictReplayLedger));
        if (phase != required) throw new ExportException("invalid_state", $"Strict replay ledger expects {required}; actual state is {phase}.");
    }

    private ExportException Failure(string code, string message)
    {
        phase = Phase.Failed;
        return new ExportException(code, message);
    }

    private static string Digest(IncrementalHash hash) => Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

    public void Dispose()
    {
        if (phase == Phase.Disposed) return;
        phase = Phase.Disposed;
        try { transcript.Dispose(); }
        finally
        {
            preparationHash.Dispose();
            replayHash.Dispose();
            traceHash.Dispose();
            if (deleteOnDispose)
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private enum Phase { Preparing, Replaying, Complete, Failed, Disposed }

    private static void ValidateHash(string? value, string name)
    {
        if (value == null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("A canonical SHA-256 hexadecimal digest is required.", name);
    }
}
