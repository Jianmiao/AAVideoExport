# Controlled background migration — 2026-10-30

The previously exercised background-video backend and frame-owned native waits
are now integrated into this source tree. Current-source managed/media checks
pass. **Native verification of this combined candidate is still required.**
The old branch's AA runs are provenance, not acceptance of the new candidate.

## Source and scope

Origin: `E:/aamod/AAVE-runtime-work/20261028/AAVideoExport-0.2.9-source`, branch
`codex/runtime-foundation`, HEAD `7c148d0` plus its uncommitted background work.
The origin was read only. Imported new files and their original SHA-256 values
are recorded in `artifacts/background-migration-20261030/copied-source-sha256.json`.

Imported production modules:

- Core `ControlledVideoFrameStore.cs`: bounded FFmpeg/ffprobe decode into exact
  source PTS/frame stores, with source identity and read locks.
- Plugin `ControlledBackgroundVideoAdapter.cs` and `CompatibilityBuild.cs`:
  the exact inspected Flandre 5.2.0.0 build, AAVE-owned textures, original layer
  alpha/transitions, per-resource command trace, and retained native snapshot.
- Plugin `NativeScaledWaitHooks.cs` plus the additive `NativeExportClock.cs`
  changes: retain native iterator effects and convert only capture-owned
  WaitTask/DelayedAdvance scaled yields into integer-frame waits.

Host/Plugin/CaptureHooks received scoped lifecycle and pre-render calls; no
entire shared file was replaced. `ExportHost.Strict.cs` now includes background
settings and captured command/source identity, rejecting any preparation/replay
difference. Disposal clears only the background snapshot along with existing
strict pass state. Existing trace cursor/immutable trace, retained startup
controller, early-bootstrap markers, and CharacterVoice lifecycle are retained.

Admission adds only Observed `FlandreScarletBackground` with exact evidence
`ControlledBackgroundVideoAdapter.pts-frames.v1`. The inventory issues that
evidence through the adapter's build and patch validation. All other Mod
admission remains unchanged; Observed background does not permit cache reuse.

## Migration review corrections

Two old adapter failure boundaries were reproduced against real Harmony before
correction: ILManipulator conflicts were not rejected, and failed unpatch could
discard the hooks needed for retry. The production adapter now:

- Rejects every Harmony patch kind on controlled callbacks, including
  ILManipulators, and rejects a matching prefix installed under another owner.
- Removes prefixes only for its own Harmony owner and checks actual removal.
- Returns false on unpatch/restoration failure while retaining retry bindings.
- Does not resume the quarantined native snapshot while the outer render lease
  remains owned. Successful shutdown removes subscriptions and clears ownership.

Plugin unload and failed-load rollback honor false before broad UnpatchSelf or
host destruction. Host restores all remaining scopes, then propagates a
background EndExport exception to prevent publication. Existing RenderControlV1
fault behavior remains unchanged: failed native restoration does not release
the real render lease or advertise readiness.

## Fresh current-source results

All evidence below was generated under this source tree on 2026-10-30:

| Suite | Result | Boundary |
| --- | --- | --- |
| ControlledVideo | 8/8 | Actual FFmpeg, independently authored CFR/VFR pixels and PTS |
| ControlledBackground | 15/15 | Resolver, row conversion, actual Harmony cleanup/conflict paths, actual render lease guard |
| Clock | 16/16 | Source-linked native-clock logic, no native coroutine dispatch claim |
| Host | 62/62 | Production lifecycle with native/encoder substitutes |
| PluginLifecycle | 7/7 | Production Plugin source with BepInEx/Unity/adapter substitutes |
| StrictCapability | 18/18 | Background-only evidence allowance, rejection and preserved trace rules |

Regression evidence in `artifacts/background-migration-20261030/`:

- `background-lifecycle-red.log`: original eight resolver tests pass and six
  new real-Harmony lifecycle tests fail as expected.
- `background-lifecycle-green.log`: 14/14 after those six fixes.
- `background-lifecycle-final.log`: 15/15 including the owned-snapshot guard.
- `plugin-shutdown-red.log` -> `plugin-shutdown-green.log`: 5/7 -> 7/7.
- `host-final.log`: 62/62, including unchanged frame/PCM counts with a changed
  background source, and restore failure retaining the actual render lease.
- `controlled-video.log`, `clock.log`, `strict-capability.log` cover the other
  fresh checks. `verification-summary.json` records final log identities.

## Native continuation

The small native fixture files `ControlledBackgroundChecks.cs` and
`BackgroundStoryEvidence.cs` were imported; route/runner/story call-site changes
are coordinated separately by the native-fixture task. `controlled-background`
uses `AA_VIDEOEXPORT_TEST_BACKGROUND_ASSETS`; the story pixel evidence uses
`AA_VIDEOEXPORT_TEST_BACKGROUND_STORY_ASSETS`. Both require explicit isolated
synthetic assets. Existing reusable assets are under the origin's
`artifacts/controlled-background-assets-20261029/` and were not modified.

Before native execution, rebuild the combined provider/Core/native fixture and
verify the exact installed candidate identities in a new independent lab.
Preserve the actual selected Mod set, source story/resources, frame/PCM/event/PTS
checks, cancellation/retry and Azurite FPS/VSync restoration. No native AA run,
installation, other Mod changes or completed-movie claim is part of this
migration task.
