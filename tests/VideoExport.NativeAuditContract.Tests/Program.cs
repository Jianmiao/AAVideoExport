using System.Reflection;
using AAVideoExport.Core;
int passed=0,failed=0;
Type Contract()=>Assembly.GetExecutingAssembly().GetType("AAVideoExport.NativePath.Tests.NativeAuditOutputContract")??throw new Exception("Missing source-linked production native output contract.");
object? Call(string name,params object?[] args)=>Contract().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,args);
void Check(bool b,string s){if(!b)throw new Exception(s);}
void Reject(Action a){try{a();}catch(Exception e)when(e.GetBaseException() is InvalidDataException or InvalidOperationException){return;}throw new Exception("Invalid production audit output accepted.");}
void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.GetBaseException().Message);}}
string root=Path.Combine(Path.GetTempPath(),"aave-native-noaudit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
string movie=Path.Combine(root,"movie.mov");File.WriteAllText(movie,"synthetic output contract fixture");
var result=new ExportResult(movie,30,1,"libx264",new FileInfo(movie).Length,null);
try{
Test("single-pass selection follows the actual loaded Core build profile",()=>Check((bool)Call("IsSinglePass",typeof(ExportOptions).Assembly)! == ExportBuildProfile.SinglePassEnabled,"Native harness did not use the loaded Core build profile."));
Test("production result has no recipe sidecar or fake receipt",()=>Check(Call("VerifyRecipeOutput",false,result)==null,"Production returned an audit proof."));
Test("production result rejects a claimed receipt even when the file is absent",()=>Reject(()=>Call("VerifyRecipeOutput",false,result with{RuntimeRecipeReceiptPath=movie+".runtime-recipes.json"})));
Test("production result rejects an actual audit sidecar",()=>{string path=movie+".runtime-recipes.json";File.WriteAllText(path,"{}");try{Reject(()=>Call("VerifyRecipeOutput",false,result));}finally{File.Delete(path);}});
Test("development audit still requires its original real receipt",()=>Reject(()=>Call("VerifyRecipeOutput",true,result)));
Test("test-owned input identity ignores only output names and directories",()=>{
    var a=new ExportOptions{Title="one",OutputDirectory=root,StrictSynchronization=true};var b=a with{Title="two",OutputDirectory=Path.Combine(root,"second")};
    var x=(string)Call("InputIdentity",new string('a',64),a)!;Check(x==(string)Call("InputIdentity",new string('a',64),b)!,"Publication filenames changed input identity.");
    Check(x!=(string)Call("InputIdentity",new string('b',64),a)!&&x!=(string)Call("InputIdentity",new string('a',64),a with{Fps=60})!,"Real input/profile change was hidden.");
});
Test("production preserves preexisting legacy audit files without creating new ones",()=>{
    string path=Path.Combine(root,"aave-timelines");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"legacy.json"),"old");var before=Call("SnapshotAuditDirectory",path)!;Call("RequireAuditDirectoryUnchanged",path,before);
    File.WriteAllText(Path.Combine(path,"new.json"),"new");try{Reject(()=>Call("RequireAuditDirectoryUnchanged",path,before));}finally{File.Delete(Path.Combine(path,"new.json"));}
    File.WriteAllText(Path.Combine(path,"legacy.json"),"changed");Reject(()=>Call("RequireAuditDirectoryUnchanged",path,before));
});
}finally{Directory.Delete(root,true);}
Console.WriteLine($"RESULT {passed} passed; {failed} failed; test-side production output contract");return failed==0?0:1;
