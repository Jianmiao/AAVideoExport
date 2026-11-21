using AAVideoExport.Core;
using System.Security.Cryptography;
using System.Text;

var passed = 0;
var failed = 0;
Run("held framework fingerprint ignores approved declaration count but preserves the audit list", () =>
{
    var first=FrameworkCapability("HarmonyDTFAssembly1",Hash("one-policy"));
    var before=StrictCapabilitySnapshot.Create(new[]{first});
    var after=StrictCapabilitySnapshot.Create(new[]{FrameworkCapability("HarmonyDTFAssembly2",first.Fingerprint),FrameworkCapability("Il2CppTrampolineDelegates",first.Fingerprint),first});
    Check(before.Fingerprint==after.Fingerprint,"Approved declaration growth changed policy fingerprint");
    Check(after.Modules.Count==3&&after.Modules.Select(m=>m.ModuleId).Distinct().Count()==3,"Normalization discarded the complete module audit list");
    Check(!after.CanReusePreflight,"Normalized observation enabled reuse");
});
Run("framework fingerprint retains unknown entries and distinct proof identities", () =>
{
    var first=FrameworkCapability("HarmonyDTFAssembly1",Hash("one-policy"));
    var baseline=StrictCapabilitySnapshot.Create(new[]{first});
    foreach(var level in new[]{StrictCapabilityLevel.Opaque,StrictCapabilityLevel.Rejected})
    {
        var unknown=new StrictCapability(first.ModuleId,first.Fingerprint,level,first.Evidence);
        var snapshot=StrictCapabilitySnapshot.Create(new[]{first,unknown});
        Check(snapshot.Fingerprint!=baseline.Fingerprint&&!snapshot.CanAdmitStrictExport,"Unknown or rejected same-name entry was hidden by framework aggregation");
    }
    foreach(var name in new[]{"Unknown.Mod","HarmonyDTFAssembly2"})
    {
        var bare=new StrictCapability(name,first.Fingerprint,StrictCapabilityLevel.Observed,new[]{"FrameworkGeneratedAssemblyScope.cached-bridges.v1"});
        var snapshot=StrictCapabilitySnapshot.Create(new[]{first,bare});
        Check(snapshot.Fingerprint!=baseline.Fingerprint&&!snapshot.CanAdmitStrictExport,"A name and bare marker were normalized as a complete held proof");
    }
    var second=FrameworkCapability("HarmonyDTFAssembly2",Hash("different-policy-payload"));
    var two=StrictCapabilitySnapshot.Create(new[]{first,second});
    Check(two.Fingerprint!=baseline.Fingerprint,"Distinct scope policy hashes were merged");
    Check(two.Fingerprint==StrictCapabilitySnapshot.Create(new[]{second,first,FrameworkCapability("HarmonyDTFAssembly100",second.Fingerprint)}).Fingerprint,
        "Distinct proof identities were not independently normalized and stably sorted");
    var malformed=new StrictCapability("HarmonyDTFAssembly2",first.Fingerprint,StrictCapabilityLevel.Observed,first.Evidence.Append("allCodeCoverage=true").ToArray());
    Check(!StrictCapabilitySnapshot.Create(new[]{malformed}).CanAdmitStrictExport,"Conflicting schema evidence was admitted");
});
Run("additional framework declarations require complete held cache evidence", () =>
{
    const string marker="FrameworkGeneratedAssemblyScope.cached-bridges.v1";
    var evidence=new[]{marker,"policy=net607.monomod22731.cached-framework-bridges.v1","evidenceKind=PINNED_CURRENT_FRAMEWORK_BRIDGE_METADATA",
        "actual-cached-assembly-reference","bounded-framework-bridges-only","preexistingCoverage=false","allCodeCoverage=false","permitsStrictExport=false","cache-reuse-disabled"};
    foreach(var name in new[]{"HarmonyDTFAssembly1","HarmonyDTFAssembly251","Il2CppTrampolineDelegates","MonoMod.Utils.GetManagedSizeHelper"})
    {
        var item=new StrictCapability(name,Hash(name),StrictCapabilityLevel.Observed,evidence);
        var snapshot=StrictCapabilitySnapshot.Create(new[]{item});snapshot.EnsureStrictExportAllowed();
        Check(!snapshot.CanReusePreflight,"Additional declaration proof enabled preflight reuse");
        foreach(var missing in evidence)
            Throws("strict_capability_unsupported",StrictCapabilitySnapshot.Create(new[]{new StrictCapability(name,Hash(name),StrictCapabilityLevel.Observed,evidence.Where(e=>e!=missing).ToArray())}).EnsureStrictExportAllowed);
        Throws("strict_capability_unsupported",StrictCapabilitySnapshot.Create(new[]{new StrictCapability(name,Hash(name),StrictCapabilityLevel.Opaque,evidence)}).EnsureStrictExportAllowed);
    }
    foreach(var name in new[]{"HarmonyDTFAssembly","HarmonyDTFAssembly0","HarmonyDTFAssembly01","HarmonyDTFAssembly4097","HarmonyDTFAssembly1.Fake","Unknown.Mod","Il2CppTrampolineDelegates.Fake","MonoMod.Utils.GetManagedSizeHelperFake"})
        Throws("strict_capability_unsupported",StrictCapabilitySnapshot.Create(new[]{new StrictCapability(name,Hash(name),StrictCapabilityLevel.Observed,evidence)}).EnsureStrictExportAllowed);
});
static StrictCapability FrameworkCapability(string name,string identity)=>new(name,identity,StrictCapabilityLevel.Observed,
    new[]{"FrameworkGeneratedAssemblyScope.cached-bridges.v1","policy=net607.monomod22731.cached-framework-bridges.v1",
        "evidenceKind=PINNED_CURRENT_FRAMEWORK_BRIDGE_METADATA","actual-cached-assembly-reference","bounded-framework-bridges-only",
        "preexistingCoverage=false","allCodeCoverage=false","permitsStrictExport=false","cache-reuse-disabled"});
