using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace AAVideoExport.Core;
// Uses bundled Anime4K weights (bloc97, MIT) and AMD RCAS (MIT).
// Separate device owned by serialized encoder; no Unity resources.
internal sealed class Direct3DAnimeUpscaler : IDisposable, IQueuedFrameProcessor
{
    private readonly ID3D11Device Device;
    private readonly ID3D11DeviceContext Context;
    readonly List<IDisposable> owned = new();
    readonly List<(ID3D11ComputeShader Shader, ID3D11ShaderResourceView[] Inputs, ID3D11UnorderedAccessView Output, int W, int H)> passes = new();
    readonly ID3D11Texture2D source;
    readonly ID3D11Texture2D[] readbacks;
    readonly Queue<int> pending = new();
    int nextSlot;
    public int Capacity => readbacks.Length;
    public int PendingFrames => pending.Count;
    readonly ID3D11Texture2D result;
    readonly ID3D11SamplerState sampler;
    readonly int width, height, outputWidth, outputHeight;
    readonly int readbackRows, readbackRowBytes;
    readonly ID3D11ShaderResourceView[] emptyInputs = new ID3D11ShaderResourceView[4];
    bool disposed;
    public string AdapterName { get; }
    public bool Nv12Output { get; }

    T Own<T>(T value)
        where T : IDisposable
    {
        owned.Add(value);
        return value;
    }

    ID3D11Texture2D Texture(int w, int h, Format format, BindFlags bind = BindFlags.ShaderResource | BindFlags.UnorderedAccess, ResourceUsage usage = ResourceUsage.Default, CpuAccessFlags cpu = CpuAccessFlags.None) => Own(Device.CreateTexture2D(new Texture2DDescription(format, w, h, 1, 1, bind, usage, cpu)));
    void Pass(string name, string code, ID3D11Texture2D[] inputs, ID3D11Texture2D output, int w, int h)
    {
        if (name == "resizeX")
        {
            int radius = (int)Math.Ceiling(3.0 * width * 2 / outputWidth);
            code = code.Replace("for(int dx=-3;dx<=4;dx++)", "for(int dx=" + (1 - radius) + ";dx<=" + radius + ";dx++)");
        }

        if (name == "resizeY")
        {
            int radius = (int)Math.Ceiling(3.0 * height * 2 / outputHeight);
            code = code.Replace("for(int dy=-3;dy<=4;dy++)", "for(int dy=" + (1 - radius) + ";dy<=" + radius + ";dy++)");
        }

        if (!name.StartsWith("conv", StringComparison.Ordinal))
            code = Regex.Replace(code, @"\b(1280|720|2560|1440|1920|1080|2559|1439)\b", m => m.Value switch
            {
                "1280" => width.ToString(),
                "720" => height.ToString(),
                "2560" => (width * 2).ToString(),
                "1440" => (height * 2).ToString(),
                "1920" => outputWidth.ToString(),
                "1080" => outputHeight.ToString(),
                "2559" => (width * 2 - 1).ToString(),
                _ => (height * 2 - 1).ToString()});
        string path = "AA_Anime4K_" + name + ".hlsl";
        var bytes = Compiler.Compile(code, "main", path, "cs_5_0", ShaderFlags.OptimizationLevel3);
        var shader = Own(Device.CreateComputeShader(bytes.Span));
        var srv = inputs.Select(t => Own(Device.CreateShaderResourceView(t))).ToArray();
        var uav = Own(Device.CreateUnorderedAccessView(output));
        passes.Add((shader, srv, uav, w, h));
    }

