# Strict preflight cache and single-render design

## Goal

Keep strict synchronization while removing the second full video render. A
strict export performs one cacheable, headless timeline preflight and one real
camera/video render. Repeated exports reuse the immutable preflight only when
the story, host, loaded Mod identity, settings, audio clock and sandbox
protocol all match.

## Guarantees

- Every successful strict output uses one AAVE-owned video timeline.
- The preflight records story state, event order, native DSP sample windows and
  exact content PCM; the render pass verifies live execution against it.
- A cache hit never skips live frame/audio verification. It skips only the
  headless preflight playback.
- Any source, settings, host, loaded-Mod, adapter or protocol change invalidates
  the cache atomically.
- Missing, stale, corrupt or semantically incomplete cache data fails closed;
  it cannot silently fall back to an unverified export.
- No silence padding, arbitrary trimming or audio tolerance is allowed.
- `RenderControlV1` protocol version 1 and Azurite ownership lifecycle remain
  unchanged.

## Runtime shape

`StrictPreflightReceipt` is an immutable metadata record containing protocol
version, identity, frame/fps/audio parameters, frame-state trace hash, PCM hash,
source hash, host identity and Mod/sandbox fingerprint. The receipt is
published with a sibling PCM file and frame transcript using create-new files,
flushes and an atomic non-overwriting rename. A cache reader accepts only a
complete matching set.

The first export runs the current audio-preparation pass without submitting
video frames. At the native story end it seals the raw DSP window, copies the
content PCM into the persistent cache, seals the state transcript and publishes
the receipt. The scene returns to the catalog and the same export then runs one
actual video replay. The replay still captures native raw PCM and validates the
cached content hash before media finalization.

On a cache hit, the host opens the sealed PCM/transcript, obtains the fixed
frame plan, and goes directly to the one actual replay. The live replay must
still reach the native End fence and exact audio endpoint. Cache reuse therefore
reduces wall time without weakening strict validation.

## Identity and invalidation

The identity is SHA-256 over the story bytes, AA host identity, active strict
settings, audio sample rate/layout, export timing options, AAVE ABI/protocol,
loaded Mod assembly fingerprints and sandbox adapter capability fingerprint.
The identity must not be based on display names or paths alone. A changed MVID,
assembly hash, profile, load order, host binary, AAVE protocol or audio setting
creates a new cache key.

## Failure behavior

- Cache missing: run the headless preflight and publish a new cache.
- Cache malformed or mismatched: preserve it as evidence, discard it for use,
  and run a fresh preflight.
- Preflight shortage, gap, overlap, unknown event, replay divergence or failed
  restoration: no cache publication and no output.
- Cache hit with live replay mismatch: delete only the new temporary output,
  preserve the old cache, and report the exact mismatch.

## Scope of this increment

This increment does not claim that arbitrary native or dynamic third-party Mod
behavior is automatically convertible. It adds the cacheable deterministic
preflight seam that the sandbox runtime will use. Unknown behavior remains
strictly unverified until its adapter produces a complete receipt.

## Verification

Core tests cover receipt identity, atomic publication, corrupt/mismatched
cache rejection, PCM/transcript ownership and reuse. Host tests cover cache
miss preflight, cache hit skipping the headless pass, identity invalidation and
live replay mismatch. Native AA acceptance must show one preflight followed by
one actual render on a miss, then one actual render on a hit, with identical
frame/sample counts and PTS.
