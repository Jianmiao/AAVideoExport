# Native UI acceptance fixtures (explicit opt-in)

This temporary BepInEx test plugin exercises AA's installed `NativeExportPanel`
and actual IL2CPP `UIInput.Start`. It is not part of the shipped mod and is not
run by `tools/Test-PortableReview.ps1`. Never install this test DLL for normal use.
It requires your own compatible AA/BepInEx installation and generated interop
references. No AA binaries or game resources are distributed here.

Build with the local installation path:

```powershell
$aa = Read-Host 'AA installation folder'
dotnet build src/VideoExport.Plugin -c Release "-p:AAInstallPath=$aa"
$candidate = (Resolve-Path 'src/VideoExport.Plugin/bin/Release/net6.0').Path
dotnet build tests/VideoExport.NativePath.Tests -c Release "-p:AAInstallPath=$aa" "-p:AAVideoExportAssemblyDirectory=$candidate"
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
Use the same candidate Plugin/Core DLLs in that test installation. Older installed
Core versions do not expose the timestamped audio-window API used by the new probe.

`--aa-export-settings` creates the production 720p panel and invokes actual NGUI
callbacks: unified video/audio controls, no tabs, inline Advanced expansion,
scrolling to bitrate, retention of partial decimal input, audio dropdown choice
and the 854x480 -> 1280x720 SR plan. It supplies a 2560x1334 test viewport and
checks the 980:800 modal ratio and uniform 90% fit. It captures its own camera to
`single-page-export` beneath the specified diagnostics directory. No story is
loaded and no performance is measured. The fixture scrolls the actual settings
list before opening Advanced, matching the production hit-target visibility
rules. The viewport hooks disappear when that AA process exits; they are never
included in production assemblies.

`--aa-export-buttons` uses the built-in playback scene with story startup
disabled, checks the menu/auto visibility option, and writes camera captures to
`native-buttons` beneath the diagnostics directory.

With neither mode flag, UI fixtures exercise path entry, picker refresh,
manual editing, UNC spelling without network access, and rejection of actual
control characters. This covers the regression where a literal backslash-n
becomes a line break when native `UIInput.Start` runs. All fixture paths derive
from the configured diagnostics root; there are no assumed developer drives.
Failures include the unwrapped exception message and a redacted path comparison
with normalized equality, character counts, and control-character counts.
The controls start inactive for the native Start check, then become active
offscreen for manual submission. Production intentionally ignores inactive inputs.

`--aa-export-clocks` verifies actual native wait dispatch, fixed unscaled time,
independent global realtime, and RenderControlV1 restoration. It does not render
a story or claim that arbitrary third-party effects follow the frame clock.

`--aa-export-audio-clock` compares native mixer blocks, exact-length requests,
and DSP-scheduled sources using two channels of known pulses. The scheduled mode
also executes production AudioFrameScheduler, TimestampedAudioWindow and
PreparedAudioTrack against actual native DSP timestamps and PCM. Full raw PCM,
lead, content and trailing buffers are retained. `CoreWindowPass` requires exact
content samples, all pulse positions, unchanged raw values and strict replay.
The original capture modes are negative controls and may fail the overall
experiment; a passing scheduled diagnostic is not a completed story export.

`--aa-export-audio-hooks` is a separate, non-rendering boundary probe. It finds
the existing `AudioManager` in `CatalogScene`, refuses to run if its current
`voiceSource` is already playing, swaps in one muted temporary source and
clip, then restores the original source in `finally`. It records the actual
generated interop signatures for `AudioSource.Play`, `PlayOneShot(AudioClip)`,
`Stop`, `Pause`, `UnPause`, `isPlaying`, `timeSamples` get/set, and
`AudioManager.isVoicePlaying`. It verifies managed Harmony prefixes for those
operations and then proves whether `AudioManager.isVoicePlaying` enters the
managed `AudioSource.isPlaying` wrapper: the native getter must still report
the temporary source as playing while the managed wrapper is forced to return
false. This distinction prevents a wrapper-only pass from being treated as
native story compatibility. The probe is diagnostic evidence only; it does not
claim that arbitrary third-party voice gates are frame-clock compatible.

`--aa-export-audio-command` checks same-source DSP reuse and linear volume
mapping against the native renderer. `--aa-export-audio-semantics` separately
uses a synthetic stereo marker clip through the production DSP dispatcher to
measure pitch-scaled marker positions, source volume, pause silence/cursor hold,
and resume-from-cursor behavior in native `AudioRenderer` PCM. It stores the PCM
hash and measured marker/cursor positions under the selected evidence folder.
This is diagnostic only: it does not enable production audio-command hooks,
does not mutate other Mods, and does not certify third-party mixer/effect paths.

`--aa-export-bgm-lifecycle` is an external functional probe for the production
single-pass BGM preparation scope. Run it through `Run-GuardedNativeAcceptance.ps1`
with `-Mode bgm-lifecycle`, the independent `effects-playback-proof-20261031/fixture-workspace`
as `-FixtureWorkspaceRoot`, and its frozen `AAVE-OfficialAuthoring-20261030.aas`
as `-StoryPath`. Native database readiness and the frozen scenario hash are
checked before AA's original scenario loader supplies the table. A scoped
test-only `GetBGM(12)` override points to a nonexistent WAV beneath the explicit
diagnostic directory; AA's actual loader, UnityWebRequest and completion body
run unchanged. The probe requires recoverable `bgm_preload_failed`, cancellation
cleanup after the real terminal callback, rejection of an unrelated callback
sharing the same BGMInfo, unchanged native custom cache, and successful fresh
built-in preparation afterward. It writes `native-bgm-lifecycle.json` and bounded
state-driven evidence. A generated test-owned PCM WAV also exercises cancelled
native success: the actual callback runs before native cache publication, the
barrier settles only after publication, and a fresh native cache-hit preparation
must preserve that cache-owned clip without releasing it. Existing cache entries
are untouched; the new test-owned cache entry remains until the isolated process
exits. The probe runs
without rendering, invoking production completions by
hand, changing settings, or making performance claims. The guarded runner
preserves/restores global state; the exact fixture BGM must remain built-in 12
in the isolated lab and the temporary test DLL must be removed after acceptance.

`--aa-export-bgm-inventory` is a separate development-only read-only diagnostic
for the selected 198-row `百鬼夜行-间章.aas` regression story, pinned to SHA256
`CDBF1FF37391336B67C9F5FAF24A83EFCF73BEE291BECEE9BCDDF303F392A7CA`.
Use the guarded runner with `-Mode bgm-inventory -StoryPath <absolute-story>`
and the existing native workspace; do not supply `-FixtureWorkspaceRoot`.
The exact frozen production Release 0.2.12 Plugin/Core hashes are verified.
After actual native readiness and workspace-path equality checks, the original
scenario loader supplies concrete rows. The probe records every authored BGM id
and row index, real `GetBGM` results for distinct positive ids, nulls and per-id
exceptions, current MenuBgmSelector state, and the production effective-occurrence
mapping in `native-bgm-inventory.json`. It installs no hooks and starts no playback
or asset loads. Source hashes and custom-cache identities are compared before
and after. A successful inventory may contain null resolver results; those are
diagnostic findings, not a claim that the story exports successfully.

`--aa-export-aamultilingual-boundary` is a diagnostic-only RenderControlV1
boundary check. It reflects the installed AAMultilingual build and verifies
that `IsTranslationPaused` changes false → true → false around AAVE ownership.
It does not promote AAMultilingual to strict Certified status or prove story
timing.

`--aa-export-character-voice` is a native boundary probe for the exact installed
CharacterVoice runtime and the production `CharacterVoiceDelayClock`. Keep all
selected Mods enabled, including CharacterVoice and an actually supported/bound
Azurite. The production plugin must already have installed the adapter; this
fixture never initializes a substitute runtime or installs its own Gate hooks.
In the isolated lab profile only, prepare `[Voice] Enabled = true`,
`PostPlaybackDelaySeconds = 0.1`, and an audible existing imported sound. Set
the isolated test's ordinary AA master/voice levels above zero using a protected,
restorable settings snapshot. The fixture never overrides those values: a zero
voice setting makes native SetVoice set the source volume to zero even if its
idle Inspector value was one, so the audible probe explicitly rejects that setup.
Set
`AA_VIDEOEXPORT_TEST_VOICE_IDENTIFIER` to its exact `aavt/voice/<sound-key>`
identifier (default `aavt/voice/aave-native-voice`), with no audio extension.
The native CharacterVoice resolver must find the real file. The fixture never
edits Mod configuration, Gate state, native-playing values or native caches.
It records whether Catalog has a missing or preview-only Test singleton.
Supply `-StoryPath` (or `AA_VIDEOEXPORT_TEST_STORY`) for a small existing voice-free
story: the fixture uses native `LoadGenericScenario` and `CoLoadSave`, allows the
complete original `Test.Start`, and pauses AUTO only during setup. Once native
SetVoice establishes the real request, it enables the native AUTO control so
CharacterVoice's original AUTO-only policy owns advancement. Read-only native
AdvanceScenario observations distinguish an early release from a legitimate
next row after the restored deadline; the fixture never suppresses advancement.
It first selects the exact native catalog row, expands its real group if needed,
and waits for the matching active detail card. Before the one permitted load
attempt it logs that card path and global/local resource roots and counts; an
unbound card or missing native character override dataset fails before preload.
It never assigns the Singleton or changes `previewMode`. An absent or preview-only
owner is an explicit failure when no story is supplied.
Before resolution it invokes the normal native resource loaders only for
missing global/local override datasets. A missing local dataset uses an empty
context under the diagnostic directory; existing imported global resources
remain authoritative. It logs both native dataset roots, matching manifest
entries, and sound/voice resolution results without replacing either resolver.

The cold request must produce exactly one native start callback, true/false
native playback observations and non-silent stereo AudioRenderer PCM. A stopped
capture counter is held past 0.1 wall seconds, then successfully rendered and
retained capture blocks advance the counter through the exact delay cutoff.
A second request must use the real cache and retains its delay when capture is
canceled; the unchanged live Gate then completes on its restored wall deadline.
The test checks main-thread identity, same-request continuity, actual adapter
reads, and settled Azurite FPS/VSync acquisition/release. Output includes raw
`character-voice-pcm.f32le` and event/phase/callback evidence in
`character-voice-evidence.json`, including failures and the precise scope.

```powershell
./tools/Run-NativeAcceptance.ps1 -LabRoot $lab -OutputDirectory $newEvidence `
  -Mode character-voice -StoryPath $existingVoiceFreeStory `
  -VoiceIdentifier 'aavt/voice/<existing-sound-key>' -VoiceFps 30
