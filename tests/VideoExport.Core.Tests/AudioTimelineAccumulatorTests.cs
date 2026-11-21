using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static Task TestAudioTimelineAccumulator()
    {
        const int fps = 24;
        const int rate = 44100;
        const int channels = 2;
        const long totalFrames = 7;
        long expected = ExportTimeline.AudioSampleFramesForVideoFrames(totalFrames, fps, rate);
        Equal(12862L, expected, "seven video frames at 44.1 kHz / 24 fps have a fixed 12862-sample endpoint");
        var expectedBudgets = new[] { 1837L, 1838L, 1837L, 1838L, 1837L, 1838L, 1837L };
        var source = Enumerable.Range(0, checked((int)(expected * channels)))
            .Select(value => value / 1000f).ToArray();

        var split = new AudioTimelineAccumulator(totalFrames, fps, rate, channels);
        var output = new List<AudioTimelineFrame>();
        int offset = 0;
        foreach (int blockFrames in new[] { 1, 317, 1837, 19, 4000, 2 })
        {
            int blockSamples = Math.Min(blockFrames * channels, source.Length - offset);
            output.AddRange(split.Append(source, offset, blockSamples));
            offset += blockSamples;
            if (offset == source.Length) break;
        }
        if (offset != source.Length) output.AddRange(split.Append(source, offset, source.Length - offset));
        split.Complete();
        Equal(totalFrames, output.Count, "split blocks emit every video frame");
        Equal(expected, split.ReceivedSampleFrames, "split blocks receive the exact total");
        Equal(expected, split.EmittedSampleFrames, "split blocks emit the exact total");
        for (int frame = 0, sourceOffset = 0; frame < output.Count; frame++)
        {
            long budget = expectedBudgets[frame];
            Equal((long)frame, output[frame].VideoFrameIndex, "split frame index");
            Equal(budget, output[frame].SampleFrames, "split frame budget");
            Equal(checked((int)(budget * channels)), output[frame].Samples.Length, "split frame interleaved length");
            for (int i = 0; i < output[frame].Samples.Length; i++)
                if (output[frame].Samples[i] != source[sourceOffset + i])
                    throw new InvalidOperationException($"split frame {frame} sample {i} was reordered");
            sourceOffset += output[frame].Samples.Length;
        }

        var merged = new AudioTimelineAccumulator(totalFrames, fps, rate, channels);
        var mergedFrames = merged.Append(source, source.Length).ToArray();
        merged.Complete();
        Equal(totalFrames, mergedFrames.Length, "one merged block emits every video frame");
        var mergedSamples = mergedFrames.SelectMany(frame => frame.Samples).ToArray();
        Check(source.SequenceEqual(mergedSamples), "merged blocks preserve exact sample values and order");

        var missing = new AudioTimelineAccumulator(totalFrames, fps, rate, channels);
        missing.Append(source, source.Length - channels);
        ThrowsCode("audio_timeline_incomplete", missing.Complete, "missing sample frame is rejected");

        var extra = new AudioTimelineAccumulator(totalFrames, fps, rate, channels);
        extra.Append(source, source.Length);
        var oneExtra = new float[channels];
        ThrowsCode("audio_timeline_extra", () => extra.Append(oneExtra, oneExtra.Length), "extra sample frame is rejected");

        var zeroBlocks = new AudioTimelineAccumulator(2, 60, 48000, channels);
        Equal(0, zeroBlocks.Append(Array.Empty<float>(), 0).Count, "initial empty block adds no invented audio");
        var partial = new float[100 * channels];
        Equal(0, zeroBlocks.Append(partial, partial.Length).Count, "partial block must wait for the rest of the frame");
        Equal(0, zeroBlocks.Append(Array.Empty<float>(), 0).Count, "mid-frame empty block is not an invented gap");
        var remainder = new float[1500 * channels];
        Equal(2, zeroBlocks.Append(remainder, remainder.Length).Count, "merged remainder completes both frames");
        zeroBlocks.Complete();

        var ownsSource = new AudioTimelineAccumulator(1, 60, 48000, channels);
        var start = new float[] { 0.125f, 0.25f };
        ownsSource.Append(start, start.Length);
        start[0] = 99;
        var end = new float[799 * channels];
        var ownedFrame = ownsSource.Append(end, end.Length).Single();
        Equal(0.125f, ownedFrame.Samples[0], "pending samples do not alias reusable Unity render buffer");
        ownsSource.Complete();

        return Task.CompletedTask;
    }

    private static Task TestAudioTimelineAccumulatorValidation()
    {
        var timeline = new AudioTimelineAccumulator(1, 60, 48000, 2);
        ThrowsCode("invalid_audio", () => timeline.Append(new float[3], 3), "partial channel frame");
        ThrowsCode("invalid_audio", () => timeline.Append(new float[4], 1, 2), "unaligned channel offset");
        ThrowsCode("invalid_audio", () => timeline.Append(new float[] { 1, float.NaN }, 2), "non-finite sample");
        ThrowsCode("invalid_audio", () => timeline.Append(new float[] { 1, float.PositiveInfinity }, 2), "infinite sample");
        Equal(0L, timeline.ReceivedSampleFrames, "invalid block leaves received count untouched");
        Equal(0L, timeline.NextVideoFrame, "invalid block leaves output frame untouched");
        ThrowsCode("audio_timeline_extra", () => timeline.Append(new float[1602], 1602), "oversized first block");
        Equal(0L, timeline.ReceivedSampleFrames, "oversized block leaves received count untouched");
        timeline.Append(new float[1600], 1600);
        timeline.Complete();

        // An early Complete is a validation failure, not a destructive flush:
        // the caller can still supply the actual delayed data, never silence.
        var delayed = new AudioTimelineAccumulator(1, 60, 48000, 2);
        delayed.Append(new float[400], 400);
        ThrowsCode("audio_timeline_incomplete", delayed.Complete, "missing data is rejected before a partial frame is emitted");
        Equal(0L, delayed.NextVideoFrame, "incomplete data has emitted no partial frame");
        delayed.Append(new float[1200], 1200);
        delayed.Complete();

        var empty = new AudioTimelineAccumulator(0, 60, 48000, 2);
        empty.Complete();
        Check(empty.IsComplete, "empty fixed timeline is complete without audio");
        ThrowsCode("audio_timeline_extra", () => empty.Append(new float[2], 2), "empty timeline rejects any audio");
        return Task.CompletedTask;
    }
}
