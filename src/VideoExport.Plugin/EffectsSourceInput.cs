using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

internal sealed record EffectsSourceRow(int Index, string Script);
internal sealed record EffectsSourceFile(string FullPath, string Sha256, long Length);
internal sealed record EffectsPlaybackProof(string Identity, IReadOnlyList<EffectsSourceFile> Inputs, IReadOnlyList<string> AbsentInputs);
internal sealed record EffectsArchiveInput(string FullPath, string Sha256, long Length, IReadOnlyList<EffectsSourceRow> Rows)
{
    public IReadOnlyList<EffectsSourceFile> AdditionalInputs { get; init; } = Array.Empty<EffectsSourceFile>();
}
internal sealed record EffectsSourceBinding(string ProjectPath, string ArchivePath, string ProjectSha256, string ArchiveSha256,
    long ProjectLength, long ArchiveLength, long StorageRevision, int PlaybackRowIndex,
    bool Mapped, bool SelectedSceneTrusted, bool ArchivePairFresh, bool PairingTrusted)
{
    public IReadOnlyList<EffectsSourceFile> AdditionalInputs { get; init; } = Array.Empty<EffectsSourceFile>();
    public EffectsPlaybackProof? PlaybackProof { get; init; }
}
internal interface IEffectsSourceService
{
    void CaptureOfficialStorage();
    EffectsArchiveInput ReadArchive(string path);
    EffectsSourceBinding? Resolve(EffectsSourceRow row);
    void ValidateCurrent(EffectsSourceBinding expected, bool requireActive);
    void RequestIndexReload();
}

internal interface IEffectsExecutionSource : IDisposable
{
    string ProjectPath { get; }
    void PrepareForPass(string selectedArchive);
    void EnsureReady();
}

