using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

// Read the installed PE metadata without loading/initializing another Mod.
string aa = args.Length > 0 ? Path.GetFullPath(args[0]) : @"F:\AzureArchive_101";
var cases = new[]
{
    new Spec(@"mods/可更改的主菜单音乐/1.7.3/MenuBgmSelector.dll", "MenuBgmSelector", new Version(1,1,4,0),
        new Guid("4e7ca964-4de8-4d46-8301-03a0b29cb1cb"), "5C56D01047FC6CABE7DAD3D51D95D733A8620FD03DC5A50041BC61ACC5CB98D4",
        new[] { "AzureArchive.MenuBgmSelector.BgmStore:_loaded,_cur", "AzureArchive.MenuBgmSelector.AudioFileLoader:_pending,_cache", "AzureArchive.MenuBgmSelector.ModNameSwitch:_currentName,_srcDir,_modsRoot,_version", "AzureArchive.MenuBgmSelector.BgmSelection:Kind,Id,FilePath,Display" },
        new[] { "AzureArchive.MenuBgmSelector.BgmPatches:HandleId,PlayBgmTaskPrefix,FrameTick", "AzureArchive.MenuBgmSelector.AudioFileLoader:Request,Poll", "AzureArchive.MenuBgmSelector.ModNameSwitch:Apply,WriteTextPreserving", "AzureArchive.MenuBgmSelector.AudioFilePicker:ShowDialog" }),
    new Spec(@"mods/AA界面美化/0.0.19/AAMenuPrototype.dll", "AAMenuPrototype", new Version(0,0,19,0),
        new Guid("47739a97-91ed-4450-9928-e6b53bf3619e"), "1610E896FDD738DAED88EF418B5D037118D7B4A2D02B1149C7CFC4F4BFF1A589",
        new[] { "AAMenuPrototype.MenuEntryHooks:Owner", "AAMenuPrototype.MenuBehaviour:_done,Probe,_menu,_animation,_backgroundCamera,_target,_portrait,_voiceSource,_voiceRequest,_directBackgroundPanel,_voiceQueue,_dialogueQueue,_activeFx,_callbacks,_entryCameras,_restoreNative,_directMaterials,_dialogueRunning,_directRendering" },
        new[] { "AAMenuPrototype.MenuBehaviour:Update,LateUpdate,DestroySceneObjects,StopMenuInteraction,StopMenuSpeech", "AAMenuPrototype.MenuEntryHooks:BeforeCatalogStart" }),
    new Spec(@"mods/音效汉化/1.7.3/SfxFolderMod.dll", "SfxFolderMod", new Version(1,7,3,0),
        new Guid("5da3b0cd-f4f3-421e-bc88-3ee5d6206c87"), "29CA0E83813704E07FE610060B177A85AB6B28EAB7FD9805E796D85B5D8B25D3",
        new[] { "AzureArchive.SfxFolderMod.SfxData:_loaded,_byKey,ShowId,_dir", "AzureArchive.SfxFolderMod.SfxPatches:_installed,_h", "AzureArchive.SfxFolderMod.SfxSearch:Explorer", "AzureArchive.SfxFolderMod.SfxEntry:Id,Cn" },
        new[] { "AzureArchive.SfxFolderMod.SfxPatches:OnNames,Decorate", "AzureArchive.SfxFolderMod.SfxData:LoadConfig,ChineseOf" })
};
int passed = 0, failed = 0;
foreach (var spec in cases)
{
    try
    {
        string path = Path.Combine(aa, spec.Path);
        using var stream = File.OpenRead(path); using var pe = new PEReader(stream); var md = pe.GetMetadataReader();
        var definition = md.GetAssemblyDefinition();
        Check(md.GetString(definition.Name) == spec.Name && definition.Version == spec.Version, "version/name changed");
        Check(md.GetGuid(md.GetModuleDefinition().Mvid) == spec.Module, "MVID changed");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == spec.Hash, "payload SHA changed");
        var types = md.TypeDefinitions.Select(handle => md.GetTypeDefinition(handle)).Where(type => type.GetDeclaringType().IsNil)
            .ToDictionary(type => md.GetString(type.Namespace) + "." + md.GetString(type.Name));
        foreach (string shape in spec.Fields)
        {
            string[] parts = shape.Split(':'); var type = types[parts[0]];
            var fields = type.GetFields().Select(handle => md.GetString(md.GetFieldDefinition(handle).Name)).ToHashSet(StringComparer.Ordinal);
            foreach (string name in parts[1].Split(',')) Check(fields.Contains(name), "missing field " + parts[0] + "." + name);
        }
        foreach (string shape in spec.Methods)
        {
            string[] parts = shape.Split(':'); var type = types[parts[0]];
            var methods = type.GetMethods().Select(handle => md.GetMethodDefinition(handle)).ToArray();
            foreach (string name in parts[1].Split(','))
                Check(methods.Count(method => md.GetString(method.Name) == name && method.RelativeVirtualAddress != 0) == 1,
                    "missing/ambiguous managed callback " + parts[0] + "." + name);
        }
        passed++; Console.WriteLine("PASS exact installed payload and retained binding shape: " + spec.Name);
    }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + spec.Name + ": " + error.Message); }
}
Console.WriteLine($"RESULT: {passed} passed; {failed} failed. Metadata only; no AA/native lifecycle execution.");
return failed == 0 ? 0 : 1;
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
sealed record Spec(string Path, string Name, Version Version, Guid Module, string Hash, string[] Fields, string[] Methods);
