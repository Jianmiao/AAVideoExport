using System.Runtime.CompilerServices;

public sealed class CreatorSplash : UnityEngine.MonoBehaviour
{
    public CreatorSplash() { gameObject.Disabled = () => { IsComplete = true; InputRestored = true; }; }
    public bool NativeAwakeRan { get; private set; }
    public bool InputRestored { get; private set; }
    public bool IsComplete { get; private set; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Awake() { NativeAwakeRan = true; InputRestored = false; IsComplete = false; }
}

public sealed class UnrelatedPresenter : UnityEngine.MonoBehaviour
{
    public bool NativeAwakeRan { get; private set; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Awake() { NativeAwakeRan = true; }
}

namespace UnityEngine
{
    public class Object { }
    public class MonoBehaviour : Object { public GameObject gameObject { get; set; } = new(); }
    public sealed class GameObject : Object
    {
        public bool activeSelf { get; private set; } = true;
        public bool activeInHierarchy { get; set; } = true;
        public Action? Disabled;
        public void SetActive(bool active)
        {
            bool wasActive = activeSelf;
            activeSelf = active;
            activeInHierarchy = active;
            if (wasActive && !active) Disabled?.Invoke();
        }
    }
    public static class Resources
    {
        public static readonly List<Object> Objects = new();
        public static int ReadCount;
        public static T[] FindObjectsOfTypeAll<T>() where T : Object
        {
            ReadCount++;
            return Objects.OfType<T>().ToArray();
        }
    }
    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
    }
}
