using System.Runtime.InteropServices;
using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;
internal static partial class Program
{
    private static async Task TestDirect3DMedia()
    {
        await TestQueuedReadbackGpu();
        await TestQueuedReadbackAudioMedia();
        foreach (string encoder in new[]
        {
            "h264_nvenc",
            "h264_amf"
        }

        )
            foreach (var size in new[]
            {
                (1920, 1080),
                (1080, 1920),
                (1080, 1080),
                (1102, 1200)
            }

            )
                foreach (int depth in new[] { 1, 4 })
                await Test($"{encoder} D3D {size}, {depth} slots: raw NV12 matches CPU conversion and survives cancel/retry", async () =>
                {
                    var options = Options($"d3d-{encoder}-{size.Item1}-{size.Item2}-{depth}")with
                    {
                        Width = size.Item1,
                        Height = size.Item2,
                        Encoder = encoder,
                        Fps = 30,
                        Direct3DReadbackFrames = depth,
                        SuperResolutionEnabled = true,
                        UpscaleAlgorithm = "anime4k-rcas",
                        AudioQuality = "none",
                        WriteCover = false
                    };
                    var canvas = options.GetCanvasLayout();
                    byte[] input = new byte[canvas.CaptureWidth * canvas.CaptureHeight * 4];
                    for (int y = 0; y < canvas.CaptureHeight; y++)
                        for (int x = 0; x < canvas.CaptureWidth; x++)
                        {
                            int at = (y * canvas.CaptureWidth + x) * 4;
                            input[at] = (byte)(x * 255 / canvas.CaptureWidth);
                            input[at + 1] = (byte)(y * 255 / canvas.CaptureHeight);
                            input[at + 2] = (byte)(((x / 8 + y / 8) % 2) * 190 + 32);
                            input[at + 3] = 255;
                        }

                    byte[] rgb;
                    using (var gpu = new Direct3DAnimeUpscaler(options, encoder, false, useNv12: false))
                        rgb = gpu.Process(input).ToArray();
                    byte[] packed;
                    using (var gpu = new Direct3DAnimeUpscaler(options, encoder, false))
                    {
                        packed = gpu.Process(input).ToArray();
                        Equal(options.Width % 4 == 0, gpu.Nv12Output, "unaligned rows keep RGBA");
                    }

                    if (options.Width % 4 == 0)
                    {
                        string path = Path.Combine(Root, options.Title + ".rgba");
                        File.WriteAllBytes(path, rgb);
                        var cpu = await RunBinary(ffmpeg, "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", options.Width + "x" + options.Height, "-i", path, "-frames:v", "1", "-vf", "scale=in_range=full:out_range=tv:out_color_matrix=bt709,format=nv12", "-f", "rawvideo", "pipe:1");
                        Equal(cpu.Length, packed.Length, "packed NV12 row/plane length");
                        long difference = 0;
                        int maximum = 0;
                        for (int i = 0; i < cpu.Length; i++)
                        {
                            int delta = Math.Abs(cpu[i] - packed[i]);
                            difference += delta;
                            maximum = Math.Max(maximum, delta);
                        }

                        Check(maximum <= 2 && difference / (double)cpu.Length < 0.25, "GPU Rec.709 and bicubic chroma match CPU within rounding tolerance");
                        Console.WriteLine($"  raw conversion mean={difference / (double)cpu.Length:F5}, max={maximum}");
                    }
                    else
                        Check(rgb.SequenceEqual(packed), "RGBA fallback preserves all pixels");
                    using (var cancelled = NewSession(options))
                    {
                        cancelled.WriteFrame(input, input.Length);
                        cancelled.Cancel();
                        ThrowsCode("cancelled", () => cancelled.WriteFrame(input, input.Length), "D3D cancellation");
                    }

                    Check(!File.Exists(Path.Combine(options.OutputDirectory, options.Title + ".mp4")), "cancel leaves no partial final video");
                    using (var retry = NewSession(options))
                    {
                        Equal(options.Width % 4 == 0 ? "d3d11-nv12" : "d3d11", retry.UpscaleExecution, "actual GPU backend without silent fallback");
                        Equal(depth, retry.UpscaleReadbackFrames, "requested queue is active without silent one-slot fallback");
                        var pointer = Marshal.AllocHGlobal(input.Length);
                        try
                        {
                            Marshal.Copy(input, 0, pointer, input.Length);
                            for (int i = 0; i < 3; i++)
                                retry.WriteFrame(pointer, input.Length);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pointer);
                        }

                        var result = retry.Complete();
                        using var metadata = await Probe(result.OutputPath);
                        var video = Stream(metadata, "video");
                        Equal("3", Property(video, "nb_read_frames"), "retry frame count");
                        Equal("tv", Property(video, "color_range"), "limited range");
                        Equal("bt709", Property(video, "color_space"), "Rec.709 matrix");
                        Equal(options.Width.ToString(), Property(video, "width"), "output width");
                        Equal(options.Height.ToString(), Property(video, "height"), "output height");
                    }
                });
    }
}
