using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Aave.WaitOwnership.CallerSource;
using ManagedOriginalCallWitness;
using Mono.Cecil;
using MonoMod.Utils;
using COp=Mono.Cecil.Cil.OpCodes;
using Instruction=Mono.Cecil.Cil.Instruction;
using Variable=Mono.Cecil.Cil.VariableDefinition;
internal static class Program
{
    static readonly List<object> checks=new();static int failures;static string output="";
    static void Need(bool value,string why){if(!value)throw new InvalidDataException(why);}
    static void Case(string name,Action work){try{work();checks.Add(new{name,passed=true});}catch(Exception e){failures++;checks.Add(new{name,passed=false,error=e.ToString()});}}
    static void Refused(Action work,string why){try{work();}catch(InvalidDataException){return;}catch(NotSupportedException){return;}throw new InvalidDataException(why);}
    static DynamicMethodDefinition Plain(bool init,int localCount)
    {
        var d=new DynamicMethodDefinition("flags",typeof(void),Array.Empty<Type>());d.Definition.Body.InitLocals=init;
        for(int i=0;i<localCount;i++)d.Definition.Body.Variables.Add(new Variable(d.Module.TypeSystem.Int32));
        d.GetILProcessor().Append(Instruction.Create(COp.Ret));return d;
    }
    static MethodInfo FileSource(bool init,int plan)
    {
        string file=Path.Combine(output,"Source-"+plan+".dll");
        using(var module=ModuleDefinition.CreateModule("FlagsSource"+plan,ModuleKind.Dll))
        {
            var type=new TypeDefinition("Owned","Source",Mono.Cecil.TypeAttributes.Public|Mono.Cecil.TypeAttributes.Abstract|Mono.Cecil.TypeAttributes.Sealed,module.TypeSystem.Object);module.Types.Add(type);
            var m=new MethodDefinition("Release",Mono.Cecil.MethodAttributes.Public|Mono.Cecil.MethodAttributes.Static,module.TypeSystem.Void);type.Methods.Add(m);
            m.Parameters.Add(new ParameterDefinition("p",Mono.Cecil.ParameterAttributes.None,module.TypeSystem.Object));m.Parameters.Add(new ParameterDefinition("a",Mono.Cecil.ParameterAttributes.None,module.TypeSystem.Object));m.Parameters.Add(new ParameterDefinition("reason",Mono.Cecil.ParameterAttributes.None,module.TypeSystem.String));
            m.Body.InitLocals=true;m.Body.Instructions.Add(Instruction.Create(COp.Ret));
            CallerSourceRewriter.Apply(m,new CallerMethodPlan(plan,CallerSourceKind.Release,module.Mvid,m.MetadataToken.ToInt32(),CallerSourceRewriter.Fingerprint(m),Array.Empty<CallerSite>()));
            m.Body.InitLocals=init;module.Write(file);
        }
        return Assembly.LoadFile(file).GetType("Owned.Source")!.GetMethod("Release")!;
    }
    public static int Main(string[] args)
    {
        if(args.Length!=1||File.Exists(args[0]))throw new ArgumentException("Fresh output required");Need(Environment.Version==new Version(6,0,36),"ordinary636 only");output=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(args[0])!,"FixtureFiles"));Directory.CreateDirectory(output);
        foreach(bool input in new[]{false,true})foreach(int locals in new[]{0,1})Case($"fixed generator predicts init flag input={input} existinglocals={locals}",()=>{using var d=Plain(input,locals);var expected=DynamicBodyExpectation.Capture(d.Definition.Body,CilIdentity.Cecil(d.Definition.Body),11);var m=(DynamicMethod)d.Generate();var call=m.CreateDelegate<Action>();Need(m.InitLocals,"pinned generator did not keep constructor default");DynamicMethodBodyReader.Read(m,call,11).RequireExact(m,call,expected);});
        foreach(bool input in new[]{false,true})Case($"new local preserves independent generator flag for input={input}",()=>{using var d=Plain(input,0);d.Definition.Body.Variables.Add(new Variable(d.Module.TypeSystem.Object));var expected=DynamicBodyExpectation.Capture(d.Definition.Body,CilIdentity.Cecil(d.Definition.Body),12);var m=(DynamicMethod)d.Generate();var call=m.CreateDelegate<Action>();DynamicMethodBodyReader.Read(m,call,12).RequireExact(m,call,expected);});
        foreach(bool input in new[]{false,true})Case($"later Cecil flag change follows pinned emitter semantics input={input}",()=>{using var d=Plain(input,1);var expected=DynamicBodyExpectation.Capture(d.Definition.Body,CilIdentity.Cecil(d.Definition.Body),13);d.Definition.Body.InitLocals=!input;var m=(DynamicMethod)d.Generate();var call=m.CreateDelegate<Action>();Need(m.InitLocals,"fixed emitter unexpectedly applied later Cecil flag");DynamicMethodBodyReader.Read(m,call,13).RequireExact(m,call,expected);});
        Case("actual dynamic flag false before materialization remains rejected",()=>{using var d=Plain(true,1);var expected=DynamicBodyExpectation.Capture(d.Definition.Body,CilIdentity.Cecil(d.Definition.Body),14);var m=(DynamicMethod)d.Generate();m.InitLocals=false;var call=m.CreateDelegate<Action>();Refused(()=>DynamicMethodBodyReader.Read(m,call,14).RequireExact(m,call,expected),"actual false flag accepted");});
        Case("actual dynamic flag mutation after snapshot remains rejected",()=>{using var d=Plain(true,1);var expected=DynamicBodyExpectation.Capture(d.Definition.Body,CilIdentity.Cecil(d.Definition.Body),15);var m=(DynamicMethod)d.Generate();var call=m.CreateDelegate<Action>();var read=DynamicMethodBodyReader.Read(m,call,15);m.InitLocals=false;Refused(()=>read.RequireExact(m,call,expected),"post-read flag mutation accepted");});
        Case("DynamicILInfo compares its explicit input flag independently of stock generator",()=>{var expected=new DynamicBodyExpectation(16,new[]{"ret:"},new byte[]{7,0},0,false,Array.Empty<MemberInfo>());var m=new DynamicMethod("info-flags",typeof(void),Array.Empty<Type>()){InitLocals=false};var il=m.GetDynamicILInfo();il.SetCode(new byte[]{0x2a},1);il.SetLocalSignature(new byte[]{7,0});var call=m.CreateDelegate<Action>();var read=DynamicMethodBodyReader.Read(m,call,16);read.RequireExact(m,call,expected);Refused(()=>read.RequireExact(m,call,expected with{InitLocals=true}),"info flag compared against generator default");});
        Case("actual file original initialized locals remains eligible without qualification",()=>{var original=FileSource(true,31);Need(original.GetMethodBody()!.InitLocals&&original.GetMethodBody()!.LocalVariables.Count>0,"initialized file fixture missing");using var r=ManagedChainRegistration.FromCurrentOriginal(original,31,CallerSourceKind.Release);Need(!r.Qualified,"registration alone qualified");});
        Case("actual file original uninitialized existing locals is refused",()=>{var original=FileSource(false,32);Need(!original.GetMethodBody()!.InitLocals&&original.GetMethodBody()!.LocalVariables.Count>0,"false file fixture missing");Refused(()=>{using var r=ManagedChainRegistration.FromCurrentOriginal(original,32,CallerSourceKind.Release);},"uninitialized original acquired source registration");});
        Case("original rewriter still refuses uninitialized existing locals",()=>{using var d=new DynamicMethodDefinition(typeof(Fixture).GetMethod(nameof(Fixture.Empty))!);d.Definition.Body.InitLocals=false;d.Definition.Body.Variables.Add(new Variable(d.Module.TypeSystem.Int32));var m=d.Definition;Refused(()=>CallerSourceRewriter.Apply(m,new CallerMethodPlan(33,CallerSourceKind.Release,m.Module.Mvid,m.MetadataToken.ToInt32(),CallerSourceRewriter.Fingerprint(m),Array.Empty<CallerSite>())),"uninitialized-existing-locals boundary widened");});
        Case("false original without locals can initialize only newly introduced hook locals",()=>{using var d=new DynamicMethodDefinition(typeof(Fixture).GetMethod(nameof(Fixture.Empty))!);var m=d.Definition;Need(m.Body.Variables.Count==0,"fixture has existing locals");m.Body.InitLocals=false;CallerSourceRewriter.Apply(m,new CallerMethodPlan(34,CallerSourceKind.Release,m.Module.Mvid,m.MetadataToken.ToInt32(),CallerSourceRewriter.Fingerprint(m),Array.Empty<CallerSite>()));Need(m.Body.InitLocals&&m.Body.Variables.Count>0,"new hook locals uninitialized");using var r=ManagedChainRegistration.ForOrdinaryControl(typeof(Fixture).GetMethod(nameof(Fixture.Empty))!,m,34,CallerSourceKind.Release);Need(!r.Qualified,"prepared fixture qualified without publication");});
        Case("ordinary prepared source cannot silently downgrade its introduced locals",()=>{using var d=new DynamicMethodDefinition(typeof(Fixture).GetMethod(nameof(Fixture.Empty))!);var m=d.Definition;CallerSourceRewriter.Apply(m,new CallerMethodPlan(35,CallerSourceKind.Release,m.Module.Mvid,m.MetadataToken.ToInt32(),CallerSourceRewriter.Fingerprint(m),Array.Empty<CallerSite>()));m.Body.InitLocals=false;Refused(()=>{using var r=ManagedChainRegistration.ForOrdinaryControl(typeof(Fixture).GetMethod(nameof(Fixture.Empty))!,m,35,CallerSourceKind.Release);},"prepared source false locals registered");});
        var report=new{Runtime=Environment.Version.ToString(),ProcessId=Environment.ProcessId,Total=checks.Count,Failures=failures,Checks=checks,NativeExecuted=false,InstalledDetour=false,AAExecuted=false,TargetBodyInvocations=0};File.WriteAllText(args[0],JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(report));return failures==0?0:1;
    }
}
public static class Fixture { public static void Empty(object p,object a,string reason){} }
