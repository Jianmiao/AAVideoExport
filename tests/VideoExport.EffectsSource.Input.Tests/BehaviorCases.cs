using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;

internal static class BehaviorCases
{
    internal static int Run()
    {
        int passed = 0, failed = 0; var results = new List<object>();
        void Test(string name, Action run)
        {
            try { run(); passed++; results.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; results.Add(new { Name = name, Passed = false, Error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        void Files(Action<Fixture> run) { using var fixture = new Fixture(); run(fixture); }
        Test("actual original archive discovery pins AAP AAS and original declared AAP2 before authored playback", () =>
        {
            string data = Path.GetFullPath("artifacts/controlled-authoring-workspace-20261030/data");
            string archive = Path.GetFullPath(Path.Combine(data, "saves/AAVE-OfficialAuthoring-20261030.aas"));
            var service = new OriginalService(data, archive);
            using var proof = EffectsSourceInput.Acquire(service, archive, Fixture.Hash(archive));
            Check(proof.ProjectSha256 == "459A7965048FD41DCA4D3AF8AA647D38709F97BA1344502F0326C4FD4FEF2D74", "Actual project was not retained.");
            Check(service.Rows.SequenceEqual(new[] { 0, 1, 1 }) && service.Reloads == 1, "Title was promoted or trusted row not re-proved.");
            proof.RevalidateForPass(); proof.ValidateCurrent(true); Task.Run(proof.ValidateFiles).GetAwaiter().GetResult();
            Check(service.Last!.AdditionalInputs.Single().Sha256 == "A4978A0BA4DF668B34E35F59D680E2C8EF3938FC4BD540FD77C830259530C7E7", "Actual AAP2 omitted.");
        });
        Test("valid proof prevents writers to project archive and additional native input", () => Files(f =>
        {
            using var proof = f.Acquire();
            foreach (string path in new[] { f.Project, f.Archive, f.Native }) Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write).Dispose());
            Check(f.Service.Rows.SequenceEqual(new[] { 0, 1, 1 }) && f.Service.Reloads == 1 && proof.Identity.Length == 64, "Acquisition sequence changed.");
        }));
        Test("worker-frozen archive hash rejects changed bytes before service calls", () => Files(f =>
        {
            Reject(() => EffectsSourceInput.Acquire(f.Service, f.Archive, new string('0', 64))); Check(f.Service.Captures == 0, "Service ran before source pin.");
            using var writable = File.Open(f.Archive, FileMode.Open, FileAccess.Write);
        }));
        Test("reader snapshot must name selected archive and exact size", () =>
        {
            Files(f => { f.Service.ArchiveInput = f.Service.ArchiveInput with { FullPath = f.Project }; Reject(() => f.Acquire()); });
            Files(f => { f.Service.ArchiveInput = f.Service.ArchiveInput with { Length = 999 }; Reject(() => f.Acquire()); });
        });
        Test("concrete archive row positions cannot be omitted or reindexed", () => Files(f =>
        { f.Service.ArchiveInput = f.Service.ArchiveInput with { Rows = new[] { new EffectsSourceRow(1, "wrong") } }; Reject(() => f.Acquire()); }));
        Test("initial missing original project binding gives source-unavailable guidance without claiming a changed timeline", () => Files(f =>
        {
            f.Service.ArchiveInput = f.Service.ArchiveInput with { Rows = new[] { new EffectsSourceRow(0, "title") } };
            var error = Reject(() => f.Acquire(), "strict_effects_source_unavailable");
            string text = ExportErrorText.Describe(error);
            Check(text.Contains("原始工程") && text.Contains("可信绑定") && !text.Contains("两遍") && !text.Contains("发生变化"),
                "Initial source discovery failure was described as a changed timeline: " + text);
            Check(error.Message == "Effects source input: No concrete selected-archive row has a fully trusted original project binding.",
                "Original source diagnostic was not retained.");
            Check(f.Service.Reloads == 0, "Missing proof reloaded index.");
            using var writable = File.Open(f.Archive, FileMode.Open, FileAccess.Write);
        }));
        Test("every original trusted flag is mandatory", () =>
        {
            for (int flag = 0; flag < 4; flag++) Files(f =>
            {
                f.Service.Binding = flag switch { 0 => f.Service.Binding with { Mapped = false }, 1 => f.Service.Binding with { SelectedSceneTrusted = false },
                    2 => f.Service.Binding with { ArchivePairFresh = false }, _ => f.Service.Binding with { PairingTrusted = false } };
                Reject(() => f.Acquire(), "strict_effects_source_unavailable"); Check(f.Service.Reloads == 0, "Invalid proof reloaded index.");
            });
        });
        Test("trusted different AAS cannot replace exact selected archive", () => Files(f =>
        { string other = Path.Combine(f.Root, "other.aas"); File.Copy(f.Archive, other); f.Service.Binding = f.Service.Binding with { ArchivePath = other }; Reject(() => f.Acquire()); }));
        Test("binding cannot map another archive row", () => Files(f =>
        { f.Service.Binding = f.Service.Binding with { PlaybackRowIndex = 0 }; Reject(() => f.Acquire()); }));
        Test("project and additional source snapshot digests must match actual bytes", () =>
        {
            Files(f => { f.Service.Binding = f.Service.Binding with { ProjectSha256 = new string('f', 64) }; Reject(() => f.Acquire()); });
            Files(f => {
                f.Service.Binding = f.Service.Binding with { AdditionalInputs = new[] { new EffectsSourceFile(f.Native, new string('f', 64), new FileInfo(f.Native).Length) } };
                Reject(() => f.Acquire()); using var p = File.Open(f.Project, FileMode.Open, FileAccess.Write); using var a = File.Open(f.Archive, FileMode.Open, FileAccess.Write);
            });
        });
        Test("changed binding between discovery and locked reproof is rejected", () => Files(f =>
        { f.Service.AfterResolve = count => { if (count == 1) f.Service.Binding = f.Service.Binding with { StorageRevision = 2 }; }; Reject(() => f.Acquire()); Check(f.Service.Reloads == 0, "Changed acquisition reloaded index."); }));
        Test("new storage revision must reprove same files and cannot change input identity", () => Files(f =>
        { using var proof = f.Acquire(); string identity = proof.Identity; f.Service.Binding = f.Service.Binding with { StorageRevision = 17 }; proof.RevalidateForPass(); Check(identity == proof.Identity, "Revision changed stable key."); }));
        Test("same-content different project path is refused between passes", () => Files(f =>
        {
            using var proof = f.Acquire(); string other = Path.Combine(f.Root, "other.aap"); File.Copy(f.Project, other);
            f.Service.Binding = f.Service.Binding with { ProjectPath = other }; Reject(proof.RevalidateForPass);
        }));
        Test("original binding lost after acquisition remains a changed-source error", () => Files(f =>
        {
            using var proof = f.Acquire();
            f.Service.Binding = f.Service.Binding with { PairingTrusted = false };
            var error = Reject(proof.RevalidateForPass);
            Check(ExportErrorText.Describe(error).Contains("发生变化"), "Retained source failure lost its changed-input guidance.");
        }));
        Test("late original-service invalidation is not healed with cached success", () => Files(f =>
        { using var proof = f.Acquire(); f.Service.Invalid = true; Reject(() => proof.ValidateCurrent()); Reject(proof.RevalidateForPass); }));
        Test("service validation stays main-thread-only while raw file verification supports workers", () => Files(f =>
        {
            using var proof = f.Acquire(); Task.Run(proof.ValidateFiles).GetAwaiter().GetResult();
            Task.Run(() => Reject(proof.RevalidateForPass)).GetAwaiter().GetResult(); Task.Run(() => Reject(() => proof.ValidateCurrent())).GetAwaiter().GetResult();
        }));
        Test("disposing rejects late publication and releases every file handle", () => Files(f =>
        {
            var proof = f.Acquire(); proof.Dispose(); proof.Dispose(); Reject(proof.ValidateFiles); Reject(() => proof.AcquireCommitScope());
            foreach (string path in new[] { f.Project, f.Archive, f.Native }) using (File.Open(path, FileMode.Open, FileAccess.Write)) { }
        }));
        Test("publication refuses reentrant disposal nested scopes and cross-thread release", () => Files(f =>
        {
            using var proof = f.Acquire(); using var commit = proof.AcquireCommitScope(); Reject(proof.Dispose); Reject(() => proof.AcquireCommitScope());
            Task.Run(() => Reject(commit.Dispose)).GetAwaiter().GetResult(); proof.ValidateFiles();
        }));
        Test("disposal on another thread waits until synchronous publication releases", () => Files(f =>
        {
            var proof = f.Acquire(); var commit = proof.AcquireCommitScope(); using var started = new ManualResetEventSlim();
            var cleanup = Task.Run(() => { started.Set(); proof.Dispose(); }); Check(started.Wait(TimeSpan.FromSeconds(3)), "Cleanup never started.");
            Check(!cleanup.Wait(60), "Publication did not retain the input lock."); commit.Dispose(); Check(cleanup.Wait(TimeSpan.FromSeconds(3)), "Cleanup did not resume."); Reject(proof.ValidateFiles);
        }));
        Test("actual changed project bytes produce a new stable input identity after release", () => Files(f =>
        {
            string old; using (var proof = f.Acquire()) old = proof.Identity;
            File.AppendAllText(f.Project, "-authorized-change"); f.Service.Binding = f.Service.Binding with { ProjectSha256 = Fixture.Hash(f.Project), ProjectLength = new FileInfo(f.Project).Length };
            using var next = f.Acquire(); Check(old != next.Identity, "Actual changed input reused old identity.");
        }));
        Test("original playback proof retains inputs without promoting editor-wide trust or clearing the ready index", () => Files(f =>
        {
            Playback(f);
            using var proof = f.Acquire();
            Check(!f.Service.Binding.ArchivePairFresh && !f.Service.Binding.PairingTrusted, "Editor permissions were promoted.");
            Check(f.Service.Reloads == 0, "The already-proved original playback index was cleared.");
            proof.RevalidateForPass(); proof.ValidateCurrent(true);
            foreach (string path in new[] { f.Project, f.Archive, f.Native }) Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write).Dispose());
        }));
        Test("playback proof cannot authorize an unmapped or untrusted selected scene", () =>
        {
            Files(f => { Playback(f); f.Service.Binding = f.Service.Binding with { Mapped = false }; Reject(() => f.Acquire(), "strict_effects_source_unavailable"); });
            Files(f => { Playback(f); f.Service.Binding = f.Service.Binding with { SelectedSceneTrusted = false }; Reject(() => f.Acquire(), "strict_effects_source_unavailable"); });
        });
        Test("playback proof dependencies are hash checked and held against writers", () => Files(f =>
        {
            string extra = Path.Combine(f.Root, "original.mapping"); File.WriteAllText(extra, "actual original mapping");
            Playback(f, new[] { new EffectsSourceFile(extra, Fixture.Hash(extra), new FileInfo(extra).Length) });
            using (var proof = f.Acquire()) Throws<IOException>(() => File.Open(extra, FileMode.Open, FileAccess.Write).Dispose());
            f.Service.Binding = f.Service.Binding with { PlaybackProof = f.Service.Binding.PlaybackProof! with
                { Inputs = new[] { new EffectsSourceFile(extra, new string('F', 64), new FileInfo(extra).Length) } } };
            Reject(() => f.Acquire());
        }));
        Test("absent original sidecar cannot appear during capture or publication", () => Files(f =>
        {
            string absent = Path.Combine(f.Root, "original.workspace.json"); Playback(f, absent: new[] { absent });
            using (var proof = f.Acquire())
            {
                File.WriteAllText(absent, "new input"); Reject(proof.ValidateFiles); Reject(() => proof.AcquireCommitScope());
            }
            Reject(() => f.Acquire());
        }));
        Test("original playback identity and read-set cannot change between passes", () =>
        {
            Files(f => { Playback(f); using var proof = f.Acquire(); f.Service.Binding = f.Service.Binding with
                { PlaybackProof = f.Service.Binding.PlaybackProof! with { Identity = new string('B', 64) } }; Reject(proof.RevalidateForPass); });
            Files(f => { Playback(f); using var proof = f.Acquire(); f.Service.Binding = f.Service.Binding with
                { PlaybackProof = f.Service.Binding.PlaybackProof! with { AbsentInputs = new[] { Path.Combine(f.Root, "different.workspace") } } }; Reject(proof.RevalidateForPass); });
            Files(f => { Playback(f); using var proof = f.Acquire(); f.Service.Binding = f.Service.Binding with { PlaybackProof = null }; Reject(proof.RevalidateForPass); });
        });
        Test("playback command identity participates in the stable export identity", () => Files(f =>
        {
            Playback(f); string old; using (var proof = f.Acquire()) old = proof.Identity;
            f.Service.Binding = f.Service.Binding with { PlaybackProof = f.Service.Binding.PlaybackProof! with { Identity = new string('B', 64) } };
            using var next = f.Acquire(); Check(old != next.Identity, "Different original playback commands reused the baseline key.");
        }));
        Console.WriteLine($"RESULT {passed} passed; {failed} failed; held-input behavior with actual files and original Core/Formats");
        File.WriteAllText(Path.GetFullPath("artifacts/effects-settings-source-20261030/input-results.json"), JsonSerializer.Serialize(new { Passed = passed, Failed = failed, Tests = results,
            Scope = "Held-input primitive and pure original archive algorithms; no native AA or reflection bridge runtime acceptance." }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Playback(Fixture fixture, IReadOnlyList<EffectsSourceFile>? inputs = null, IReadOnlyList<string>? absent = null)
    {
        fixture.Service.Binding = fixture.Service.Binding with { ArchivePairFresh = false, PairingTrusted = false,
            PlaybackProof = new(new string('A', 64), inputs ?? Array.Empty<EffectsSourceFile>(), absent ?? Array.Empty<string>()) };
    }
    private static ExportException Reject(Action action, string expectedCode = "timeline_source_changed")
    {
        try { action(); }
        catch (ExportException error)
        {
            Check(error.Code == expectedCode, $"Expected {expectedCode}, got {error.Code}: {error.Message}");
            return error;
        }
        throw new Exception("Expected source proof refusal.");
    }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}

sealed class Fixture : IDisposable
{
    public string Root = Path.Combine(Path.GetTempPath(), "aave-effects-source-" + Guid.NewGuid().ToString("N"));
    public string Project, Archive, Native; public Service Service;
    public Fixture()
    {
        Directory.CreateDirectory(Root); Project = Path.Combine(Root, "fixture.aap"); Archive = Path.Combine(Root, "fixture.aas"); Native = Path.Combine(Root, "declared.native");
        File.WriteAllText(Project, "actual-project"); File.WriteAllText(Archive, "actual-archive"); File.WriteAllText(Native, "actual-native");
        Service = new Service {
            ArchiveInput = new(Archive, Hash(Archive), new FileInfo(Archive).Length, new[] { new EffectsSourceRow(0, "title"), new EffectsSourceRow(1, "scene") }),
            Binding = new(Project, Archive, Hash(Project), Hash(Archive), new FileInfo(Project).Length, new FileInfo(Archive).Length, 1, 1, true, true, true, true)
                { AdditionalInputs = new[] { new EffectsSourceFile(Native, Hash(Native), new FileInfo(Native).Length) } }
        };
    }
    public EffectsSourceInput Acquire() => EffectsSourceInput.Acquire(Service, Archive, Hash(Archive));
    public static string Hash(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)); }
    public void Dispose() { Directory.Delete(Root, true); }
}
sealed class Service : IEffectsSourceService
{
    public EffectsArchiveInput ArchiveInput = null!; public EffectsSourceBinding Binding = null!;
    public List<int> Rows = new(); public int Captures, Reloads, TrustedResolves; public bool Invalid; public Action<int>? AfterResolve;
    public void CaptureOfficialStorage() { if (Invalid) throw new ExportException("timeline_source_changed", "invalidated"); Captures++; }
    public EffectsArchiveInput ReadArchive(string path) => ArchiveInput;
    public EffectsSourceBinding? Resolve(EffectsSourceRow row) { Rows.Add(row.Index); if (row.Index == 0) return null; var value = Binding; AfterResolve?.Invoke(++TrustedResolves); return value; }
    public void ValidateCurrent(EffectsSourceBinding expected, bool requireActive)
    { if (Invalid || expected.ProjectPath != Binding.ProjectPath || expected.ProjectSha256 != Binding.ProjectSha256 || expected.StorageRevision != Binding.StorageRevision) throw new ExportException("timeline_source_changed", "changed service"); }
    public void RequestIndexReload() => Reloads++;
}
sealed class OriginalService(string data, string archive) : IEffectsSourceService
{
    private readonly AapProjectReader projectReader = new(); private readonly AasScenarioReader archiveReader = new();
    public readonly List<int> Rows = new(); public int Reloads; private long revision; public EffectsSourceBinding? Last;
    public void CaptureOfficialStorage() => revision++;
    public EffectsArchiveInput ReadArchive(string path)
    {
        var result = archiveReader.Read(path); if (!result.Success || result.Value == null) throw new Exception(result.Error); var snapshot = result.Value;
        return new(snapshot.Source.FullPath, snapshot.Source.RevisionSha256, snapshot.Source.Length, snapshot.Records.Select(record => new EffectsSourceRow(record.RecordIndex, record.CompiledScript)).ToArray());
    }
    public EffectsSourceBinding? Resolve(EffectsSourceRow row)
    {
        Rows.Add(row.Index); var identity = CommandIdentity.CompiledScript(row.Script);
        var selected = new ObservedCompiledSceneIdentity(identity.Sha256, identity.Utf16Length, identity.LineCount) { PlaybackRowIndex = row.Index };
        var scanned = new ProjectPairScanner().Scan(data); if (!scanned.Success || scanned.Value == null) throw new Exception(scanned.Error);
        var arbiter = new ActiveProjectPairArbiter(projectReader.Read, archiveReader.Read, new ObservedSceneIdentityResolver());
        var resolved = arbiter.Arbitrate(selected, scanned.Value); if (!resolved.Success || resolved.Pair == null || resolved.Resolution == null) return null;
        var pair = resolved.Pair; var resolution = resolved.Resolution; var p = projectReader.Read(pair.AapPath).Value!; var a = archiveReader.Read(pair.AasPath).Value!;
        byte[] bytes = File.ReadAllBytes(pair.AapPath); using var json = JsonDocument.Parse(bytes); var compatibility = OfficialAapCompatibilityProjection.ValidateRead(pair.AapPath, json.RootElement, bytes);
        if (!compatibility.Success || compatibility.Value == null) throw new Exception(compatibility.Error);
        var extra = compatibility.Value.NativeProjectPath.Length == 0 ? Array.Empty<EffectsSourceFile>() : new[] { new EffectsSourceFile(compatibility.Value.NativeProjectPath, compatibility.Value.NativeRevisionSha256, new FileInfo(compatibility.Value.NativeProjectPath).Length) };
        return Last = new(p.Source.FullPath, a.Source.FullPath, p.Source.RevisionSha256, a.Source.RevisionSha256, p.Source.Length, a.Source.Length, revision,
            resolution.PlaybackRecordIndex ?? -1, resolution.Status == ObservedSceneIdentityStatus.Mapped, resolution.SelectedSceneTrusted, resolution.ArchivePairFresh, resolution.PairingTrusted) { AdditionalInputs = extra };
    }
    public void ValidateCurrent(EffectsSourceBinding expected, bool requireActive)
    { if (Last == null || Last.ProjectPath != expected.ProjectPath || Last.ArchivePath != archive || expected.StorageRevision != revision) throw new ExportException("timeline_source_changed", "original binding changed"); }
    public void RequestIndexReload() => Reloads++;
}
