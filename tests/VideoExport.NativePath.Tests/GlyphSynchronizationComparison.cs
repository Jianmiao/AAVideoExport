using System.Text.Json;

namespace AAVideoExport.NativePath.Tests;

internal sealed class GlyphFrameSet
{
    public string Rule { get; set; } = "native-font-atlas-v1";
    public int Width { get; set; }
    public int Height { get; set; }
    public int FrameCount { get; set; }
    public string RawOrigin { get; set; } = "bottom-left";
    public List<GlyphFrameSnapshot> Frames { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}
internal sealed class GlyphFrameSnapshot
{
    public long Frame { get; set; }
    public string Proof { get; set; } = "builtin-manual-postrender-v1";
    public string CameraState { get; set; } = "";
    public int CullingMask { get; set; }
    public float[] Viewport { get; set; } = Array.Empty<float>();
    public List<GlyphLabelSnapshot> Labels { get; set; } = new();
}
internal sealed class GlyphLabelSnapshot
{
    public string Key { get; set; } = "";
    public string InvariantState { get; set; } = "";
    public string Shader { get; set; } = "";
    public string Clipping { get; set; } = "";
    public int Layer { get; set; }
    public bool Active { get; set; }
    public bool Enabled { get; set; }
    public bool RendererEnabled { get; set; }
    public bool RendererVisible { get; set; }
    public bool OriginalGeometry { get; set; }
    public int AtlasWidth { get; set; }
    public int AtlasHeight { get; set; }
    public float[] Vertices { get; set; } = Array.Empty<float>();
    public float[] Colors { get; set; } = Array.Empty<float>();
    public float[] Uvs { get; set; } = Array.Empty<float>();
    public float[] Projected { get; set; } = Array.Empty<float>();
    public float[] Depths { get; set; } = Array.Empty<float>();
}
internal sealed record GlyphFrameDifference(long Frame, int ChangedPixels, int ChangedChannels, int MaxChannelDifference, string[] GlyphQuads);
internal sealed record GlyphComparisonResult(string Contract, bool RawExact, int Frames, long ChangedPixels, long ChangedChannels,
    int MaxChannelDifference, IReadOnlyList<GlyphFrameDifference> Differences);

internal static class GlyphSynchronizationComparison
{
    internal const string Contract = "authored-render equivalence with font-atlas exception";
    internal static GlyphComparisonResult CompareFiles(string firstRaw, string secondRaw, GlyphFrameSet first, GlyphFrameSet second)
    {
        ValidateSet(first); ValidateSet(second);
        Require(first.Rule == second.Rule && first.Width == second.Width && first.Height == second.Height
            && first.FrameCount == second.FrameCount && first.RawOrigin == second.RawOrigin, "Raw frame layout or contract changed.");
        int bytes = checked(first.Width * first.Height * 4);
        long expected = checked((long)bytes * first.FrameCount);
        Require(new FileInfo(firstRaw).Length == expected && new FileInfo(secondRaw).Length == expected,
            "Raw RGBA stream is truncated or has additional frames.");
        using var left = File.OpenRead(firstRaw); using var right = File.OpenRead(secondRaw);
        var a = new byte[bytes]; var b = new byte[bytes];
        var differences = new List<GlyphFrameDifference>();
        long totalPixels = 0, totalChannels = 0; int maximum = 0;
        for (int frame = 0; frame < first.FrameCount; frame++)
        {
            ReadFull(left, a); ReadFull(right, b);
            if (a.AsSpan().SequenceEqual(b)) continue;
            var quads = Eligible(first.Frames[frame], second.Frames[frame], first.Width, first.Height);
            int pixels = 0, channels = 0, max = 0;
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < bytes; index += 4)
            {
                if (a[index] == b[index] && a[index + 1] == b[index + 1] && a[index + 2] == b[index + 2] && a[index + 3] == b[index + 3]) continue;
                int pixel = index / 4, x = pixel % first.Width, y = pixel / first.Width;
                int delta = 0;
                for (int channel = 0; channel < 4; channel++)
                {
                    int value = Math.Abs((int)a[index + channel] - b[index + channel]);
                    if (value != 0) channels++;
                    delta = Math.Max(delta, value);
                }
                Require(delta <= 2, $"Frame {frame} pixel ({x},{y}) exceeds the glyph-only maximum: {delta}/255.");
                var match = quads.FirstOrDefault(q => Covers(q, x + .5, y + .5));
                Require(match != null, $"Frame {frame} pixel ({x},{y}) differs outside an eligible changed-UV glyph triangle.");
                used.Add(match!.Key); pixels++; max = Math.Max(max, delta);
            }
            differences.Add(new(frame, pixels, channels, max, used.OrderBy(x => x, StringComparer.Ordinal).ToArray()));
            totalPixels += pixels; totalChannels += channels; maximum = Math.Max(maximum, max);
        }
        return new(Contract, differences.Count == 0, first.FrameCount, totalPixels, totalChannels, maximum, differences);
    }

