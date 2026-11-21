using System.Reflection;

if (args.Length is < 1 or > 2) throw new ArgumentException("Pass the bootstrap DLL to inspect and optional --patcher-smoke.");
var bootstrap = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var trace = bootstrap.GetType("AAVideoExport.RuntimeBootstrap.StartupTrace");
if (trace is null) { Console.WriteLine("FAIL explicit durable bootstrap startup tracing is missing"); return 1; }
var write = trace.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!;
var writeTarget = trace.GetMethod("WriteTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
void Write(string stage, string detail = "") => write.Invoke(null, new object?[] { stage, detail });
int checks = 0;
void Check(bool pass, string message) { if (!pass) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
const string variable = "AA_VIDEOEXPORT_STARTUP_TRACE_FILE";
var previous = Environment.GetEnvironmentVariable(variable);
var directory = Path.Combine(Path.GetTempPath(), "aave-bootstrap-trace-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "startup.trace");
try
{
    if (args.Contains("--patcher-smoke"))
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var dependency = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(dependency) ? Assembly.LoadFrom(dependency) : null;
        };
        Environment.SetEnvironmentVariable(variable, path);
        Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION", "0");
        Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES", "0");
        var patcher = bootstrap.GetType("AAVideoExport.RuntimeBootstrap.RuntimeAttributionPatcher", true)!;
        // Only exercise Initialize's disabled path. BepInEx owns BasePatcher
        // construction and its process-global Paths/config in a real preloader.
        var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(patcher);
        patcher.GetMethod("Initialize")!.Invoke(instance, null);
        var stages = File.ReadAllLines(path).Select(line => line.Split('|')[4][6..]).ToArray();
        Check(stages.SequenceEqual(new[] { "Patcher.Initialize.enter", "Patcher.v1.before", "Patcher.v1.after",
            "Patcher.v2.before", "v2.Initialize.enter", "v2.Initialize.disabled", "Patcher.v2.after", "Patcher.Initialize.complete" }),
            "patcher Initialize preserves all ordered startup boundaries with both observers disabled");
        Console.WriteLine("RESULT 1 passed; standalone patcher diagnostic path only, no AA startup proof");
        return 0;
    }
    Environment.SetEnvironmentVariable(variable, null);
    Write("disabled");
    Check(!Directory.EnumerateFileSystemEntries(directory).Any(), "unset diagnostic path writes no files");

    Environment.SetEnvironmentVariable(variable, path);
    Write("test.first", "line one\r\nline two|field");
    using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        Check(reader.Length > 0, "completed marker is immediately visible to an independent file reader");
    var first = File.ReadAllLines(path);
    Check(first.Length == 1 && first[0].Contains("stage=test.first") && first[0].Contains("line one  line two/field"), "untrusted detail cannot split records");
    Check(first[0].Contains("pid=" + Environment.ProcessId + "|") && first[0].Contains("tid=" + Environment.CurrentManagedThreadId + "|") && first[0].Contains("utc="), "markers identify the process thread and UTC time");

    var target = typeof(string).GetMethod(nameof(string.StartsWith), new[] { typeof(string), typeof(StringComparison) })!;
    writeTarget.Invoke(null, new object?[] { "test.target", target });
    var targetLine = File.ReadLines(path).Last();
    Check(targetLine.Contains("System.String::StartsWith") && targetLine.Contains("token=0x" + target.MetadataToken.ToString("X8")) && targetLine.Contains("System.String, System.StringComparison"), "target markers distinguish declaring type token and overload signature");

    Parallel.For(0, 80, i => Write("test.concurrent", i.ToString()));
    var lines = File.ReadAllLines(path);
    var sequences = lines.Select(line => long.Parse(line.Split('|')[0][4..])).ToArray();
    Check(lines.Length == 82 && sequences.SequenceEqual(Enumerable.Range(1, 82).Select(i => (long)i)), "concurrent writers preserve complete ordered append records");

    Environment.SetEnvironmentVariable(variable, "relative-startup.trace");
    Write("relative");
    Check(!File.Exists("relative-startup.trace"), "relative trace paths are rejected rather than writing to the host working directory");
    Environment.SetEnvironmentVariable(variable, directory);
    Write("directory-is-not-file");
    Check(File.ReadAllLines(path).Length == 82, "diagnostic I/O failure does not escape or corrupt earlier evidence");

    Console.WriteLine("RESULT " + checks + " passed; diagnostic transport only, no native startup proof");
    return 0;
}
finally
{
    Environment.SetEnvironmentVariable(variable, previous);
    File.Delete(path);
    Directory.Delete(directory);
}
