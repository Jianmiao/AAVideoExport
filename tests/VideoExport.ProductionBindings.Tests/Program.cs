using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
int passed=0,failed=0;
void Test(string name,Action run){try{run();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
void Reject(Action run){try{run();}catch(NotSupportedException){return;}throw new Exception("Expected incompatible binding refusal");}
if(!ExportBuildProfile.DevelopmentAuditEnabled)
{
    Test("production adapter binding never reads audit image location or module identity",()=>new CompatibilityBuild("KnownAdapter",new Version(1,0),Guid.Empty,"not-an-audit-hash").Validate(new BindingAssembly()));
    Test("production still rejects the wrong API owner name",()=>Reject(()=>new CompatibilityBuild("DifferentAdapter",new Version(1,0),Guid.Empty,"").Validate(new BindingAssembly())));
    Test("production still rejects an unsupported API version",()=>Reject(()=>new CompatibilityBuild("KnownAdapter",new Version(2,0),Guid.Empty,"").Validate(new BindingAssembly())));
}
else
{
    Test("development audit still checks exact module and physical build",()=>{var a=typeof(Program).Assembly;Reject(()=>new CompatibilityBuild(a.GetName().Name!,a.GetName().Version!,Guid.Empty,"bad").Validate(a));});
}
Console.WriteLine($"RESULT {passed} passed; {failed} failed");return failed==0?0:1;
sealed class BindingAssembly:Assembly
{
    public override AssemblyName GetName(bool copiedName)=>new("KnownAdapter"){Version=new Version(1,0)};
    public override AssemblyName GetName()=>GetName(false);
    public override string Location=>throw new Exception("AUDIT location read in production");
    public override Module ManifestModule=>throw new Exception("AUDIT module provenance read in production");
}
