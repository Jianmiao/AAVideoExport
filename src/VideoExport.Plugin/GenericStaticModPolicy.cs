using System.Reflection;
using System.Security.Cryptography;
using AAVideoExport.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace AAVideoExport.Plugin;

// Generic static classification for assemblies that have no managed time,
// Harmony patch, MonoBehaviour callback, render/audio/video, or P/Invoke
// surface. It never uses an assembly name. It is intentionally fail-closed:
// any observable behavior family rejects the passive classification.
internal static class GenericStaticModPolicy
{
    internal const string EvidenceMarker = StrictCapability.GenericStaticInertEvidence;

    private static readonly string[] RenderAudioVideoPrefixes =
    {
        "UnityEngine.Camera", "UnityEngine.RenderTexture", "UnityEngine.Graphics", "UnityEngine.Screen",
        "UnityEngine.Canvas", "UnityEngine.GUI", "UnityEngine.AudioSource", "UnityEngine.AudioListener",
        "UnityEngine.VideoPlayer", "UnityEngine.Video.", "UnityEngine.Light", "UnityEngine.SpriteRenderer",
        "UnityEngine.Renderer", "UnityEngine.Shader", "UnityEngine.Material"
    };
    private static readonly string[] BehaviourTypePrefixes =
    {
        "UnityEngine.GameObject", "UnityEngine.Component", "UnityEngine.Transform", "UnityEngine.Object",
        "UnityEngine.SceneManagement", "AzureArchive", "HarmonyLib"
    };
    private static readonly string[] BehaviourCallbacks =
    {
        "Update", "LateUpdate", "FixedUpdate", "OnGUI", "OnRenderImage", "OnPreRender", "OnPostRender"
    };

    internal static bool TryClassify(string assemblyPath, out IReadOnlyList<string> evidence)
    {
        evidence = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return Reject(out evidence, "assembly-path-unavailable");
        string sha256;
        try { sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Reject(out evidence, "assembly-unreadable:" + error.GetType().Name);
        }
        var clock = ModClockAudit.Scan(assemblyPath);
        var report = clock.Assemblies.SingleOrDefault();
        if (report == null || !report.Succeeded)
            return Reject(out evidence, "clock-audit-unavailable");
        if (report.CallSites.Count != 0)
            return Reject(out evidence, "managed-clock-call-sites=" + report.CallSites.Count);

        try
        {
            using var module = ModuleDefinition.ReadModule(assemblyPath, new ReaderParameters { ReadingMode = ReadingMode.Immediate });
            int harmony = 0, behaviour = 0, renderAudioVideo = 0, pinvoke = 0, gameOrHarmony = 0;
            foreach (var type in module.Types.SelectMany(Flatten))
            {
                if (IsBehaviourType(type)) behaviour++;
                if (IsBehaviourTypeReference(type.BaseType)) gameOrHarmony++;
                foreach (var field in type.Fields)
                {
                    if (IsRenderAudioVideo(field.FieldType)) renderAudioVideo++;
                    if (IsBehaviourTypeReference(field.FieldType)) gameOrHarmony++;
                }
                foreach (var method in type.Methods)
                {
                    if (method.IsPInvokeImpl || method.PInvokeInfo != null) pinvoke++;
                    if (BehaviourCallbacks.Contains(method.Name, StringComparer.Ordinal)) behaviour++;
                    if (method.CustomAttributes.Any(IsHarmonyAttribute) || type.CustomAttributes.Any(IsHarmonyAttribute))
                        harmony++;
                    if (IsBehaviourTypeReference(method.ReturnType)) gameOrHarmony++;
                    if (!method.HasBody) continue;
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is TypeReference typeReference)
                        {
                            if (IsRenderAudioVideo(typeReference)) renderAudioVideo++;
                            if (IsBehaviourTypeReference(typeReference)) gameOrHarmony++;
                        }
                        if (instruction.Operand is MethodReference methodReference)
                        {
                            if (IsRenderAudioVideo(methodReference.DeclaringType)) renderAudioVideo++;
                            if (IsRenderAudioVideo(methodReference.ReturnType)) renderAudioVideo++;
                            if (IsBehaviourTypeReference(methodReference.DeclaringType)) gameOrHarmony++;
                        }
                    }
                }
            }
            if (harmony != 0) return Reject(out evidence, "harmony-patch-attributes=" + harmony);
            if (behaviour != 0) return Reject(out evidence, "behaviour-callbacks=" + behaviour);
            if (renderAudioVideo != 0) return Reject(out evidence, "render-audio-video-references=" + renderAudioVideo);
            if (pinvoke != 0) return Reject(out evidence, "pinvoke-methods=" + pinvoke);
            if (gameOrHarmony != 0) return Reject(out evidence, "game-or-harmony-references=" + gameOrHarmony);
            evidence = new[]
            {
                EvidenceMarker, "managed-clock-call-sites=0", "harmony-patch-attributes=0",
                "behaviour-callbacks=0", "render-audio-video-references=0", "pinvoke-methods=0",
                "game-or-harmony-references=0", "sha256=" + sha256
            };
            return true;
        }
        catch (Exception error)
        {
            return Reject(out evidence, "metadata-scan-failed:" + error.GetType().Name);
        }
    }

    internal static bool TryClassify(Assembly assembly, out IReadOnlyList<string> evidence)
    {
        try
        {
            string location = assembly.Location;
            if (string.IsNullOrWhiteSpace(location)) return Reject(out evidence, "assembly-location-unavailable");
            return TryClassify(location, out evidence);
        }
        catch (NotSupportedException)
        {
            return Reject(out evidence, "assembly-location-unavailable");
        }
    }

    private static bool Reject(out IReadOnlyList<string> evidence, string reason)
    {
        evidence = new[] { "generic-static-policy-rejected", reason };
        return false;
    }

    private static bool IsBehaviourType(TypeDefinition type)
    {
        if (type.BaseType is { } baseType && baseType.FullName.Contains("MonoBehaviour", StringComparison.Ordinal))
            return true;
        return type.Interfaces.Any(value => value.InterfaceType.FullName.Contains("MonoBehaviour", StringComparison.Ordinal));
    }

    private static bool IsRenderAudioVideo(TypeReference type)
    {
        string name = type.FullName;
        return RenderAudioVideoPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool IsBehaviourTypeReference(TypeReference? type)
    {
        if (type == null) return false;
        string name = type.FullName;
        return BehaviourTypePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool IsHarmonyAttribute(CustomAttribute attribute)
    {
        string name = attribute.AttributeType.FullName;
        return name.StartsWith("HarmonyLib.Harmony", StringComparison.Ordinal);
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Flatten)) yield return nested;
    }
}
