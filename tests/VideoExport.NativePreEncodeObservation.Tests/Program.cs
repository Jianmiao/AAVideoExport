using System.Text.Json;
using AAVideoExport.Core;
using AAVideoExport.NativePath.Tests;
using AAVideoExport.Plugin;
using UnityEngine;

Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_TEST_FONT_ATLAS_EQUIVALENCE", "1");
string root = Path.Combine(Path.GetTempPath(), "aave-native-glyph-observer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int nextAttempt = 0, passed = 0;
void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
void Test(string name, Action run) { run(); passed++; Console.WriteLine("PASS " + name); }
GlyphFrameSet Run(Action<ExportHost, RenderTexture>? arrange = null, Action<ExportHost, RenderTexture>? render = null)
{
    int attempt = ++nextAttempt;
    var camera = new Camera { name = "capture", aspect = 1.5f, rect = new Rect(.1f, .1f, .5f, .5f) };
    var target = new RenderTexture { width = 8, height = 8 };
    var host = new ExportHost(camera, target);
    UnityEngine.Object.Labels = Array.Empty<UILabel>();
    UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline = null;
    UnityEngine.Rendering.RenderPipelineManager.currentPipeline = null;
    arrange?.Invoke(host, target);
    NativePreEncodeObservation.BeginAttempt(attempt, host, new ExportOptions(), root);
    try
    {
        if (render == null) host._native.RenderFrame(target);
        else render(host, target);
        host._frames.Capture();
        Check(camera.targetTexture == null && camera.aspect == 1.5f && camera.rect.x == .1f,
            "native render scope did not restore target/aspect/rect");
    }
    finally { NativePreEncodeObservation.FinishAttempt("managed-collector-test"); }
    return JsonSerializer.Deserialize<GlyphFrameSet>(File.ReadAllText(Path.Combine(root, $"preencode-attempt-{attempt:00}", "glyph-frames.json")))!;
}
void Rejected(GlyphFrameSet frames, string expected)
{
    Check(frames.Frames.Count == 0, "invalid render was sealed as glyph evidence");
    Check(frames.Errors.Any(e => e.Contains(expected, StringComparison.OrdinalIgnoreCase)),
        "missing observation failure: " + expected + "; actual: " + string.Join("; ", frames.Errors));
}
try
{
    Test("direct manual render without FireOnPostRender produces capture-time metadata before restoration", () =>
    {
        var result = Run((host, target) => host._native.LastCamera.DuringRender = () =>
        {
            Check(host._native.LastCamera.targetTexture == target, "render did not use bound capture target");
            Check(host._native.LastCamera.aspect == 1 && host._native.LastCamera.rect.width == 1,
                "render did not use capture projection");
        });
        Check(Camera.FireOnPostRenderCalls == 0, "test unexpectedly invoked the old callback");
        Check(result.FrameCount == 1 && result.Frames.Count == 1,
            $"manual render produced {result.FrameCount} raw frame(s) but {result.Frames.Count} glyph snapshot(s)");
        Check(result.Errors.Count == 0, string.Join("; ", result.Errors));
        Check(result.Frames[0].Viewport.SequenceEqual(new[] { 0f, 0f, 8f, 8f }), "recorded restored viewport");
    });
    Test("render failure remains a failure and cannot seal an observation", () =>
    {
        bool threw = false;
        var result = Run((host, target) => host._native.LastCamera.DuringRender = () => throw new InvalidOperationException("native render failed"),
            (host, target) => { try { host._native.RenderFrame(target); } catch (InvalidOperationException e) { threw = e.Message == "native render failed"; } });
        Check(threw, "observer suppressed or changed native render exception");
        Rejected(result, "render failed");
    });
    Test("changing projection during render fails closed", () => Rejected(Run((host, target) =>
        host._native.LastCamera.DuringRender = () => host._native.LastCamera.projectionMatrix = Matrix4x4.Changed), "projection"));
    Test("changing bound target during render fails closed", () => Rejected(Run((host, target) =>
        host._native.LastCamera.DuringRender = () => host._native.LastCamera.targetTexture = new RenderTexture()), "target"));
    Test("wrong frame binding fails closed", () => Rejected(Run((host, target) => host._captured = 1), "frame"));
    Test("unexpected render target fails closed", () => Rejected(Run(render: (host, target) =>
        host._native.RenderFrame(new RenderTexture { width = 8, height = 8 })), "target"));
    Test("a missing manual render is explicitly rejected before raw submission", () => Rejected(Run(render: (_, _) => { }), "missing"));
    Test("an excluded layer never contributes glyph observations", () =>
    {
        var result = Run((host, target) =>
        {
            host._native.LastCamera.cullingMask = 1;
            var label = new UILabel(); label.drawCall.gameObject.layer = 2;
            UnityEngine.Object.Labels = new[] { label };
        });
        Check(result.Errors.Count == 0 && result.Frames.Single().Labels.Count == 0, "excluded glyph entered observation");
    });
    Test("a visible glyph uses the temporary native viewport", () =>
    {
        var result = Run((host, target) => UnityEngine.Object.Labels = new[] { new UILabel() });
        Check(result.Errors.Count == 0, string.Join("; ", result.Errors));
        var glyph = result.Frames.Single().Labels.Single();
        Check(glyph.Projected.SequenceEqual(new[] { 2f, 2f, 2f, 6f, 6f, 6f, 6f, 2f }), "glyph projected after scope restoration");
    });
    Test("the observed URP asset manual fallback preserves the strict rendered glyph contract", () =>
    {
        var result=Run((host,target)=>
        {
            UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline=new UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset();
            UnityEngine.Rendering.RenderPipelineManager.currentPipeline=new UnityEngine.Object { NativeTypeName="UnityEngine.Rendering.Universal.UniversalRenderPipeline" };
            host._native.LastCamera.CameraData=new();
            UnityEngine.Object.Labels=new[]{new UILabel()};
        });
        Check(result.Errors.Count==0,string.Join("; ",result.Errors));
        Check(result.Frames.Single().Proof=="manual-camera-render-v1", "manual URP capture was mislabeled builtin");
        Check(result.Frames[0].CameraState.Contains("UniversalRenderPipelineAsset"), "native pipeline identity is missing");
        Check(result.Frames[0].Labels.Single().Projected.SequenceEqual(new[]{2f,2f,2f,6f,6f,6f,6f,2f}),"changed manual projection");
    });
    Test("an unknown configured pipeline remains rejected with its exact native type",()=>
    {
        var result=Run((host,target)=>UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline=new UnityEngine.Object{NativeTypeName="Example.UnknownRenderPipelineAsset"});
        Rejected(result,"Example.UnknownRenderPipelineAsset");
    });
    Test("camera postprocessing state changing during the direct render is rejected",()=>
    {
        var result=Run((host,target)=>
        {
            UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline=new UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset();
            host._native.LastCamera.CameraData=new();
            host._native.LastCamera.DuringRender=()=>host._native.LastCamera.CameraData.renderPostProcessing=true;
        });
        Rejected(result,"render path");
    });
    Test("unsupported overlay camera stacks remain rejected",()=>
    {
        var result=Run((host,target)=>
        {
            UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline=new UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset();
            host._native.LastCamera.CameraData=new();host._native.LastCamera.CameraData.m_Cameras.Add(new Camera());
        });
        Rejected(result,"stack");
    });
    Console.WriteLine($"{passed} collector behavior tests passed; actual AA fixture verification remains separate");
}
finally
{
    NativePreEncodeObservation.Shutdown();
    Directory.Delete(root, true);
}
