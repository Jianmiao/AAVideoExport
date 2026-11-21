# Exact DynamicMethod final-IL recipe v2 — 2026-10-30

The current implementation replaces the unstable global Emit-detour strategy.
It is not a claim that all Emit calls, failed Emit attempts, or transient
non-executed generator mutations were observed. The earlier strategy and its
native/isolated crash evidence are retained in `artifacts/runtime-recipes-20261030`.
They are not release evidence for this replacement.

## Exact current contract

- API type: `AAVideoExport.RuntimeBootstrap.RuntimeRecipeV2`, protocol `2`.
- Scope schema: `aave.runtime-recipes.v2`.
- Production policy: `aa101.interop-initializer.intptr.final-il.v2`.
- Required evidence kind, on both scopes and individual recipes:
  `PRE_MATERIALIZATION_FINAL_IL`.
- `RuntimeRecipeV2.EvidenceKind` exposes the same exact string.
- `preexistingCoverage`, `allCodeCoverage`, and `permitsStrictExport` remain
  false in both v1 and v2. Neither API alone grants whole-Mod or movie admission.
- Client scope validation, individual-delegate lookup, native helper, retained
  Host scope and final sidecar reject the old typed-emission policy/evidence.

The AA 1.0.1 / .NET 6.0.7 / Il2CppInterop 1.5.3 host/native/runtime/preloader pins
remain fixed. The current story/render output manifest is the explicit 20-image
`InteropImageManifest` revision `aa101-story-render-explicit20-20261030.v1`,
containing exact SHA-256/MVID/version triples rather than a directory rule.
Its source-input context also pins Generator, Cpp2IL, global-metadata.dat and the
current cache marker; the marker does not authenticate generated output bytes.
The manifest and payload affect PolicySha256 while the final-IL strategy name
remains unchanged. See `artifacts/runtime-recipes-flatbuffers-20261030/README.md`
for the current selection, FlatBuffers audit, red/green tests and native candidate.
Production qualification still requires the
actual AA host process, not a standalone process pointed at an AA directory.

## What the replacement proves

1. Actual anonymous DynamicMethod construction must have been seen on the stack
   of the pinned real Interop initializer factory. Name-only or same-holder
   matching cannot manufacture provenance. Missing construction, unknown
   producers and preexisting cached methods remain unqualified.
2. Before the original CreateDelegate materializes the method, `FinalIlSnapshot`
   reads the method's existing `m_ilGenerator`; it does not call GetILGenerator
   to manufacture evidence. The generator must belong to the exact method.
   `m_methodHandle` and `m_resolver` must still be null. `m_DynamicILInfo` must
   be null, because this runtime prioritizes that raw path if it exists.
3. The actual final body must be exactly seven bytes encoding
   `ldarg.0; newobj <constructor>; ret`, with no locals, exceptions, labels or
   fixups. The real dynamic token must be a method token in the actual scope,
   resolving to the public IntPtr constructor, including a closed generic
   `GenericMethodInfo` context. Unknown representations, indices and signatures
   are rejected. These bytes and constructor semantics are frozen, not inferred
   from a log of intended Emit calls.
4. The requested unbound `Func<IntPtr,T>`, actual input/return types, mutable
   redirect value and recursive constructor closure must match the pinned
   factory policy. Each intermediate IntPtr constructor must only call its
   direct wrapper base, ending at the pinned Il2CppObjectBase/native GC-handle
   leaf. Different targets and extra constructor behavior remain outside scope.
5. After CreateDelegate returns, the actual `DynamicResolver.m_code` must equal
   the frozen pre-materialization bytes. Resolver method/scope, constructor
   token semantics, locals and exception state must agree. The actual returned
   delegate's `RTDynamicMethod.m_owner` must be that exact DynamicMethod.
   A restored generator cannot hide different materialized code.
6. Find, retained-scope validation, Seal and Commit recheck the actual resolver,
   method handle, generator, signature, token semantics, current redirect,
   actual cached returned delegate and protected member state. Discovered
   divergence latches the record/scope invalid; restoring bytes or a patch
   later does not heal that receipt. The frozen byte hash is never replaced.

Initializer identities and the exact host/class-pointer-store boundary remain
recorded. This does not prove that all earlier type-initializer execution was
observed or that native initialization is side-effect-free. The unchanged
constructor-closure algorithm previously verified all 94 historical target
metadata chains without invoking their factories, initializers or constructors;
real current emissions still require the separate native acceptance.

## Hook and mutation boundaries

Exactly 14 v2 hooks are installed for the process lifetime:

- 8 public DynamicMethod constructors;
- 2 non-generic CreateDelegate overloads;
- 1 declared DynamicMethod.Invoke overload;
- 3 high-level Harmony PatchProcessor Patch/Unpatch entry points.

There are zero ILGenerator or DynamicILGenerator detours, and no GetILGenerator
or GetDynamicILInfo detours. The same 52 concrete emitter/mutation methods are
retained as a protected metadata set, together with the generator/raw accessors:
current foreign patches are rejected and high-level Patch/Unpatch attempts are
latched. Transient protected constructor/factory patches cannot disappear
between polls and restore a receipt.

