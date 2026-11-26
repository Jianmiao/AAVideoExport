using UnityEngine;

namespace AAVideoExport.Plugin;

/// <summary>
/// Generic final-mixer audio plane. The plane deliberately observes the
/// Unity mixer instead of naming individual voice or BGM Mods: every AudioSource
/// that reaches the listener is already part of the exported PCM. It also
/// repairs a globally muted listener for the duration of the export, because a
/// mute is a playback-boundary state, not a per-Mod identity.
/// </summary>
internal static class RuntimeAudioPlane
{
    private static readonly List<AudioSource> Sources = new();
    private static readonly HashSet<AudioSource> Anchored = new();
    private static bool _active;
    private static bool _restoreListenerVolume;
    private static float _listenerVolume = 1f;
    private static int _beginSourceCount;
    private static int _lastPlayingCount;
    private static long _observedFrame = -1;

    internal static int SourceCount => Sources.Count;
    internal static int PlayingCount => _lastPlayingCount;
    internal static bool ListenerRepaired => _restoreListenerVolume;

    internal static void Probe()
    {
        try
        {
            var sources = UnityEngine.Object.FindObjectsOfType<AudioSource>(true);
            RuntimeVirtualizationHost.Probe("audio", RuntimePlaneState.Probed,
                "sources=" + sources.Length + "; listener=" + AudioListener.volume.ToString("0.###"));
        }
        catch (Exception error)
        {
            RuntimeVirtualizationHost.Probe("audio", RuntimePlaneState.Probed, "probe unavailable: " + error.GetType().Name);
        }
    }

    internal static int BeginExport()
    {
        Sources.Clear();
        Anchored.Clear();
        foreach (var source in UnityEngine.Object.FindObjectsOfType<AudioSource>(true))
        {
            if (source == null || !source.isActiveAndEnabled) continue;
            Sources.Add(source);
        }
        _beginSourceCount = Sources.Count;
        // A looping source is the one part of the mixer whose phase survives
        // between independent exports. Rewind it to its authored clip origin
        // so repeated passes observe the same timeline even when the scene has
        // been running before the second pass. This is generic mixer state, not
        // a Mod identity rule.
        int rewound = 0;
        foreach (var source in Sources)
        {
            if (source == null || !source.isActiveAndEnabled || !source.isPlaying || !source.loop || source.clip == null)
                continue;
            try { source.timeSamples = 0; rewound++; Anchored.Add(source); }
            catch (Exception error) { Plugin.LogPerformance("audio loop rewind failed: " + error.GetType().Name); }
        }
        _lastPlayingCount = CountPlaying();
        _listenerVolume = AudioListener.volume;
        // A muted listener is a global playback boundary. Export audio is an
        // explicit request for the authored track, so bypass the mute without
        // changing any individual source volume or mute flag.
        if (_listenerVolume <= 0.0001f)
        {
            AudioListener.volume = 1f;
            _restoreListenerVolume = true;
        }
        _active = true;
        _observedFrame = -1;
        RuntimeVirtualizationHost.Trace("audio_begin", new Dictionary<string, object?>
        {
            ["sources"] = _beginSourceCount,
            ["playing"] = _lastPlayingCount,
            ["rewound_loops"] = rewound,
            ["listener_volume"] = _listenerVolume,
            ["listener_repaired"] = _restoreListenerVolume
        });
        return _beginSourceCount;
    }

    internal static void BeforeCapture(long frame)
    {
        if (!_active) return;
        int anchored = 0;
        foreach (var source in UnityEngine.Object.FindObjectsOfType<AudioSource>(true))
        {
            if (source == null || !source.isActiveAndEnabled) continue;
            if (!Sources.Contains(source)) Sources.Add(source);
            if (!source.isPlaying || source.clip == null || Anchored.Contains(source)) continue;
            // The first authored block can become audible during frame 2 or 3
            // depending on process start jitter. Deferring the first onset
            // anchor to a fixed startup frame keeps two independent exports on
            // the same authored audio timeline without naming any Mod.
            if (frame < 3) continue;
            // A one-shot that became audible between two frame reads starts
            // at an arbitrary offset inside the mixer buffer. Anchoring its
            // first observed frame to the clip origin makes every pass observe
            // the same authored onset; this is generic source state, not a
            // per-Mod rule.
            try
            {
                source.timeSamples = 0;
                Anchored.Add(source);
                anchored++;
            }
            catch (Exception error)
            {
                Plugin.LogPerformance("audio onset anchor failed: " + error.GetType().Name);
            }
        }
        if (anchored > 0)
            RuntimeVirtualizationHost.Trace("audio_anchor", new Dictionary<string, object?>
            {
                ["frame"] = frame,
                ["anchored"] = anchored,
                ["sources"] = Sources.Count
            });
    }

    internal static IDisposable AcquireCommitScope()
    {
        RuntimeVirtualizationHost.Trace("audio_commit", new Dictionary<string, object?>
        {
            ["action"] = "begin",
            ["sources"] = Sources.Count,
            ["playing"] = CountPlaying()
        });
        return new AudioCommitScope();
    }

    private sealed class AudioCommitScope : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RuntimeVirtualizationHost.Trace("audio_commit", new Dictionary<string, object?>
            {
                ["action"] = "end",
                ["sources"] = Sources.Count,
                ["playing"] = CountPlaying()
            });
        }
    }

    internal static void CaptureFrame(long frame, int sampleRate, int channels, long sampleFrames)
    {
        if (!_active) return;
        if (frame == _observedFrame) return;
        _observedFrame = frame;
        int playing = CountPlaying();
        if (playing != _lastPlayingCount || frame % 30 == 0)
        {
            _lastPlayingCount = playing;
            if (frame % 30 == 0)
                RuntimeVirtualizationHost.Trace("audio_frame", new Dictionary<string, object?>
                {
                    ["frame"] = frame,
                    ["sources"] = Sources.Count,
                    ["playing"] = playing,
                    ["sample_rate"] = sampleRate,
                    ["channels"] = channels,
                    ["sample_frames"] = sampleFrames
                });
        }
    }

    internal static void EndExport(long capturedAudioSampleFrames)
    {
        if (!_active) return;
        _active = false;
        if (_restoreListenerVolume)
        {
            try { AudioListener.volume = _listenerVolume; }
            catch (Exception error)
            {
                Plugin.LogPerformance("audio listener restore failed: " + error.GetType().Name);
            }
            _restoreListenerVolume = false;
        }
        RuntimeVirtualizationHost.Trace("audio_end", new Dictionary<string, object?>
        {
            ["sources"] = Sources.Count,
            ["playing"] = CountPlaying(),
            ["listener_volume"] = AudioListener.volume,
            ["captured_sample_frames"] = capturedAudioSampleFrames
        });
        Sources.Clear();
        Anchored.Clear();
    }

    private static int CountPlaying()
    {
        int count = 0;
        foreach (var source in Sources)
            if (source != null && source.isActiveAndEnabled && source.isPlaying) count++;
        return count;
    }
}