    static string Header(string[] names, int w, int h) => string.Join("\n", names.Select((n, i) => "Texture2D<float4> " + n + ":register(t" + i + ");")) + "\nSamplerState aa_sampler:register(s0);RWTexture2D<float4> aa_out:register(u0);static uint2 aa_id;\n";
    static string Entry(string body, int w, int h) => "[numthreads(8,8,1)]void main(uint3 id:SV_DispatchThreadID){if(id.x>=" + w + "||id.y>=" + h + ")return;aa_id=id.xy;" + body + "}\n";
    public Direct3DAnimeUpscaler(ExportOptions options, string encoder, bool flipVertical, bool useNv12 = true, int queueDepth = 1)
    {
        if (queueDepth is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(queueDepth));
        var canvas = options.GetCanvasLayout();
        width = canvas.CaptureWidth;
        height = canvas.CaptureHeight;
        outputWidth = canvas.OutputWidth;
        outputHeight = canvas.OutputHeight;
        int vendor = encoder.EndsWith("_nvenc", StringComparison.Ordinal) ? 0x10de : encoder.EndsWith("_amf", StringComparison.Ordinal) ? 0x1002 : encoder.EndsWith("_qsv", StringComparison.Ordinal) ? 0x8086 : options.RenderGpuVendorId;
        int deviceId = vendor == options.RenderGpuVendorId ? options.RenderGpuDeviceId : 0;
        // Four bytes per RGBA texel pack four consecutive NV12 bytes. Keep
        // arbitrary even canvases on RGBA when their row is not divisible by four.
        Nv12Output = useNv12 && options.UseDirect3DNv12 && outputWidth % 4 == 0;
        readbackRows = Nv12Output ? checked(outputHeight * 3 / 2) : outputHeight;
        readbackRowBytes = Nv12Output ? outputWidth : checked(outputWidth * 4);
        Output = new byte[checked(readbackRows * readbackRowBytes)];
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1 selected = null !;
        for (int i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            if (adapter.Description1.VendorId == vendor && (deviceId == 0 || adapter.Description1.DeviceId == deviceId))
            {
                selected = adapter;
                break;
            }

            adapter.Dispose();
        }

        if (selected == null)
            throw new Exception("Requested adapter missing");
        AdapterName = selected.Description1.Description;
        try
        {
            D3D11.D3D11CreateDevice(selected, DriverType.Unknown, DeviceCreationFlags.None, new[] { FeatureLevel.Level_11_0 }, out Device, out Context).CheckError();
        }
        finally
        {
            selected.Dispose();
        }

        owned.Add(Device);
        owned.Add(Context);
        try
        {
            sampler = Own(Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp)));
            source = Texture(width, height, Format.R8G8B8A8_UNorm, BindFlags.ShaderResource);
            var oriented = Texture(width, height, Format.R8G8B8A8_UNorm);
            Pass("conv_orient", Header(new[] { "src" }, width, height) + Entry("aa_out[id.xy]=float4(src.Load(int3(id.x," + (flipVertical ? (height - 1) + "-id.y" : "id.y") + ",0)).rgb,1);", width, height), new[] { source }, oriented, width, height);
            var textures = new Dictionary<string, ID3D11Texture2D>
            {
                {
                    "MAIN",
                    oriented
                }
            };
            using var stream = typeof(ExportOptions).Assembly.GetManifestResourceStream("AAVideoExport.Core.Shaders.AA_Anime4K.glsl");
            using var reader = new StreamReader(stream!);
            string original = reader.ReadToEnd();
            var blocks = original.Split(new[] { "//!DESC" }, StringSplitOptions.None);
            if (blocks.Length != 11)
                throw new ExportException("upscaler_unavailable", "The bundled Anime4K model must have exactly ten passes.");
            for (int p = 1; p < blocks.Length; p++)
            {
                string block = blocks[p];
                string[] binds = Regex.Matches(block, @"//!BIND (\w+)").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
                string save = Regex.Match(block, @"//!SAVE (\w+)").Groups[1].Value;
                bool depth = block.Contains("Depth-to-Space");
                int w = depth ? width * 2 : width, h = depth ? height * 2 : height;
                if (depth != (p == 10) || binds.Length == 0 || binds.Length > 4 || save.Length == 0)
                    throw new ExportException("upscaler_unavailable", "The bundled Anime4K pass layout is incompatible.");
                var target = Texture(w, h, Format.R16G16B16A16_Float);
                string code = Header(binds, w, h);
                if (!depth)
                {
                    string body = block.Substring(block.IndexOf("#define go_"));
                    body = Regex.Replace(body, @"(\w+)_texOff\(vec2\(x_off, y_off\)\)", m => m.Groups[1].Value + ".SampleLevel(aa_sampler,(float2(aa_id)+0.5+float2(x_off,y_off))/float2(" + width + "," + height + "),0)");
                    body = Regex.Replace(body, @"mat4\(([^)]+)\) \* (go_\d\([^)]+\))", m => "mul(" + m.Groups[2].Value + ",float4x4(" + m.Groups[1].Value + "))");
                    body = body.Replace("vec4", "float4").Replace("vec2", "float2").Replace("float4 hook()", "float4 aa_conv()");
                    code += body + "\n" + Entry("aa_out[id.xy]=aa_conv();", w, h);
                }
                else
                {
                    code += Entry("uint2 src=id.xy/2;int c=(id.y%2)*2+id.x%2;float r=conv2d_last_tf.Load(int3(src,0))[c];float g=conv2d_last_tf1.Load(int3(src,0))[c];float b=conv2d_last_tf2.Load(int3(src,0))[c];aa_out[id.xy]=float4(r,g,b,b)+MAIN.SampleLevel(aa_sampler,(float2(id.xy)+0.5)/float2(" + w + "," + h + "),0);", w, h);
                }

                Pass("conv" + p, code, binds.Select(n => textures[n]).ToArray(), target, w, h);
                textures[save] = target;
            }

