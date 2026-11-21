using System.Diagnostics;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AAVideoExport.Plugin;

// Attach to both native catalog layouts, alongside the four existing actions.
// Coordinates are inherited from the native toolbar, never from screen pixels.
internal sealed class CatalogExportAction : IDisposable
{
    private readonly List<Entry> _entries = new();
    private readonly Action<CatalogFileInfo> _open;
    private Texture2D? _icon;
    private double _nextScan;
    public CatalogExportAction(Action<CatalogFileInfo> open) => _open = open;

    public void Tick(bool hidden)
    {
        // Export replaces Unity's time with the video clock, then restores it.
        // A deadline in that clock can otherwise suppress rescans for minutes.
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now >= _nextScan)
        {
            _nextScan = now + .5;
            for (int index = _entries.Count - 1; index >= 0; index--)
            {
                var entry = _entries[index];
                if (IsAttached(entry)) continue;
                if (entry.Button != null)
                {
                    entry.Button.SetActive(false);
                    Object.Destroy(entry.Button);
                }
                _entries.RemoveAt(index);
            }
            foreach (var info in Object.FindObjectsOfType<CatalogFileInfo>())
            {
                if (info == null || info.controlPanel == null || _entries.Any(e => e.Info == info)) continue;
                var grid = info.controlPanel.transform.Find("Grid");
                var last = grid == null ? null : grid.Find("DeleteBtn");
                var before = grid == null ? null : grid.Find("ShareBtn");
                if (last == null || before == null) continue;
                Build(info, last, before);
            }
        }
        if (_entries.Count > 0 && _icon == null) _icon = MakeIcon();
        foreach (var entry in _entries)
        {
            if (entry.Button == null || entry.Info == null || entry.Info.controlPanel == null ||
                entry.Last == null || entry.Before == null || entry.Graphic == null) continue;
            if (entry.Graphic.mainTexture != _icon) entry.Graphic.mainTexture = _icon;
            bool isSave = entry.Info.block != null && !string.IsNullOrWhiteSpace(entry.Info.block.path) &&
                string.Equals(Path.GetExtension(entry.Info.block.path), ".aas", StringComparison.OrdinalIgnoreCase);
            entry.Button.SetActive(!hidden && isSave && entry.Info.controlPanel.activeInHierarchy);
            entry.Button.transform.localPosition = entry.Last.localPosition + (entry.Last.localPosition - entry.Before.localPosition);
            entry.Button.transform.localScale = entry.Last.localScale;
        }
    }

    private static bool IsAttached(Entry entry)
    {
        if (entry.Button == null || entry.Info == null || entry.Info.controlPanel == null ||
            entry.Last == null || entry.Before == null || entry.Graphic == null) return false;
        var grid = entry.Info.controlPanel.transform.Find("Grid");
        return grid != null && entry.Button.transform.parent == grid &&
            grid.Find("DeleteBtn") == entry.Last && grid.Find("ShareBtn") == entry.Before;
    }

    private void Build(CatalogFileInfo info, Transform last, Transform before)
    {
        if (_icon == null) _icon = MakeIcon();
        var widget = last.GetComponent<UIWidget>();
        int size = widget == null ? 64 : Math.Max(32, widget.width);
        var button = new GameObject("AAVideoExport.ToolbarAction");
        button.SetActive(false);
        button.layer = last.gameObject.layer;
        button.transform.SetParent(last.parent, false);
        var graphic = button.AddComponent<UITexture>();
        graphic.mainTexture = _icon;
        graphic.width = size;
        graphic.height = widget == null ? size : widget.height;
        graphic.pivot = widget == null ? UIWidget.Pivot.Center : widget.pivot;
        graphic.depth = (widget == null ? 100 : widget.depth) + 1;
        graphic.color = widget == null ? new Color(1, .92f, .19f, 1) : widget.color;
        var collider = button.AddComponent<BoxCollider>();
        collider.size = new Vector3(size, graphic.height, 1);
        collider.center = graphic.localCenter;
        var clicked = DelegateSupport.ConvertDelegate<UIEventListener.VoidDelegate>((Action<GameObject>)(_ =>
        {
            if (info != null && info.block != null) _open(info);
        }));
        if (clicked == null) { Object.Destroy(button); throw new InvalidOperationException("Unable to bind native export action."); }
        UIEventListener.Get(button).onClick = clicked;
        _entries.Add(new Entry(info, button, last, before, graphic, clicked));
    }

    private static Texture2D MakeIcon()
    {
        const int size = 80;
        var pixels = new Color[size * size];
        void Line(float x1, float y1, float x2, float y2, float stroke = 5)
        {
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x2 - x1, dy = y2 - y1;
                float t = Math.Clamp(((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy), 0, 1);
                float distance = MathF.Sqrt(MathF.Pow(x - x1 - t * dx, 2) + MathF.Pow(y - y1 - t * dy, 2));
                float alpha = Math.Clamp(stroke / 2f + .8f - distance, 0, 1);
                if (alpha > pixels[y * size + x].a) pixels[y * size + x] = new Color(1, 1, 1, alpha);
            }
        }
        // Original line-art download tray, visually weighted like AA's toolbar icons.
        Line(11, 10, 68, 10, 7); Line(11, 10, 13, 26, 7); Line(68, 10, 70, 26, 7);
        Line(46, 72, 38, 29, 7); Line(22, 44, 38, 28, 7); Line(38, 28, 59, 45, 7);
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = "AAVideoExport.AuthoredExportIcon";
        // The managed owner survives scene changes; keep its authored texture
        // alive through AA's UnloadUnusedAssets and release it in Dispose.
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.SetPixels(pixels); texture.Apply(false, false);
        return texture;
    }

    public void Dispose()
    {
        foreach (var entry in _entries) if (entry.Button != null) Object.Destroy(entry.Button);
        _entries.Clear();
        if (_icon != null) Object.Destroy(_icon);
        _icon = null;
    }
    private sealed record Entry(CatalogFileInfo Info, GameObject Button, Transform Last, Transform Before,
        UITexture Graphic, UIEventListener.VoidDelegate Click);
}
