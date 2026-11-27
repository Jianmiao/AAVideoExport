using UnityEngine;
using UnityEngine.Video;

namespace AAVideoExport.Plugin;

/// <summary>
/// Generic scene and playback-boundary plane. It records the actual runtime
/// scene instead of identifying a Mod: camera scenes, active canvases, video
/// players, time scale and audio-listener state. The exported story remains
/// driven by AA; this plane only repairs global states that would otherwise
/// make an otherwise valid export unobservable.
/// </summary>
internal static class RuntimeScenePlane
{
    private static bool _active;
    private static bool _restoreTimeScale;
    private static float _timeScale = 1f;
    private static int _activeCanvasCount;
    private static int _activeVideoPlayerCount;
    private static readonly List<MonoBehaviour> ObservedMenuObjects = new();
    private static long _observedFrame = -1;

    internal static string SceneName { get; private set; } = "";
    internal static int ActiveCanvasCount => _activeCanvasCount;
    internal static int ActiveVideoPlayerCount => _activeVideoPlayerCount;
    internal static bool TimeScaleRepaired => _restoreTimeScale;

    internal static void Probe()
    {
        try
        {
            var cameras = UnityEngine.Object.FindObjectsOfType<Camera>(true);
            var scene = cameras.Length == 0 ? "" : cameras[0].gameObject.scene.name;
            int canvases = 0;
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
                if (canvas != null && canvas.isActiveAndEnabled) canvases++;
            int players = 0;
            foreach (var player in UnityEngine.Object.FindObjectsOfType<VideoPlayer>(true))
                if (player != null && player.isActiveAndEnabled) players++;
            RuntimeVirtualizationHost.Probe("scene", RuntimePlaneState.Probed,
                "scene=" + scene + "; canvases=" + canvases + "; video-players=" + players + "; time-scale=" + Time.timeScale.ToString("0.###"));
        }
        catch (Exception error)
        {
            RuntimeVirtualizationHost.Probe("scene", RuntimePlaneState.Probed, "probe unavailable: " + error.GetType().Name);
        }
    }

    internal static void BeginExport(int fps)
    {
        _timeScale = Time.timeScale;
        _restoreTimeScale = _timeScale <= 0.0001f;
        if (_restoreTimeScale) Time.timeScale = 1f;
        if (fps > 0)
        {
            try { Time.captureDeltaTime = 1f / fps; }
            catch (Exception error) { Plugin.LogPerformance("scene capture-delta failed: " + error.GetType().Name); }
        }
        int warmed = PrimeFontAtlases();
        try
        {
            Canvas.ForceUpdateCanvases();
            RuntimeVirtualizationHost.Trace("font_atlas_canvas", new Dictionary<string, object?>
            {
                ["textures"] = FontTextureSummary()
            });
        }
        catch (Exception error) { Plugin.LogPerformance("scene canvas force failed: " + error.GetType().Name); }
        _activeCanvasCount = CountCanvases();
        _activeVideoPlayerCount = CountVideoPlayers();
        SceneName = ActiveSceneName();
        _observedFrame = -1;
        _active = true;
        RuntimeVirtualizationHost.Trace("scene_begin", new Dictionary<string, object?>
        {
            ["scene"] = SceneName,
            ["canvases"] = _activeCanvasCount,
            ["video_players"] = _activeVideoPlayerCount,
            ["font_labels_warmed"] = warmed,
            ["animators"] = UnityEngine.Object.FindObjectsOfType<Animator>(true).Length,
            ["animator_paths"] = string.Join("|", UnityEngine.Object.FindObjectsOfType<Animator>(true)
                .Where(value => value != null && value.isActiveAndEnabled)
                .Select(value => HierarchyPath(value.transform) + ":" + (value.runtimeAnimatorController == null ? "" : value.runtimeAnimatorController.name))
                .Take(20)),
            ["canvas_groups"] = UnityEngine.Object.FindObjectsOfType<CanvasGroup>(true)
                .Count(value => value != null && value.isActiveAndEnabled),
            ["capture_delta"] = Time.captureDeltaTime,
            ["time_scale"] = _timeScale,
            ["time_scale_repaired"] = _restoreTimeScale
        });
    }