            var resizedX = Texture(outputWidth, height * 2, Format.R16G16B16A16_Float);
            var resizedY = Texture(outputWidth, outputHeight, Format.R16G16B16A16_Float);
            string curves = "float aa_linear(float x){return 0.8703105327665798*pow(max(x,0.0)+0.0595848334,2.4);}float nonlinear(float x){return pow(max(x,0.0)/0.8703105327665798,1.0/2.4)-0.0595848334;}float3 lin(float3 v){return float3(aa_linear(v.x),aa_linear(v.y),aa_linear(v.z));}float3 enc(float3 v){return float3(nonlinear(v.x),nonlinear(v.y),nonlinear(v.z));}\nfloat lanczos(float x){x=abs(x);if(x>=3)return 0;if(x<1e-6)return 1;return sin(3.141592653589793*x)*sin(3.141592653589793*x/3)/(3.289868133696453*x*x);}\n";
            string resizeX = Header(new[] { "src" }, 1920, 1440) + curves + Entry("float pos=(id.x+0.5)*2560.0/1920.0-0.5;int base=(int)floor(pos);float3 sum=0;float norm=0;for(int dx=-3;dx<=4;dx++){float k=lanczos((pos-base-dx)/ (2560.0/1920.0));float3 v=src.Load(int3(clamp(base+dx,0,2559),id.y,0)).rgb;sum+=lin(v)*k;norm+=k;}aa_out[id.xy]=float4(sum/norm,1);", 1920, 1440);
            Pass("resizeX", resizeX, new[] { textures["MAIN"] }, resizedX, outputWidth, height * 2);
            string resizeY = Header(new[] { "src" }, 1920, 1080) + curves + Entry("float pos=(id.y+0.5)*1440.0/1080.0-0.5;int base=(int)floor(pos);float3 sum=0;float norm=0;for(int dy=-3;dy<=4;dy++){float k=lanczos((pos-base-dy)/(1440.0/1080.0));float3 v=src.Load(int3(id.x,clamp(base+dy,0,1439),0)).rgb;sum+=v*k;norm+=k;}aa_out[id.xy]=float4(sum/norm,1);", 1920, 1080);
            Pass("resizeY", resizeY, new[] { resizedX }, resizedY, outputWidth, outputHeight);
            using var rcasStream = typeof(ExportOptions).Assembly.GetManifestResourceStream("AAVideoExport.Core.Shaders.AA_RCAS.glsl");
            using var rcasReader = new StreamReader(rcasStream!);
            string rcas = rcasReader.ReadToEnd();
            rcas = rcas.Substring(rcas.IndexOf("#define AA_RCAS"));
            rcas = rcas.Replace("@RCAS_SHARPNESS@", EncoderCatalog.Number(options.UpscaleAlgorithm == "anime4k-rcas" ? options.RcasSharpness : 0)).Replace("vec4 hook()", "vec4 aa_rcas()");
            rcas = Regex.Replace(rcas, @"HOOKED_texOff\(vec2\(([^)]*)\)\)", m => "src.SampleLevel(aa_sampler,(float2(aa_id)+0.5+float2(" + (m.Groups[1].Value.Contains(',') ? m.Groups[1].Value : "0,0") + "))/float2(1920,1080),0)");
            rcas = Regex.Replace(rcas, @"vec3\(([^(),]+)\)", m => "((float3)(" + m.Groups[1].Value + "))");
            rcas = rcas.Replace("vec4", "float4").Replace("vec3", "float3").Replace("vec2", "float2");
            if (options.UpscaleAlgorithm != "anime4k-rcas" || options.RcasSharpness == 0)
                rcas = "float4 aa_rcas(){return src.Load(int3(aa_id,0));}";
            var rgb = Texture(outputWidth, outputHeight, Format.R8G8B8A8_UNorm);
            Pass("rcas", Header(new[] { "src" }, 1920, 1080) + curves + rcas + "\n" + Entry("float4 sharp=aa_rcas();aa_out[id.xy]=float4(enc(sharp.rgb),1);", 1920, 1080), new[] { resizedY }, rgb, outputWidth, outputHeight);
            result = rgb;
            if (Nv12Output)
            {
                int packedWidth = outputWidth / 4;
                result = Texture(packedWidth, readbackRows, Format.R8G8B8A8_UNorm);
                // Match the CPU Rec.709 conversion: horizontal 2-pixel averaging
                // and an eight-tap vertical bicubic (B=0,C=.6) chroma filter.
                // A plain 2x2 box visibly differs on fine colored edges.
                string color = "float y8(float3 c){return 16+219*dot(c,float3(0.2126,0.7152,0.0722));}" + "float3 chroma(int x,int y){float3 sum=0;const float k[8]={-0.0140625,-0.0421875,0.1125,0.44375,0.44375,0.1125,-0.0421875,-0.0140625};" + "[unroll]for(int j=0;j<8;j++){int row=clamp(y+j-3,0," + (outputHeight - 1) + ");sum+=(src.Load(int3(x,row,0)).rgb+src.Load(int3(x+1,row,0)).rgb)*0.5*k[j];}return sum;}" + "float2 uv8(float3 c){return 128+224*float2(dot(c,float3(-0.114572106,-0.385427894,0.5)),dot(c,float3(0.5,-0.454152908,-0.045847092)));}";
                string body = "int x=id.x*4;float4 bytes;if(id.y<" + outputHeight + "){bytes=float4(y8(src.Load(int3(x,id.y,0)).rgb),y8(src.Load(int3(x+1,id.y,0)).rgb),y8(src.Load(int3(x+2,id.y,0)).rgb),y8(src.Load(int3(x+3,id.y,0)).rgb));}" + "else{int y=(id.y-" + outputHeight + ")*2;bytes=float4(uv8(chroma(x,y)),uv8(chroma(x+2,y)));}aa_out[id.xy]=round(bytes)/255.0;";
                // This shader already uses the actual dimensions; skip the legacy
                // fixed-resolution replacements performed for resize/RCAS passes.
                Pass("conv_packNV12", Header(new[] { "src" }, packedWidth, readbackRows) + color + Entry(body, packedWidth, readbackRows), new[] { rgb }, result, packedWidth, readbackRows);
            }

