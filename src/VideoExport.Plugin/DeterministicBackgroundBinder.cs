using System;
using System.IO;
using UnityEngine;

namespace AAVideoExport.Plugin;

/// <summary>
/// The story presenters own the visible background image. When that image's
/// resource has a cached local video with the same base name, bind the cached
/// frame texture directly. This is generic story-resource resolution, not a
/// Mod identity check, and it avoids depending on a RenderTexture that the
/// owning Mod may clear or replace during a story transition.
/// </summary>
internal static class DeterministicBackgroundBinder
{
    private static BackgroundPresenter[] _presenters = Array.Empty<BackgroundPresenter>();
    private static int _probeCountdown;

    internal static void Reset()
    {
        _presenters = Array.Empty<BackgroundPresenter>();
        _probeCountdown = 0;
    }

    internal static void Bind(Texture2D frame)
    {
        EnsurePresenters();
        foreach (var presenter in _presenters)
        {
            if (presenter == null) continue;
            BindLayer(presenter.main, frame);
            BindLayer(presenter.overlap, frame);
        }
    }

    private static void BindLayer(BackgroundPresenter.Layer layer, Texture2D frame)
    {
        if (layer == null) return;
        if (string.IsNullOrEmpty(LayerBaseName(layer))) return;
        try
        {
            var image = layer.Image;
            if (image == null) return;
            if (!ReferenceEquals(image.mainTexture, frame)) image.mainTexture = frame;
        }
        catch { }
    }

    private static void EnsurePresenters()
    {
        if (_presenters.Length != 0 || _probeCountdown-- > 0) return;
        try { _presenters = UnityEngine.Object.FindObjectsOfType<BackgroundPresenter>(true); }
        catch { _presenters = Array.Empty<BackgroundPresenter>(); }
        _probeCountdown = 8;
    }

    private static string LayerBaseName(BackgroundPresenter.Layer layer)
    {
        if (layer == null) return "";
        try
        {
            var content = layer.Content;
            var resource = content == null ? null : content.Resource;
            if (resource == null) return "";
            string path = "";
            try { path = resource.FullPath ?? ""; } catch { }
            string name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name))
            {
                try { name = Path.GetFileNameWithoutExtension(resource.Name ?? ""); } catch { }
            }
            return DeterministicVideoCache.HasBaseName(name) ? name : "";
        }
        catch { return ""; }
    }
}
