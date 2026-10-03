# Native UI acceptance fixtures (explicit opt-in)

This temporary BepInEx test plugin exercises AA's installed `NativeExportPanel`
and actual IL2CPP `UIInput.Start`. It is not part of the shipped mod and is not
run by `tools/Test-PortableReview.ps1`. Never install this test DLL for normal use.
It requires your own compatible AA/BepInEx installation and generated interop
references. No AA binaries or game resources are distributed here.

Build with the local installation path:

```powershell
$aa = Read-Host 'AA installation folder'
dotnet build tests/VideoExport.NativePath.Tests -c Release "-p:AAInstallPath=$aa"
```

Close AA before installing the fixture. Place the resulting
`AAVideoExport.NativePath.Tests.dll` in a temporary folder under
`BepInEx/plugins` in that installation. Set these environment variables in the
PowerShell session from which you launch AA:

```powershell
$env:AA_VIDEOEXPORT_NATIVE_TESTS = '1'
$env:AA_VIDEOEXPORT_TEST_OUTPUT = Join-Path (Get-Location) 'artifacts/native-acceptance'
& (Join-Path $aa 'AzureArchive.exe') --aa-export-settings
```

Without the explicit enable flag and an absolute diagnostic directory the
fixture stays disabled and adds no components or hooks. Review BepInEx's log for
`PATH-CHECK RESULT`. Close AA and remove the temporary test DLL after testing.
The test is loaded into a real application; use a disposable AA test installation,
with no unsaved work. It does not send mouse or keyboard input.

`--aa-export-settings` creates the production 720p panel and invokes actual NGUI
callbacks: unified video/audio controls, no tabs, inline Advanced expansion,
scrolling to bitrate, retention of partial decimal input, audio dropdown choice
and the 854x480 -> 1280x720 SR plan. It supplies a 2560x1334 test viewport and
checks the 980:800 modal ratio and uniform 90% fit. It captures its own camera to
`single-page-export` beneath the specified diagnostics directory. No story is
loaded and no performance is measured. The viewport hooks disappear when that
AA process exits; they are never included in production assemblies.

`--aa-export-buttons` uses the built-in playback scene with story startup
disabled, checks the menu/auto visibility option, and writes camera captures to
`native-buttons` beneath the diagnostics directory.

With neither mode flag, inactive UI fixtures exercise path entry, picker refresh,
manual editing, UNC spelling without network access, and rejection of actual
control characters. This covers the regression where a literal backslash-n
becomes a line break when native `UIInput.Start` runs. All fixture paths derive
from the configured diagnostics root; there are no assumed developer drives.

The path mode's six-frame synthetic media export is separately disabled by
default. To opt in, set `AA_VIDEOEXPORT_NATIVE_MEDIA=1`, provide FFmpeg/ffprobe on
`PATH` or via `FFMPEG_DIR`, and optionally set `AA_VIDEOEXPORT_TEST_ENCODER`
(default `h264_qsv`, requiring Intel QSV). Its 96x64 output is written to
`native-input-export` under the same diagnostics root. Unsupported hardware is
a failure, not an automatic pass. This tests correctness, not rendering speed.

Screenshots may include locally configured folder names. Review them before
sharing; generated diagnostics and native game resources are not repository files.
