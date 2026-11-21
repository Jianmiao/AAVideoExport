# Input-keyed repeat output proof — 2026-10-30

`PrepareStrictIdentity` freezes the existing input-derived key before native
playback: story content, host, loaded capability/configuration fingerprint,
playback/audio settings and export options (excluding output title/directory).
Preparation no longer rehashes this key using observed voice or background
output. The successful receipt remains under that one key.

`RepeatExportBaseline` protocol 2 requires all of these output fields:

- Completed frame count, frame-level row/selection digest and exact raw PCM.
- `EventTraceSha256`: the actual ordered `StrictSandboxTrace` command digest.
- `VoiceReceiptSha256`: SHA-256 of the complete prepared voice receipt JSON.
- `BackgroundCommandsSha256`: SHA-256 of the prepared ordered background commands.

The existing preparation/replay command, voice, background, row and PCM checks
remain in place. The repeat baseline is checked before publishing a preflight,
and checked/published again by the final verified output commit callback. That
callback captures immutable strings, not disposed Unity/session objects.
Changed events with identical frame/PCM output now reject under the same input
key. Real source input changes still select a different key. Missing resource
inputs are not invented: an omitted external asset identity can conservatively
cause a mismatch rather than selecting an unproven new baseline.

Empty observations have real hashes: an empty command trace, serialized `null`
voice receipt when no voice provider participates, and serialized empty
background commands. Missing active trace state fails; it is not replaced by
zeros. This receipt does not independently prove identical encoded pixels or
unobserved third-party behavior; the native decoded-media and authored-effect
acceptance checks remain required.

## Existing receipts and preflight caches

An existing protocol 1 receipt at the same input key fails with
`baseline_invalid`, as do unknown versions or incomplete protocol 2 records.
Validation and commit preserve its exact bytes. There is no implicit upgrade,
overwrite, ignore-existing behavior, version-derived replacement key, or
automatic deletion. Older output-derived keys cannot be reverse-mapped to
missing input metadata; this change does not claim to migrate those files.

The current preflight cache protocol stores row/PCM proof but not the complete
observed event/voice/background proof. Host therefore performs fresh full
preparation and never selects that incomplete cache for replay. Directly
presenting such a receipt at pass start explicitly fails. Existing cache files
are preserved. Preflight publication remains intermediate evidence, not a
successful movie baseline.

## Verification

Focused logs are in `artifacts/repeat-output-baseline-20261030/`:

- `baseline-red.log`: 12 passed, 7 failed, including legacy acceptance and missing output proof.
- `host-red.log`: 92/96; changed key and three output-drift paths failed.
- `cache-red.log`: 96/97; a valid row/PCM-only cache entered replay without event proof.
- Final results are recorded in `verification-summary.json`.

The Host suite compiles production host code with native/encoder substitutes.
These tests do not launch AA or prove actual decoded pixels, Mod effect
preservation, cancellation, Azurite restoration, or installation readiness.
