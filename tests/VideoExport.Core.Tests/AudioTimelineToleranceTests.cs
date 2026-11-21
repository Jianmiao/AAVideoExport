using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestAudioTimelineTolerances()
    {
        foreach (var (fps, allowedFrames) in new[] { (24, 2), (25, 2), (30, 3), (50, 4), (60, 5) })
        foreach (int difference in new[] { -allowedFrames, allowedFrames, -allowedFrames - 1, allowedFrames + 1 })
        {
            bool accepted = Math.Abs(difference) <= allowedFrames;
            string name = $"audio-tail-{fps}fps-{difference}";
            await Test($"{fps} fps audio tail of {difference} frames is {(accepted ? "muxed" : "rejected")}", () =>
            {
                var options = Options(name) with { Fps = fps, EncodingMode = "software", WriteCover = false };
                using var session = new ExportSession(options, ffmpeg, ffprobe, "libx264");
                var pixels = Frame(0, true);
                for (int frame = 0; frame < fps; frame++) session.WriteFrame(pixels, pixels.Length);
                int sampleFrames = (int)Math.Round(SampleRate * (1 + difference / (double)fps));
                var audio = new float[sampleFrames * Channels];
                session.WriteAudio(audio, audio.Length);
                if (accepted)
                {
                    var result = session.Complete();
                    Equal((long)fps, result.Frames, "audio tail does not alter video frame count");
                    Check(Math.Abs(result.DurationSeconds - 1) < 1e-9, "audio tail does not stretch video time");
                }
                else
                    ThrowsCode("audio_timeline_mismatch", () => session.Complete(), name);
                return Task.CompletedTask;
            });
        }
        await Test("missing audio is rejected despite the frame-rate tolerance", () =>
        {
            var options = Options("audio-tail-missing") with { Fps = 30, EncodingMode = "software", WriteCover = false };
            using var session = new ExportSession(options, ffmpeg, ffprobe, "libx264");
            var pixels = Frame(0, true);
            for (int frame = 0; frame < options.Fps; frame++) session.WriteFrame(pixels, pixels.Length);
            ThrowsCode("audio_timeline_mismatch", () => session.Complete(), "missing audio");
            return Task.CompletedTask;
        });
    }
}
