# Decoded media content acceptance — 2026-10-30

Native StoryExportChecks no longer treats equal identity, counts and duration
as sufficient evidence of repeated movie content. VerifyMedia retains its
independent ffprobe PTS/count/start/end checks and now also decodes each
completed movie through the test-only DecodedMediaFingerprint helper.

## Exact content evidence

- Video is decoded to raw frames in the independently probed native pixel
  format. No generic RGBA8 conversion, scaling or precision reduction is used.
  FFmpeg emits SHA-256 framehash records with preserved input timestamps.
  The helper validates dimensions, the exact number of records, consistent
  positive frame byte counts, and every frame's PTS/duration grid.
- A readable TSV records each zero-based frame index, reduced rational PTS and
  duration, byte count and frame SHA-256. The original framehash text is kept.
  The compact video content digest is SHA256 of the ordered binary per-frame
  SHA256 values; the timeline has a separate digest.
- Audio is decoded to signed little-endian PCM16 at the independently probed
  rate/channel count. Streaming SHA-256 covers every PCM byte. Byte count must
  equal sample frames × channels × 2, with no added or omitted samples.
- Both decoders hold the input file read-only throughout on Windows and run
  with bounded deadlines/cancellation. Evidence output uses new files. Partial
  work is distinguishable from a completed fingerprint summary.

Repeat acceptance compares the full decoded layout, exact counts, actual
timestamp digest, ordered frame-content digest and PCM16 digest. A video
mismatch identifies the first differing zero-based frame using the TSVs.
Container byte length/SHA remains recorded only as provenance; neither is a
content-equivalence condition. Lossless remuxing is therefore allowed.

The success marker is now:

```text
PATH-CHECK PASS repeated-strict-identity-frame-sample-duration-decoded-content-match
```

## Fresh real-media regression

The initial count-only comparator produced **5/7**, correctly exposing two
failures: equal-length files with one changed decoded video pixel and one
changed PCM16 value were accepted. After the content checks both fail as
required, while an unchanged-content remux passes.

The final source-linked suite is **9/9**:

1. Decoded native BGRA plane hashes and PCM16 hash match independently authored
   bytes, and all readable evidence files contain the expected records.
2. A single changed RGB value in zero-based frame 2 is rejected and localized.
3. A single PCM16 value change is rejected despite equal sample count/duration.
4. Different container bytes from a lossless remux pass content equivalence.
5. Real H.264/YUV420P plus MOV/PCM16 survives independent remux comparison.
6. Incorrect expected video record count is rejected.
7. Incorrect expected PCM sample count is rejected.
8. A nonzero source presentation start is preserved and rejected.
9. A cancelled decode cannot publish completed fingerprint evidence.

Logs: `artifacts/decoded-media-acceptance-20261030/decoded-content-red.log`,
`decoded-content-green.log`, and `decoded-content-final.log`. The same directory's
`verification-summary.json` points to the final fixtures/manifests and records
their identities.

## Scope

Only the new native-test helper, StoryExportChecks' verification blocks and
MediaEvidence/call signatures, and the standalone regression project changed.
The existing top-level background/translation Install and Finish checks were
retained. No production code, cache reuse, tracing, output tolerances or
admission policy changed. No AA native run or installation was performed by
this increment; the rebuilt native fixture must now exercise actual repeated
story exports and report the decoded-content result honestly.
