# Held framework scope in Host and Inventory — 2026-10-30

The production Host now holds the actual `FrameworkGeneratedAssemblyScope`.
Controller acquisition completes first, framework acquisition installs its
monitors next, and recipe acquisition/capability capture follows. The same
framework object survives media-state initialization and preparation/replay;
there is no per-frame reacquisition or name-only fallback.

Admission validates before native loading and capture, on Update, at frame
entry and after rendering/audio, at pass sealing (including physical rehash),
and on the final worker. Worker handoff prevents main-thread frame admission
from using the token after terminal publication. The worker validates physical
images again before recipe sealing and acquires guards in this order:

`Startup -> StaticInert -> Menu -> Voice -> Framework -> Recipe`

Guard disposal reverses this order synchronously on the same worker. Recipe
failure still releases Framework. Cancellation and finalization failure dispose
both held proofs, and main-thread cleanup retains any failed release for retry.
The scope engine itself is unchanged from its independent 29-case verification.

Inventory uses `AuthorizesAssembly` on the active held scope. This validates
physical pins and the complete cached graph, then compares the actual captured
Assembly references. Only those two objects receive the framework marker and
stable `IdentitySha256` fingerprint; generated MVIDs do not enter those two
capabilities. They are Observed, not Certified. Missing proof and fake same-name
objects stay Opaque; invalid/disposed proof is Rejected. Unknown generated
assemblies still fail closed. `CanReusePreflight` remains false, and evidence
retains `preexistingCoverage=false`, `allCodeCoverage=false`,
`permitsStrictExport=false`. Story/audio tracing switches were not broadened.

## Fresh managed verification

- Host production source links: 91/91. New coverage includes missing cache before
  source loading, acquisition order, missing scope on a strict frame, late
  mutation, same object across replay, seal rejection, commit ordering/refusal,
  reverse guard disposal, cancellation while the final worker waits, and cleanup.
- Framework engine plus real Inventory source links on actual .NET 6.0.7: 32/32.
  Three added cases cover exact real cached objects/stable capability identity,
  same-name/same-normalized-template impostors, and latched late cache mutation.
  The existing 29 cases still cover real Harmony mutation categories, physical
  pins, reference identity, monitor protection, cancellation, and recipe coexistence.
- Existing StaticInert/Inventory source links: 26/26.
- Core strict capability policy: 25/25.
- Story preparation source links: 5/5.
- Production plugin Release build against `F:\AzureArchive_101`: zero warnings,
  zero errors. This read-only reference build does not install a payload.

The Host tests use fake native/encoder boundaries; the framework tests use real
cached MonoMod objects and runtime. These results do not establish full Default
15-Mod native playback, preserved audiovisual effects, completed two-pass movie,
or real Azurite FPS/VSync handoff. Those acceptance gates remain root-owned.
No AA launch, staging, installation, bootstrap edit, or other-Mod edit was made
by this integration increment.
