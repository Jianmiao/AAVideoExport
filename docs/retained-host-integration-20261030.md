# Retained menu, voice and recipe Host integration — 2026-10-30

This increment implements the shared Host path and its tests. It does not
certify a successful full Default-profile movie; root-owned native validation
is still required, including the replacement bootstrap described below.

## Current recipe contract

The old broad CoreCLR Emit-detour candidate is not deliverable after the native
crash investigation. This Host now accepts only:

- policy `aa101.interop-initializer.intptr.final-il.v2`
- evidence kind `PRE_MATERIALIZATION_FINAL_IL`
- schema `aave.runtime-recipes.v2`, protocol 2
- scoped marker `RetainedRecipeScope.interop-initializer-final-il.v2`

RetainedRecipeScope uses the client's exported expected-policy/kind constants.
The Core sidecar rejects old policy or evidence-kind values too. The existing
v1 attribution interface and RenderControlV1 remain separate. The three recipe
claims `preexistingCoverage`, `allCodeCoverage`, and `permitsStrictExport` remain
false, and preflight reuse/story tracing/audio tracing remain disabled.

## Host order

1. Resolve optional adapters and attach the actual CharacterVoice session
   controller. Validate the exact authored-delay Gate binding. Acquire the
   startup and static-inert controllers and the retained menu/resource lease.
   Unfinished original custom BGM loading waits automatically and can cancel;
   successful scopes are retained instead of reacquired each Update.
2. Acquire the framework generated-assembly scope after those controller hooks
   exist, installing its independent monitors before acquiring the recipe scope;
   then capture capability evidence. Voice admission requires the held instance, not a
   global compatibility flag. Recipe admission requires the actual observed
   holder Assembly object, not its display name.
3. Freeze/load the selected native story. After its preparation worker has
   produced the story hash, extract the verified concrete GenericScenarioExcel
   voice identifiers and bind the voice scope once, before native CoLoadSave.
4. On each actual player entry, run the beauty Mod's original non-Catalog Update
   before NativeCaptureScope snapshots. Prepare/pump native voice preload with
   ordinary native clocks and resource coroutines still running. No offline
   scope, capture clock, frame or authored advance starts until resources are
   ready. Warmup does not consume the first-frame capture timeout.
5. Run the ordinary mixer primer; after ResetAudioTimeline, arm the voice pass
   at frame/sample zero and only then execute the first authored AdvanceScenario.
   Coordinates use the read-only NativeCaptureScope.AudioRawSampleFrames cursor,
   not a Unity pointer or a DSP epoch. Each armed frame validates the controller
   and both exact delay-clock Harmony/Gate bindings.
6. Seal the preparation voice receipt and retain it on that same scope. Replay
   uses that receipt, checks the concrete identifier set again, and seals the
   matching result. Menu, static, startup, voice and collecting recipe scopes
   survive the scene roundtrip.
7. End native playback and restore pass state. The final worker validates media
   and earlier receipts, then seals the same recipe scope for its terminal
   proof. Main-thread frame admission no longer uses a token handed to that
   worker, including after the guard consumes it.
8. Publication scopes enter Startup → StaticInert → Menu → Voice → Framework → Recipe.
   Recipe itself takes the actual Harmony mutex then its recipe gate, last.
   Disposal reverses that order on the same worker. Cancellation/finalization
   failure releases the token; failed release/cleanup references remain retryable.

Retained callbacks synchronously latch invalidity. Host stops the next frame
and performs Cancel/Dispose at a safe Host boundary, rather than recursively
disposing other controllers inside an unknown observer stack. The originating
controllers also latch their own worker-publication fault. Checks occur before
frame acceptance, after authored callbacks/native rendering/audio, and again
inside the publication-scope composition.

## Menu/resource boundary

New independent files are `RetainedPlaybackProof.cs`, `RetainedPatchGuard.cs`
and `RetainedMenuPlaybackBoundary.cs`. Shared wiring is in the new Host partials
and minimal Plugin/Inventory/Core edits.

- MenuBgmSelector's original PlayBGM/SetBGM id handling, custom clip substitution,
  original prewarm/Poll, and settings effects remain active. Selection, native
  cached clip identity, source bytes and configuration are retained. Its exact
  rename-worker write boundary waits for release and then executes the original
  write; no assertion relies on total process-thread counts.
