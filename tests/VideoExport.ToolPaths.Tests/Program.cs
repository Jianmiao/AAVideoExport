using AAVideoExport.Plugin;

var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "蓝铜矿 编码器", "AAVideoExport", "0.2.0"));
var bundledFfmpeg = Path.Combine(root, "tools", "ffmpeg.exe");
var bundledFfprobe = Path.Combine(root, "tools", "ffprobe.exe");
var external = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "用户 编码器", "ffmpeg-custom.exe"));
var tests = new (string Name, Action Run)[]
{
    ("bare defaults select bundled files with Chinese names and spaces", () =>
    {
        Equal(bundledFfmpeg, Resolve("ffmpeg.exe", "ffmpeg.exe", path => path == bundledFfmpeg));
        Equal(bundledFfprobe, Resolve("ffprobe.exe", "ffprobe.exe", path => path == bundledFfprobe));
    }),
    ("missing bundled tools retain legacy PATH lookup", () =>
    {
        Equal("ffmpeg.exe", Resolve("ffmpeg.exe", "ffmpeg.exe", _ => false));
        Equal("ffprobe.exe", Resolve("ffprobe.exe", "ffprobe.exe", _ => false));
    }),
    ("null empty whitespace and case-insensitive defaults use the same bundle", () =>
    {
        foreach (var setting in new string?[] { null, "", "   ", "FFMPEG.EXE", " ffmpeg.exe " })
            Equal(bundledFfmpeg, Resolve(setting, "ffmpeg.exe", _ => true));
    }),
    ("explicit absolute paths win without fallback or existence probes", () =>
        Equal(external, Resolve(external, "ffmpeg.exe", _ => throw new Exception("Explicit paths must not probe bundle")))),
    ("custom relative paths resolve against installed mod directory", () =>
    {
        Equal(Path.Combine(root, "custom tools", "ffmpeg.exe"),
            Resolve(Path.Combine("custom tools", "ffmpeg.exe"), "ffmpeg.exe", _ => throw new Exception("No bundle probe")));
        Equal(Path.GetFullPath(Path.Combine(root, "..", "shared tools", "ffprobe.exe")),
            Resolve(Path.Combine("..", "shared tools", "ffprobe.exe"), "ffprobe.exe", _ => false));
    }),
    ("explicit relative default filename does not become PATH lookup", () =>
        Equal(Path.Combine(root, "ffmpeg.exe"), Resolve("." + Path.DirectorySeparatorChar + "ffmpeg.exe", "ffmpeg.exe", _ => true))),
    ("custom bare tool names are mod-relative", () =>
        Equal(Path.Combine(root, "custom-ffmpeg.exe"), Resolve("custom-ffmpeg.exe", "ffmpeg.exe", _ => false))),
    ("relative mod directories are rejected rather than depending on cwd", () =>
        Throws(() => EncoderToolPaths.Resolve("", "ffmpeg.exe", "relative-mod", _ => true))),
    ("tool filenames cannot escape the tools directory", () =>
    {
        foreach (var fileName in new[] { "../ffmpeg.exe", "..\\ffmpeg.exe", "C:ffmpeg.exe", "" })
            Throws(() => EncoderToolPaths.Resolve("", fileName, root, _ => true));
    }),
    ("Windows drive-relative and root-relative configurations are rejected", () =>
    {
        if (!OperatingSystem.IsWindows()) return;
        Throws(() => Resolve("C:ffmpeg.exe", "ffmpeg.exe", _ => true));
        Throws(() => Resolve("\\tools\\ffmpeg.exe", "ffmpeg.exe", _ => true));
    })
};

int passed = 0;
foreach (var (name, run) in tests)
{
    try { run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.WriteLine("FAIL " + name + ": " + error); }
}
Console.WriteLine($"{passed}/{tests.Length} tool-path tests passed; no encoder executable was run.");
return passed == tests.Length ? 0 : 1;

string Resolve(string? setting, string fileName, Func<string, bool> exists) =>
    EncoderToolPaths.Resolve(setting, fileName, root, exists);
static void Equal(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new Exception($"Expected {expected}, actual {actual}");
}
static void Throws(Action action)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new Exception("Expected an argument error.");
}
