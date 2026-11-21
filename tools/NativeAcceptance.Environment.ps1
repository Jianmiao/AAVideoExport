#requires -Version 7.2
function Set-AaveNativeAuditEnvironment {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][Diagnostics.ProcessStartInfo]$StartInfo,[switch]$DevelopmentAudit)
    $removed=@($StartInfo.Environment.Keys|Where-Object {$_-match'^AA_VIDEOEXPORT_(RUNTIME_.*|STARTUP_TRACE_FILE|AUDIT.*|DEVELOPMENT_AUDIT)$'}|Sort-Object)
    foreach($name in $removed){[void]$StartInfo.Environment.Remove($name)}
    $StartInfo.Environment['AA_VIDEOEXPORT_TEST_DEVELOPMENT_AUDIT']=if($DevelopmentAudit){'1'}else{'0'}
    if($DevelopmentAudit){$StartInfo.Environment['AA_VIDEOEXPORT_RUNTIME_RECIPES']='1';$StartInfo.Environment['AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION']='1'}
    return [pscustomobject]@{DevelopmentAudit=[bool]$DevelopmentAudit;RemovedAuditVariables=$removed;ChildAuditVariables=@($StartInfo.Environment.Keys|Where-Object {$_-match'^AA_VIDEOEXPORT_(RUNTIME_.*|STARTUP_TRACE_FILE|AUDIT.*|DEVELOPMENT_AUDIT)$'}|Sort-Object)}
}
