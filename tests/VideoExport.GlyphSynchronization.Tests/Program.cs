using AAVideoExport.NativePath.Tests;
using System.Text.Json;

if(args.Length==4 && (args[0]=="--inspect-native" || args[0]=="--compare-native"))
{
    NativeGlyphEvidence.Run(args[0],args[1],args[2],args[3]);return;
}

string root = Path.Combine(Path.GetTempPath(), "aave-glyph-comparison-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Test(string name, Action run) { run(); passed++; Console.WriteLine("PASS " + name); }
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
GlyphFrameSet Set() => new()
{
    Width = 8, Height = 8, FrameCount = 1,
    Frames = new() { new() { Frame = 0, CameraState = "{\"orthographic\":false,\"nearClipPlane\":0.1,\"farClipPlane\":100}", CullingMask = 1, Viewport = new[] { 0f, 0f, 8f, 8f }, Labels = new() { Label() } } }
};
GlyphLabelSnapshot Label() => new()
{
    Key = "scene/player/label[0]", InvariantState = "font:7;text:0;size:16;style:normal;transform:identity;color:white",
    Shader = "GUI/Text Shader", Clipping = "None", Layer = 0, Active = true, Enabled = true,
    RendererEnabled = true, RendererVisible = true, OriginalGeometry = true, AtlasWidth = 512, AtlasHeight = 1024,
    Vertices = new[] { 2f,2f,0f, 2f,6f,0f, 6f,6f,0f, 6f,2f,0f },
    Colors = Enumerable.Repeat(1f, 16).ToArray(), Uvs = new[] { .125f,.125f, .125f,.25f, .25f,.25f, .25f,.125f },
    Projected = new[] { 2f,2f, 2f,6f, 6f,6f, 6f,2f }, Depths = new[] { 1f,1f,1f,1f }
};
T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
void Shift(GlyphFrameSet data, float x = 1f / 512, float y = 2f / 1024)
{
    // Use exactly representable normalized atlas coordinates, like the real font atlas.
    var uv = data.Frames[0].Labels[0].Uvs;
    for (int i = 0; i < uv.Length; i += 2) { uv[i] += x; uv[i + 1] += y; }
}
byte[] Pixels() => Enumerable.Repeat((byte)100, 8 * 8 * 4).ToArray();
void Delta(byte[] bytes, int x, int y, int channel, int delta) => bytes[(y * 8 + x) * 4 + channel] = checked((byte)(100 + delta));
GlyphComparisonResult Compare(byte[] a, byte[] b, GlyphFrameSet aa, GlyphFrameSet bb)
{
    string first = Path.Combine(root, "a.rgba"), second = Path.Combine(root, "b.rgba");
    File.WriteAllBytes(first, a); File.WriteAllBytes(second, b);
    return GlyphSynchronizationComparison.CompareFiles(first, second, aa, bb);
}
void Reject(Action run)
{
    try { run(); } catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Expected strict glyph comparison rejection.");
}
try
{
    Test("equal original RGBA remains exact without a glyph exception", () =>
    {
        var a = Set(); a.Frames[0].Labels.Clear();
        var result = Compare(Pixels(), Pixels(), a, Clone(a)); Check(result.RawExact && result.ChangedPixels == 0, "not exact");
    });
    Test("integer atlas translation permits only bounded original glyph pixel changes including alpha", () =>
    {
        var a = Set(); var b = Clone(a); Shift(b); var pixels = Pixels();
        Delta(pixels, 3, 3, 0, 1); Delta(pixels, 3, 3, 1, -1); Delta(pixels, 4, 4, 2, 2); Delta(pixels, 4, 4, 3, -2);
        var r = Compare(Pixels(), pixels, a, b);
        Check(!r.RawExact && r.ChangedPixels == 2 && r.ChangedChannels == 4 && r.MaxChannelDifference == 2, "wrong bounded counts");
    });
    Test("directly observed manual camera proof retains strict glyph-only equivalence", () =>
    {
        var a=Set();a.Frames[0].Proof="manual-camera-render-v1";var b=Clone(a);Shift(b);var pixels=Pixels();
        Delta(pixels,3,3,0,1);Check(!Compare(Pixels(),pixels,a,b).RawExact,"manual-camera proof was not checked");
        Delta(pixels,1,3,0,1);Reject(()=>Compare(Pixels(),pixels,a,b));
        pixels=Pixels();Delta(pixels,3,3,0,1);b.Frames[0].Proof="builtin-manual-postrender-v1";Reject(()=>Compare(Pixels(),pixels,a,b));
    });
    Test("native Unlit Text shader obeys the same original-triangle bounded rule", () =>
    {
        var a=Set();a.Frames[0].Labels[0].Shader="Unlit/Text";var b=Clone(a);Shift(b);var p=Pixels();Delta(p,3,3,0,2);
        var result=Compare(Pixels(),p,a,b);Check(result.ChangedPixels==1 && result.MaxChannelDifference==2,"native text shader did not use bounded rule");
        Delta(p,1,3,0,1);Reject(()=>Compare(Pixels(),p,a,b));
        p=Pixels();Delta(p,3,3,0,3);Reject(()=>Compare(Pixels(),p,a,b));
    });
    Test("unknown changed-UV shaders and cross-run shader changes remain rejected", () =>
    {
        var a=Set();a.Frames[0].Labels[0].Shader="Unlit/UnknownText";var b=Clone(a);Shift(b);var p=Pixels();Delta(p,3,3,0,1);
        Reject(()=>Compare(Pixels(),p,a,b));
        a=Set();b=Clone(a);Shift(b);b.Frames[0].Labels[0].Shader="Unlit/Text";Reject(()=>Compare(Pixels(),p,a,b));
    });
    Test("identical unchanged-UV unsupported labels never contribute to the allowed mask", () =>
    {
        var a=Set();var untouched=Label();untouched.Key="unrelated-unchanged-label";untouched.Shader="UnknownShader";
        untouched.Projected=new[]{0f,0f,0f,8f,8f,8f,8f,0f};a.Frames[0].Labels.Add(untouched);
        var b=Clone(a);Shift(b);var p=Pixels();Delta(p,3,3,0,1);Compare(Pixels(),p,a,b);
        Delta(p,1,1,0,1);Reject(()=>Compare(Pixels(),p,a,b));
        p=Pixels();Delta(p,3,3,0,1);b.Frames[0].Labels[1].InvariantState+=";changed";Reject(()=>Compare(Pixels(),p,a,b));
    });
    Test("orthographic zero and negative glyph depths strictly inside native clip planes are valid", () =>
    {
        foreach(float depth in new[]{0f,-2f})
        {
            var a=Set();a.Frames[0].CameraState="{\"orthographic\":true,\"nearClipPlane\":-10,\"farClipPlane\":10}";
            a.Frames[0].Labels[0].Depths=Enumerable.Repeat(depth,4).ToArray();var b=Clone(a);Shift(b);var p=Pixels();Delta(p,3,3,0,1);
            Check(Compare(Pixels(),p,a,b).ChangedPixels==1,"orthographic in-volume glyph rejected");
        }
    });
    Test("perspective behind-camera and orthographic crossing or touching clip planes remain rejected", () =>
    {
        foreach(var sample in new[]{(Ortho:false,Depth:0f),(Ortho:false,Depth:-2f),(Ortho:true,Depth:-11f),(Ortho:true,Depth:11f),(Ortho:true,Depth:-10f),(Ortho:true,Depth:10f)})
        {
            var a=Set();if(sample.Ortho)a.Frames[0].CameraState="{\"orthographic\":true,\"nearClipPlane\":-10,\"farClipPlane\":10}";
            a.Frames[0].Labels[0].Depths[0]=sample.Depth;var b=Clone(a);Shift(b);var p=Pixels();Delta(p,3,3,0,1);Reject(()=>Compare(Pixels(),p,a,b));
        }
    });
    Test("missing invalid or perspective-negative clip-plane metadata fails closed", () =>
    {
        foreach(string state in new[]{"{}","{\"orthographic\":true,\"nearClipPlane\":10,\"farClipPlane\":-10}","{\"orthographic\":false,\"nearClipPlane\":-10,\"farClipPlane\":10}"})
        {
            var a=Set();a.Frames[0].CameraState=state;var b=Clone(a);Shift(b);var p=Pixels();Delta(p,3,3,0,1);Reject(()=>Compare(Pixels(),p,a,b));
        }
    });
    Test("one nonfont pixel difference fails even with a legitimate glyph difference", () =>
    {
        var a = Set(); var b = Clone(a); Shift(b); var p = Pixels(); Delta(p, 3, 3, 0, 1); Delta(p, 1, 3, 0, 1);
        Reject(() => Compare(Pixels(), p, a, b));
    });
    Test("three code values within a glyph fail", () =>
    {
        var a = Set(); var b = Clone(a); Shift(b); var p = Pixels(); Delta(p, 3, 3, 3, 3); Reject(() => Compare(Pixels(), p, a, b));
    });
    Test("zero UV displacement cannot authorize even a one-code-value glyph difference", () =>
    {
        var a = Set(); var p = Pixels(); Delta(p, 3, 3, 0, 1); Reject(() => Compare(Pixels(), p, a, Clone(a)));
    });
    Test("fractional translation UV scaling and corner shear fail", () =>
    {
        foreach (int mode in new[] { 0, 1, 2 })
        {
            var a = Set(); var b = Clone(a); Shift(b); var u = b.Frames[0].Labels[0].Uvs;
            if (mode == 0) for (int i = 0; i < u.Length; i += 2) u[i] += .5f / 512;
            if (mode == 1) for (int i = 0; i < u.Length; i += 2) u[i] *= 2;
            if (mode == 2) u[0] += 1f / 512;
            var p = Pixels(); Delta(p, 3, 3, 0, 1); Reject(() => Compare(Pixels(), p, a, b));
        }
    });
    Test("changed text font settings color vertices transform and projected geometry fail", () =>
    {
        foreach (Action<GlyphLabelSnapshot> mutate in new Action<GlyphLabelSnapshot>[]
        {
            x => x.InvariantState += ";different-text", x => x.InvariantState += ";font:8",
            x => x.Colors[0] = .99f, x => x.Vertices[0] += .01f,
            x => x.Projected[0] += .01f, x => x.InvariantState += ";world-transform-changed",
            x => x.AtlasWidth = 1024, x => x.Shader = "another-shader", x => x.Clipping = "SoftClip"
        })
        {
            var a = Set(); var b = Clone(a); Shift(b); mutate(b.Frames[0].Labels[0]);
            var p = Pixels(); Delta(p, 3, 3, 0, 1); Reject(() => Compare(Pixels(), p, a, b));
        }
    });
    Test("rotated glyph triangles never permit the unused corners of their bounding box", () =>
    {
        var a = Set(); a.Frames[0].Labels[0].Projected = new[] { 4f,1f, 1f,4f, 4f,7f, 7f,4f };
        var b = Clone(a); Shift(b); var good = Pixels(); Delta(good, 3, 3, 0, 1); Compare(Pixels(), good, a, b);
        var bad = Pixels(); Delta(bad, 1, 1, 0, 1); Reject(() => Compare(Pixels(), bad, a, b));
    });
    Test("actual viewport restricts glyph coverage without dilation", () =>
    {
        var a = Set(); a.Frames[0].Viewport = new[] { 3f,0f, 5f,8f }; var b = Clone(a); Shift(b);
        var p = Pixels(); Delta(p, 2, 3, 0, 1); Reject(() => Compare(Pixels(), p, a, b));
        p = Pixels(); Delta(p, 3, 3, 0, 1); Compare(Pixels(), p, a, b);
    });
    Test("excluded overlay layer inactive renderer and transparent glyphs cannot mask changes", () =>
    {
        foreach (int mode in new[] { 0, 1, 2, 3, 4 })
        {
            var a = Set(); var l = a.Frames[0].Labels[0];
            if (mode == 0) l.Layer = 2;
            if (mode == 1) l.RendererEnabled = false;
            if (mode == 2) l.Active = false;
            if (mode == 3) l.RendererVisible = false;
            if (mode == 4) for (int i = 3; i < l.Colors.Length; i += 4) l.Colors[i] = 0;
            var b = Clone(a); Shift(b); var p = Pixels(); Delta(p, 3, 3, 0, 1); Reject(() => Compare(Pixels(), p, a, b));
        }
    });
    Test("shifted duplicate missing or failed-render frame metadata fails", () =>
    {
        foreach (int mode in new[] { 0, 1, 2, 3 })
        {
            var a = Set(); var b = Clone(a); Shift(b);
            if (mode == 0) b.Frames[0].Frame = 1;
            if (mode == 1) b.Frames.Add(Clone(b.Frames[0]));
            if (mode == 2) b.Frames.Clear();
            if (mode == 3) b.Errors.Add("render did not complete");
            Reject(() => Compare(Pixels(), Pixels(), a, b));
        }
    });
    Test("unknown projection clipping shader custom geometry nonfinite or duplicate label fails", () =>
    {
        foreach (int mode in new[] { 0, 1, 2, 3, 4, 5 })
        {
            var a = Set(); var b = Clone(a); Shift(b);
            if (mode == 0) b.Frames[0].Proof = "srp-not-observed";
            if (mode == 1) b.Frames[0].Labels[0].Depths[0] = -1;
            if (mode == 2) b.Frames[0].Labels[0].Clipping = "TextureMask";
            if (mode == 3) b.Frames[0].Labels[0].OriginalGeometry = false;
            if (mode == 4) b.Frames[0].Labels[0].Projected[0] = float.NaN;
            if (mode == 5) b.Frames[0].Labels.Add(Clone(b.Frames[0].Labels[0]));
            var p = Pixels(); Delta(p, 3, 3, 0, 1); Reject(() => Compare(Pixels(), p, a, b));
        }
    });
    Test("raw truncation dimension or origin mismatch fails without image tolerance", () =>
    {
        var a = Set(); Reject(() => Compare(Pixels(), Pixels()[..^1], a, Clone(a)));
        var b = Clone(a); b.Width++; Reject(() => Compare(Pixels(), Pixels(), a, b));
        b = Clone(a); b.RawOrigin = "top-left"; Reject(() => Compare(Pixels(), Pixels(), a, b));
    });
    Test("atlas-wrapping UV coordinates cannot authorize a glyph exception", () =>
    {
        var a = Set(); var b = Clone(a); Shift(b, 2, 0); var p = Pixels(); Delta(p, 3, 3, 0, 1);
        Reject(() => Compare(Pixels(), p, a, b));
    });
    Console.WriteLine($"{passed}/21 glyph synchronization behavior groups passed; native collection still requires AA verification");
}
finally { Directory.Delete(root, recursive: true); }
