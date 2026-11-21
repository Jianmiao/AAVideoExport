using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace AAVideoExport.Core;

/// <summary>Source PTS and presentation interval in the stream's exact time base.
/// Duration is the interval until the next PTS; the last frame uses its reported duration.</summary>
public readonly record struct ControlledVideoFrameInfo(int Index, long PresentationTimestamp, long Duration, long ByteOffset);

/// <summary>
/// Predecodes the first video stream into unscaled RGBA frames without frame-rate
/// conversion. The first source PTS is playback time zero. Looping wraps at the
/// last source frame's reported end; non-looping playback holds its last frame.
/// The caller owns the published cache directory. Dispose closes its read handle
/// but retains the immutable files for the export's audit/cleanup policy.
/// This is a media backend, not a sandbox for the decoder or arbitrary native Mods.
/// </summary>
public sealed class ControlledVideoFrameStore : IDisposable
{
    private readonly FileStream data;
    private readonly string sourcePath;
    private readonly object gate = new();
    private bool disposed;

    public int Width { get; }
    public int Height { get; }
    public int FrameCount => Frames.Count;
    public int FrameByteCount { get; }
    public string SourceSha256 { get; }
    public string DataSha256 { get; }
    public string DirectoryPath { get; }
    public long TimeBaseNumerator { get; }
    public long TimeBaseDenominator { get; }
    public long DurationTicks { get; }
    public IReadOnlyList<ControlledVideoFrameInfo> Frames { get; }

    private ControlledVideoFrameStore(string sourcePath, string directory, IndexDocument index)
    {
        this.sourcePath = sourcePath;
        DirectoryPath = directory; SourceSha256 = index.SourceSha256; DataSha256 = index.DataSha256;
        Width = index.Width; Height = index.Height; FrameByteCount = checked(Width * Height * 4);
        TimeBaseNumerator = index.TimeBaseNumerator; TimeBaseDenominator = index.TimeBaseDenominator;
        DurationTicks = index.DurationTicks;
        Frames = new ReadOnlyCollection<ControlledVideoFrameInfo>(index.Frames.ToArray());
        data = new FileStream(Path.Combine(directory, "frames.rgba"), FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.RandomAccess);
        if (data.Length != checked((long)FrameByteCount * Frames.Count))
        {
            data.Dispose();
            throw Invalid("Decoded frame store length changed before opening.");
        }
    }

