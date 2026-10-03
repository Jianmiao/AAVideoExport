using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestEmptyAudioFrames()
    {
        var options = Options("empty-mixer-frames") with
        {
            Container = "mov", Codec = "qtrle", Encoder = "qtrle",
            AudioQuality = "pcm16", WriteCover = false
        };
        using var session = NewSession(options, false);
        session.WriteAudio(Array.Empty<float>(), 0);
        Equal(0L, session.AudioSampleFrames, "initial empty mixer frame cannot synthesize silence");
        for (int index = 0; index < Fps; index++)
        {
            var frame = Frame(index, false);
            session.WriteFrame(frame, frame.Length);
            int start = index * SampleRate / Fps;
            int end = (index + 1) * SampleRate / Fps;
            var samples = Audio(start, end - start);
            session.WriteAudio(samples, samples.Length);
            // An empty block following nonempty input must neither reuse stale
            // PCM nor insert a video-frame-sized silence block.
            session.WriteAudio(Array.Empty<float>(), 0);
            Equal((long)end, session.AudioSampleFrames, "empty mixer frame preserves PCM position");
        }
        var result = session.Complete();
        Equal(1d, result.DurationSeconds, "empty mixer frames cannot extend video duration");
        var decoded = await DecodeAudio(result.OutputPath);
        Equal(SampleRate * Channels, decoded.Length, "exact decoded samples without added silence");
        CheckPcm(decoded, 1.01 / 32768.0);
        using var probe = await Probe(result.OutputPath);
        CheckVideo(probe, "qtrle");
        CheckAudio(probe, "pcm_s16le", SampleRate);
    }
}
