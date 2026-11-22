using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace AAVideoExport.Plugin;

/// <summary>
/// Generic observation plane for render ownership. It intentionally does not
/// classify a camera by the Mod that created it. Every enabled camera in the
/// active story scene participates in the final composition; cameras that
/// already own a target texture are rendered explicitly into the export target
/// after the stack owner has drawn its frame.
/// </summary>
internal static class RuntimeRenderPlane
{
    private static int _cameraCount;
    private static int _ownedCameraCount;
    private static int _overlayCameraCount;
    private static int _canvasCount;
    private static int _videoPlayerCount;
    private static long _observedFrame = -1;

    internal static int CameraCount => _cameraCount;
    internal static int OwnedCameraCount => _ownedCameraCount;

    internal static void Probe()
    {
        try
        {
            var cameras = UnityEngine.Object.FindObjectsOfType<Camera>(true);
            int owned = 0, overlay = 0;
            foreach (var camera in cameras)
            {
                if (camera == null) continue;
                if (camera.targetTexture != null) owned++;
                var data = camera.GetComponent<UniversalAdditionalCameraData>();
                if (data != null && data.renderType == CameraRenderType.Overlay) overlay++;
            }
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            var players = UnityEngine.Object.FindObjectsOfType<UnityEngine.Video.VideoPlayer>(true);
            RuntimeVirtualizationHost.Probe("render", RuntimePlaneState.Probed,
                "cameras=" + cameras.Length + "; owned-target=" + owned + "; overlay=" + overlay
                + "; canvases=" + canvases.Length + "; video-players=" + players.Length);
        }
        catch (Exception error)
        {
            RuntimeVirtualizationHost.Probe("render", RuntimePlaneState.Probed, "probe unavailable: " + error.GetType().Name);
        }
    }

    internal static void BeginExport(int participatingCameras, int ownedTargets, int overlayCameras, int canvases, int videoPlayers)
    {
        _cameraCount = participatingCameras;
        _ownedCameraCount = ownedTargets;
        _overlayCameraCount = overlayCameras;
        _canvasCount = canvases;
        _videoPlayerCount = videoPlayers;
        _observedFrame = -1;
        RuntimeVirtualizationHost.Trace("render_begin", new Dictionary<string, object?>
        {
            ["participating_cameras"] = participatingCameras,
            ["owned_target_cameras"] = ownedTargets,
            ["overlay_cameras"] = overlayCameras,
            ["canvases"] = canvases,
            ["video_players"] = videoPlayers
        });
    }

    internal static void CaptureFrame(long frame)
    {
        if (frame == _observedFrame) return;
        _observedFrame = frame;
        if (frame % 30 != 0) return;
        RuntimeVirtualizationHost.Trace("render_frame", new Dictionary<string, object?>
        {
            ["frame"] = frame,
            ["participating_cameras"] = _cameraCount,
            ["owned_target_cameras"] = _ownedCameraCount,
            ["overlay_cameras"] = _overlayCameraCount,
            ["canvases"] = _canvasCount,
            ["video_players"] = _videoPlayerCount
        });
    }

    internal static void EndExport(long frames)
    {
        RuntimeVirtualizationHost.Trace("render_end", new Dictionary<string, object?>
        {
            ["frames"] = frames,
            ["participating_cameras"] = _cameraCount,
            ["owned_target_cameras"] = _ownedCameraCount,
            ["overlay_cameras"] = _overlayCameraCount
        });
    }
}