Run("framework bridge admission is exact observed evidence and never enables reuse", () =>
{
    const string marker = "FrameworkGeneratedAssemblyScope.cached-bridges.v1";
    foreach (string name in new[] { "MonoMod.Utils.Cil.ILGeneratorProxy", "MonoMod.RuntimeDetour.Runtime.NETCore3+Helpers" })
    {
        var capability = new StrictCapability(name, Hash("held-framework"), StrictCapabilityLevel.Observed, new[] { marker });
        var snapshot = StrictCapabilitySnapshot.Create(new[] { capability });
        snapshot.EnsureStrictExportAllowed();
        Check(!snapshot.CanReusePreflight, "bounded framework proof enabled cache reuse");
        Throws("strict_capability_unsupported", StrictCapabilitySnapshot.Create(new[] { Capability(name, StrictCapabilityLevel.Observed) }).EnsureStrictExportAllowed);
    }
    foreach (string name in new[] { "MonoMod.Utils.Cil.FakeGenerated", "Unknown.Mod", "Anonymously Hosted DynamicMethods Assembly" })
        Throws("strict_capability_unsupported", StrictCapabilitySnapshot.Create(new[] { new StrictCapability(name, Hash(name), StrictCapabilityLevel.Observed, new[] { marker }) }).EnsureStrictExportAllowed);
});
Run("legacy low-level Emit recipe evidence no longer authorizes the shared holder", () =>
{
    var stale = new StrictCapability("Anonymously Hosted DynamicMethods Assembly", Hash("policy"), StrictCapabilityLevel.Observed,
        new[] { "RetainedRecipeScope.interop-initializer.v2" });
    Throws("strict_capability_unsupported", StrictCapabilitySnapshot.Create(new[] { stale }).EnsureStrictExportAllowed);
    var current = new StrictCapability(stale.ModuleId, Hash("new-policy"), StrictCapabilityLevel.Observed,
        new[] { "RetainedRecipeScope.interop-initializer-final-il.v2" });
    var snapshot = StrictCapabilitySnapshot.Create(new[] { current }); snapshot.EnsureStrictExportAllowed();
    Check(!snapshot.CanReusePreflight, "finite factory proof authorized cache reuse");
});
Run("menu and voice admission require their own retained proof markers", () =>
{
    foreach (var pair in new[]
    {
        ("MenuBgmSelector", "RetainedMenuPlaybackBoundary.menu-bgm.v1"),
        ("AAMenuPrototype", "RetainedMenuPlaybackBoundary.menu-scene.v1"),
        ("SfxFolderMod", "RetainedMenuPlaybackBoundary.sfx-map.v1"),
        ("Rukari.CharacterVoice", "CharacterVoiceSessionAdapter.held-controller.v1")
    })
    {
        var item = new StrictCapability(pair.Item1, Hash(pair.Item1), StrictCapabilityLevel.Observed, new[] { pair.Item2 });
        var valid = StrictCapabilitySnapshot.Create(new[] { item }); valid.EnsureStrictExportAllowed();
        Check(!valid.CanReusePreflight, "retained proof enabled cache reuse");
        Throws("strict_capability_unsupported", StrictCapabilitySnapshot.Create(new[] { Capability(pair.Item1, StrictCapabilityLevel.Observed) }).EnsureStrictExportAllowed);
        Throws("strict_capability_unsupported", StrictCapabilitySnapshot.Create(new[] { new StrictCapability("Unknown.Mod", Hash("Unknown.Mod"), StrictCapabilityLevel.Observed, new[] { pair.Item2 }) }).EnsureStrictExportAllowed);
    }
});
Run("only certified modules allow preflight reuse", () =>
{
    var certified = StrictCapabilitySnapshot.Create(new[] { Capability("a", StrictCapabilityLevel.Certified), Capability("b", StrictCapabilityLevel.Certified) });
    Check(certified.CanReusePreflight, "certified snapshot was not reusable");
    var mixed = StrictCapabilitySnapshot.Create(new[] { Capability("a", StrictCapabilityLevel.Certified), Capability("b", StrictCapabilityLevel.Observed) });
    Check(!mixed.CanReusePreflight, "observed module was allowed to reuse preflight");
});
Run("strict admission allows an empty Mod set", () =>
{
    var snapshot = StrictCapabilitySnapshot.Create(Array.Empty<StrictCapability>());
    snapshot.EnsureStrictExportAllowed();
    Check(snapshot.CanAdmitStrictExport, "an empty Mod set should not block strict export");
});
Run("strict admission allows certified adapters and the observed Azurite handoff", () =>
{
    var snapshot = StrictCapabilitySnapshot.Create(new[]
    {
        Capability("Rukari.MoreEffects", StrictCapabilityLevel.Certified),
        Capability("FlandreScarletPortrait", StrictCapabilityLevel.Certified),
        Capability("Azurite", StrictCapabilityLevel.Observed)
    });
    snapshot.EnsureStrictExportAllowed();
    Check(snapshot.CanAdmitStrictExport, "certified adapters plus the RenderControlV1 observer should be admitted");
    Check(!snapshot.CanReusePreflight, "the observed Azurite handoff must not authorize preflight cache reuse");
});
Run("strict admission rejects an opaque module with evidence", () =>
{
    var snapshot = StrictCapabilitySnapshot.Create(new[]
    {
        new StrictCapability("Unknown.Mod", Hash("unknown"), StrictCapabilityLevel.Opaque,
            new[] { "assembly-not-yet-certified" })
    });
    Check(!snapshot.CanAdmitStrictExport, "opaque modules must not pass strict admission");
    Throws("strict_capability_unsupported", snapshot.EnsureStrictExportAllowed);
});
Run("exact empty Mods need retained observed evidence and never enable cache reuse", () =>
{
    foreach(string name in new[]{"ExampleCore","ExampleDependent","ExampleStandalone"})
    {
        var scoped=new StrictCapability(name,Hash(name),StrictCapabilityLevel.Observed,
            new[]{"StaticInertModEvidence.empty-load.v1","StaticInertScope.retained.v1"});
        var snapshot=StrictCapabilitySnapshot.Create(new[]{scoped});snapshot.EnsureStrictExportAllowed();
        Check(!snapshot.CanReusePreflight,"Empty-Mod scope enabled cache reuse");
        foreach(var evidence in new[]{Array.Empty<string>(),new[]{"StaticInertModEvidence.empty-load.v1"},new[]{"StaticInertScope.retained.v1"}})
            Throws("strict_capability_unsupported",StrictCapabilitySnapshot.Create(new[]{new StrictCapability(name,Hash(name),StrictCapabilityLevel.Observed,evidence)}).EnsureStrictExportAllowed);
    }
});
Run("empty Mod markers never authorize unknown anonymous or CharacterVoice", () =>
{
    foreach(string name in new[]{"Unknown.Mod","Anonymously Hosted DynamicMethods Assembly","Rukari.CharacterVoice"})
        Throws("strict_capability_unsupported",StrictCapabilitySnapshot.Create(new[]{new StrictCapability(name,Hash(name),StrictCapabilityLevel.Observed,
            new[]{"StaticInertModEvidence.empty-load.v1","StaticInertScope.retained.v1"})}).EnsureStrictExportAllowed);
});
Run("scoped translation dependencies and retained startup evidence admit only their exact module kinds", () =>
{
    var pairs = new[]
    {
        ("AAMultilingual", "MultilingualCaptureAdapter.frame-owned.v1"),
        ("Rukari.Lib", "RuntimeModBoundaries.rukari-idle.v1"),
        ("Rukari.Lib.Runtime", "RuntimeModBoundaries.rukari-idle.v1"),
        ("AzureArchive.VideoTools.Core", "RuntimeModBoundaries.effects-dependency.v1"),
        ("AzureArchive.VideoTools.Formats", "RuntimeModBoundaries.effects-dependency.v1"),
        ("AASkipIntro", "AASkipIntroExportBoundary.retained.v1")
    };
    var snapshot = StrictCapabilitySnapshot.Create(pairs.Select(pair => new StrictCapability(pair.Item1, Hash(pair.Item1), StrictCapabilityLevel.Observed, new[] { pair.Item2 })));
    snapshot.EnsureStrictExportAllowed(); Check(!snapshot.CanReusePreflight, "scoped evidence authorized cache reuse");
    foreach (var pair in pairs)
        Throws("strict_capability_unsupported", StrictCapabilitySnapshot.Create(new[] { Capability(pair.Item1, StrictCapabilityLevel.Observed) }).EnsureStrictExportAllowed);
});
Run("scoped dependency evidence never admits title unknown anonymous or voice modules", () =>
{
    foreach (string name in new[] { "TitlePageMod", "Unknown.Mod", "Anonymously Hosted DynamicMethods Assembly", "Rukari.CharacterVoice" })
        foreach (string marker in new[] { "RuntimeModBoundaries.rukari-idle.v1", "MultilingualCaptureAdapter.frame-owned.v1", "AASkipIntroExportBoundary.retained.v1" })
            Throws("strict_capability_unsupported", StrictCapabilitySnapshot.Create(new[] { new StrictCapability(name, Hash(name), StrictCapabilityLevel.Observed, new[] { marker }) }).EnsureStrictExportAllowed);
});
Run("scoped controlled background evidence permits export but never cache reuse", () =>
{
    var module = new StrictCapability("FlandreScarletBackground", Hash("exact-background"), StrictCapabilityLevel.Observed,
        new[] { "ControlledBackgroundVideoAdapter.pts-frames.v1" });
    var snapshot = StrictCapabilitySnapshot.Create(new[] { module });
    snapshot.EnsureStrictExportAllowed();
    Check(snapshot.CanAdmitStrictExport && !snapshot.CanReusePreflight, "scoped background admission changed cache policy");
});
Run("observed background without adapter evidence remains rejected", () =>
{
    var snapshot = StrictCapabilitySnapshot.Create(new[] { Capability("FlandreScarletBackground", StrictCapabilityLevel.Observed) });
    Throws("strict_capability_unsupported", snapshot.EnsureStrictExportAllowed);
});
Run("background evidence does not admit unrelated observed modules", () =>
{
    foreach (string name in new[] { "Unknown.Observed", "Rukari.CharacterVoice", "Rukari.Lib", "AAMultilingual", "TitlePageMod" })
    {
        var module = new StrictCapability(name, Hash(name), StrictCapabilityLevel.Observed,
            new[] { "ControlledBackgroundVideoAdapter.pts-frames.v1" });
        Throws("strict_capability_unsupported", StrictCapabilitySnapshot.Create(new[] { module }).EnsureStrictExportAllowed);
    }
});
Run("strict admission rejects a rejected adapter before capture", () =>
{
    var snapshot = StrictCapabilitySnapshot.Create(new[]
    {
        new StrictCapability("Rukari.MoreEffects", Hash("more-effects"), StrictCapabilityLevel.Rejected,
            new[] { "MoreEffectsCompatibility.partial", "queue shape changed" })
    });
    try { snapshot.EnsureStrictExportAllowed(); throw new Exception("rejected adapter was admitted"); }
    catch (ExportException error)
    {
        Check(error.Code == "strict_capability_unsupported", "wrong strict admission error code");
        Check(error.Message.Contains("Rukari.MoreEffects", StringComparison.Ordinal), "module id missing from admission error");
        Check(error.Message.Contains("queue shape changed", StringComparison.Ordinal), "evidence missing from admission error");
    }
});
Run("strict admission rejects an arbitrary observed module", () =>
{
    var snapshot = StrictCapabilitySnapshot.Create(new[]
    {
        Capability("Unknown.Observed", StrictCapabilityLevel.Observed)
    });
    Check(!snapshot.CanAdmitStrictExport, "only the explicit Azurite observed boundary may be admitted");
    Throws("strict_capability_unsupported", snapshot.EnsureStrictExportAllowed);
});
Run("capability fingerprint is order independent", () =>
{
    var first = StrictCapabilitySnapshot.Create(new[] { Capability("a", StrictCapabilityLevel.Certified), Capability("b", StrictCapabilityLevel.Certified) });
    var second = StrictCapabilitySnapshot.Create(new[] { Capability("b", StrictCapabilityLevel.Certified), Capability("a", StrictCapabilityLevel.Certified) });
    Equal(first.Fingerprint, second.Fingerprint);
});
Run("sandbox trace accepts exact replay and hashes commands", () =>
{
    var trace = new StrictSandboxTrace();
    var first = Command(0, 0, 0, "Play");
    var second = Command(1, 1, 333333, "Stop");
    trace.Append(first); trace.Append(second); string hash = trace.TraceSha256;
    trace.BeginReplay(); trace.Expect(first); trace.Expect(second); trace.CompleteReplay();
    Check(hash.Length == 64, "trace hash was not canonical");
});
Run("sandbox trace requires a zero-based first sequence", () =>
{
    var trace = new StrictSandboxTrace();
    Throws("sandbox_trace_order", () => trace.Append(Command(1, 0, 0, "Play")));
    Equal(0, trace.Commands.Count);
    trace.Append(Command(0, 0, 0, "Play"));
    Equal(1, trace.Commands.Count);
});
Run("sandbox trace sequence follows preparation and replay phases", () =>
{
    var trace = new StrictSandboxTrace();
    Equal(0L, trace.NextSequence);
    var first = Command(trace.NextSequence, 0, 0, "Play");
    trace.Append(first);
    Equal(1L, trace.NextSequence);
    var second = Command(trace.NextSequence, 1, 333333, "Stop");
    trace.Append(second);
    Equal(2L, trace.NextSequence);

    trace.BeginReplay();
    Equal(0L, trace.NextSequence);
    Equal(2, trace.Commands.Count);
    trace.Expect(Command(trace.NextSequence, 0, 0, "Play"));
    Equal(1L, trace.NextSequence);
    trace.Expect(Command(trace.NextSequence, 1, 333333, "Stop"));
    Equal(2L, trace.NextSequence);
    trace.CompleteReplay();

    Throws("invalid_state", () => { _ = trace.NextSequence; });
    Throws("invalid_state", () => trace.Append(first));
    Throws("invalid_state", () => trace.Expect(first));
    Throws("invalid_state", trace.BeginReplay);
    Throws("invalid_state", trace.CompleteReplay);
});
Run("sandbox trace keeps its cursor after a replay mismatch", () =>
{
    var trace = new StrictSandboxTrace();
    var first = Command(0, 0, 0, "Play");
    var second = Command(1, 1, 333333, "Stop");
    trace.Append(first);
    trace.Append(second);
    trace.BeginReplay();
    Throws("sandbox_replay_divergence", () => trace.Expect(Command(0, 0, 0, "Stop")));
    Equal(0L, trace.NextSequence);
    trace.Expect(first);
    Throws("sandbox_replay_divergence", trace.CompleteReplay);
    Equal(1L, trace.NextSequence);
    Throws("sandbox_replay_divergence", () => trace.Expect(Command(1, 1, 333333, "Play")));
    Equal(1L, trace.NextSequence);
    trace.Expect(second);
    Throws("sandbox_replay_divergence", () => trace.Expect(Command(2, 2, 666666, "Play")));
    Equal(2L, trace.NextSequence);
    trace.CompleteReplay();
});
Run("sandbox trace requires contiguous append sequences", () =>
{
    var trace = new StrictSandboxTrace();
    trace.Append(Command(0, 0, 0, "Play"));
    Throws("sandbox_trace_order", () => trace.Append(Command(2, 1, 333333, "Stop")));
    Throws("sandbox_trace_order", () => trace.Append(Command(0, 1, 333333, "Stop")));
    Equal(1, trace.Commands.Count);
    Equal(1L, trace.NextSequence);
    trace.Append(Command(1, 1, 333333, "Stop"));
    Equal(2L, trace.NextSequence);
});
Run("sandbox trace command collection cannot bypass append validation", () =>
{
    var trace = new StrictSandboxTrace();
    var commands = trace.Commands;
    var first = Command(0, 0, 0, "Play");
    trace.Append(first);
    if (commands is ICollection<StrictSandboxCommand> collection)
    {
        try { collection.Clear(); throw new Exception("command collection allowed mutation"); }
        catch (NotSupportedException) { }
    }
    Equal(1, commands.Count);
    Equal(first, commands[0]);
    trace.Append(Command(1, 1, 333333, "Stop"));
    Equal(2, commands.Count);
});
Run("sandbox trace rejects order and replay divergence", () =>
{
    var trace = new StrictSandboxTrace();
    trace.Append(Command(0, 1, 10, "Play"));
    Throws("sandbox_trace_order", () => trace.Append(Command(2, 0, 9, "Stop")));
    Throws("sandbox_trace_order", () => trace.Append(Command(1, 0, 10, "Stop")));
    Throws("sandbox_trace_order", () => trace.Append(Command(1, 1, 9, "Stop")));
    Equal(1L, trace.NextSequence);
    trace.BeginReplay();
    Throws("sandbox_replay_divergence", () => trace.Expect(Command(0, 1, 10, "Stop")));
});
Run("sandbox trace rejects missing replay commands", () =>
{
    var trace = new StrictSandboxTrace(); trace.Append(Command(0, 0, 0, "Play")); trace.BeginReplay();
    Throws("sandbox_replay_divergence", trace.CompleteReplay);
});
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

StrictCapability Capability(string id, StrictCapabilityLevel level) => new(id, Hash(id), level, new[] { "fixture" });
StrictSandboxCommand Command(long sequence, long frame, long ticks, string operation) => new(sequence, frame, ticks, Hash("module"), "Audio", operation, "source-1", Hash(operation), null, null);
string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
void Run(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); } }
void Throws(string code, Action action) { try { action(); } catch (ExportException error) { if (error.Code == code) return; throw; } throw new Exception("Expected " + code + "."); }
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
