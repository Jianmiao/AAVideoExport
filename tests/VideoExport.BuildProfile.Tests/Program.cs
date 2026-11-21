using AAVideoExport.Core;
bool expected=args.Contains("--expect-audit");
bool expectedSinglePass=!expected || args.Contains("--expect-single-pass");
Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES","1");
Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION","1");
if(ExportBuildProfile.DevelopmentAuditEnabled!=expected)
{
    Console.WriteLine($"FAIL build audit mode: expected={expected}, actual={ExportBuildProfile.DevelopmentAuditEnabled}; ambient audit flags cannot promote production");
    return 1;
}
Console.WriteLine($"PASS build audit mode={expected}; ambient runtime flags do not select the production audit path");
bool singlePass=typeof(ExportBuildProfile).GetProperty("SinglePassEnabled")?.GetValue(null) is true;
if(singlePass!=expectedSinglePass)
{
    Console.WriteLine($"FAIL single-pass build selection: expected={expectedSinglePass}, actual={singlePass}");
    return 1;
}
Console.WriteLine($"PASS single-pass build selection={singlePass}");
return 0;
