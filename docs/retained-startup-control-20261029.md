# Retained startup control and early provenance — 2026-10-29

The original goal remains active. This increment implements a sustained,
exact-build AASkipIntro controller and publication synchronization, and proves
dynamic-method attribution feasibility. It does not complete strict export for
the currently blocked Mod set and does not publish a completed movie.

## Runtime changes

`AASkipIntroExportBoundary` checks version 0.1.0.0, MVID
`9a4693f8-010c-4c58-b66d-fad395393afb`, SHA-256
`A78A9BB90FDD61C4A7D32B03AE35B08A8633BDF68B4CFBFEEE5360EEAE770DC6`, the real
postfix signature/owner, the actual Harmony graph (including IL manipulators),
and typed CreatorSplash state. Its observation prefix never suppresses native
Awake or the Mod's SetActive(false) postfix.

The host acquires once before capability capture and retains the controller
across preparation, scene reload, replay, finalization and cleanup. An absent
Mod is an explicit acquired snapshot too, so a late load is not hidden behind
a null lease. Live validation uses the Unity main thread; the encoder worker
checks only synchronized retained proof.

Two review findings were reproduced and fixed:

1. A latched fault used to run before completed cancellation cleanup could be
   processed, leaving Busy/ownership stuck. The host now skips repeated live
   validation while cancelling and finishes cleanup/scene return normally.
2. A pre-receipt check could miss a fault latched during receipt fsync. Core's
   new `AcquireOutputCommitScope` revalidates after receipt persistence and
   holds the same fault mutex through final destination moves and state commit.
   No slow receipt fsync is performed while holding that mutex. Lock order is
   boundary gate then export-session gate; the protected region is synchronous.

The admission inventory has not promoted AASkipIntro to Certified or bypassed
other blocked Mods. Its previous Observed policy remains pending a complete
positive story/movie path. Cache reuse and story/audio trace flags remain off.

## Evidence

| Check | Result and limitation |
|---|---|
| Host lifecycle integration | 55/55, source-linked native/encoder substitutes |
| Controller and real managed Harmony | 25/25; original Mod postfix source preserved in fixtures |
| Actual FFmpeg StrictSession | 18/18, including successful publication, receipt failure, scope refusal and fault during receipt |
| Native retained controller | 11/11; 30 actual Unity Updates, live patch graph invalidation, worker scope, lease restoration |
| Dynamic method observer on AA's actual .NET 6.0.7 | 19/19; 2,412 fixture outputs unchanged; console experiment, not installed bootstrap |

Native evidence: `artifacts/acceptance-20261029-04-startup-controller-final`.
PID 52112, exit 0, no timeout. Isolated lab `native-lab-20261029-startup`,
unchanged AaveRepeat enabled Mod set. Logs verify AA 1.0.1 recognition and
Azurite RenderControlV1 suspension/release. Native assertions do not exercise
a complete story export or prove arbitrary Mod behavior.

- AAVE provider SHA-256: `7B000524E706D3E0FC476382B2C92F177AF3025CC95275390EFFB6C81F5B1A8A`
- Core SHA-256: `98960814500D2FF17E32D68A96E26B23A438F2438EED736C34C09DF0958FDFF4`
- Native fixture SHA-256: `4833DE46B0E549F1F7FB55E9EB2D210AD33C3B4D142A20DD6E3F40DECDD76B20`

Red/green and concurrency evidence: `artifacts/startup-controller-20261029/`.
Dynamic provenance implementation and limitations:
`tools/RuntimeAttributionProbe/` and `docs/runtime-attribution-20261029.md`.

## Next concrete prerequisite

Integrate the observer at the verified BepInEx preloader seam in an opt-in
isolated lab, preserving its explicit coverage-start boundary and UNKNOWN
preexisting creators. Method creation/materialization is not effect control:
direct Invoke, MethodBuilder, native engines, optimized call stacks and other
unobserved paths remain open. Do not exclude the entire anonymous holder or
claim all dynamic code safe based on its name.

Then connect exact live adapter proof to admission and run an actual nontrivial
story twice with its effects present, asserting frame/audio/event correspondence,
PCM/PTS, source preservation and Azurite restoration. That positive native
movie path is still pending; refusal tests or this adapter test do not satisfy it.
