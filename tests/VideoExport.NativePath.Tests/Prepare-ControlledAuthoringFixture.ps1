param(
    [Parameter(Mandatory=$true)][string]$OutputWorkspace,
    [Parameter(Mandatory=$true)][string]$TemplateProject,
    [Parameter(Mandatory=$true)][string]$SourceFixture,
    [string]$Ffmpeg = 'E:/ffmpeg/bin/ffmpeg.exe',
    [string]$Ffprobe = 'E:/ffmpeg/bin/ffprobe.exe',
    [string]$StoryName = 'AAVE-OfficialAuthoring-20261030'
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($OutputWorkspace)
$template=(Resolve-Path -LiteralPath $TemplateProject).Path
$source=(Resolve-Path -LiteralPath $SourceFixture).Path
if(Test-Path -LiteralPath $root){throw 'Use a new independent workspace; this helper never overwrites one.'}
if([IO.Path]::GetExtension($template) -ne '.aap2'){throw 'An actual native AAP2 template is required.'}
$before=(Get-FileHash -LiteralPath $template -Algorithm SHA256).Hash
$projectDir=Join-Path $root 'data/projects'
$bgs=Join-Path $root 'data/overrides/bgs'
$sounds=Join-Path $root 'data/overrides/sounds'
foreach($path in @($projectDir,$bgs,$sounds,(Join-Path $root 'data/saves'))){New-Item -ItemType Directory -Path $path -Force|Out-Null}
$copy=Join-Path $projectDir ($StoryName+'.aap2')
Copy-Item -LiteralPath $template -Destination $copy
$sourceVoice=Join-Path $source 'data/overrides/sounds/SE_BatSwing_01.wav'
Copy-Item -LiteralPath $sourceVoice -Destination (Join-Path $sounds 'SE_BatSwing_01.wav')
$assets=[Collections.Generic.List[object]]::new()
foreach($name in @('controlled-bg','controlled-bg-2')){
    $input=Join-Path $source ('data/overrides/bgs/'+$name+'.mp4')
    $output=Join-Path $bgs ($name+'.mp4')
    & $Ffmpeg -nostdin -v error -i $input -vf 'scale=128:64:flags=neighbor,setsar=1' -an -c:v libx264 -profile:v baseline -level:v 3.0 -pix_fmt yuv420p -crf 18 -preset veryfast -movflags +faststart $output
    if($LASTEXITCODE -ne 0){throw 'Could not create baseline H264 fixture video.'}
    $png=Join-Path $bgs ($name+'.png')
    & $Ffmpeg -nostdin -v error -i $output -frames:v 1 $png
    if($LASTEXITCODE -ne 0){throw 'Could not capture native input thumbnail.'}
    $oracle=Join-Path $bgs ($name+'.rgba')
    & $Ffmpeg -nostdin -v error -i $output -an -pix_fmt rgba -f rawvideo $oracle
    if($LASTEXITCODE -ne 0){throw 'Could not decode fixture oracle.'}
    $probe=(& $Ffprobe -v error -count_frames -select_streams v:0 -show_entries 'stream=codec_name,profile,width,height,pix_fmt,r_frame_rate,nb_read_frames' -of json $output|ConvertFrom-Json).streams[0]
    if($probe.width -ne 128 -or $probe.height -ne 64 -or $probe.codec_name -ne 'h264'){throw 'Unexpected native video format.'}
    $assets.Add([ordered]@{Name=$name;Video=$output;VideoSha256=(Get-FileHash $output -Algorithm SHA256).Hash;Thumbnail=$png;ThumbnailSha256=(Get-FileHash $png -Algorithm SHA256).Hash;DecodedRgba=$oracle;DecodedRgbaSha256=(Get-FileHash $oracle -Algorithm SHA256).Hash;Probe=$probe})
}
Copy-Item -LiteralPath (Join-Path $source 'data/overrides/manifest.json') -Destination (Join-Path $root 'data/overrides/manifest.json')
$marker=[ordered]@{Schema=2;Owner='AAVE acceptance';Purpose='Independent official authoring compiler input, not manually assembled compiled ScriptKr';StoryName=$StoryName;TemplateProject=$template;TemplateSha256=$before;CopiedProject=$copy;Videos=$assets;VoiceIdentifier='aavt/voice/SE_BatSwing_01';VoiceFile=(Join-Path $sounds 'SE_BatSwing_01.wav');VoiceSha256=(Get-FileHash (Join-Path $sounds 'SE_BatSwing_01.wav') -Algorithm SHA256).Hash;ExpectedPresets=@('sway','spin','headbutt','squash');ExpectedOwnedVoiceRequests=3}
$marker|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $root 'aave-fixture-workspace.json') -Encoding utf8
if((Get-FileHash -LiteralPath $template -Algorithm SHA256).Hash -ne $before){throw 'The original AAP2 template changed.'}
[pscustomobject]@{Workspace=$root;CopiedProject=$copy;OriginalTemplateUnchanged=$true;VideoSize='128x64';RuntimeCompilationPending=$true}|ConvertTo-Json
