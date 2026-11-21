using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FlatData;
using Google.FlatBuffers;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Writes new, explicitly marked acceptance inputs through AA's actual native
// FlatBuffer builder. It never edits an existing story or substitutes a resolver.
public sealed class ControlledStoryAuthoringChecks : MonoBehaviour
{
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private bool done;
    public ControlledStoryAuthoringChecks(IntPtr pointer) : base(pointer) { }
    public void Update()
    {
        if (done) return;
        if (SceneManager.GetActiveScene().name != "CatalogScene" || UserSettings.Instance?.data == null
            || ScenarioResourceManager.Instance?.AreDbsLoaded != true)
        {
            if (wall.Elapsed.TotalSeconds > 70) { done = true; Plugin.Report("PATH-CHECK FAIL controlled-story native resources timed out"); Plugin.Report("PATH-CHECK RESULT 0 passed; 1 failed"); }
            return;
        }
        done = true;
        try { Create(); Plugin.Report("PATH-CHECK RESULT 1 passed; 0 failed"); }
        catch (Exception error) { Plugin.Report("PATH-CHECK FAIL controlled-story-authoring: " + error); Plugin.Report("PATH-CHECK RESULT 0 passed; 1 failed"); }
    }

    [HideFromIl2Cpp]
    private static void Create()
    {
        string root = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_FIXTURE_WORKSPACE") ?? "";
        if (!Path.IsPathFullyQualified(root) || !File.Exists(Path.Combine(root, "aave-fixture-workspace.json")))
            throw new InvalidOperationException("An explicit independent fixture workspace is required.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "aave-fixture-workspace.json")));
        if (marker.RootElement.GetProperty("Owner").GetString() != "AAVE acceptance") throw new InvalidOperationException("Wrong workspace owner.");
        string name = marker.RootElement.GetProperty("StoryName").GetString()!;
        string output = Path.GetFullPath(Path.Combine(root, "data", "saves", name + ".aas"));
        if (!Same(output, ScenarioResourceManager.GetPersistentFilePath("saves", name, ".aas")))
            throw new InvalidOperationException("Native workspace resolution must point to this independent fixture.");
        if (File.Exists(output)) throw new IOException("Never overwrite an existing acceptance story.");
        var resources = ScenarioResourceManager.Instance;
        resources.LoadGlobalResourceOverrides();
        string localRoot = Path.Combine(root, "data", "saves", name);
        Directory.CreateDirectory(localRoot);
        resources.LoadLocalResourceOverrides(localRoot);
        uint first = FindBackground(resources, Path.Combine(root, "data", "overrides", "bgs", "controlled-bg.png"));
        uint second = FindBackground(resources, Path.Combine(root, "data", "overrides", "bgs", "controlled-bg-2.png"));
        const string voice = "aavt/voice/SE_BatSwing_01";
        if (!resources.TryGetVoiceOverridePath(voice, out string voicePath) || !File.Exists(voicePath))
            throw new InvalidOperationException("The real CharacterVoice/native resolver must resolve the fixture input.");
        var rows = new[]
        {
            new Row("3;히나;00\n#wait;250", "", "", first, "native portrait and dynamic background"),
            new Row("3;히나;00;Voice and sway\n#aavt;fx;3;sway;duration=600", "Voice and sway", voice, first, "voice cold start and MoreEffects sway"),
            new Row("3;히나;01;Spin and background transition\n#aavt;fx;3;spin;duration=600", "Spin and background transition", "", second, "MoreEffects spin and source transition"),
            new Row("3;히나;02;Voice and headbutt\n#aavt;fx;3;headbutt;duration=600", "Voice and headbutt", voice, second, "repeated cached voice and headbutt"),
            new Row("3;히나;00;Voice and squash\n#aavt;fx;3;squash;duration=600", "Voice and squash", voice, first, "repeated cached voice and squash"),
            new Row("#3;em;[반응]\n#wait;500", "", "", first, "native portrait emotion and wait"),
            new Row("#3;h\n#wait;250", "", "", second, "native hide and end")
        };
        var builder = new FlatBufferBuilder(8192);
        var offsets = new Il2CppReferenceArray<Offset<GenericScenarioExcel>>(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            Row row = rows[i];
            var empty = builder.CreateString("");
            var script = builder.CreateString(row.Script);
            var text = builder.CreateString(row.Text);
            var spoken = builder.CreateString(row.Voice);
            offsets[i] = GenericScenarioExcel.CreateGenericScenarioExcel(builder, 1, 0, 12,
                empty, 0, row.Background, 0, empty, script, text, text, text, text, text, spoken, false);
        }
        var vector = GenericScenarioExcelTable.CreateDataListVector(builder, offsets);
        var tableOffset = GenericScenarioExcelTable.CreateGenericScenarioExcelTable(builder, vector);
        GenericScenarioExcelTable.FinishGenericScenarioExcelTableBuffer(builder, tableOffset);
        byte[] bytes = builder.SizedByteArray().ToArray();
        var reread = GenericScenarioExcelTable.GetRootAsGenericScenarioExcelTable(new ByteBuffer(new Il2CppStructArray<byte>(bytes)));
        if (reread.DataListLength != rows.Length) throw new InvalidOperationException("Native row count did not round-trip.");
        for (int i = 0; i < rows.Length; i++)
        {
            var boxed = reread.DataList(i);
            if (i == 0) Plugin.Report("PATH-CHECK controlled-story row runtime type=" + boxed.Cast<Il2CppSystem.Object>().GetIl2CppType().FullName);
            var actual = boxed.Cast<GenericScenarioExcel>(); Row expected = rows[i];
            if (actual.ScriptKr != expected.Script || actual.VoiceJp != expected.Voice
                || actual.BGName != expected.Background || actual.BGMId != 12 || actual.TextEn != expected.Text)
                throw new InvalidOperationException("Native authored fields failed round-trip at row " + i + ": " + JsonSerializer.Serialize(new
                { expected, actual = new { actual.ScriptKr, actual.VoiceJp, actual.BGName, actual.BGMId, actual.TextEn } }));
        }
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) stream.Write(bytes);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllText(Path.Combine(root, "controlled-story-recipe.json"), JsonSerializer.Serialize(new
        {
            Schema = 1, Story = output, StorySha256 = sha, VoiceIdentifier = voice, VoiceFile = voicePath,
            VoiceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(voicePath))),
            Rows = rows, NativeRoundTrip = true, NativeBackgroundIds = new[] { first, second },
            Scope = "Authored acceptance input only. Runtime execution of every listed effect still requires separate native evidence."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Report("PATH-CHECK PASS controlled-story native authoring and field round-trip; rows=" + rows.Length + "; sha256=" + sha + "; path=" + output);
    }

    [HideFromIl2Cpp]
    private static uint FindBackground(ScenarioResourceManager resources, string path)
    {
        var iterator = resources.EnumerateBackgrounds().GetEnumerator();
        var traversal = iterator.Cast<Il2CppSystem.Collections.IEnumerator>();
        while (traversal.MoveNext())
        {
            var resource = iterator.Current;
            if (resource != null && Same(resource.FullPath, path)) return resource.Id;
        }
        throw new InvalidOperationException("Native background resolver did not enumerate fixture " + path);
    }
    [HideFromIl2Cpp]
    private static bool Same(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private sealed record Row(string Script, string Text, string Voice, uint Background, string ExpectedEffect);
}
