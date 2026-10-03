using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AAVideoExport.Plugin;

// Only native story layout calls see the output dimensions. The progress UI,
// mouse input and the actual OS window continue to use the physical screen.
internal sealed class NativeExportViewport : IDisposable
{
    private static NativeExportViewport? _current;
    [ThreadStatic] private static int _layoutDepth;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly HashSet<int> _scenes;
    private readonly Test _player;
    private readonly UIRoot[] _roots;
    private bool _disposed;
    internal int Width { get; }
    internal int Height { get; }
    internal static bool InLayout => _current != null && _layoutDepth > 0 && Environment.CurrentManagedThreadId == _current._thread;
    internal static int LayoutWidth => _current!.Width;
    internal static int LayoutHeight => _current!.Height;

    internal NativeExportViewport(Test player, IReadOnlyList<Camera> cameras, int width, int height)
    {
        if (_current != null) throw new InvalidOperationException("An export viewport is already active.");
        _player = player;
        Width = width; Height = height;
        _scenes = cameras.Select(c => c.gameObject.scene.handle).ToHashSet();
        _roots = Object.FindObjectsOfType<UIRoot>().Where(r => r != null && _scenes.Contains(r.gameObject.scene.handle)).ToArray();
        _current = this;
    }

    internal static LayoutContext Enter(Component component)
    {
        var current = _current;
        bool owns = current != null && Environment.CurrentManagedThreadId == current._thread && component != null &&
            (component.Pointer == current._player.Pointer || current._scenes.Contains(component.gameObject.scene.handle));
        return new LayoutContext(owns);
    }

    internal readonly struct LayoutContext : IDisposable
    {
        private readonly int _previous;
        internal LayoutContext(bool enabled)
        {
            _previous = _layoutDepth;
            _layoutDepth = enabled ? _previous + 1 : 0;
        }
        public void Dispose() => _layoutDepth = _previous;
    }

    internal void RefreshLayout()
    {
        foreach (var root in _roots)
        {
            if (root == null) continue;
            using var context = Enter(root);
            root.UpdateScale(true);
            foreach (var anchor in root.GetComponentsInChildren<UIAnchor>(true))
                if (anchor != null && anchor.gameObject.activeInHierarchy) { anchor.ScreenSizeChanged(); anchor.Update(); }
            foreach (var stretch in root.GetComponentsInChildren<UIStretch>(true))
                if (stretch != null && stretch.gameObject.activeInHierarchy) { stretch.ScreenSizeChanged(); stretch.Update(); }
            foreach (var rect in root.GetComponentsInChildren<UIRect>(true))
                if (rect != null && rect.gameObject.activeInHierarchy) rect.ResetAndUpdateAnchors();
            foreach (var panel in root.GetComponentsInChildren<UIPanel>(true))
                if (panel != null && panel.gameObject.activeInHierarchy) panel.Refresh();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(_current, this)) _current = null;
        // The owner restores cameras first, then rebuilds the live story UI at
        // the current physical dimensions. Do not rewind animation transforms.
        RefreshLayout();
    }
}

[HarmonyPatch]
internal static class StoryLayoutViewportHook
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(UIRoot), nameof(UIRoot.UpdateScale));
        yield return AccessTools.PropertyGetter(typeof(UIRoot), nameof(UIRoot.activeHeight));
        yield return AccessTools.Method(typeof(UIAnchor), nameof(UIAnchor.Update));
        yield return AccessTools.Method(typeof(UIStretch), nameof(UIStretch.Update));
        yield return AccessTools.Method(typeof(UIRect), nameof(UIRect.UpdateAnchorsInternal));
        yield return AccessTools.Method(typeof(UIRect), nameof(UIRect.UpdateAnchors));
        yield return AccessTools.Method(typeof(Test), nameof(Test.Update));
    }
    static void Prefix(Component __instance, out NativeExportViewport.LayoutContext __state) =>
        __state = NativeExportViewport.Enter(__instance);
    static Exception? Finalizer(Exception? __exception, NativeExportViewport.LayoutContext __state)
    {
        __state.Dispose();
        return __exception;
    }
}

[HarmonyPatch]
internal static class StoryViewportWidthHook
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.width));
        yield return AccessTools.PropertyGetter(typeof(UICamera), nameof(UICamera.screenWidth));
    }
    static bool Prefix(ref int __result)
    {
        if (!NativeExportViewport.InLayout) return true;
        __result = NativeExportViewport.LayoutWidth;
        return false;
    }
}

[HarmonyPatch]
internal static class StoryViewportHeightHook
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.height));
        yield return AccessTools.PropertyGetter(typeof(UICamera), nameof(UICamera.screenHeight));
    }
    static bool Prefix(ref int __result)
    {
        if (!NativeExportViewport.InLayout) return true;
        __result = NativeExportViewport.LayoutHeight;
        return false;
    }
}
