using System.Text.RegularExpressions;

namespace AAVideoExport.Core;

public sealed class FfmpegCapabilities
{
    private readonly string gpuName;
    public IReadOnlyList<EncoderCapability> Encoders { get; }
    public IReadOnlyList<EncoderCapability> HardwareEncoders { get; }

    private FfmpegCapabilities(IReadOnlyList<EncoderCapability> encoders, string gpuName)
    {
        Encoders = encoders;
        HardwareEncoders = HardwareEncoderPolicy.GetAvailable(encoders);
        this.gpuName = gpuName;
    }

    public static Task<FfmpegCapabilities> ProbeAsync(string ffmpegPath, string gpuName, CancellationToken cancellationToken = default) =>
        ProbeCandidatesAsync(ffmpegPath, gpuName, false, cancellationToken);

    public static Task<FfmpegCapabilities> ProbeHardwareAsync(string ffmpegPath, string gpuName, CancellationToken cancellationToken = default) =>
        ProbeCandidatesAsync(ffmpegPath, gpuName, true, cancellationToken);

    private static async Task<FfmpegCapabilities> ProbeCandidatesAsync(string ffmpegPath, string gpuName, bool hardwareOnly, CancellationToken cancellationToken)
    {
        var listing = await FfmpegProcess.RunAsync(ffmpegPath, new[] { "-hide_banner", "-encoders" }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        listing.EnsureSuccess("Encoder discovery");
        using var semaphore = new SemaphoreSlim(2);
        var tasks = EncoderCatalog.All.Where(candidate => !hardwareOnly || candidate.Hardware).Select(async candidate =>
        {
            if (!Regex.IsMatch(listing.Output, @"(?m)^\s*V\S*\s+" + Regex.Escape(candidate.Name) + @"\s"))
                return candidate with { Detail = "Not included in this FFmpeg build." };
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var options = new ExportOptions { Codec = candidate.Codec, Container = candidate.Codec == "qtrle" ? "mov" : "mkv", Width = 256, Height = 144, Fps = 30, AudioQuality = "none" };
                await TestEncoderAsync(ffmpegPath, options, candidate.Name, cancellationToken).ConfigureAwait(false);
                return candidate with { Available = true, Detail = "Passed a real 3-frame encode at 256 × 144." };
            }
            catch (ExportException ex) when (ex.Code != "cancelled")
            {
                string detail = ex.Message;
                return candidate with { Detail = detail.Length <= 1000 ? detail : detail[^1000..] };
            }
            finally { semaphore.Release(); }
        });
        return new FfmpegCapabilities(Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false)), gpuName ?? "");
    }

    public string PickHardwareEncoder(ExportOptions options) => HardwareEncoderPolicy.Select(options, HardwareEncoders, gpuName);

    public string PickEncoder(ExportOptions options)
    {
        options.Validate();
        if (options.Encoder != "auto")
        {
            if (Encoders.Any(e => e.Name == options.Encoder && e.Codec == options.Codec && e.Available)) return options.Encoder;
            throw new ExportException("encoder_unavailable", "The selected encoder failed its real encode check.");
        }
        string preferred = gpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("Arc", StringComparison.OrdinalIgnoreCase) ? "_qsv"
            : gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "_nvenc"
            : gpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "_amf" : "";
        var selected = Encoders.Where(e => e.Available && e.Codec == options.Codec)
            .OrderBy(e => preferred.Length > 0 && e.Name.EndsWith(preferred, StringComparison.Ordinal) ? 0 : e.Hardware ? 1 : 2).FirstOrDefault();
        return selected?.Name ?? throw new ExportException("encoder_unavailable", "No working encoder is available for " + options.Codec + ".");
    }

    /// <summary>Checks the requested dimensions, frame rate, and rate control before capture starts.</summary>
    public static async Task TestEncoderAsync(string ffmpegPath, ExportOptions options, string encoder, CancellationToken cancellationToken = default)
    {
        await PrepareEncoderAsync(ffmpegPath, options, encoder, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ExportOptions> PrepareEncoderAsync(string ffmpegPath, ExportOptions options, string encoder, CancellationToken cancellationToken = default, bool nv12Input = false)
    {
        options = await VulkanDevicePolicy.BindRendererAsync(ffmpegPath, options, encoder, cancellationToken).ConfigureAwait(false);
        return await ChooseShaderExecutionAsync(options, candidate =>
            TestResolvedEncoderAsync(ffmpegPath, candidate, encoder, cancellationToken, nv12Input)).ConfigureAwait(false);
    }

    internal static async Task<ExportOptions> ChooseShaderExecutionAsync(ExportOptions options, Func<ExportOptions, Task> test)
    {
        try
        {
            await test(options).ConfigureAwait(false);
            return options;
        }
        catch (ExportException error) when (options.UseComputeUpscale && UpscaleShaders.UsesGpu(options) &&
            options.UpscaleAlgorithm is "anime4k-cnn" or "anime4k-rcas" && error.Code == "upscaler_unavailable")
        {
            // Older Vulkan drivers/builds can retain the original fragment
            // implementation of the same CNN on the same selected adapter.
            var fallback = options with { UseComputeUpscale = false };
            await test(fallback).ConfigureAwait(false);
            return fallback;
        }
    }

    private static async Task TestResolvedEncoderAsync(string ffmpegPath, ExportOptions options, string encoder, CancellationToken cancellationToken, bool nv12Input = false)
    {
        var canvas = options.GetCanvasLayout();
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
            "color=c=black:s=" + canvas.CaptureWidth + "x" + canvas.CaptureHeight + ":r=" + options.Fps, "-frames:v", "3", "-an" };
        EncoderArguments.AddHardwareDeviceOptions(args, options, encoder);
        EncoderArguments.AddFilterThreadOptions(args, options);
        args.AddRange(nv12Input ? EncoderArguments.VideoNv12(options,encoder) : EncoderArguments.Video(options, encoder, false));
        args.AddRange(new[] { "-f", "null", "-" });
        string? shaderDirectory = null;
        try
        {
            if (UpscaleShaders.UsesGpu(options))
            {
                // libplacebo can ignore a malformed pass and still return a
                // successful encode. Capture that warning during preflight.
                args[args.IndexOf("-loglevel")+1]="warning";
                shaderDirectory = Path.Combine(Path.GetTempPath(), "aa-upscale-preflight-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(shaderDirectory);
                UpscaleShaders.Stage(shaderDirectory, options);
            }
            var result = await FfmpegProcess.RunAsync(ffmpegPath, args,
                TimeSpan.FromSeconds(shaderDirectory == null ? 10 : 30), cancellationToken, shaderDirectory, encoder).ConfigureAwait(false);
            result.EnsureSuccess("Encoder/upscaler preflight (" + encoder + ")");
            if(shaderDirectory != null) ValidateShaderPreflight(result);
        }
        catch (ExportException error) when (shaderDirectory != null && error.Code is "encoder_failed" or "process_timeout")
        {
            throw new ExportException("upscaler_unavailable", "GPU upscaling preflight failed on " + VulkanDevicePolicy.DeviceArgument(encoder) +
                ". The requested GPU must have a working Vulkan driver; another GPU will not be selected silently. " + error.Message, error);
        }
        finally
        {
            if (shaderDirectory != null)
            {
                try
                {
                    File.Delete(Path.Combine(shaderDirectory, UpscaleShaders.FileName(options.UpscaleAlgorithm)));
                    Directory.Delete(shaderDirectory); // Only our shader file; no recursive deletion.
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static void ValidateShaderPreflight(ProcessResult result)
    {
        if(result.Error.Contains("Pass has no hooked textures",StringComparison.OrdinalIgnoreCase))
            throw new ExportException("upscaler_unavailable",
                "The GPU driver ignored a required model pass. "+result.Error);
    }
}
