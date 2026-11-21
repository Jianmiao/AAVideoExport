using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static Task TestExportTimeline()
    {
        Equal(1837L, ExportTimeline.AudioSampleFramesForVideoFrame(0, 24, 44100), "first 44.1 kHz / 24 fps frame budget");
        Equal(1838L, ExportTimeline.AudioSampleFramesForVideoFrame(1, 24, 44100), "second 44.1 kHz / 24 fps frame budget");
        Equal(44100L, ExportTimeline.AudioSampleFramesForVideoFrames(24, 24, 44100), "one second at 44.1 kHz / 24 fps");
        Equal(48000L, ExportTimeline.AudioSampleFramesForVideoFrames(30, 30, 48000), "one second at 48 kHz / 30 fps");
        ThrowsArgument(() => ExportTimeline.AudioSampleFramesForVideoFrame(-1, 24, 48000), "negative video frame index");
        ThrowsArgument(() => ExportTimeline.AudioSampleFramesForVideoFrames(1, 0, 48000), "invalid video frame rate");
        return Task.CompletedTask;
    }

    private static async Task TestAudioFrameBudgets()
    {
        var options = Options("audio-frame-budget") with { WriteCover = false, EncodingMode = "software" };
        using var session = NewSession(options);
        session.WriteAudioFrame(0, Array.Empty<float>(), 0);
        for (int frameIndex = 0; frameIndex < Fps; frameIndex++)
        {
            var pixels = Frame(frameIndex, true);
            session.WriteFrame(pixels, pixels.Length);
            int start = frameIndex * SampleRate / Fps;
            int end = (frameIndex + 1) * SampleRate / Fps;
            var audio = Audio(start, end - start, SampleRate);
            session.WriteAudioFrame(frameIndex, audio, audio.Length);
        }
        var result = session.Complete();
        Equal((long)Fps, result.Frames, "strict audio frame budget preserves video frame count");
        Equal((long)SampleRate, session.AudioSampleFrames, "strict audio frame budget preserves the complete sample timeline");
        await Task.CompletedTask;

        var invalidOptions = Options("audio-frame-budget-gap") with { WriteCover = false, EncodingMode = "software" };
        using var invalid = NewSession(invalidOptions);
        var first = Frame(0, true);
        invalid.WriteFrame(first, first.Length);
        var firstAudio = Audio(0, SampleRate / Fps, SampleRate);
        invalid.WriteAudioFrame(0, firstAudio, firstAudio.Length);
        invalid.WriteFrame(first, first.Length);
        ThrowsCode("audio_frame_timing_mismatch", () => invalid.WriteAudioFrame(1, Array.Empty<float>(), 0), "mid-stream empty audio block");
    }

    private static void ThrowsArgument(Action action, string name)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Expected ArgumentOutOfRangeException: " + name);
    }
}