- AAMenuPrototype performs its own scene exit, including voice/request/queue,
  native UI/camera and renderer restoration. During playback the proof requires
  those menu-only resources to remain absent; the component is not disabled.
  Normal Catalog animation/rebuild behavior remains outside playback.
- SfxFolderMod's original name filtering, Chinese lookup and item decoration
  are preserved. The loaded mapping, ShowId and configuration are retained;
  a still-live editor SoundExplorer in story playback is not silently ignored.
- Exact versions, MVIDs and SHA-256 are checked. Original native dispatch and
  observer graphs cover all Harmony patch kinds/owners. A late change invalidates
  the same scope permanently. Cleanup removes only this observer's owner and
  retains incomplete cleanup for retry.

Current binary audit material is under
`artifacts/retained-menu-boundaries-20261030/`. The BGM rename coverage check
also requires the unchanged startup display-name configuration. A changed or
unprovable startup rename context is explicitly unsupported by this boundary;
it is not claimed as arbitrary preexisting-thread coverage.

## Dynamic inventory and identities

Dynamic/pathless assemblies are examined before infrastructure-name exclusions.
Only the actual holder validated through the held client proof receives the
factory marker. Its module fingerprint is the stable policy SHA; anonymous MVID,
scope token and accumulating recipe count do not enter preparation identity.
The sealed recipe hash is terminal evidence, not a reason to mutate prep identity.

The two actual cached framework assemblies, `MonoMod.RuntimeDetour.Runtime.NETCore3+Helpers`
and `MonoMod.Utils.Cil.ILGeneratorProxy`, now require the same held
FrameworkGeneratedAssemblyScope from acquisition through both passes and final
publication. The complete pinned cache/template proof authorizes only the exact
Assembly references. Their stable scope digest replaces random generated MVIDs
in capability identity; they remain Observed and cannot enable preflight reuse.
Missing scopes and same-name/same-template counterfeits remain Opaque; a changed
or disposed held scope is Rejected. See `framework-generated-host-integration-20261030.md`.

## Terminal sidecar

Core can publish `<movie>.runtime-recipes.json` inside the existing commit guard.
The private receipt is flushed before entering the short publication scope;
non-overwriting renames happen under that scope. Two filesystem renames are
**not** a single filesystem transaction. The sidecar alone is never movie-success
proof: it records the expected movie basename, byte length and SHA-256, and
`RuntimeFactoryRecipeReceipt.VerifyPublished` requires the associated movie to
exist and match. Native StoryExportChecks uses that verifier, then separately
compares decoded frame/PCM content.

Failure rolls back only a sidecar moved by this session with its matching
ownership id. Existing/replacement user files are preserved. Sharing/ownership
rollback failure reports the residual path and keeps retry state. The sealed
policy/recipe hashes and all three false claims are written explicitly without
serializer-generated accessors in the terminal writer.

## Evidence status

- Host production lifecycle source links: 83/83, with fake native/encoder/remote
  boundaries. Covers warmup, both passes, no scope reacquisition, late failures,
  safe callback cleanup, final guard order and cancellation/finalization release.
- Retained menu/proof/real-Harmony engine: 14/14; current installed binding
  metadata/version/MVID/SHA shape: 3/3. These are not native playback results.
- Actual CharacterVoice Gate/Harmony delay binding tests: 22/22.
- Retained recipe client-contract wrapper: 6/6; actual Inventory source links:
  26/26, including actual-object versus same-name impostor and stable identity.
- Core sidecar association/ownership tests: 8/8 after final-IL migration.
- Core actual FFmpeg strict-session tests: terminal integration added three
  failures to the original 18 tests, then reached 21/21; a fresh final-IL run is
  recorded separately.

The earlier real Menu/Voice/recipe lock-composition checks exercised two actual
interleavings, including Voice.DisposeBackend with actual Harmony.Unpatch. Their
old-bootstrap pass is not proof that the crashed Emit-detour candidate is safe;
the replacement bootstrap owner must rerun the composition/native acceptance.

Standalone native menu fixture `RetainedMenuPlaybackChecks` preserves real
Catalog animation over 30 Updates, calls original BGM/Sfx logic, checks a latched
late patch mutation, and releases its lease. It is ready for root routing/build;
this agent did not start AA, install/stage a candidate, or modify other Mods.
