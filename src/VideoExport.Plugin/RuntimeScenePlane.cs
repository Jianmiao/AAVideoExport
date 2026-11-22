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

    internal static void BeginExport()
    {
        _timeScale = Time.timeScale;
        _restoreTimeScale = _timeScale <= 0.0001f;
        if (_restoreTimeScale) Time.timeScale = 1f;
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