A known producer may be preparing its method while a scope collects. It cannot
be reported QUALIFIED without both pre-materialization proof and actual resolver
confirmation. Seal requires every record complete. Unknown methods, missing
associations, reentrant callback loss, original construction/materialization
failures and record/delegate/parser/scope overflow fail closed.

There is no claim to retain an entire Emit API history. Ordinary Emit exceptions
still propagate normally; the final code and missing delegate are the evidence
boundary. An already-held generator can be edited without an Emit callback.
Such edits do not alter the runtime's copied materialized code; current-state
validation conservatively rejects detectable source divergence. New executable
materialization remains guarded. Arbitrary direct native detours, reflective
memory rewriting between checks, and ordinary typed Delegate.Invoke are not a
whole-process sandbox and must not be described as intercepted globally.

Outside publication guards the original method runs normally: invalid recipes
remove export evidence rather than suppressing a Mod's ordinary behavior.

## Retained scopes and publication

Public APIs remain `GetSnapshotJson`, `GetRecipeJson(Delegate)`,
`AcquireScopeJson`, `ValidateScopeJson`, `SealScopeJson`,
`ValidateScopeForCommitJson`, `ValidateHolder(string, Assembly)`,
`EnterCommitScope(string, CancellationToken)` and `ReleaseScope`.

The holder check uses ReferenceEquals against the actual observed method's
Module.Assembly. A same-name dynamic AssemblyBuilder cannot inherit permission.
The policy digest includes the new final-IL strategy, exact payload/image pins
and bounds. The sealed recipe digest uses sorted semantic code/constructor
identities, not tokens, process ordinals, anonymous MVIDs or native addresses.

Acquire locks in the existing order:
`Startup -> StaticInert -> Menu -> Voice -> Harmony PatchProcessor.locker -> recipe gate`.
Recipe is innermost and disposed first; no controller is acquired under it.
Callbacks only latch their own state and never call Host.Fail or Unity.

The synchronous publication guard holds the inspected Harmony mutex and recipe
gate. Other-thread anonymous construction/materialization/Invoke and Harmony
mutations cannot cross it; same-worker attempts throw and invalidate the scope.
Do not await inside the guard. Waiting is cancellation-aware at 25ms intervals.
The acquiring thread consumes the token before releasing its locks. A worker's
finally may still call ReleaseScope idempotently. `GetPatchInfo` uses a distinct
PatchInfos lock and is not falsely described as taking PatchProcessor.locker.

## Evidence for this replacement

Initial final-IL strategy evidence: `artifacts/runtime-recipes-final-il-20261030`.
The subsequent fixed story/render manifest has separate current evidence in
`artifacts/runtime-recipes-flatbuffers-20261030`; its actual native ByteBuffer
qualification remains a root-owned acceptance step.

- Production parser: 17/17 real DynamicMethod/raw/resolver checks. An independent
  intentionally defective copy that omitted token, private-precompiled and
  actual-resolver validation produced seven expected failures; the production
  helper then passed all 17 again. The defective copy is labeled negative
  control only and must never be staged.
- Ten fresh pure-v2 processes: 46/46 each, actual AA .NET 6.0.7.
- Ten fresh v1+v2 processes: 47/47 each, same payload and runtime.
- Includes actual Interop factory, generic token context, wrong code/token/
  signature/owner, unknown and missing producer, already-private-materialized
  method, restored generator with changed resolver, post-materialization source
  and resolver changes, redirect/cache mutation, holder reference spoof,
  protected patch history, overflow, cancellation and real Menu/Voice/recipe
  guard composition including DisposeBackend-first lock ordering.
- v1 regression 36/36; client contract checks 6/6; updated plugin and native
  helper compile with zero warnings/errors.
- Two fresh processes with opposite creation order retain equal policy and
  semantic recipe-set digests despite different scope tokens.

These are isolated managed results, not native acceptance or a claim that the
AA crash has been finally repaired. Root owns native staging, current-profile
preservation, Azurite host recognition/binding and FPS/VSync handoff verification.

## Root-owned native entry

The independent native component remains
`tests/VideoExport.NativePath.Tests/RuntimeRecipeChecks.cs`; it strictly requires
the new policy and evidence kind for every scope and current recipe.

Use the guarded root runner with:

```text
AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION=1
AA_VIDEOEXPORT_RUNTIME_RECIPES=1
AA_VIDEOEXPORT_RECIPE_BASELINE_TARGETS_JSON=<absolute current-94-constructor-metadata.json>
AA_VIDEOEXPORT_STARTUP_TRACE_FILE=<absolute diagnostic file in an existing directory>
```

The helper waits for Catalog, reads already-created initializer caches without
calling getters to manufacture evidence, checks every current delegate/holder
and expected historical target, validates across 15 Updates, seals, exercises
the worker guard with a diagnostic marker only, verifies consumption and then
reacquires a healthy scope. It creates no fake DynamicMethod and no movie.
Startup trace records are appended and flushed to disk independently of JSON,
BepInEx logging and Unity. No AA launch or staging is performed by the managed
runner or by this bootstrap implementation task.
