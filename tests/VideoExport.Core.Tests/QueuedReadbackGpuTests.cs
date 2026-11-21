using System.Security.Cryptography;
using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;
internal static partial class Program
{
    // Called by --d3d-media. Missing Windows/GPU/encoders must fail, not skip.
    // Portable boundary tests cannot substitute for these byte/order checks.
    private static async Task TestQueuedReadbackGpu()
    {
        foreach (string encoder in new[] { "h264_nvenc", "h264_amf" })
            foreach (var size in new[] { (1920, 1080), (1080, 1920), (1918, 1080) })
                foreach (int depth in new[] { 1, 4 })
                    await Test($"{encoder} queued D3D {size}, {depth} slots: every output byte/order matches baseline", () =>
                    {
                        var options = Options($"queued-raw-{encoder}-{size.Item1}-{size.Item2}-{depth}") with
                        {
                            Width = size.Item1, Height = size.Item2, Encoder = encoder, Fps = 30,
                            SuperResolutionEnabled = true, UpscaleAlgorithm = "anime4k-rcas",
                            Direct3DReadbackFrames = depth, AudioQuality = "none", WriteCover = false
                        };
                        var canvas = options.GetCanvasLayout();
                        var frame = new byte[canvas.CaptureWidth * canvas.CaptureHeight * 4];
                        var hashes = new Queue<byte[]>();
                        using var baseline = new Direct3DAnimeUpscaler(options, encoder, false);
                        using var queued = new Direct3DAnimeUpscaler(options, encoder, false, queueDepth: depth);
                        int received = 0;
                        void Receive()
                        {
                            byte[] actual = SHA256.HashData(queued.Receive(CancellationToken.None));
                            Check(hashes.Dequeue().SequenceEqual(actual), "frame " + received + " matches baseline bytes in FIFO order");
                            received++;
                        }
                        for (int i = 0; i < 13; i++)
                        {
                            for (int y = 0; y < canvas.CaptureHeight; y++)
                                for (int x = 0; x < canvas.CaptureWidth; x++)
                                {
                                    int at = (y * canvas.CaptureWidth + x) * 4;
                                    frame[at] = (byte)((x / 8 + i * 23) % 256);
                                    frame[at + 1] = (byte)((y / 8 + i * 47) % 256);
                                    frame[at + 2] = (byte)(((x / 17 + y / 19 + i) % 2) * 200 + 20);
                                    frame[at + 3] = 255;
                                }
                            hashes.Enqueue(SHA256.HashData(baseline.Process(frame)));
                            queued.Submit(frame);
                            Array.Fill(frame, (byte)0); // D3D must own its snapshot now.
                            if (queued.PendingFrames == depth) Receive();
                        }
                        while (queued.PendingFrames > 0) Receive();
                        Equal(13, received, "final tail is present exactly once");
                        Equal(0, hashes.Count, "all expected frames consumed");
                        Equal(size.Item1 % 4 == 0, queued.Nv12Output, "packed NV12 versus non-four-aligned RGBA policy");
                        return Task.CompletedTask;
                    });
    }
    private static async Task TestQueuedReadbackAudioMedia()
    {
        foreach (string encoder in new[] { "h264_nvenc", "h264_amf" })
            foreach (int depth in new[] { 1, 4 })
                foreach (int count in new[] { 1, 7 })
                    await Test($"{encoder} {depth}-slot session {count} frames: actual ordered video plus exact PCM tail", async () =>
                    {
                        var options = Options($"queued-av-{encoder}-{depth}-{count}") with
                        {
                            Width = 1920, Height = 1080, Encoder = encoder, Fps = 30,
                            SuperResolutionEnabled = true, UpscaleAlgorithm = "anime4k-rcas",
                            Direct3DReadbackFrames = depth, AudioQuality = "pcm16",
                            AudioSampleRate = 48000, Container = "mkv", WriteCover = false
                        };
                        var canvas = options.GetCanvasLayout();
                        var frame = new byte[canvas.CaptureWidth * canvas.CaptureHeight * 4];
                        var expectedColors = new List<byte[]>();
                        var expectedAudio = new float[count * 1600 * 2];
                        using var reference = new Direct3DAnimeUpscaler(options, encoder, false, useNv12: false);
                        using var session = NewSession(options, flipVertical: false);
                        Equal(depth, session.UpscaleReadbackFrames, "actual requested queue is active");
                        for (int i = 0; i < count; i++)
                        {
                            for (int at = 0; at < frame.Length; at += 4)
                            {
                                frame[at] = (byte)(40 + i * 30); frame[at + 1] = 80;
                                frame[at + 2] = 160; frame[at + 3] = 255;
                            }
                            byte[] referenceRgb = reference.Process(frame);
                            int center = ((options.Height / 2) * options.Width + options.Width / 2) * 4;
                            expectedColors.Add(referenceRgb.AsSpan(center, 3).ToArray());
                            session.WriteFrame(frame, frame.Length);
                            var audio = new float[1600 * 2];
                            for (int sample = 0; sample < 1600; sample++)
                            {
                                // Unique frame levels plus exact PCM16 sample
                                // markers cannot alias a three-frame queue lag,
                                // unlike a tone repeating every 100 ms.
                                audio[sample * 2] = (i * 3000 + sample % 257 + 1000) / 32768f;
                                audio[sample * 2 + 1] = -(i * 2000 + sample % 127 + 2000) / 32768f;
                            }
                            audio.CopyTo(expectedAudio, i * audio.Length);
                            session.WriteAudio(audio, audio.Length);
                        }
                        Equal((long)count, session.FramesAccepted, "video acceptance uses source clock");
                        Equal((long)Math.Max(0, count - depth + 1), session.FramesWritten, "tail remains pending before completion");
                        var result = session.Complete();
                        byte[] decoded = await DecodeVideo(result.OutputPath);
                        Equal(options.Width * options.Height * 4 * count, decoded.Length, "full video decode includes every tail frame");
                        for (int i = 0; i < count; i++)
                        {
                            int center = ((i * options.Height + options.Height / 2) * options.Width + options.Width / 2) * 4;
                            for (int channel = 0; channel < 3; channel++)
                                Check(Math.Abs(decoded[center + channel] - expectedColors[i][channel]) <= 8,
                                    $"decoded frame {i} center channel {channel} retains GPU reference order/color");
                        }
                        float[] actualAudio = await DecodeAudio(result.OutputPath);
                        Equal(expectedAudio.Length, actualAudio.Length, "exact captured PCM samples including the final GPU tail");
                        double maxError = 0;
                        for (int i = 0; i < expectedAudio.Length; i++)
                            maxError = Math.Max(maxError, Math.Abs(actualAudio[i] - expectedAudio[i]));
                        Check(maxError <= 1.01 / 32768.0, "PCM samples retain their original timeline and values");
                        using var probe = await Probe(result.OutputPath);
                        Equal(count.ToString(), Property(Stream(probe, "video"), "nb_read_frames"), "real frame count");
                        Equal("30/1", Property(Stream(probe, "video"), "avg_frame_rate"), "original frame rate");
                    });
    }

}