            // Each pending frame has independent staging storage. Intermediate
            // textures are reused only on this one ordered immediate context.
            readbacks = Enumerable.Range(0, queueDepth).Select(_ => Texture(
                Nv12Output ? outputWidth / 4 : outputWidth, readbackRows,
                Format.R8G8B8A8_UNorm, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)).ToArray();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public byte[] Output { get; }

    public byte[] Process(byte[] pixels)
    {
        if (PendingFrames != 0) throw new InvalidOperationException("Readback queue must be empty for Process.");
        Submit(pixels);
        return Receive(CancellationToken.None);
    }

    // Retained as the one-frame compatibility/reference operation. Do not mix
    // it with an already populated queue: that would return the wrong frame.
    public byte[] Process(IntPtr pixels)
    {
        if (PendingFrames != 0) throw new InvalidOperationException("Readback queue must be empty for Process.");
        Submit(pixels);
        return Receive(CancellationToken.None);
    }

    public unsafe void Submit(byte[] pixels)
    {
        if (pixels == null || pixels.Length < checked(width * height * 4))
            throw new ArgumentException("Invalid RGBA input buffer.", nameof(pixels));
        fixed (byte* pointer = pixels) Submit((IntPtr)pointer);
    }

    public void Submit(IntPtr pixels)
    {
        if (disposed) throw new ObjectDisposedException(nameof(Direct3DAnimeUpscaler));
        if (pixels == IntPtr.Zero) throw new ArgumentException("A frame pointer is required.", nameof(pixels));
        if (pending.Count == readbacks.Length) throw new InvalidOperationException("Readback ring is full.");
        // D3D11 snapshots caller data before returning, so no borrowed capture
        // pointer remains live when the existing frame worker returns its buffer.
        Context.UpdateSubresource(source, 0, null, pixels, width * 4, 0);
        Context.CSSetSampler(0, sampler);
        foreach (var pass in passes)
        {
            Context.CSSetShaderResources(0, pass.Inputs);
            Context.CSSetUnorderedAccessView(0, pass.Output);
            Context.CSSetShader(pass.Shader);
            Context.Dispatch((pass.W + 7) / 8, (pass.H + 7) / 8, 1);
            Context.CSSetShaderResources(0, emptyInputs);
            Context.CSSetUnorderedAccessView(0, null!);
        }
        Context.CopyResource(readbacks[nextSlot], result);
        pending.Enqueue(nextSlot);
        nextSlot = (nextSlot + 1) % readbacks.Length;
    }

    public unsafe byte[] Receive(CancellationToken cancellationToken)
    {
        if (disposed) throw new ObjectDisposedException(nameof(Direct3DAnimeUpscaler));
        cancellationToken.ThrowIfCancellationRequested();
        if (pending.Count == 0) throw new InvalidOperationException("Readback ring is empty.");
        var readback = readbacks[pending.Peek()];
        // An in-flight native Map is driver-controlled, as in the baseline.
        // Cancellation is checked on both sides; it must never send or drain a
        // completed readback into an encoder after the user has cancelled.
        var mapped = Context.Map(readback, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixed (byte* dest = Output)
                for (int y = 0; y < readbackRows; y++)
                    Buffer.MemoryCopy((byte*)mapped.DataPointer + y * mapped.RowPitch,
                        dest + y * readbackRowBytes, readbackRowBytes, readbackRowBytes);
        }
        finally { Context.Unmap(readback, 0); }
        pending.Dequeue();
        return Output;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        pending.Clear();
        for (int i = owned.Count - 1; i >= 0; i--)
            owned[i].Dispose();
    }
}
