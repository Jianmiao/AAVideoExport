using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Plugin;
using HarmonyLib;
using UnityEngine;

namespace AAVideoExport.NativePath.Tests;

// Read-only failure evidence. This never grants admission or changes an AA gate.
internal static class StoryRejectionDiagnostics
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Dictionary<string,string>? initialParts;
    private static string initialComposite="";
    private static IntPtr initialHost;
    private static int initialAttempt;

    internal static void Install(Harmony hooks)
    {
        hooks.Patch(typeof(ExportHost).GetMethod("InitializeStrictPreparation",Flags)!,
            postfix:new HarmonyMethod(typeof(StoryRejectionDiagnostics),nameof(CaptureInitialSettings)));
    }
    private static void CaptureInitialSettings(ExportHost __instance)
    {
        try
        {
            initialHost=__instance.Pointer;initialAttempt++;
            initialComposite=(string)typeof(ExportHost).GetField("_strictSettings",Flags)!.GetValue(__instance)!;
            initialParts=PlaybackParts();
            File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot,$"story-settings-initial-{initialAttempt:00}.json"),JsonSerializer.Serialize(new
            { pid=Environment.ProcessId,attempt=initialAttempt,initialComposite,parts=initialParts,scope="Read-only after production InitializeStrictPreparation; no settings or source mutation." },new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){Plugin.Report("PATH-CHECK STORY initial-settings-diagnostic-unavailable="+error.GetBaseException().Message);}
    }
    internal static void WriteTimelineFailure(ExportHost host)
    {
        try
        {
            object? Field(string name)=>typeof(ExportHost).GetField(name,Flags)!.GetValue(host);
            string expected=(string)Field("_strictSettings")!;
            string current=Read(()=> (string)typeof(ExportHost).GetMethod("PlaybackSettingsIdentity",Flags)!.Invoke(null,null)!);
            var parts=PlaybackParts();var sourceCheck=Field("_strictReplaySourceCheck") as Task<string>;
            string path=(string)Field("_selectedStoryKey")!;
            File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot,"story-timeline-source-failure.json"),JsonSerializer.Serialize(new
            {
                pid=Environment.ProcessId,attempt=initialAttempt,capturedFrames=Field("_captured"),strictPreparing=Field("_strictPreparing"),
                replayPending=Field("_strictReplayPending"),expectedSettings=expected,currentSettings=current,settingsEqual=expected==current,
                initialSnapshotMatchesHost=host.Pointer==initialHost,initialComposite,initialParts,currentParts=parts,
                changedParts=initialParts==null?Array.Empty<string>():parts.Keys.Where(key=>!initialParts.TryGetValue(key,out var before)||before!=parts[key]).ToArray(),
                expectedStoryHash=Field("_strictStoryHash"),sourceCheckStatus=sourceCheck?.Status.ToString(),
                completedSourceCheck=sourceCheck?.IsCompletedSuccessfully==true?sourceCheck.Result:null,
                selectedStory=path,currentStoryHash=Read(()=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))),
                expectedAudio=Field("_strictAudioSettings"),currentAudio=AudioSettings.outputSampleRate+"|"+AudioSettings.speakerMode,
                scope="Failure prefix before production Cancel clears pass state. All values are observed; no gate or input is changed."
            },new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){Plugin.Report("PATH-CHECK STORY timeline-source-diagnostic-unavailable="+error.GetBaseException().Message);}
    }
    private static Dictionary<string,string> PlaybackParts()
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        result["nativeSettings"]=Read(()=>
        {
            var data=UserSettings.Instance?.data??throw new InvalidOperationException("Native settings unavailable");
            return FormattableString.Invariant($"{data.autoDelay}|{data.masterVolume:R}|{data.voiceVolume:R}|{data.bgmVolume:R}|{data.soundEffectVolume:R}|{data.stEnabled}|{data.fontIndex}");
        });
        foreach(var name in new[]{"ControlledBackgroundVideoAdapter","RuntimeModBoundaries","MultilingualCaptureAdapter","RetainedMenuPlaybackBoundary"})
            result[name]=Read(()=> (string)typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin."+name,true)!.GetMethod("PlaybackSettingsIdentity",Flags)!.Invoke(null,null)!);
        return result;
    }

    internal static void Write(ExportHost host)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var boundary = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.RuntimeModBoundaries", true)!;
            var validated = (IEnumerable<Assembly>)boundary.GetField("Validated", flags)!.GetValue(null)!;
            var accepted = validated.ToHashSet();
            var rows = assemblies.Select(a => new
            {
                name = a.FullName,
                dynamic = a.IsDynamic,
                path = Read(() => a.Location),
                mvid = Read(() => a.ManifestModule.ModuleVersionId.ToString()),
                sha256 = Read(() => a.IsDynamic || string.IsNullOrEmpty(a.Location) ? "" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location)))),
                boundaryValidated = accepted.Contains(a),
                types = a.IsDynamic || Read(() => a.Location).Length == 0 ? Types(a) : null
            }).ToArray();
            File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "story-admission-failure.json"),
                JsonSerializer.Serialize(new
                {
                    pid = Environment.ProcessId,
                    frameworkHeld = typeof(ExportHost).GetField("_frameworkScope", flags)!.GetValue(host) != null,
                    assemblies = rows,
                    generatedCaches = new[]
                    {
                        Cache(assemblies, "0Harmony", "HarmonyLib.DelegateTypeFactory", "TypeCache"),
                        Cache(assemblies, "Il2CppInterop.Runtime", "Il2CppInterop.Runtime.DelegateSupport", "ourDelegateTypes"),
                        Cache(assemblies, "MonoMod.Utils", "MonoMod.Utils.Extensions", "_GetManagedSizeHelper")
                    }
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { Plugin.Report("PATH-CHECK STORY admission-diagnostic-unavailable=" + error.GetBaseException().Message); }
    }

    private static object Cache(Assembly[] assemblies, string assemblyName, string typeName, string fieldName)
    {
        try
        {
            var type = assemblies.Single(a => a.GetName().Name == assemblyName).GetType(typeName, true)!;
            var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            return new { typeName, fieldName, value = Describe(field.GetValue(null), 0) };
        }
        catch (Exception error) { return new { typeName, fieldName, error = error.GetBaseException().Message }; }
    }

    private static object? Describe(object? value, int depth)
    {
        if (value == null) return null;
        if (value is Type type) return new { type = type.AssemblyQualifiedName, assemblyMvid = Read(() => type.Assembly.ManifestModule.ModuleVersionId.ToString()) };
        if (value is Assembly assembly) return new { assembly = assembly.FullName, mvid = Read(() => assembly.ManifestModule.ModuleVersionId.ToString()) };
        if (value is MemberInfo member) return new { member = member.ToString(), declaringType = member.DeclaringType?.AssemblyQualifiedName };
        if (value is System.Collections.IDictionary dictionary)
        {
            var entries = new List<object>();
            var iterator = dictionary.GetEnumerator();
            while (iterator.MoveNext()) entries.Add(new { key = Describe(iterator.Key, depth + 1), value = Describe(iterator.Value, depth + 1) });
            return entries.ToArray();
        }
        if (value is System.Collections.IEnumerable sequence && value is not string && depth < 4)
            return sequence.Cast<object>().Select(e => Describe(e, depth + 1)).ToArray();
        if (value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return value.ToString();
        if (depth >= 4) return value.GetType().FullName;
        return value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .ToDictionary(f => f.Name, f => Describe(f.GetValue(value), depth + 1));
    }

    private static object Types(Assembly assembly)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            return assembly.GetTypes().Select(t => new
            {
                name = t.FullName,
                attributes = t.Attributes.ToString(),
                parent = t.BaseType?.AssemblyQualifiedName,
                fields = t.GetFields(flags).Select(f => new { f.Name, type = f.FieldType.AssemblyQualifiedName, attributes = f.Attributes.ToString() }).ToArray(),
                methods = t.GetMethods(flags).Cast<MethodBase>().Concat(t.GetConstructors(flags)).Select(m => new
                {
                    name = m.ToString(), attributes = m.Attributes.ToString(), implementation = m.GetMethodImplementationFlags().ToString(),
                    il = Read(() => Convert.ToHexString(m.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>()))
                }).ToArray()
            }).ToArray();
        }
        catch (Exception error) { return new { error = error.GetBaseException().Message }; }
    }

    private static string Read(Func<string> read)
    {
        try { return read(); }
        catch (Exception error) { return "unavailable:" + error.GetType().Name; }
    }
}
