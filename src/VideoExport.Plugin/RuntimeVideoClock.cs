using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

namespace AAVideoExport.Plugin;

/// <summary>
/// Generic virtual clock for Unity VideoPlayer sources. It never identifies a
/// Mod. Export start waits for each active player to produce a decoded frame,
/// then pins it to frame zero so every run begins at the same media phase.
/// During capture the player is held at a deterministic frame. Seeking is
/// issued once per target and the host explicitly waits until the decoder has
/// observed that frame before capturing, so an asynchronous decode never races
/// the readback.
/// </summary>
internal static class RuntimeVideoClock
{
    private sealed record State(float Speed, long Frame, bool Playing, bool Paused, VideoTimeReference TimeReference, double ExternalTime);
    private sealed class Pending
    {
        public int Settle;
        public bool Step;
        public int Cooldown;
        public int Tries;
    }

    private static readonly Dictionary<VideoPlayer, State> States = new();
    private static readonly Dictionary<VideoPlayer, long> LastTargets = new();
    private static readonly Dictionary<VideoPlayer, string> LastUrls = new();
    private static readonly Dictionary<VideoPlayer, bool> HasWrapped = new();
    private static readonly Dictionary<VideoPlayer, Pending> Pendings = new();
    private static bool _active;
    private static bool _started;
    private static float _startedAt;
    private static int _fps = 30;
    private static bool _runInBackground;
    private static bool _captureReady = true;
    private static float _waitStartedAt = -1f;
    private static int _waitPumps;
    private static int _pumpedUnityFrame = -1;

    internal static void BeginExport(int fps, string storyPath = "")
    {
        _active = true;
        _started = false;
        _startedAt = Time.realtimeSinceStartup;
        _fps = fps > 0 ? fps : 30;
        States.Clear();
        LastTargets.Clear();
        LastUrls.Clear();
        HasWrapped.Clear();
        Pendings.Clear();
        _captureReady = true;
        _waitStartedAt = -1f;
        _waitPumps = 0;
        _pumpedUnityFrame = -1;
        _runInBackground = Application.runInBackground;
        Application.runInBackground = true;
        DeterministicVideoCache.Begin(storyPath);
    }

    /// <summary>
    /// Moves a prepared player to frame zero. A frozen player cannot process a
    /// frame seek, so a non-zero position is first made seekable by briefly
    /// restoring its playback speed. The caller retries across native Updates;
    /// the start gate stays closed until the pin is actually observable.
    /// </summary>
    private static bool PinToFirstFrame(VideoPlayer player)
    {
        if (player.frame == 0)
        {
            if (player.playbackSpeed != 0f) player.playbackSpeed = 0f;
            return true;
        }
        if (player.playbackSpeed == 0f) player.playbackSpeed = 1f;
        player.frame = 0;
        return false;
    }

    internal static bool ReadyToStart()
    {
        if (!_active || _started) return true;
        // Decode the local video assets once, while the authored timeline is
        // still held. The cache is independent of the Mod that owns the player.
        if (!DeterministicVideoCache.Prepare()) return false;
        var players = UnityEngine.Object.FindObjectsOfType<VideoPlayer>(true);
        bool sawActive = false, ready = true;
        foreach (var player in players)
        {
            if (player == null || !player.isActiveAndEnabled) continue;
            sawActive = true;
            if (!player.isPrepared || player.frameCount <= 0 || player.frame < 0) { ready = false; continue; }
            if (!States.ContainsKey(player))
                States[player] = new State(player.playbackSpeed, player.frame, player.isPlaying, player.isPaused,
                    player.timeReference, player.externalReferenceTime);
            if (!LastTargets.ContainsKey(player)) LastTargets[player] = 0;
            if (DeterministicVideoCache.Has(player.url))
            {
                if (!PrimeCachedPlayer(player)) ready = false;
            }
            else if (!PinToFirstFrame(player)) ready = false;
        }
        if (ready || !sawActive)
        {
            _started = true;
            return true;
        }
        if (Time.realtimeSinceStartup - _startedAt >= 30f)
        {
            // Never hold the authored timeline hostage to a broken decoder.
            // The timeout is diagnostic only; normal media becomes ready in
            // well under a second.
            _started = true;
            Plugin.LogPerformance("video clock start gate timed out with an undecoded VideoPlayer.");
            return true;
        }
        return false;
    }

    private static bool PrimeCachedPlayer(VideoPlayer player)
    {
        // Some consumers only bind a render texture after frame > 0. The
        // cached pixels are applied independently of this marker, so the
        // player may stay on frame one while the exported frame advances.
        if (player.frame <= 0)
        {
            if (player.playbackSpeed == 0f) player.playbackSpeed = 1f;
            player.frame = 1;
            return false;
        }
        if (player.playbackSpeed != 0f) player.playbackSpeed = 0f;
        return true;
    }