    internal static void CaptureFrame(long frame)
    {
        if (!_active || frame == _observedFrame) return;
        _observedFrame = frame;
        if (frame % 30 != 0) return;
        _activeCanvasCount = CountCanvases();
        _activeVideoPlayerCount = CountVideoPlayers();
        RuntimeVirtualizationHost.Trace("scene_frame", new Dictionary<string, object?>
        {
            ["frame"] = frame,
            ["scene"] = ActiveSceneName(),
            ["canvases"] = _activeCanvasCount,
            ["video_players"] = _activeVideoPlayerCount,
            ["time_scale"] = Time.timeScale
        });
    }

    internal static void EndExport()
    {
        if (!_active) return;
        _active = false;
        if (_restoreTimeScale)
        {
            try { Time.timeScale = _timeScale; }
            catch (Exception error)
            {
                Plugin.LogPerformance("scene time-scale restore failed: " + error.GetType().Name);
            }
            _restoreTimeScale = false;
        }
        RuntimeVirtualizationHost.Trace("scene_end", new Dictionary<string, object?>
        {
            ["scene"] = SceneName,
            ["canvases"] = CountCanvases(),
            ["video_players"] = CountVideoPlayers(),
            ["time_scale"] = Time.timeScale
        });
    }

    internal static IDisposable AcquireStartupLease()
    {
        RuntimeVirtualizationHost.Trace("startup_boundary", new Dictionary<string, object?>
        {
            ["action"] = "acquire",
            ["scene"] = ActiveSceneName(),
            ["time_scale"] = Time.timeScale
        });
        return new Lease("startup_boundary", () => { });
    }

    internal static void MarkStartupReached(string detail)
    {
        RuntimeVirtualizationHost.Trace("startup_boundary", new Dictionary<string, object?>
        {
            ["action"] = "reached",
            ["detail"] = detail,
            ["scene"] = ActiveSceneName()
        });
    }

