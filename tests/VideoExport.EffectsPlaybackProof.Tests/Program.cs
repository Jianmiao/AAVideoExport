using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;
using AzureArchive.VideoTools.Formats.Sidecar;

// These are the actual reported inputs. Never write their contents or metadata.
const string projectPath = @"E:\AAdata\data\projects\百鬼夜行-间章.aap";
const string archivePath = @"E:\AAdata\data\saves\百鬼夜行-间章.aas";
var service = new ActualService(projectPath, archivePath);
int passed = 0, failed = 0;
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Reject(Action action)
{
    try { action(); }
    catch (Exception error) when (error.GetBaseException() is ExportException) { return; }
    throw new Exception("Invalid effective playback input was accepted.");
}
Test("reported project has stale editor flags but an exact original 13-command 8-batch playback index", () =>
{
    Check(service.Playback.Records.Count == 198 && service.Compilation.CommandCount == 13 && service.Index.BatchCount == 8, "Reported input changed.");
    var row = service.Resolve(new EffectsSourceRow(3, service.Playback.Records[3].CompiledScript))!;
    Check(row.Mapped && row.SelectedSceneTrusted && !row.ArchivePairFresh && !row.PairingTrusted, "Reported editor disagreement no longer reproduces.");
});
Test("actual original playable source acquires without rewriting editor trust flags", () =>
{
    using var proof = EffectsSourceInput.Acquire(service, archivePath, service.Playback.Source.RevisionSha256);
    Check(proof.ArchiveSha256 == service.Playback.Source.RevisionSha256, "Wrong archive acquired.");
    proof.RevalidateForPass(); proof.ValidateFiles();
    Check(service.Reloads == 0, "Read-only playback proof unnecessarily cleared the original runtime index.");
});
Test("wrong exact playback revision is rejected by the original index proof", () => Reject(() => service.CreateProof(new string('0', 64))));
Test("wrong exact playback path key is rejected by the original index proof", () => Reject(() => service.CreateProof(pathKey: new string('0', 64))));
Test("wrong record count is rejected by the original index proof", () => Reject(() => service.CreateProof(recordCount: 197)));
Test("uncovered external workspace branch is rejected", () => Reject(() => service.CreateProof(workspace: "Primary+embedded-environment:13")));
Test("changed continue identities change the retained effective input identity", () =>
{
    object original = service.CreateProof()!;
    object changed = service.CreateProof(continues: new[] { new string('1', 64) })!;
    Check(original.GetType().GetProperty("Identity")!.GetValue(original)?.ToString() != changed.GetType().GetProperty("Identity")!.GetValue(changed)?.ToString(), "Continue context omitted from identity.");
});
Test("an absent dependency appearing is rejected before acquisition", () =>
{
    string path = Path.GetTempFileName();
    try { Reject(() => service.CreateProof(absent: new[] { path })); }
    finally { File.Delete(path); }
});
Test("existing fully trusted source never invokes the embedded-only proof branch", () =>
{
    var row = service.Resolve(new EffectsSourceRow(3, service.Playback.Records[3].CompiledScript))! with { ArchivePairFresh = true, PairingTrusted = true, PlaybackProof = null };
    var method = typeof(EffectsSourceInput).Assembly.GetType("AAVideoExport.Plugin.EffectsPlaybackProofBuilder")!
        .GetMethod("WithRequiredProof", BindingFlags.Static | BindingFlags.NonPublic);
    if (method == null) throw new Exception("The playback proof branch does not yet preserve existing complete source bindings.");
    var result = (EffectsSourceBinding)method.Invoke(null, new object[] { row, (Func<EffectsPlaybackProof>)(() => throw new Exception("Embedded commands are disabled for this already-trusted legacy binding.")) })!;
    Check(result == row && result.PlaybackProof == null, "Legacy trusted source was routed through embedded-only requirements.");
});
Console.WriteLine($"RESULT {passed} passed; {failed} failed; actual original read-only source and production retained proof");
return failed == 0 ? 0 : 1;

