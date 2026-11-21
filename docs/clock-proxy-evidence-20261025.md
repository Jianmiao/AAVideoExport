# Export clock proxy: implementation and native evidence

Status: development candidate, not a completed strict export release.

## Implemented boundaries

- More Effects 1.6.7 uses a scoped animation/action clock. Cancellation rebases
  character starts, action-gate deadlines, pending segment deadlines and queue
  origins in one validated transaction under the queue lock. Failed restoration
  retains the clock offset and rejects another export.
- Flandre Portrait 5.2.0 uses a scoped realtime proxy for five managed methods
  and seven clock calls, including both async state machines. Version, MVID,
  SHA-256, signatures and patch conflicts are checked before binding. Persistent
  adapter-local offsets keep pending async locals continuous after cancellation.
- The global `Time.realtimeSinceStartup` patch has been removed. Decoder
  watchdogs, filesystem timeouts and unrelated Mods retain native wall time.
- Native realtime waits are mapped from the export-entry baseline, so a first
  poll delayed by a wall-clock stall cannot prematurely finish an existing wait.
- Known incomplete More Effects adapters and the audited, unadapted Character
  Voice 1.6.8 build are rejected before native story loading. This is a bounded
  admission rule, not an exhaustive certification of arbitrary Mods.
- Strict capability admission now snapshots the actual loaded Mod assemblies
  before story reading or `RenderControlV1.Acquire`. Certified More Effects and
  Portrait adapters are admitted; Azurite and its `RenderControlV1` consumer
  are renderer-only `Observed` evidence and may participate in strict capture
  but cannot authorize preflight-cache reuse. Opaque, rejected, unknown, and
  non-infrastructure dynamic assemblies fail closed with their module and
  evidence in the diagnostic. This gate applies to strict export preparation;
  ordinary AA playback does not acquire it.
- RenderControlV1 remains protocol version 1. The speculative change moving
  `ReleaseIdleBudget` before `Begin` acquisition was removed: the earlier buttons
  fixture did not call `Begin`, so its later passing run proved no causal fix.

## Verification

All evidence paths below are relative to the source checkout.

