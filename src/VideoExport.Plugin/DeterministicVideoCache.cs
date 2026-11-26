using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Video;

namespace AAVideoExport.Plugin;

/// <summary>
/// Decodes small local video assets once, before the authored timeline starts,
/// into fixed RGBA textures. During export the VideoPlayer is held still and the
/// cached frame texture is bound directly to the story background layer. The
/// cache is keyed only by the video URL and bounded by frame count/size, never by
/// a Mod identity.
/// </summary>
internal static class DeterministicVideoCache
{
    private const int MaxFrameCount = 256;
    private const long MaxClipBytes = 384L * 1024 * 1024;
    private const int MaxFiles = 48;
    private const int MaxProbeMilliseconds = 15000;

    private sealed class Clip
    {
        public string Key = "";
        public string BaseName = "";
        public int Width;
        public int Height;
        public int FrameCount;
        public float FrameRate;
        public Texture2D[] Frames = Array.Empty<Texture2D>();
    }

    private sealed class ShadowTarget
    {
        public RenderTexture Display = null!;
        public RenderTexture Decode = null!;
    }

    private static readonly Dictionary<string, Clip> Clips = new();
    private static readonly Dictionary<VideoPlayer, ShadowTarget> Shadows = new();
    private static string _activeBase = "";
    private static string _pendingBase = "";
    private static long _baseCommitFrame = -1;
    private static bool _hadBase;
    private static readonly List<string> Files = new();
    private static bool _begun;
    private static bool _ready;
    private static int _preferredWidth = 1920;
    private static int _preferredHeight = 1080;
    private static string _lastError = "";

