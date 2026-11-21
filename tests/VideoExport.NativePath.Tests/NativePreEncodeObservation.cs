using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AAVideoExport.NativePath.Tests;

// External diagnostic only. Never installed unless explicitly requested, and
// never reads Unity objects from the encoder worker. It does not replace frames,
// advance state, modify clocks, or relax the decoded-pixel comparison.
internal static class NativePreEncodeObservation
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly object Sync = new();
    private static Harmony? hooks;
    private static Attempt? current;
    private static readonly Dictionary<int, string> AttemptDirectories = new();
    private static readonly HashSet<long> SavedFrames = new() { 0, 14, 18, 19, 20, 21, 22, 109, 121, 139, 188, 250, 251, 266, 452 };
    private static readonly HashSet<long> GeometryFrames = new() { 14, 19, 21, 121, 139, 188, 452 };
    internal static bool FontAtlasEquivalenceEnabled => Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_FONT_ATLAS_EQUIVALENCE") == "1";
    internal static bool Enabled => FontAtlasEquivalenceEnabled || Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_PREENCODE") == "1";

    internal static void Install()
    {
        if (!Enabled || hooks != null) return;
        hooks = new Harmony("aave.test.preencode-observation");
        hooks.Patch(typeof(ExportSession).GetMethod(nameof(ExportSession.WriteFrame), new[] { typeof(byte[]), typeof(int) })!,
            prefix: new HarmonyMethod(typeof(NativePreEncodeObservation), nameof(BeforeManagedFrame)));
        hooks.Patch(typeof(ExportSession).GetMethod(nameof(ExportSession.WriteFrame), new[] { typeof(IntPtr), typeof(int) })!,
            prefix: new HarmonyMethod(typeof(NativePreEncodeObservation), nameof(BeforeNativeFrame)));
        Type pipeline = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.FramePipeline", true)!;
        hooks.Patch(pipeline.GetMethod("Capture", Members, null, Type.EmptyTypes, null)!,
            prefix: new HarmonyMethod(typeof(NativePreEncodeObservation), nameof(BeforeCapture)));
        if (FontAtlasEquivalenceEnabled)
        {
            Type scope = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeCaptureScope", true)!;
            hooks.Patch(scope.GetMethod("RenderFrame", Members, null, new[] { typeof(RenderTexture) }, null)!,
                prefix: new HarmonyMethod(typeof(NativePreEncodeObservation), nameof(BeforeGlyphRenderFrame)),
                transpiler: new HarmonyMethod(typeof(NativePreEncodeObservation), nameof(ObserveManualRenderCall)),
                finalizer: new HarmonyMethod(typeof(NativePreEncodeObservation), nameof(FinishGlyphRenderFrame)));
        }
        Plugin.Report("PATH-CHECK PREENCODE enabled; diagnostic copies excluded from performance runs");
    }

    internal static void BeginAttempt(int attempt, ExportHost host, ExportOptions options, string evidenceDirectory)
    {
        if (!Enabled) return;
        Install();
        lock (Sync)
        {
            if (current != null) throw new InvalidOperationException("Previous pre-encode observation was not finished.");
            var canvas = options.GetCanvasLayout();
            string directory = Path.Combine(evidenceDirectory, "preencode-attempt-" + attempt.ToString("00"));
            Directory.CreateDirectory(directory);
            current = new Attempt(attempt, host, canvas.CaptureWidth, canvas.CaptureHeight, options.Fps, directory);
            AttemptDirectories.Add(attempt, directory);
        }
    }

    internal static void FinishAttempt(string outcome)
    {
        lock (Sync)
        {
            var attempt = current;
            if (attempt == null) return;
            // The harness calls this after the real writer/cleanup has settled.
            long? fullBytes = attempt.FullFrames?.Length;
            attempt.FullFrames?.Dispose();
            var report = new
            {
                attempt.Index, Outcome = outcome, attempt.Width, attempt.Height, attempt.Fps,
                PixelFormat = "rgba", Orientation = "original pre-encoder buffer; native capture is normally bottom-up",
                FramesObserved = attempt.Frames.Count, FramesAccepted = attempt.Session?.FramesAccepted,
                FramesWritten = attempt.Session?.FramesWritten,
                RollingRgbaSha256 = Convert.ToHexString(attempt.Rolling.GetHashAndReset()),
                FullRgbaStream = fullBytes.HasValue ? "all-frames.rgba" : null, FullRgbaBytes = fullBytes,
                Frames = attempt.Frames, CaptureStates = attempt.States, LabelGeometry = attempt.LabelGeometry, Errors = attempt.Errors
            };
            File.WriteAllText(Path.Combine(attempt.Directory, "observation.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            if (FontAtlasEquivalenceEnabled)
            {
                attempt.Glyphs.FrameCount = attempt.Frames.Count;
                attempt.Glyphs.Errors.AddRange(attempt.Errors);
                var errors = attempt.Glyphs.Errors.GroupBy(error => System.Text.RegularExpressions.Regex.Replace(error, @"raw frame \d+", "raw frame <index>"))
                    .Select(group => new { Reason = group.Key, Count = group.Count(), First = group.First(), Last = group.Last() }).ToArray();
                attempt.Glyphs.Errors.Clear();
                attempt.Glyphs.Errors.AddRange(errors.Select(error => error.Reason + " (occurrences=" + error.Count + ")"));
                File.WriteAllText(Path.Combine(attempt.Directory, "glyph-frames.json"), JsonSerializer.Serialize(attempt.Glyphs));
                File.WriteAllText(Path.Combine(attempt.Directory, "render-path-observation.json"), JsonSerializer.Serialize(new
                {
                    Proof = "manual-camera-render-v1", CompletedManualRenders = attempt.Glyphs.Frames.Count,
                    Paths = attempt.RenderPaths, Errors = errors
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            attempt.Rolling.Dispose(); current = null;
            Plugin.Report($"PATH-CHECK PREENCODE attempt={attempt.Index}; frames={attempt.Frames.Count}; states={attempt.States.Count}; errors={attempt.Errors.Count}");
        }
    }

    internal static GlyphComparisonResult CompareCompletedAttempts(int firstAttempt, int secondAttempt)
    {
        if (!FontAtlasEquivalenceEnabled) throw new InvalidOperationException("Native font-atlas equivalence requires its explicit development test flag.");
        string first = AttemptDirectories[firstAttempt], second = AttemptDirectories[secondAttempt];
        var a = JsonSerializer.Deserialize<GlyphFrameSet>(File.ReadAllText(Path.Combine(first, "glyph-frames.json")))
            ?? throw new InvalidDataException("First native glyph frame evidence is missing.");
        var b = JsonSerializer.Deserialize<GlyphFrameSet>(File.ReadAllText(Path.Combine(second, "glyph-frames.json")))
            ?? throw new InvalidDataException("Second native glyph frame evidence is missing.");
        GlyphComparisonResult result;
        try { result = GlyphSynchronizationComparison.CompareFiles(Path.Combine(first, "all-frames.rgba"), Path.Combine(second, "all-frames.rgba"), a, b); }
        catch (InvalidDataException error)
        {
            File.WriteAllText(Path.Combine(second, "authored-render-comparison-failed.json"), JsonSerializer.Serialize(new
            {
                Contract = GlyphSynchronizationComparison.Contract, firstAttempt, secondAttempt, Error = error.Message,
                ExplicitTestFlag = "AA_VIDEOEXPORT_TEST_FONT_ATLAS_EQUIVALENCE=1", ComparatorRelaxedGlobally = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        File.WriteAllText(Path.Combine(second, "authored-render-comparison.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    // Instrument the managed call site in the external test assembly. Unity's
    // FireOnPostRender managed wrapper is not called by this IL2CPP manual path.
    // The replacement invokes the same Camera.Render exactly once, with both
    // observation boundaries inside RenderFrame's target/aspect/rect try/finally.
    private static IEnumerable<CodeInstruction> ObserveManualRenderCall(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.Select(i => new CodeInstruction(i)).ToArray();
        var render = typeof(Camera).GetMethod(nameof(Camera.Render), Type.EmptyTypes)!;
        var calls = code.Where(i => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && Equals(i.operand, render)).ToArray();
        if (calls.Length != 1) throw new InvalidDataException("Native glyph observation requires exactly one direct Camera.Render call.");
        calls[0].opcode = OpCodes.Call;
        calls[0].operand = typeof(NativePreEncodeObservation).GetMethod(nameof(RenderObservedCamera), Members)!;
        return code;
    }

    private static void BeforeGlyphRenderFrame(object __instance, RenderTexture __0, out GlyphRenderBinding? __state)
    {
        __state = null;
        lock (Sync)
        {
            var attempt = current;
            if (attempt == null || !FontAtlasEquivalenceEnabled || !ReferenceEquals(Field(attempt.Host, "_native"), __instance)) return;
            try
            {
                if (attempt.PendingGlyphRender != null) throw new InvalidDataException("Nested native glyph render frame is unsupported.");
                object pipeline = Field(attempt.Host, "_frames") ?? throw new InvalidDataException("Native capture pipeline is missing.");
                var target = Field(pipeline, "_renderTarget") as RenderTexture;
                var camera = __instance.GetType().GetProperty("LastCamera", Members)?.GetValue(__instance) as Camera;
                if (camera == null || target == null || __0 != target) throw new InvalidDataException("Native glyph camera/render target binding is missing or changed.");
                if (__instance.GetType().GetProperty("AutomaticCameraCapture", Members)?.GetValue(__instance) is not false)
                    throw new InvalidDataException("Font-atlas observation requires AutomaticCameraCapture=false at the directly observed manual call.");
                long frame = (long)Field(attempt.Host, "_captured")!;
                if (frame != attempt.Glyphs.Frames.Count) throw new InvalidDataException("Native glyph frame observations are duplicated or shifted.");
                __state = new GlyphRenderBinding(attempt, __instance, camera, target, frame);
                attempt.PendingGlyphRender = __state;
            }
            catch (Exception error) { attempt.Glyphs.Errors.Add("bind-render-glyphs: " + error.GetBaseException().Message); }
        }
    }

    private static void RenderObservedCamera(Camera camera)
    {
        GlyphRenderBinding? binding;
        lock (Sync)
        {
            binding = current?.PendingGlyphRender;
            if (binding != null)
            {
                try
                {
                    if (binding.Started) throw new InvalidDataException("Native glyph Camera.Render was invoked more than once for a frame.");
                    binding.Started = true;
                    binding.Before = CaptureGlyphCameraState(binding, camera);
                }
                catch (Exception error) { binding.Attempt.Glyphs.Errors.Add("pre-render-glyphs: " + error.GetBaseException().Message); }
            }
        }
        // Do not catch or replace the native exception. RenderFrame's original
        // finally restores the camera, and its finalizer invalidates evidence.
        camera.Render();
        if (binding == null || binding.Before == null) return;
        lock (Sync)
        {
            try
            {
                var after = CaptureGlyphCameraState(binding, camera);
                if (binding.Before.CameraState != after.CameraState || binding.Before.CullingMask != after.CullingMask
                    || !binding.Before.Viewport.AsSpan().SequenceEqual(after.Viewport))
                    throw new InvalidDataException("Native glyph camera projection/viewport changed during Camera.Render.");
                CaptureGlyphLabels(camera, after);
                binding.Attempt.Glyphs.Frames.Add(after);
                binding.Completed = true;
            }
            catch (Exception error) { binding.Attempt.Glyphs.Errors.Add("post-render-glyphs: " + error.GetBaseException().Message); }
        }
    }

    private static Exception? FinishGlyphRenderFrame(Exception? __exception, GlyphRenderBinding? __state)
    {
        if (__state == null) return __exception;
        lock (Sync)
        {
            if (__exception != null) __state.Attempt.Glyphs.Errors.Add("native-glyph-render failed: " + __exception.GetBaseException().Message);
            else if (!__state.Completed) __state.Attempt.Glyphs.Errors.Add("Native glyph render completed without a sealed observation.");
            if (ReferenceEquals(__state.Attempt.PendingGlyphRender, __state)) __state.Attempt.PendingGlyphRender = null;
        }
        return __exception;
    }

    private static GlyphFrameSnapshot CaptureGlyphCameraState(GlyphRenderBinding binding, Camera camera)
    {
        var attempt = binding.Attempt;
        var target = binding.Target;
        if (!ReferenceEquals(current, attempt) || !ReferenceEquals(attempt.PendingGlyphRender, binding)
            || !ReferenceEquals(Field(attempt.Host, "_native"), binding.Scope) || camera != binding.Camera)
            throw new InvalidDataException("Native glyph render camera/frame binding changed.");
        object? pipeline = Field(attempt.Host, "_frames");
        if (pipeline == null || Field(pipeline, "_renderTarget") is not RenderTexture pipelineTarget
            || pipelineTarget != target || camera.targetTexture != target)
            throw new InvalidDataException("Native glyph render target changed before observation was sealed.");
        long frame = (long)Field(attempt.Host, "_captured")!;
        if (frame != binding.Frame || frame != attempt.Glyphs.Frames.Count)
            throw new InvalidDataException("Native glyph render frame was shifted or duplicated.");
        if (target.width != attempt.Width || target.height != attempt.Height || target.antiAliasing != 1)
            throw new InvalidDataException("Font-atlas projection target dimensions/MSAA are unsupported.");
        string renderPath = CaptureRenderPath(binding, camera);
        var viewport = camera.pixelRect;
        var view = Matrix(camera.worldToCameraMatrix);
        var projection = Matrix(camera.projectionMatrix);
        var rect = new[] { camera.rect.x, camera.rect.y, camera.rect.width, camera.rect.height };
        var pixels = new[] { viewport.x, viewport.y, viewport.width, viewport.height };
        if (!view.All(float.IsFinite) || !projection.All(float.IsFinite) || !rect.All(float.IsFinite) || !pixels.All(float.IsFinite)
            || viewport.x < 0 || viewport.y < 0 || viewport.width <= 0 || viewport.height <= 0
            || viewport.x + viewport.width > target.width || viewport.y + viewport.height > target.height)
            throw new InvalidDataException("Native glyph camera projection/viewport is nonfinite or outside the capture target.");
        return new GlyphFrameSnapshot
        {
            Frame = frame, Proof = "manual-camera-render-v1", CullingMask = camera.cullingMask, Viewport = pixels,
            CameraState = JsonSerializer.Serialize(new
            {
                RenderPath = renderPath,
                Path = ScenePath(camera.transform), camera.orthographic, camera.orthographicSize, camera.fieldOfView,
                camera.nearClipPlane, camera.farClipPlane, camera.aspect, camera.depth,
                ClearFlags = camera.clearFlags.ToString(), View = view, Projection = projection, Rect = rect,
                target.width, target.height, Format = target.format.ToString(), target.antiAliasing
            })
        };
    }

    private static string CaptureRenderPath(GlyphRenderBinding binding, Camera camera)
    {
        // A configured SRP asset does not identify which call captured this
        // frame. On AA's observed callback-unavailable fallback the call above
        // is the real manual Camera.Render, not an automatic SRP callback.
        // Admit only the native URP asset type observed for this host; retain
        // native identity locally and stable configuration in repeat evidence.
        var asset = GraphicsSettings.currentRenderPipeline;
        var pipeline = RenderPipelineManager.currentPipeline;
        string? assetType = asset?.GetIl2CppType().FullName;
        string? pipelineType = pipeline?.GetIl2CppType().FullName;
        var urp = asset?.TryCast<UniversalRenderPipelineAsset>();
        var data = camera.GetComponent<UniversalAdditionalCameraData>();
        int stackCount = 0;
        if (data != null)
        {
            // Read the stored list, not cameraStack/scriptableRenderer getters:
            // those may lazily create a renderer merely for observation.
            var stackProperty = data.GetType().GetProperty("m_Cameras", Members);
            var stackField = data.GetType().GetField("m_Cameras", Members);
            if (stackProperty == null && stackField == null) throw new InvalidDataException("Native camera stack metadata is unavailable.");
            object? stack = stackProperty != null ? stackProperty.GetValue(data) : stackField!.GetValue(data);
            if (stack != null)
                stackCount = (int)(stack.GetType().GetProperty("Count", Members)?.GetValue(stack)
                    ?? throw new InvalidDataException("Native camera stack count is unavailable."));
        }
        var state = new
        {
            AutomaticCapture = binding.Scope.GetType().GetProperty("AutomaticCameraCapture", Members)?.GetValue(binding.Scope),
            AssetNativeType = assetType, AssetName = asset?.name,
            AssetSettings = urp == null ? null : new { urp.renderScale, urp.msaaSampleCount, urp.supportsHDR },
            PipelineNativeType = pipelineType,
            Camera = new { CameraType = camera.cameraType.ToString(), camera.allowHDR, camera.allowMSAA, camera.allowDynamicResolution, camera.stereoEnabled },
            CameraData = data == null ? null : new
            {
                NativeType = data.GetIl2CppType().FullName, RenderType = data.renderType.ToString(), StackCount = stackCount,
                data.renderPostProcessing, Antialiasing = data.antialiasing.ToString(), AntialiasingQuality = data.antialiasingQuality.ToString(),
                data.stopNaN, data.dithering, data.requiresDepthTexture, data.requiresColorTexture
            }
        };
        string stableState = JsonSerializer.Serialize(state);
        string identity = JsonSerializer.Serialize(new
        {
            AssetPointer = asset?.Pointer.ToString(), AssetInstance = asset?.GetInstanceID(),
            PipelinePointer = pipeline?.Pointer.ToString(), CameraDataPointer = data?.Pointer.ToString()
        });
        string key = stableState + identity;
        if (binding.Attempt.RenderPathKeys.Add(key) && binding.Attempt.RenderPaths.Count < 8)
        {
            binding.Attempt.RenderPaths.Add(new { Frame = binding.Frame, State = state, Identity = identity });
            if (binding.Attempt.RenderPaths.Count == 1)
                Plugin.Report("PATH-CHECK GLYPH manual render path=" + stableState);
        }
        if (asset != null && (assetType != "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset" || urp == null))
            throw new InvalidDataException("Unsupported native manual capture pipeline asset: " + assetType + "; " + stableState);
        if (pipeline != null && pipelineType != "UnityEngine.Rendering.Universal.UniversalRenderPipeline")
            throw new InvalidDataException("Unsupported native active render pipeline: " + pipelineType + "; " + stableState);
        if (state.AutomaticCapture is not false || (data != null && (data.renderType.ToString() != "Base" || stackCount != 0)))
            throw new InvalidDataException("Unsupported automatic or overlay/stacked manual camera render path: " + stableState);
        if (binding.RenderPathIdentity != null && (binding.RenderPathIdentity != identity || binding.RenderPathState != stableState))
            throw new InvalidDataException("Native manual camera render path identity or postprocessing state changed during Camera.Render.");
        binding.RenderPathIdentity = identity; binding.RenderPathState = stableState;
        return stableState;
    }

    private static void CaptureGlyphLabels(Camera camera, GlyphFrameSnapshot result)
    {
                var viewport = camera.pixelRect;
                foreach (var label in UnityEngine.Object.FindObjectsOfType<UILabel>())
                {
                    if (label == null || !label.enabled || !label.gameObject.activeInHierarchy || label.finalAlpha <= 0) continue;
                    var draw = label.drawCall; var geometry = label.geometry;
                    if (draw == null || draw.mRenderer == null || !draw.mRenderer.enabled || !draw.gameObject.activeInHierarchy
                        || geometry == null || geometry.verts.Count == 0) continue;
                    int layer = draw.gameObject.layer;
                    if ((camera.cullingMask & (1 << layer)) == 0 || !draw.mRenderer.isVisible) continue;
                    var renderer = draw.mRenderer; var material = renderer.sharedMaterial; var texture = material?.mainTexture;
                    if (material == null || texture == null) throw new InvalidDataException("Actual glyph draw material or atlas is unavailable.");
                    int count = geometry.verts.Count;
                    if (geometry.mRtpVerts.Count != count || geometry.uvs.Count != count || geometry.cols.Count != count)
                        throw new InvalidDataException("Actual transformed glyph geometry does not match the original widget geometry.");
                    var positions = new float[count * 3]; var originals = new float[count * 3];
                    var colors = new float[count * 4]; var uvs = new float[count * 2];
                    var projected = new float[count * 2]; var depths = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        var p = geometry.mRtpVerts[i]; var original = geometry.verts[i]; var uv = geometry.uvs[i]; var color = geometry.cols[i];
                        positions[i * 3] = p.x; positions[i * 3 + 1] = p.y; positions[i * 3 + 2] = p.z;
                        originals[i * 3] = original.x; originals[i * 3 + 1] = original.y; originals[i * 3 + 2] = original.z;
                        colors[i * 4] = color.r; colors[i * 4 + 1] = color.g; colors[i * 4 + 2] = color.b; colors[i * 4 + 3] = color.a;
                        uvs[i * 2] = uv.x; uvs[i * 2 + 1] = uv.y;
                        var screen = camera.WorldToViewportPoint(draw.transform.TransformPoint(p));
                        projected[i * 2] = viewport.x + screen.x * viewport.width;
                        projected[i * 2 + 1] = viewport.y + screen.y * viewport.height;
                        depths[i] = screen.z;
                    }
                    string clipping = "None";
                    for (var panel = label.panel; panel != null; panel = panel.parentPanel)
                        if (panel.clipping != UIDrawCall.Clipping.None) clipping = panel.clipping.ToString();
                    var font = label.trueTypeFont;
                    result.Labels.Add(new GlyphLabelSnapshot
                    {
                        Key = ScenePath(label.transform), Shader = material.shader?.name ?? "", Clipping = clipping, Layer = layer,
                        Active = true, Enabled = true, RendererEnabled = true, RendererVisible = true,
                        OriginalGeometry = geometry.onCustomWrite == null && font != null,
                        AtlasWidth = texture.width, AtlasHeight = texture.height,
                        Vertices = positions, Colors = colors, Uvs = uvs, Projected = projected, Depths = depths,
                        InvariantState = JsonSerializer.Serialize(new
                        {
                            label.name, Text = label.text, Processed = label.mProcessedText, label.fontSize, label.finalFontSize,
                            Style = label.fontStyle.ToString(), Font = font?.name, FontInstance = font?.GetInstanceID(),
                            Texture = texture.name, TextureInstance = texture.GetInstanceID(), texture.width, texture.height,
                            Filter = texture.filterMode.ToString(), Wrap = texture.wrapMode.ToString(),
                            Material = material.name, Shader = material.shader?.name, material.renderQueue,
                            Keywords = material.shaderKeywords?.ToArray(),
                            TextureScale = new[] { material.mainTextureScale.x, material.mainTextureScale.y },
                            TextureOffset = new[] { material.mainTextureOffset.x, material.mainTextureOffset.y },
                            label.alpha, label.finalAlpha, LabelToWorld = Matrix(label.transform.localToWorldMatrix),
                            DrawToWorld = Matrix(draw.transform.localToWorldMatrix), OriginalVertices = originals,
                            renderer.sortingLayerID, renderer.sortingOrder
                        })
                    });
                }
    }
    private static string ScenePath(Transform value)
    {
        var parts = new List<string>();
        for (var node = value; node != null; node = node.parent) parts.Add(node.name + "[" + node.GetSiblingIndex() + "]");
        parts.Reverse(); return value.gameObject.scene.name + "/" + string.Join("/", parts);
    }
    private static float[] Matrix(Matrix4x4 m) => new[] { m.m00, m.m01, m.m02, m.m03, m.m10, m.m11, m.m12, m.m13,
        m.m20, m.m21, m.m22, m.m23, m.m30, m.m31, m.m32, m.m33 };

    internal static void Shutdown()
    {
        FinishAttempt("observer-shutdown");
        hooks?.UnpatchSelf(); hooks = null;
    }

    private static void BeforeCapture(object __instance)
    {
        lock (Sync)
        {
            var attempt = current;
            if (attempt == null) return;
            try
            {
                var session = (ExportSession?)Field(__instance, "_encoder");
                var owned = (ExportSession?)Field(attempt.Host, "_session");
                if (session == null || !ReferenceEquals(session, owned)) return;
                attempt.Session = session;
                long frame = (long)Field(attempt.Host, "_captured")!;
                if (FontAtlasEquivalenceEnabled && (attempt.PendingGlyphRender != null || attempt.Glyphs.Frames.Count != frame + 1
                    || attempt.Glyphs.Frames[^1].Frame != frame))
                    attempt.Glyphs.Errors.Add("Missing, incomplete or shifted capture-time glyph observation for raw frame " + frame + ".");
                var title = PlayerSettings.TitleSettings.instance;
                object? animation = title?.titleAnimation;
                string? clipName = title?.titleAnimation?.clip?.name;
                object? animationState = null;
                if (animation != null && clipName != null)
                    animationState = animation.GetType().GetProperty("Item", Members)?.GetValue(animation, new object[] { clipName });
                Type clock = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeExportClock", true)!;
                attempt.States.Add(new
                {
                    Frame = frame, UnityFrame = Time.frameCount, NativeTime = Time.time, NativeDelta = Time.deltaTime,
                    NativeRealtime = Time.realtimeSinceStartup, CaptureDelta = Time.captureDeltaTime,
                    ClockFrame = StaticField(clock, "_timelineFrame"), ClockSeconds = StaticField(clock, "_elapsedSeconds"),
                    ClockNow = StaticField(clock, "_now"), ClockDelta = StaticField(clock, "_delta"), ClockSnapshotUnityFrame = StaticField(clock, "_snapshotFrame"),
                    TitlePresent = title != null, TitleRootActive = title?.titleRoot?.activeInHierarchy,
                    Alpha = Values(title?.tweenAlpha, "enabled", "mStarted", "mStartTime", "tweenFactor", "amountPerDelta", "duration", "delay", "ignoreTimeScale", "value"),
                    TitleText = Values(title?.titleText, "alpha", "finalAlpha", "text"),
                    HeaderText = Values(title?.headerText, "alpha", "finalAlpha", "text"),
                    Animation = Values(animation, "enabled", "isPlaying"), Clip = clipName,
                    AnimationState = Values(animationState, "time", "normalizedTime", "speed", "length", "enabled", "weight")
                });
                if (GeometryFrames.Contains(frame))
                    foreach (var label in UnityEngine.Object.FindObjectsOfType<UILabel>())
                    {
                        if (label == null || !label.enabled || !label.gameObject.activeInHierarchy) continue;
                        string text = label.text ?? "";
                        if (!text.Contains("AAVE", StringComparison.Ordinal) && !text.Contains("Official authoring", StringComparison.Ordinal)) continue;
                        var geometry = label.geometry;
                        var vertices = new List<object>(); var uvs = new List<object>(); var colors = new List<object>();
                        if (geometry != null)
                        {
                            for (int i = 0; i < geometry.verts.Count; i++) { var v = geometry.verts[i]; vertices.Add(new { v.x, v.y, v.z }); }
                            for (int i = 0; i < geometry.uvs.Count; i++) { var uv = geometry.uvs[i]; uvs.Add(new { uv.x, uv.y }); }
                            for (int i = 0; i < geometry.cols.Count; i++) { var color = geometry.cols[i]; colors.Add(new { color.r, color.g, color.b, color.a }); }
                        }
                        var position = label.transform.localPosition; var scale = label.transform.localScale;
                        var rotation = label.transform.localRotation; var texture = label.mainTexture;
                        var matrix = label.transform.localToWorldMatrix;
                        attempt.LabelGeometry.Add(new
                        {
                            Frame = frame, label.name, Text = text, Processed = label.mProcessedText,
                            label.fontSize, label.finalFontSize, Style = label.fontStyle.ToString(),
                            TrueTypeFont = label.trueTypeFont?.name, Texture = texture?.name,
                            TextureWidth = texture?.width, TextureHeight = texture?.height,
                            TextureInstance = texture?.GetInstanceID(), Material = label.material?.name,
                            Filter = texture?.filterMode.ToString(), Wrap = texture?.wrapMode.ToString(),
                            Shader = label.material?.shader?.name, label.alpha, label.finalAlpha,
                            Position = new { position.x, position.y, position.z }, Scale = new { scale.x, scale.y, scale.z },
                            Rotation = new { rotation.x, rotation.y, rotation.z, rotation.w },
                            LocalToWorld = new[] { matrix.m00, matrix.m01, matrix.m02, matrix.m03, matrix.m10, matrix.m11, matrix.m12, matrix.m13,
                                matrix.m20, matrix.m21, matrix.m22, matrix.m23, matrix.m30, matrix.m31, matrix.m32, matrix.m33 },
                            Vertices = vertices, Uvs = uvs, Colors = colors
                        });
                    }
            }
            catch (Exception error) { attempt.Errors.Add("capture-state: " + error.GetBaseException().Message); }
        }
    }

    private static void BeforeManagedFrame(ExportSession __instance, byte[] __0, int __1)
    {
        if (__0 == null || __1 < 0 || __1 > __0.Length) return;
        ObserveFrame(__instance, __0.AsSpan(0, __1));
    }
    private static unsafe void BeforeNativeFrame(ExportSession __instance, IntPtr __0, int __1)
    {
        if (__0 == IntPtr.Zero || __1 <= 0) return;
        ObserveFrame(__instance, new ReadOnlySpan<byte>((void*)__0, __1));
    }
    private static void ObserveFrame(ExportSession session, ReadOnlySpan<byte> rgba)
    {
        lock (Sync)
        {
            var attempt = current;
            if (attempt == null || !ReferenceEquals(attempt.Session, session)) return;
            try
            {
                long frame = session.FramesAccepted;
                if (rgba.Length != checked(attempt.Width * attempt.Height * 4))
                    throw new InvalidOperationException("Observed frame dimensions differ from the declared native canvas.");
                if (frame != attempt.Frames.Count)
                    throw new InvalidOperationException($"Observed frame order {frame} follows {attempt.Frames.Count} prior frames.");
                string hash = Convert.ToHexString(SHA256.HashData(rgba));
                attempt.Rolling.AppendData(rgba);
                attempt.FullFrames?.Write(rgba);
                string? file = null;
                if (SavedFrames.Contains(frame))
                {
                    file = $"frame-{frame:000000}.rgba";
                    using var output = new FileStream(Path.Combine(attempt.Directory, file), FileMode.CreateNew, FileAccess.Write);
                    output.Write(rgba);
                }
                attempt.Frames.Add(new FrameEvidence(frame, rgba.Length, hash, file));
            }
            catch (Exception error) { attempt.Errors.Add("pre-encode: " + error.GetBaseException().Message); }
        }
    }
    private static object? Field(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value);
    private static object? StaticField(Type type, string name) => type.GetField(name, Members)?.GetValue(null);
    private static Dictionary<string, object?>? Values(object? value, params string[] names)
    {
        if (value == null) return null;
        var result = new Dictionary<string, object?>();
        foreach (string name in names)
        {
            try
            {
                var property = value.GetType().GetProperty(name, Members);
                result[name] = property != null ? property.GetValue(value) : Field(value, name);
            }
            catch (Exception error) { result[name] = "unavailable: " + error.GetBaseException().Message; }
        }
        return result;
    }
    private sealed record FrameEvidence(long Frame, int Bytes, string RgbaSha256, string? RawFile);
    private sealed class GlyphRenderBinding
    {
        internal readonly Attempt Attempt;
        internal readonly object Scope;
        internal readonly Camera Camera;
        internal readonly RenderTexture Target;
        internal readonly long Frame;
        internal GlyphFrameSnapshot? Before;
        internal string? RenderPathIdentity, RenderPathState;
        internal bool Started, Completed;
        internal GlyphRenderBinding(Attempt attempt, object scope, Camera camera, RenderTexture target, long frame)
        { Attempt = attempt; Scope = scope; Camera = camera; Target = target; Frame = frame; }
    }
    private sealed class Attempt
    {
        internal readonly int Index, Width, Height, Fps;
        internal readonly ExportHost Host;
        internal readonly string Directory;
        internal readonly IncrementalHash Rolling = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal readonly List<FrameEvidence> Frames = new();
        internal readonly List<object> States = new();
        internal readonly List<object> LabelGeometry = new();
        internal readonly List<string> Errors = new();
        internal readonly FileStream? FullFrames;
        internal readonly GlyphFrameSet Glyphs;
        internal ExportSession? Session;
        internal GlyphRenderBinding? PendingGlyphRender;
        internal readonly List<object> RenderPaths = new();
        internal readonly HashSet<string> RenderPathKeys = new(StringComparer.Ordinal);
        internal Attempt(int index, ExportHost host, int width, int height, int fps, string directory)
        {
            Index = index; Host = host; Width = width; Height = height; Fps = fps; Directory = directory;
            Glyphs = new GlyphFrameSet { Width = width, Height = height };
            if (FontAtlasEquivalenceEnabled || Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_PREENCODE_FULL") == "1")
                FullFrames = new FileStream(Path.Combine(directory, "all-frames.rgba"), FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        }
    }
}