sealed class ActualService : IEffectsSourceService
{
    public ProjectSnapshot Project;
    public PlaybackArchiveSnapshot Playback;
    public EmbeddedProjectCommandCompilation Compilation;
    public IPlaybackCommandIndex Index;
    public int Reloads;
    private long revision;
    private readonly ObservedSceneIdentityResolver resolver = new();
    private readonly EffectsSourceFile[] additional;
    public ActualService(string projectPath, string archivePath)
    {
        Project = new AapProjectReader().Read(projectPath).Value ?? throw new Exception("Original project reader failed.");
        Playback = new AasScenarioReader().Read(archivePath).Value ?? throw new Exception("Original archive reader failed.");
        var compilation = new EmbeddedProjectCommandCompiler().Compile(Project, Playback, "1.6.7");
        Compilation = compilation.Value ?? throw new Exception(compilation.Error);
        var bound = new PlaybackCommandBinder().Bind(Playback, Compilation.Projection);
        Index = bound.Value ?? throw new Exception(bound.Error);
        byte[] bytes = File.ReadAllBytes(projectPath); using var document = JsonDocument.Parse(bytes);
        var native = OfficialAapCompatibilityProjection.ValidateRead(projectPath, document.RootElement, bytes).Value ?? throw new Exception("Original native projection validation failed.");
        additional = new[] { new EffectsSourceFile(native.NativeProjectPath, native.NativeRevisionSha256, new FileInfo(native.NativeProjectPath).Length) };
    }
    public object? CreateProof(string? hash = null, string? pathKey = null, int? recordCount = null, string workspace = "embedded-environment:13", IReadOnlyList<string>? continues = null, IReadOnlyList<string>? absent = null)
    {
        var type = typeof(EffectsSourceInput).Assembly.GetType("AAVideoExport.Plugin.EffectsPlaybackProofBuilder");
        if (type == null) return null; // Pre-fix red uses the unchanged actual acquisition path.
        return type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] {
            Index, pathKey ?? Playback.Source.PathKey, hash ?? Playback.Source.RevisionSha256, recordCount ?? Playback.Records.Count,
            workspace, continues ?? Compilation.ContinueIdentities, "actual-original-embedded-config", additional,
            absent ?? new[] { JsonCompilationMappingEvidenceStore.GetPath(Project.Source.FullPath) }
        });
    }
    public void CaptureOfficialStorage() => revision++;
    public EffectsArchiveInput ReadArchive(string path) => new(Playback.Source.FullPath, Playback.Source.RevisionSha256, Playback.Source.Length,
        Playback.Records.Select(record => new EffectsSourceRow(record.RecordIndex, record.CompiledScript)).ToArray());
    public EffectsSourceBinding? Resolve(EffectsSourceRow row)
    {
        var script = CommandIdentity.CompiledScript(row.Script);
        var result = resolver.Resolve(new(script.Sha256, script.Utf16Length, script.LineCount) { PlaybackRowIndex = row.Index }, Project, Playback);
        if (!result.Success || result.Value == null) return null;
        var value = result.Value;
        var binding = new EffectsSourceBinding(Project.Source.FullPath, Playback.Source.FullPath, Project.Source.RevisionSha256,
            Playback.Source.RevisionSha256, Project.Source.Length, Playback.Source.Length, revision, value.PlaybackRecordIndex ?? -1,
            value.Status == ObservedSceneIdentityStatus.Mapped, value.SelectedSceneTrusted, value.ArchivePairFresh, value.PairingTrusted) { AdditionalInputs = additional };
        if (binding.Mapped && binding.SelectedSceneTrusted)
            typeof(EffectsSourceBinding).GetProperty("PlaybackProof")?.SetValue(binding, CreateProof());
        return binding;
    }
    public void ValidateCurrent(EffectsSourceBinding expected, bool requireActive)
    {
        if (expected.ArchiveSha256 != Playback.Source.RevisionSha256 || expected.ProjectSha256 != Project.Source.RevisionSha256 || expected.StorageRevision != revision)
            throw new ExportException("timeline_source_changed", "Actual source changed.");
    }
    public void RequestIndexReload() => Reloads++;
}
