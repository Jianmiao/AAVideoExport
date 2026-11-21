# Strict preflight cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cache the strict headless timeline preflight and keep exactly one real video render per export.

**Architecture:** Persist an immutable `StrictPreflightReceipt`, sealed PCM file and state transcript under the existing timeline receipt directory. On a cache miss, the existing strict preparation pass fills those artifacts; on a hit, the host opens them and skips preparation playback while preserving live replay validation.

**Tech Stack:** .NET 6, C#, existing AAVE Core/Plugin, JSON metadata, atomic Windows file publication, existing strict PCM/transcript primitives, native AA acceptance harness.

**Spec:** `docs/superpowers/specs/2026-10-25-strict-preflight-cache-design.md`

## Global Constraints

- Strict synchronization only; no tolerance mode, silence padding, arbitrary trimming or unverified fallback.
- Preserve `AAVideoExport.Integration.RenderControlV1` protocol major version 1 and main-thread lifecycle ordering.
- Do not modify other Mods or the primary AA installation; use isolated lab copies.
- Cache identity must include source, host, loaded Mod fingerprints, settings, audio layout and AAVE ABI/protocol.
- Every task requires a failing focused test before production implementation and fresh verification before completion claims.

---

### Task 1: Immutable preflight receipt and cache store

**Files:**
- Create: `src/VideoExport.Core/StrictPreflightReceipt.cs`
- Create: `tests/VideoExport.StrictPreflight.Tests/Program.cs`
- Create: `tests/VideoExport.StrictPreflight.Tests/VideoExport.StrictPreflight.Tests.csproj`
- Modify: `src/VideoExport.Core/VideoExport.Core.csproj`

**Interfaces:**
- `StrictPreflightReceipt.Create(identity, sourceHash, hostIdentity, modFingerprint, settingsFingerprint, frameCount, fps, sampleRate, channels, frameTraceSha256, pcmSha256)`.
- `StrictPreflightCache(path).TryRead(expectedIdentity, out StrictPreflightReceipt receipt, out CacheFiles files)`.
- `StrictPreflightCache.Publish(receipt, pcmSourcePath, transcriptSourcePath)`.
- `StrictPreflightCache.Open(receipt)` returns read-only sealed PCM/transcript paths.

- [x] Write tests for cache miss, complete atomic publication, identity mismatch, malformed JSON, missing sibling files, and preservation of an existing winner.
- [x] Implement canonical SHA-256 validation, bounded JSON, CreateNew staging files, flushes and non-overwriting atomic rename.
- [x] Run the focused suite and verify all receipt/cache tests pass.
- [x] Build `src/VideoExport.Core` with zero warnings and errors.

### Task 2: Open sealed PCM and transcript without deleting cache artifacts

**Files:**
- Modify: `src/VideoExport.Core/PreparedAudioTrack.cs`
- Modify: `src/VideoExport.Core/StrictReplayLedger.cs`
- Modify: `tests/VideoExport.StrictPreflight.Tests/Program.cs`

**Interfaces:**
- `PreparedAudioTrack.OpenSealed(path, fps, sampleRate, channels, expectedFrames, expectedSha256)` opens an immutable cached track and validates byte length/hash before use.
- `StrictReplayLedger.OpenSealed(path, channels, expectedFrames, expectedTraceSha256, expectedPcmSha256)` opens an immutable transcript for replay and never deletes the cache-owned file on dispose.
- Existing temporary constructors retain their private-file cleanup behavior.

- [x] Add tests proving cached files remain after reader disposal and frame/sample order remains strict.
- [x] Implement separate ownership modes for temporary and cache-owned files; keep all validation before returning an opened reader.
- [x] Run focused cache tests plus `tests/VideoExport.StrictReplay.Tests`.
- [x] Build Core with zero warnings and errors.

### Task 3: Feed the cache miss path from the existing strict preparation

**Files:**
- Modify: `src/VideoExport.Plugin/ExportHost.Strict.cs`
- Modify: `src/VideoExport.Plugin/ExportHost.cs`
- Modify: `src/VideoExport.Plugin/Plugin.cs` only if a stable cache directory accessor is required.
- Modify: `tests/VideoExport.Host.Tests/StrictHostRegression.cs`
- Modify: `tests/VideoExport.Host.Tests/Fakes.cs`

**Interfaces:**
- `PrepareStrictIdentity` includes the loaded Mod/sandbox fingerprint in `_strictIdentity`.
- `FinishStrictPass` publishes a receipt only after raw audio, prepared PCM and state transcript pass all current strict checks.
- `_strictCacheHit` and `_strictPreflightReceipt` select the cache-hit path without weakening live replay checks.

- [x] Implement cache publication after `SealStrictAudio`, with source/host/settings identity captured before playback and no overwrite of an existing receipt.
- [x] Preserve the current scene roundtrip and existing `RepeatExportBaseline`; cache publication is separate from the media baseline.
- [x] Run `dotnet run --project tests/VideoExport.Host.Tests -c Release`; all 39 lifecycle and drain checks pass.

### Task 4: Cache-hit launch and one actual render

**Files:**
- Modify: `src/VideoExport.Plugin/ExportHost.cs`
- Modify: `src/VideoExport.Plugin/ExportHost.Strict.cs`
- Modify: `src/VideoExport.Plugin/NativeExportPanel.cs` only for accurate stage text/counters.
- Modify: `tests/VideoExport.Host.Tests/StrictHostRegression.cs`