    internal static IDisposable AcquireMenuLease()
    {
        ObservedMenuObjects.Clear();
        foreach (var behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true))
        {
            if (behaviour == null || !behaviour.isActiveAndEnabled) continue;
            if (behaviour.gameObject.name.IndexOf("menu", StringComparison.OrdinalIgnoreCase) < 0) continue;
            ObservedMenuObjects.Add(behaviour);
        }
        RuntimeVirtualizationHost.Trace("menu_boundary", new Dictionary<string, object?>
        {
            ["action"] = "acquire",
            ["menu_objects"] = ObservedMenuObjects.Count,
            ["scene"] = ActiveSceneName()
        });
        return new Lease("menu_boundary", () => { });
    }

    internal static void MenuPlayback(string action)
    {
        RuntimeVirtualizationHost.Trace("menu_boundary", new Dictionary<string, object?>
        {
            ["action"] = action,
            ["menu_objects"] = ObservedMenuObjects.Count,
            ["playing_audio_sources"] = CountPlayingAudio()
        });
    }

    internal static void Reset()
    {
        ObservedMenuObjects.Clear();
        SceneName = "";
        _activeCanvasCount = 0;
        _activeVideoPlayerCount = 0;
        _observedFrame = -1;
    }

    private const string PrintableAscii =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 /:-_,.()[]!?%+";
    private static string _storyFontText = "";

    internal static void SetStoryFontText(string storyPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(storyPath) || !File.Exists(storyPath)) return;
            var bytes = File.ReadAllBytes(storyPath);
            var source = System.Text.Encoding.UTF8.GetString(bytes);
            var printable = new System.Text.StringBuilder(source.Length);
            foreach (var ch in source)
                if (char.IsLetterOrDigit(ch) || char.IsPunctuation(ch) || ch == ' ' || ch == '\n' || ch == '\r' || ch == '\t')
                    printable.Append(ch);
            _storyFontText = printable.ToString();
        }
        catch
        {
            _storyFontText = "";
        }
    }

    private static int PrimeFontAtlases()
    {
        var labels = new List<MonoBehaviour>();
        foreach (var behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true))
        {
            if (behaviour == null || behaviour.GetType().Name != "UILabel") continue;
            labels.Add(behaviour);
        }
        labels.Sort((left, right) => string.CompareOrdinal(HierarchyPath(left.transform), HierarchyPath(right.transform)));
        var fonts = new Dictionary<int, Font>();
        var sizes = new HashSet<int> { 42 };
        int warmed = 0;
        foreach (var label in labels)
        {
            try
            {
                var text = ReadMember(label, "text") as string;
                var size = ReadMember(label, "fontSize") as int? ?? 0;
                if (size <= 0) size = ReadMember(label, "finalFontSize") as int? ?? 0;
                var style = ReadMember(label, "fontStyle") as FontStyle? ?? FontStyle.Normal;
                var face = ReadMember(label, "font");
                var font = face == null ? null : ReadMember(face, "dynamicFont") as Font;
                if (font == null) font = ReadMember(label, "trueTypeFont") as Font;
                if (font == null || size <= 0) continue;
                fonts[font.GetInstanceID()] = font;
                sizes.Add(size);
                RequestFont(font, (text ?? "") + PrintableAscii, size, style);
                warmed++;
            }
            catch (Exception error)
            {
                Plugin.LogPerformance("scene font request failed: " + error.GetType().Name);
            }
        }
        RuntimeVirtualizationHost.Trace("font_atlas_prime", new Dictionary<string, object?>
        {
            ["labels"] = labels.Count,
            ["fonts"] = fonts.Count,
            ["requested"] = warmed,
            ["story_chars"] = _storyFontText.Length,
            ["textures"] = FontTextureSummary()
        });

        // Grow every dynamic atlas to its final cold size before any story
        // glyph is placed. A second in-process export otherwise inherits the
        // atlas from the first pass at a different capacity and packs the same
        // glyphs at different 1-LSB offsets.
        var allFonts = UnityEngine.Resources.FindObjectsOfTypeAll<Font>();
        foreach (var font in allFonts)
        {
            if (font == null) continue;
            try
            {
                foreach (var size in sizes) RequestFont(font, PrintableAscii, size, FontStyle.Normal);
                fonts[font.GetInstanceID()] = font;
            }
            catch { }
        }
        Canvas.ForceUpdateCanvases();
        RuntimeVirtualizationHost.Trace("font_atlas_ascii", new Dictionary<string, object?>
        {
            ["textures"] = FontTextureSummary()
        });

        foreach (var font in allFonts)
        {
            if (font == null) continue;
            try
            {
                bool storyFont = font.name.Contains("NotoSansTC") || font.name.Contains("NowarRounded-CN") || font.name.Contains("MainFont");
                foreach (var size in sizes)
                {
                    if (storyFont)
                    {
                        RequestFont(font, _storyFontText, size, FontStyle.Normal);
                        RequestFont(font, _storyFontText, size, FontStyle.Bold);
                        RequestFont(font, _storyFontText, size, FontStyle.Italic);
                        RequestFont(font, _storyFontText, size, FontStyle.BoldAndItalic);
                    }
                    else
                    {
                        RequestFont(font, _storyFontText, size, FontStyle.Normal);
                    }
                }
            }
            catch { }
        }
        Canvas.ForceUpdateCanvases();
        RuntimeVirtualizationHost.Trace("font_atlas_story", new Dictionary<string, object?>
        {
            ["textures"] = FontTextureSummary()
        });
        return warmed;
    }

    private static void RequestFont(Font font, string text, int size, FontStyle style)
    {
        if (font == null || size <= 0 || string.IsNullOrEmpty(text)) return;
        var request = font.GetType().GetMethod("RequestCharactersInTexture",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public, null,
            new[] { typeof(string), typeof(int), typeof(FontStyle) }, null);
        if (request == null) return;
        request.Invoke(font, new object[] { text, size, style });
    }

    private static string FontTextureSummary()
    {
        var fonts = UnityEngine.Resources.FindObjectsOfTypeAll<Font>();
        var parts = new List<string>();
        foreach (var font in fonts)
        {
            if (font == null) continue;
            try
            {
                var texture = font.material == null ? null : font.material.mainTexture;
                parts.Add(font.name + ":" + (texture == null ? "-" : texture.width + "x" + texture.height));
            }
            catch { parts.Add(font.name + ":?"); }
        }
        return string.Join("|", parts);
    }

    internal static void ResetRepeatUi()
    {
        int removed = 0;
        foreach (var transform in UnityEngine.Object.FindObjectsOfType<Transform>(true))
        {
            try
            {
                if (transform == null) continue;
                string name = transform.gameObject.name;
                if (name.Contains("UIScenarioScriptLogUnit"))
                {
                    UnityEngine.Object.Destroy(transform.gameObject);
                    removed++;
                }
            }
            catch { }
        }
        RuntimeVirtualizationHost.Trace("repeat_ui_reset", new Dictionary<string, object?> { ["removed"] = removed });
    }

    private static object? ReadMember(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(instance);
        var field = instance.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        return field?.GetValue(instance);
    }

    private static string HierarchyPath(Transform transform)
    {
        var parts = new List<string>();
        while (transform != null)
        {
            parts.Add(transform.GetSiblingIndex().ToString("D6") + ":" + transform.name);
            transform = transform.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    internal static IDisposable AcquireStartupCommitScope() =>
        new RuntimeSceneCommitScope("startup", MarkStartupReached);

    internal static IDisposable AcquireMenuCommitScope() =>
        new RuntimeSceneCommitScope("menu", phase => MenuPlayback(phase));

    internal sealed class RuntimeSceneCommitScope : IDisposable
    {
        private readonly string _boundary;
        private readonly Action<string> _observe;
        private bool _disposed;
        internal RuntimeSceneCommitScope(string boundary, Action<string> observe)
        {
            _boundary = boundary;
            _observe = observe;
            _observe(_boundary + "-commit-begin");
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _observe(_boundary + "-commit-end");
        }
    }

    private static int CountCanvases()
    {
        int count = 0;
        foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
            if (canvas != null && canvas.isActiveAndEnabled) count++;
        return count;
    }

    private static int CountVideoPlayers()
    {
        int count = 0;
        foreach (var player in UnityEngine.Object.FindObjectsOfType<VideoPlayer>(true))
            if (player != null && player.isActiveAndEnabled) count++;
        return count;
    }

    private static int CountPlayingAudio()
    {
        int count = 0;
        foreach (var source in UnityEngine.Object.FindObjectsOfType<AudioSource>(true))
            if (source != null && source.isActiveAndEnabled && source.isPlaying) count++;
        return count;
    }

    private static string ActiveSceneName()
    {
        var camera = Camera.main;
        if (camera != null) return camera.gameObject.scene.name;
        var cameras = UnityEngine.Object.FindObjectsOfType<Camera>(true);
        return cameras.Length == 0 ? "" : cameras[0].gameObject.scene.name;
    }

    private sealed class Lease : IDisposable
    {
        private readonly string _kind;
        private readonly Action _dispose;
        private bool _done;
        internal Lease(string kind, Action dispose) { _kind = kind; _dispose = dispose; }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _dispose();
            RuntimeVirtualizationHost.Trace(_kind, new Dictionary<string, object?>
            {
                ["action"] = "release",
                ["scene"] = ActiveSceneName()
            });
        }
    }
}
