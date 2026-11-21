using System.Runtime.CompilerServices;

namespace UnityEngine
{
    public class Object
    {
        public static UILabel[] Labels = Array.Empty<UILabel>();
        public string name = "native";
        public int GetInstanceID() => 7;
        public IntPtr Pointer => new(7);
        public string NativeTypeName = "UnityEngine.Object";
        public NativeType GetIl2CppType() => new() { FullName = NativeTypeName };
        public T? TryCast<T>() where T : class => this as T;
        public static T[] FindObjectsOfType<T>() => Labels.Cast<T>().ToArray();
    }
    public class NativeType { public string FullName { get; set; } = ""; }
    public class GameObject : Object
    {
        public bool activeInHierarchy = true;
        public int layer;
        public Scene scene = new();
    }
    public class Scene { public string name = "scene"; }
    public class Transform : Object
    {
        public Transform? parent;
        public GameObject gameObject = new();
        public Vector3 localPosition, localScale;
        public Quaternion localRotation;
        public Matrix4x4 localToWorldMatrix => Matrix4x4.Identity;
        public int GetSiblingIndex() => 0;
        public Vector3 TransformPoint(Vector3 value) => value;
    }
    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x=x; this.y=y; this.width=width; this.height=height; }
    }
    public struct Vector2 { public float x,y; public Vector2(float x,float y){this.x=x;this.y=y;} }
    public struct Vector3 { public float x,y,z; public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;} }
    public struct Quaternion { public float x,y,z,w; }
    public struct Color { public float r,g,b,a; public Color(float r,float g,float b,float a){this.r=r;this.g=g;this.b=b;this.a=a;} }
    public struct Matrix4x4
    {
        public float m00,m01,m02,m03,m10,m11,m12,m13,m20,m21,m22,m23,m30,m31,m32,m33;
        public static Matrix4x4 Identity => new() { m00=1,m11=1,m22=1,m33=1 };
        public static Matrix4x4 Changed => new() { m00=2,m11=1,m22=1,m33=1 };
    }
    public class Texture : Object { public int width=512,height=512; public string filterMode="Bilinear",wrapMode="Clamp"; }
    public class RenderTexture : Texture { public int antiAliasing=1; public string format="ARGB32"; }
    public class Shader : Object { public Shader(){name="GUI/Text Shader";} }
    public class Font : Object { }
    public class Material : Object
    {
        public Texture mainTexture = new(); public Shader shader = new(); public int renderQueue=3000;
        public string[] shaderKeywords=Array.Empty<string>(); public Vector2 mainTextureScale=new(1,1), mainTextureOffset;
    }
    public class Renderer { public bool enabled=true,isVisible=true; public Material sharedMaterial=new(); public int sortingLayerID,sortingOrder; }
    public class Camera : Object
    {
        public static int FireOnPostRenderCalls;
        public RenderTexture? targetTexture;
        public Transform transform = new();
        public GameObject gameObject = new();
        public bool enabled=true,orthographic=true;
        public float orthographicSize=1,fieldOfView=60,nearClipPlane=.1f,farClipPlane=100,aspect=1,depth;
        public string clearFlags="SolidColor";
        public int cullingMask=-1;
        public Rect rect=new(0,0,1,1);
        public Matrix4x4 worldToCameraMatrix=Matrix4x4.Identity,projectionMatrix=Matrix4x4.Identity;
        public Rect pixelRect => new(rect.x*(targetTexture?.width??16), rect.y*(targetTexture?.height??16), rect.width*(targetTexture?.width??16), rect.height*(targetTexture?.height??16));
        public Action? DuringRender;
        public UnityEngine.Rendering.Universal.UniversalAdditionalCameraData? CameraData;
        public T? GetComponent<T>() where T : class => CameraData as T;
        public bool allowHDR,allowMSAA,allowDynamicResolution,stereoEnabled;
        public string cameraType="Game";
        [MethodImpl(MethodImplOptions.NoInlining)] public void Render() { DuringRender?.Invoke(); }
        [MethodImpl(MethodImplOptions.NoInlining)] public static void FireOnPostRender(Camera camera) { FireOnPostRenderCalls++; }
        public Vector3 WorldToViewportPoint(Vector3 value) => new((value.x+1)*.5f,(value.y+1)*.5f,value.z);
    }
    public static class Time { public static int frameCount; public static float time,deltaTime,realtimeSinceStartup,captureDeltaTime; }
}
namespace UnityEngine.Rendering
{
    public static class GraphicsSettings { public static UnityEngine.Object? currentRenderPipeline; }
    public static class RenderPipelineManager { public static UnityEngine.Object? currentPipeline; }
}
namespace UnityEngine.Rendering.Universal
{
    public class UniversalRenderPipelineAsset : UnityEngine.Object
    {
        public float renderScale=1; public int msaaSampleCount=1; public bool supportsHDR;
        public UniversalRenderPipelineAsset(){NativeTypeName="UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset";name="AA pipeline";}
    }
    public class UniversalAdditionalCameraData : UnityEngine.Object
    {
        public string renderType="Base",antialiasing="None",antialiasingQuality="High";
        public bool renderPostProcessing,stopNaN,dithering,requiresDepthTexture,requiresColorTexture;
        public List<UnityEngine.Camera> m_Cameras=new();
    }
}
public class UIGeometry
{
    public object? onCustomWrite;
    public List<UnityEngine.Vector3> verts = new() { new(-.5f,-.5f,1),new(-.5f,.5f,1),new(.5f,.5f,1),new(.5f,-.5f,1) };
    public List<UnityEngine.Vector3> mRtpVerts = new() { new(-.5f,-.5f,1),new(-.5f,.5f,1),new(.5f,.5f,1),new(.5f,-.5f,1) };
    public List<UnityEngine.Vector2> uvs = new() { new(.125f,.125f),new(.125f,.25f),new(.25f,.25f),new(.25f,.125f) };
    public List<UnityEngine.Color> cols = Enumerable.Repeat(new UnityEngine.Color(1,1,1,1),4).ToList();
}
public class UIDrawCall
{
    public enum Clipping { None, SoftClip }
    public UnityEngine.Renderer mRenderer = new();
    public UnityEngine.GameObject gameObject = new();
    public UnityEngine.Transform transform = new();
}
public class UIPanel { public UIPanel? parentPanel; public UIDrawCall.Clipping clipping; }
public class UILabel : UnityEngine.Object
{
    public bool enabled=true; public UnityEngine.GameObject gameObject=new(); public UnityEngine.Transform transform=new();
    public float alpha=1,finalAlpha=1; public UIDrawCall drawCall=new(); public UIGeometry geometry=new(); public UIPanel panel=new();
    public UnityEngine.Font trueTypeFont=new(); public UnityEngine.Texture mainTexture=new(); public UnityEngine.Material material=new();
    public string text="glyph",mProcessedText="glyph",fontStyle="Normal"; public int fontSize=16,finalFontSize=16;
}
namespace PlayerSettings
{
    public class TitleSettings
    {
        public static TitleSettings? instance;
        public Animation? titleAnimation; public UnityEngine.GameObject? titleRoot; public object? tweenAlpha;
        public UILabel? titleText,headerText;
    }
    public class Animation { public UnityEngine.Object? clip; }
}
namespace AAVideoExport.Core
{
    public class ExportOptions { public int Fps=30; public (int CaptureWidth,int CaptureHeight) GetCanvasLayout() => (8,8); }
    public class ExportSession
    {
        public long FramesAccepted,FramesWritten;
        [MethodImpl(MethodImplOptions.NoInlining)] public void WriteFrame(byte[] bytes,int count) { FramesAccepted++;FramesWritten++; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void WriteFrame(IntPtr bytes,int count) { FramesAccepted++;FramesWritten++; }
    }
}
namespace AAVideoExport.Plugin
{
    public class ExportHost
    {
        public NativeCaptureScope _native; public FramePipeline _frames; public AAVideoExport.Core.ExportSession _session=new(); public long _captured;
        public ExportHost(UnityEngine.Camera camera,UnityEngine.RenderTexture target){_native=new(camera);_frames=new(_session,target);}
    }
    public class FramePipeline
    {
        public AAVideoExport.Core.ExportSession _encoder; public UnityEngine.RenderTexture _renderTarget;
        public FramePipeline(AAVideoExport.Core.ExportSession session,UnityEngine.RenderTexture target){_encoder=session;_renderTarget=target;}
        [MethodImpl(MethodImplOptions.NoInlining)] public void Capture(){_encoder.WriteFrame(new byte[8*8*4],8*8*4);}
    }
    public class NativeCaptureScope
    {
        public UnityEngine.Camera LastCamera {get;}
        public bool AutomaticCameraCapture {get;set;}
        public NativeCaptureScope(UnityEngine.Camera camera){LastCamera=camera;}
        // Same relevant call/exception boundary as NativeCaptureScope.RenderFrame:
        // the native Render call runs with temporary target/aspect/rect, then finally restores them.
        [MethodImpl(MethodImplOptions.NoInlining)] public void RenderFrame(UnityEngine.RenderTexture target)
        {
            var camera=LastCamera;var oldTarget=camera.targetTexture;var oldAspect=camera.aspect;var oldRect=camera.rect;
            try {camera.targetTexture=target;camera.aspect=1;camera.rect=new(0,0,1,1);camera.Render();}
            finally {camera.targetTexture=oldTarget;camera.aspect=oldAspect;camera.rect=oldRect;}
        }
    }
    public static class NativeExportClock { }
}
namespace AAVideoExport.NativePath.Tests { internal static class Plugin { internal static void Report(string message) => Console.WriteLine(message); } }
