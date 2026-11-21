using System.Runtime.CompilerServices;

namespace UnityEngine
{
    public class GameObject { public bool activeInHierarchy = true; }
    public class MonoBehaviour { public GameObject gameObject = new(); }
}
public sealed class Test : UnityEngine.MonoBehaviour
{
    public bool hasSelection = true, IsAutoEnabled = true;
    public int cur = 4;
    public SelectionManager? selectionManager;
}
public sealed class SelectionManager : UnityEngine.MonoBehaviour
{
    public bool isSelectionActive = true, autoModeEnabled = true;
    public int defaultSelectionIndex;
    public float autoSelectDelaySeconds = 2;
    public List<SelectionElement>? elements = new();
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void CreateSelection() { }
    public sealed class _CoAutoSelect_d__16
    {
        public SelectionManager __4__this { get; set; } = null!;
        public int index { get; set; }
        public SelectionElement _elem_5__2 { get; set; } = null!;
        public float _elapsed_5__3 { get; set; }
        public bool DropClick;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool MoveNext()
        {
            if (_elapsed_5__3 < __4__this.autoSelectDelaySeconds) return true;
            if (!DropClick) _elem_5__2.button!.SimulateClick();
            return false;
        }
    }
}
public sealed class SelectionElement : UnityEngine.MonoBehaviour
{
    public UI.MXButton? button = new();
    public int Submissions;
    public bool Throw;
    public void OnSelect()
    {
        if (Throw) throw new IOException("native callback failure");
        Submissions++;
        button!.disabled = true;
    }
}
namespace UI
{
    public sealed class MXButton : UnityEngine.MonoBehaviour
    {
        public bool disabled, NativeSubmits;
        public int NativePressCalls;
        public Action? OnClicked;
        public void SimulateClick() => new _CoSimulatePress_d__28 { __4__this = this }.MoveNext();
        public sealed class _CoSimulatePress_d__28
        {
            public MXButton __4__this { get; set; } = null!;
            public int __1__state { get; set; }
            [MethodImpl(MethodImplOptions.NoInlining)]
            public bool MoveNext()
            {
                __4__this.NativePressCalls++;
                if (__4__this.NativeSubmits) __4__this.OnClicked?.Invoke();
                __1__state = -1;
                return false;
            }
        }
    }
}
