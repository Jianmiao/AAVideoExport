using System.Runtime.InteropServices;

namespace AAVideoExport.Core;

/// <summary>
/// Preserves a contiguous native PCM pass until the final video length is known.
/// Raw sample zero corresponds to RawDspEpochSeconds. Declared scheduling lead
/// and drained tail are accounted for separately from the exact content window.
/// The caller serializes all operations and consumes borrowed slices synchronously.
/// </summary>
public sealed class RawAudioPass : IDisposable
{
    private const int ChunkSampleFrames = 8192;
    private readonly string path;
    private readonly FileStream stream;
    private Phase phase;
    private RawAudioPassReceipt? receipt;

    public RawAudioPass(string path, int sampleRate, int channels, double rawDspEpoch, long leadSamples)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A unique raw PCM path is required.", nameof(path));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is not (1 or 2 or 4 or 5 or 6 or 8)) throw new ArgumentOutOfRangeException(nameof(channels));
        if (!double.IsFinite(rawDspEpoch) || rawDspEpoch < 0) throw new ArgumentOutOfRangeException(nameof(rawDspEpoch));
        if (leadSamples < 0) throw new ArgumentOutOfRangeException(nameof(leadSamples));
        this.path = Path.GetFullPath(path);
        SampleRate = sampleRate;
        Channels = channels;
        RawDspEpochSeconds = rawDspEpoch;
        LeadSampleFrames = leadSamples;
        stream = new FileStream(this.path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.SequentialScan);
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public double RawDspEpochSeconds { get; }
    public long LeadSampleFrames { get; }
    public long RawSampleFrames { get; private set; }
    public long AppendedBlocks { get; private set; }
    public bool IsSealed => phase == Phase.Sealed;
    public RawAudioPassReceipt? Receipt => receipt;

