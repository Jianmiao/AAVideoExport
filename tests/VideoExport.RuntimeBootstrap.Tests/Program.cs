using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using AAVideoExport.RuntimeBootstrap;

var results = new List<object>();
var failed = 0;
void Check(string name, bool passed, object? detail = null)
{
    results.Add(new { name, passed, detail });
    if (!passed) failed++;
}
var path = Path.Combine(AppContext.BaseDirectory, "AAVideoExport.RuntimeBootstrap.dll");
Check("early bootstrap assembly exists", File.Exists(path));
if (!File.Exists(path)) return Report();
var bootstrap = Assembly.LoadFrom(path);
var api = bootstrap.GetType("AAVideoExport.RuntimeBootstrap.RuntimeAttributionV1", throwOnError: true)!;
object? Call(string name, params object?[] parameters) => api.GetMethod(name,
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, parameters);
T Get<T>(string name) => (T)api.GetProperty(name)!.GetValue(null)!;
JsonElement Snapshot() => JsonDocument.Parse((string)Call("GetSnapshotJson")!).RootElement.Clone();
JsonElement Attribution(Delegate value) => JsonDocument.Parse((string)Call("GetAttributionJson", value)!).RootElement.Clone();

Check("versioned diagnostics cannot certify strict export", Get<int>("ProtocolVersion") == 1 &&
    !Snapshot().GetProperty("permitsStrictExport").GetBoolean());
foreach (var optIn in new string?[] { null, "", "true", "0", " 1", "1 " })
{
    Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION", optIn);
    Call("InitializeFromEnvironment");
    Check("only exact opt in enables hooks: " + (optIn ?? "unset"), !Get<bool>("IsInstalled"));
}

var a = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "AAVE.Probe.ProducerA.dll"));
var b = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "AAVE.Probe.ProducerB.dll"));
(DynamicMethod method, Func<int,int> call) Build(Assembly assembly, string name, params object[] parameters)
{
    var created = (object[])assembly.GetType("FixtureProducer.Factory")!.GetMethod(name)!.Invoke(null, parameters)!;
    return ((DynamicMethod)created[0], (Func<int,int>)created[1]);
}
var baseline = Build(a, "Build", 3);
var baselineB = Build(b, "Build", 9);
var variants = Enumerable.Range(0,8).Select(i => Build(a, "BuildWithConstructor", i, 30+i)).ToArray();
var boundBase = Build(b, "BuildBound", 23);
_ = Snapshot();
Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION", args.Contains("--without-hooks") ? "0" : "1");
Call("InitializeFromEnvironment");
Check("opt in installs early hooks", Get<bool>("IsInstalled"), Snapshot());
if (!Get<bool>("IsInstalled"))
{
    Check("negative control preserves original delegate output", baseline.call(39)==42 && baselineB.call(33)==42);
    return Report();
}
Check("coverage begins after install and never claims preexisting or all code", Get<string>("CoverageStartedUtc").Length > 0 &&
    !Snapshot().GetProperty("preexistingCoverage").GetBoolean() && !Snapshot().GetProperty("allCodeCoverage").GetBoolean());
var first = Build(a, "Build", 3);
var second = Build(b, "Build", 9);
var ra = Attribution(first.call);
var rb = Attribution(second.call);
Check("same name and holder do not collapse method identity", ra.GetProperty("status").GetString() == "OBSERVED" &&
    rb.GetProperty("status").GetString() == "OBSERVED" &&
    ra.GetProperty("record").GetProperty("id").GetInt64() != rb.GetProperty("record").GetProperty("id").GetInt64());
Check("observed caller belongs to each actual creator assembly", ra.GetProperty("record").GetProperty("creatorAssembly").GetString() == a.GetName().Name &&
    rb.GetProperty("record").GetProperty("creatorAssembly").GetString() == b.GetName().Name);
