namespace AAVideoExport.Plugin;

// Monotonic retry scheduling. State keys describe semantic inputs, never mtime
// alone, and reporting is separate from a retry so one failure is not spammed.
internal sealed class RetainedInstallSchedule
{
    private double nextProbe, nextAttempt;
    private string state="", reported="";
    private bool succeeded;
    private int failures;
    internal bool ShouldProbe(double now)
    {
        if(now<nextProbe)return false;
        nextProbe=now+1;return true;
    }
    internal bool ShouldAttempt(double now, string current)
    {
        if(current!=state){state=current;succeeded=false;failures=0;nextAttempt=now;}
        return !succeeded&&now>=nextAttempt;
    }
    internal bool Failed(double now, string reason)
    {
        succeeded=false;failures=Math.Min(failures+1,6);nextAttempt=now+Math.Min(30,Math.Pow(2,failures-1));
        if(reported==reason)return false;reported=reason;return true;
    }
    internal void Succeeded(){succeeded=true;failures=0;reported="";}
}
