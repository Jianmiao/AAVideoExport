using System.Diagnostics;
using Microsoft.Win32;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AAVideoExport.Core;

internal static class VulkanDevicePolicy
{
    // FFmpeg's Vulkan adapter numbers are independent of NVENC/AMF/QSV numbers.
    // Binding by vendor keeps a hybrid laptop's upscaler with its chosen encoder.
    internal static string DeviceArgument(string encoder, int index = -1) => index >= 0
        ? "vulkan=aa_upscale:" + index.ToString(CultureInfo.InvariantCulture) : "vulkan=aa_upscale" +
        (encoder.EndsWith("_nvenc", StringComparison.Ordinal) ? ":NVIDIA"
        : encoder.EndsWith("_amf", StringComparison.Ordinal) ? ":AMD"
        : encoder.EndsWith("_qsv", StringComparison.Ordinal) ? ":Intel" : "");

    private static readonly Lazy<string?> NvidiaManifest = new(FindActiveNvidiaManifest);

    internal static void ConfigureEnvironment(ProcessStartInfo start, string? encoder = null)
    {
        if (!OperatingSystem.IsWindows() || !start.ArgumentList.Any(arg => arg.StartsWith("vulkan=aa_upscale", StringComparison.Ordinal))) return;
        if (!(encoder?.EndsWith("_nvenc", StringComparison.Ordinal) ?? false) && !start.ArgumentList.Contains("vulkan=aa_upscale:NVIDIA")) return;
        // Preserve an explicitly configured driver override. A missing requested
        // GPU then fails preflight rather than falling back to another adapter.
        if (HasValue(start, "VK_DRIVER_FILES") || HasValue(start, "VK_ICD_FILENAMES")) return;
        string? manifest = NvidiaManifest.Value;
        if (manifest == null) return;
        start.Environment.TryGetValue("VK_ADD_DRIVER_FILES", out var existing);
        var paths = (existing ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (paths.Contains(manifest, StringComparer.OrdinalIgnoreCase)) return;
        // Some OEM drivers have OpenGL/CUDA/NVENC registered but omit Vulkan's
        // manifest registration. Add the *active* installed driver's manifest
        // for this FFmpeg child only. No registry or machine environment writes.
        start.Environment["VK_ADD_DRIVER_FILES"] = string.IsNullOrEmpty(existing)
            ? manifest : manifest + Path.PathSeparator + existing;
    }

    private static bool HasValue(ProcessStartInfo start, string name) =>
        start.Environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value);

    internal static async Task<ExportOptions> BindRendererAsync(string ffmpeg, ExportOptions options, string encoder, CancellationToken token)
    {
        if (!UpscaleShaders.UsesGpu(options) || options.UpscaleGpuIndex >= 0 || options.RenderGpuDeviceId <= 0) return options;
        int vendor = EncoderVendorId(encoder);
        // A manually selected different vendor intentionally uses that encoder's
        // GPU. Only bind AA's adapter when rendering and encoding share a vendor.
        if (vendor == 0 || options.RenderGpuVendorId != vendor) return options;
        var args = new[] { "-hide_banner", "-loglevel", "verbose", "-nostdin", "-init_hw_device", DeviceArgument(encoder),
            "-f", "lavfi", "-i", "color=s=16x16", "-frames:v", "1", "-f", "null", "-" };
        var listing = await FfmpegProcess.RunAsync(ffmpeg, args, TimeSpan.FromSeconds(15), token, gpuEncoder: encoder).ConfigureAwait(false);
        if (listing.ExitCode != 0)
            throw new ExportException("upscaler_unavailable", "无法初始化所选显卡的超分设备。请检查 Vulkan 驱动，或关闭超分／选择插值模型。", new InvalidOperationException(listing.Error));
        int index = FindRenderAdapterIndex(listing.Error, encoder, options.RenderGpuDeviceId);
        if (index < 0)
            throw new ExportException("upscaler_unavailable", "超分设备中找不到 AA 正在使用的显卡，已停止以避免误用其他显卡。可关闭超分或使用插值模型。");
        return options with { UpscaleGpuIndex = index };
    }

    internal static int FindRenderAdapterIndex(string listing, string encoder, int deviceId)
    {
        int vendor = EncoderVendorId(encoder);
        foreach (Match match in Regex.Matches(listing, @"(?m)^\[Vulkan @ [^\]]+\]\s+(\d+): (.+) \((integrated|discrete|virtual|software|other)\) \(0x([0-9a-fA-F]+)\)\s*$"))
        {
            string name = match.Groups[2].Value;
            bool matchingVendor = vendor == 0x10de ? name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                : vendor == 0x1002 ? name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                : vendor == 0x8086 && name.Contains("Intel", StringComparison.OrdinalIgnoreCase);
            if (matchingVendor && int.TryParse(match.Groups[4].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) && id == deviceId)
                return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return -1;
    }

    private static int EncoderVendorId(string encoder) => encoder.EndsWith("_nvenc", StringComparison.Ordinal) ? 0x10de
        : encoder.EndsWith("_amf", StringComparison.Ordinal) ? 0x1002 : encoder.EndsWith("_qsv", StringComparison.Ordinal) ? 0x8086 : 0;

    private static string? FindActiveNvidiaManifest()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (adapters == null) return null;
            foreach (string name in adapters.GetSubKeyNames().Where(name => name.Length == 4 && name.All(char.IsDigit)))
            {
                using var adapter = adapters.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is not string description ||
                    !description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;
                var paths = adapter.GetValue("OpenGLDriverName") switch
                {
                    string path => new[] { path }, string[] values => values, _ => Array.Empty<string>()
                };
                foreach (string path in paths)
                {
                    if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
                        !string.Equals(Path.GetFileName(path), "nvoglv64.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    string manifest = Path.Combine(Path.GetDirectoryName(path)!, "nv-vk64.json");
                    if (File.Exists(manifest)) return manifest;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        return null;
    }
}
