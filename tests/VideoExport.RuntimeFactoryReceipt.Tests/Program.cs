using System.Reflection;
using System.Text.Json;
using AAVideoExport.Core;

string root = Path.Combine(Path.GetTempPath(), "aave-runtime-receipt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
int passed=0,failed=0,index=0;
void Test(string name,Action run){try{run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error.Message);}}
Fixture Make()
{
    string dir=Path.Combine(root,(++index).ToString());Directory.CreateDirectory(dir);string staged=Path.Combine(dir,"staged.bin"),movie=Path.Combine(dir,"movie.mp4");
    File.WriteAllBytes(staged,new byte[]{1,2,3,4,5});
    Type type=typeof(ExportSession).Assembly.GetType("AAVideoExport.Core.RuntimeFactoryRecipeReceipt")??throw new Exception("Final runtime recipe receipt is missing.");
    object proof=Activator.CreateInstance(type,new string('a',64),new string('b',64))!;
    object sidecar=Call(proof,"Stage",dir,staged,movie,1L,30,96,64,"source-identity")!;
    return new Fixture(type,sidecar,staged,movie,movie+".runtime-recipes.json");
}
try
{
    Test("a sidecar alone never proves movie publication",()=>{var f=Make();Call(f.Sidecar,"Publish");Reject(()=>Verify(f));Check(File.Exists(f.Receipt)&&!File.Exists(f.Movie),"fixture lost orphan distinction");});
    Test("associated movie must exist with exact expected hash and truthful limited flags",()=>
    {var f=Make();Call(f.Sidecar,"Publish");File.Move(f.Staged,f.Movie);Verify(f);using(var j=JsonDocument.Parse(File.ReadAllText(f.Receipt))){var r=j.RootElement;Check(!r.GetProperty("preexistingCoverage").GetBoolean()&&!r.GetProperty("allCodeCoverage").GetBoolean()&&!r.GetProperty("permitsStrictExport").GetBoolean(),"receipt broadened coverage");Check(r.GetProperty("movieFileName").GetString()=="movie.mp4","receipt not tied to output basename");}File.WriteAllBytes(f.Movie,new byte[]{1,2,3,4,6});Reject(()=>Verify(f));});
    Test("non-overwriting publication and rollback preserve an existing user sidecar",()=>
    {var f=Make();File.WriteAllText(f.Receipt,"user-owned");Reject(()=>Call(f.Sidecar,"Publish"));Check((bool)Call(f.Sidecar,"TryRollback",new object?[]{null})!,"unmoved sidecar rollback failed");Check(File.ReadAllText(f.Receipt)=="user-owned","existing sidecar was overwritten or deleted");});
    Test("failed movie publication rolls back only its owned sidecar",()=>
    {var f=Make();Call(f.Sidecar,"Publish");object?[] result={null};Check((bool)Call(f.Sidecar,"TryRollback",result)!&&!File.Exists(f.Receipt),"owned sidecar was left behind");Check(File.Exists(f.Staged),"rollback removed unrelated staged source");});
    Test("rollback sharing failure reports the residual and supports retry",()=>
    {var f=Make();Call(f.Sidecar,"Publish");using(var held=new FileStream(f.Receipt,FileMode.Open,FileAccess.Read,FileShare.Read)){object?[] result={null};Check(!(bool)Call(f.Sidecar,"TryRollback",result)!&&result[0]?.ToString()?.Contains(f.Receipt)==true,"rollback hid its residual path");}object?[] retry={null};Check((bool)Call(f.Sidecar,"TryRollback",retry)!&&!File.Exists(f.Receipt),"owned rollback could not retry");});
    Test("rollback does not delete a replacement with another ownership identity",()=>
    {var f=Make();Call(f.Sidecar,"Publish");File.WriteAllText(f.Receipt,"{\"receiptId\":\"another-owner\"}");object?[] result={null};Check(!(bool)Call(f.Sidecar,"TryRollback",result)!&&File.ReadAllText(f.Receipt).Contains("another-owner"),"rollback deleted a replacement user file");});
    Test("malformed ownership metadata is preserved and its residual path is reported",()=>
    {var f=Make();Call(f.Sidecar,"Publish");File.WriteAllText(f.Receipt,"{\"receiptId\":42}");object?[] result={null};Check(!(bool)Call(f.Sidecar,"TryRollback",result)!&&result[0]?.ToString()?.Contains(f.Receipt)==true&&File.Exists(f.Receipt),"malformed ownership was removed or its residual hidden");});
    Test("old low-level Emit evidence is rejected even when the movie association matches",()=>
    {var f=Make();Call(f.Sidecar,"Publish");File.Move(f.Staged,f.Movie);string good=File.ReadAllText(f.Receipt);File.WriteAllText(f.Receipt,good.Replace("aa101.interop-initializer.intptr.final-il.v2","aa101.interop-initializer.intptr.v1"));Reject(()=>Verify(f));File.WriteAllText(f.Receipt,good.Replace("PRE_MATERIALIZATION_FINAL_IL","EMIT_TRACE"));Reject(()=>Verify(f));});
}
finally{Directory.Delete(root,true);}
Console.WriteLine($"RESULT: {passed} passed; {failed} failed. File-association fixtures only; no media-decoder claim.");return failed==0?0:1;
static object? Call(object target,string method,params object?[] args)
{try{return target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(target,args);}catch(TargetInvocationException e)when(e.InnerException!=null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}}
static void Verify(Fixture f){try{f.Type.GetMethod("VerifyPublished",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,new object[]{f.Receipt});}catch(TargetInvocationException e)when(e.InnerException!=null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}}
static void Reject(Action action){try{action();}catch{return;}throw new Exception("invalid receipt operation succeeded");}
static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
sealed record Fixture(Type Type,object Sidecar,string Staged,string Movie,string Receipt);