    /// <summary>
    /// Advances the desired media frame for the next capture and reports
    /// whether every active player has reached and settled on that frame.
    /// The export host must not capture while this returns false.
    /// </summary>
    internal static bool Pump(long exportFrame)
    {
        if (!_active || !_started) return true;
        if (_pumpedUnityFrame == Time.frameCount) return _captureReady;
        _pumpedUnityFrame = Time.frameCount;

        bool ready = true;
        bool sawActive = false;
        foreach (var player in UnityEngine.Object.FindObjectsOfType<VideoPlayer>(true))
        {
            if (player == null || !player.isActiveAndEnabled) continue;
            long frameCount;
            long currentFrame;
            try
            {
                frameCount = (long)player.frameCount;
                currentFrame = player.frame;
                if (frameCount <= 0 || currentFrame < 0 || !player.canSetTime) continue;
            }
            catch { continue; }
            sawActive = true;

            if (!States.TryGetValue(player, out var state))
            {
                state = new State(player.playbackSpeed, currentFrame, player.isPlaying, player.isPaused,
                    player.timeReference, player.externalReferenceTime);
                States[player] = state;
            }

            if (DeterministicVideoCache.TryApply(player, exportFrame, _fps))
            {
                // The cached pixels are already in the player's target texture.
                // Keep the consumer's "first frame ready" marker satisfied, but
                // do not let it advance the decoder over the cached frame.
                if (player.frame <= 0)
                {
                    if (player.playbackSpeed == 0f) player.playbackSpeed = 1f;
                    player.frame = 1;
                }
                else if (player.playbackSpeed != 0f)
                {
                    player.playbackSpeed = 0f;
                }
                Trace(exportFrame, player, true, cached: true);
                continue;
            }

            try
            {
                long targetFrame = (long)Math.Floor(exportFrame * (double)player.frameRate / _fps);
                targetFrame %= frameCount;
                long lastTarget = LastTargets.TryGetValue(player, out var recorded) ? recorded : currentFrame;

                if (targetFrame != lastTarget)
                {
                    LastTargets[player] = targetFrame;
                    var request = new Pending { Settle = 1 };
                    LastUrls[player] = player.url;
                    bool stepForward = targetFrame > 1;
                    if (!stepForward)
                    {
                        // Frame zero and frame one are the points where an
                        // exact seek is safer than a speculative step.
                        if (targetFrame == 0) HasWrapped[player] = true;
                        Pendings[player] = request;
                        player.playbackSpeed = 1f;
                        player.frame = targetFrame;
                        player.playbackSpeed = 0f;
                    }
                    else
                    {
                        // Stepping avoids the asynchronous seek race for
                        // forward motion. One step is issued, then the host
                        // waits a few native updates before the next one.
                        request.Step = true;
                        request.Cooldown = 3;
                        Pendings[player] = request;
                        player.StepForward();
                    }
                }

                if (Pendings.TryGetValue(player, out var pending))
                {
                    if (pending.Step)
                    {
                        if (player.frame < targetFrame)
                        {
                            if (pending.Cooldown > 0)
                            {
                                pending.Cooldown--;
                            }
                            else
                            {
                                if (pending.Tries < 4)
                                {
                                    pending.Tries++;
                                    pending.Cooldown = 3;
                                    player.StepForward();
                                }
                                else
                                {
                                    pending.Step = false;
                                    pending.Tries = 0;
                                    player.playbackSpeed = 1f;
                                    player.frame = targetFrame;
                                    player.playbackSpeed = 0f;
                                }
                            }
                            ready = false;
                        }
                        else if (player.frame == targetFrame)
                        {
                            if (pending.Settle > 0)
                            {
                                // One extra native update lets the render
                                // texture consume the decoded frame.
                                pending.Settle--;
                                ready = false;
                            }
                            else
                            {
                                Pendings.Remove(player);
                                if (player.playbackSpeed != 0f) player.playbackSpeed = 0f;
                            }
                        }
                        else
                        {
                            // The step overshot; fall back to an exact seek.
                            pending.Step = false;
                            player.playbackSpeed = 1f;
                            player.frame = targetFrame;
                            player.playbackSpeed = 0f;
                            ready = false;
                        }
                    }
                    else if (player.frame != targetFrame)
                    {
                        if (pending.Tries < 4)
                        {
                            pending.Tries++;
                        }
                        else if (targetFrame > player.frame)
                        {
                            // A second seek can still be ignored. Fall back to
                            // exactly one StepForward and wait for it.
                            pending.Step = true;
                            pending.Tries = 0;
                            pending.Cooldown = 3;
                            player.StepForward();
                        }
                        else
                        {
                            pending.Tries = 0;
                            player.playbackSpeed = 1f;
                            player.frame = targetFrame;
                            player.playbackSpeed = 0f;
                        }
                        ready = false;
                    }
                    else if (pending.Settle > 0)
                    {
                        pending.Settle--;
                        ready = false;
                    }
                    else
                    {
                        Pendings.Remove(player);
                        if (player.playbackSpeed != 0f) player.playbackSpeed = 0f;
                    }
                }
                else if (player.frame != targetFrame)
                {
                    ready = false;
                }
            }
            catch (Exception error)
            {
                Plugin.LogPerformance("video clock seek failed: " + error.GetType().Name);
                ready = false;
            }

            if (Environment.GetEnvironmentVariable("AAVE_TRACE_VIDEO") is { Length: > 0 })
                Trace(exportFrame, player, ready);
        }

        _captureReady = ready || !sawActive;
        if (_captureReady)
        {
            _waitStartedAt = -1f;
            _waitPumps = 0;
        }
        else if (_waitStartedAt < 0f)
        {
            _waitStartedAt = Time.realtimeSinceStartup;
            _waitPumps = 0;
        }
        else if (++_waitPumps >= 600)
        {
            throw new TimeoutException("video clock did not reach its target frame within 600 updates.");
        }
        return _captureReady;
    }