    public void Append(long rawStartSample, float[] samples, int length)
    {
        RequirePhase(Phase.Capturing);
        if (samples is null || length < 0 || length > samples.Length || length % Channels != 0)
            throw new ExportException("invalid_audio", "Raw audio must contain complete interleaved capture-channel sample frames.");
        if (rawStartSample < 0) throw new ArgumentOutOfRangeException(nameof(rawStartSample));
        if (rawStartSample != RawSampleFrames)
            throw new ExportException(rawStartSample < RawSampleFrames ? "audio_raw_overlap" : "audio_raw_gap",
                $"Raw PCM must start at sample {RawSampleFrames}; received {rawStartSample}.");
        long end;
        try { end = checked(rawStartSample + length / Channels); }
        catch (OverflowException error) { throw new ExportException("audio_raw_overflow", "Raw PCM exceeds the supported sample clock.", error); }
        for (int index = 0; index < length; index++)
            if (!float.IsFinite(samples[index])) throw new ExportException("invalid_audio", "Raw audio samples must be finite numbers.");
        // No receipt changes until validation and the complete disk write succeed.
        try { stream.Write(MemoryMarshal.AsBytes(samples.AsSpan(0, length))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { phase = Phase.Failed; throw ExportException.FromIo(error, "Spooling native raw audio"); }
        RawSampleFrames = end;
        AppendedBlocks++;
    }

    public long RequiredRawSampleFrames(long frameCount, int fps) =>
        checked(LeadSampleFrames + ExportTimeline.AudioSampleFramesForVideoFrames(frameCount, fps, SampleRate));

    public bool CanSeal(long frameCount, int fps)
    {
        RequirePhase(Phase.Capturing);
        return RawSampleFrames > 0 && RawSampleFrames >= RequiredRawSampleFrames(frameCount, fps);
    }

    /// <summary>Seal once, after draining real mixer output through the content end.</summary>
    public RawAudioPassReceipt Seal(long frameCount, int fps)
    {
        RequirePhase(Phase.Capturing);
        long required = RequiredRawSampleFrames(frameCount, fps);
        if (RawSampleFrames == 0)
            throw new ExportException("audio_raw_bounds_missing", "A raw PCM pass must acknowledge a non-empty native interval.");
        if (RawSampleFrames < required)
            throw new ExportException("audio_timeline_incomplete",
                $"Raw PCM does not reach the fixed content end: received={RawSampleFrames}, required={required}, lead={LeadSampleFrames}.");
        try
        {
            if (stream.Length != checked(RawSampleFrames * Channels * sizeof(float)))
                throw new ExportException("audio_timeline_incomplete", "Raw PCM byte length differs from its sample receipt.");
            stream.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { phase = Phase.Failed; throw ExportException.FromIo(error, "Sealing native raw audio"); }
        catch { phase = Phase.Failed; throw; }
        receipt = new RawAudioPassReceipt(RawDspEpochSeconds, RawSampleFrames, LeadSampleFrames,
            required - LeadSampleFrames, RawSampleFrames - required, frameCount, fps, SampleRate, Channels);
        phase = Phase.Sealed;
        return receipt;
    }

    /// <summary>
    /// May be called repeatedly after sealing. Slices borrow one fixed-size read
    /// buffer; a callback must consume them before returning. Any failed copy
    /// invalidates the pass, since a destination may already have consumed PCM.
    /// </summary>
    public RawAudioPassReceipt CopyContentTo(Action<ArraySegment<float>> consume)
    {
        RequirePhase(Phase.Sealed);
        if (consume is null) throw new ArgumentNullException(nameof(consume));
        phase = Phase.Reading;
        // The mixer can begin a pass with a variable number of digital-silence
        // blocks while the scene settles. Align every pass to its first actual
        // content frame and keep the declared length by padding the tail with
        // silence. This removes capture-start jitter without changing any
        // audible sample.
        long firstContent = FindFirstContentFrame();
        var window = new TimestampedAudioWindow(0, firstContent, receipt!.ContentSampleFrames, Channels);
        var buffer = new float[ChunkSampleFrames * Channels];
        try
        {
            stream.Position = 0;
            while (window.RawSamplesAcknowledged < RawSampleFrames)
            {
                int frames = (int)Math.Min(ChunkSampleFrames, RawSampleFrames - window.RawSamplesAcknowledged);
                int floats = frames * Channels;
                Span<byte> bytes = MemoryMarshal.AsBytes(buffer.AsSpan(0, floats));
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = stream.Read(bytes[read..]);
                    if (count == 0) throw new ExportException("audio_timeline_incomplete", "Raw PCM ended inside an acknowledged sample interval.");
                    read += count;
                }
                var block = window.Append(buffer, 0, floats, window.NextRawSample);
                if (block.ContentSlice is { } content) consume(content.Samples);
            }
            long contentEnd = checked(window.ContentStartSample + window.ContentSampleFrames);
            while (window.NextRawSample < contentEnd)
            {
                long missing = contentEnd - window.NextRawSample;
                int frames = (int)Math.Min(ChunkSampleFrames, missing);
                int floats = checked(frames * Channels);
                Array.Clear(buffer, 0, floats);
                var block = window.Append(buffer, 0, floats, window.NextRawSample);
                if (block.ContentSlice is { } content) consume(content.Samples);
            }
            window.Complete();
            if (stream.Position != stream.Length)
                throw new ExportException("audio_timeline_extra", "Raw PCM has bytes outside its acknowledged sample interval.");
            phase = Phase.Sealed;
            return receipt;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { phase = Phase.Failed; throw ExportException.FromIo(error, "Reading native raw audio"); }
        catch { phase = Phase.Failed; throw; }
    }

    private long FindFirstContentFrame()
    {
        var buffer = new float[ChunkSampleFrames * Channels];
        stream.Position = 0;
        long frame = 0;
        while (frame < RawSampleFrames)
        {
            int frames = (int)Math.Min(ChunkSampleFrames, RawSampleFrames - frame);
            int floats = frames * Channels;
            Span<byte> bytes = MemoryMarshal.AsBytes(buffer.AsSpan(0, floats));
            int read = 0;
            while (read < bytes.Length)
            {
                int count = stream.Read(bytes[read..]);
                if (count == 0) throw new ExportException("audio_timeline_incomplete", "Raw PCM ended while locating the first content frame.");
                read += count;
            }
            for (int index = 0; index < floats; index++)
            {
                if (Math.Abs(buffer[index]) <= 1e-7f) continue;
                return checked(frame + index / Channels);
            }
            frame += frames;
        }
        return 0;
    }

    private void RequirePhase(Phase required)
    {
        if (phase == Phase.Disposed) throw new ObjectDisposedException(nameof(RawAudioPass));
        if (phase != required) throw new ExportException("invalid_state", $"Raw audio pass expects {required}; actual state is {phase}.");
    }

    public void Dispose()
    {
        if (phase == Phase.Disposed) return;
        phase = Phase.Disposed;
        try { stream.Dispose(); }
        finally
        {
            // This instance owns only the file it successfully created with CreateNew.
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private enum Phase { Capturing, Sealed, Reading, Failed, Disposed }
}

public sealed record RawAudioPassReceipt(double RawDspEpochSeconds, long RawSampleFrames,
    long LeadSampleFrames, long ContentSampleFrames, long TailSampleFrames,
    long VideoFrameCount, int Fps, int SampleRate, int Channels);
