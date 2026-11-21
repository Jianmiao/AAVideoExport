using AAVideoExport.Plugin;

var cases = new (string Name, Action Run)[]
{
    ("pending retained Host cleanup prevents plugin-wide unpatch and permits retry", () =>
    {
        var plugin = new Plugin(); plugin.Load(); ExportHost.PendingCleanup = true;
        try
        {
            Check(!plugin.Unload() && !Events.Items.Contains("harmony.unpatch") && !Events.Items.Contains("host.destroy"),
                "pending retained voice/menu cleanup allowed broad unpatch or destruction");
        }
        finally { ExportHost.PendingCleanup = false; plugin.Unload(); }
    }),
    ("translation and runtime cleanup failure each block broad unpatch until retry", () =>
    {
        foreach (bool translation in new[] { true, false })
        {
            Events.Reset(); var plugin = new Plugin(); plugin.Load();
            MultilingualCaptureAdapter.ShutdownSucceeds = !translation; RuntimeModBoundaries.ShutdownSucceeds = translation;
            Check(!plugin.Unload() && !Events.Items.Contains("harmony.unpatch") && !Events.Items.Contains("host.destroy"),
                "scoped cleanup failure allowed broad unpatch/destruction");
            MultilingualCaptureAdapter.ShutdownSucceeds = RuntimeModBoundaries.ShutdownSucceeds = true;
            Check(plugin.Unload(), "scoped cleanup could not retry");
        }
    }),
    ("failed background shutdown blocks broad unpatch and remains retryable", () =>
    {
        var plugin = new Plugin(); plugin.Load();
        ControlledBackgroundVideoAdapter.ShutdownSucceeds = false;
        Check(!plugin.Unload(), "unload ignored pending background cleanup");
        Check(!Events.Items.Contains("harmony.unpatch") && !Events.Items.Contains("host.destroy"),
            "failed background cleanup was bypassed by broad unpatch or destruction");
        ControlledBackgroundVideoAdapter.ShutdownSucceeds = true;
        Check(plugin.Unload() && Events.Items.Count(e => e == "background.shutdown") == 2,
            "background cleanup could not retry before unload");
    }),
    ("load rollback retains pending background cleanup before broad unpatch", () =>
    {
        var plugin = new Plugin(); Events.FailAddComponent = true;
        ControlledBackgroundVideoAdapter.ShutdownSucceeds = false;
        bool failed = false;
        try { plugin.Load(); } catch (InvalidOperationException) { failed = true; }
        Check(failed && Events.Items.Contains("background.shutdown") && !Events.Items.Contains("harmony.unpatch"),
            "load rollback bypassed pending background cleanup");
        ControlledBackgroundVideoAdapter.ShutdownSucceeds = true;
        Check(plugin.Unload(), "pending background load rollback could not retry");
    }),
    ("load initializes and discovers voice adapter before creating host", () =>
    {
        var plugin = new Plugin(); plugin.Load();
        try
        {
            Check(Events.Items.IndexOf("voice.initialize") >= 0
                && Events.Items.IndexOf("voice.install") > Events.Items.IndexOf("voice.initialize")
                && Events.Items.IndexOf("voice.install") < Events.Items.IndexOf("host.create"),
                "voice adapter was not initialized/discovered before host activation");
        }
        finally { plugin.Unload(); }
    }),
    ("voice failure callback targets the current host synchronously", () =>
    {
        var plugin = new Plugin(); plugin.Load();
        try
        {
            var initial = ExportHost.Current!;
            var current = new ExportHost();
            var error = new InvalidOperationException("fixture voice fault");
            CharacterVoiceDelayClock.Fault?.Invoke(error);
            Check(current.Faults == 1 && ReferenceEquals(current.LastFault, error) && initial.Faults == 0,
                "voice fault callback was missing, deferred or bound to the stale host");
        }
        finally { plugin.Unload(); }
    }),
    ("failed voice shutdown blocks unload and remains retryable before owner unpatch", () =>
    {
        var plugin = new Plugin(); plugin.Load();
        CharacterVoiceDelayClock.ShutdownSucceeds = false;
        Check(!plugin.Unload(), "unload succeeded despite incomplete voice restoration/unpatch");
        Check(!Events.Items.Contains("harmony.unpatch") && !Events.Items.Contains("host.destroy"),
            "failed voice cleanup was bypassed by broad unpatch or host destruction");
        CharacterVoiceDelayClock.ShutdownSucceeds = true;
        Check(plugin.Unload(), "unload could not retry after voice cleanup recovered");
        Check(Events.Items.Count(e => e == "voice.shutdown") == 2
            && Events.Items.Count(e => e == "harmony.unpatch") == 1
            && Events.Items.IndexOf("host.shutdown") < Events.Items.IndexOf("voice.shutdown")
            && Events.Items.LastIndexOf("voice.shutdown") < Events.Items.IndexOf("harmony.unpatch"),
            "unload cleanup order did not retain voice ownership until restoration");
    }),
    ("load rollback shuts voice adapter down before broad owner unpatch", () =>
    {
        var plugin = new Plugin(); Events.FailAddComponent = true;
        bool failed = false;
        try { plugin.Load(); } catch (InvalidOperationException) { failed = true; }
        Check(failed && Events.Items.Contains("voice.shutdown")
            && Events.Items.IndexOf("voice.shutdown") < Events.Items.IndexOf("harmony.unpatch"),
            "load rollback omitted voice cleanup or unpatched before it");
    }),
    ("failed load rollback preserves voice cleanup for an unload retry", () =>
    {
        var plugin = new Plugin(); Events.FailAddComponent = true;
        CharacterVoiceDelayClock.ShutdownSucceeds = false;
        bool failed = false;
        try { plugin.Load(); } catch (InvalidOperationException) { failed = true; }
        Check(failed && Events.Items.Contains("voice.shutdown") && !Events.Items.Contains("harmony.unpatch"),
            "load failure bypassed an incomplete voice cleanup");
        CharacterVoiceDelayClock.ShutdownSucceeds = true;
        Check(plugin.Unload(), "load failure cleanup could not be retried");
    })
};

int failed = 0;
string root = Path.Combine(Path.GetTempPath(), "aave-plugin-lifecycle-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "GameAssembly.dll"), "unexecuted identity fixture");
BepInEx.Paths.GameRootPath = root; BepInEx.Paths.PluginPath = root;
try
{
    foreach (var test in cases)
    {
        Events.Reset(); CharacterVoiceDelayClock.Reset(); ControlledBackgroundVideoAdapter.ShutdownSucceeds = true; ExportHost.PendingCleanup = false;
        MultilingualCaptureAdapter.ShutdownSucceeds = RuntimeModBoundaries.ShutdownSucceeds = true; ExportHost.Current = null;
        try { test.Run(); Console.WriteLine("PASS " + test.Name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error.Message); }
    }
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"{cases.Length - failed}/{cases.Length} passed (production Plugin lifecycle; fake BepInEx/Unity and adapter boundaries)");
return failed == 0 ? 0 : 1;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
