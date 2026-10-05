using System.Globalization;
using System.Text.Json;

namespace AAVideoExport.Core;

internal static class MediaVerifier
{
    internal static async Task VerifyAsync(string ffprobePath, string path, ExportOptions options, long frameCount, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await FfmpegProcess.RunAsync(ffprobePath, new[] { "-v", "error", "-show_entries",
            "stream=codec_type,codec_name,width,height,avg_frame_rate,r_frame_rate,nb_frames,duration,start_time,sample_rate,channels:format=duration", "-of", "json", path }, timeout, cancellationToken).ConfigureAwait(false);
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
            double expectedDuration = (double)frameCount / options.Fps;
            double duration = Number(document.RootElement.GetProperty("format"), "duration");
            double durationTolerance = Math.Max(2d / options.Fps, 0.05);
            if (options.AudioQuality != "none")
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

    private static string Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.ToString() : "";
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
