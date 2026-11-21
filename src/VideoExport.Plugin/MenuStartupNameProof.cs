using System.Reflection;
using System.Text.Json;

namespace AAVideoExport.Plugin;

internal sealed record MenuStartupConfigEvidence(bool Available, bool BeforeMenuAssemblyLoad, string ConfigPath,
    bool Exists, string RawSha256, bool HasDisplayName, string DisplayName);
internal sealed class MenuStartupNameProof
{
    internal static MenuStartupConfigEvidence ReadEarly(string configPath)
    {
        var assemblies=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="AAVideoExport.RuntimeBootstrap").ToArray();
        if(assemblies.Length!=1||assemblies[0].IsDynamic||string.IsNullOrWhiteSpace(assemblies[0].Location))throw new InvalidOperationException("Early menu configuration snapshot provider is unavailable.");
        var type=assemblies[0].GetType("AAVideoExport.RuntimeBootstrap.StartupConfigSnapshot",true)!;
        const BindingFlags flags=BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly;
        if(!type.IsPublic||!type.IsAbstract||!type.IsSealed||type.GetProperty("ProtocolVersion",flags)?.GetValue(null) is not int version||version!=1)
            throw new InvalidOperationException("Early menu configuration snapshot protocol differs.");
        var method=type.GetMethod("GetSnapshotJson",flags,null,new[]{typeof(string)},null);
        if(method?.ReturnType!=typeof(string))throw new MissingMethodException("Startup config snapshot signature differs.");
        using var json=JsonDocument.Parse((string)method.Invoke(null,new object[]{Path.GetFullPath(configPath)})!);var root=json.RootElement;
        if(root.GetProperty("schema").GetString()!="aave.startup-menu-config.v1"||!root.GetProperty("available").GetBoolean()||!root.GetProperty("beforeMenuAssemblyLoad").GetBoolean())
            throw new InvalidOperationException("Early menu configuration evidence unavailable: "+root.GetProperty("reason").GetString());
        return new(true,true,root.GetProperty("configPath").GetString()!,root.GetProperty("exists").GetBoolean(),root.GetProperty("rawSha256").GetString()!,
            root.GetProperty("hasDisplayName").GetBoolean(),root.GetProperty("displayName").GetString()!);
    }
    internal static void Validate(MenuStartupConfigEvidence startup, string configPath, string assemblyPath,
        string sourceDirectory, string modsRoot, string version, string runtimeName, string boundName,
        bool currentHasName, string currentName, DateTime processStartedUtc)
    {
        if (!startup.Available || !startup.BeforeMenuAssemblyLoad)
            throw new InvalidOperationException("Menu BGM has no configuration evidence from before its assembly load.");
        bool Same(string left,string right)=>string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),StringComparison.OrdinalIgnoreCase);
        if(!Same(startup.ConfigPath,configPath))throw new InvalidOperationException("Menu BGM startup evidence belongs to another profile configuration.");
        string actualSource=Path.GetDirectoryName(Path.GetFullPath(assemblyPath))??throw new InvalidOperationException("Menu BGM assembly path has no directory.");
        string actualMod=Path.GetDirectoryName(actualSource)??throw new InvalidOperationException("Menu BGM assembly path has no Mod directory.");
        string actualMods=Path.GetDirectoryName(actualMod)??throw new InvalidOperationException("Menu BGM assembly path has no mods root.");
        string installedName=Path.GetFileName(actualMod);
        if(Path.GetFileName(assemblyPath)!="MenuBgmSelector.dll"||Path.GetFileName(actualMods)!="mods"||!Same(sourceDirectory,actualSource)||!Same(modsRoot,actualMods)
            ||runtimeName!=installedName||version!=Path.GetFileName(actualSource))throw new InvalidOperationException("Menu BGM runtime install identity differs from its actual loaded DLL directory.");
        string configDirectory=Path.GetDirectoryName(Path.GetFullPath(configPath))!,profileDirectory=Path.GetDirectoryName(configDirectory)!;
        if(Path.GetFileName(configDirectory)!="configs"||!Same(Path.GetDirectoryName(profileDirectory)!,Path.Combine(Path.GetDirectoryName(actualMods)!,"profiles")))
            throw new InvalidOperationException("Menu BGM configuration is outside the actual installation's profile tree.");
        if(startup.Exists && (startup.RawSha256.Length!=64||!startup.RawSha256.All(Uri.IsHexDigit)) || !startup.Exists&&startup.HasDisplayName)
            throw new InvalidOperationException("Menu BGM startup evidence has inconsistent existence/hash fields.");
        bool NoRename(bool has,string value)=>!has||value.Trim().Length==0||value.Trim()==installedName;
        if(!NoRename(startup.HasDisplayName,startup.DisplayName))throw new InvalidOperationException("Menu BGM initial DisplayName requested a rename: "+startup.DisplayName);
        if(!NoRename(currentHasName,currentName))throw new InvalidOperationException("Menu BGM current disk DisplayName requests a rename: "+currentName);
        if(!NoRename(true,boundName))throw new InvalidOperationException("Menu BGM currently bound DisplayName requests a rename: "+boundName);
        // ConfigFile.Bind saves even an unchanged value. Modification times,
        // comments and equivalent serialization are deliberately not evidence.
    }
    internal static (bool Found,string Name) ReadCurrent(string path)
    {
        if(!File.Exists(path))return(false,"");
        if(new FileInfo(path).Length>65536)throw new InvalidDataException("Menu configuration exceeds its bounded input size.");
        string section="",name="";bool found=false;
        foreach(string line in File.ReadLines(path,new System.Text.UTF8Encoding(false,true)))
        {
            string value=line.Trim();if(value.Length==0||value.StartsWith('#'))continue;
            if(value.StartsWith('[')){if(!value.EndsWith(']'))throw new InvalidDataException("Ambiguous config section.");section=value[1..^1].Trim();continue;}
            int equals=value.IndexOf('=');if(equals<=0||section!="Name"||value[..equals].Trim()!="DisplayName")continue;
            if(found)throw new InvalidDataException("Duplicate Name.DisplayName is ambiguous.");found=true;name=value[(equals+1)..].Trim();
            if(name.Contains('\\')||name.Contains('"')||name.Any(char.IsControl))throw new InvalidDataException("Unsupported display-name escape syntax.");
        }
        return(found,name);
    }
}
