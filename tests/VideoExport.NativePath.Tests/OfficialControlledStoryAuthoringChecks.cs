using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AzureArchive.Automation;
using FlatData;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Authoring input fixture only. Native SaveProject and AuthoringCompiler produce
// the paired AAP2/AAS; installed Mods own directive sanitization and voice binding.
// No compiled ScriptKr/VoiceJp repair, resolver replacement or preset execution.
public sealed class OfficialControlledStoryAuthoringChecks : MonoBehaviour
{
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private bool done;
    public OfficialControlledStoryAuthoringChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (done) return;
        try
        {
            if (!Ready())
            {
                if (wall.Elapsed.TotalSeconds > 70) throw new TimeoutException("Official authoring fixture native tables did not become ready.");
                return;
            }
            done = true;
            Create();
            Plugin.Report("PATH-CHECK RESULT 1 passed; 0 failed");
        }
        catch (Exception error)
        {
            done = true;
            while (error is TargetInvocationException invocation && invocation.InnerException != null) error = invocation.InnerException;
            Plugin.Report("PATH-CHECK FAIL official-controlled-story-authoring: " + error);
            Plugin.Report("PATH-CHECK RESULT 0 passed; 1 failed");
        }
    }

    [HideFromIl2Cpp]
    private static bool Ready()
    {
        try
        {
            var resources = ScenarioResourceManager.Instance; var characters = CharacterManager.Instance;
            return SceneManager.GetActiveScene().name == "CatalogScene" && UserSettings.Instance?.isReady == true
                && resources != null && resources.AreDbsLoaded && resources.ExcelDb != null && resources.ScenarioCharacterNameQuery != null && !resources.Preloading
                && characters != null && !characters.Preloading && characters.characterHashDict?.ByteBuffer != null && characters.characterHashDict.DataListLength > 0;
        }
        catch { return false; }
    }

    [HideFromIl2Cpp]
    private static void Create()
    {
        string root = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_FIXTURE_WORKSPACE") ?? "";
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("An explicit independent official-authoring workspace is required.");
        root = Path.GetFullPath(root);
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "aave-fixture-workspace.json")));
        if (marker.RootElement.GetProperty("Owner").GetString() != "AAVE acceptance" || marker.RootElement.GetProperty("Schema").GetInt32() != 2)
            throw new InvalidOperationException("Only the new official-authoring fixture workspace is allowed.");
        string name = marker.RootElement.GetProperty("StoryName").GetString()!;
        string template = marker.RootElement.GetProperty("TemplateProject").GetString()!;
        string templateHash = marker.RootElement.GetProperty("TemplateSha256").GetString()!;
        string projectPath = Path.Combine(root, "data", "projects", name + ".aap2");
        string storyPath = Path.Combine(root, "data", "saves", name + ".aas");
        string projectRoot = Path.Combine(root, "data", "projects", name);
        if (!Same(projectPath, ScenarioResourceManager.GetPersistentFilePath("projects", name, ".aap2"))
            || !Same(storyPath, ScenarioResourceManager.GetPersistentFilePath("saves", name, ".aas")))
            throw new InvalidOperationException("Native workspace resolution must point to the isolated official-authoring inputs.");
        if (File.Exists(storyPath)) throw new IOException("Do not overwrite a published fixture; prepare a new workspace for another authoring attempt.");
        if (!string.Equals(HashFile(template), templateHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Original template digest differs from the protected input.");
        RequireInstalledCompilerParticipants();

        var resources = ScenarioResourceManager.Instance;
        resources.LoadGlobalResourceOverrides(); Directory.CreateDirectory(projectRoot); resources.LoadLocalResourceOverrides(projectRoot);
        uint first = FindBackground(resources, Path.Combine(root, "data", "overrides", "bgs", "controlled-bg.png"));
        uint second = FindBackground(resources, Path.Combine(root, "data", "overrides", "bgs", "controlled-bg-2.png"));
        const string voiceKey = "SE_BatSwing_01", voiceIdentifier = "aavt/voice/SE_BatSwing_01";
        if (!resources.TryGetVoiceOverridePath(voiceIdentifier, out string voicePath) || !File.Exists(voicePath))
            throw new InvalidOperationException("The original CharacterVoice/native resolver cannot resolve the isolated voice asset.");
        var project = resources.LoadProject(name) ?? throw new InvalidOperationException("Native LoadProject did not read the independent copied AAP2.");
        var scriptNodes = new List<ScriptNodeData>(); var entries = new List<EntryNodeData>();
        for (int i = 0; i < project.nodes.Count; i++)
        {
            var scriptNode = project.nodes[i].TryCast<ScriptNodeData>(); if (scriptNode != null) scriptNodes.Add(scriptNode);
            var entry = project.nodes[i].TryCast<EntryNodeData>(); if (entry != null) entries.Add(entry);
        }
        if (scriptNodes.Count != 1 || entries.Count != 1 || project.nodes.Count != 2)
            throw new InvalidOperationException("The copied native template must contain one entry connected to one dialogue node.");
        project.ProjectName = name; project.ProjectId = Guid.NewGuid().ToString(); project.FormatVersion = 2;
        project.PreviewBgName = first; project.PreviewHeader = "AAVE native acceptance"; project.PreviewTitle = "Official authoring / mixed effects";
        entries[0].Header = project.PreviewHeader; entries[0].Title = project.PreviewTitle;
        scriptNodes[0].NodeName = "Official mixed effects";
        var definitions = new[]
        {
            new Definition("AAVE 01 ready", "#wait;250", first, "00", false, false, ""),
            new Definition("AAVE 02 voice and sway", "#aavt;voice;" + voiceKey + "\n#aavt;fx;3;sway;amplitude=12;frequency=2;cycles=1", first, "00", true, false, "sway"),
            new Definition("AAVE 03 spin and video transition", "#aavt;fx;3;spin;frequency=2;cycles=1;axis=y", second, "01", false, false, "spin"),
            new Definition("AAVE 04 voice and headbutt", "#aavt;voice;" + voiceKey + "\n#aavt;fx;3;headbutt;duration=600", second, "02", true, false, "headbutt"),
            new Definition("AAVE 05 voice and squash", "#aavt;voice;" + voiceKey + "\n#aavt;fx;3;squash;amplitude=0.15;frequency=2;cycles=1", first, "00", true, false, "squash"),
            new Definition("AAVE 06 native emotion", "#wait;500", first, "00", false, true, ""),
            new Definition("AAVE 07 finished", "#wait;250", second, "00", false, false, "")
        };
        var scripts = new Il2CppSystem.Collections.Generic.List<ScriptData>();
        var authoringEvidence = new List<object>();
        foreach (var definition in definitions)
        {
            var script = new ScriptData
            {
                LineId = Guid.NewGuid().ToString(), text = definition.Text, additionalPrompt = definition.Prompt,
                voice = "", sound = "", popup = "", placeText = "", bgEffect = 0, transition = 0,
                bgName = definition.Background, bgFriendlyName = definition.Background == first ? "controlled-bg" : "controlled-bg-2",
                bgmId = 12, selectionGroup = 0, isDialogScript = true, speakerSlotNum = 3,
                characters = new Il2CppSystem.Collections.Generic.List<ScriptData.CharacterRecordData>(),
                highlightedSlotNums = new Il2CppSystem.Collections.Generic.List<int>()
            };
            for (int slot = 0; slot < 6; slot++)
                script.characters.Add(new ScriptData.CharacterRecordData
                {
                    name = slot == 3 ? "히나" : "", faceId = slot == 3 ? definition.Face : "00",
                    startingPos = slot == 3 ? 3 : 0, endingPos = slot == 3 ? 3 : 0, displayOrder = 1,
                    emoticon = (EmoticonType)(slot == 3 && definition.Emotion ? 0 : -1),
                    action = (CharacterAction)0, effect = (CharacterEffect)0,
                    appear = (Script.CharacterRecord.AppearType)0, shapeOverride = (Script.CharacterRecord.ShapeOverrideType)0
                });
            script.highlightedSlotNums.Add(3); scripts.Add(script);
            var parsed = ParseEffects(definition.Prompt);
            if (parsed.Errors.Length != 0 || parsed.Commands.Length != (definition.Preset.Length == 0 ? 0 : 1))
                throw new InvalidOperationException("Actual installed effect authoring parser rejected the fixture: " + string.Join(" | ", parsed.Errors));
            authoringEvidence.Add(new { script.LineId, definition.Text, definition.Prompt, ExpectedVoice = definition.Voice ? voiceIdentifier : "",
                ExpectedPreset = definition.Preset, CanonicalCommands = parsed.Commands, SpeakerSlot = script.speakerSlotNum });
        }
        scriptNodes[0].Scripts = scripts;
        if (!resources.SaveProject(project, check: false)) throw new InvalidOperationException("Native SaveProject rejected the isolated project DTO.");
        string authoredHash = HashFile(projectPath);
        var originalProject = PersistentData.projectData; string originalPath = PersistentData.projectPath;
        string compileReceipt;
        try
        {
            // This context points only to the real newly saved fixture project.
            PersistentData.projectData = project; PersistentData.projectPath = projectPath;
            var receipt = AuthoringCompiler.CompileToSaves(project, storyPath, "sha256:" + authoredHash.ToLowerInvariant(), null);
            compileReceipt = receipt?.ToString() ?? "null";
        }
        finally { PersistentData.projectData = originalProject; PersistentData.projectPath = originalPath; }
        File.WriteAllText(Path.Combine(root, "official-compile-receipt.json"), compileReceipt);
        if (!File.Exists(storyPath) || new FileInfo(storyPath).Length == 0)
            throw new InvalidOperationException("Official CompileToSaves did not publish the isolated AAS; inspect its native receipt.");
        var compiled = resources.LoadGenericScenario(name);
        if (compiled == null || compiled.ByteBuffer == null || compiled.DataListLength == 0) throw new InvalidOperationException("Native reload of the official compiler output failed.");
        int ownedVoices = 0; var compiledRows = new List<object>();
        for (int i = 0; i < compiled.DataListLength; i++)
        {
            var row = compiled.DataList(i).Cast<GenericScenarioExcel>();
            if (row.GetIl2CppType().FullName != "GenericScenarioExcel") throw new InvalidOperationException("Unexpected concrete compiled native row.");
            var extraction = ParseEffects(row.ScriptKr ?? "");
            if (extraction.HasDirectives) throw new InvalidOperationException("The actual compiler left raw embedded directives in AAS row " + i + "; no manual sanitization is allowed.");
            string voice = row.VoiceJp ?? "";
            if (voice.StartsWith("aavt/voice/", StringComparison.Ordinal))
            {
                if (voice != voiceIdentifier) throw new InvalidOperationException("Official compiler produced an unexpected owned voice identifier.");
                ownedVoices++;
            }
            compiledRows.Add(new { index = i, row.ScriptKr, row.VoiceJp, row.TextJp, row.TextEn, row.TextTw, row.BGName, row.BGMId });
        }
        if (ownedVoices != 3) throw new InvalidOperationException("Official compiler/CharacterVoice binding produced " + ownedVoices + " owned voices; expected exactly three.");
        if (!string.Equals(HashFile(template), templateHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Original source template changed during native compilation.");
        var recipe = new
        {
            Schema = 2, AuthoringPipeline = "native LoadProject -> native SaveProject -> original AuthoringCompiler.CompileToSaves",
            Project = projectPath, ProjectSha256 = HashFile(projectPath), Story = storyPath, StorySha256 = HashFile(storyPath),
            ProtectedTemplate = template, ProtectedTemplateSha256 = templateHash, OriginalTemplateUnchanged = true,
            VoiceIdentifier = voiceIdentifier, VoiceFile = voicePath, VoiceSha256 = HashFile(voicePath),
            AuthoringRows = authoringEvidence, CompiledRows = compiledRows, OwnedVoiceOccurrences = ownedVoices,
            ExpectedPresets = new[] { "sway", "spin", "headbutt", "squash" }, NativeBackgroundIds = new[] { first, second },
            RawDirectivesAbsentFromCompiledScript = true, CompilationReceipt = compileReceipt,
            Scope = "Valid native authored input only. Normal playback must independently prove four real preset Begin events and three native voice requests; no execution was synthesized here."
        };
        File.WriteAllText(Path.Combine(root, "controlled-story-recipe.json"), JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Report("PATH-CHECK PASS official-controlled-story authoring=" + projectPath + "; output=" + storyPath
            + "; compiled_rows=" + compiled.DataListLength + "; owned_voices=" + ownedVoices + "; raw_aavt_in_aas=false");
    }

    [HideFromIl2Cpp]
    private static void RequireInstalledCompilerParticipants()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var effects = assemblies.Single(assembly => assembly.GetName().Name == "Rukari.MoreEffects");
        var voice = assemblies.Single(assembly => assembly.GetName().Name == "Rukari.CharacterVoice");
        var effectCompiler = effects.GetType("AzureArchive.VideoTools.Runtime.EditorEmbeddedDirectivePatch", true)!;
        if (effectCompiler.GetField("_registration", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) == null)
            throw new InvalidOperationException("The actual shared MoreEffects authoring compiler is not registered.");
        var voiceCompiler = voice.GetType("Rukari.CharacterVoice.Runtime.VoiceSupportPatch", true)!;
        if (voiceCompiler.GetField("_installed", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is not true)
            throw new InvalidOperationException("The actual CharacterVoice authoring/resource bridge is not installed.");
    }

    [HideFromIl2Cpp]
    private static (bool HasDirectives, string[] Commands, string[] Errors) ParseEffects(string prompt)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "AzureArchive.VideoTools.Core");
        var type = assembly.GetType("AzureArchive.VideoTools.Core.Commands.EmbeddedAavtDirectiveExtractor", true)!;
        object parser = Activator.CreateInstance(type)!;
        object result = type.GetMethod("Extract")!.Invoke(parser, new object[] { prompt, false })!;
        var resultType = result.GetType();
        var commands = ((IEnumerable)resultType.GetProperty("Commands")!.GetValue(result)!).Cast<object>()
            .Select(command => (string)command.GetType().GetProperty("CanonicalDirective")!.GetValue(command)!).ToArray();
        var errors = ((IEnumerable)resultType.GetProperty("Errors")!.GetValue(result)!).Cast<string>().ToArray();
        return ((bool)resultType.GetProperty("HasEmbeddedDirectives")!.GetValue(result)!, commands, errors);
    }

    [HideFromIl2Cpp]
    private static uint FindBackground(ScenarioResourceManager resources, string path)
    {
        var iterator = resources.EnumerateBackgrounds().GetEnumerator(); var traversal = iterator.Cast<Il2CppSystem.Collections.IEnumerator>();
        while (traversal.MoveNext()) { var item = iterator.Current; if (item != null && Same(item.FullPath, path)) return item.Id; }
        throw new InvalidOperationException("Native background manifest omitted fixture " + path);
    }
    [HideFromIl2Cpp]
    private static string HashFile(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)); }
    [HideFromIl2Cpp]
    private static bool Same(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private sealed record Definition(string Text, string Prompt, uint Background, string Face, bool Voice, bool Emotion, string Preset);
}
