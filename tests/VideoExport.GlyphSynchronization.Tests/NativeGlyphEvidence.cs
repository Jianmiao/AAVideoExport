using System.Text.Json;
using AAVideoExport.NativePath.Tests;

internal static class NativeGlyphEvidence
{
    internal static void Run(string mode, string firstDirectory, string secondDirectory, string output)
    {
        var a=JsonSerializer.Deserialize<GlyphFrameSet>(File.ReadAllText(Path.Combine(firstDirectory,"glyph-frames.json")))!;
        var b=JsonSerializer.Deserialize<GlyphFrameSet>(File.ReadAllText(Path.Combine(secondDirectory,"glyph-frames.json")))!;
        string ar=Path.Combine(firstDirectory,"all-frames.rgba"),br=Path.Combine(secondDirectory,"all-frames.rgba");
        object result=mode=="--compare-native" ? GlyphSynchronizationComparison.CompareFiles(ar,br,a,b) : Inspect(ar,br,a,b);
        string json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(output,json);
        Console.WriteLine(json);
    }

    // Diagnostic only: determine whether the exact native shader name omitted
    // from the whitelist has the same bounded evidence. This does not return
    // the authoritative comparison verdict; --compare-native calls the actual
    // source-linked comparator, without replacing saved native failure reports.
    private static object Inspect(string firstRaw,string secondRaw,GlyphFrameSet a,GlyphFrameSet b)
    {
        if(a.FrameCount!=b.FrameCount || a.Width!=b.Width || a.Height!=b.Height || a.Errors.Count!=0 || b.Errors.Count!=0)
            throw new InvalidDataException("Native streams or observations are incomplete.");
        int frameBytes=checked(a.Width*a.Height*4);
        using var left=File.OpenRead(firstRaw);using var right=File.OpenRead(secondRaw);
        if(left.Length!=(long)frameBytes*a.FrameCount || right.Length!=left.Length)throw new InvalidDataException("Native raw byte counts differ.");
        var aa=new byte[frameBytes];var bb=new byte[frameBytes];
        long changedPixels=0,changedChannels=0,outside=0,overflow=0,changedFrames=0,changedQuads=0;
        int maximum=0;bool invariantExact=true,translationsExact=true;
        var attribution=new Dictionary<string,long>(StringComparer.Ordinal);
        var examples=new List<object>();
        for(int frame=0;frame<a.FrameCount;frame++)
        {
            Read(left,aa);Read(right,bb);
            var af=a.Frames[frame];var bf=b.Frames[frame];
            invariantExact &= af.Frame==bf.Frame && af.Proof==bf.Proof && af.CameraState==bf.CameraState
                && af.CullingMask==bf.CullingMask && af.Viewport.AsSpan().SequenceEqual(bf.Viewport) && af.Labels.Count==bf.Labels.Count;
            var other=bf.Labels.ToDictionary(label=>label.Key,StringComparer.Ordinal);
            var quads=new List<(string Key,string Shader,float[] Points)>();
            foreach(var label in af.Labels)
            {
                if(!other.TryGetValue(label.Key,out var r)){invariantExact=false;continue;}
                invariantExact &= label.InvariantState==r.InvariantState && label.Shader==r.Shader && label.Clipping==r.Clipping
                    && label.OriginalGeometry==r.OriginalGeometry && label.Layer==r.Layer && label.Active==r.Active && label.Enabled==r.Enabled
                    && label.RendererEnabled==r.RendererEnabled && label.RendererVisible==r.RendererVisible
                    && label.AtlasWidth==r.AtlasWidth && label.AtlasHeight==r.AtlasHeight
                    && label.Vertices.AsSpan().SequenceEqual(r.Vertices) && label.Colors.AsSpan().SequenceEqual(r.Colors)
                    && label.Projected.AsSpan().SequenceEqual(r.Projected) && label.Depths.AsSpan().SequenceEqual(r.Depths);
                if(label.Uvs.Length!=r.Uvs.Length){translationsExact=false;continue;}
                for(int i=0;i<label.Uvs.Length;i+=8)
                {
                    if(label.Uvs.AsSpan(i,8).SequenceEqual(r.Uvs.AsSpan(i,8)))continue;
                    double dx=(double)r.Uvs[i]-label.Uvs[i],dy=(double)r.Uvs[i+1]-label.Uvs[i+1];
                    bool integral=(dx!=0 || dy!=0) && dx*label.AtlasWidth==Math.Truncate(dx*label.AtlasWidth)
                        && dy*label.AtlasHeight==Math.Truncate(dy*label.AtlasHeight);
                    for(int j=0;j<8;j+=2)integral &= (double)r.Uvs[i+j]-label.Uvs[i+j]==dx && (double)r.Uvs[i+j+1]-label.Uvs[i+j+1]==dy;
                    translationsExact &= integral;
                    if(!integral || !label.OriginalGeometry || label.Clipping!="None" || !label.Active || !label.Enabled
                        || !label.RendererEnabled || !label.RendererVisible || (af.CullingMask&(1<<label.Layer))==0
                        || !Enumerable.Range(i/2,4).Any(v=>label.Colors[v*4+3]>0))continue;
                    changedQuads++;
                    quads.Add((label.Key+"/quad-"+i/8,label.Shader,label.Projected.AsSpan(i,8).ToArray()));
                }
            }
            if(aa.AsSpan().SequenceEqual(bb))continue;
            changedFrames++;
            for(int i=0;i<frameBytes;i+=4)
            {
                if(aa[i]==bb[i] && aa[i+1]==bb[i+1] && aa[i+2]==bb[i+2] && aa[i+3]==bb[i+3])continue;
                changedPixels++;int delta=0;
                for(int c=0;c<4;c++){int d=Math.Abs((int)aa[i+c]-bb[i+c]);if(d!=0)changedChannels++;delta=Math.Max(delta,d);}
                maximum=Math.Max(maximum,delta);if(delta>2)overflow++;
                int p=i/4,x=p%a.Width,y=p/a.Width;
                var match=quads.FirstOrDefault(q=>Covers(q.Points,af.Viewport,x+.5,y+.5));
                if(match.Key==null)outside++;
                else attribution[match.Shader]=attribution.GetValueOrDefault(match.Shader)+1;
                if(examples.Count<8)examples.Add(new{Frame=frame,X=x,Y=y,Delta=delta,match.Shader,match.Key});
            }
        }
        return new {DiagnosticOnly=true,Frames=a.FrameCount,a.Width,a.Height,AllNonUvLabelAndCameraMetadataExact=invariantExact,
            AllChangedUvsExactIntegerTranslations=translationsExact,ChangedUvQuads=changedQuads,ChangedFrames=changedFrames,
            ChangedPixels=changedPixels,ChangedChannels=changedChannels,MaxChannelDifference=maximum,
            PixelsOutsideOriginalChangedUvTriangles=outside,PixelsAboveTwo=overflow,ShaderPixelAttribution=attribution,Examples=examples};
    }
    private static void Read(Stream stream,byte[] bytes){int n=0;while(n<bytes.Length){int read=stream.Read(bytes,n,bytes.Length-n);if(read==0)throw new EndOfStreamException();n+=read;}}
    private static bool Covers(float[] p,float[] view,double x,double y)=>x>=view[0] && y>=view[1] && x<view[0]+view[2] && y<view[1]+view[3]
        && (Triangle(x,y,p[0],p[1],p[2],p[3],p[4],p[5]) || Triangle(x,y,p[0],p[1],p[4],p[5],p[6],p[7]));
    private static bool Triangle(double x,double y,double ax,double ay,double bx,double by,double cx,double cy)
    {double a=Area(ax,ay,bx,by,x,y),b=Area(bx,by,cx,cy,x,y),c=Area(cx,cy,ax,ay,x,y);return a>=0 && b>=0 && c>=0 || a<=0 && b<=0 && c<=0;}
    private static double Area(double ax,double ay,double bx,double by,double cx,double cy)=>(bx-ax)*(cy-ay)-(by-ay)*(cx-ax);
}
