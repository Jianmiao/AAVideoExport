# Effective playback input proof — 2026-10-31

## Reported failure and cause

A reported 198-record story was refused before capture with
`timeline_source_changed: Effects source input: No concrete selected-archive
row has a fully trusted original project binding.` This was an initial
source-acquisition failure, not evidence of a change between two passes.

The original installed editor mapper found 188 individually trusted mapped
rows, but its whole-project flags were false: the project timestamp was later
than publication and two empty, non-command first scenes were not mapped.
The original native-project and published-archive content hashes still
matched their publication record. More importantly, the original formal
embedded command compiler/binder accepted all 13 commands in eight batches.

The exporter's initial input gate had conflated editor-wide mapping
permissions with the actual playback compiler's accepted input.

## Bounded correction

The legacy four-flag-valid path remains unchanged. For an individually
trusted mapped row whose editor-wide flags fail, the additional proof:

- Calls the inspected original effective playback loader; does not change
  the original options, pair, mapping flags or playback index.
- Covers the enabled embedded scene-command branch only. External workspace
  manifests and backup manifests require a separate proof and remain refused.
- Compares every original index row/batch, archive path key/hash and count
  with the actual ready runtime index, plus continuation identities, immutable
  projection, registry revision, options and storage revision.
- Retains exact selected AAS, original AAP, validated native-project inputs
  and any original compilation mapping as read-only file handles. Original
  missing dependency paths are observed for appearance, not described as locks.
- Includes the effective command proof and complete retained read-set in
  the stable export identity and re-proves them before replay.
- Does not request a redundant index reload after proving the ready index.
  Original reload processing would clear commands and continuation state.

The host can wait for an observable original load without releasing the
initial native advance or creating an offline capture scope. Cancellation
and a fixed 120-second deadline apply. It validates the retained runtime
again immediately before the first authored advance. Index replacement,
reload, options, registry or continuation changes during capture are refused.

The initial missing-binding error now has its own source-unavailable Chinese
message; actual changes after acquisition retain `timeline_source_changed`.
The public RenderControlV1 protocol remains version 1.

## Verification status

Evidence directory: `artifacts/effects-playback-proof-20261031`.

- Held-input behavior: RED 21 passed / 5 failed; GREEN 26 / 0.
- Actual original Core/Formats input and production proof: RED 1 / 7;
  final GREEN 9 / 0, including preservation of the legacy branch.
- Production-source host lifecycle: RED 102 / 9; final GREEN 113 / 0.
- Core presentation regression: GREEN 28 / 0.
- Plugin build: zero warnings/errors. RenderControlV1 public ABI passes.
- Native old-build replay on the actual reported story reproduces the exact
  missing-trusted-binding failure in `native-user-red2`.

Native candidate execution passed the actual reported story's acquisition
gate and captured more than 800 authored frames. That long run was
intentionally stopped; it is not a full export acceptance result.

The separate native-authored fixture keeps all source file bytes unchanged,
but its copied AAP/AAP2 timestamps are later than its copied AAS. Original
editor-wide freshness/pairing flags are false, so it exercises the new proof
branch. `native-stale-fixture-repeat2` passes **7/7**: one cancellation and
two complete movies, five preparation/replay passes, original effect event
and transform checks, source preservation and actual Azurite restoration.
Both movies have 453 frames, 724800 sample frames and 15.1-second durations;
decoded frame contents, timestamps, PCM and input identity match. Both MOV
SHA256 values are `2FB778C0A8901883CFF7DFC007A9B9A0FE46F0AB853DF51BFB29862BC96C5043`.

This native acceptance uses 640x360, 30 fps, software libx264 and PCM16. It
does not establish a complete long-story export or the user's 1080p/60 fps
Intel hardware configuration. The original source-acquisition defect is
reproduced and corrected independently of those encoder settings.