| Check | Result | Evidence |
| --- | --- | --- |
| Portable regression groups | 330 passed in 23 groups | `artifacts/portable-20261025-12/portable-results.json` |
| Strict host and story-path regression | Host 42/42; StoryPath 5/5 | `tests/VideoExport.Host.Tests`, `tests/VideoExport.StoryPath.Tests` |
| Scoped portrait adapter and installed DLL metadata | 12/12 | `tests/VideoExport.PortraitClock.Tests` |
| Timestamped audio window and DSP scheduler, including recorded native PCM | 8/8 | `tests/VideoExport.TimestampedAudioWindow.Tests` |
| Actual FFmpeg strict sessions with nonzero PCM pulses | 14/14 | `tests/VideoExport.StrictSession.Tests/bin/Release/net6.0/strict-session-media/20261024-192054-e325bbe36d1d4aa1a8667b5898bdc7f0` |
| Native settings controls | 3/3 | `artifacts/acceptance-20261025-03-settings` |
| Native path editing and submission | 7/7 | `artifacts/acceptance-20261025-04-paths` |
| Known unsupported voice build rejected before capture | 2/2, including unchanged source | `artifacts/acceptance-20261025-04-admission` |
| Native clock dispatch and restored Azurite ownership | 8/8 | `artifacts/acceptance-20261025-06-clocks` |
| Native audio operation and AudioManager boundary probe | 9/9 | `artifacts/acceptance-20261025-20-audio-hooks` |
| Native DSP audio command reuse and volume mapping | 1/1 | `artifacts/acceptance-20261025-45-audio-command-reuse-final/native-checks.log` |
| Native DSP pitch, volume, pause silence/cursor hold and resume marker positions | 1/1; 15 exact marker positions, seven before pause and eight after resume | `artifacts/acceptance-20261025-48-audio-semantics/audio-semantics-evidence.json` |
| AAMultilingual RenderControlV1 pause boundary | 1/1; false → true → false around ownership, lease restored | `artifacts/acceptance-20261025-59-aamultilingual-boundary/native-checks.log` |
| AASkipIntro post-start boundary | 1/1; no active CreatorSplash in CatalogScene, ownership restored | `artifacts/acceptance-20261025-61-aaskipintro-boundary/native-checks.log` |
| MenuBgmSelector read-only ownership boundary | 1/1; loaded assembly reflected, 3 AudioSources/0 playing before-during-after, 121 process threads at each sample, ownership restored; does not certify arbitrary thread ownership or story behavior | `artifacts/acceptance-20261025-menubgm-boundary-03/native-checks.log` |
| Native strict capability admission with unknown Mod set | 2/2; rejected before story read/ownership, source hash unchanged | `artifacts/acceptance-20261025-62-capability-rejection-final/native-checks.log` |
| SfxFolderMod read-only ownership boundary | 1/1; stable resource/audio counters and 0 playing sources before/during/after ownership | `artifacts/acceptance-20261025-68-sfxfolder-boundary/native-checks.log` |
| Character Voice static timing/audio audit | 39 direct call sites; 38 wall-clock and one AudioSource.Stop; independent playback gate remains unadapted | `artifacts/charvoice-audit-20261025.log` |
| Current Mod admission audit | 11 assemblies audited; none newly promoted to Certified | `docs/current-mod-admission-audit-20261025.md` |
| Native clock dispatch after raw-audio integration | 8/8 | `artifacts/acceptance-20261025-22-clocks` |
| Real strict story export, repeated twice | 4/4; 721 frames and 1,153,600 PCM sample frames on both runs | `artifacts/acceptance-20261025-19-story` |
| Headless preflight plus one real render per attempt | 4/4; 721 frames and 1,153,600 PCM sample frames | `artifacts/acceptance-20261025-29-preflight-only` |
| Native global-clock regression before removing hook | Failed as expected | `artifacts/acceptance-20261025-05-clocks-before` |

Native runs use isolated AA copies, one process at a time, and graceful exit on
result. The current acceptance lab is `artifacts/native-lab-20261025-13` with
AAVE 0.2.8 and Azurite 1.0.39. The copied profile omits the missing TitlePage
directory; the source profile is not rewritten. Repeat tests use a copied
profile without the known unsupported voice build. Explicitly selected source
.aas files are read in place and hashed, not edited. No primary AA installation
or unrelated Mod directory was used for build or test writes.

The full-story fixture now uses the actual active catalog row, expands its native
group and checks database readiness. `CatalogFileInfo` is a detail view, not the
row itself. Earlier arbitrary-detail fallback and cached nullable table reads
were invalid fixture assumptions and are no longer used.

## Native audio boundary and current strict export

A real story reached its native end at 722 frames / 30 fps / 48 kHz. Unity
delivered 1,155,072 sample frames, whereas that interval requires 1,155,200.
Strict sealing rejected the 128-sample shortage and published no movie.
Evidence: `artifacts/acceptance-20261025-07-repeat`.

The native marker probe measured a 1,024-sample DSP block. Twelve video frames
produced 19,456 samples rather than 19,200. A second source started on video
frame 1 did not land on sample 1,600. Asking the native renderer for exactly
1,600 samples was rejected. These are actual PCM observations, not estimates
from container duration. Evidence:
`artifacts/acceptance-20261025-10-audio-clock/audio-clock-evidence.json`.

The follow-up DSP-scheduled probe placed every authored non-start marker at its
exact requested sample, including the event started on video frame 1. It still
returned a 23,552-sample raw interval for a 19,200-sample content window, so
this proves event placement but does not prove a finished export contract.
Evidence: `artifacts/acceptance-20261025-11-audio-clock/audio-clock-evidence.json`.