// A selected archive is authorized by the original source service, never by
// an inferred sibling filename or whatever project happens to be active.
// Read locks and raw-byte hashes retain the proof through both native passes
// and the synchronous output-publication lease. No Unity access occurs here.
internal sealed class EffectsSourceInput : IDisposable
{
    internal static EffectsSourceInput AcquireExecution(IEffectsExecutionSource source, string selectedArchive)
    {
        ArgumentNullException.ThrowIfNull(source);
        var owner = new EffectsSourceInput(source, FullPath(selectedArchive));
        try
        {
            source.PrepareForPass(owner.executionArchive);
            source.EnsureReady();
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }
    private readonly object gate = new();
    private readonly IEffectsSourceService service = null!;
    private readonly IEffectsExecutionSource? execution;
    private readonly string executionArchive = "";
    private readonly int mainThread;
    private readonly Dictionary<string, FilePin> pins = new(StringComparer.OrdinalIgnoreCase);
    private EffectsSourceBinding binding = null!;
    private EffectsSourceRow row = null!;
    private bool disposed, commitHeld;

    private EffectsSourceInput(IEffectsSourceService service)
    { this.service = service; mainThread = Environment.CurrentManagedThreadId; }
    private EffectsSourceInput(IEffectsExecutionSource execution, string archive)
    { this.execution = execution; executionArchive = archive; mainThread = Environment.CurrentManagedThreadId; Identity = "execution:" + archive; }
    internal string ProjectPath => execution?.ProjectPath ?? binding.ProjectPath;
    internal string ArchivePath => execution != null ? executionArchive : binding.ArchivePath;
    internal string ProjectSha256 => execution == null ? binding.ProjectSha256 : throw new InvalidOperationException("Production execution has no audit hash.");
    internal string ArchiveSha256 => execution == null ? binding.ArchiveSha256 : throw new InvalidOperationException("Production execution has no audit hash.");
    internal string Identity { get; private set; } = "";

    internal static EffectsSourceInput Acquire(IEffectsSourceService service, string selectedArchive, string expectedArchiveSha256)
    {
        ArgumentNullException.ThrowIfNull(service);
        var owner = new EffectsSourceInput(service);
        try
        {
            string selected = FullPath(selectedArchive);
            string expected = HashValue(expectedArchiveSha256);
            var selectedPin = owner.AddPin(selected, expected, null);
            service.CaptureOfficialStorage();
            var archive = service.ReadArchive(selected) ?? throw Invalid("Original archive reader returned no input snapshot.");
            if (!SamePath(archive.FullPath, selected) || HashValue(archive.Sha256) != expected || archive.Length != selectedPin.Length)
                throw Invalid("Original archive snapshot differs from the exact selected file.");
            if (archive.Rows == null || archive.Rows.Count == 0) throw Invalid("Original selected archive has no concrete rows.");
            var rows = archive.Rows.Select(value => value == null ? throw Invalid("Original archive contains a null row.") : value with { }).ToArray();
            for (int index = 0; index < rows.Length; index++)
                if (rows[index].Index != index || rows[index].Script == null)
                    throw Invalid("Original archive rows violate the concrete positional script contract.");
            owner.AddPins(archive.AdditionalInputs);
            EffectsSourceBinding? selectedBinding = null;
            foreach (var candidateRow in rows)
            {
                var candidate = service.Resolve(candidateRow);
                if (!Trusted(candidate)) continue;
                var normalized = Normalize(candidate!);
                if (!SamePath(normalized.ArchivePath, selected)) throw Invalid("Original discovery selected a different AAS path.");
                if (normalized.PlaybackRowIndex != candidateRow.Index) throw Invalid("Original discovery mapped a different archive row.");
                if (normalized.ArchiveSha256 != expected || normalized.ArchiveLength != selectedPin.Length)
                    throw Invalid("Original discovered archive snapshot changed before input acquisition.");
                selectedBinding = normalized; owner.row = candidateRow; break;
            }
            if (selectedBinding == null) throw new ExportException("strict_effects_source_unavailable", "Effects source input: No concrete selected-archive row has a fully trusted original project binding.");
            owner.binding = selectedBinding;
            owner.AddPin(selectedBinding.ProjectPath, selectedBinding.ProjectSha256, selectedBinding.ProjectLength);
            owner.AddPins(selectedBinding.AdditionalInputs);
            if (selectedBinding.PlaybackProof is { } playbackProof) owner.AddPins(playbackProof.Inputs);
            owner.ValidateFiles();
            // Discovery may have read a file before its path was known. Repeat
            // the original proof with all returned sources held read-only.
            var retained = Normalize(service.Resolve(owner.row) ?? throw Invalid("Original binding disappeared after locking its inputs."));
            owner.RequireSameInput(retained);
            if (retained.StorageRevision != selectedBinding.StorageRevision) throw Invalid("Original storage revision changed during acquisition.");
            service.ValidateCurrent(retained, true);
            owner.binding = retained;
            string filesIdentity = InputIdentity(owner.pins.Values.Select(pin => pin.Identity));
            owner.Identity = retained.PlaybackProof == null ? filesIdentity
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filesIdentity + "|original-playback=" + PlaybackIdentity(retained.PlaybackProof))));
            // The playback proof includes the original ready index. Asking the
            // Mod to clear it now would discard its commands before frame zero.
            if (retained.PlaybackProof == null) service.RequestIndexReload();
            owner.ValidateCurrent(true);
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    internal void RevalidateForPass()
    {
        MainThread();
        lock (gate)
        {
            if (execution != null)
            {
                RequireLive(); execution.PrepareForPass(executionArchive); execution.EnsureReady(); return;
            }
            RequireLive(); ValidateFilesLocked();
            service.CaptureOfficialStorage(); RequireLive();
            var next = Normalize(service.Resolve(row) ?? throw Invalid("The original selected-source binding is unavailable before the next pass."));
            RequireSameInput(next);
            service.ValidateCurrent(next, true); RequireLive();
            ValidateFilesLocked();
            // A scene round trip can advance the original storage revision.
            // That is allowed only after re-proving the same actual inputs.
            binding = next;
        }
    }
    internal void ValidateCurrent(bool requireActive = false)
    {
        MainThread();
        lock (gate)
        {
            if (execution != null)
            {
                RequireLive(); if (requireActive) execution.EnsureReady(); return;
            }
            RequireLive(); ValidateFilesLocked(); service.ValidateCurrent(binding, requireActive);
            RequireLive(); ValidateFilesLocked();
        }
    }
    internal void ValidateFiles()
    {
        if (execution != null) throw new InvalidOperationException("Production execution does not run file audits.");
        lock (gate) { RequireLive(); ValidateFilesLocked(); }
    }
    internal IDisposable AcquireCommitScope()
    {
        if (execution != null) throw new InvalidOperationException("Production execution has no audit publication guard.");
        Monitor.Enter(gate);
        try
        {
            RequireLive();
            if (commitHeld) throw Invalid("A source-input publication scope is already held on this thread.");
            ValidateFilesLocked(); commitHeld = true;
            return new CommitScope(this, Environment.CurrentManagedThreadId);
        }
        catch { Monitor.Exit(gate); throw; }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (execution != null) { execution.Dispose(); disposed = true; return; }
            if (commitHeld) throw Invalid("Cannot dispose a source proof reentrantly inside its publication scope.");
            disposed = true;
            List<Exception>? errors = null;
            foreach (var pin in pins.Values)
            {
                try { pin.Stream.Dispose(); }
                catch (Exception error) { (errors ??= new()).Add(error); }
            }
            pins.Clear();
            if (errors != null) throw new AggregateException("One or more effects-source input handles could not be released.", errors);
        }
    }
    private void RequireSameInput(EffectsSourceBinding candidate)
    {
        if (!Trusted(candidate) || !SamePath(candidate.ProjectPath, binding.ProjectPath) || !SamePath(candidate.ArchivePath, binding.ArchivePath)
            || candidate.ProjectSha256 != binding.ProjectSha256 || candidate.ArchiveSha256 != binding.ArchiveSha256
            || candidate.ProjectLength != binding.ProjectLength || candidate.ArchiveLength != binding.ArchiveLength
            || candidate.PlaybackRowIndex != row.Index || AdditionalIdentity(candidate.AdditionalInputs) != AdditionalIdentity(binding.AdditionalInputs)
            || PlaybackIdentity(candidate.PlaybackProof) != PlaybackIdentity(binding.PlaybackProof))
            throw Invalid("Original project/archive proof changed while the selected source inputs were retained.");
    }
    private FilePin AddPin(string path, string expectedHash, long? expectedLength)
    {
        path = FullPath(path); expectedHash = HashValue(expectedHash);
        if (pins.TryGetValue(path, out var existing))
        {
            if (existing.Sha256 != expectedHash || expectedLength.HasValue && existing.Length != expectedLength.Value)
                throw Invalid("The original service supplied conflicting identities for one source path.");
            return existing;
        }
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            if (length <= 0 || expectedLength.HasValue && length != expectedLength.Value) throw Invalid("Source input length differs from its original reader snapshot.");
            using var sha = SHA256.Create(); string actual = Convert.ToHexString(sha.ComputeHash(stream)); stream.Position = 0;
            if (actual != expectedHash) throw Invalid("Source input bytes differ from their original reader snapshot.");
            var pin = new FilePin(path, actual, length, stream); pins.Add(path, pin); stream = null; return pin;
        }
        finally { stream?.Dispose(); }
    }
    private void AddPins(IReadOnlyList<EffectsSourceFile> additional)
    {
        if (additional == null) throw Invalid("The original service omitted its additional input collection.");
        foreach (var input in additional)
        {
            if (input == null || input.Length <= 0) throw Invalid("An additional source input is incomplete.");
            AddPin(input.FullPath, input.Sha256, input.Length);
        }
    }
    private void ValidateFilesLocked()
    {
        RequireLive();
        if (binding?.PlaybackProof is { } proof)
            foreach (string path in proof.AbsentInputs)
                if (File.Exists(path) || Directory.Exists(path))
                    throw Invalid("An originally absent effects-source dependency appeared.");
        foreach (var pin in pins.Values)
        {
            if (!File.Exists(pin.Path) || pin.Stream.Length != pin.Length || new FileInfo(pin.Path).Length != pin.Length)
                throw Invalid("A retained effects-source file disappeared or changed length.");
            pin.Stream.Position = 0;
            using var sha = SHA256.Create(); string actual = Convert.ToHexString(sha.ComputeHash(pin.Stream)); pin.Stream.Position = 0;
            if (actual != pin.Sha256) throw Invalid("A retained effects-source file changed bytes.");
        }
    }
    private static EffectsSourceBinding Normalize(EffectsSourceBinding value)
    {
        if (!Trusted(value) || value.ProjectLength <= 0 || value.ArchiveLength <= 0 || value.StorageRevision < 0 || value.PlaybackRowIndex < 0)
            throw Invalid("Original source binding is not a complete trusted mapping.");
        if (SamePath(value.ProjectPath, value.ArchivePath)) throw Invalid("Project and archive inputs cannot be the same file.");
        if (value.AdditionalInputs == null) throw Invalid("Original source binding omitted additional dependencies.");
        return value with {
            ProjectPath = FullPath(value.ProjectPath), ArchivePath = FullPath(value.ArchivePath),
            ProjectSha256 = HashValue(value.ProjectSha256), ArchiveSha256 = HashValue(value.ArchiveSha256),
            AdditionalInputs = Array.AsReadOnly(value.AdditionalInputs.Select(file => file == null || file.Length <= 0
                ? throw Invalid("Original source dependency is incomplete.") : file with { FullPath = FullPath(file.FullPath), Sha256 = HashValue(file.Sha256) }).ToArray()),
            PlaybackProof = NormalizePlayback(value.PlaybackProof)
        };
    }
    // Whole-project editor mapping permissions and the original command
    // loader's read-only playback proof are different contracts. The latter
    // never promotes editor permissions and still requires one exact selected
    // scene, immutable source files, and a verified original playback index.
    private static bool Trusted(EffectsSourceBinding? value) => value is { Mapped: true, SelectedSceneTrusted: true }
        && (value.ArchivePairFresh && value.PairingTrusted || value.PlaybackProof != null);
    private static EffectsPlaybackProof? NormalizePlayback(EffectsPlaybackProof? proof)
    {
        if (proof == null) return null;
        if (proof.Inputs == null || proof.AbsentInputs == null) throw Invalid("Original playback proof omitted its complete read-set.");
        return new(HashValue(proof.Identity), Array.AsReadOnly(proof.Inputs.Select(file => file == null || file.Length <= 0
            ? throw Invalid("Original playback dependency is incomplete.") : file with { FullPath = FullPath(file.FullPath), Sha256 = HashValue(file.Sha256) }).ToArray()),
            Array.AsReadOnly(proof.AbsentInputs.Select(FullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()));
    }
    private static string PlaybackIdentity(EffectsPlaybackProof? proof) => proof == null ? "none"
        : JsonSerializer.Serialize(new { Identity = HashValue(proof.Identity), Inputs = InputIdentity(proof.Inputs),
            AbsentInputs = proof.AbsentInputs.Select(path => FullPath(path).ToUpperInvariant()).OrderBy(path => path, StringComparer.Ordinal).ToArray() });
    private static string AdditionalIdentity(IEnumerable<EffectsSourceFile> files) => InputIdentity(files);
    private static string InputIdentity(IEnumerable<EffectsSourceFile> files)
    {
        var ordered = files.Select(file => new { Path = FullPath(file.FullPath).ToUpperInvariant(), Sha256 = HashValue(file.Sha256), file.Length })
            .OrderBy(file => file.Path, StringComparer.Ordinal).ThenBy(file => file.Sha256, StringComparer.Ordinal).ToArray();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Version = 1, Files = ordered }))));
    }
    private static string FullPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw Invalid("An original source path is not absolute.");
        return Path.GetFullPath(value);
    }
    private static bool SamePath(string left, string right) => string.Equals(FullPath(left), FullPath(right), StringComparison.OrdinalIgnoreCase);
    private static string HashValue(string value)
    {
        if (value == null || value.Length != 64 || !value.All(Uri.IsHexDigit)) throw Invalid("An original source SHA-256 is missing or malformed.");
        return value.ToUpperInvariant();
    }
    private void MainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThread) throw Invalid("Original source service requires its acquiring main thread.");
    }
    private void RequireLive() { if (disposed) throw Invalid("Effects source input has already been released."); }
    private static ExportException Invalid(string reason) => new("timeline_source_changed", "Effects source input: " + reason);
    private sealed record FilePin(string Path, string Sha256, long Length, FileStream Stream)
    { internal EffectsSourceFile Identity => new(Path, Sha256, Length); }
    private sealed class CommitScope(EffectsSourceInput owner, int thread) : IDisposable
    {
        private EffectsSourceInput? held = owner;
        public void Dispose()
        {
            if (held == null) return;
            if (Environment.CurrentManagedThreadId != thread) throw Invalid("Effects source publication lease must end on its acquiring worker without await.");
            var value = held; held = null; value.commitHeld = false; Monitor.Exit(value.gate);
        }
    }
}
