using System.Reflection;
using System.Text.Json;

namespace AAVideoExport.Plugin;

// Optional early-observer diagnostics. This bridge never changes admission,
// event execution, cache flags or the fixed RenderControlV1 contract.
internal static class RuntimeAttributionClient
{
    internal const string ExpectedRecipePolicy="aa101.interop-initializer.intptr.final-il.v2";
    internal const string ExpectedRecipeEvidenceKind="PRE_MATERIALIZATION_FINAL_IL";
    internal static bool TryAcquireRecipeScope(out string token, out string evidence, Action<string> report)
    {
        token="";evidence="";
        if(!TryRecipeCall("AcquireScopeJson",Type.EmptyTypes,Array.Empty<object>(),out evidence,report))return false;
        try
        {
            using var json=JsonDocument.Parse(evidence);var root=json.RootElement;
            token=root.GetProperty("token").GetString()??"";
            if(!RecipeScopeValid(root))
            {
                string reason=root.TryGetProperty("reason",out var detail)?detail.GetString()??"":"missing recipe reason";
                ReportRecipe(report,new InvalidOperationException("Recipe acquisition rejected: "+reason));
                ReleaseRecipeScope(token,report);token="";return false;
            }
            return token.Length>0;
        }
        catch(Exception error){ReleaseRecipeScope(token,report);token="";ReportRecipe(report,error);return false;}
    }
    internal static bool TryValidateRecipeScope(string token,bool forCommit,out string evidence,Action<string> report)
    {
        evidence="";
        if(string.IsNullOrEmpty(token)||Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES")!="1")return false;
        try
        {
            var method=RecipeValidationMethod(RecipeType(),forCommit);
            evidence=(string)method.Invoke(null,new object[]{token})!;
            using var json=JsonDocument.Parse(evidence);return RecipeScopeValid(json.RootElement)&&json.RootElement.GetProperty("token").GetString()==token
                &&(!forCommit||json.RootElement.GetProperty("sealed").GetBoolean());
        }
        catch(Exception error){ReportRecipe(report,error);return false;}
    }
    private static MethodInfo RecipeValidationMethod(Type type,bool forCommit)
    {
        const BindingFlags flags=BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly;
        // Older v2 bootstraps still supply live validation through the original
        // endpoint. Only absence permits fallback; malformed/failed status APIs
        // must not be hidden by a second, potentially different validation call.
        var method=forCommit?null:type.GetMethod("ValidateScopeStatusJson",flags);
        method??=type.GetMethod(forCommit?"ValidateScopeForCommitJson":"ValidateScopeJson",flags,null,new[]{typeof(string)},null);
        if(method?.ReturnType!=typeof(string)||method.IsGenericMethodDefinition
            ||!method.GetParameters().Select(parameter=>parameter.ParameterType).SequenceEqual(new[]{typeof(string)}))
            throw new MissingMethodException("Recipe live validation API signature differs.");
        return method;
    }
    internal static bool TrySealRecipeScope(string token,out string evidence,Action<string> report)
    {
        evidence="";if(string.IsNullOrEmpty(token)||!TryRecipeCall("SealScopeJson",new[]{typeof(string)},new object[]{token},out evidence,report))return false;
        try{using var json=JsonDocument.Parse(evidence);return RecipeScopeValid(json.RootElement)&&json.RootElement.GetProperty("token").GetString()==token&&json.RootElement.GetProperty("sealed").GetBoolean();}
        catch(Exception error){ReportRecipe(report,error);return false;}
    }
    internal static bool TryGetEmissionRecipe(Delegate value,out string evidence,Action<string> report)
    {
        evidence="";if(value==null)return false;
        if(!TryRecipeCall("GetRecipeJson",new[]{typeof(Delegate)},new object[]{value},out evidence,report))return false;
        try{using var json=JsonDocument.Parse(evidence);return json.RootElement.GetProperty("status").GetString()=="QUALIFIED"
            &&json.RootElement.GetProperty("evidenceKind").GetString()==ExpectedRecipeEvidenceKind
            &&json.RootElement.GetProperty("validatedBeforeMaterialization").GetBoolean();}
        catch(Exception error){ReportRecipe(report,error);return false;}
    }
    internal static bool TryValidateRecipeHolder(string token,Assembly candidate,Action<string> report)
    {
        if(candidate==null||!TryValidateRecipeScope(token,false,out _,report))return false;
        try
        {
            var method=RecipeType().GetMethod("ValidateHolder",BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly,null,new[]{typeof(string),typeof(Assembly)},null);
            if(method?.ReturnType!=typeof(bool))throw new MissingMethodException("Recipe holder reference contract differs.");
            return method.Invoke(null,new object[]{token,candidate}) is true;
        }
        catch(Exception error){ReportRecipe(report,error);return false;}
    }
    internal static void ReleaseRecipeScope(string token,Action<string> report)
    {
        if(string.IsNullOrEmpty(token))return;
        try{var type=RecipeType();var method=type.GetMethod("ReleaseScope",BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly,null,new[]{typeof(string)},null);
            if(method?.ReturnType!=typeof(void))throw new MissingMethodException("Recipe ReleaseScope(string) signature differs.");method.Invoke(null,new object[]{token});}
        catch(Exception error){ReportRecipe(report,error);}
    }
    internal static bool TryEnterRecipeCommitScope(string token,out IDisposable? lease,Action<string> report)
        =>TryEnterRecipeCommitScope(token,CancellationToken.None,out lease,report);
    internal static bool TryEnterRecipeCommitScope(string token,CancellationToken cancellationToken,out IDisposable? lease,Action<string> report)
    {
        lease=null;IDisposable? candidate=null;
        if(string.IsNullOrEmpty(token)||Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES")!="1")return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var method=RecipeType().GetMethod("EnterCommitScope",BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly,null,new[]{typeof(string),typeof(CancellationToken)},null);
            if(method?.ReturnType!=typeof(IDisposable))throw new MissingMethodException("Recipe publication guard signature differs.");
            candidate=method.Invoke(null,new object[]{token,cancellationToken}) as IDisposable;
            if(candidate==null)throw new InvalidOperationException("Recipe guard was not returned.");
            // This is reentrant on our owned locks. Do not do an uncancellable
            // pre-validation before entering the cancellation-aware guard.
            if(!TryValidateRecipeScope(token,true,out _,_=>{}))throw new InvalidOperationException("Returned guard lacks the exact host/sealed scope proof.");
            cancellationToken.ThrowIfCancellationRequested();
            lease=candidate;candidate=null;return true;
        }
        catch(Exception error){candidate?.Dispose();candidate=null;ReportRecipe(report,error);return false;}
        finally{candidate?.Dispose();}
    }
    private static bool RecipeScopeValid(JsonElement root)=>root.GetProperty("schema").GetString()=="aave.runtime-recipes.v2"
        &&root.GetProperty("valid").GetBoolean()&&root.GetProperty("knownHostBaseline").GetBoolean()
        &&root.GetProperty("policy").GetString()==ExpectedRecipePolicy
        &&root.GetProperty("evidenceKind").GetString()==ExpectedRecipeEvidenceKind
        &&root.GetProperty("policySha256").GetString() is {Length:64} policyHash&&policyHash.All(Uri.IsHexDigit)
        &&(!root.GetProperty("sealed").GetBoolean()||root.GetProperty("recipesSha256").GetString() is {Length:64} recipesHash&&recipesHash.All(Uri.IsHexDigit))
        &&!root.GetProperty("preexistingCoverage").GetBoolean()&&!root.GetProperty("allCodeCoverage").GetBoolean()&&!root.GetProperty("permitsStrictExport").GetBoolean();
    private static Type RecipeType()
    {
        var assemblies=AppDomain.CurrentDomain.GetAssemblies().Where(a=>AssemblySimpleNameCache.Get(a)=="AAVideoExport.RuntimeBootstrap").ToArray();
        if(assemblies.Length!=1)throw new InvalidOperationException("Exactly one recipe bootstrap is required.");
        if(assemblies[0].IsDynamic||string.IsNullOrWhiteSpace(assemblies[0].Location))throw new NotSupportedException("Recipe bootstrap must have a verifiable loaded payload.");
        var type=assemblies[0].GetType("AAVideoExport.RuntimeBootstrap.RuntimeRecipeV2",true)!;
        const BindingFlags flags=BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly;
        if(!type.IsPublic||!type.IsAbstract||!type.IsSealed||type.GetProperty("ProtocolVersion",flags)?.GetValue(null) is not int version||version!=2
            ||type.GetProperty("IsInstalled",flags)?.GetValue(null) is not bool installed||!installed)throw new NotSupportedException("Recipe protocol/observation is unavailable.");
        return type;
    }
    private static bool TryRecipeCall(string name,Type[] parameters,object[] values,out string json,Action<string> report)
    {
        json="";if(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES")!="1")return false;
        try{var type=RecipeType();var method=type.GetMethod(name,BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly,null,parameters,null);
            if(method?.ReturnType!=typeof(string))throw new MissingMethodException("Recipe API signature differs: "+name);json=(string)method.Invoke(null,values)!;return true;}
        catch(Exception error){ReportRecipe(report,error);return false;}
    }
    private static void ReportRecipe(Action<string> report,Exception error)
    {try{report("AA Video Export: recipe scope unavailable: "+error.GetBaseException().Message);}catch{/* Diagnostics cannot alter host behavior. */}}

    internal static bool TryMark(string marker, Action<string> report)
    {
        if (Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION") != "1") return false;
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => AssemblySimpleNameCache.Get(assembly) == "AAVideoExport.RuntimeBootstrap").ToArray();
            if (assemblies.Length != 1) throw new InvalidOperationException("Exactly one early observer assembly is required.");
            var type = assemblies[0].GetType("AAVideoExport.RuntimeBootstrap.RuntimeAttributionV1", throwOnError: true)!;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            if (!type.IsPublic || !type.IsAbstract || !type.IsSealed
                || type.GetProperty("ProtocolVersion", flags)?.GetValue(null) is not int version || version != 1
                || type.GetProperty("IsInstalled", flags)?.GetValue(null) is not bool installed || !installed)
                throw new InvalidOperationException("Early observer protocol or active state is unavailable.");
            var method = type.GetMethod("Mark", flags, null, new[] { typeof(string) }, null);
            if (method == null || method.ReturnType != typeof(void))
                throw new MissingMethodException("Early observer Mark(string) contract is unavailable.");
            method.Invoke(null, new object[] { marker });
            return true;
        }
        catch (Exception error)
        {
            var cause = error is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException! : error;
            try { report("AA Video Export: early observer marker unavailable: " + cause.GetType().Name + ": " + cause.Message); }
            catch { /* Optional diagnostics must not prevent AAVE startup. */ }
            return false;
        }
    }
}
