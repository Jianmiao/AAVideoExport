# AAVE unified export timeline

Date: 2026-10-31. Status: implemented and installed as frozen 0.2.12 R2; scoped native, media, performance and ordinary-primary evidence is recorded in artifacts/unified-timeline-20261031/DELIVERY-0.2.12.md.

## User requirements

- During export, story, animation, effects, voice, music and video must use one logical timeline and a defined update/capture order.
- Production must not run development program audits, runtime proof inventories, per-export repeat verification or provenance sidecars.
- Preserve each Mod's real behavior and authored timing. Keep local start offsets, pauses, voice gates, effect curves and source data.
- Pursue single-pass actual audio/video generation. Do not mistake disabling synchronization checks or re-labeling two passes for this goal.
- Retain the bounded GPU readback/encoding pipeline, cancellation, resource restoration and RenderControlV1 interoperability.
- Compare full elapsed export cost under the same AA host, Mod set, story, output frame rate, resolution and encoder. Old screenshots from different conditions are not a benchmark.
- Verify the actual AA native path before a reversible production deployment. Development observations belong in the separate test harness.

## Existing implementation facts

- `ExportTimeline` already derives rational audio sample budgets from integer video frame counts.
- `NativeCaptureScope.RenderAudio` already uses Unity's offline AudioRenderer, including zero-length callbacks needed to advance native mixing.
- `RawAudioPass` already retains the real contiguous PCM interval, with explicit primer/content/tail handling.
- Current two-pass execution is imposed by `ExportHost.Strict.cs` and the pre-bound `ExportSession` plan. It is not proof that native audio requires replay.
- `NativeExportClock.Now` is latched per Unity frame, while several animation adapters directly read `_captured`; `Delta` is constant even during held logical time. These must be aligned without double-stepping native updates.
- Release/Debug have an existing no-audit source split; DevelopmentAudit retains old diagnostics. Neither is yet evidence of a completed unified single-pass product.

## Architecture

Keep the native story and Mod rendering paths. The export host owns integer frame advancement and publishes one frame-time snapshot to adapters. Derive sample windows from that same frame position. Resource readiness can hold logical progression; real cancellation and timeouts continue to use wall time.

Single-pass capture submits video during the first and only authored playback. Real offline PCM is spooled contiguously, drained through the exact content end and trimmed to that end before muxing. Spooling/copying PCM is not a second story playback. The first prototype retains this bounded, existing PCM implementation; subsequent profiling determines whether one PCM copy should be eliminated.

Use an explicit `UnifiedTimeline` development build initially (`ExportBuildProfile.SinglePassEnabled`). After native acceptance, promote the same execution path to ordinary Release/Debug. Preserve DevelopmentAudit as a separate development-only mode; never silently fall back from single-pass production to the old audit/replay pipeline.

## Acceptance evidence

1. Unit/behavior tests catch frozen-frame time progression, mixed adapter phases, accidental second playback, unsealed live timelines and mismatched final PCM/frame lengths.
2. Real native fixture emits a complete movie from one authored pass and passes external frame/PTS/PCM/event observations, including the visible effects, voice and background video.
3. Repetition and a cancel-then-export cycle preserve authored output and restore normal AA/Mod behavior.
4. Production has no audit bootstrap, runtime audit environment activation, proof inventories or proof sidecars.
5. Performance report includes preparation, authored playback and finalization under identical configurations; no isolated preparation FPS is presented as total export throughput.
6. Release package, installation receipt, backup and rollback correspond to the native-tested binaries.
