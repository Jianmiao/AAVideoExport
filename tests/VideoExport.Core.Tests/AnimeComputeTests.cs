using AAVideoExport.Core;
using System.Text.RegularExpressions;

namespace AAVideoExport.Core.Tests;
internal static partial class Program
{
    private static async Task TestAnimeComputeContract()
    {
        await Test("a zero-exit encoder cannot silently ignore a required shader pass", () =>
        {
            ThrowsCode("upscaler_unavailable", () => FfmpegCapabilities.ValidateShaderPreflight(new ProcessResult(0, "", "[libplacebo] Pass has no hooked textures (will be ignored)!")), "ignored model pass");
            FfmpegCapabilities.ValidateShaderPreflight(new ProcessResult(0, "", "[encoder] deprecated pixel format warning"));
            return Task.CompletedTask;
        });
        await Test("Anime4K runs the same ten CNN passes as bounded compute dispatches", () =>
        {
            foreach (string model in new[]
            {
                "anime4k-cnn",
                "anime4k-rcas"
            }

            )
            {
                string stage = Path.Combine(Root, "compute-contract-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                var options = new ExportOptions
                {
                    SuperResolutionEnabled = true,
                    UpscaleAlgorithm = model
                };
                UpscaleShaders.Stage(stage, options);
                string shader = File.ReadAllText(Path.Combine(stage, UpscaleShaders.FileName(model)));
                Equal(10, Regex.Matches(shader, @"//!COMPUTE 8 8").Count, "all original CNN passes use compute");
                Equal(10, Regex.Matches(shader, @"greaterThanEqual\(id,imageSize\(out_image\)\)").Count, "rounded dispatch cannot write beyond edges");
                Equal(10, Regex.Matches(shader, @"vec4 aa_conv\(\)").Count, "FP32 reconstruction retained");
                Check(!shader.Contains("float16") && !shader.Contains("f16mat"), "precision is unchanged");
                if (model == "anime4k-rcas")
                    Check(shader.Contains("vec4 hook()") && shader.Contains("0.87"), "RCAS stage and strength retained");
                string originalStage = Path.Combine(stage, "fragment");
                Directory.CreateDirectory(originalStage);
                UpscaleShaders.Stage(originalStage, options with { UseComputeUpscale = false });
                string original = File.ReadAllText(Path.Combine(originalStage, UpscaleShaders.FileName(model)));
                Check(!original.Contains("//!COMPUTE"), "fragment fallback retained");
                Equal(string.Join("\n", Regex.Matches(original, @"mat4\([^\r\n]+|result \+= vec4\([^\r\n]+").Select(m => m.Value)), string.Join("\n", Regex.Matches(shader, @"mat4\([^\r\n]+|result \+= vec4\([^\r\n]+").Select(m => m.Value)), "all convolution weights and arithmetic unchanged");
            }

            return Task.CompletedTask;
        });
        await Test("compute preflight fallback retains the exact model and selected adapter", async () =>
        {
            var options = new ExportOptions
            {
                SuperResolutionEnabled = true,
                UpscaleAlgorithm = "anime4k-rcas",
                UpscaleGpuIndex = 5,
                RenderGpuVendorId = 0x1002,
                RenderGpuDeviceId = 0x744c
            };
            var attempts = new List<ExportOptions>();
            var chosen = await FfmpegCapabilities.ChooseShaderExecutionAsync(options, candidate =>
            {
                attempts.Add(candidate);
                if (candidate.UseComputeUpscale)
                    throw new ExportException("upscaler_unavailable", "unsupported compute");
                return Task.CompletedTask;
            });
            Equal(2, attempts.Count, "exactly one original fragment retry");
            Equal(options with { UseComputeUpscale = false }, chosen, "model/strength/dimensions/GPU/quality retained");
            int count = 0;
            try
            {
                await FfmpegCapabilities.ChooseShaderExecutionAsync(options, _ =>
                {
                    count++;
                    throw new ExportException("cancelled", "cancel");
                });
                throw new Exception("cancel was swallowed");
            }
            catch (ExportException error)when (error.Code == "cancelled")
            {
            }

            Equal(1, count, "cancel cannot restart GPU work");
            count = 0;
            try
            {
                await FfmpegCapabilities.ChooseShaderExecutionAsync(options, _ =>
                {
                    count++;
                    throw new ExportException("upscaler_unavailable", "driver missing");
                });
                throw new Exception("missing GPU was swallowed");
            }
            catch (ExportException error)when (error.Code == "upscaler_unavailable")
            {
            }

            Equal(2, count, "failure of both implementations remains an error");
        });
    }
}
