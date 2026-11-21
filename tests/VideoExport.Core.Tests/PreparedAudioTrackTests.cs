using AAVideoExport.Core;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static Task TestPreparedAudioTrack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aave-prepared-audio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "audio.f32le");
        try
        {
            const int frames = 7, fps = 24, rate = 44100, channels = 2;
            long total = ExportTimeline.AudioSampleFramesForVideoFrames(frames, fps, rate);
            Equal(12862L, total, "prepared track fixed rational endpoint");
            var expectedBudgets = new[] { 1837L, 1838L, 1837L, 1838L, 1837L, 1838L, 1837L };
            var source = Enumerable.Range(0, (int)total * channels).Select(value => value / 10000f).ToArray();
            using (var track = new PreparedAudioTrack(path, fps, rate, channels))
            {
                Equal(new PreparedAudioBlockReceipt(0, 0, 0), track.Append(Array.Empty<float>(), 0), "empty block receipt");
                int sourceOffset = 0;
                foreach (int blockSamples in new[] { 2, 194, 1234, 4000 })
                {
                    var block = source.AsSpan(sourceOffset, blockSamples).ToArray();
                    var receipt = track.Append(block, block.Length);
                    Equal((long)sourceOffset / channels, receipt.FirstSampleFrame, "receipt has absolute sample start");
                    sourceOffset += blockSamples;
                    Array.Fill(block, 999f); // Unity's next RenderAudio can reuse its buffer.
                }
                var tail = source.AsSpan(sourceOffset).ToArray();
                track.Append(tail, tail.Length);
                track.Seal(frames);
                Equal(total, track.ReceivedSampleFrames, "sealed exact source sample count");
                if (ExportBuildProfile.DevelopmentAuditEnabled)
                    Equal(Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(source.AsSpan()))).ToLowerInvariant(), track.SamplesSha256, "development PCM receipt digest");
                else
                    Equal("", track.SamplesSha256, "production retains PCM without calculating an audit digest");
                var replay = new List<float>();
                for (int index = 0; index < frames; index++)
                {
                    var frame = track.ReadFrame(index);
                    Equal(expectedBudgets[index], frame.SampleFrames, "replay uses exact rational budget");
                    replay.AddRange(frame.Samples);
                }
                track.CompleteReplay();
                Check(source.SequenceEqual(replay), "disk-backed replay preserves every original float sample in order");
                ThrowsCode("invalid_state", () => track.Append(Array.Empty<float>(), 0), "sealed audio cannot mutate");
                ThrowsCode("audio_frame_order_mismatch", () => track.ReadFrame(frames), "replay cannot duplicate tail frame");
            }
            Check(!File.Exists(path), "disposing prepared audio removes owned spool");

            using (var missing = new PreparedAudioTrack(path, 60, 48000, 2))
            {
                missing.Append(new float[1598], 1598);
                ThrowsCode("audio_timeline_incomplete", () => missing.Seal(1), "one missing sample cannot be padded");
                ThrowsCode("invalid_state", () => missing.ReadFrame(0), "unsealed incomplete audio cannot be replayed");
                missing.Append(new float[2], 2);
                missing.Seal(1);
                ThrowsCode("audio_frame_order_mismatch", () => missing.ReadFrame(1), "out of order replay rejected");
                ThrowsCode("audio_timeline_incomplete", missing.CompleteReplay, "unconsumed replay rejected");
                missing.ReadFrame(0);
                missing.CompleteReplay();
            }
            using (var extra = new PreparedAudioTrack(path, 60, 48000, 2))
            {
                extra.Append(new float[1602], 1602);
                ThrowsCode("audio_timeline_extra", () => extra.Seal(1), "one extra sample cannot be trimmed");
            }
            File.WriteAllText(path, "existing data");
            try { using var collision = new PreparedAudioTrack(path, 60, 48000, 2); throw new Exception("existing path must be rejected"); }
            catch (IOException) { }
            Equal("existing data", File.ReadAllText(path), "existing files are never overwritten or deleted");
            File.Delete(path);
            return Task.CompletedTask;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
