using AAVideoExport.Plugin;
internal static class ProductionExecutionCases
{
    internal static int Run()
    {
        int passed=0,failed=0;
        void Test(string name,Action run){try{run();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
        Test("production source lease needs native readiness but no hash or provenance",()=>
        {
            var source=new ExecutionSource();string path=Path.GetFullPath("production.aas");
            using var lease=EffectsSourceInput.AcquireExecution(source,path);lease.ValidateCurrent(true);lease.RevalidateForPass();
            if(source.Prepares!=2||source.ReadyChecks<2||lease.ArchivePath!=path||!lease.Identity.StartsWith("execution:",StringComparison.Ordinal))throw new Exception("Functional source lifecycle was not executed.");
            try{_=lease.ProjectSha256;throw new Exception("A production lease exposed an audit hash.");}catch(InvalidOperationException){}
        });
        Test("production source errors release native resources instead of inventing a proof",()=>
        {
            var source=new ExecutionSource{FailPrepare=true};
            try{EffectsSourceInput.AcquireExecution(source,Path.GetFullPath("production.aas"));throw new Exception("Expected native preparation failure");}
            catch(IOException){}
            if(!source.Disposed)throw new Exception("Failed preparation retained resources.");
        });
        Test("production release ends ownership and late native readiness is rejected",()=>
        {
            var source=new ExecutionSource();var lease=EffectsSourceInput.AcquireExecution(source,Path.GetFullPath("production.aas"));lease.Dispose();
            if(!source.Disposed)throw new Exception("Functional source not released.");
            try{lease.ValidateCurrent(true);throw new Exception("Released input reused");}catch(AAVideoExport.Core.ExportException){}
        });
        Console.WriteLine($"RESULT {passed} passed; {failed} failed; production source readiness, no audit");return failed==0?0:1;
    }
    private sealed class ExecutionSource:IEffectsExecutionSource
    {
        internal int Prepares,ReadyChecks;internal bool FailPrepare,Disposed;
        public string ProjectPath=>Path.GetFullPath("production.aap");
        public void PrepareForPass(string archive){Prepares++;if(FailPrepare)throw new IOException("native source unavailable");}
        public void EnsureReady(){ReadyChecks++;if(Disposed)throw new InvalidOperationException("released");}
        public void Dispose()=>Disposed=true;
    }
}
