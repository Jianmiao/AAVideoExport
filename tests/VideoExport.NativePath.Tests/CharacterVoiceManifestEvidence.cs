using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FlatData;
using Il2CppInterop.Runtime;

namespace AAVideoExport.NativePath.Tests;

// Root invokes this after the actual native frozen-save load. Expected values
// come from the isolated fixture recipe, never from the production extractor.
internal static class CharacterVoiceManifestEvidence
{
    internal static void Verify(GenericScenarioExcelTable frozenSave, string storyPath, string expectedSourceSha256,
        IReadOnlyList<string> expectedOwnedOccurrences, Action<string> report)
    {
        using var stream = File.OpenRead(storyPath);
        using var sha = SHA256.Create();
        string actualHash = Convert.ToHexString(sha.ComputeHash(stream));
        if (!string.Equals(actualHash, expectedSourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native manifest fixture source does not match the independent recipe hash.");
        var occurrences = new List<string>();
        var rows = new List<object>();
        for (int index = 0; index < frozenSave.DataListLength; index++)
        {
            var boxed = frozenSave.DataList(index) ?? throw new InvalidOperationException("Native fixture row is missing.");
            var concrete = boxed.Cast<GenericScenarioExcel>();
            string type = concrete.GetIl2CppType().FullName;
            if (type != "GenericScenarioExcel") throw new InvalidOperationException("Native fixture returned an unsupported concrete row: " + type);
            string voice = concrete.VoiceJp ?? "";
            rows.Add(new { index, type, voice });
            if (voice.StartsWith("aavt/voice/", StringComparison.Ordinal)) occurrences.Add(voice);
        }
        if (!occurrences.SequenceEqual(expectedOwnedOccurrences, StringComparer.Ordinal))
            throw new InvalidOperationException("Actual concrete native VoiceJp occurrences differ from the independent fixture recipe.");
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "AAVideoExport");
        var adapter = assembly.GetType("AAVideoExport.Plugin.CharacterVoiceSessionAdapter", true)!;
        var collect = adapter.GetMethod("CollectKnownIdentifiers", BindingFlags.Static | BindingFlags.NonPublic)!;
        var actual = (string[])collect.Invoke(null, new object[] { frozenSave })!;
        var expected = expectedOwnedOccurrences.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("Production known-voice extraction differs from the actual native fixture recipe.");
        report("PATH-CHECK PASS character-voice-native-manifest " + JsonSerializer.Serialize(new
        {
            SourceSha256 = actualHash, NativeRows = rows, OwnedOccurrences = occurrences.Count,
            DistinctKnownIdentifiers = actual, ConcreteGetterOnly = true,
            Scope = "Actual frozen native rows and production extractor against an independently authored AAS recipe."
        }));
    }
}