    /// <summary>
    /// Does not publish a store until both metadata and all decoded bytes agree.
    /// maxDecodedBytes is checked against the complete frame index before decode.
    /// Source writes/deletion are locked on Windows while probing and decoding;
    /// the source hash is rechecked before publication on all platforms.
    /// </summary>
    public static async Task<ControlledVideoFrameStore> PrepareAsync(string sourcePath, string ffmpegPath, string ffprobePath,
        string cacheRoot, CancellationToken cancellationToken = default, long maxDecodedBytes = 2L * 1024 * 1024 * 1024,
        TimeSpan? processTimeout = null)
    {
        RequirePath(sourcePath, nameof(sourcePath)); RequirePath(ffmpegPath, nameof(ffmpegPath));
        RequirePath(ffprobePath, nameof(ffprobePath)); RequirePath(cacheRoot, nameof(cacheRoot));
        if (maxDecodedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxDecodedBytes));
        TimeSpan timeout = processTimeout ?? TimeSpan.FromMinutes(5);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(processTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        sourcePath = Path.GetFullPath(sourcePath);
        cacheRoot = Path.GetFullPath(cacheRoot);
        Directory.CreateDirectory(cacheRoot);
        string id = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(cacheRoot, ".building-" + id);
        string published = Path.Combine(cacheRoot, "video-" + id);
        bool complete = false, moved = false;
        Directory.CreateDirectory(stage);
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            string sourceHash = await HashAsync(source, cancellationToken).ConfigureAwait(false);
            var probe = await FfmpegProcess.RunAsync(ffprobePath,
                new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,time_base",
                    "-of", "json", sourcePath }, timeout, cancellationToken).ConfigureAwait(false);
            probe.EnsureSuccess("Inspect controlled background video");
            var (width, height, tbNumerator, tbDenominator) = ParseStream(probe.Output);
            int frameBytes = checked(width * height * 4);
            var frames = new List<ControlledVideoFrameInfo>();
            var frameProbe = await FfmpegProcess.RunLinesAsync(ffprobePath,
                new[] { "-v", "error", "-select_streams", "v:0", "-show_frames", "-show_entries",
                    "frame=pts,duration,pkt_duration,width,height", "-of", "compact=p=0:nk=0", sourcePath },
                line =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) return;
                    var values = Fields(line);
                    // ffprobe may emit a separate side-data line without frame
                    // dimensions. Such a line contains no timing/frame record.
                    if (!values.ContainsKey("width") && !values.ContainsKey("height") && !values.ContainsKey("pts")) return;
                    if (Long(values, "width") != width || Long(values, "height") != height)
                        throw Invalid("Source changes frame dimensions during decode.");
                    long pts = Long(values, "pts");
                    if (frames.Count > 0 && pts <= frames[^1].PresentationTimestamp)
                        throw Invalid("Source frame PTS must be present and strictly increasing.");
                    if (frames.Count >= 1_000_000 || checked((frames.Count + 1L) * frameBytes) > maxDecodedBytes)
                        throw new ExportException("video_store_limit", "Decoded source video exceeds the frame/byte budget.");
                    long duration = OptionalLong(values, "duration") ?? OptionalLong(values, "pkt_duration") ?? 0;
                    if (duration < 0) throw Invalid("Source frame duration is negative.");
                    frames.Add(new ControlledVideoFrameInfo(frames.Count, pts, duration, checked((long)frames.Count * frameBytes)));
                }, timeout, cancellationToken).ConfigureAwait(false);
            frameProbe.EnsureSuccess("Index controlled background video frames");
            if (frames.Count == 0) throw Invalid("Video has no decodable timestamped frames.");
            if (frames[^1].Duration <= 0)
                throw Invalid("Last source frame has no positive duration; loop endpoint cannot be inferred.");
            for (int i = 0; i < frames.Count - 1; i++)
                frames[i] = frames[i] with { Duration = checked(frames[i + 1].PresentationTimestamp - frames[i].PresentationTimestamp) };
            long durationTicks = checked(frames[^1].PresentationTimestamp - frames[0].PresentationTimestamp + frames[^1].Duration);
            if (durationTicks <= 0) throw Invalid("Source playback duration is not positive.");
            long expectedBytes = checked((long)frameBytes * frames.Count);
            string rawPath = Path.Combine(stage, "frames.rgba");
            var decode = await FfmpegProcess.RunAsync(ffmpegPath,
                new[] { "-v", "error", "-xerror", "-nostdin", "-n", "-noautorotate", "-i", sourcePath,
                    "-map", "0:v:0", "-an", "-sn", "-dn", "-vsync", "0", "-pix_fmt", "rgba",
                    "-fs", maxDecodedBytes.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", rawPath },
                timeout, cancellationToken).ConfigureAwait(false);
            decode.EnsureSuccess("Decode controlled background video");
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(rawPath) || new FileInfo(rawPath).Length != expectedBytes)
                throw Invalid("Decoded RGBA bytes do not match the exact source frame index.");
            string dataHash;
            await using (var raw = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                dataHash = await HashAsync(raw, cancellationToken).ConfigureAwait(false);
            source.Position = 0;
            if (await HashAsync(source, cancellationToken).ConfigureAwait(false) != sourceHash)
                throw new ExportException("video_source_changed", "Background source changed while its frame store was built.");
            var index = new IndexDocument(1, sourcePath, sourceHash, dataHash, width, height,
                tbNumerator, tbDenominator, durationTicks, frames.ToArray());
            await File.WriteAllTextAsync(Path.Combine(stage, "index.json"),
                JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stage, published);
            moved = true;
            cancellationToken.ThrowIfCancellationRequested();
            var result = new ControlledVideoFrameStore(sourcePath, published, index);
            complete = true;
            return result;
        }
        finally
        {
            if (!complete)
            {
                // Only these newly-created, GUID-scoped directories are owned
                // by this build. Never delete the cache root or source path.
                string owned = moved ? published : stage;
                if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
            }
        }
    }

    /// <summary>Integer/rational selection; no floating point clock is consulted.</summary>
    public ControlledVideoFrameInfo SelectFrame(long outputFrame, int outputFps, bool loop)
    {
        if (outputFrame < 0) throw new ArgumentOutOfRangeException(nameof(outputFrame));
        if (outputFps <= 0) throw new ArgumentOutOfRangeException(nameof(outputFps));
        lock (gate)
        {
            ThrowIfDisposed();
            BigInteger time = (BigInteger)outputFrame * TimeBaseDenominator;
            BigInteger denominator = (BigInteger)outputFps * TimeBaseNumerator;
            BigInteger end = DurationTicks * denominator;
            if (loop) time %= end;
            else if (time >= end) return Frames[^1];
            int lo = 0, hi = Frames.Count - 1;
            long origin = Frames[0].PresentationTimestamp;
            while (lo < hi)
            {
                int middle = lo + (hi - lo + 1) / 2;
                BigInteger start = ((BigInteger)Frames[middle].PresentationTimestamp - origin) * denominator;
                if (start <= time) lo = middle; else hi = middle - 1;
            }
            return Frames[lo];
        }
    }

    /// <summary>Copy one selected frame into a reusable exact-sized RGBA buffer.
    /// Unity texture upload remains the main-thread caller's responsibility.</summary>
    public ControlledVideoFrameInfo CopyFrameForOutput(long outputFrame, int outputFps, bool loop, byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length != FrameByteCount) throw new ArgumentException("RGBA buffer must hold exactly one frame.", nameof(rgba));
        lock (gate)
        {
            var frame = SelectFrame(outputFrame, outputFps, loop);
            data.Position = frame.ByteOffset;
            int at = 0;
            while (at < rgba.Length)
            {
                int read = data.Read(rgba, at, rgba.Length - at);
                if (read == 0) throw Invalid("Frame store ended within a decoded source frame.");
                at += read;
            }
            return frame;
        }
    }

    public async Task ValidateSourceIdentityAsync(CancellationToken cancellationToken = default)
    {
        lock (gate) ThrowIfDisposed();
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (await HashAsync(source, cancellationToken).ConfigureAwait(false) != SourceSha256)
            throw new ExportException("video_source_changed", "Background source no longer matches its decoded frame store.");
    }

    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; data.Dispose(); } }
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(ControlledVideoFrameStore)); }
    private static async Task<string> HashAsync(Stream stream, CancellationToken token)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, token).ConfigureAwait(false));
    }
    private static (int Width, int Height, long Numerator, long Denominator) ParseStream(string json)
    {
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() != 1) throw Invalid("Expected one selected video stream.");
        var stream = streams[0];
        int width = stream.GetProperty("width").GetInt32(), height = stream.GetProperty("height").GetInt32();
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
            throw Invalid("Source dimensions are invalid or exceed the controlled decode limit.");
        var parts = (stream.GetProperty("time_base").GetString() ?? "").Split('/');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long numerator) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long denominator) || numerator <= 0 || denominator <= 0)
            throw Invalid("Source time base must be an exact positive rational.");
        return (width, height, numerator, denominator);
    }
    private static Dictionary<string, string> Fields(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in line.Split('|'))
        {
            int equals = part.IndexOf('=');
            if (equals < 1) continue;
            string key = part[..equals];
            if (key is "pts" or "width" or "height" or "duration" or "pkt_duration")
                if (!result.TryAdd(key, part[(equals + 1)..])) throw Invalid("Duplicate source frame metadata field.");
        }
        return result;
    }
    private static long Long(IReadOnlyDictionary<string, string> values, string name) =>
        OptionalLong(values, name) ?? throw Invalid("Source frame lacks an exact integer " + name + ".");
    private static long? OptionalLong(IReadOnlyDictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out string? value) || value == "N/A") return null;
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
            throw Invalid("Invalid source frame " + name + ".");
        return number;
    }
    private static void RequirePath(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An explicit path is required.", name);
    }
    private static ExportException Invalid(string message) => new("video_frame_store_invalid", message);
    private sealed record IndexDocument(int Schema, string SourcePath, string SourceSha256, string DataSha256,
        int Width, int Height, long TimeBaseNumerator, long TimeBaseDenominator, long DurationTicks,
        ControlledVideoFrameInfo[] Frames);
}
