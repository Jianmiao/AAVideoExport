using System.Security.Cryptography;
using System.Text.Json;

namespace AAVideoExport.NativePath.Tests;

internal sealed record NativePcmObservationSnapshot(string Schema, long Frames, int Fps, int SampleRate, int Channels,
    long SampleFrames, string ContentSha256, long RawSampleFrames, long TailSampleFrames);
internal sealed record NativePcmContent(string Path, NativePcmObservationSnapshot Observation);
internal sealed record NativePcmWindow(long Frame, long StartSampleFrame, long SampleFrames, double OriginalRms, double DecodedRms,
    double MaximumErrorLsb, long NonzeroOriginalSamples, long NonzeroDecodedSamples);
internal sealed record NativePcm16Comparison(string Schema, bool Passed, string OriginalPath, string DecodedPath, string OriginalSha256,
    string DecodedSha256, long Frames, long SampleFrames, int Channels, double ToleranceLsb, double MaximumErrorLsb,
    long ClippedSamples, long MismatchedSamples, long? FirstMismatchedSample, NativePcmWindow[] Windows);

// Full mixed-fixture audio observation lives in the test plugin, never in the
// ordinary export path. Copy every accepted original raw PCM block immediately.
internal sealed class NativePcmObservation
{
    private readonly int fps, sampleRate, channels;
    private readonly MemoryStream samples = new();
    private long rawFrames;
    public NativePcmObservation(int fps, int sampleRate, int channels)
    { this.fps = fps; this.sampleRate = sampleRate; this.channels = channels; }
    internal void Append(long startSample, float[] source, int count)
    {
        if (startSample != rawFrames || count < 0 || count > source.Length || count % channels != 0)
            throw new InvalidDataException("External original PCM observation contains a gap, overlap or partial channel sample.");
        int bytes = checked(count * sizeof(float));
        if (samples.Length + bytes > 256L * 1024 * 1024) throw new InvalidDataException("The short mixed-fixture PCM observer exceeded its bounded allocation.");
        var copy = new byte[bytes]; Buffer.BlockCopy(source, 0, copy, 0, bytes); samples.Write(copy, 0, copy.Length);
        rawFrames += count / channels;
    }
    internal NativePcmObservationSnapshot Complete(long frames)
    {
        if (frames <= 0 || checked(frames * sampleRate) % fps != 0) throw new InvalidDataException("External PCM comparison requires an exact fixed sample window.");
        long required = checked(frames * sampleRate / fps);
        if (rawFrames < required) throw new InvalidDataException("External native PCM does not cover all authored frame samples.");
        int bytes = checked((int)(required * channels * sizeof(float)));
        string hash = Convert.ToHexString(SHA256.HashData(samples.GetBuffer().AsSpan(0, bytes)));
        return new("aave.native-test-pcm.v1", frames, fps, sampleRate, channels, required, hash, rawFrames, rawFrames - required);
    }
    internal NativePcmContent WriteContent(long frames, string path)
    {
        var observation = Complete(frames);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        output.Write(samples.GetBuffer().AsSpan(0, checked((int)(observation.SampleFrames * channels * sizeof(float)))));
        return new(Path.GetFullPath(path), observation);
    }
    internal static NativePcm16Comparison ComparePcm16(NativePcmContent content, string decodedPath, string evidencePath)
    {
        var observed = content.Observation;
        long sampleCount = checked(observed.SampleFrames * observed.Channels);
        if (sampleCount <= 0 || sampleCount > 64L * 1024 * 1024
            || new FileInfo(content.Path).Length != sampleCount * sizeof(float)
            || new FileInfo(decodedPath).Length != sampleCount * sizeof(short))
            throw new InvalidDataException("Native/decoded PCM windows differ in exact sample count.");
        byte[] originalBytes = File.ReadAllBytes(content.Path), decodedBytes = File.ReadAllBytes(decodedPath);
        string originalHash = Convert.ToHexString(SHA256.HashData(originalBytes));
        if (originalHash != observed.ContentSha256) throw new InvalidDataException("Observed original mixed PCM changed after capture.");
        var original = new float[sampleCount]; var decoded = new short[sampleCount];
        Buffer.BlockCopy(originalBytes, 0, original, 0, originalBytes.Length); Buffer.BlockCopy(decodedBytes, 0, decoded, 0, decodedBytes.Length);
        var windows = new List<NativePcmWindow>(); long mismatches = 0, clipped = 0; long? first = null; double maximum = 0;
        const double tolerance = 1.0001; // PCM16 quantization only; no timing, gain, channel or length adjustment.
        for (long frame = 0; frame < observed.Frames; frame++)
        {
            long start = checked(frame * observed.SampleRate / observed.Fps), end = checked((frame + 1) * observed.SampleRate / observed.Fps);
            double originalEnergy = 0, decodedEnergy = 0, frameMaximum = 0; long originalNonzero = 0, decodedNonzero = 0;
            for (long index = start * observed.Channels; index < end * observed.Channels; index++)
            {
                float sample = original[index]; if (!float.IsFinite(sample)) throw new InvalidDataException("Original native mixed PCM contains a non-finite sample.");
                double bounded = Math.Clamp((double)sample, -1d, 32767d / 32768d);
                if (sample < -1d || sample > 32767d / 32768d) clipped++;
                double actual = decoded[index] / 32768d, error = Math.Abs(bounded * 32768d - decoded[index]);
                if (error > tolerance) { mismatches++; first ??= index; }
                maximum = Math.Max(maximum, error); frameMaximum = Math.Max(frameMaximum, error);
                originalEnergy += sample * (double)sample; decodedEnergy += actual * actual;
                if (sample != 0) originalNonzero++; if (decoded[index] != 0) decodedNonzero++;
            }
            long values = (end - start) * observed.Channels;
            windows.Add(new(frame, start, end - start, Math.Sqrt(originalEnergy / values), Math.Sqrt(decodedEnergy / values), frameMaximum, originalNonzero, decodedNonzero));
        }
        var result = new NativePcm16Comparison("aave.native-test-pcm16-window-comparison.v1", mismatches == 0,
            content.Path, Path.GetFullPath(decodedPath), originalHash, Convert.ToHexString(SHA256.HashData(decodedBytes)),
            observed.Frames, observed.SampleFrames, observed.Channels, tolerance, maximum, clipped, mismatches, first, windows.ToArray());
        using (var output = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(output, result, new JsonSerializerOptions { WriteIndented = true });
        if (mismatches != 0) throw new InvalidDataException($"Decoded movie PCM differs from original native mixed PCM at interleaved sample {first}; mismatches={mismatches}; max error={maximum:R} PCM16 LSB. See {evidencePath}.");
        return result;
    }
}
