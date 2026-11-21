using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.Emit;
using MonoMod.Utils.Cil;
using MonoMod.RuntimeDetour;

if(Environment.Version!=new Version(6,0,7)){Console.WriteLine("FAIL actual fixed .NET 6.0.7 required");return 1;}
GC.KeepAlive(typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).Assembly);
if(args.Contains("--name-cache-tests")) return AssemblyNameMetadataChecks.Run();
if(args.Contains("--name-cache-benchmark")) return AssemblyNameMetadataChecks.Benchmark();
if(args.Contains("--harmony-receipt-tests")) return HarmonyMetadataReceiptChecks.Run();
if(args.Contains("--harmony-receipt-fallback")) return HarmonyMetadataReceiptChecks.Fallback();
if(args.Length==2&&args[0]=="--additional-case")
{
    try{FrameworkAdditionalChecks.Run(args[1]);Console.WriteLine("PASS additional-"+args[1]);return 0;}
    catch(Exception error){Console.WriteLine("FAIL additional-"+args[1]+": "+error);return 1;}
}
if(args.Contains("--physical-negative"))
{
    _=new HarmonyLib.Harmony("aave.framework-scope.physical-negative");_=ILGeneratorShim.ProxyType;_=DetourHelper.Runtime;
    try{using var unexpected=AAVideoExport.Plugin.FrameworkGeneratedAssemblyScope.Acquire();Console.WriteLine("FAIL modified physical image was authorized");return 1;}
    catch(Exception error){if(error.GetBaseException().Message.Contains("physical image SHA-256 changed",StringComparison.Ordinal)){Console.WriteLine("PASS physical image mutation rejected before scope creation");return 0;}throw;}
}
if(args.Length==2&&args[0]=="--case")
{
    try{FrameworkGeneratedScopeChecks.Run(args[1]);Console.WriteLine("PASS "+args[1]);return 0;}
    catch(Exception error){Console.WriteLine("FAIL "+args[1]+": "+error);return 1;}
}
if(args.Contains("--audit"))
{
    if(args.Contains("--with-monitors"))
    {
        _=new HarmonyLib.Harmony("aave.framework-scope.warm");_=ILGeneratorShim.ProxyType;_=DetourHelper.Runtime;
        try{using var discarded=AAVideoExport.Plugin.FrameworkGeneratedAssemblyScope.Acquire();}catch(Exception error){Console.WriteLine("AUDIT NOTE "+error.Message);}
    }
    var proxy=ILGeneratorShim.ProxyType;var runtime=DetourHelper.Runtime;var platform=typeof(DetourHelper).Assembly.GetType("MonoMod.RuntimeDetour.Platforms.DetourRuntimeNETCore30Platform",true)!;
    var helper=(MethodInfo)platform.GetField("_getTypeFromHandleUnsafeMethod",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(runtime)!;
    foreach(var item in new[]{(proxy.Assembly,true),(helper.Module.Assembly,false)})
    {
        var shape=AAVideoExport.Plugin.FrameworkGeneratedAssemblyScope.InspectTemplate(item.Item1,item.Item2,typeof(ILGeneratorShim));
        Console.WriteLine("TEMPLATE "+item.Item1.GetName().Name+" hash="+shape.Hash);Console.WriteLine(shape.Canonical);
    }
    return 0;
}
if(args.Contains("--positive"))
{
    _=new HarmonyLib.Harmony("aave.framework-scope.warm");_=ILGeneratorShim.ProxyType;_=DetourHelper.Runtime;
    using var scope=AAVideoExport.Plugin.FrameworkGeneratedAssemblyScope.Acquire();scope.Validate(true);
    foreach(var assembly in scope.Assemblies)if(!scope.AuthorizesAssembly(assembly))throw new Exception("Actual cached assembly was rejected");
    Console.WriteLine("PASS "+scope.IdentitySha256);
    using(scope.EnterCommitScope()){}
    bool consumed=false;try{scope.Validate();}catch(InvalidOperationException){consumed=true;}
    if(!consumed)throw new Exception("Publication did not consume scope");return 0;
}
if(args.Contains("--inspect"))
{
    var proxy=ILGeneratorShim.ProxyType;
    var runtime=DetourHelper.Runtime;
    var parent=runtime.GetType().BaseType!;
    while(parent.FullName!="MonoMod.RuntimeDetour.Platforms.DetourRuntimeNETCore30Platform")parent=parent.BaseType!;
    var helper=(MethodInfo)parent.GetField("_getTypeFromHandleUnsafeMethod",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(runtime)!;
    Console.WriteLine("RUNTIME "+runtime.GetType().FullName);
    foreach(var assembly in new[]{proxy.Assembly,helper.Module.Assembly})
    {
        Console.WriteLine("ASSEMBLY "+assembly.FullName+" dynamic="+assembly.IsDynamic+" location="+assembly.Location);
        foreach(var attr in assembly.GetCustomAttributesData())Console.WriteLine("ATTRIBUTE "+attr);
        DumpRows(assembly);
        foreach(var type in assembly.GetTypes())
        {
            Console.WriteLine("TYPE "+type.FullName+" attrs="+(int)type.Attributes+" base="+type.BaseType);
            foreach(var gp in type.GetGenericArguments())Console.WriteLine("GENERIC "+gp.Name+" attrs="+(int)gp.GenericParameterAttributes+" constraints="+string.Join(",",gp.GetGenericParameterConstraints().Select(t=>t.ToString())));
            foreach(var field in type.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly))Console.WriteLine("FIELD "+field+" attrs="+(int)field.Attributes);
            foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(type.GetConstructors()))
            {
                var body=method.GetMethodBody();Console.WriteLine("METHOD "+method+" attrs="+(int)method.Attributes+" impl="+(int)method.GetMethodImplementationFlags()+" init="+body?.InitLocals+" max="+body?.MaxStackSize+" il="+(body==null?"null":Convert.ToHexString(body.GetILAsByteArray()!)));
                foreach(var parameter in method.GetParameters())Console.WriteLine("PARAM "+parameter+" attrs="+(int)parameter.Attributes+" default="+parameter.HasDefaultValue+" raw="+(parameter.RawDefaultValue?.ToString()??"null")+" custom="+parameter.GetCustomAttributesData().Count+" token="+parameter.MetadataToken.ToString("X8"));
            }
        }
    }
    return 0;
}
var proof=typeof(Program).Assembly.GetType("AAVideoExport.Plugin.FrameworkGeneratedAssemblyScope");
if(proof==null){Console.WriteLine("FAIL no independent actual-cache framework generated assembly proof exists");return 1;}
int passed=0,failed=0;
var directory=Path.GetFullPath(Environment.GetEnvironmentVariable("AA_FRAMEWORK_TEST_EVIDENCE")??"artifacts/framework-generated-20261030");Directory.CreateDirectory(directory);
foreach(var name in FrameworkGeneratedScopeChecks.Cases)
{
    var start=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(var arg in new[]{"exec","--fx-version","6.0.7",typeof(Program).Assembly.Location,"--case",name})start.ArgumentList.Add(arg);
    using var process=System.Diagnostics.Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
    bool finished=process.WaitForExit(30000);if(!finished){process.Kill(true);process.WaitForExit();}
    File.WriteAllText(Path.Combine(directory,name+".stdout.log"),output.GetAwaiter().GetResult());File.WriteAllText(Path.Combine(directory,name+".stderr.log"),errors.GetAwaiter().GetResult());
    if(finished&&process.ExitCode==0){passed++;Console.WriteLine("PASS "+name);}else{failed++;Console.WriteLine("FAIL "+name+" exit="+process.ExitCode);}
}
Console.WriteLine($"RESULT {passed} passed; {failed} failed; actual .NET {Environment.Version}; no AA launch");return failed==0?0:1;

static unsafe void DumpRows(Assembly assembly)
{
    if(!assembly.TryGetRawMetadata(out byte* blob,out int length))throw new Exception("No loaded metadata");
    var reader=new MetadataReader(blob,length);
    foreach(var table in Enum.GetValues<TableIndex>())
    {int count=reader.GetTableRowCount(table);if(count>0)Console.WriteLine("TABLE "+table+"="+count);}
}
