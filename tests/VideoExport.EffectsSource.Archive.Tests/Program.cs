using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;

string source = Path.GetFullPath("artifacts/controlled-authoring-workspace-20261030/data");
string evidence = Path.GetFullPath("artifacts/effects-settings-source-20261030/archive-proof");
Directory.CreateDirectory(evidence);
string work = Path.Combine(evidence, "fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
string selectedAas = Path.GetFullPath(Path.Combine(source, "saves/AAVE-OfficialAuthoring-20261030.aas"));
string expectedAap = Path.GetFullPath(Path.Combine(source, "projects/AAVE-OfficialAuthoring-20261030.aap"));
string expectedAapHash = "459A7965048FD41DCA4D3AF8AA647D38709F97BA1344502F0326C4FD4FEF2D74";
string expectedAasHash = "78D2B7D2C2DDDD87145E5C301703F7B04AE45A55AFB19236150084C1FED3EF22";
var aapReader = new AapProjectReader(); var aasReader = new AasScenarioReader();
var resolver = new ObservedSceneIdentityResolver();
var arbiter = new ActiveProjectPairArbiter(aapReader.Read, aasReader.Read, resolver);
var scan = new ProjectPairScanner().Scan(source);
if (!scan.Success || scan.Value == null) throw new Exception("Actual original scanner failed: " + scan.Error);
var pairs = scan.Value;
var aapResult = aapReader.Read(expectedAap); var aasResult = aasReader.Read(selectedAas);
if (!aapResult.Success || aapResult.Value == null) throw new Exception("Original AAP reader failed: " + aapResult.Error);
if (!aasResult.Success || aasResult.Value == null) throw new Exception("Original AAS reader failed: " + aasResult.Error);
var project = aapResult.Value; var playback = aasResult.Value;
byte[] projectionBytes = File.ReadAllBytes(expectedAap);
using var projectionDocument = JsonDocument.Parse(projectionBytes);
var compatibility = OfficialAapCompatibilityProjection.ValidateRead(expectedAap, projectionDocument.RootElement, projectionBytes);
if (!compatibility.Success || compatibility.Value == null) throw new Exception("Original native-source projection validation failed: " + compatibility.Error);
var nativeSource = compatibility.Value;
var resolutions = new List<object>();
int passed = 0, failed = 0; var results = new List<object>();
void Test(string name, Action run)
{
    try { run(); passed++; results.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; results.Add(new { Name = name, Passed = false, Error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static string Hash(string path) { using var stream = File.OpenRead(path); using var hash = SHA256.Create(); return Convert.ToHexString(hash.ComputeHash(stream)); }
static ObservedCompiledSceneIdentity Identity(PlaybackRecordSnapshot record)
{
    var identity = CommandIdentity.CompiledScript(record.CompiledScript);
    return new(identity.Sha256, identity.Utf16Length, identity.LineCount) { PlaybackRowIndex = record.RecordIndex };
}
static bool Trusted(ObservedSceneIdentityResolution? value) => value != null && value.Status == ObservedSceneIdentityStatus.Mapped
    && value.SelectedSceneTrusted && value.ArchivePairFresh && value.PairingTrusted;
Test("original scanner selects the actual compiled AAP and exact selected AAS", () =>
{
    Check(pairs.Count == 1 && Path.GetFullPath(pairs[0].AapPath) == expectedAap && Path.GetFullPath(pairs[0].AasPath) == selectedAas,
        "Original scanner did not reproduce the actual native-discovered pair.");
    Check(Hash(expectedAap) == expectedAapHash && Hash(selectedAas) == expectedAasHash, "Actual authored fixture source bytes changed.");
});
Test("original source snapshots contain exact full-file SHA256 rather than mtimes", () =>
{
    Check(project.Source.FullPath == expectedAap && playback.Source.FullPath == selectedAas, "Original snapshot paths differ.");
    Check(project.Source.RevisionSha256.Equals(expectedAapHash, StringComparison.OrdinalIgnoreCase)
        && playback.Source.RevisionSha256.Equals(expectedAasHash, StringComparison.OrdinalIgnoreCase), "Original reader hash is not the full-file SHA256.");
    Check(project.Source.Length == new FileInfo(expectedAap).Length && playback.Source.Length == new FileInfo(selectedAas).Length, "Original snapshot sizes differ.");
});
Test("original projection API exposes and validates the actual AAP2 dependency", () =>
{
    Check(nativeSource.ProjectPath == expectedAap && nativeSource.ProjectionRevisionSha256 == expectedAapHash, "Original projection binding changed.");
    Check(nativeSource.NativeProjectPath.Length != 0 && nativeSource.NativeRevisionSha256 == Hash(nativeSource.NativeProjectPath), "Original AAP2 input is missing or its whole-file hash changed.");
    Check(OfficialAapCompatibilityProjection.VerifyStillCurrent(nativeSource).Success, "Original native-source revalidation failed.");
    var method = typeof(OfficialAapCompatibilityProjection).GetMethod("ValidateRead")!;
    var invoke = (ProjectionValidator)method.CreateDelegate(typeof(ProjectionValidator));
    object result = invoke(expectedAap, projectionDocument.RootElement, projectionBytes, false, 67108864L, 128);
    Check((bool)result.GetType().GetProperty("Success")!.GetValue(result)!, "Exact-span-parameter/reference-covariant-return delegate failed.");
});
Test("title row is not promoted from playback-only to trusted mapping", () =>
{
    var result = resolver.Resolve(Identity(playback.Records[0]), project, playback);
    resolutions.Add(new { Row = 0, result.Success, result.Error, result.Value });
    Check(result.Success && result.Value?.Status == ObservedSceneIdentityStatus.PlaybackRecordOnly && !Trusted(result.Value), "Title unexpectedly mapped as a trusted authored scene.");
});
Test("all seven actual authored rows map to trusted original project scenes", () =>
{
    Check(playback.Records.Count == 8, "Native fixture record count changed.");
    foreach (var record in playback.Records.Skip(1))
    {
        var result = resolver.Resolve(Identity(record), project, playback);
        resolutions.Add(new { Row = record.RecordIndex, result.Success, result.Error, result.Value });
        Check(result.Success && Trusted(result.Value) && result.Value!.PlaybackRecordIndex == record.RecordIndex,
            "Original trusted mapping failed at row " + record.RecordIndex + ": " + result.Error + "; " + JsonSerializer.Serialize(result.Value));
    }
});
Test("original arbitration finds the selected pair uniquely from actual row1 identity", () =>
{
    var result = arbiter.Arbitrate(Identity(playback.Records[1]), pairs);
    Check(result.Status == ActivePairArbitrationStatus.Unique && result.Success && Trusted(result.Resolution)
        && result.Pair?.AasPath == selectedAas && result.Pair?.AapPath == expectedAap, "Original arbitration did not confirm the exact archive pair.");
});
Test("changed script identity cannot select the pair", () =>
{
    var id = Identity(playback.Records[1]) with { CompiledScriptSha256 = new string('0', 64) };
    var result = arbiter.Arbitrate(id, pairs);
    Check(!result.Success && result.Status != ActivePairArbitrationStatus.Unique, "Unknown compiled script selected an original pair.");
});
Test("original mapping rejects a wrong renamed selected archive", () =>
{
    string wrong = Path.Combine(work, "WrongSelectedArchive.aas"); File.Copy(selectedAas, wrong);
    var loaded = aasReader.Read(wrong);
    Check(loaded.Success && loaded.Value != null, "Wrong-name fixture could not be read.");
    var result = resolver.Resolve(Identity(loaded.Value!.Records[1]), project, loaded.Value);
    Check(result.Success && !Trusted(result.Value) && result.Value!.MappingIssues.Any(issue => issue.Code == "FileNameMismatch"), "Filename mismatch retained full trusted flags.");
});
Test("trusted same-content pair elsewhere still requires exact selected-AAS path guard", () =>
{
    string clone = Path.Combine(work, "same-content"); Directory.CreateDirectory(clone);
    string aap = Path.Combine(clone, Path.GetFileName(expectedAap)); string aas = Path.Combine(clone, Path.GetFileName(selectedAas));
    File.Copy(expectedAap, aap); File.Copy(selectedAas, aas); File.Copy(nativeSource.NativeProjectPath, Path.Combine(clone, Path.GetFileName(nativeSource.NativeProjectPath)));
    var result = arbiter.Arbitrate(Identity(playback.Records[1]), new[] { new DiscoveredProjectPair("AAVE-OfficialAuthoring-20261030", aap, aas, true) });
    Check(result.Success && Trusted(result.Resolution) && !string.Equals(Path.GetFullPath(result.Pair!.AasPath), selectedAas, StringComparison.OrdinalIgnoreCase),
        "Wrong-location fixture did not establish why selected path equality is required in addition to trusted flags: " + JsonSerializer.Serialize(result));
});
Test("equal-length same-mtime source mutation changes original revision SHA", () =>
{
    string clone = Path.Combine(work, "same-metadata"); Directory.CreateDirectory(clone);
    string copy = Path.Combine(clone, Path.GetFileName(expectedAap)); File.Copy(expectedAap, copy);
    File.Copy(nativeSource.NativeProjectPath, Path.Combine(clone, Path.GetFileName(nativeSource.NativeProjectPath)));
    byte[] bytes = File.ReadAllBytes(copy); int space = Array.IndexOf(bytes, (byte)' '); Check(space >= 0, "No whitespace mutation location.");
    DateTime stamp = File.GetLastWriteTimeUtc(copy); bytes[space] = (byte)'\t'; File.WriteAllBytes(copy, bytes); File.SetLastWriteTimeUtc(copy, stamp);
    var changed = aapReader.Read(copy);
    Check(changed.Success && changed.Value != null && changed.Value.Source.Length == project.Source.Length
        && changed.Value.Source.RevisionSha256 != project.Source.RevisionSha256
        && changed.Value.Source.RevisionSha256.Equals(Hash(copy), StringComparison.OrdinalIgnoreCase), "Original snapshot hash missed equal-size, equal-time byte mutation: " + changed.Error);
});
Test("project timestamp newer than archive fails original freshness gate", () =>
{
    string clone = Path.Combine(work, "stale"); Directory.CreateDirectory(clone);
    string aap = Path.Combine(clone, Path.GetFileName(expectedAap)); string aas = Path.Combine(clone, Path.GetFileName(selectedAas));
    File.Copy(expectedAap, aap); File.Copy(selectedAas, aas); File.Copy(nativeSource.NativeProjectPath, Path.Combine(clone, Path.GetFileName(nativeSource.NativeProjectPath)));
    File.SetLastWriteTimeUtc(aap, File.GetLastWriteTimeUtc(aas).AddMinutes(5));
    var p = aapReader.Read(aap); var a = aasReader.Read(aas);
    Check(p.Success && a.Success, "Stale fixture cannot be read: " + p.Error + "; " + a.Error);
    var result = resolver.Resolve(Identity(a.Value!.Records[1]), p.Value!, a.Value!);
    Check(result.Success && !result.Value!.ArchivePairFresh && !Trusted(result.Value), "Stale project/archive pair was trusted.");
});
Test("offline proof leaves original AAP and AAS bytes unchanged", () =>
    Check(Hash(expectedAap) == expectedAapHash && Hash(selectedAas) == expectedAasHash, "Offline proof changed original inputs."));
Console.WriteLine($"RESULT {passed} passed; {failed} failed; pure original Core/Formats only");
File.WriteAllText(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(new {
    Passed = passed, Failed = failed, Runtime = Environment.Version.ToString(), DataRoot = source, SelectedAas = selectedAas,
    ProjectSource = project.Source, PlaybackSource = playback.Source, NativeCompatibility = nativeSource, Pairs = pairs, Resolutions = resolutions, Tests = results,
    RecordApi = new { RecordType = typeof(PlaybackRecordSnapshot).FullName, Properties = typeof(PlaybackRecordSnapshot).GetProperties().Select(property => new { property.Name, Type = property.PropertyType.FullName }) },
    Scope = "Offline actual original Core/Formats; no native AA, Unity constructor/cctor, Rukari.MoreEffects startup, scene playback, or production modification."
}, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;
internal delegate object ProjectionValidator(string path, JsonElement json, ReadOnlySpan<byte> bytes, bool requireNative, long maxBytes, int maxDepth);
