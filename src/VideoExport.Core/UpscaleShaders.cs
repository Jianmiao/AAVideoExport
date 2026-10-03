namespace AAVideoExport.Core;

internal static class UpscaleShaders
{
    internal static bool UsesGpu(ExportOptions options) => options.SuperResolutionEnabled && options.GetCanvasLayout().IsProxy &&
        options.UpscaleAlgorithm is "fsr1-luma" or "anime4k-cnn" or "anime4k-rcas";

    internal static string FileName(string algorithm) => algorithm switch
    {
        "fsr1-luma" => "AA_FSR1.glsl", "anime4k-cnn" => "AA_Anime4K.glsl", "anime4k-rcas" => "AA_Anime4K_RCAS.glsl",
        _ => throw new ExportException("invalid_settings", "This algorithm does not use a GPU shader.")
    };

    internal static void Stage(string directory, ExportOptions options)
    {
        string algorithm = options.UpscaleAlgorithm;
        string name = FileName(algorithm);
        string shader = ReadResource(algorithm == "anime4k-rcas" ? "AA_Anime4K.glsl" : name);
        if (options.UseComputeUpscale && algorithm is "anime4k-cnn" or "anime4k-rcas")
            shader = ComputeAnime(shader);
        if (algorithm == "anime4k-rcas" && options.RcasSharpness > 0)
            shader += "\n" + ReadResource("AA_RCAS.glsl").Replace("@RCAS_SHARPNESS@", EncoderCatalog.Number(options.RcasSharpness));
        using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false));
        writer.Write(shader);
    }

    private static string ComputeAnime(string shader)
    {
        // Keep every weight, activation and FP32 convolution. Dispatch one
        // invocation per output texel; guard the rounded edge workgroups.
        var passes = shader.Split(new[] { "//!DESC" }, StringSplitOptions.None);
        for (int i = 1; i < passes.Length; i++)
        {
            passes[i] = System.Text.RegularExpressions.Regex.Replace(passes[i],
                @"(//!WHEN[^\r\n]+)(\r?\n)", "$1$2//!COMPUTE 8 8$2");
            passes[i] = passes[i].Replace("vec4 hook()", "vec4 aa_conv()") +
                "\nvoid hook() { ivec2 id=ivec2(gl_GlobalInvocationID.xy); " +
                "if(any(greaterThanEqual(id,imageSize(out_image)))) return; " +
                "imageStore(out_image,id,aa_conv()); }\n";
        }
        return string.Join("//!DESC", passes);
    }

    private static string ReadResource(string name)
    {
        using var resource = typeof(UpscaleShaders).Assembly.GetManifestResourceStream("AAVideoExport.Core.Shaders." + name)
            ?? throw new ExportException("upscaler_unavailable", "The bundled upscaling shader is missing.");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }
}
