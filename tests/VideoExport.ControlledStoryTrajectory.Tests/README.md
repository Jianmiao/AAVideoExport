# Controlled story trajectory comparison

This source-linked regression exercises the native acceptance observer's pure
event/receipt comparator. It does not launch AA, render effects, establish
native clock interception, or prove movie cancellation/restoration.

Run:

```powershell
dotnet run --project tests/VideoExport.ControlledStoryTrajectory.Tests -c Release
```

The observer now requires every preparation/replay pair and every completed
attempt to have the same ordered authored event records, complete voice
receipt (including sample positions and asset identity), and background
commands. A mismatch reports its first JSON path and compared attempt/pass.

Normalization is explicit and closed: JSON object property order does not
matter; `pass-begin` Attempt/Preparation and `original-layer-rendered`
layer/image pointer addresses are omitted. Every frame, sample position,
event order, directive, visibility, background source frame, and semantic
layer ordinal remains exact. There is no wall-time field in the accepted
schema. Unknown event kinds, new/missing fields, duplicate JSON properties,
negative event frames, empty trajectories, and incomplete pairs fail closed.
New event kinds must receive a reviewed schema rather than being discarded.

Successful `controlled-story-export-evidence.json` files contain
`TrajectoryComparison`, including readable normalized records and their
SHA-256 digests. Raw diagnostic events are also preserved. The production
`RepeatExportBaseline` is unchanged by this test-only work; decoded media
verification and actual native restoration remain separate acceptance gates.

Initial regression against the extracted old within-pair comparison:
`artifacts/event-trajectory-acceptance-20261030/red.log` (1 passed, 18 failed).
The corrected comparator reaches 19 passed, 0 failed in `green.log`.
