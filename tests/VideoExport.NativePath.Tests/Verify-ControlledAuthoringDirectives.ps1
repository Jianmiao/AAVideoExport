param([string]$MoreEffectsDirectory='F:/AzureArchive_101/mods/更多的画面效果/1.6.7', [string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $MoreEffectsDirectory 'AzureArchive.VideoTools.Core.dll'))
$type=$assembly.GetType('AzureArchive.VideoTools.Core.Commands.EmbeddedAavtDirectiveExtractor',$true)
$parser=[Activator]::CreateInstance($type)
$presetType=$assembly.GetType('AzureArchive.VideoTools.Core.Characters.CharacterPresetDirectiveParser',$true)
$presetParser=[Activator]::CreateInstance($presetType)
$red=@(foreach($preset in @('sway','spin','squash')){
    $prompt='#aavt;fx;3;'+$preset+';duration=600'
    $parsed=$type.GetMethod('Extract').Invoke($parser,@($prompt,$false))
    if($parsed.Errors.Count -eq 0 -or $parsed.Commands.Count -ne 0){throw ('Invalid legacy fixture input was unexpectedly accepted: '+$preset)}
    [pscustomobject]@{Preset=$preset;Input=$prompt;Rejected=$true;Errors=@($parsed.Errors)}
})
$definitions=@(
    @{Preset='sway';Prompt="#aavt;voice;SE_BatSwing_01`n#aavt;fx;3;sway;amplitude=12;frequency=2;cycles=1"},
    @{Preset='spin';Prompt='#aavt;fx;3;spin;frequency=2;cycles=1;axis=y'},
    @{Preset='headbutt';Prompt="#aavt;voice;SE_BatSwing_01`n#aavt;fx;3;headbutt;duration=600"},
    @{Preset='squash';Prompt="#aavt;voice;SE_BatSwing_01`n#aavt;fx;3;squash;amplitude=0.15;frequency=2;cycles=1"}
)
$green=@(foreach($definition in $definitions){
    $parsed=$type.GetMethod('Extract').Invoke($parser,@($definition.Prompt,$false))
    if($parsed.Errors.Count -ne 0 -or $parsed.Commands.Count -ne 1){throw ('Actual installed parser rejected '+$definition.Preset+': '+($parsed.Errors -join ' | '))}
    $command=$presetType.GetMethod('Parse').Invoke($presetParser,@($parsed.Commands[0].CanonicalDirective))
    if(!$command.Success -or ($definition.Preset -eq 'spin' -and $command.Value.SpinAxis.ToString() -ne 'Y')){throw 'Canonical command did not retain the accepted Y-axis spin branch.'}
    [pscustomobject]@{Preset=$definition.Preset;Input=$definition.Prompt;Canonical=$parsed.Commands[0].CanonicalDirective;SpinAxis=$(if($definition.Preset -eq 'spin'){$command.Value.SpinAxis.ToString()}else{$null});Passed=$true}
})
if($EvidenceDirectory){
    $target=[IO.Path]::GetFullPath($EvidenceDirectory);New-Item -ItemType Directory -Path $target -Force|Out-Null
    $red|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $target 'installed-directive-parser-red.json') -Encoding utf8
    $green|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $target 'installed-directive-parser-green.json') -Encoding utf8
}
[pscustomobject]@{InvalidDurationCasesRejected=$red.Count;ValidFullAdditionalPrompts=$green;Scope='Actual installed pure-managed parser only; no preset execution or AA launch.'}|ConvertTo-Json -Depth 6
