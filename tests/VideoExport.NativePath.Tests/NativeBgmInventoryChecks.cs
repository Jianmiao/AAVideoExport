using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Development-only read-only inventory. No hooks, preload, playback, resource
// override, scenario mutation, cache mutation, or native completion invocation.
public sealed class NativeBgmInventoryChecks : MonoBehaviour
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string ExpectedHash = "CDBF1FF37391336B67C9F5FAF24A83EFCF73BEE291BECEE9BCDDF303F392A7CA";
    private const int ExpectedRows = 198;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly List<object> rows = new(), lookups = new(), mappings = new(), errors = new();
    private readonly SortedDictionary<long, List<int>> authored = new();
    private readonly SortedDictionary<long, int> effective = new();
    private readonly HashSet<long> nullIds = new();
    private string story = "", beforeHash = "", afterHash = "", nativePath = "", cacheBefore = "", cacheAfter = "";
    private ScenarioResourceManager? resources;
    private GenericScenarioExcelTable? table;
    private object? menuSelection, context;
    private bool finished, collected;
    private double nextReadinessLog;

    public NativeBgmInventoryChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (finished) return;
        try
        {
            if (wall.Elapsed.TotalSeconds > 130) throw new TimeoutException("Native BGM inventory exceeded 130 seconds waiting for actual native readiness.");
            resources = ScenarioResourceManager.Instance;
            var host = typeof(ExportHost).GetField("Current", Members)?.GetValue(null);
            bool ready = SceneManager.GetActiveScene().name == "CatalogScene" && UserSettings.Instance?.data != null
                && resources != null && resources.AreDbsLoaded && resources.ExcelDb != null
                && resources.ScenarioCharacterNameQuery != null && !resources.Preloading
                && host != null && host.GetType().GetProperty("Busy", Members)?.GetValue(host) is false;
            if (!ready)
            {
                if (wall.Elapsed.TotalSeconds >= nextReadinessLog)
                {
                    nextReadinessLog = wall.Elapsed.TotalSeconds + 5;
                    Plugin.Report("PATH-CHECK BGM-INVENTORY READINESS " + JsonSerializer.Serialize(new {
                        Scene = SceneManager.GetActiveScene().name, Databases = resources?.AreDbsLoaded,
                        Preloading = resources?.Preloading, Host = host != null, Seconds = wall.Elapsed.TotalSeconds }));
                }
                return;
            }
            Collect();
            Finish(null);
        }
        catch (Exception error) { Finish(Unwrap(error)); }
    }

    [HideFromIl2Cpp]
    private void Collect()
    {
        story = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_STORY") ?? "";
        Require(Path.IsPathFullyQualified(story) && File.Exists(story), "An explicit existing absolute story path is required.");
        story = Path.GetFullPath(story);
        Require(Path.GetFileName(story) == "百鬼夜行-间章.aas", "This development inventory is scoped to the specified regression story.");
        Require(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_FIXTURE_WORKSPACE")),
            "This read-only inventory must use the existing native workspace without fixture-workspace replacement.");
        nativePath = ScenarioResourceManager.GetPersistentFilePath("saves", Path.GetFileNameWithoutExtension(story), ".aas");
        Require(string.Equals(story, Path.GetFullPath(nativePath), StringComparison.OrdinalIgnoreCase),
            "The selected story does not match AA's current native workspace resolution.");
        beforeHash = HashFile(story);
        Require(beforeHash == ExpectedHash, "The regression story bytes differ from the explicitly selected SHA256.");
        var provider = typeof(ExportHost).Assembly;
        var core = typeof(ExportOptions).Assembly;
        string providerHash = HashFile(provider.Location), coreHash = HashFile(core.Location);
        Require(provider.GetName().Version == new Version(0, 2, 12, 0)
            && providerHash == "820C4446DDF24043CC900F588A4CFA1404CEF51C283850459C1494D1C7D4EE05"
            && coreHash == "F728E74DA722B803EF5FF4367E4D7A2CA9F3E75038292EB702B3B464C72BCFCC"
            && NativeAuditOutputContract.IsSinglePass(core) && !NativeAuditOutputContract.IsDevelopmentAudit(core),
            "This inventory requires the unchanged frozen production Release 0.2.12 provider and Core.");
        context = new {
            Scene = SceneManager.GetActiveScene().name, NativeResourceManager = resources!.Pointer.ToString(),
            DatabasesLoaded = resources.AreDbsLoaded, resources.Preloading,
            GlobalOverridesLoaded = resources.GlobalOverrideData != null, LocalOverridesLoaded = resources.LocalOverrideData != null,
            GlobalBasePath = resources.GlobalOverrideData?.basePath, LocalBasePath = resources.LocalOverrideData?.basePath,
            PersistentProjectPath = PersistentData.projectPath,
            Provider = provider.Location, ProviderVersion = provider.GetName().Version?.ToString(),
            ProviderSha256 = providerHash, CoreSha256 = coreHash, SinglePass = true, DevelopmentAudit = false
        };
        cacheBefore = CacheSnapshot();
        table = resources.LoadGenericScenario(Path.GetFileNameWithoutExtension(story));
        Require(table != null && table.ByteBuffer != null && table.DataListLength == ExpectedRows,
            "AA's original scenario loader did not return the expected concrete 198-row table.");
        for (int index = 0; index < table!.DataListLength; index++)
        {
            try
            {
                var row = table.DataList(index)?.Cast<GenericScenarioExcel>()
                    ?? throw new InvalidOperationException("The native row is null.");
                string concrete = row.GetIl2CppType().FullName;
                Require(concrete == "GenericScenarioExcel", "The native row is not a concrete GenericScenarioExcel.");
                long id = row.BGMId;
                rows.Add(new { RowIndex = index, BgmId = id, NativeType = concrete });
                if (!authored.TryGetValue(id, out var indices)) authored.Add(id, indices = new List<int>());
                indices.Add(index);
            }
            catch (Exception error) { errors.Add(new { Scope = "native-row", RowIndex = index, Exception = Describe(error) }); }
        }
        ReadEffectiveMapping();
        var requested = authored.Keys.Where(id => id > 0).Concat(effective.Keys.Where(id => id > 0)).Distinct().OrderBy(id => id);
        foreach (long id in requested)
        {
            object? infoRecord = null, errorRecord = null;
            string status;
            try
            {
                // Call the actual installed native resolver, with no test hook.
                // This does not invoke GetBgmClipAsync or acquire any audio asset.
                var info = resources.GetBGM(id);
                status = info == null ? "null" : "resolved";
                if (info == null) nullIds.Add(id);
                else infoRecord = new { info.id, info.path, info.isOverride, info.name, info.volume,
                    info.offset, info.loopInTime, info.loopOutTime, info.transitionTime, Pointer = info.Pointer.ToString() };
            }
            catch (Exception error)
            {
                status = "exception"; errorRecord = Describe(error);
                errors.Add(new { Scope = "native-GetBGM", Id = id, Exception = errorRecord });
            }
            var record = new { RequestedId = id, AuthoredCount = authored.GetValueOrDefault(id)?.Count ?? 0,
                RowIndices = authored.GetValueOrDefault(id)?.ToArray() ?? Array.Empty<int>(),
                EffectiveCount = effective.GetValueOrDefault(id), Status = status, NativeInfo = infoRecord, Exception = errorRecord };
            lookups.Add(record); Plugin.Report("PATH-CHECK BGM-INVENTORY ID " + JsonSerializer.Serialize(record));
        }
        cacheAfter = CacheSnapshot();
        afterHash = HashFile(story);
        Require(afterHash == beforeHash, "The selected story changed during read-only inventory.");
        Require(cacheAfter == cacheBefore, "Native custom BGM cache membership or clip identity changed during inventory.");
        collected = true;
    }

    [HideFromIl2Cpp]
    private void ReadEffectiveMapping()
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => value.GetName().Name == "MenuBgmSelector");
            if (assembly == null) menuSelection = new { Loaded = false };
            else
            {
                var store = assembly.GetType("AzureArchive.MenuBgmSelector.BgmStore", true)!;
                var current = store.GetProperty("Current", Members)?.GetValue(null);
                var setting = assembly.GetType("AzureArchive.MenuBgmSelector.GameLobbySetting", true)!;
                menuSelection = new { Loaded = true, Assembly = assembly.Location,
                    Kind = current == null ? null : Read(current, "Kind")?.ToString(),
                    Id = current == null ? null : Read(current, "Id")?.ToString(),
                    LobbyBgmEnabled = setting.GetMethod("IsLobbyBgmEnabled", Members)?.Invoke(null, null) };
            }
        }
        catch (Exception error) { errors.Add(new { Scope = "menu-selection-read", Exception = Describe(error) }); }
        var adapter = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeBgmSessionAdapter", true)!;
        try
        {
            var method = adapter.GetMethod("CollectOccurrences", Members) ?? throw new MissingMethodException(adapter.FullName, "CollectOccurrences");
            foreach (DictionaryEntry pair in (IDictionary)method.Invoke(null, new object[] { table! })!)
                effective.Add(Convert.ToInt64(pair.Key), Convert.ToInt32(pair.Value));
        }
        catch (Exception error) { errors.Add(new { Scope = "production-CollectOccurrences-read", Exception = Describe(error) }); }
        foreach (long id in authored.Keys.Where(value => value > 0))
        {
            try
            {
                var method = adapter.GetMethod("EffectiveMenuBgmId", Members) ?? throw new MissingMethodException(adapter.FullName, "EffectiveMenuBgmId");
                long mapped = Convert.ToInt64(method.Invoke(null, new object[] { id }));
                mappings.Add(new { AuthoredId = id, EffectiveId = mapped, Count = authored[id].Count, RowIndices = authored[id].ToArray() });
            }
            catch (Exception error) { errors.Add(new { Scope = "production-effective-id-read", Id = id, Exception = Describe(error) }); }
        }
    }

    [HideFromIl2Cpp]
    private string CacheSnapshot()
    {
        var entries = new SortedDictionary<long, string>();
        foreach (var pair in resources!._loadedCustomBgms) entries.Add(pair.Key, pair.Value?.Pointer.ToString() ?? "null");
        return JsonSerializer.Serialize(entries);
    }

    [HideFromIl2Cpp]
    private void Finish(Exception? failure)
    {
        if (finished) return;
        finished = true;
        if (failure != null) errors.Add(new { Scope = "inventory", Exception = Describe(failure) });
        if (story.Length != 0 && File.Exists(story))
        {
            try { afterHash = HashFile(story); }
            catch (Exception error) { errors.Add(new { Scope = "final-source-hash", Exception = Describe(error) }); }
        }
        bool unchanged = beforeHash.Length != 0 && beforeHash == afterHash;
        var evidence = new { Schema = 1, Scope = "Development-only read-only actual native BGM inventory; no playback or asset loads",
            InventoryCompleted = collected, Passed = collected && errors.Count == 0 && unchanged,
            Story = story, NativeResolvedStory = nativePath, ExpectedStorySha256 = ExpectedHash,
            SourceSha256Before = beforeHash, SourceSha256After = afterHash, SourceUnchanged = unchanged,
            ExpectedRows, NativeRowCount = table?.DataListLength, ReadRowCount = rows.Count,
            NonpositiveRowCount = authored.Where(pair => pair.Key <= 0).Sum(pair => pair.Value.Count),
            PositiveRowCount = authored.Where(pair => pair.Key > 0).Sum(pair => pair.Value.Count),
            Context = context, MenuBgmSelector = menuSelection,
            AuthoredIds = authored.Select(pair => new { Id = pair.Key, Count = pair.Value.Count, RowIndices = pair.Value.ToArray() }).ToArray(),
            ProductionEffectiveOccurrences = effective.Select(pair => new { Id = pair.Key, Count = pair.Value }).ToArray(),
            EffectiveMappings = mappings, NativeNullInfoIds = nullIds.OrderBy(id => id).ToArray(), NativeLookups = lookups,
            Rows = rows, Errors = errors, NativeCustomCacheBefore = cacheBefore, NativeCustomCacheAfter = cacheAfter,
            Seconds = wall.Elapsed.TotalSeconds };
        File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "native-bgm-inventory.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Report("PATH-CHECK BGM-INVENTORY SUMMARY " + JsonSerializer.Serialize(new {
            evidence.InventoryCompleted, evidence.Passed, evidence.NativeRowCount, DistinctAuthoredIds = authored.Count,
            PositiveNativeLookups = lookups.Count, evidence.NativeNullInfoIds, ErrorCount = errors.Count, evidence.SourceUnchanged }));
        Plugin.Report($"PATH-CHECK RESULT {(evidence.Passed ? 1 : 0)} passed; {(evidence.Passed ? 0 : 1)} failed");
    }

    [HideFromIl2Cpp]
    private static object? Read(object instance, string name) => instance.GetType().GetProperty(name, Members)?.GetValue(instance)
        ?? instance.GetType().GetField(name, Members)?.GetValue(instance);
    [HideFromIl2Cpp]
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    [HideFromIl2Cpp]
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [HideFromIl2Cpp]
    private static Exception Unwrap(Exception error) { while (error is TargetInvocationException invocation && invocation.InnerException is { } inner) error = inner; return error; }
    [HideFromIl2Cpp]
    private static object Describe(Exception error) { error = Unwrap(error); return new { Type = error.GetType().FullName, error.Message, error.StackTrace }; }
}
