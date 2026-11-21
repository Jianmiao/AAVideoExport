using AAVideoExport.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestGpuDeviceBindings()
    {
        await Test("GPU upscaling stays on the selected NVIDIA, AMD or Intel encoder vendor", () =>
        {
            foreach (var row in new[] { ("h264_nvenc", "NVIDIA"), ("hevc_nvenc", "NVIDIA"),
                ("h264_amf", "AMD"), ("hevc_amf", "AMD"), ("h264_qsv", "Intel"), ("av1_qsv", "Intel") })
            {
                var options = new ExportOptions { SuperResolutionEnabled = true };
                var args = new List<string> { "-hide_banner", "-f", "rawvideo", "-i", "pipe:0" };
                EncoderArguments.AddHardwareDeviceOptions(args, options, row.Item1);
                Equal("vulkan=aa_upscale:" + row.Item2, args[args.IndexOf("-init_hw_device") + 1], "explicit vendor device for " + row.Item1);
                Check(args.IndexOf("-init_hw_device") < args.IndexOf("-i"), "device initialized before input");
            }
            return Task.CompletedTask;
        });
        await Test("native and CPU interpolation do not initialize an unrelated Vulkan GPU", () =>
        {
            foreach (var options in new[] { new ExportOptions(), new ExportOptions { SuperResolutionEnabled = true, UpscaleAlgorithm = "bilinear" } })
            {
                var args = new List<string> { "-hide_banner" };
                EncoderArguments.AddHardwareDeviceOptions(args, options, "h264_nvenc");
                Check(!args.Contains("-init_hw_device"), "native/basic interpolation keeps its previous path");
            }
            return Task.CompletedTask;
        });
        await Test("same-vendor integrated and discrete adapters follow AA device ID and FFmpeg enumeration", () =>
        {
            const string listing = "[Vulkan @ abc]     0: AMD Radeon Graphics (integrated) (0x1900)\n" +
                "[Vulkan @ abc]     1: NVIDIA GeForce (discrete) (0x28a0)\n" +
                "[Vulkan @ abc]     2: Intel Arc Graphics (discrete) (0x46a6)\n" +
                "[Vulkan @ abc]     3: AMD Radeon RX Graphics (discrete) (0x744c)\n";
            foreach (var row in new[] { ("h264_amf", 0x744c, 3), ("h264_amf", 0x1900, 0),
                ("hevc_nvenc", 0x28a0, 1), ("av1_qsv", 0x46a6, 2), ("h264_amf", 0x28a0, -1), ("h264_amf", 0x7fff, -1) })
                Equal(row.Item3, VulkanDevicePolicy.FindRenderAdapterIndex(listing, row.Item1, row.Item2), "renderer identity for " + row.Item1);
            Equal(5, VulkanDevicePolicy.FindRenderAdapterIndex(listing.Replace("3: AMD", "5: AMD"), "h264_amf", 0x744c), "enumeration order is not assumed");
            Equal("vulkan=aa_upscale:5", VulkanDevicePolicy.DeviceArgument("h264_amf", 5), "resolved Vulkan adapter index");
            return Task.CompletedTask;
        });
        await Test("explicit Vulkan driver overrides and non-NVIDIA processes retain their environment", () =>
        {
            foreach (string variable in new[] { "VK_DRIVER_FILES", "VK_ICD_FILENAMES" })
            {
                var start = new ProcessStartInfo();
                start.ArgumentList.Add("vulkan=aa_upscale:2");
                start.Environment[variable] = "user-selected-driver.json";
                start.Environment["VK_ADD_DRIVER_FILES"] = "user-added-driver.json";
                VulkanDevicePolicy.ConfigureEnvironment(start, "h264_nvenc");
                Equal("user-selected-driver.json", start.Environment[variable], "preserve explicit override");
                Equal("user-added-driver.json", start.Environment["VK_ADD_DRIVER_FILES"], "do not supplement explicit override");
            }
            foreach (string encoder in new[] { "h264_amf", "h264_qsv" })
            {
                var start = new ProcessStartInfo();
                start.ArgumentList.Add(VulkanDevicePolicy.DeviceArgument(encoder));
                start.Environment["VK_ADD_DRIVER_FILES"] = "user-added-driver.json";
                VulkanDevicePolicy.ConfigureEnvironment(start, encoder);
                Equal("user-added-driver.json", start.Environment["VK_ADD_DRIVER_FILES"], "no NVIDIA driver on " + encoder);
            }
            return Task.CompletedTask;
        });
        await Test("adapter binding skips native output, CPU interpolation and absent renderer metadata", async () =>
        {
            foreach (var options in new[] {
                new ExportOptions { RenderGpuVendorId = 0x10de, RenderGpuDeviceId = 0x28a0 },
                new ExportOptions { SuperResolutionEnabled = true, UpscaleAlgorithm = "bilinear", RenderGpuVendorId = 0x10de, RenderGpuDeviceId = 0x28a0 },
                new ExportOptions { SuperResolutionEnabled = true } })
                Equal(options, await VulkanDevicePolicy.BindRendererAsync("missing-ffmpeg", options, "h264_nvenc", CancellationToken.None), "no new probe for unaffected path");
        });
    }

    private static async Task TestGpuDeviceRuntime()
    {
        await Test("production FFmpeg process selects NVIDIA Vulkan and binds the enumerated renderer ID", async () =>
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "verbose", "-f", "lavfi", "-i", "color=s=16x16", "-frames:v", "1", "-f", "null", "-" };
            EncoderArguments.AddHardwareDeviceOptions(args, new ExportOptions { SuperResolutionEnabled = true }, "h264_nvenc");
            var result = await FfmpegProcess.RunAsync(ffmpeg, args, TimeSpan.FromSeconds(15), CancellationToken.None);
            result.EnsureSuccess("NVIDIA Vulkan device selection");
            Check(result.Error.Contains("Using device: NVIDIA", StringComparison.Ordinal), "actual selected vendor confirmed in FFmpeg output");
            Console.WriteLine(result.Error.Split('\n').First(line => line.Contains("Using device:")));
            var device = Regex.Match(result.Error, @"NVIDIA .+ \(discrete\) \(0x([0-9a-fA-F]+)\)");
            Check(device.Success, "NVIDIA PCI device ID enumerated");
            var options = new ExportOptions { SuperResolutionEnabled = true, RenderGpuVendorId = 0x10de,
                RenderGpuDeviceId = int.Parse(device.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                Title = "renderer-device-" + Guid.NewGuid().ToString("N"), OutputDirectory = Path.Combine(Root, "gpu-device-runtime"),
                AudioQuality = "none", WriteCover = false, UpscaleAlgorithm = "anime4k-rcas" };
            var bound = await VulkanDevicePolicy.BindRendererAsync(ffmpeg, options, "h264_nvenc", CancellationToken.None);
            Check(bound.UpscaleGpuIndex >= 0, "bound actual renderer to Vulkan index");
            using var session = new ExportSession(options, ffmpeg, ffprobe, "h264_nvenc", flipVertical: false);
            var canvas = options.GetCanvasLayout();
            for (int frame = 0; frame < 3; frame++)
            {
                var pixels = ProxyFrame(canvas, frame);
                session.WriteFrame(pixels, pixels.Length);
            }
            var output = session.Complete();
            var decoded = await DecodeVideo(output.OutputPath);
            Equal(options.Width * options.Height * 4 * 3, decoded.Length, "all three upscaled output frames decode");
            Console.WriteLine("Actual Anime4K + RCAS / NVENC session passed on Vulkan index " + bound.UpscaleGpuIndex);
            Console.WriteLine("Retained output: " + output.OutputPath);
            var missing = options with { RenderGpuDeviceId = 0x7fffffff, OutputDirectory = Path.Combine(Root, "missing-render-adapter-" + Guid.NewGuid().ToString("N")) };
            try
            {
                using var unexpected = new ExportSession(missing, ffmpeg, ffprobe, "h264_nvenc");
                throw new InvalidOperationException("missing renderer must not silently select another GPU");
            }
            catch (ExportException error) when (error.Code == "upscaler_unavailable") { }
            Check(!Directory.Exists(missing.OutputDirectory), "missing renderer fails before creating output files");
        });
    }
}