    internal static void Begin(string storyPath)
    {
        End();
        DeterministicBackgroundBinder.Reset();
        _begun = true;
        _ready = false;
        _lastError = "";
        DiscoverPreferredSize();
        Files.Clear();
        if (string.IsNullOrWhiteSpace(storyPath)) return;
        try
        {
            string root = Path.GetDirectoryName(Path.GetFullPath(storyPath)) ?? "";
            if (root.Length == 0 || !Directory.Exists(root)) return;
            var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(IsVideoFile)
                .Where(path =>
                {
                    try { return new FileInfo(path).Length <= 96L * 1024 * 1024; }
                    catch { return false; }
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxFiles)
                .ToList();
            Files.AddRange(candidates);
        }
        catch (Exception error)
        {
            _lastError = error.GetType().Name + ": " + error.Message;
        }
    }

    internal static bool Prepare()
    {
        if (!_begun || _ready) return true;
        _ready = true;
        foreach (string path in Files)
        {
            try
            {
                Clip? clip = DecodeClip(path);
                if (clip != null && clip.Key.Length != 0 && !Clips.ContainsKey(clip.Key))
                    Clips[clip.Key] = clip;
            }
            catch (Exception error)
            {
                _lastError = error.GetType().Name + ": " + error.Message;
            }
        }
        Plugin.LogPerformance("deterministic video cache ready: clips=" + Clips.Count + ", files=" + Files.Count +
            (string.IsNullOrEmpty(_lastError) ? "" : ", last_error=" + _lastError));
        return true;
    }

    internal static bool TryApply(VideoPlayer player, long exportFrame, int exportFps)
    {
        if (!_ready || player == null) return false;
        string url = "";
        try { url = player.url ?? ""; } catch { }
        if (!Has(url)) return false;
        RenderTexture? current = null;
        try { current = player.targetTexture; }
        catch { }
        ShadowTarget shadow;
        if (!Shadows.TryGetValue(player, out shadow))
        {
            if (current == null) return false;
            shadow = CreateShadow(current);
            if (shadow.Display == null || shadow.Decode == null) return false;
            Shadows[player] = shadow;
        }
        if (_baseCommitFrame >= 0 && exportFrame >= _baseCommitFrame)
        {
            _activeBase = _pendingBase;
            _baseCommitFrame = -1;
            if (_activeBase.Length != 0) _hadBase = true;
        }
        Clip? clip = _activeBase.Length == 0 ? null : FindClipByBase(_activeBase);
        if (clip == null && !_hadBase) clip = FindClip(url);
        if (clip == null || clip.FrameCount <= 0) return false;

        if (current != null && !ReferenceEquals(current, shadow.Decode))
        {
            if (!ReferenceEquals(current, shadow.Display))
            {
                if (current.width != shadow.Display.width || current.height != shadow.Display.height)
                {
                    RenderTexture oldDecode = shadow.Decode;
                    RenderTexture? replacement = CreateDecode(current);
                    if (replacement == null) return false;
                    shadow.Decode = replacement;
                    try { oldDecode.Release(); } catch { }
                    UnityEngine.Object.Destroy(oldDecode);
                }
                shadow.Display = current;
            }
            try { player.targetTexture = shadow.Decode; } catch { }
        }
        else if (current == null)
        {
            try { player.targetTexture = shadow.Decode; } catch { }
        }
        try
        {
            player.playbackSpeed = 0.0f;
            if (player.frame <= 0) player.frame = 1;
        }
        catch { }

        int index = (int)(Math.Floor(exportFrame * (double)clip.FrameRate / Math.Max(1, exportFps)) % clip.FrameCount);
        if (index < 0) index = 0;
        Texture2D frame = clip.Frames[index];
        if (frame == null) return false;
        try
        {
            Graphics.Blit(frame, shadow.Display);
            DeterministicBackgroundBinder.Bind(frame);
            return true;
        }
        catch (Exception error)
        {
            _lastError = error.GetType().Name + ": " + error.Message;
            return false;
        }
    }

    internal static bool Has(string url)
    {
        Clip? clip = FindClip(url);
        return clip != null && clip.FrameCount > 0;
    }

    internal static int FrameCount(string url) => FindClip(url)?.FrameCount ?? 0;

    internal static float FrameRate(string url) => FindClip(url)?.FrameRate ?? 0f;

    internal static bool HasBaseName(string baseName)
    {
        if (string.IsNullOrEmpty(baseName)) return false;
        return Clips.Values.Any(clip => string.Equals(clip.BaseName, baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static Clip? FindClipByBase(string baseName)
    {
        if (string.IsNullOrEmpty(baseName)) return null;
        foreach (var clip in Clips.Values)
        {
            if (string.Equals(clip.BaseName, baseName, StringComparison.OrdinalIgnoreCase))
                return clip;
        }
        return null;
    }

    internal static void ApplyBaseCue(string baseName, long frame)
    {
        if (_activeBase.Length == 0 && !string.IsNullOrEmpty(baseName))
        {
            _pendingBase = baseName;
            _baseCommitFrame = frame + 9;
            return;
        }
        if (!string.Equals(baseName, _pendingBase, StringComparison.OrdinalIgnoreCase))
        {
            _pendingBase = baseName;
            _baseCommitFrame = frame + 9;
        }
    }

    internal static void End()
    {
        foreach (var clip in Clips.Values)
        {
            foreach (var frame in clip.Frames)
            {
                if (frame == null) continue;
                UnityEngine.Object.Destroy(frame);
            }
        }
        foreach (var pair in Shadows)
        {
            try
            {
                if (pair.Key != null && pair.Value.Display != null)
                    pair.Key.targetTexture = pair.Value.Display;
            }
            catch { }
            if (pair.Value.Decode != null)
            {
                try { pair.Value.Decode.Release(); } catch { }
                UnityEngine.Object.Destroy(pair.Value.Decode);
            }
        }
        Shadows.Clear();
        Clips.Clear();
        _activeBase = "";
        _pendingBase = "";
        _baseCommitFrame = -1;
        _hadBase = false;
        Files.Clear();
        _begun = false;
        _ready = false;
    }

    private static ShadowTarget CreateShadow(RenderTexture display)
    {
        RenderTexture? decode = CreateDecode(display);
        if (decode == null) return new ShadowTarget { Display = display, Decode = null! };
        return new ShadowTarget { Display = display, Decode = decode };
    }

    private static RenderTexture? CreateDecode(RenderTexture display)
    {
        try
        {
            var decode = new RenderTexture(display.width, display.height, 0, display.format);
            decode.name = "AAVE Video Shadow Decode";
            decode.hideFlags = HideFlags.HideAndDontSave;
            if (!decode.Create())
            {
                UnityEngine.Object.Destroy(decode);
                return null;
            }
            return decode;
        }
        catch (Exception error)
        {
            _lastError = error.GetType().Name + ": " + error.Message;
            return null;
        }
    }

    private static Clip? FindClip(string url)
    {
        string key = NormalizeUrl(url);
        return key.Length == 0 ? null : (Clips.TryGetValue(key, out var clip) ? clip : null);
    }

    private static void DiscoverPreferredSize()
    {
        _preferredWidth = 1920;
        _preferredHeight = 1080;
        long best = 0;
        try
        {
            foreach (var player in UnityEngine.Object.FindObjectsOfType<VideoPlayer>(true))
            {
                if (player == null || !player.isActiveAndEnabled || player.targetTexture == null) continue;
                var texture = player.targetTexture;
                long area = (long)texture.width * texture.height;
                if (area <= best || area > 3840L * 2160L) continue;
                best = area;
                _preferredWidth = texture.width;
                _preferredHeight = texture.height;
            }
        }
        catch { }
    }

    private static Clip? DecodeClip(string path)
    {
        string key = NormalizeFile(path);
        if (key.Length == 0) return null;

        float frameRate = ProbeFrameRate(path);
        if (frameRate <= 0f) return null;
        int width = _preferredWidth, height = _preferredHeight;
        if (width <= 0 || height <= 0) return null;

        string rawPath = Path.Combine(Path.GetTempPath(), "aave-video-" + Guid.NewGuid().ToString("N") + ".rgba");
        try
        {
            string arguments = "-v error -y -i \"" + path + "\" -map 0:v:0 -an -vf scale=" + width + ":" + height +
                " -pix_fmt rgba -vsync 0 -f rawvideo \"" + rawPath + "\"";
            if (!Run(Plugin.FfmpegPath, arguments, MaxProbeMilliseconds)) return null;
            var info = new FileInfo(rawPath);
            long bytes = info.Length;
            long expected = (long)width * height * 4L;
            if (expected <= 0 || bytes <= 0 || bytes % expected != 0) return null;
            long frameCount = bytes / expected;
            if (frameCount <= 0 || frameCount > MaxFrameCount || bytes > MaxClipBytes) return null;
            int frameBytes = (int)expected;

            byte[] raw = File.ReadAllBytes(rawPath);
            var frames = new Texture2D[(int)frameCount];
            for (int i = 0; i < frames.Length; i++)
            {
                var pixels = new byte[frameBytes];
                Buffer.BlockCopy(raw, i * frameBytes, pixels, 0, frameBytes);
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.name = "AAVE Video Cache " + Path.GetFileName(path) + " #" + i;
                texture.hideFlags = HideFlags.HideAndDontSave;
                texture.LoadRawTextureData(pixels);
                texture.Apply(false, true);
                frames[i] = texture;
            }
            return new Clip
            {
                Key = key,
                BaseName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant(),
                Width = width,
                Height = height,
                FrameCount = (int)frameCount,
                FrameRate = frameRate,
                Frames = frames
            };
        }
        finally
        {
            try { if (File.Exists(rawPath)) File.Delete(rawPath); } catch { }
        }
    }

    private static float ProbeFrameRate(string path)
    {
        string arguments = "-v error -select_streams v:0 -show_entries stream=avg_frame_rate,r_frame_rate " +
            "-of default=noprint_wrappers=1:nokey=1 \"" + path + "\"";
        string output = RunCapture(Plugin.FfprobePath, arguments, MaxProbeMilliseconds);
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string text = line.Trim();
            int slash = text.IndexOf('/');
            if (slash <= 0) continue;
            if (!double.TryParse(text.Substring(0, slash), out double numerator)) continue;
            if (!double.TryParse(text.Substring(slash + 1), out double denominator) || denominator <= 0) continue;
            double rate = numerator / denominator;
            if (rate > 0 && rate < 1000) return (float)rate;
        }
        return 0f;
    }

    private static bool Run(string executable, string arguments, int timeoutMilliseconds)
    {
        string output = RunCapture(executable, arguments, timeoutMilliseconds, out int exitCode);
        if (exitCode != 0 && string.IsNullOrWhiteSpace(output)) return false;
        return exitCode == 0;
    }

    private static string RunCapture(string executable, string arguments, int timeoutMilliseconds)
        => RunCapture(executable, arguments, timeoutMilliseconds, out _);

    private static string RunCapture(string executable, string arguments, int timeoutMilliseconds, out int exitCode)
    {
        exitCode = -1;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start);
            if (process == null) return "";
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(); } catch { }
                return stderr;
            }
            exitCode = process.ExitCode;
            return stdout.Length != 0 ? stdout : stderr;
        }
        catch (Exception error)
        {
            _lastError = error.GetType().Name + ": " + error.Message;
            return "";
        }
    }

    private static bool IsVideoFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFile(string pathOrUrl) => NormalizeUrl(pathOrUrl);

    private static string NormalizeUrl(string? url)
    {
        string raw = url?.Trim() ?? "";
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            try { return Path.GetFullPath(Uri.UnescapeDataString(uri.LocalPath)).ToLowerInvariant(); }
            catch { }
        }
        return raw.ToLowerInvariant();
    }
}