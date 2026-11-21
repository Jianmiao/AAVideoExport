using AAVideoExport.Plugin;
using AAVideoExport.Core;
using System.Security.Cryptography;
using System.Text.Json;

// Resolve the actual installed managed references only when the lifecycle
// fixture requests them. This does not initialize or launch AA/Unity.
string references = args.Length > 0 ? Path.GetFullPath(args[0]) : @"F:\AzureArchive_101";
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string relative in new[] { "BepInEx/core", "BepInEx/interop" })
    {
        string path = Path.Combine(references, relative, name.Name + ".dll");
        if (File.Exists(path)) return System.Reflection.Assembly.LoadFrom(path);
    }
    return null;
};

string root = Path.Combine(AppContext.BaseDirectory, "artifacts", "controlled-background-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0, failed = 0;
var results = new List<object>();
void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
void Test(string name, Action run)
{
    try { run(); passed++; results.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; results.Add(new { Name = name, Passed = false, Error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
string FileAt(string relative)
{
    string path = Path.GetFullPath(Path.Combine(root, relative));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, new byte[] { 1, 2, 3 }); return path;
}
Test("sidecar source wins over project imports and does not mutate assets", () =>
{
    string image = FileAt(@"sidecar/data/saves/story/bgs/demo.png");
    string sidecar = FileAt(@"sidecar/data/saves/story/bgs/demo.mp4");
    FileAt(@"sidecar/data/saves/story/bgs/.imports/01/demo.webm");
    string backup = Path.Combine(root, "sidecar/backups");
    string[] before = Directory.GetFiles(Path.Combine(root, "sidecar"), "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
    string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sidecar)));
    Check(ControlledBackgroundSourceResolver.Resolve(image, "resource-id", backup) == sidecar, "Sidecar did not retain first priority.");
    Check(Directory.GetFiles(Path.Combine(root, "sidecar"), "*", SearchOption.AllDirectories).OrderBy(x => x).SequenceEqual(before), "Resolution wrote asset files.");
    Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sidecar))) == hash, "Resolution modified source bytes.");
});
Test("project imports and paired project roots retain precedence", () =>
{
    string image = FileAt(@"imports/data/saves/story/bgs/demo.png");
    string paired = FileAt(@"imports/data/projects/story/bgs/.imports/01/demo.mov");
    string backup = FileAt(@"imports/backups/story/demo.mp4");
    Check(ControlledBackgroundSourceResolver.Resolve(image, "demo", Path.GetDirectoryName(Path.GetDirectoryName(backup)!)!) == paired, "Paired .imports source was not found before backups.");
});
Test("backup source uses current project before unrelated projects", () =>
{
    string image = FileAt(@"backup/data/saves/story/bgs/demo.png");
    string current = FileAt(@"backup/videos/story/demo.mkv");
    FileAt(@"backup/videos/other/demo.mp4");
    Check(ControlledBackgroundSourceResolver.Resolve(image, "demo", Path.Combine(root, "backup/videos")) == current, "Current project backup not preferred.");
});
Test("ambiguous fallback refuses to substitute an unrelated same-name resource", () =>
{
    string image = FileAt(@"ambiguous/data/saves/story/bgs/demo.png");
    FileAt(@"ambiguous/videos/first/demo.mp4"); FileAt(@"ambiguous/videos/second/demo.mp4");
    try { ControlledBackgroundSourceResolver.Resolve(image, "demo", Path.Combine(root, "ambiguous/videos")); }
    catch (ExportException error) { Check(error.Code == "background_source_ambiguous", "Wrong ambiguity error."); return; }
    throw new Exception("Ambiguous fallback was silently selected.");
});
Test("existing original basename cache is honored read-only", () =>
{
    string image = FileAt(@"cached/data/saves/story/bgs/demo.png");
    FileAt(@"cached/data/saves/story/bgs/demo.mp4");
    string cached = FileAt(@"cached/previous/demo.webm");
    var cache = new Dictionary<string, string> { ["demo"] = cached };
    Check(ControlledBackgroundSourceResolver.Resolve(image, "demo", Path.Combine(root, "cached/missing"), cache) == cached, "Original live mapping was not preserved.");
    Check(cache.Count == 1 && cache["demo"] == cached, "Resolver modified original mapping.");
});
Test("missing video keeps a native static background without creating lookup directories", () =>
{
    string image = FileAt(@"static/data/saves/story/bgs/quiet.png");
    string backup = Path.Combine(root, "static/nonexistent");
    Check(ControlledBackgroundSourceResolver.Resolve(image, "quiet", backup) == null, "Static asset was replaced with a video.");
    Check(!Directory.Exists(backup), "Read-only resolver created VideoBackup.");
});
Test("RGBA rows reverse for Unity while every source byte remains unchanged", () =>
{
    byte[] pixels = { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255 };
    var original = pixels.ToArray(); var flipped = new byte[pixels.Length];
    ControlledBackgroundSourceResolver.CopyTopDownToBottomUp(pixels, flipped, 2, 2);
    Check(flipped.SequenceEqual(new byte[] { 7, 8, 9, 255, 10, 11, 12, 255, 1, 2, 3, 255, 4, 5, 6, 255 }), "Texture rows were inverted or altered.");
    Check(pixels.SequenceEqual(original), "Source frame bytes were mutated.");
});
Test("invalid configured directory retains original default backup fallback without writes", () =>
{
    string configuredFile = FileAt(@"invalid-root/configured-file");
    string fallback = Path.Combine(root, "invalid-root/default");
    string video = FileAt(@"invalid-root/default/story/demo.mp4");
    string image = FileAt(@"invalid-root/data/saves/story/bgs/demo.png");
    string directory = ControlledBackgroundSourceResolver.ResolveVideoDirectory(configuredFile, root, fallback);
    Check(directory == fallback, "Original default fallback was lost for a configured regular file.");
    Check(ControlledBackgroundSourceResolver.Resolve(image, "demo", directory) == video, "Default video silently became a static background.");
    string belowFile = ControlledBackgroundSourceResolver.ResolveVideoDirectory(Path.Combine(configuredFile, "child"), root, fallback);
    Check(belowFile == fallback, "A file in the configured ancestor path lost the default fallback.");
    Check(ControlledBackgroundSourceResolver.ResolveVideoDirectory("\0invalid", root, fallback) == fallback, "Invalid path did not retain the default fallback.");
});

BackgroundPatchLifecycleTests.Run(Test);

File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { Passed = passed, Failed = failed, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Artifacts: " + root);
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;
