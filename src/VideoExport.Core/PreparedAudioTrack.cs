using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AAVideoExport.Core;

/// <summary>
/// Disk-backed PCM preparation pass. The preparation pass preserves every real
/// mixer sample in arrival order; sealing fixes its exact video/sample contract.
/// A replay reads that immutable PCM on the rational video-frame grid. The
/// caller serializes append, seal, replay and disposal.
/// </summary>
public sealed class PreparedAudioTrack : IDisposable
{
    private readonly string path;
    private readonly int fps;
    private readonly int sampleRate;
    private readonly int channels;
    private readonly FileStream stream;
    private readonly bool deleteOnDispose;
    private readonly IncrementalHash? hash = ExportBuildProfile.DevelopmentAuditEnabled ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
    private bool disposed;
    private bool sealedTrack;
    private long receivedSampleFrames;
    private long totalVideoFrames = -1;
    private long nextReplayFrame;

    public PreparedAudioTrack(string path, int fps, int sampleRate, int channels)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A unique temporary PCM path is required.", nameof(path));
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is not (1 or 2 or 4 or 5 or 6 or 8)) throw new ArgumentOutOfRangeException(nameof(channels));
        this.path = Path.GetFullPath(path);
        this.fps = fps;
        this.sampleRate = sampleRate;
        this.channels = channels;
        deleteOnDispose = true;
        // Never open or delete somebody else's existing file. The parent
        // creates the private temporary directory before constructing this.
        try { stream = new FileStream(this.path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan); }
        catch { hash?.Dispose(); throw; }
    }

    public long ReceivedSampleFrames => receivedSampleFrames;
    public long TotalVideoFrames => totalVideoFrames;
    public long NextReplayFrame => nextReplayFrame;
    public long AppendedBlocks { get; private set; }
    public long EmptyBlocks { get; private set; }
    public bool IsSealed => sealedTrack;
    // No checksum is claimed in ordinary execution builds.
    public string SamplesSha256 { get; private set; } = "";
    public string StoragePath => path;

    public void CopySealedTo(string destination)
    {
        RequireOpen();
        if (!sealedTrack) throw new ExportException("invalid_state", "Prepared PCM must be sealed before it can be copied to a cache.");
        long position = stream.Position;
        try
        {
            stream.Position = 0;
            using var output = new FileStream(Path.GetFullPath(destination), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
            stream.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        finally { stream.Position = position; }
    }

    /// <summary>Opens an immutable cache-owned PCM track without taking ownership of its file.</summary>
    public static PreparedAudioTrack OpenSealed(string path, int fps, int sampleRate, int channels,
        long videoFrameCount, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A cached PCM path is required.", nameof(path));
        if (videoFrameCount <= 0) throw new ArgumentOutOfRangeException(nameof(videoFrameCount));
        if (expectedSha256 == null || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A canonical PCM SHA-256 digest is required.", nameof(expectedSha256));
        FileStream? stream = null;
        try
        {
            stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.SequentialScan);
            long expectedFrames = ExportTimeline.AudioSampleFramesForVideoFrames(videoFrameCount, fps, sampleRate);
            long expectedBytes = checked(expectedFrames * channels * sizeof(float));
            if (stream.Length != expectedBytes) throw new ExportException("preflight_cache_invalid", "Cached prepared PCM length differs from its fixed timeline.");
            using var hash = SHA256.Create();
            string actual = Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new ExportException("preflight_cache_invalid", "Cached prepared PCM hash differs from its receipt.");
            stream.Position = 0;
            return new PreparedAudioTrack(stream, Path.GetFullPath(path), fps, sampleRate, channels,
                videoFrameCount, expectedFrames, actual);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private PreparedAudioTrack(FileStream stream, string path, int fps, int sampleRate, int channels,
        long videoFrameCount, long sampleFrames, string sha256)
    {
        this.path = path;
        this.fps = fps;
        this.sampleRate = sampleRate;
        this.channels = channels;
        this.stream = stream;
        deleteOnDispose = false;
        totalVideoFrames = videoFrameCount;
        receivedSampleFrames = sampleFrames;
        SamplesSha256 = sha256;
        sealedTrack = true;
    }

    /// <summary>
    /// Append a borrowed Unity buffer synchronously. The receipt identifies its
    /// absolute sample interval; the caller can log it with the preparation
    /// frame/plot-event transcript without keeping all PCM in memory.
    /// </summary>
    public PreparedAudioBlockReceipt Append(float[] samples, int length) => Append(samples, 0, length);

    public PreparedAudioBlockReceipt Append(float[] samples, int offset, int length)
    {
        RequireOpen();
        if (sealedTrack) throw new ExportException("invalid_state", "Prepared audio is sealed and cannot receive new samples.");
        if (samples is null || offset < 0 || length < 0 || offset > samples.Length - length
            || offset % channels != 0 || length % channels != 0)
            throw new ExportException("invalid_audio", "Audio must contain complete interleaved capture-channel sample frames.");
        for (int index = offset; index < offset + length; index++)
            if (!float.IsFinite(samples[index])) throw new ExportException("invalid_audio", "Audio samples must be finite numbers.");
        long count = length / channels;
        long newTotal = checked(receivedSampleFrames + count);
        var bytes = MemoryMarshal.AsBytes(samples.AsSpan(offset, length));
        try { stream.Write(bytes); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw ExportException.FromIo(error, "Preparing offline audio"); }
        hash?.AppendData(bytes);
        var receipt = new PreparedAudioBlockReceipt(AppendedBlocks, receivedSampleFrames, count);
        receivedSampleFrames = newTotal;
        AppendedBlocks++;
        if (length == 0) EmptyBlocks++;
        return receipt;
    }

    /// <summary>
    /// Freeze the preparation pass only if every required sample is present.
    /// Neither extra samples nor missing samples are trimmed/padded.
    /// </summary>
    public void Seal(long videoFrameCount)
    {
        RequireOpen();
        if (sealedTrack) throw new ExportException("invalid_state", "Prepared audio already has a fixed frame contract.");
        long expected = ExportTimeline.AudioSampleFramesForVideoFrames(videoFrameCount, fps, sampleRate);
        if (receivedSampleFrames != expected)
            throw new ExportException(receivedSampleFrames < expected ? "audio_timeline_incomplete" : "audio_timeline_extra",
                $"Prepared audio does not match the fixed timeline: video_frames={videoFrameCount}, fps={fps}, sample_rate={sampleRate}, expected_sample_frames={expected}, received_sample_frames={receivedSampleFrames}, difference={receivedSampleFrames - expected}, blocks={AppendedBlocks}, empty_blocks={EmptyBlocks}.");
        long expectedBytes = checked(expected * channels * sizeof(float));
        if (stream.Length != expectedBytes)
            throw new ExportException("audio_timeline_incomplete", $"Prepared PCM byte length differs from its sample receipt: expected={expectedBytes}, actual={stream.Length}.");
        try
        {
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw ExportException.FromIo(error, "Sealing offline audio"); }
        totalVideoFrames = videoFrameCount;
        SamplesSha256 = hash == null ? "" : Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        sealedTrack = true;
    }

    public AudioTimelineFrame ReadFrame(long videoFrameIndex)
    {
        RequireOpen();
        if (!sealedTrack) throw new ExportException("invalid_state", "Offline audio must be sealed before video replay begins.");
        if (videoFrameIndex != nextReplayFrame || videoFrameIndex >= totalVideoFrames)
            throw new ExportException("audio_frame_order_mismatch", $"Prepared audio expects frame {nextReplayFrame} of {totalVideoFrames}; requested {videoFrameIndex}.");
        long budget = ExportTimeline.AudioSampleFramesForVideoFrame(videoFrameIndex, fps, sampleRate);
        var samples = new float[checked((int)(budget * channels))];
        var bytes = MemoryMarshal.AsBytes(samples.AsSpan());
        int read = 0;
        try
        {
            while (read < bytes.Length)
            {
                int count = stream.Read(bytes[read..]);
                if (count == 0) throw new ExportException("audio_timeline_incomplete", $"Prepared audio ended inside video frame {videoFrameIndex}.");
                read += count;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw ExportException.FromIo(error, "Reading prepared offline audio"); }
        nextReplayFrame++;
        return new AudioTimelineFrame(videoFrameIndex, samples, budget);
    }

    public void CompleteReplay()
    {
        RequireOpen();
        if (!sealedTrack || nextReplayFrame != totalVideoFrames || stream.Position != stream.Length)
            throw new ExportException("audio_timeline_incomplete",
                $"Prepared audio replay is incomplete: frame={nextReplayFrame}, required={totalVideoFrames}, byte_offset={stream.Position}, bytes={stream.Length}.");
    }

    private void RequireOpen()
    {
        if (disposed) throw new ObjectDisposedException(nameof(PreparedAudioTrack));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { stream.Dispose(); }
        finally
        {
            hash?.Dispose();
            // The file is private and created by this instance with CreateNew.
            // Keep cancellation cleanup best-effort without masking the export
            // error that led here (e.g. storage device unavailable).
            if (deleteOnDispose)
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

public readonly record struct PreparedAudioBlockReceipt(long BlockIndex, long FirstSampleFrame, long SampleFrames);
