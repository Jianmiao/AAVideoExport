using AAVideoExport.Plugin;

var before = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION");
var beforeRecipe = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES");
int passed = 0, failed = 0;
void Test(string name, Action run)
{
    try { run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
try
{
    RecipeStatusTransportChecks.Run(Test);
    Test("disabled recipe scope cannot report success or invoke diagnostics",()=>
    {
        Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES",null);
        Check(!RuntimeAttributionClient.TryAcquireRecipeScope(out var token,out var json,_=>throw new Exception("must not report"))&&token==""&&json=="","Disabled recipe acquired scope");
    });
    Test("missing recipe observer fails closed even when diagnostics throw",()=>
    {
        Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES","1");
        Check(!RuntimeAttributionClient.TryAcquireRecipeScope(out _,out _,_=>throw new IOException("diagnostic sink")),"Missing recipe observer succeeded");
        Check(!RuntimeAttributionClient.TryValidateRecipeScope("unknown",true,out _,_=>{}),"Unknown scope validated");
    });
    Test("recipe scope contract requires exact host scope and truthful global flags",()=>
    {
        var method=typeof(RuntimeAttributionClient).GetMethod("RecipeScopeValid",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        bool Valid(string json){using var document=System.Text.Json.JsonDocument.Parse(json);return (bool)method.Invoke(null,new object[]{document.RootElement})!;}
        string good="{\"schema\":\"aave.runtime-recipes.v2\",\"valid\":true,\"knownHostBaseline\":true,\"evidenceKind\":\"PRE_MATERIALIZATION_FINAL_IL\",\"policy\":\"aa101.interop-initializer.intptr.final-il.v2\",\"policySha256\":\""+new string('a',64)+"\",\"sealed\":false,\"recipesSha256\":\"\",\"preexistingCoverage\":false,\"allCodeCoverage\":false,\"permitsStrictExport\":false}";
        Check(Valid(good),"Exact limited contract was rejected");
        Check(!Valid(good.Replace("aa101.interop-initializer.intptr.final-il.v2","aa101.interop-initializer.intptr.v1")),"Old unstable typed-emission policy passed");
        Check(!Valid(good.Replace("PRE_MATERIALIZATION_FINAL_IL","TYPED_EMISSION_HISTORY")),"Old emission strategy passed");
        Check(!Valid(good.Replace("\"permitsStrictExport\":false","\"permitsStrictExport\":true")),"Global authorization claim passed");
        Check(!Valid(good.Replace("\"knownHostBaseline\":true","\"knownHostBaseline\":false")),"Test/unverified baseline passed");
        Check(!Valid(good.Replace("aa101.interop-initializer.intptr.final-il.v2","arbitrary-factory")),"Another factory scope passed");
    });
    Test("disabled optional observer never invokes reporting", () =>
    {
        Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION", null);
        Check(!RuntimeAttributionClient.TryMark("test", _ => throw new Exception("must not report")), "disabled marker was admitted");
    });
    Test("missing observer reports unavailability without startup failure", () =>
    {
        Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION", "1");
        int reports = 0;
        Check(!RuntimeAttributionClient.TryMark("test", _ => reports++), "missing observer marker succeeded");
        Check(reports == 1, "missing observer was not reported once");
    });
    Test("throwing diagnostics cannot turn missing observation into plugin failure", () =>
    {
        Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION", "1");
        Check(!RuntimeAttributionClient.TryMark("test", _ => throw new IOException("fixture log sink unavailable")),
            "missing observer marker succeeded");
    });
}
finally { Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION", before);Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES",beforeRecipe); }
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;
