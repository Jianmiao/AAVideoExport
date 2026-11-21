param([string]$AAInstallPath = 'F:/AzureArchive_101')
$ErrorActionPreference = 'Stop'
$provider = Join-Path $AAInstallPath 'mods/人物配音支持/1.6.8/Rukari.CharacterVoice.dll'
$expectedProvider = '06C4FE4F87282258FE45A8436FCDDBDCA079C58ED6356C31391F5BC3D855C054'
if ((Get-FileHash -LiteralPath $provider -Algorithm SHA256).Hash -ne $expectedProvider) { throw 'Actual CharacterVoice binary differs from the scoped adapter.' }
$cecil = Join-Path $AAInstallPath 'BepInEx/core/Mono.Cecil.dll'
if (!(Test-Path -LiteralPath $cecil)) { $cecil = Join-Path $PSScriptRoot '../../tools/RuntimeAttributionProbe/bin/Release/net6.0/Mono.Cecil.dll' }
Add-Type -Path $cecil
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($provider)
$stream = [IO.File]::OpenRead($provider)
$pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
try {
    if ($assembly.MainModule.Mvid -ne [guid]'3c455a03-837f-40d5-a719-281df650be68') { throw 'Actual CharacterVoice MVID differs.' }
    $type = $assembly.MainModule.Types | Where-Object FullName -eq 'Rukari.CharacterVoice.Runtime.VoicePlaybackRuntime'
    $targets = @{
        SetVoicePrefix='AE92DDFA6FFB588F4DC42C9985155D93DE211F4D7CAF70B591D7C6DD75E9C30B'
        ObserveOwnedPlayback='7CC3076FD0E821A000A7D11B827CF4864E4CBA66BF3E72A8F9DA3215DB91F5CA'
        TickOnMainThread='3789D8EBA2C4B3528B8ACB7B947FC1C051215F2C4F6DDB5A0C75D74552D7CFF2'
        ResetOnMainThread='399E8880C90887FC475F3F30A13FA79845D33E62058C0216C109F7AF86B52F1E'
    }
    foreach ($name in ($targets.Keys | Sort-Object)) {
        $methods = @($type.Methods | Where-Object Name -eq $name)
        if ($methods.Count -ne 1 -or !$methods[0].IsStatic) { throw ('Unexpected runtime method shape: ' + $name) }
        $method = $methods[0]
        $body = [System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody($pe, $method.RVA)
        $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($body.GetILBytes()))
        if ($actual -ne $targets[$name]) { throw ('Actual runtime IL differs: ' + $name) }
        [pscustomobject]@{ Method=$name; Signature=$method.FullName; ILBytes=$body.GetILBytes().Length; Sha256=$actual; Passed=$true } | ConvertTo-Json -Compress
    }
    'RESULT 4 native runtime metadata targets passed; 0 failed; no Runtime initialization or AA launch'
}
finally { $pe.Dispose(); $stream.Dispose(); $assembly.Dispose() }
