using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using AAVideoExport.Integration;
using Object = UnityEngine.Object;

// Drive production Prepare, not a copy of its path condition. Native boundary
// follows the inspected AA loader: empty workspace => persistentDataPath.
var tests = new (string Name, Action<Fixture> Run)[]
{
    ("default workspace uses native persistent-data fallback instead of process directory", f =>
    {
        UserSettings.Instance!.WorkspacePath = "";
        f.ExpectLoaded();
    }),
    ("custom workspace accepts native forward slashes and Chinese filenames", f =>
    {
        UserSettings.Instance!.WorkspacePath = f.Root + Path.DirectorySeparatorChar;
        f.ExpectLoaded();
    }),
    ("another workspace with identical story basename remains rejected", f =>
    {
        UserSettings.Instance!.WorkspacePath = f.Root + "-other";
        f.ExpectRejected("story_location_unsupported");
    }),
    ("deleted selected story never reads a different same-name story", f =>
    {
        File.Delete(f.Story);
        f.ExpectRejected("story_missing");
    }),
    ("workspace switch after selection cannot silently substitute another story", f =>
    {
        UserSettings.Instance!.WorkspacePath = f.Root;
        Set(f.Host, "_selectedStoryKey", f.Story);
        UserSettings.Instance.WorkspacePath = f.Root + "-switched";
        f.ExpectRejected("story_location_unsupported");
    })
};
int failures = 0;
foreach (var test in tests)
{
    using var fixture = new Fixture();
    try { test.Run(fixture); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + error.Message); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed (production story preparation with fake native path service)");
return failures == 0 ? 0 : 1;

static void Set(ExportHost host, string name, object? value) => typeof(ExportHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);

sealed class Fixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aave-story-path-" + Guid.NewGuid().ToString("N"));
    internal string Story { get; }
    internal ExportHost Host { get; }
    private readonly ExportOptions _options;

    internal Fixture()
    {
        Object.ResetRegistry(); UnityEngine.Debug.Messages.Clear();
        Story = Path.Combine(Root, "data", "saves", "剧情 第一章.版本2.aas");
        Directory.CreateDirectory(Path.GetDirectoryName(Story)!);
        File.WriteAllText(Story, "test fixture");
        UserSettings.Instance = new() { WorkspacePath = Root };
        ScenarioResourceManager.PersistentDataPath = Root;
        ScenarioResourceManager.Instance = new();
        Host = new ExportHost(new IntPtr(1)); Host.Awake();
        Set("_capabilities", new FfmpegCapabilities());
        Set("_capabilitiesMode", "hardware"); Set("_probeMode", "hardware");
        var selected = new CatalogFileInfo { block = new() { path = Story.Replace('\\', '/') }, controlPanel = new() };
        Set("_selectedStory", selected); Set("_selectedStoryKey", Story);
        _options = new ExportOptions { Title = "fixture", OutputDirectory = Root };
        Field<NativeExportPanel>("_panel").Options = _options;
    }

    internal void ExpectLoaded()
    {
        Prepare();
        var pending = Field<Task<ExportSession>?>("_prepare");
        if (pending == null) throw new Exception("selected native story rejected: " + Field<string>("_status"));
        pending.GetAwaiter().GetResult().Dispose(); Set("_prepare", null);
        if (ScenarioResourceManager.Instance!.LoadedNames.Single() != "剧情 第一章.版本2")
            throw new Exception("loader received a different source key");
        if (Field<string>("_selectedStoryKey") != Story || Field<GenericScenarioExcelTable?>("_frozenSave") == null)
            throw new Exception("fresh story source identity was lost");
    }

    internal void ExpectRejected(string code)
    {
        Prepare();
        if (!UnityEngine.Debug.Messages.Any(m => m.Contains("failure=" + code)))
            throw new Exception("expected " + code + ", got " + Field<string>("_status"));
        if (ScenarioResourceManager.Instance!.LoadedNames.Count != 0 || Field<Task<ExportSession>?>("_prepare") != null || RenderControlV1.IsRenderingOwned)
            throw new Exception("invalid selection reached loader or render acquisition");
    }

    private void Prepare() => typeof(ExportHost).GetMethod("Prepare", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Host, new object[] { _options });
    private void Set(string name, object? value) => typeof(ExportHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Host, value);
    private T Field<T>(string name) => (T)typeof(ExportHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host)!;
    public void Dispose()
    {
        Host.Shutdown();
        File.Delete(Story);
        Directory.Delete(Path.GetDirectoryName(Story)!);
        Directory.Delete(Path.Combine(Root, "data")); Directory.Delete(Root);
        UserSettings.Instance = null; ScenarioResourceManager.Instance = null;
    }
}
