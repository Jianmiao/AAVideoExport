using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace AAVideoExport.Core;

internal static class MediaVerifier
{
    // Ordinary completion reads the container's declared stream metadata. It
    // does not walk packets, decode content, hash output or issue audit receipts.
    internal static async Task CheckCompletionAsync(string ffprobePath, string path, ExportOptions options, long frameCount, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Require(frameCount > 0 && File.Exists(path) && new FileInfo(path).Length > 0, "The encoded output is empty.");
        var result = await FfmpegProcess.RunAsync(ffprobePath, new[] { "-v", "error", "-show_entries",
            "stream=codec_type,codec_name,width,height,avg_frame_rate,time_base,nb_frames,duration_ts,start_pts,sample_rate,channels:format=duration", "-of", "json", path }, timeout, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Output completion check");
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var videos = streams.Where(stream => Text(stream, "codec_type") == "video").ToArray();
            Require(videos.Length == 1, "Expected one completed video stream.");
            var video = videos[0];
            Require(Text(video, "codec_name") == options.Codec, "The output video codec differs from the selected codec.");
            Require(Integer(video, "width") == options.Width && Integer(video, "height") == options.Height, "The output dimensions differ from the requested canvas.");
            Require(Math.Abs(Rate(Text(video, "avg_frame_rate")) - options.Fps) < 0.001, "The output frame rate is incorrect.");
            string declaredFrames = Text(video, "nb_frames");
            if (long.TryParse(declaredFrames, NumberStyles.None, CultureInfo.InvariantCulture, out long count))
                Require(count == frameCount, "The container frame count differs from submitted video frames.");
            if (options.StrictSynchronization)
            {
                Require(Text(video, "time_base") == "1/" + options.Fps && Integer(video, "start_pts") == 0
                    && Integer(video, "duration_ts") == frameCount, "The completed video track does not match the execution frame plan.");
            }
            double duration = Number(document.RootElement.GetProperty("format"), "duration");
            Require(double.IsFinite(duration) && Math.Abs(duration - frameCount / (double)options.Fps) <= Math.Max(2d / options.Fps, 0.05), "The output duration differs from the captured timeline.");
            var audio = streams.Where(stream => Text(stream, "codec_type") == "audio").ToArray();
            if (options.AudioQuality == "none") Require(audio.Length == 0, "Unexpected output audio stream.");
            else
            {
                Require(audio.Length == 1, "Expected one completed audio stream.");
                string codec = options.AudioQuality.StartsWith("aac", StringComparison.Ordinal) ? "aac" : options.AudioQuality == "pcm16" ? "pcm_s16le" : "pcm_s24le";
                Require(Text(audio[0], "codec_name") == codec && Integer(audio[0], "sample_rate") == options.AudioSampleRate && Integer(audio[0], "channels") == 2, "The output audio format differs from the selected format.");
                if (options.StrictSynchronization)
                    Require(Text(audio[0], "time_base") == "1/" + options.AudioSampleRate && Integer(audio[0], "start_pts") == 0
                        && Integer(audio[0], "duration_ts") == ExportTimeline.AudioSampleFramesForVideoFrames(frameCount, options.Fps, options.AudioSampleRate), "The completed audio track does not match the execution sample plan.");
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new ExportException("verification_failed", "The encoder returned incomplete output metadata.", error); }
    }

    internal static async Task VerifyAsync(string ffprobePath, string path, ExportOptions options, long frameCount, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await FfmpegProcess.RunAsync(ffprobePath, new[] { "-v", "error", "-show_entries",
            "stream=codec_type,codec_name,width,height,avg_frame_rate,r_frame_rate,time_base,nb_frames,duration_ts,duration,start_pts,start_time,sample_rate,channels:format=duration", "-of", "json", path }, timeout, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Final media verification");
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var videos = streams.Where(s => Text(s, "codec_type") == "video").ToArray();
            Require(videos.Length == 1, "Expected exactly one video stream.");
            var video = videos[0];
            Require(Text(video, "codec_name") == options.Codec, "Video codec differs from the requested codec.");
            Require(video.GetProperty("width").GetInt32() == options.Width && video.GetProperty("height").GetInt32() == options.Height, "Video dimensions differ from the captured frames.");
            // MOV/MP4 contain a sample count. Matroska requires a packet scan, which avoids decoding
            // the entire movie. Each video sample/packet from the supported encoders is one frame;
            // the integration harness separately decodes every frame to check this contract.
            if (!long.TryParse(Text(video, "nb_frames"), out long actualFrames) || actualFrames <= 0)
            {
                var packets = await FfmpegProcess.RunAsync(ffprobePath, new[] { "-v", "error", "-select_streams", "v:0", "-count_packets",
                    "-show_entries", "stream=nb_read_packets", "-of", "json", path }, timeout, cancellationToken).ConfigureAwait(false);
                packets.EnsureSuccess("Video packet verification");
                using var packetDocument = JsonDocument.Parse(packets.Output);
                var packetStream = packetDocument.RootElement.GetProperty("streams")[0];
                Require(long.TryParse(Text(packetStream, "nb_read_packets"), out actualFrames), "The container has no verifiable video packet count.");
            }
            Require(actualFrames == frameCount, "Container video frame count differs from the number captured.");
            Require(Math.Abs(Rate(Text(video, "avg_frame_rate")) - options.Fps) < 0.001, "Video frame rate is incorrect.");
            string expectedTimeBase = options.Container is "mp4" or "mov" ? "1/" + options.Fps : "1/1000";
            Require(Text(video, "time_base") == expectedTimeBase, "Container video time base is not the selected export time base.");
            if (options.StrictSynchronization)
            {
                Require(Integer(video, "start_pts") == 0, "Strict synchronization requires video to start at timestamp zero.");
                Require(options.Container is "mp4" or "mov",
                    "Strict synchronization requires a container with an exact video frame time base and sample-accurate audio track timing.");
                Require(Integer(video, "duration_ts") == frameCount,
                    "Strict video duration differs from the planned frame count.");
            }
            await VerifyVideoPtsAsync(ffprobePath, path, video, options, frameCount, timeout, cancellationToken).ConfigureAwait(false);
            double expectedDuration = (double)frameCount / options.Fps;
            double duration = Number(document.RootElement.GetProperty("format"), "duration");
            double durationTolerance = options.StrictSynchronization
                ? (options.AudioQuality == "none" ? 0.000001 : 1d / options.AudioSampleRate)
                : Math.Max(2d / options.Fps, 0.05);
            if (!options.StrictSynchronization && options.AudioQuality != "none")
                durationTolerance = Math.Max(durationTolerance, options.AudioTimelineToleranceSeconds);
            Require(double.IsFinite(duration) && Math.Abs(duration - expectedDuration) <= durationTolerance, "Container duration does not match the captured timeline.");
            var audioStreams = streams.Where(s => Text(s, "codec_type") == "audio").ToArray();
            if (options.AudioQuality == "none") Require(audioStreams.Length == 0, "Unexpected audio stream.");
            else
            {
                Require(audioStreams.Length == 1, "Expected one audio stream.");
                var audio = audioStreams[0];
                string codec = options.AudioQuality.StartsWith("aac", StringComparison.Ordinal) ? "aac" : options.AudioQuality == "pcm16" ? "pcm_s16le" : "pcm_s24le";
                Require(Text(audio, "codec_name") == codec, "Audio codec is incorrect.");
                Require(Text(audio, "sample_rate") == options.AudioSampleRate.ToString(CultureInfo.InvariantCulture) && audio.GetProperty("channels").GetInt32() == 2, "Audio must have the requested sample rate and two channels.");
                if (options.StrictSynchronization)
                {
                    await VerifyStrictAudioAsync(ffprobePath, path, audio, options, frameCount, timeout, cancellationToken).ConfigureAwait(false);
                    return;
                }
                double audioStart = Number(audio, "start_time");
                double videoStart = Number(video, "start_time");
                double offsetTolerance = codec == "aac" ? 1024d / options.AudioSampleRate + 0.002 : 0.002;
                Require(double.IsFinite(audioStart) && double.IsFinite(videoStart) && Math.Abs(audioStart - videoStart) <= offsetTolerance,
                    "Audio and video start times differ beyond codec priming tolerance.");
                double audioDuration = Number(audio, "duration");
                if (double.IsFinite(audioDuration)) Require(Math.Abs(audioDuration - expectedDuration) <= durationTolerance, "Audio duration does not match the captured timeline.");
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new ExportException("verification_failed", "Unable to verify the encoded media: " + ex.Message, ex); }
    }

    private static async Task VerifyStrictAudioAsync(string ffprobePath, string path, JsonElement audio,
        ExportOptions options, long frameCount, TimeSpan timeout, CancellationToken cancellationToken)
    {
        long expectedSamples = ExportTimeline.AudioSampleFramesForVideoFrames(frameCount, options.Fps, options.AudioSampleRate);
        Rational audioTimeBase = ParseRational(Text(audio, "time_base"));
        Require(audioTimeBase.Numerator == 1 && audioTimeBase.Denominator == options.AudioSampleRate,
            "The container does not expose a sample-accurate audio time base required by strict synchronization.");
        Require(Integer(audio, "start_pts") == 0, "Strict synchronization requires audio to start at sample zero after codec priming.");
        Require(Integer(audio, "duration_ts") == expectedSamples,
            $"Strict audio duration differs from the planned sample count {expectedSamples}; container edits must preserve every intended sample.");

        // AAC decoder frames include codec padding. The effective timeline is
        // instead determined by packet duration plus explicit skip/discard
        // metadata and the container's exact track edit duration. Checking all
        // three avoids both accepting missing samples and rejecting valid AAC
        // just because its last decoder frame contains padded samples.
        var sequence = new AudioPtsSequenceVerifier(expectedSamples);
        var result = await FfmpegProcess.RunLinesAsync(ffprobePath, new[] { "-v", "error", "-select_streams", "a:0",
            "-show_entries", "packet=pts,duration:packet_side_data=side_data_type,skip_samples,discard_padding",
            "-of", "compact=p=0:nk=0", path }, sequence.Accept, timeout, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Strict audio timestamp verification");
        sequence.Complete();
    }

    private static async Task VerifyVideoPtsAsync(string ffprobePath, string path, JsonElement video, ExportOptions options,
        long frameCount, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Do not request JSON here.  A long export can contain hundreds of
        // thousands of timestamps, while FfmpegProcess intentionally bounds
        // diagnostic stdout.  The compact one-value-per-line form lets us
        // validate the sequence as it streams without retaining the whole
        // listing or silently accepting a truncated tail.
        var timeBase = ParseRational(Text(video, "time_base"));
        var sequence = new VideoPtsSequenceVerifier(frameCount, options.Fps, timeBase);
        var result = await FfmpegProcess.RunLinesAsync(ffprobePath, new[] { "-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=pts", "-of", "compact=p=0:nk=1", path },
            sequence.Accept, timeout, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Video timestamp verification");
        sequence.Complete();
    }

    private static Rational ParseRational(string value)
    {
        string[] parts = value.Split('/');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long numerator) ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long denominator) ||
            numerator <= 0 || denominator <= 0)
            throw new ExportException("verification_failed", "Video stream has no valid timestamp time base.");
        return new Rational(numerator, denominator);
    }

    private static string Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.ToString() : "";
    private static long Integer(JsonElement element, string property)
    {
        Require(long.TryParse(Text(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value),
            "The container has no verifiable integer " + property + ".");
        return value;
    }
    private static double Number(JsonElement element, string property) => double.TryParse(Text(element, property), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;
    private static double Rate(string value)
    {
        string[] parts = value.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d != 0 ? n / d : double.NaN;
    }
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new ExportException("verification_failed", message);
    }
}

internal readonly record struct Rational(long Numerator, long Denominator);

/// <summary>Checks sample-exact audio coverage, accounting for explicit codec priming and tail discard.</summary>
internal sealed class AudioPtsSequenceVerifier
{
    private readonly long expectedSamples;
    private long end;
    private long packets;
    private string? failure;

    internal AudioPtsSequenceVerifier(long expectedSamples)
    {
        if (expectedSamples <= 0) throw new ArgumentOutOfRangeException(nameof(expectedSamples));
        this.expectedSamples = expectedSamples;
    }

    internal void Accept(string line)
    {
        if (failure is not null || string.IsNullOrWhiteSpace(line)) return;
        try
        {
            long? pts = null, duration = null;
            long skip = 0, discard = 0;
            bool sawSkip = false, sawDiscard = false;
            foreach (string field in line.Split('|'))
            {
                int equals = field.IndexOf('=');
                if (equals <= 0) throw new FormatException("Missing field name.");
                string key = field.Substring(0, equals);
                int colon = key.LastIndexOf(':');
                if (colon >= 0) key = key.Substring(colon + 1);
                if (key is not ("pts" or "duration" or "skip_samples" or "discard_padding")) continue;
                if (!long.TryParse(field.Substring(equals + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                    throw new FormatException("Non-integer " + key + ".");
                switch (key)
                {
                    case "pts": if (pts is not null) throw new FormatException("Duplicate PTS."); pts = value; break;
                    case "duration": if (duration is not null) throw new FormatException("Duplicate duration."); duration = value; break;
                    case "skip_samples": if (sawSkip) throw new FormatException("Duplicate skip."); skip = value; sawSkip = true; break;
                    case "discard_padding": if (sawDiscard) throw new FormatException("Duplicate discard."); discard = value; sawDiscard = true; break;
                }
            }
            if (pts is null || duration is null || duration <= 0 || skip < 0 || discard < 0)
                throw new FormatException("Missing or invalid audio packet timing.");
            checked
            {
                if (skip + discard > duration.Value) throw new FormatException("Codec skip/discard exceeds the packet duration.");
                long start = pts.Value + skip;
                long nextEnd = pts.Value + duration.Value - discard;
                if (start != end) throw new FormatException($"Audio packets contain a gap or overlap at sample {end}; next start is {start}.");
                if (packets > 0 && skip != 0) throw new FormatException("Codec priming appeared after the first audio packet.");
                if (nextEnd > expectedSamples) throw new FormatException("Audio packets extend beyond the planned sample boundary.");
                end = nextEnd;
                packets++;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // Keep draining the process; report the first mismatch at Complete.
            failure = "Strict audio timestamp verification failed: " + ex.Message;
        }
    }

    internal void Complete()
    {
        if (failure is not null) throw new ExportException("verification_failed", failure);
        if (packets == 0 || end != expectedSamples)
            throw new ExportException("verification_failed", $"Effective audio packet sample count {end} differs from the planned sample count {expectedSamples}.");
    }
}

/// <summary>
/// Strictly validates every packet's position on the presentation-time grid.
/// B-frame packets legitimately arrive out of display order, so requiring
/// monotonic packet PTS would reject valid H.264/H.265 exports. A one-bit-per-
/// frame presence map proves each strictly increasing display-grid position
/// occurs exactly once, without decoding the movie or retaining its listing.
/// </summary>
internal sealed class VideoPtsSequenceVerifier
{
    private readonly long expectedFrames;
    private readonly long fps;
    private readonly Rational timeBase;
    private readonly BitArray present;
    private long seen;
    private string? failure;

    internal VideoPtsSequenceVerifier(long expectedFrames, long fps, Rational timeBase)
    {
        if (expectedFrames < 0 || expectedFrames > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(expectedFrames));
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        if (timeBase.Numerator <= 0 || timeBase.Denominator <= 0) throw new ArgumentOutOfRangeException(nameof(timeBase));
        this.expectedFrames = expectedFrames;
        this.fps = fps;
        this.timeBase = timeBase;
        present = new BitArray((int)expectedFrames);
    }

    internal long Count => seen;

    internal void Accept(string line)
    {
        if (failure is not null) return;
        string value = line.Trim();
        if (value.Length == 0) return;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pts))
        {
            failure = "Video timestamp scan returned a non-integer PTS.";
            return;
        }
        if (seen >= expectedFrames)
        {
            failure = "Video timestamp scan returned more frames than were captured.";
            return;
        }
        if (pts < 0)
        {
            failure = "Video presentation timestamps must start at zero, without negative presentation times.";
            return;
        }
        long index;
        try
        {
            // Recover the display index from the exact rational time. The
            // configured stream grids have a resolution finer than one frame.
            checked { index = (pts * timeBase.Numerator * fps + timeBase.Denominator / 2) / timeBase.Denominator; }
        }
        catch (OverflowException)
        {
            failure = "Video packet timestamp exceeds the verifiable timeline range.";
            return;
        }
        if (index >= expectedFrames)
        {
            failure = "Video packet timestamp lies outside the captured timeline.";
            return;
        }
        long expected = ExpectedPts(index);
        if (pts != expected)
        {
            failure = $"Video frame {index} PTS {pts} does not match the expected grid value {expected}.";
            return;
        }
        if (present[(int)index])
        {
            failure = $"Video presentation timestamp for frame {index} occurs more than once.";
            return;
        }
        present[(int)index] = true;
        seen++;
    }

    internal void Complete()
    {
        if (failure is not null) throw new ExportException("verification_failed", failure);
        if (seen != expectedFrames)
            throw new ExportException("verification_failed", $"Video timestamp count {seen} differs from the captured frame count {expectedFrames}.");
    }

    private long ExpectedPts(long index)
    {
        // Round the exact frame time to the stream's integer time base. This
        // matches Matroska's millisecond grid (e.g. 24fps => 0,42,83,125)
        // while retaining exact integer values for an MP4 1/fps time base.
        checked
        {
            long numerator = index * timeBase.Denominator;
            long denominator = fps * timeBase.Numerator;
            return (numerator + denominator / 2) / denominator;
        }
    }
}