    private static void ValidateSet(GlyphFrameSet set)
    {
        Require(set.Rule == "native-font-atlas-v1" && set.RawOrigin == "bottom-left", "Unknown raw image/glyph comparison contract.");
        Require(set.Width is > 0 and <= 16384 && set.Height is > 0 and <= 16384 && set.FrameCount > 0,
            "Invalid raw frame dimensions or count.");
        Require(set.Errors.Count == 0, "Native glyph observation failed: " + string.Join("; ", set.Errors));
        Require(set.Frames.Count == set.FrameCount, "Missing or duplicate post-render frame metadata.");
        for (int i = 0; i < set.Frames.Count; i++) Require(set.Frames[i].Frame == i, "Shifted or unordered post-render frame metadata.");
    }

    private sealed record Quad(string Key, float[] Points, float[] Viewport);
    private static List<Quad> Eligible(GlyphFrameSnapshot a, GlyphFrameSnapshot b, int width, int height)
    {
        Require((a.Proof == "builtin-manual-postrender-v1" || a.Proof == "manual-camera-render-v1") && b.Proof == a.Proof,
            "Unrecognized capture camera/render projection proof.");
        Require(a.CameraState.Length != 0 && a.CameraState == b.CameraState && a.CullingMask == b.CullingMask,
            "Capture camera, target viewport or projection changed.");
        var clip = CameraClipRange(a.CameraState);
        Require(a.Viewport.Length == 4 && b.Viewport.Length == 4 && Same(a.Viewport, b.Viewport)
            && a.Viewport[0] >= 0 && a.Viewport[1] >= 0 && a.Viewport[2] > 0 && a.Viewport[3] > 0
            && a.Viewport[0] + a.Viewport[2] <= width && a.Viewport[1] + a.Viewport[3] <= height,
            "Invalid or changed actual render viewport.");
        Require(a.Labels.Select(l => l.Key).Distinct(StringComparer.Ordinal).Count() == a.Labels.Count
            && b.Labels.Select(l => l.Key).Distinct(StringComparer.Ordinal).Count() == b.Labels.Count, "Ambiguous glyph drawable identity.");
        var right = b.Labels.ToDictionary(l => l.Key, StringComparer.Ordinal);
        Require(a.Labels.Count == b.Labels.Count, "Visible glyph drawable set changed.");
        var result = new List<Quad>();
        foreach (var left in a.Labels)
        {
            Require(left.Key.Length != 0 && right.TryGetValue(left.Key, out _), "A corresponding native glyph drawable is missing.");
            var other = right[left.Key];
            Require(left.InvariantState.Length != 0 && left.InvariantState == other.InvariantState,
                "Glyph text, font, settings, colors, or transforms changed: " + left.Key);
            Require(left.Shader == other.Shader && left.Clipping == other.Clipping && left.OriginalGeometry == other.OriginalGeometry,
                "Glyph shader, clipping or original geometry state changed: " + left.Key);
            Require(left.Layer == other.Layer && left.Active == other.Active && left.Enabled == other.Enabled
                && left.RendererEnabled == other.RendererEnabled && left.RendererVisible == other.RendererVisible, "Glyph visibility state changed.");
            Require(left.AtlasWidth == other.AtlasWidth && left.AtlasHeight == other.AtlasHeight
                && PowerOfTwo(left.AtlasWidth) && PowerOfTwo(left.AtlasHeight), "Unsupported or changed glyph atlas dimensions.");
            int vertices = left.Vertices.Length / 3;
            Require(vertices > 0 && vertices % 4 == 0 && left.Vertices.Length == vertices * 3
                && left.Colors.Length == vertices * 4 && left.Uvs.Length == vertices * 2
                && left.Projected.Length == vertices * 2 && left.Depths.Length == vertices,
                "Incomplete original glyph quad data.");
            Require(Same(left.Vertices, other.Vertices) && Same(left.Colors, other.Colors)
                && Same(left.Projected, other.Projected) && Same(left.Depths, other.Depths), "Glyph mesh/color/projection changed: " + left.Key);
            Require(Finite(left.Uvs) && Finite(other.Uvs) && other.Uvs.Length == left.Uvs.Length
                && left.Uvs.All(v => v >= 0 && v <= 1) && other.Uvs.All(v => v >= 0 && v <= 1)
                && left.Depths.All(d => d > clip.Near && d < clip.Far), "Nonfinite/out-of-atlas UV or glyph touching/crossing the captured camera clip planes.");
            Require(left.Layer is >= 0 and < 32, "Invalid glyph renderer layer.");
            // Unchanged drawables cannot authorize any different pixel. Keep
            // their complete metadata exact, but do not demand mask-eligible
            // shader/clipping behavior from a drawable never added to the mask.
            if (left.Uvs.AsSpan().SequenceEqual(other.Uvs)) continue;
            // The saved AA native run identifies Unlit/Text (not the initially
            // assumed GUI/Text Shader) on its original NGUI font geometry.
            // These are exact names, never a substring-based shader admission.
            Require((left.Shader == "GUI/Text Shader" || left.Shader == "Unlit/Text") && left.Clipping == "None"
                && left.OriginalGeometry, "Unsupported glyph shader, clipping or custom geometry: " + left.Key);
            bool visible = left.Active && left.Enabled && left.RendererEnabled && left.RendererVisible
                && (a.CullingMask & (1 << left.Layer)) != 0;
            for (int v = 0; v < vertices; v += 4)
            {
                int offset = v * 2;
                double dx = (double)other.Uvs[offset] - left.Uvs[offset];
                double dy = (double)other.Uvs[offset + 1] - left.Uvs[offset + 1];
                if (dx == 0 && dy == 0)
                {
                    Require(left.Uvs.AsSpan(offset, 8).SequenceEqual(other.Uvs.AsSpan(offset, 8)), "Glyph UV shape changed without a common translation.");
                    continue;
                }
                for (int corner = 1; corner < 4; corner++)
                    Require((double)other.Uvs[offset + corner * 2] - left.Uvs[offset + corner * 2] == dx
                        && (double)other.Uvs[offset + corner * 2 + 1] - left.Uvs[offset + corner * 2 + 1] == dy,
                        "Glyph UV scale, shear, rotation, or corner offset changed.");
                double px = dx * left.AtlasWidth, py = dy * left.AtlasHeight;
                Require(px == Math.Truncate(px) && py == Math.Truncate(py), "Glyph UV movement is not an exact integer atlas-pixel translation.");
                if (!visible || !Enumerable.Range(v, 4).Any(i => left.Colors[i * 4 + 3] > 0)) continue;
                float[] points = left.Projected.AsSpan(offset, 8).ToArray();
                Require(Area(points[0], points[1], points[2], points[3], points[4], points[5]) != 0
                    && Area(points[0], points[1], points[4], points[5], points[6], points[7]) != 0, "Degenerate glyph triangle projection.");
                result.Add(new(left.Key + "/quad-" + v / 4, points, a.Viewport));
            }
        }
        return result;
    }
    private static (float Near, float Far) CameraClipRange(string cameraState)
    {
        try
        {
            using var document = JsonDocument.Parse(cameraState);
            var camera = document.RootElement;
            Require(camera.ValueKind == JsonValueKind.Object
                && camera.TryGetProperty("orthographic", out var orthographic)
                && (orthographic.ValueKind == JsonValueKind.True || orthographic.ValueKind == JsonValueKind.False)
                && camera.TryGetProperty("nearClipPlane", out var nearValue) && nearValue.ValueKind == JsonValueKind.Number
                && camera.TryGetProperty("farClipPlane", out var farValue) && farValue.ValueKind == JsonValueKind.Number,
                "Missing captured camera projection type or clip-plane metadata.");
            float near = camera.GetProperty("nearClipPlane").GetSingle(), far = camera.GetProperty("farClipPlane").GetSingle();
            bool isOrthographic = camera.GetProperty("orthographic").GetBoolean();
            Require(float.IsFinite(near) && float.IsFinite(far) && near < far && (isOrthographic || near > 0),
                "Invalid captured camera clip range or nonpositive perspective near plane.");
            // NGUI's native orthographic camera has near=-10, far=10 and
            // visible glyphs at depth=0. Require the actual captured open clip
            // interval; a perspective-only depth>0 test rejects valid geometry.
            return (near, far);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid captured camera projection metadata.", error); }
    }
    private static bool Covers(Quad quad, double x, double y)
    {
        var view = quad.Viewport;
        if (x < view[0] || y < view[1] || x >= view[0] + view[2] || y >= view[1] + view[3]) return false;
        var p = quad.Points;
        return Triangle(x, y, p[0], p[1], p[2], p[3], p[4], p[5]) || Triangle(x, y, p[0], p[1], p[4], p[5], p[6], p[7]);
    }
    private static bool Triangle(double x, double y, double ax, double ay, double bx, double by, double cx, double cy)
    {
        double a = Area(ax, ay, bx, by, x, y), b = Area(bx, by, cx, cy, x, y), c = Area(cx, cy, ax, ay, x, y);
        // Closed triangle at exact pixel centers: no label rectangle, rounding
        // expansion, border dilation or geometric epsilon is used.
        return a >= 0 && b >= 0 && c >= 0 || a <= 0 && b <= 0 && c <= 0;
    }
    private static double Area(double ax, double ay, double bx, double by, double cx, double cy) => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
    private static bool PowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
    private static bool Same(float[] a, float[] b) => Finite(a) && Finite(b) && a.AsSpan().SequenceEqual(b);
    private static bool Finite(float[] values) => values.All(float.IsFinite);
    private static void ReadFull(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            Require(read > 0, "Raw RGBA stream ended during a frame."); offset += read;
        }
    }
    private static void Require(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
}
