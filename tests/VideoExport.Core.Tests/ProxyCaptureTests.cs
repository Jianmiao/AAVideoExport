using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestProxyLayouts()
    {
        await Test("proxy capture preserves landscape, portrait and square delivery dimensions", () =>
        {
            foreach (var shape in new[] { (1920, 1080, 1280, 720), (1080, 1920, 720, 1280), (1080, 1080, 720, 720), (1600, 900, 1280, 720) })
            {
                var options = Options("proxy-layout") with { Width = shape.Item1, Height = shape.Item2, CaptureMode = "proxy720" };
                var original = options with { };
                options.Validate(); var canvas = options.GetCanvasLayout();
                Equal(shape.Item3, canvas.CaptureWidth, "reduced capture width");
                Equal(shape.Item4, canvas.CaptureHeight, "reduced capture height");
                Equal(shape.Item1, canvas.OutputWidth, "delivery width");
                Equal(shape.Item2, canvas.OutputHeight, "delivery height");
                Check(canvas.IsProxy && canvas.VideoFilter == null && canvas.OffsetX == 0 && canvas.OffsetY == 0,
                    "proxy viewport cannot become an invalid crop or introduce padding");
                Equal(original, options, "proxy must not change the layout/output request");
            }
            var middle = (Options("proxy900") with { Width = 1920, Height = 1080, CaptureMode = "proxy900" }).GetCanvasLayout();
            Equal(1600, middle.CaptureWidth, "900p capture width"); Equal(900, middle.CaptureHeight, "900p capture height");
            var below = (Options("small") with { CaptureMode = "proxy720" }).GetCanvasLayout();
            Check(below.IsIdentity, "small output must never be upsampled or reduced by a larger proxy preset");
            return Task.CompletedTask;
        });
        await Test("proxy rejects unknown modes and legacy fit/fill combinations", () =>
        {
            foreach (var mode in new[] { "unknown", "", "PROXY720" })
                ThrowsCode("invalid_settings", (Options("invalid-proxy") with { CaptureMode = mode }).Validate, "unknown capture mode");
            foreach (var mode in new[] { "fit", "fill" })
                ThrowsCode("invalid_settings", (Options("invalid-proxy") with { CaptureMode = "proxy720", CanvasMode = mode }).Validate,
                    "proxy cannot alter legacy fit/fill composition");
            return Task.CompletedTask;
        });
        await Test("720p upscaling uses one Rec.709 scale and preserves hardware quality settings", () =>
        {
            foreach (string encoder in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" })
            {
                var options = Options("proxy-encoder") with { Width = 1920, Height = 1080, CaptureMode = "proxy720", Encoder = encoder, BitrateKbps = 6000 };
                var args = EncoderArguments.Video(options, encoder, true).ToArray();
                string filter = args[Array.IndexOf(args, "-vf") + 1];
                Equal(1, filter.Split("scale=", StringSplitOptions.None).Length - 1, "only one resize/color conversion pass");
                Check(filter.StartsWith("vflip,scale=w=1920:h=1080:flags=bilinear:in_range=full:out_range=tv:out_color_matrix=bt709", StringComparison.Ordinal),
                    "proxy keeps vertical correction, chosen output dimensions, limited range and Rec.709");
                Check(!filter.Contains("crop=") && !filter.Contains("pad="), "proxy must retain the complete composition");
                Equal("6000k", args[Array.IndexOf(args, "-b:v") + 1], "bitrate must not change to manufacture a speed gain");
                if (encoder.EndsWith("_nvenc")) Equal("p4", args[Array.IndexOf(args, "-preset") + 1], "NVENC quality preset retained");
            }
            return Task.CompletedTask;
        });
    }

    private static byte[] ProxyFrame(CanvasLayout canvas, int frame)
    {
        var pixels = new byte[canvas.CaptureWidth * canvas.CaptureHeight * 4];
        for (int y = 0; y < canvas.CaptureHeight; y++)
        for (int x = 0; x < canvas.CaptureWidth; x++)
        {
            int quadrant = (x >= canvas.CaptureWidth / 2 ? 1 : 0) + (y >= canvas.CaptureHeight / 2 ? 2 : 0);
            var color = ProxyColor(quadrant, frame);
            int at = ((canvas.CaptureHeight - 1 - y) * canvas.CaptureWidth + x) * 4;
            pixels[at] = color.R; pixels[at + 1] = color.G; pixels[at + 2] = color.B; pixels[at + 3] = 255;
        }
        return pixels;
    }

    private static (byte R, byte G, byte B) ProxyColor(int quadrant, int frame) => quadrant switch
    {
        // Large per-frame changes remain distinguishable in p4 VBR B-frames.
        // A 10-level blue change can be quantized away even by the native
        // baseline; use a stronger sequence marker without relaxing tolerance.
        0 => ((byte)(210 - frame * 40), 25, 35), 1 => (25, (byte)(200 - frame * 40), 35),
        2 => (35, 45, (byte)(210 - frame * 40)), _ => ((byte)(180 - frame * 40), 190, 45)
    };

    private static async Task TestProxyMedia()
    {
        await Test("cancelling a real native FFmpeg pipe returns before freeing its buffer", async () =>
        {
            var options = Options("native-pipe-cancel") with
            {
                Width = 1920, Height = 1080, CaptureMode = "proxy720", AudioQuality = "none", WriteCover = false
            };
            using var session = NewSession(options);
            byte[] pixels = ProxyFrame(options.GetCanvasLayout(), 0);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = Task.Run(() =>
            {
                IntPtr buffer = Marshal.AllocHGlobal(pixels.Length);
                try
                {
                    Marshal.Copy(pixels, 0, buffer, pixels.Length);
                    for (;;)
                    {
                        session.WriteFrame(buffer, pixels.Length);
                        entered.TrySetResult();
                    }
                }
                catch (ExportException error) when (error.Code == "cancelled") { }
                finally { Marshal.FreeHGlobal(buffer); }
            });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                session.Cancel();
                await writer.WaitAsync(TimeSpan.FromSeconds(10));
                Check(!File.Exists(session.OutputPath), "cancelled native writes cannot publish a completed video");
            }
            finally { session.Cancel(); await writer.WaitAsync(TimeSpan.FromSeconds(10)); }
        });
        foreach (var shape in new[] { (1920, 1080, "balanced"), (1080, 1920, "quality") })
        foreach (string algorithm in new[] { "bilinear", "bicubic", "lanczos" })
        foreach (bool native in new[] { false, true })
        {
            await Test($"decoded super resolution {shape.Item3} {algorithm} {shape.Item1}x{shape.Item2}, native={native}: pixels, order, PCM and cover", async () =>
            {
                var options = Options($"sr-media-{shape.Item3}-{algorithm}-{native}") with
                {
                    Width = shape.Item1, Height = shape.Item2, SuperResolutionEnabled = true,
                    SuperResolutionTier = shape.Item3, UpscaleAlgorithm = algorithm,
                    Fps = 30, Codec = "qtrle", Encoder = "qtrle", Container = "mov", AudioQuality = "pcm16"
                };
                var canvas = options.GetCanvasLayout();
                using var session = NewSession(options);
                const int count = 3;
                IntPtr memory = native ? Marshal.AllocHGlobal(canvas.CaptureWidth * canvas.CaptureHeight * 4) : IntPtr.Zero;
                try
                {
                    ThrowsCode("invalid_frame", () => session.WriteFrame(IntPtr.Zero, 4), "empty native buffer");
                    for (int i = 0; i < count; i++)
                    {
                        byte[] pixels = ProxyFrame(canvas, i);
                        if (native) { Marshal.Copy(pixels, 0, memory, pixels.Length); session.WriteFrame(memory, pixels.Length); }
                        else session.WriteFrame(pixels, pixels.Length);
                        var audio = new float[SampleRate / options.Fps * Channels];
                        Array.Fill(audio, 0.125f); session.WriteAudio(audio, audio.Length);
                    }
                }
                finally { if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory); }
                var result = session.Complete();
                var decoded = await DecodeVideo(result.OutputPath);
                int frameBytes = options.Width * options.Height * 4;
                Equal(frameBytes * count, decoded.Length, "delivered dimensions and frame count");
                for (int i = 0; i < count; i++)
                for (int quadrant = 0; quadrant < 4; quadrant++)
                {
                    int x = options.Width * (quadrant % 2 == 0 ? 1 : 3) / 4;
                    int y = options.Height * (quadrant < 2 ? 1 : 3) / 4;
                    int at = i * frameBytes + (y * options.Width + x) * 4;
                    var color = ProxyColor(quadrant, i);
                    Check(Math.Abs(decoded[at] - color.R) <= 1 && Math.Abs(decoded[at + 1] - color.G) <= 1 && Math.Abs(decoded[at + 2] - color.B) <= 1,
                        "upscaled quadrants retain orientation, color and per-frame change");
                }
                var samples = await DecodeAudio(result.OutputPath);
                Equal(count * SampleRate / options.Fps * Channels, samples.Length, "PCM remains tied to output time rather than capture pixels");
                Check(samples.All(sample => Math.Abs(sample - 0.125f) < 0.00004f), "PCM amplitude remains unchanged");
                var cover = await RunBinary(ffmpeg, "-v", "error", "-i", result.CoverPath!, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");
                Check(cover.SequenceEqual(decoded.Take(frameBytes)), "cover has the delivered resolution and the same first frame");
            });
        }
    }

    private static async Task TestProxyHardwareMedia()
    {
        // Explicit local hardware acceptance. These modes deliberately fail on
        // machines without the requested GPU instead of reporting a fake pass.
        foreach (string encoder in new[] { "h264_nvenc", "h264_amf" })
        foreach (string algorithm in new[] { "bilinear", "bicubic", "lanczos", "fsr1-luma", "anime4k-cnn", "anime4k-rcas" })
        foreach (string tier in algorithm is "fsr1-luma" or "anime4k-cnn" or "anime4k-rcas" ? new[] { "balanced", "quality" } : new[] { "balanced" })
        {
            await Test($"actual {encoder} {tier} {algorithm}: delivered pixels, orientation and Rec.709 metadata", async () =>
            {
                bool gpuModel = algorithm is "fsr1-luma" or "anime4k-cnn" or "anime4k-rcas";
                var options = Options($"proxy-gpu-{encoder}-{algorithm}-{tier}") with
                {
                    Width = 1920, Height = 1080, SuperResolutionEnabled = true, SuperResolutionTier = tier, UpscaleAlgorithm = algorithm, Fps = 60,
                    Encoder = encoder, BitrateKbps = 6000, AudioQuality = gpuModel ? "pcm16" : "none", WriteCover = gpuModel
                };
                var canvas = options.GetCanvasLayout(); using var session = NewSession(options);
                IntPtr memory = Marshal.AllocHGlobal(canvas.CaptureWidth * canvas.CaptureHeight * 4);
                const int count = 3;
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        byte[] pixels = ProxyFrame(canvas, i); Marshal.Copy(pixels, 0, memory, pixels.Length);
                        session.WriteFrame(memory, pixels.Length);
                        if (gpuModel)
                        {
                            var audio = new float[SampleRate / options.Fps * Channels];
                            Array.Fill(audio, 0.125f); session.WriteAudio(audio, audio.Length);
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(memory); }
                var result = session.Complete(); var pixelsOut = await DecodeVideo(result.OutputPath);
                int frameBytes = options.Width * options.Height * 4;
                Equal(frameBytes * count, pixelsOut.Length, "hardware output decodes every delivered frame");
                for (int i = 0; i < count; i++)
                for (int quadrant = 0; quadrant < 4; quadrant++)
                {
                    int x = options.Width * (quadrant % 2 == 0 ? 1 : 3) / 4;
                    int y = options.Height * (quadrant < 2 ? 1 : 3) / 4;
                    int at = i * frameBytes + (y * options.Width + x) * 4;
                    var expected = ProxyColor(quadrant, i);
                    Check(Math.Abs(pixelsOut[at] - expected.R) <= 7 && Math.Abs(pixelsOut[at + 1] - expected.G) <= 7 && Math.Abs(pixelsOut[at + 2] - expected.B) <= 7,
                        "RGBA conversion keeps colors, top/bottom orientation and frame order within codec tolerance");
                }
                using var metadata = await Probe(result.OutputPath); var video = Stream(metadata, "video");
                Equal("tv", Property(video, "color_range"), "limited output range");
                foreach (string field in new[] { "color_space", "color_primaries", "color_transfer" })
                    Equal("bt709", Property(video, field), field);
                if (gpuModel)
                {
                    var samples = await DecodeAudio(result.OutputPath);
                    Equal(count * SampleRate / options.Fps * Channels, samples.Length, "GPU shader cannot change audio timing");
                    Check(samples.All(sample => Math.Abs(sample - 0.125f) < 0.00004f), "PCM amplitude survives GPU-model export");
                    var cover = await RunBinary(ffmpeg, "-v", "error", "-i", result.CoverPath!, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");
                    Check(cover.SequenceEqual(pixelsOut.Take(frameBytes)), "GPU-model cover matches the delivered first frame");
                }
            });
        }
    }

    private static async Task TestGpuModelActivation()
    {
        foreach (string model in new[] { "fsr1-luma", "anime4k-cnn", "anime4k-rcas" })
        foreach (string tier in new[] { "balanced", "quality" })
        await Test($"{model} {tier} shader changes actual pixels compared with the identical GPU path without the shader", async () =>
        {
            var options = Options("sr-model-activation") with { Width = 1920, Height = 1080,
                SuperResolutionEnabled = true, SuperResolutionTier = tier, UpscaleAlgorithm = model, Codec = "h264" };
            var canvas = options.GetCanvasLayout();
            string directory = Path.Combine(Root, model + "-" + tier + "-activation");
            Directory.CreateDirectory(directory); UpscaleShaders.Stage(directory, options);
            var videoArgs = EncoderArguments.Video(options, "h264_nvenc", false).ToArray();
            string filter = videoArgs[Array.IndexOf(videoArgs, "-vf") + 1];
            var hashes = new List<string>();
            foreach (bool withShader in new[] { false, true })
            {
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
                    $"testsrc=s={canvas.CaptureWidth}x{canvas.CaptureHeight}:r=30,format=rgba", "-frames:v", "1", "-an", "-vf",
                    withShader ? filter : filter.Replace(":custom_shader_path=" + UpscaleShaders.FileName(model), ""), "-f", "framemd5", "-" };
                EncoderArguments.AddHardwareDeviceOptions(args, options, "h264_nvenc");
                var result = await FfmpegProcess.RunAsync(ffmpeg, args, TimeSpan.FromSeconds(30), CancellationToken.None, directory);
                result.EnsureSuccess("Model activation control");
                string hash = result.Output.Split('\n').Last(line => line.Length > 0 && !line.StartsWith('#')).Split(',').Last().Trim();
                hashes.Add(hash);
                File.WriteAllText(Path.Combine(directory, withShader ? "shader.framemd5" : "control.framemd5"), result.Output);
            }
            Check(hashes[0] != hashes[1], "model must really run, including the 1.2x quality tier");
            if (model == "anime4k-rcas")
            {
                string zeroDirectory = Path.Combine(directory, "zero-sharpness");
                Directory.CreateDirectory(zeroDirectory);
                UpscaleShaders.Stage(zeroDirectory, options with { RcasSharpness = 0 });
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
                    $"testsrc=s={canvas.CaptureWidth}x{canvas.CaptureHeight}:r=30,format=rgba", "-frames:v", "1", "-an", "-vf", filter, "-f", "framemd5", "-" };
                EncoderArguments.AddHardwareDeviceOptions(args, options, "h264_nvenc");
                var result = await FfmpegProcess.RunAsync(ffmpeg, args, TimeSpan.FromSeconds(30), CancellationToken.None, zeroDirectory);
                result.EnsureSuccess("Zero sharpness control");
                File.WriteAllText(Path.Combine(zeroDirectory, "shader.framemd5"), result.Output);
                string zeroHash = result.Output.Split('\n').Last(line => line.Length > 0 && !line.StartsWith('#')).Split(',').Last().Trim();
                string plainOutput = File.ReadAllText(Path.Combine(Root, "anime4k-cnn-" + tier + "-activation", "shader.framemd5"));
                string plainHash = plainOutput.Split('\n').Last(line => line.Length > 0 && !line.StartsWith('#')).Split(',').Last().Trim();
                Equal(plainHash, zeroHash, "zero sharpness exactly bypasses RCAS");
                Check(zeroHash != hashes[1], "0.87 sharpness actually changes pixels after Anime4K");
            }
        });
    }

    private static Task BenchmarkProxy()
    {
        var results = new List<object>();
        const int count = 300;
        // Interleave the four paths; these are repeated synthetic frames,
        // measuring transfer/filter/encode/finalization, not Unity rendering.
        for (int repeat = 0; repeat < 3; repeat++)
        foreach (string mode in new[] { "native", "proxy720" })
        foreach (bool native in new[] { false, true })
        {
            var options = Options($"proxy-bench-{repeat}-{mode}-{native}") with
            {
                Width = 1920, Height = 1080, Fps = 60, CaptureMode = mode,
                Encoder = "h264_nvenc", Codec = "h264", BitrateKbps = 6000,
                AudioQuality = "none", WriteCover = false
            };
            var canvas = options.GetCanvasLayout(); var pixels = ProxyFrame(canvas, 0);
            IntPtr memory = native ? Marshal.AllocHGlobal(pixels.Length) : IntPtr.Zero;
            if (native) Marshal.Copy(pixels, 0, memory, pixels.Length);
            try
            {
                var start = Stopwatch.StartNew();
                using var session = NewSession(options);
                double preflight = start.Elapsed.TotalSeconds;
                var transfer = Stopwatch.StartNew();
                for (int i = 0; i < count; i++)
                    if (native) session.WriteFrame(memory, pixels.Length); else session.WriteFrame(pixels, pixels.Length);
                double writes = transfer.Elapsed.TotalSeconds;
                var result = session.Complete(); transfer.Stop(); start.Stop();
                results.Add(new { repeat, mode, native, frames = count, captureWidth = canvas.CaptureWidth, captureHeight = canvas.CaptureHeight,
                    frameBytes = pixels.Length, preflightSeconds = preflight, writeSeconds = writes,
                    writeAndFinalizeSeconds = transfer.Elapsed.TotalSeconds, totalSeconds = start.Elapsed.TotalSeconds, output = result.OutputPath });
                Console.WriteLine($"BENCHMARK {mode} native={native}: {writes:F3}s writes, {transfer.Elapsed.TotalSeconds:F3}s with finalization");
            }
            finally { if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory); }
        }
        File.WriteAllText(Path.Combine(Root, "proxy-benchmark.json"), JsonSerializer.Serialize(new
        {
            scope = "synthetic repeated color quadrants; no Unity render/readback/audio; identical NVENC p4 VBR 6000kbps and output1080p60",
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
        return Task.CompletedTask;
    }
}
