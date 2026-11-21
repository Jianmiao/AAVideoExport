using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestDirect3DQuality()
    {
        foreach (string encoder in new[] { "h264_nvenc", "h264_amf" })
        await Test(encoder + " actual D3D: orientation, zero sharpening and model activation", () => {
            var options = Options("d3d-quality-contract") with {
                Width=1080, Height=1080, Fps=30, SuperResolutionEnabled=true,
                UpscaleAlgorithm="anime4k-cnn", AudioQuality="none", WriteCover=false
            };
            var canvas = options.GetCanvasLayout();
            int row=canvas.CaptureWidth*4;
            byte[] input=new byte[row*canvas.CaptureHeight];
            for(int y=0;y<canvas.CaptureHeight;y++) for(int x=0;x<canvas.CaptureWidth;x++) {
                int at=y*row+x*4;
                input[at]=(byte)(x*255/canvas.CaptureWidth);
                input[at+1]=(byte)(y*255/canvas.CaptureHeight);
                input[at+2]=(byte)(((x/7+y/7)%2)*190+32);
                input[at+3]=17; // Alpha does not participate in opaque video reconstruction.
            }
            byte[] plain;
            using(var gpu=new Direct3DAnimeUpscaler(options,encoder,false,useNv12:false))
                plain=gpu.Process(input).ToArray();
            using(var gpu=new Direct3DAnimeUpscaler(options with {UpscaleAlgorithm="anime4k-rcas",RcasSharpness=0},encoder,false,useNv12:false))
                Check(plain.SequenceEqual(gpu.Process(input)),"RCAS zero is an exact bypass of sharpening");
            using(var gpu=new Direct3DAnimeUpscaler(options with {UpscaleAlgorithm="anime4k-rcas",RcasSharpness=.87},encoder,false,useNv12:false)) {
                byte[] sharp=gpu.Process(input);
                int changed=plain.Zip(sharp,(a,b)=>a!=b?1:0).Sum();
                Check(changed>plain.Length/100,"nonzero sharpening changes actual edge pixels");
            }
            byte[] reversed=new byte[input.Length];
            for(int y=0;y<canvas.CaptureHeight;y++) Buffer.BlockCopy(input,y*row,reversed,(canvas.CaptureHeight-1-y)*row,row);
            byte[] expected;
            using(var gpu=new Direct3DAnimeUpscaler(options,encoder,false,useNv12:false))
                expected=gpu.Process(reversed).ToArray();
            using(var gpu=new Direct3DAnimeUpscaler(options,encoder,true,useNv12:false))
                Check(expected.SequenceEqual(gpu.Process(input)),"vertical orientation is applied before the CNN, byte exactly");
            Check(Enumerable.Range(0,plain.Length/4).All(i=>plain[i*4+3]==255),"all delivered pixels are opaque");
            return Task.CompletedTask;
        });
    }
}
