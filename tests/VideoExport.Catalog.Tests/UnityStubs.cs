namespace UnityEngine
{
    public class Object
    {
        private static readonly List<Object> Registry = new();
        private bool _destroyed;
        public string name = "";
        public HideFlags hideFlags;
        public Object() => Registry.Add(this);
        public static T[] FindObjectsOfType<T>() where T : Object => Registry.OfType<T>()
            .Where(o => o != null && (o is not Component c || c.gameObject.activeInHierarchy)).ToArray();
        public static void Destroy(Object value)
        {
            if (value == null) return;
            if (value is GameObject go)
            {
                foreach (var child in go.transform.Children.ToArray()) Destroy(child.gameObject);
                foreach (var component in go.Components) component._destroyed = true;
                go.transform.parent?.Children.Remove(go.transform);
            }
            value._destroyed = true;
        }
        public static void ResetRegistry() => Registry.Clear();
        public static bool operator ==(Object? left, Object? right)
        {
            bool leftNull = ReferenceEquals(left, null) || left._destroyed;
            bool rightNull = ReferenceEquals(right, null) || right._destroyed;
            return leftNull || rightNull ? leftNull == rightNull : ReferenceEquals(left, right);
        }
        public static bool operator !=(Object? left, Object? right) => !(left == right);
        public override bool Equals(object? obj) => this == obj as Object;
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
    public class GameObject : Object
    {
        public List<Component> Components { get; } = new();
        public Transform transform;
        public int layer;
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        public GameObject(string value) { name = value; transform = AddComponent<Transform>(); }
        public T AddComponent<T>() where T : Component, new()
        {
            var value = new T { gameObject = this }; Components.Add(value); return value;
        }
        public T? GetComponent<T>() where T : Component => Components.OfType<T>().FirstOrDefault(c => c != null);
        public void SetActive(bool active) => activeSelf = active;
    }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public class Transform : Component
    {
        public readonly List<Transform> Children = new();
        public Transform? parent;
        public Vector3 localPosition;
        public Vector3 localScale = new(1, 1, 1);
        public void SetParent(Transform? value, bool worldPositionStays)
        {
            parent?.Children.Remove(this); parent = value; value?.Children.Add(this);
        }
        public Transform? Find(string value) => Children.FirstOrDefault(t => t != null && t.gameObject.name == value);
    }
    public class BoxCollider : Component { public Vector3 size, center; }
    public class Texture : Object { }
    public class Texture2D : Texture
    {
        public Texture2D(int width, int height, TextureFormat format, bool mipmap) { }
        public void SetPixels(Color[] pixels) { }
        public void Apply(bool updateMipmaps, bool makeNoLongerReadable) { }
    }
    public enum TextureFormat { RGBA32 }
    [Flags] public enum HideFlags { None = 0, DontUnloadUnusedAsset = 32, HideAndDontSave = 61 }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    }
    public static class Time { public static float unscaledTime; }
}

public class CatalogFileInfo : UnityEngine.Component
{
    public UnityEngine.GameObject controlPanel = null!;
    public CatalogBlock? block;
}
public class CatalogBlock { public string path = ""; }
public class UIWidget : UnityEngine.Component
{
    public enum Pivot { Center }
    public int width = 64, height = 64, depth = 10;
    public Pivot pivot;
    public UnityEngine.Color color = new(1, 1, 1, 1);
    public UnityEngine.Vector3 localCenter;
}
public class UITexture : UIWidget { public UnityEngine.Texture? mainTexture; }
public class UIEventListener : UnityEngine.Component
{
    public delegate void VoidDelegate(UnityEngine.GameObject value);
    public VoidDelegate? onClick;
    public static UIEventListener Get(UnityEngine.GameObject target) => target.GetComponent<UIEventListener>() ?? target.AddComponent<UIEventListener>();
}
namespace Il2CppInterop.Runtime
{
    public static class DelegateSupport
    {
        public static T ConvertDelegate<T>(Delegate source) where T : Delegate => (T)Delegate.CreateDelegate(typeof(T), source.Target, source.Method);
    }
}
