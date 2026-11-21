using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;
internal static partial class Program
{
    private static async Task TestDirect3DPolicy()
    {
        await Test("Direct3D is limited to supported Anime4K canvases and preserves native/other models", () =>
        {
            var options = new ExportOptions
            {
                SuperResolutionEnabled = true,
                UpscaleAlgorithm = "anime4k-rcas"
            };
            Check(Direct3DUpscalePolicy.IsEligible(options, "h264_nvenc") == OperatingSystem.IsWindows(), "NVIDIA capability candidate");
            Check(Direct3DUpscalePolicy.IsEligible(options, "h264_amf") == OperatingSystem.IsWindows(), "AMD capability candidate");
            Check(Direct3DUpscalePolicy.IsEligible(options, "h264_qsv") == OperatingSystem.IsWindows(), "Intel preflight candidate");
            foreach (var excluded in new[]
            {
                options with
                {
                    UseDirect3DUpscale = false
                },
                options with
                {
                    SuperResolutionEnabled = false
                },
                options with
                {
                    UpscaleAlgorithm = "fsr1-luma"
                },
                options with
                {
                    UpscaleAlgorithm = "bilinear"
                },
                options with
                {
                    Width = 3840,
                    Height = 2160
                }
            }

            )
                Check(!Direct3DUpscalePolicy.IsEligible(excluded, "h264_nvenc"), "keep previous backend for excluded case");
            Check(!Direct3DUpscalePolicy.IsEligible(options, "libx264"), "no unbound GPU for software encoder");
            var encoded = Direct3DUpscalePolicy.EncodingOptions(options);
            Check(!encoded.GetCanvasLayout().IsProxy && !UpscaleShaders.UsesGpu(encoded), "already-upscaled frames cannot be scaled twice");
            Equal(options.BitrateKbps, encoded.BitrateKbps, "same bitrate");
            Equal(options.Fps, encoded.Fps, "same timing");
            Equal(options.AudioQuality, encoded.AudioQuality, "same audio");
            Check(options.SuperResolutionEnabled && options.GetCanvasLayout().CaptureWidth == 1280, "caller still captures 720p");
            Check(EncoderArguments.Video(encoded, "h264_nvenc", false).Contains("p4"), "same NVENC quality preset");
            var packed = EncoderArguments.VideoNv12(encoded, "h264_nvenc").ToList();
            Check(packed.Contains("p4") && packed.Contains(options.BitrateKbps + "k"), "NV12 retains rate control and quality preset");
            Check(!packed[packed.IndexOf("-vf") + 1].Contains("scale="), "NV12 must not apply RGB conversion again");
            Equal("nv12", packed[packed.IndexOf("-pix_fmt") + 1], "NV12 encoder input");
            return Task.CompletedTask;
        });
    }
}
