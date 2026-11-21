using System.Collections.ObjectModel;

namespace AAVideoExport.RuntimeBootstrap;

// Frozen audited outputs: historical real factories plus direct production/native
// consumer dependencies. Never populate this manifest from runtime directory scans.
internal static class InteropImageManifest
{
    internal const string Revision = "aa101-story-render-resource-explicit22-20261030.v1";
    internal sealed record ImagePin(string Sha256,string Mvid,string Version);
    private static readonly IReadOnlyDictionary<string,ImagePin> Images = new ReadOnlyDictionary<string,ImagePin>(
        new Dictionary<string,ImagePin>(StringComparer.Ordinal)
        {
            ["Assembly-CSharp"] = new("D95062F3A49FBA645986DC7FE0CEE749197C9D0C84FF381FC3CA2D9D2823C013","96dd2e04-7746-4d77-a090-f5c1ec0135db","0.0.0.0"),
            ["Google.FlatBuffers"] = new("780191D7C243192F822FF17E8CD229DD5CBB3933E1961C8E056A4D5CCB28F4ED","14c3a961-6c51-4333-8b8b-3872ee01c951","1.0.0.0"),
            ["Il2CppSystem.Core"] = new("444D4B53EAAAC6C754D89FAAB912C0F09E0351E400412269BC23642B169DA156","9b80ca49-8b59-476d-b829-2e0c57efc407","4.0.0.0"),
            ["Il2Cppmscorlib"] = new("07AC56BC73CAFAAF853010A1A2418BEB47CEAA792C2CEC0FC8692DB65F90491A","c540a200-d916-440e-a98d-0b5d10f78d98","4.0.0.0"),
            ["Newtonsoft.Json"] = new("B7130E45DA18BE0C23E09D8BEEA755A51F5FD612DB195B1578B8E9A34101760F","f942e9af-2a42-49d3-8efc-46a5f2b57be4","13.0.0.0"),
            ["Unity.RenderPipelines.Universal.Runtime"] = new("1E475E8B56AA14DC8D1DA3F389DA9DBAEAF9FC77F2ADA616945FBF3BD1CD1FA4","2fb76294-e4b8-40e7-b182-1f949cabaeab","0.0.0.0"),
            ["UnityEngine.AnimationModule"] = new("7BA489B1BBDD932EA2BDDC7EBE7565D1AAE90FA460FBCED2C0B82DB1002E676E","21596cb7-7b19-4c74-b5cb-7c9dd576f04b","0.0.0.0"),
            ["UnityEngine.AssetBundleModule"] = new("BE463FCBBCDD68D3228ED24ADA27284B0FF0424C582A5AA718796DF331FC213F","68c715d3-8eeb-44bc-baaa-241148e2f960","0.0.0.0"),
            ["UnityEngine.AudioModule"] = new("A267F271C26E2072B46318C5F537185D5C09AD618D5EBC50D81BF14A0FDBEB07","1b5e83e2-1650-463c-b205-0aa5885686a5","0.0.0.0"),
            ["UnityEngine.CoreModule"] = new("FD65570BC81061E45FD9CE3BD162CD5839E0EFB8F4D76F6902EAD8232CD69E69","1081b610-9c9f-481b-b184-fbcd5a221a5b","0.0.0.0"),
            ["UnityEngine.ImageConversionModule"] = new("C1DE4B14B17C619489C155E2F30D3796BB65115E1CB8A4956B264ECB65A63E42","84e40a1f-15b2-42c4-97f3-9d57d6ce195e","0.0.0.0"),
            ["UnityEngine.InputLegacyModule"] = new("35E5C2DE4EBF07A53245F4387D879FD0591BA9F4020E637AB2AA76B1455A1D28","b101110d-d087-41b0-92b0-43fa5d24b466","0.0.0.0"),
            ["UnityEngine.ParticleSystemModule"] = new("4B3B1811A2C6B2252F3537F2E313B5D76733F078E8A0DD64BD1631FDFE8AF05B","e0475e49-253f-48e2-9f54-b3fb74979575","0.0.0.0"),
            ["UnityEngine.PhysicsModule"] = new("BA019D8B957E1003FE70633E0CAA23CFF3A12C6EB876B6F35ADA93F367CB5E3A","c320a9bf-0cb4-4aa4-a8af-341779ecdc30","0.0.0.0"),
            ["UnityEngine.TextRenderingModule"] = new("47950E3CB970EE1692FD95F319427655DCA08D806B63B49C4DEC8952794BD2D3","cf232ce8-7f47-4537-8697-2f05abb2b946","0.0.0.0"),
            ["UnityEngine.UI"] = new("D8268787B9DF6AF471D9AD97BF7AA9ED80C9E595F4BFDB29FDF25495EB17E341","5878987e-af63-4020-a7e6-dc5a38fef23b","1.0.0.0"),
            ["UnityEngine.UIModule"] = new("82A0941984C4E2CED9CA822AE577E8492B96F70ECC2302315DEECBE5E9192A9B","061c88f7-e770-48f9-951d-ef1df347fc52","0.0.0.0"),
            ["UnityEngine.UnityWebRequestModule"] = new("C7082A228C543B3869F2C233590AC0CAE6243F2F1FEF5D291812FE1337B37F2D","05d528e2-c427-4835-b58e-dce0de4ccfb9","0.0.0.0"),
            ["UnityEngine.UnityWebRequestAudioModule"] = new("157F9E1585767DA1FF998D135DA7E1D23C3A22B29ED3590106226AA9C7527D0C","674424bd-ea1f-4bf2-9971-6843edf6291f","0.0.0.0"),
            ["UnityEngine.VideoModule"] = new("C0C9E71655B0E7882D128F47D728D999B397A881575EA57506DAF1C5E86387B7","292b9ac0-fbe1-410e-bed7-30ee2aca5f1d","0.0.0.0"),
            ["spine-csharp"] = new("91F09E4315E4DC263864DD0EC20531FD4C29291A2F4AB2195B315A229A33D096","cfdeff4a-9ce4-46d5-bddf-a07ac279fe38","0.0.0.0"),
            ["spine-unity"] = new("24A429A2CCAF22E76DD94BC2BC026E003275D6D069B148EF94F0BA76ED71E852","5860369d-d698-4d3c-bc16-f31b9c05829f","0.0.0.0"),
        });
    // The generation cache marker is freshness context only. Exact output pins
    // above, actual loaded metadata and per-type final-IL/constructor proof remain mandatory.
    internal static readonly IReadOnlyDictionary<string,string> BaselineFiles = new ReadOnlyDictionary<string,string>(
        new Dictionary<string,string>(StringComparer.Ordinal)
        {
            ["BepInEx/core/Il2CppInterop.Generator.dll"]="95364BF2686F50D114784E482A0E78C472F6A649D07EA31A075328B2B756899D",
            ["BepInEx/core/Cpp2IL.Core.dll"]="A7AE866298472BFA5176C1B5B71ADFA9FE6E2F1DEEF7B8D50F063C0E2C0BA258",
            ["AzureArchive_Data/il2cpp_data/Metadata/global-metadata.dat"]="B4AEEE165E7E10E0F46F85E095FD94487FDB041215F6DCB7AE9DE5D28A364F43",
            ["BepInEx/interop/assembly-hash.txt"]="E93B46667AF8387BA6A203DF50EA6D2E5E81516004D42B48683175734350A09E",
        });
    internal static int Count => Images.Count;
    internal static bool Contains(string name) => Images.ContainsKey(name);
    internal static bool TryGet(string name,out ImagePin? image) => Images.TryGetValue(name,out image);
    internal static string IdentityText => Revision+"\n"
        +string.Join("\n",Images.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+"="+p.Value.Sha256+"|"+p.Value.Mvid+"|"+p.Value.Version))+"\n"
        +string.Join("\n",BaselineFiles.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+"="+p.Value));
}
