# Unified export timeline implementation plan

> For agentic workers: use subagent-driven-development for independently owned tasks and verify each integrated result. Continue the already authorized implementation without another design-approval pause.

**Goal:** One logical export timeline with single-pass real audio/video, preserved Mod behavior and production without audits.

**Architecture:** Extend the existing integer-frame/audio mapping and native clock. Introduce a live session timeline that seals at natural story end. Keep native playback, bounded frame queues and offline PCM; remove preparation/replay from the new production execution path.

**Tech Stack:** C#/.NET 6, BepInEx IL2CPP/Harmony, Unity offline AudioRenderer, FFmpeg, PowerShell native acceptance runners.

**Spec:** `docs/unified-export-timeline-20261031.md`

## Global constraints

- Source root is an existing non-Git directory; preserve current changes and checkpoint touched source/test/tool files rather than inventing a Git history.
- Normal Release/Debug must not activate audit machinery. Prototype is explicitly `UnifiedTimeline`; DevelopmentAudit remains separate.
- Do not edit user projects, saves or unrelated Mods. Root coordinates all AA process and deployment operations.
- Actual sample data, visible effects, cancellation and restoration are requirements; passing text/shape assertions are insufficient.
- No completion or speed claims without fresh native evidence.

## Task 1 — Live session timeline and actual PCM

Files: `src/VideoExport.Core/ExportSession.cs`; corresponding Core/production tests.

Interface: `BeginLiveTimeline()` before video/audio output; `SealLiveTimeline(StrictFramePlan plan)` at natural end. Keep `BindStrictTimeline` for the existing developer path. Add an offset-aware `WriteAudio(float[] samples, int offset, int length)` if needed to consume borrowed PCM slices without allocation.

- [x] Add failing behavior tests for output before live initialization, duplicate sealing, exact frame/sample completion, cancellation, and real FFmpeg mux output.
- [x] Permit queued video while the final duration is not yet known; seal the exact final frame count without requiring pending GPU readbacks to have completed synchronously.
- [x] Require final accepted/written frame count and PCM length to match the sealed execution plan. Preserve no-padding strict mux behavior.
- [x] Run the targeted tests and build; record red/green logs in `artifacts/unified-timeline-20261031`.

## Task 2 — One native authored pass

Files: `Directory.Build.props`, `src/VideoExport.Core/ExportBuildProfile.cs`, `src/VideoExport.Plugin/ExportHost.cs`, `ExportHost.Strict.cs`, new `ExportHost.SinglePass.cs`, Host behavioral test project.

- [x] Add build selection for `UnifiedTimeline` and a public readable `SinglePassEnabled` property used by the external test harness.
- [x] Add failing Host tests requiring one native launch, first-pass video, actual raw PCM, no prepared schedule/ledger and cleanup on cancellation.
- [x] Retain existing mixer priming, voice start mapping, fixed natural end and bounded drain. Initialize a live session, submit first-pass video, copy sealed raw content once and finalize without reloading the story.
- [x] Keep the two-pass developer path isolated and unchanged. Update prototype progress text to describe actual work.
- [x] Build the real plugin against the installed AA interop and run Host/Core behavior suites.

## Task 3 — Shared frame-time publication

Files: `NativeExportClock.cs`, existing clock adapters, Host capture/start/restore boundaries, clock/adapter tests.

- [x] Record concrete source clock/update mapping for the installed supported Mods.
- [x] Add failing behavior tests showing that waiting/priming does not repeatedly advance delta consumers, and all adapters observe the same frame time during one Unity frame.
- [x] Publish common frame position/time/delta through the existing clock; wire timestamp, voice, background and native wait consumers to it at the agreed boundaries.
- [x] Preserve native update order, local effect start offsets and restoration. Avoid calling native Update twice or replacing wall-clock cancellation/timeouts.
- [x] Run clock/voice/effects/background tests and inspect the actual native transitions.

## Task 4 — Independent native prototype evidence

Files: `tests/VideoExport.NativePath.Tests`, native acceptance runner scripts; independent fixture workspace and lab.

- [x] Add a single-pass-aware external observer based on actual build mode and actual begin/end/PCM events.
- [x] Expect one authored pass per completed export and no preparation/replay artifacts. Preserve full decoded pixels, PCM, timestamps, voice/background/event observations.
- [x] Stage only tested prototype binaries and the independent test plugin in the marked lab; back up/remove the lab's known old audit bootstrap.
- [x] Run complete short fixture, repeat and cancellation/recovery; inspect output and failures. Fix real root causes before moving on.

## Task 5 — Performance, production promotion and reversible release

Files: performance runner/results, build profile, packaging scripts, README/changelog/release metadata.

- [x] Compare same-host/same-Mod/same-story/same-settings execution with the frozen baseline and compatible 0.2.4 path. Report preparation and total time alongside steady throughput.
- [x] Profile only if the new native measurements reveal remaining material bottlenecks; keep development observers out of the timing runs.
- [x] Promote native-accepted single-pass behavior to ordinary builds; run production no-audit tests and independent final review.
- [x] Freeze the exact release artifacts, build/test package and rollback, install reversibly, and verify ordinary launch and one real export without test/audit environment.
- [x] Mark the goal complete only when all spec evidence is present; unresolved native failures remain open work.

## Final execution evidence

All plan work is supported by artifacts/unified-timeline-20261031/DELIVERY-0.2.12.md and completion-audit-r2.md. The final runtime is R2 (Plugin 47DF9237...B958021; Core F728E74D...72BCFCC). Real lab short repetition/cancellation, native BGM lifecycle, complete 198-row user story, matched 0.2.4 comparisons, final package checks, reversible primary installation, and a user-operated ordinary primary UI export are recorded separately. Primary test/audit components are absent, settings are restored, and the AA test process is closed. Foreground operations are prohibited by the user's latest instruction; later observation used background-only APIs and the user performed the export/panel-close clicks.
