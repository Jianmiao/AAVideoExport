using System.Globalization;
using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.NativePath.Tests;

internal sealed record NativeBenchmarkMediaEvidence(string Scope, string VideoCodec, string? AudioCodec, int Width, int Height,
    int Fps, long Frames, int SampleRate, int Channels, long ExpectedAudioSampleFrames, long DecodedAudioSampleFrames,
    long AudioPaddingSampleFrames, string VideoDuration, string? AudioDuration);

// Independent format-aware benchmark inspection. AAC decoding is not a
// lossless PCM oracle and never enters the strict mixed-PCM acceptance path.
internal static class NativeBenchmarkMedia
{
    internal static NativeBenchmarkMediaEvidence Inspect(JsonElement root, ExportOptions options, long expectedFrames)
    {
        if (expectedFrames <= 0 || options.Container is not ("mp4" or "mov"))
            throw new InvalidDataException("Whole-export inspection needs a positive MP4/MOV frame count.");
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(stream => Text(stream, "codec_type") == "video");
        if (Text(video, "codec_name") != options.Codec || Number(video, "width") != options.Width || Number(video, "height") != options.Height)
            throw new InvalidDataException("Benchmark movie video codec or dimensions differ from requested settings.");
        var videoTime = Rational(Text(video, "time_base"));
        var audios = streams.Where(stream => Text(stream, "codec_type") == "audio").ToArray();
        bool hasAudio = options.AudioQuality != "none";
        if (audios.Length != (hasAudio ? 1 : 0)) throw new InvalidDataException("Benchmark movie audio stream count differs from requested settings.");
        string? audioCodec = null; int channels = 0; (long N, long D) audioTime = (1, 1);
        if (hasAudio)
        {
            var audio = audios[0]; audioCodec = Text(audio, "codec_name");
            string expected = options.AudioQuality.StartsWith("aac", StringComparison.Ordinal) ? "aac" : options.AudioQuality == "pcm24" ? "pcm_s24le" : "pcm_s16le";
            channels = checked((int)Number(audio, "channels")); audioTime = Rational(Text(audio, "time_base"));
            if (audioCodec != expected || Number(audio, "sample_rate") != options.AudioSampleRate || channels is < 1 or > 8 || Number(audio, "start_pts") != 0)
                throw new InvalidDataException("Benchmark audio codec, sample rate, channels or start PTS differ from requested settings.");
        }
        long frames = 0, samples = 0;
        foreach (var frame in root.GetProperty("frames").EnumerateArray())
        {
            if (Text(frame, "media_type") == "video")
            {
                if (checked(Number(frame, "pts") * videoTime.N * options.Fps) != checked(frames * videoTime.D))
                    throw new InvalidDataException("Benchmark video PTS is not the requested exact frame grid.");
                frames++;
            }
            else if (Text(frame, "media_type") == "audio")
            {
                if (!hasAudio || checked(Number(frame, "pts") * audioTime.N * options.AudioSampleRate) != checked(samples * audioTime.D))
                    throw new InvalidDataException("Benchmark decoded audio PTS has a gap, overlap or nonzero start.");
                long count = Number(frame, "nb_samples"); if (count <= 0) throw new InvalidDataException("Decoded audio frame is empty.");
                samples = checked(samples + count);
            }
        }
        long expectedSamples = checked(expectedFrames * options.AudioSampleRate / options.Fps);
        long padding = hasAudio ? samples - expectedSamples : 0;
        if (frames != expectedFrames || Number(video, "start_pts") != 0
            || checked(Number(video, "duration_ts") * videoTime.N * options.Fps) != checked(expectedFrames * videoTime.D))
            throw new InvalidDataException("Benchmark frame count or video track duration differs from the completed export.");
        if (hasAudio)
        {
            // AAC can retain one partial codec block after the authored window;
            // keep its exact count in evidence instead of claiming PCM equality.
            if (padding < 0 || (audioCodec == "aac" ? padding >= 1024 : padding != 0))
                throw new InvalidDataException("Benchmark decoded audio length exceeds the declared codec padding allowance.");
            long durationError = Math.Abs(checked(Number(audios[0], "duration_ts") * audioTime.N * options.AudioSampleRate - expectedSamples * audioTime.D));
            long allowance = audioCodec == "aac" ? 1024 : 1;
            if (durationError > checked(allowance * audioTime.D)) throw new InvalidDataException("Benchmark audio stream duration differs from the authored video window.");
        }
        return new("Whole-export timing and independently decoded format/count/PTS inspection only; no lossy-content equivalence claim.",
            options.Codec, audioCodec, options.Width, options.Height, options.Fps, frames, options.AudioSampleRate, channels,
            hasAudio ? expectedSamples : 0, samples, padding, frames + "/" + options.Fps,
            hasAudio ? Number(audios[0], "duration_ts") + "*" + audioTime.N + "/" + audioTime.D : null);
    }
    private static string Text(JsonElement item, string name) => item.GetProperty(name).ToString();
    private static long Number(JsonElement item, string name) => long.Parse(Text(item, name), CultureInfo.InvariantCulture);
    private static (long N, long D) Rational(string value)
    {
        string[] parts = value.Split('/');
        if (parts.Length != 2 || !long.TryParse(parts[0], out long n) || !long.TryParse(parts[1], out long d) || n <= 0 || d <= 0)
            throw new InvalidDataException("Benchmark stream has an invalid time base.");
        return (n, d);
    }
}
