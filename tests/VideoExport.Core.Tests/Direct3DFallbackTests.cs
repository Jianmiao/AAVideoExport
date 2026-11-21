using System.Diagnostics;
using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private const string D3DFixtureVariable = "AA_VIDEO_TEST_D3D_FALLBACK";

    private static int RunDirect3DFixture(string[] args)
    {
        string folder = Environment.GetEnvironmentVariable(D3DFixtureVariable)!;
        string mode = File.ReadAllText(Path.Combine(folder, "mode.txt"));
        File.WriteAllText(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(args));
        bool preflight = args.Contains("lavfi") && args.Contains("null");
        bool vulkan = args.Contains("-init_hw_device");
        bool nv12 = args.Contains("nv12");
        if (preflight && !vulkan && (nv12 || mode == "reject-d3d"))
        {
            Console.Error.WriteLine("Controlled fixture: driver rejects " + (nv12 ? "NV12" : "D3D RGBA") + " input.");
            return 74;
        }
        var start = new ProcessStartInfo(File.ReadAllText(Path.Combine(folder, "ffmpeg.txt")))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        _ = Task.Run(async () => {
            try { await Console.OpenStandardInput().CopyToAsync(process.StandardInput.BaseStream); }
            catch (IOException) { }
            finally { process.StandardInput.Close(); }
        });
        var output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        var error = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        process.WaitForExit();
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        return process.ExitCode;
    }

    private static async Task TestDirect3DFallback()
    {
        foreach (string mode in new[] { "reject-nv12", "reject-d3d" })
        await Test("actual " + mode + " preflight retains the model, GPU and permits encoding", async () => {
            string folder = Path.Combine(Root, mode + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "mode.txt"), mode);
            File.WriteAllText(Path.Combine(folder, "ffmpeg.txt"), ffmpeg);
            string? before = Environment.GetEnvironmentVariable(D3DFixtureVariable);
            Environment.SetEnvironmentVariable(D3DFixtureVariable, folder);
            try
            {
                var options = Options("fallback-" + mode) with {
                    Width=1920, Height=1080, Fps=30, AudioQuality="none", WriteCover=false,
                    Encoder="h264_nvenc", SuperResolutionEnabled=true, UpscaleAlgorithm="anime4k-rcas",
                    RenderGpuVendorId=0x10de
                };
                string fixture = Path.Combine(AppContext.BaseDirectory, "VideoExport.Core.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
                using var session = new ExportSession(options, fixture, ffprobe, "h264_nvenc", flipVertical:false);
                Equal(mode == "reject-nv12" ? "d3d11" : "compute8", session.UpscaleExecution, "the real fallback was used before capture");
                Equal(0L, session.FramesWritten, "preflight does not advance source frames");
                Check(session.UpscaleFallbackReason.Contains("Controlled fixture"), "driver failure is recorded");
                var canvas = options.GetCanvasLayout();
                byte[] pixels = new byte[canvas.CaptureWidth*canvas.CaptureHeight*4];
                for(int i=0;i<pixels.Length;i+=4) {pixels[i]=70; pixels[i+1]=140; pixels[i+2]=210; pixels[i+3]=255;}
                for(int i=0;i<3;i++) session.WriteFrame(pixels, pixels.Length);
                var result = session.Complete();
                using var metadata = await Probe(result.OutputPath);
                Equal("3", Property(Stream(metadata,"video"),"nb_read_frames"), "fallback encodes all source frames");
                var attempts = Directory.GetFiles(folder,"*.json").Select(p=>JsonSerializer.Deserialize<string[]>(File.ReadAllText(p))!).ToArray();
                Check(attempts.Any(a=>a.Contains("nv12") && a.Contains("lavfi")), "NV12 was actually attempted");
                Check(attempts.Any(a=>a.Contains("lavfi") && !a.Contains("nv12") && !a.Contains("-init_hw_device")), "same-model RGBA was actually attempted");
                foreach (var attempt in attempts.Where(a=>a.Contains("-init_hw_device"))) {
                    Check(attempt.Contains("h264_nvenc"), "fallback retains the selected hardware encoder");
                    Check(attempt.Any(a=>a.Contains("AA_Anime4K_RCAS.glsl")), "fallback retains Anime4K and RCAS");
                    Check(!attempt.Any(a=>a.Contains("libx264")), "no silent software substitution");
                }
            }
            finally {Environment.SetEnvironmentVariable(D3DFixtureVariable, before);}
        });
    }
}
