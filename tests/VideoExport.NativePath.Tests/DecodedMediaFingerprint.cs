using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AAVideoExport.NativePath.Tests;

internal sealed record DecodedMediaLayout(int Width, int Height, int Fps, int SampleRate, int Channels,
    long Frames, long AudioSampleFrames, string VideoPixelFormat);

internal sealed record DecodedMediaFingerprintResult(DecodedMediaLayout Layout, long VideoFrameBytes, string VideoSha256,
    string VideoTimelineSha256, string AudioPcm16Sha256, long AudioPcm16Bytes,
    string FramesPath, string FrameHashPath, string AudioEvidencePath, string SummaryPath)
{
    public string AudioPcm16Path { get; init; } = "";
}

// Test-only, pure managed acceptance evidence. No Unity access or production
// export policy changes. Hash decoded pixels/samples, never container bytes.
internal static class DecodedMediaFingerprint
{
    internal static async Task<DecodedMediaFingerprintResult> CaptureAsync(string ffmpeg, string media,
        DecodedMediaLayout layout, string evidencePrefix, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (layout.Width <= 0 || layout.Height <= 0 || layout.Fps <= 0 || layout.SampleRate <= 0 || layout.Channels <= 0
            || layout.Frames <= 0 || layout.AudioSampleFrames <= 0 || string.IsNullOrWhiteSpace(layout.VideoPixelFormat) || timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(layout), "Decoded media needs explicit positive dimensions and counts.");
        long audioBytes = checked(layout.AudioSampleFrames * layout.Channels * 2);
        // Keep both decoders on one immutable file image on Windows. The
        // input is never rewritten or used as the content-comparison hash.
        await using var sourceLock = new FileStream(media, FileMode.Open, FileAccess.Read, FileShare.Read);
        string prefix = Path.GetFullPath(evidencePrefix);
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        string rawPath = prefix + ".framehash.txt", framesPath = prefix + ".frames.tsv";
        string rawPartial = rawPath + ".partial", framesPartial = framesPath + ".partial";
        string pcmPath = prefix + ".audio.s16le", pcmPartial = pcmPath + ".partial";

        var video = await RunAsync(ffmpeg, new[] { "-v", "error", "-nostdin", "-threads", "1", "-copyts", "-i", media,
            "-map", "0:v:0", "-an", "-sn", "-dn", "-vsync", "0", "-c:v", "rawvideo", "-pix_fmt", layout.VideoPixelFormat,
            "-threads", "1", "-f", "framehash", "-hash", "sha256", "pipe:1" }, async stream =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 16384, leaveOpen: true);
            await using var raw = Writer(rawPartial);
            await using var manifest = Writer(framesPartial);
            await manifest.WriteLineAsync("index\tpts_num\tpts_den\tduration_num\tduration_den\tbytes\tsha256").ConfigureAwait(false);
            using var pixels = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var timestamps = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long count = 0, tbNum = 0, tbDen = 0, frameBytes = 0;
            bool sawDimensions = false, sawHash = false;
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                await raw.WriteLineAsync(line).ConfigureAwait(false);
                if (line.StartsWith("#hash:", StringComparison.Ordinal))
                    sawHash = string.Equals(line[(line.IndexOf(':') + 1)..].Trim(), "SHA256", StringComparison.OrdinalIgnoreCase);
                else if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
                {
                    if (tbNum != 0) throw new InvalidDataException("Duplicate decoded video time base.");
                    (tbNum, tbDen) = ParseRational(line[(line.IndexOf(':') + 1)..].Trim());
                }
                else if (line.StartsWith("#dimensions 0:", StringComparison.Ordinal))
                {
                    string dimensions = line[(line.IndexOf(':') + 1)..].Trim();
                    if (dimensions != layout.Width.ToString(CultureInfo.InvariantCulture) + "x" + layout.Height.ToString(CultureInfo.InvariantCulture))
                        throw new InvalidDataException("Decoded video dimensions changed: " + dimensions);
                    sawDimensions = true;
                }
                if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
                if (!sawHash || !sawDimensions || tbNum <= 0 || tbDen <= 0)
                    throw new InvalidDataException("Decoded framehash headers are incomplete.");
                string[] parts = line.Split(',').Select(part => part.Trim()).ToArray();
                if (parts.Length != 6 || Integer(parts[0]) != 0)
                    throw new InvalidDataException("Unexpected decoded framehash record: " + line);
                _ = Integer(parts[1]); // DTS is retained in the raw evidence; decoded presentation order is checked below.
                long pts = Integer(parts[2]), duration = Integer(parts[3]), bytes = Integer(parts[4]);
                string hash = parts[5].ToUpperInvariant();
                if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Malformed decoded video SHA-256.");
                if (count >= layout.Frames || bytes <= 0 || frameBytes != 0 && bytes != frameBytes)
                    throw new InvalidDataException("Decoded video record count or native-format frame byte count changed.");
                frameBytes = bytes;
                if ((BigInteger)pts * tbNum * layout.Fps != (BigInteger)count * tbDen
                    || duration <= 0 || (BigInteger)duration * tbNum * layout.Fps != tbDen)
                    throw new InvalidDataException("Decoded video PTS/duration does not match the verified frame grid at frame " + count + ".");
                var position = Reduce((BigInteger)pts * tbNum, tbDen);
                var length = Reduce((BigInteger)duration * tbNum, tbDen);
                string timing = FormattableString.Invariant($"{count}\t{position.Numerator}\t{position.Denominator}\t{length.Numerator}\t{length.Denominator}");
                await manifest.WriteLineAsync(timing + "\t" + bytes.ToString(CultureInfo.InvariantCulture) + "\t" + hash).ConfigureAwait(false);
                timestamps.AppendData(Encoding.UTF8.GetBytes(timing + "\n"));
                pixels.AppendData(Convert.FromHexString(hash));
                count++;
            }
            if (count != layout.Frames) throw new InvalidDataException("Decoded video record count mismatch: expected " + layout.Frames + ", got " + count + ".");
            return (Pixels: Convert.ToHexString(pixels.GetHashAndReset()), Timeline: Convert.ToHexString(timestamps.GetHashAndReset()), Bytes: frameBytes);
        }, timeout, cancellationToken).ConfigureAwait(false);
        File.Move(rawPartial, rawPath); File.Move(framesPartial, framesPath);

        var audio = await RunAsync(ffmpeg, new[] { "-v", "error", "-nostdin", "-threads", "1", "-i", media,
            "-map", "0:a:0", "-vn", "-sn", "-dn", "-c:a", "pcm_s16le", "-ar", layout.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", layout.Channels.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "pipe:1" }, async stream =>
        {
            using var pcm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var retainedPcm = new FileStream(pcmPartial, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var buffer = new byte[65536]; long count = 0; int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                count = checked(count + read);
                if (count > audioBytes) throw new InvalidDataException("Decoded PCM16 contains extra audio samples.");
                pcm.AppendData(buffer, 0, read);
                await retainedPcm.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (count != audioBytes) throw new InvalidDataException("Decoded PCM16 byte/sample count mismatch: expected " + audioBytes + ", got " + count + ".");
            return (Hash: Convert.ToHexString(pcm.GetHashAndReset()), Bytes: count);
        }, timeout, cancellationToken).ConfigureAwait(false);
        File.Move(pcmPartial, pcmPath);
        string audioPath = prefix + ".pcm16.json", summaryPath = prefix + ".fingerprint.json";
        await WriteNewJsonAsync(audioPath, new { Format = "s16le", layout.SampleRate, layout.Channels,
            layout.AudioSampleFrames, Bytes = audio.Bytes, Sha256 = audio.Hash }).ConfigureAwait(false);
        var result = new DecodedMediaFingerprintResult(layout, video.Bytes, video.Pixels, video.Timeline, audio.Hash, audio.Bytes,
            framesPath, rawPath, audioPath, summaryPath) { AudioPcm16Path = pcmPath };
        await WriteNewJsonAsync(summaryPath, result).ConfigureAwait(false);
        return result;
    }

    internal static void RequireEquivalent(DecodedMediaFingerprintResult first, DecodedMediaFingerprintResult second)
    {
        RequireSameTimingAndAudio(first, second);
        if (!string.Equals(first.VideoSha256, second.VideoSha256, StringComparison.Ordinal))
        {
            long? frame = FirstDifferentVideoFrame(first.FramesPath, second.FramesPath);
            throw new InvalidDataException("Repeated decoded video pixels differ" +
                (frame.HasValue ? " at zero-based frame " + frame.Value.ToString(CultureInfo.InvariantCulture) : " (see frame manifests)") + ".");
        }
    }

    internal static void RequireSameTimingAndAudio(DecodedMediaFingerprintResult first, DecodedMediaFingerprintResult second)
    {
        if (first.Layout != second.Layout || first.VideoFrameBytes != second.VideoFrameBytes || first.AudioPcm16Bytes != second.AudioPcm16Bytes)
            throw new InvalidDataException("Repeated decoded media changed dimensions, rates, channels or frame/sample counts.");
        if (!string.Equals(first.VideoTimelineSha256, second.VideoTimelineSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Repeated decoded video PTS/duration content differs.");
        if (!string.Equals(first.AudioPcm16Sha256, second.AudioPcm16Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Repeated decoded PCM16 content differs despite matching sample count and duration.");
    }

    internal static void RequireOriginalPixelsToDecodedConsistency(bool rawExact, bool decodedPixelsExact)
    {
        if (rawExact && !decodedPixelsExact)
            throw new InvalidDataException("Original RGBA pixels match exactly but decoded pixels differ; a font-atlas exception cannot authorize encoder divergence.");
    }

    private static long? FirstDifferentVideoFrame(string first, string second)
    {
        using var left = File.ReadLines(first).Skip(1).GetEnumerator();
        using var right = File.ReadLines(second).Skip(1).GetEnumerator();
        long index = 0;
        while (left.MoveNext())
        {
            if (!right.MoveNext() || !string.Equals(left.Current.Split('\t').Last(), right.Current.Split('\t').Last(), StringComparison.Ordinal))
                return index;
            index++;
        }
        return right.MoveNext() ? index : null;
    }

    private static StreamWriter Writer(string path) => new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    private static async Task WriteNewJsonAsync<T>(string path, T value)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(output, value, new JsonSerializerOptions { WriteIndented = true }).ConfigureAwait(false);
    }
    private static long Integer(string value) => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static (long Numerator, long Denominator) ParseRational(string value)
    {
        string[] parts = value.Split('/');
        if (parts.Length != 2 || Integer(parts[0]) <= 0 || Integer(parts[1]) <= 0) throw new InvalidDataException("Invalid decoded time base: " + value);
        return (Integer(parts[0]), Integer(parts[1]));
    }
    private static (BigInteger Numerator, BigInteger Denominator) Reduce(BigInteger numerator, BigInteger denominator)
    {
        BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        return (numerator / divisor, denominator / divisor);
    }
    private static async Task<T> RunAsync<T>(string executable, IEnumerable<string> arguments, Func<Stream, Task<T>> consume,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start independent media decoder.");
        void Stop() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
        async Task<T> ReadOutput()
        {
            try { return await consume(process.StandardOutput.BaseStream).ConfigureAwait(false); }
            catch { Stop(); throw; }
        }
        var output = ReadOutput(); var errors = ReadErrorTailAsync(process.StandardError);
        try
        {
            await Task.WhenAll(output, errors, process.WaitForExitAsync()).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(errors.Result))
                throw new InvalidDataException("Independent decoder failed: exit=" + process.ExitCode + "; " + errors.Result);
            return await output.ConfigureAwait(false);
        }
        catch
        {
            Stop();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            throw;
        }
    }
    private static async Task<string> ReadErrorTailAsync(StreamReader reader)
    {
        var buffer = new char[4096]; var tail = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 32768) tail.Remove(0, tail.Length - 32768);
        }
        return tail.ToString();
    }
}