Consequently, equal frame counts and patched time getters do not establish
sample-aligned event delivery. The production path now uses a timestamped raw
audio pass that retains the complete native interval, then extracts only the
declared content interval after the native story end. A bounded drain may add
native tail blocks without advancing the frozen video frame count; a shortage,
gap, overlap, non-finite sample or invalid DSP interval fails closed.

This module is now implemented in `src/VideoExport.Core/TimestampedAudioWindow.cs`.
It validates raw timestamps, rejects missing/overlapping samples and DSP
precision loss, and returns borrowed content slices with lead/tail receipts.
The native probe feeds it real mixer output and passes the resulting 19,200
samples through unchanged production PreparedAudioTrack sealing and all twelve
replay frames without sample changes. Evidence:
`artifacts/acceptance-20261025-13-audio-window`. A follow-up also supplies the
window's raw sample positions from observed native DSP timestamps rather than
assuming continuity from buffer lengths:
`artifacts/acceptance-20261025-14-audio-window-timestamps`.

`NativeAudioSampleClock` maps each measured DSP block to a continuous raw
sample interval. `ExportHost` records story state separately from mixer arrival,
drains to the exact content endpoint, and replays the immutable content PCM on
the second pass. The real story acceptance exported two identical 721-frame,
24.0-second files with 1,153,600 audio sample frames each; source SHA-256,
frame count, audio count and stream timestamps matched on both runs. The
previous 128-sample shortage is therefore handled by measured tail draining,
without tolerance, silence padding or arbitrary trimming.

The preflight-cache increment now persists an immutable receipt, PCM copy and
state transcript under the timeline receipt directory. A cache miss performs
headless preparation followed by one actual video replay. Cache reuse is
deliberately disabled until the loaded Mod set has a certified deterministic
sandbox capability: a forced cache-hit experiment detected a real story-state
divergence at frame 420 and failed closed, proving that stale timing is never
silently reused.

The scheduled-source experiment remains diagnostic only: it confirms that
Unity's 1,024-sample DSP blocks can place non-start markers at requested sample
positions, but does not certify every third-party audio command. Dynamic
VideoPlayer decoding, Character Voice playback-gate adaptation, and unobserved
native or third-party event paths still require separate support and native
acceptance. Successful adapter installation is not a whole-Mod visual
synchronization guarantee.

The native audio-semantics probe now exercises the production
`StrictAudioDspDispatcher` with a 48 kHz stereo marker clip at pitch 2.0 and
volume 0.5. Unity placed all seven pre-pause markers and all eight post-resume
markers at the exact predicted sample frames; the pause block was silent and
the source cursor held at 4,096 samples through pause and resume. Evidence also
records the PCM SHA-256
`2e973132f2c2edfa32aa6f04c3999cdbaa23d799083cc31e5e1bca043c265bcf`.
This is a native AudioSource/AudioRenderer boundary result only. Production
AudioSource command tracing remains disabled, and the result does not certify
unknown mixer, decoder, or third-party event paths.

Dynamic VideoPlayer decoding, Character Voice playback-gate adaptation, and
unobserved native or third-party event paths still require separate support and
native acceptance. Successful adapter installation is not a whole-Mod visual
synchronization guarantee.

AAMenuPrototype read-only ownership boundary | 1/1; Plugin/MenuBehaviour/MenuEntryHooks reflected, one active MenuBehaviour, 0 MenuEntryHooks, 3 active AudioSources/0 playing, 121 threads stable before/during/after, ownership restored; evidence `artifacts/acceptance-20261025-aamenu-boundary-01/native-checks.log`; this does not certify menu/window/story-time behavior.
AAMenuPrototype re-run with thread-count equality gate | 1/1; 122 process threads stable before/during/after, one active MenuBehaviour, zero MenuEntryHooks, 3 AudioSources/0 playing, ownership restored; `artifacts/acceptance-20261025-aamenu-boundary-02/native-checks.log`.
