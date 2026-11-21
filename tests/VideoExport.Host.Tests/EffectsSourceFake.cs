using System.Security.Cryptography;
using AAVideoExport.Plugin;

// Only the original Mod/native discovery service is substituted in Host tests.
// The production input file locks, hashes and retained lease run unchanged.
internal sealed class EffectsSourceFake : IEffectsSourceService
{
    internal static Action? BeforeValidation;
    private readonly string archive, project;
    internal EffectsSourceFake(string archive)
    {
        this.archive = Path.GetFullPath(archive);
        project = Path.Combine(Path.GetDirectoryName(archive)!, "effects-source-fixture.aap");
        if (!File.Exists(project)) File.WriteAllText(project, "same complete authored effects project");
    }
    public void CaptureOfficialStorage() { }
    public EffectsArchiveInput ReadArchive(string path) => new(archive, Hash(archive), new FileInfo(archive).Length,
        new[] { new EffectsSourceRow(0, "fixture authored row") });
    public EffectsSourceBinding? Resolve(EffectsSourceRow row) => new(project, archive, Hash(project), Hash(archive),
        new FileInfo(project).Length, new FileInfo(archive).Length, 1, row.Index, true, true, true, true);
    public void ValidateCurrent(EffectsSourceBinding expected, bool requireActive) { BeforeValidation?.Invoke(); }
    public void RequestIndexReload() { }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed class EffectsExecutionFake : IEffectsExecutionSource
{
    private string archive = "";
    private bool disposed;
    public string ProjectPath => "";
    public void PrepareForPass(string selectedArchive) { archive = selectedArchive; EnsureReady(); }
    public void EnsureReady()
    {
        if (disposed || !File.Exists(archive)) throw new AAVideoExport.Core.ExportException("story_missing", "Selected execution input is unavailable.");
        EffectsSourceFake.BeforeValidation?.Invoke();
    }
    public void Dispose() => disposed = true;
}