Check("observed delegate output equals baseline", Enumerable.Range(-100,201).All(i =>
    first.call(i) == baseline.call(i) && second.call(i) == baselineB.call(i)));
Check("unobserved returned delegate stays unknown", Attribution(baseline.call).GetProperty("status").GetString() == "UNKNOWN");

var loads = new List<string>();
AssemblyLoadEventHandler handler = (_, e) => loads.Add(e.LoadedAssembly.FullName ?? "?");
AppDomain.CurrentDomain.AssemblyLoad += handler;
var late = Build(a, "Build", 17);
AppDomain.CurrentDomain.AssemblyLoad -= handler;
Check("late creation captured without AssemblyLoad", loads.Count == 0 &&
    Attribution(late.call).GetProperty("status").GetString() == "OBSERVED", loads);
Check("all eight public constructor overloads preserve results and attribution", Enumerable.Range(0,8).All(i => {
    var item = Build(a,"BuildWithConstructor",i,30+i);
    var record = Attribution(item.call).GetProperty("record");
    return record.GetProperty("constructorEvents").GetInt64() >= 1 &&
        Enumerable.Range(-100,201).All(x=>item.call(x)==variants[i].call(x));
}));
var bound = Build(b,"BuildBound",23);
Check("bound delegate overload preserves identity and results", Attribution(bound.call).GetProperty("status").GetString()=="OBSERVED" &&
    Enumerable.Range(-100,201).All(x=>bound.call(x)==boundBase.call(x)));
var newlyMaterialized = (Func<int,int>)baseline.method.CreateDelegate(typeof(Func<int,int>));
var preexisting = Attribution(newlyMaterialized).GetProperty("record");
Check("preexisting method later materialization never invents a creator", preexisting.GetProperty("constructorEvents").GetInt64()==0 &&
    preexisting.GetProperty("creatorAssembly").GetString()=="UNKNOWN");

var direct = (DynamicMethod)a.GetType("FixtureProducer.Factory")!.GetMethod("BuildWithoutDelegate")!.Invoke(null,new object[]{40})!;
var beforeInvoke = Snapshot();
var invokeResult = direct.Invoke(null,new object[]{2});
var directDelegate = (Func<int,int>)direct.CreateDelegate(typeof(Func<int,int>));
Check("public DynamicMethod Invoke is observed without altering result", (int)invokeResult! == 42 &&
    Attribution(directDelegate).GetProperty("record").GetProperty("invokeEvents").GetInt64()==1);
Check("snapshot is a detached immutable copy", !beforeInvoke.GetProperty("records").EnumerateArray().Any(r=>
    r.GetProperty("name").GetString()=="DirectInvoke" && r.GetProperty("invokeEvents").GetInt64()>0));
Call("Mark", "fixture:export-start");
Check("diagnostic marker recorded", Snapshot().GetProperty("markers").EnumerateArray().Any(m=>m.GetProperty("name").GetString()=="fixture:export-start"));
Check("callback failures remain zero", Snapshot().GetProperty("lossCounters").GetProperty("callbackFailures").GetInt64()==0);
Check("all observed records are diagnostic only", !Attribution(first.call).GetProperty("permitsStrictExport").GetBoolean() &&
    Attribution(first.call).GetProperty("evidenceKind").GetString()=="STACK_OBSERVATION_ONLY");
var liveObserver = (RuntimeObserver)api.GetField("observer", BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
liveObserver.Dispose();
foreach (var result in CollectorTests.Run()) Check(result.Name, result.Passed, result.Detail);
return Report();

int Report()
{
    Console.WriteLine(JsonSerializer.Serialize(new { schema="aave.runtime-bootstrap.tests.v1", runtime=Environment.Version.ToString(),
        checks=results, failed, scope="Isolated fixtures only; no AA startup, Mod certification, or export-effects proof." },
        new JsonSerializerOptions { WriteIndented=true }));
    return failed == 0 ? 0 : 1;
}
