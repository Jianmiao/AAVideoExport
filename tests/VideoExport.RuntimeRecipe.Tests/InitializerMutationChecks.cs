using System.Reflection;
using AAVideoExport.RuntimeBootstrap;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

internal static class InitializerMutationChecks
{
    internal static int Run()
    {
        using var observer=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(typeof(ManagedInitializerWrapper)));
        observer.Install();if(!observer.IsInstalled)throw new Exception(observer.Failure);
        var store=typeof(Il2CppObjectBase).GetNestedType("InitializerStore`1",BindingFlags.NonPublic)!.MakeGenericType(typeof(ManagedInitializerWrapper));
        var value=(Delegate)store.GetProperty("Initializer",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(null)!;
        if(observer.Find(value).Status!="QUALIFIED")throw new Exception("Initial real factory recipe was not qualified");
        var patcher=new Harmony("aave.recipe.cctor-history");var target=typeof(ManagedInitializerWrapper).TypeInitializer!;
        try{patcher.Patch(target,prefix:new HarmonyMethod(typeof(InitializerMutationChecks).GetMethod(nameof(Prefix),BindingFlags.Static|BindingFlags.Public)!));}
        finally{patcher.UnpatchSelf();}
        bool rejected=observer.Find(value).Status!="QUALIFIED";
        Console.WriteLine((rejected?"PASS":"FAIL")+" real factory receipt rejects transient cctor Patch then Unpatch; runtime="+Environment.Version);
        return rejected?0:1;
    }
    public static void Prefix(){}
    public sealed class ManagedInitializerWrapper:Il2CppObjectBase
    {
        public static int Initialized;
        static ManagedInitializerWrapper(){Initialized=1;}
        public ManagedInitializerWrapper(IntPtr pointer):base(pointer){}
    }
}