    internal static bool CaptureReady() => _active && _captureReady;

    /// <summary>
    /// Re-applies cached frames only while a player is in its URL-transition
    /// frame (frame zero). A transition can clear the target texture after the
    /// first frame callback but before the consuming camera draws; a second
    /// copy at camera begin keeps the cached pixels visible.
    /// </summary>
    internal static void RefreshCache(long exportFrame)
    {
        if (!_active || !_started) return;
        foreach (var player in UnityEngine.Object.FindObjectsOfType<VideoPlayer>(true))
        {
            if (player == null || !player.isActiveAndEnabled) continue;
            try
            {
                if (DeterministicVideoCache.Has(player.url))
                    DeterministicVideoCache.TryApply(player, exportFrame, _fps);
            }
            catch { }
        }
    }

    internal static bool IsActive => _active && _started;

    private static void Trace(long exportFrame, VideoPlayer player, bool ready, bool cached = false)
    {
        long frameCount = (long)player.frameCount;
        RuntimeVirtualizationHost.Trace("video_clock", new Dictionary<string, object?>
        {
            ["frame"] = exportFrame,
            ["fps"] = _fps,
            ["player_id"] = player.GetInstanceID(),
            ["player_go"] = player.gameObject == null ? "" : player.gameObject.name,
            ["target_frame"] = frameCount > 0
                ? (long)Math.Floor(exportFrame * (double)player.frameRate / _fps) % frameCount
                : -1,
            ["video_frame"] = player.frame,
            ["wrap_pending"] = Pendings.ContainsKey(player),
            ["can_step"] = player.canStep,
            ["frame_count"] = frameCount,
            ["frame_rate"] = player.frameRate,
            ["time"] = player.time,
            ["playing"] = player.isPlaying,
            ["paused"] = player.isPaused,
            ["prepared"] = player.isPrepared,
            ["speed"] = player.playbackSpeed,
            ["ready"] = ready,
            ["time_update_mode"] = player.timeUpdateMode.ToString(),
            ["run_in_background"] = Application.runInBackground,
            ["texture"] = player.targetTexture == null ? "" : player.targetTexture.name,
            ["texture_id"] = player.targetTexture == null ? 0 : player.targetTexture.GetInstanceID(),
            ["url"] = player.url,
            ["cache"] = cached,
            ["cache_frames"] = DeterministicVideoCache.FrameCount(player.url),
            ["cache_rate"] = DeterministicVideoCache.FrameRate(player.url)
        });
    }

    internal static void EndExport()
    {
        if (!_active) return;
        _active = false;
        Pendings.Clear();
        LastTargets.Clear();
        LastUrls.Clear();
        HasWrapped.Clear();
        foreach (var pair in States)
        {
            var player = pair.Key;
            var state = pair.Value;
            if (player == null) continue;
            try
            {
                // Restore speed before seeking; a speed-zero player ignores the
                // frame seek and would otherwise resume from the frozen frame.
                player.playbackSpeed = state.Speed;
                if (player.canSetTime) player.frame = state.Frame;
                player.timeReference = state.TimeReference;
                player.externalReferenceTime = state.ExternalTime;
                if (state.Playing) player.Play();
                else if (state.Paused) player.Pause();
            }
            catch (Exception error)
            {
                Plugin.LogPerformance("video clock restore failed: " + error.GetType().Name);
            }
        }
        States.Clear();
        DeterministicVideoCache.End();
        Application.runInBackground = _runInBackground;
    }
}