```

`-VoiceFps` also accepts 24 and 60. Missing resources, zero configured delay,
absent runtime binding, unsupported Azurite, silence or an unadapted wall-clock
delay are failures, never passes. This fixture does not enter the production
ExportHost/movie pipeline or change strict admission. Cold-load frame identity,
repeat video/PCM/PTS identity, all dialogue/manual paths, and a complete movie
remain separate acceptance requirements even after this probe passes.

For the distinct user-muted case, explicitly set
`AA_VIDEOEXPORT_TEST_VOICE_EXPECT_SILENT=1` and retain ordinary AA
`voiceVolume = 0` with nonzero master volume. This changes only the expected
outcome: it never edits voice levels, source properties, playback or admission.
The test requires non-silent decoded native clip content, exactly zero captured
PCM, the original native source volume remaining zero, and the same cold/cache
events, AUTO delay cutoff, cancellation remainder and Azurite restoration checks.
Evidence marks this as `silent-user-voice-level-zero`, not audible certification.
Clear the environment flag before an audible run.

The earlier all-zero capture in the diagnostic run was explained by the user's
existing `voiceVolume = 0`: native SetVoice correctly changed the idle source's
Inspector volume from one to zero. That result was not audio lost by export.
The subsequent explicit audible run used a protected test-only voice level of
one and captured nonzero PCM while retaining the original playback path.

`--aa-export-aaskipintro-boundary` checks the post-start boundary: no active
`CreatorSplash` remains in CatalogScene and AAVE can acquire and release
RenderControlV1 ownership. This is diagnostic evidence only; it does not
replace a cold-start and two-pass story acceptance.

`--aa-export-startup-controller` exercises the production retained
`AASkipIntroExportBoundary`, pins the actual installed Mod, retains validation
for 30 native Updates, and verifies worker commit checks and same-thread
publication scopes. It introduces and removes one no-op test-owned postfix to
prove real patch-graph drift invalidates the controller. The original Mod
postfix and its binary remain untouched. This is retained-adapter acceptance,
not a completed story export or whole-Mod-set certification.

`--aa-export-bootstrap-attribution` requires the opt-in early patcher
`AAVideoExport.RuntimeBootstrap.dll` under the isolated lab's BepInEx/patchers
and `AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION=1` in the launch environment. It verifies
capture before ordinary AAVE loading, marker propagation, real DynamicMethod
identity and unchanged arithmetic outputs, late creation without AssemblyLoad,
and a settled, actually bound Azurite FPS/VSync handoff across native Updates.
Snapshots explicitly deny preexisting/all-code coverage and strict admission.
This mode does not export a movie or certify the loaded Mod set. Preserve the
missing-patcher negative control and early-FPS-handoff evidence separately.

`--aa-export-menubgm-boundary` is a read-only MenuBgmSelector boundary probe.
It waits for CatalogScene, reflects the loaded `MenuBgmSelector` assembly, and
samples process thread count, loaded types, and `AudioSource` count/playing
state before, during, and after a short RenderControlV1 lease. It does not open
the file dialog, select or switch BGM, start playback, or write files/config.
The process thread sample is observational only and cannot prove ownership of
arbitrary threads created by another Mod; this probe therefore does not certify
MenuBgmSelector timing or strict export compatibility.

`--aa-export-sfxfolder-boundary` is a read-only SfxFolderMod boundary probe.
It waits for CatalogScene, reflects the loaded SfxFolderMod data/patch state,
and samples process threads and AudioSource counts before, during and after a
short RenderControlV1 lease. It does not open SoundExplorer, invoke search or
filter methods, change configuration, play audio, or write Mod data. Stable
state is evidence about this boundary only; it cannot certify arbitrary future
SoundExplorer use or native behavior.

`--aa-export-story` requires `AA_VIDEOEXPORT_TEST_STORY` to be an explicit existing
`.aas` in AA's actual workspace. It waits for native resources, expands the
matching catalog group, selects the native row, and uses the real encoder probe
and production export entry. The default is two exports with independent ffprobe
inspection of PTS, frame count, PCM sample count and duration; source hashes must
remain unchanged. `AA_VIDEOEXPORT_EXPECT_REJECTION` requests an exact error-code
test instead. Refusal is never represented as a completed movie.

The same story harness reflects the loaded Core's `ExportBuildProfile.SinglePassEnabled`.
For a `UnifiedTimeline` candidate, it requires exactly one live authored pass per
completed movie, zero preparation/replay flags, and no prepared audio, frame
schedule or ledger. No environment variable forces production into this path.
Build this temporary harness against the exact candidate Plugin/Core directory;
the `UnifiedTimeline` harness output lives under `bin/UnifiedTimeline/net6.0`.
Two-pass candidates retain the original two-pass requirements. Bounded performance
observations use `single-pass-render-encode` for a single-pass candidate and
`first-audio-preparation` for a two-pass candidate; these are different workloads.

With the explicit eight-row mixed recipe, test-only observers copy every original
`RawAudioPass.Append` block including bounded drain. Completed authored PCM is
retained as `original-mixed-pcm-attempt-XX-pass-XX.f32le`. The independently decoded
MOV audio is also retained, then compared against every original mixed sample
with only one PCM16 quantization LSB allowed. There is no offset, gain, resampling,
channel adjustment, padding or length repair. `*.original-pcm-comparison.json`
records frame-indexed sample windows, original/decoded RMS, maximum error and
the first mismatch. Voice lifecycle, all four authored effects, both video
backgrounds, exact movie PTS and actual Azurite restoration stay mandatory.
`AA_VIDEOEXPORT_TEST_CANCEL_FIRST=1` interrupts the first original capture after
at least 12 frames, requires no published movie and full restoration, then runs
the requested two completed exports. Portable observation tests and a harness
build alone never establish real native movie acceptance.

For a bounded user-profile timing window, provide an explicit
`AA_VIDEOEXPORT_TEST_OPTIONS_JSON` and `AA_VIDEOEXPORT_TEST_PERF_STOP_FRAME=360`.
The first 60 authored frames are excluded. Single-pass runs must show actual
accepted and encoded video frames, record their counts at the measured stop,
and cancel cleanly without publishing a movie. For a complete short-fixture
benchmark instead, set `AA_VIDEOEXPORT_TEST_WHOLE_EXPORT_BENCHMARK=1`, provide
the options JSON and leave the stop-frame flag unset. The timer covers the real
`Prepare` invocation through `ReportCompleted`, including native loading,
rendering, encoding, muxing and production completion checks. Results go to
`story-whole-export-benchmark.json`; external ffprobe inspection occurs after
the timer. AAC checks preserve exact frame PTS, continuous decoded audio PTS
and explicitly reported partial codec-block padding; they never claim lossless
PCM equivalence. Fixed 640x360 PCM16 acceptance remains a distinct path.

Both timing modes reject development audit, mixed-content observers and
`AA_VIDEOEXPORT_TEST_PREENCODE=1`. The latter enables diagnostic-only original
pre-encoder frame hashes, selected RGBA frame copies and main-thread title/clock
state in `preencode-attempt-XX`; it is intended to locate pixel divergence,
not to measure speed. Main logs contain compact PCM comparison summaries while
the complete per-frame windows remain in their original JSON sidecars.

`AA_VIDEOEXPORT_TEST_BGM_TRACE=1` is a separate startup diagnostic, also rejected
by both timing modes. It observes the original `AudioManager.PlayBGM` request,
resource callback, initial coroutine steps and pooled BGM `AudioSource.Play`.
Before/after records for the first eight mixer calls and first eight authored
mixer calls include shared/native frame positions, raw PCM cursors, DSP time,
clip identity/loading properties and the real source cursor, gain, pitch and
loop/playback state. Records stay in bounded memory during capture and are
written only after real cleanup as `bgm-start-attempt-XX.json`. No clip load,
preparation, playback command, scheduling, PCM or setting is changed. Missing
native seams or getter errors remain explicit diagnostic failures; the trace
does not by itself establish or repair BGM synchronization.

`AA_VIDEOEXPORT_TEST_FONT_ATLAS_EQUIVALENCE=1` explicitly selects an additional
authored-render comparison contract and automatically enables full original RGBA
capture plus native post-render glyph evidence. It is rejected in timing modes.
Repeated movies still require exact identity, frame/sample counts, PTS, decoded
PCM, original native PCM and event trajectories. Original pixels outside an
eligible changed-UV glyph triangle must match exactly; inside it, the independent
comparator requires matching text/font/mesh/colors/transforms and an integer
atlas-pixel translation, with at most two channel levels of difference.
`story-authored-render-equivalence-XX.json` separately reports raw exactness and
decoded-pixel exactness. If raw RGBA is exact but decoded pixels differ, the
encoder discrepancy remains a failure. This opt-in result is labeled
"authored-render equivalence with font-atlas exception", never byte-identical.
Without the flag, the original exact decoded-pixel comparator is unchanged.

Set `AA_VIDEOEXPORT_TEST_QUIT=1` to exit normally after the result. The helper
`tools/Run-NativeAcceptance.ps1` requires an isolated test copy marked by
`aave-test-lab.json`, refuses to start beside another AA instance, uses one new
diagnostic directory per run, and records the process exit plus native logs.
Do not reuse test profiles as the user's normal active profile.

The path mode's six-frame synthetic media export is separately disabled by
default. To opt in, set `AA_VIDEOEXPORT_NATIVE_MEDIA=1`, provide FFmpeg/ffprobe on
`PATH` or via `FFMPEG_DIR`, and optionally set `AA_VIDEOEXPORT_TEST_ENCODER`
(default `h264_qsv`, requiring Intel QSV). Its 96x64 output is written to
`native-input-export` under the same diagnostics root. Unsupported hardware is
a failure, not an automatic pass. This tests correctness, not rendering speed.

Screenshots may include locally configured folder names. Review them before
sharing; generated diagnostics and native game resources are not repository files.

`--aa-export-aamenu-boundary` is a read-only AAMenuPrototype boundary probe. It waits for CatalogScene, reflects `AAMenuPrototype.Plugin`, `MenuBehaviour`, and `MenuEntryHooks`, and samples menu component counts, process threads, and AudioSource counts/playing state before, during, and after a short RenderControlV1 lease. It never opens the custom menu, calls native window APIs, starts audio, changes settings, or writes files. This evidence cannot certify story-time menu isolation or strict timing.