**Interfaces:**
- `InitializeStrictPreparation` attempts a matching cache read after identity inputs are available.
- Cache hit opens sealed PCM/transcript, constructs the fixed `StrictFramePlan`, and starts native playback directly in video replay mode.
- Live replay still calls `RenderAudio`, `RawAudioPass`, `ValidateState`, exact End fence, `SealStrictAudio`, `CompleteReplay` and media verification.

- [x] Implement the cache-hit branch and keep it behind `StrictPreflightCacheReuseEnabled`, which defaults off until deterministic sandbox certification.
- [x] Force-test cache reuse; frame-420 story-state divergence failed closed with `timeline_replay_mismatch`.
- [x] Run Host, RawAudioPass, StrictReplay, StoryPath and Audio suites.

### Task 5: Native acceptance and timing evidence

**Files:**
- Modify: `tests/VideoExport.NativePath.Tests/StoryExportChecks.cs` only for phase/counter assertions.
- Modify: `tools/Run-NativeAcceptance.ps1` only if a fresh mode/output assertion is needed.
- Update: `docs/clock-proxy-evidence-20261025.md`

- [x] Run a fresh isolated story export on a cache miss and verify one headless preflight followed by one actual video render, exact frames/sample counts and source hash preservation.
- [x] Run the same story/settings with cache reuse disabled; verify the strict safe path repeats headless preflight and one actual render.
- [x] Force a cache hit; verify live story divergence is rejected before output publication.
- [x] Run audio-hooks and clocks after the implementation build; verify Azurite ownership and RenderControlV1 lifecycle.
- [x] Update the evidence document with miss/hit guard timings and exact acceptance paths.

### Task 6: Full regression and release gate

**Files:**
- Modify: `docs/strict-two-pass-0.2.8.md` or replace it with a strict-preflight-cache note.
- Update: `src/VideoExport.Core/README.md` and relevant test READMEs.

- [x] Run Core, Host, Audio, RawAudioPass, StrictReplay, TimestampedAudioWindow, StrictSession, Clock and NativePath builds/tests.
- [x] Verify no primary AA installation or unrelated Mod files changed; all native writes remained under the isolated lab and evidence folders.
- [x] Verify cache artifacts use unique identities and old cached receipts are never overwritten.
- [x] Record final cache-miss/cache-hit guard evidence, plus strict capability rejection evidence for the current unknown Mod set.

The current isolated profile intentionally contains unregistered Mods, so the
post-admission native story check is an expected `strict_capability_unsupported`
rejection. The earlier cache-miss and forced-cache-hit story runs remain the
timing evidence; no unverified movie is published after the admission gate.

### Task 7: Sandbox capability and command-trace foundation

**Files:**
- Create: `src/VideoExport.Core/StrictCapability.cs`
- Create: `tests/VideoExport.StrictCapability.Tests/Program.cs`

- [x] Add capability levels (`Certified`, `Observed`, `Opaque`, `Rejected`) and an order-independent capability fingerprint.
- [x] Add append-only sandbox command traces with exact preparation/replay matching and canonical trace hashes.
- [x] Verify certified-only preflight reuse, order-independent fingerprints, command divergence, missing commands and sequence ordering.
- [x] Connect the capability snapshot to loaded Mod inventory; cache reuse remains disabled until native sandbox certification.
- [x] Enforce strict admission before story loading or RenderControlV1 acquisition; opaque, rejected, unknown, and dynamic Mod assemblies fail closed with diagnostics.
- [x] Audit the current loaded Mod set with MVID/SHA and IL/runtime risk evidence; no unverified module was promoted to `Certified`.
- [x] Add an AAMultilingual RenderControlV1 pause-boundary probe; it remains `Observed` and is not admitted for strict export or cache reuse.
- [x] Add an AASkipIntro post-start boundary probe; it remains `Observed` pending cold-start and two-pass story evidence.
- [x] Hook native story `AdvanceScenario` and `End` into the command trace; the pre-admission isolated AA story acceptance remained 4/4, while the current unknown Mod set is rejected by the strict admission gate.
- [x] Add guarded AudioSource command hooks for Play/OneShot/Delayed/Scheduled/Stop/Pause/UnPause/timeSamples; tracing remains disabled until the audio adapter is certified so current exports are unaffected.

### Task 8: Deterministic audio segment model

**Files:**
- Create: `src/VideoExport.Core/StrictAudioCommandModel.cs`
- Create: `tests/VideoExport.StrictAudioCommand.Tests/Program.cs`
- Create: `src/VideoExport.Plugin/StrictAudioCommandHooks.cs`

- [x] Model Play, OneShot, Stop, Pause, UnPause, Seek, scheduled end, volume and pitch changes as exact sample-addressed segments.
- [x] Map validated segments to exact DSP timestamps with quarter-sample precision and overlap checks.
- [x] Add a Unity-facing dispatcher that only accepts validated segments and calls `PlayScheduled`/`SetScheduledEndTime` with exact clip offsets.
- [x] Route the native scheduled-marker probe through the production dispatcher; non-start marker scheduling passed, while the full start-marker strict signal remains a documented negative control.
- [x] Reject backward command order, invalid source state and non-positive OneShot lengths atomically.
- [x] Add guarded Unity AudioSource command hooks; keep them disabled until the segment model is certified against native playback.
- [x] Connect certified segments to DSP scheduling and prove native volume/pitch mapping, pause/resume cursor semantics, and same-source clip reuse; evidence remains diagnostic until third-party mixer/decoder paths are separately certified.
