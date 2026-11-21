using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using AAVideoExport.Plugin;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using MonoMod.Utils.Cil;

internal static class AssemblyNameMetadataChecks
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Func<Assembly,string?> ReadName = typeof(FrameworkGeneratedAssemblyScope).Assembly
        .GetType("AAVideoExport.Plugin.AssemblySimpleNameCache")?.GetMethod("Get", PrivateStatic)
        ?.CreateDelegate<Func<Assembly,string?>>() ?? (assembly => assembly.GetName().Name);

    internal static int Run()
    {
        int passed=0,failed=0;
        void Test(string name,Action run)
        {try{run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error.GetBaseException().Message);}}
        Test("repeated immutable CLR simple-name reads avoid repeated AssemblyName allocation",()=>
        {
            Assembly actual=typeof(object).Assembly;
            for(int i=0;i<64;i++)_ = ReadName(actual);
            long start=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<1000;i++) if(ReadName(actual)!="System.Private.CoreLib")throw new Exception("Actual CLR name changed.");
            long allocated=GC.GetAllocatedBytesForCurrentThread()-start;
            if(allocated>1024)throw new Exception("Repeated immutable name lookup allocated "+allocated+" bytes.");
        });
        Test("custom Assembly overrides remain live rather than cached",()=>
        {
            var mutable=new MutableAssembly{Name="first"};
            if(ReadName(mutable)!="first")throw new Exception("Initial custom assembly name differed.");
            mutable.Name="second";
            if(ReadName(mutable)!="second")throw new Exception("Mutable override was cached.");
        });
        Test("a dynamic assembly loaded after an earlier miss remains visible",()=>
        {
            string name="AAVE.NameMetadata.Late."+Guid.NewGuid().ToString("N");
            RejectLoaded(name);
            Assembly actual=CreateDynamic(name,AssemblyBuilderAccess.Run);
            if(!ReferenceEquals(Loaded(name),actual))throw new Exception("Late actual assembly was omitted.");
        });
        Test("a second same-name dynamic assembly still makes unique lookup fail",()=>
        {
            string name="AAVE.NameMetadata.Duplicate."+Guid.NewGuid().ToString("N");
            Assembly first=CreateDynamic(name,AssemblyBuilderAccess.Run);
            if(!ReferenceEquals(Loaded(name),first))throw new Exception("Initial assembly was not found.");
            Assembly second=CreateDynamic(name,AssemblyBuilderAccess.Run);
            if(ReferenceEquals(first,second)||ReadName(first)!=ReadName(second))throw new Exception("Duplicate fixture is invalid.");
            RejectLoaded(name);
        });
        Test("cached simple-name metadata does not retain collectible assemblies",()=>
        {
            var weak=CreateCollectible();
            for(int i=0;i<12&&weak.IsAlive;i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
            if(weak.IsAlive)throw new Exception("Name metadata retained the collectible runtime assembly.");
        });
        Console.WriteLine($"RESULT {passed} passed; {failed} failed; assembly-name-only cache contract");
        return failed==0?0:1;
    }

    internal static int Benchmark()
    {
        _=new Harmony("aave.name-metadata.benchmark");_ = ILGeneratorShim.ProxyType;_ = DetourHelper.Runtime;
        Type[] arguments={typeof(int),typeof(long),typeof(double),typeof(float),typeof(short),typeof(byte),typeof(uint),typeof(ulong),typeof(ushort),typeof(sbyte),typeof(bool),typeof(char),typeof(string),typeof(object),typeof(IntPtr),typeof(UIntPtr)};
        int produced=0;
        foreach(Type left in arguments)foreach(Type right in arguments)
        {
            if(produced++>=250)break;
            Type marker=typeof(Marker<,>).MakeGenericType(left,right);
            MethodInfo method=typeof(Target<>).MakeGenericType(marker).GetMethod(nameof(Target<int>.Invoke))!;
            _=DelegateTypeFactory.instance.CreateDelegateType(method,CallingConvention.Cdecl);
        }
        using var scope=FrameworkGeneratedAssemblyScope.Acquire();
        for(int i=0;i<3;i++)scope.Validate();
        var times=new List<double>();long allocated=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<20;i++){long before=Stopwatch.GetTimestamp();scope.Validate();times.Add((Stopwatch.GetTimestamp()-before)*1000d/Stopwatch.Frequency);}
        allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
        Console.WriteLine(JsonSerializer.Serialize(new{Kind="actual-production-framework-validation",Runtime=Environment.Version.ToString(),Iterations=times.Count,
            AssemblyCount=AppDomain.CurrentDomain.GetAssemblies().Length,DeclaredHarmonyDelegates=250,MeanMs=times.Average(),MedianMs=times.OrderBy(value=>value).ElementAt(times.Count/2),
            MinimumMs=times.Min(),MaximumMs=times.Max(),AllocatedBytesPerValidation=allocated/(double)times.Count,Identity=scope.IdentitySha256,
            Scope="Isolated exact .NET 6.0.7 original Harmony producers and full production validation; no live AA process touched."}));
        return 0;
    }
    private static Assembly Loaded(string name)
    {try{return (Assembly)typeof(FrameworkGeneratedAssemblyScope).GetMethod("Loaded",PrivateStatic)!.Invoke(null,new object[]{name})!;}catch(TargetInvocationException error){throw error.InnerException!;}}
    private static void RejectLoaded(string name)
    {try{_=Loaded(name);}catch(InvalidOperationException){return;}throw new Exception("Unique live assembly lookup unexpectedly succeeded.");}
    private static Assembly CreateDynamic(string name,AssemblyBuilderAccess access) => AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name),access)
        .DefineDynamicModule("main").DefineType("Marker",TypeAttributes.Public).CreateType()!.Assembly;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCollectible()
    {var actual=CreateDynamic("AAVE.NameMetadata.Collectible."+Guid.NewGuid().ToString("N"),AssemblyBuilderAccess.RunAndCollect);_ = ReadName(actual);return new WeakReference(actual);}
    private sealed class MutableAssembly:Assembly
    {internal string Name="";public override AssemblyName GetName()=>new(Name);public override AssemblyName GetName(bool copiedName)=>new(Name);}
    public sealed class Marker<T1,T2>{}
    public static class Target<T>{public static IntPtr Invoke(IntPtr value)=>value;}
}
