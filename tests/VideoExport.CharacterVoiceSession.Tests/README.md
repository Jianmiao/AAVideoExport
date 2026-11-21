# Known CharacterVoice session receipts

This finite adapter observes the exact installed CharacterVoice 1.6.8 build
already audited by `CharacterVoiceDelayClock`. It does not replace that delay
adapter, patch its Gate targets, change watchdogs, forge native playing state,
wrap/replay native callbacks, change user mute, or authorize arbitrary Mods.

`CharacterVoiceSessionTrace.cs` is a pure event/state module used by the native
bridge and its executable tests. `CharacterVoiceSessionAdapter.cs` observes the
real installed Runtime through four owner-scoped Harmony targets with pinned
binary/MVID/original-IL identities:

- `SetVoicePrefix(string)` postfix: a real accepted request, after original Gate Begin.
- `ObserveOwnedPlayback(bool)` prefix/postfix: original Starting→Playing and Playing→PostPlaybackDelay/Completed transitions using the actual native boolean argument.
- `TickOnMainThread()` postfix: original authored-delay completion or watchdog failure.
- `ResetOnMainThread(string)` postfix: reject a canceled/replaced unfinished request rather than inventing its end.

The observations are main-thread scoped to the actual current non-preview
story player and its native voice channel. Provider sequence numbers, player
pointers, source pointers and clip pointers guard one pass only. They are absent
from receipts. Stable identity uses the frozen story SHA-256, role
`character-voice-channel`, canonical owned identifier, original file SHA-256,
actual decoded PCM SHA-256 and clip format. Repeated identical voices receive
separate pass-local request ordinals. Each request must have exactly one
Request, NativeStart, NativeEnd and DelayComplete event, with monotonic frame
and sample coordinates. A zero-delay end completes in the same frame; a 0.1s
delay releases at ceil(FPS/10) frames, not at a rounded wall-time threshold.

## Host integration contract

The files do not edit Host, Plugin, Core, Inventory, settings or installed Mods.
Root integration must preserve these operations and their order:

1. Before any story read, call `CharacterVoiceSessionAdapter.Attach(onFault, log)`
   on the main thread and retain that exact instance. `ValidateController()`
   checks its actual installed Runtime and four hooks; freeze the export's
   structural patch/capability fingerprint only after this attachment. The stable
   hook owner is `halocue.aa.videoexport.character-voice-session`. Attachment is
   not permission to capture an unbound story.
2. Follow the real native selected-story path and freeze its
   `GenericScenarioExcelTable`. Obtain IDs with
   `CharacterVoiceSessionAdapter.CollectKnownIdentifiers(frozenSave)`.
   Every row is strictly cast to the actual `GenericScenarioExcel` type and read
   through its concrete `VoiceJp` getter. This host's interface getter dispatch
   was observed to enter an unrelated SmallPortrait getter; no interface fallback
   is permitted. Only asset IDs are deduplicated; repeated playback requests remain.
3. Call `scope.BindStory(storySha256, ids)` exactly once after source inspection.
   Binding only freezes immutable data and never changes hooks. Prepare, preload,
   BeginPass and ValidateFrame refuse an unbound scope. Rebinding or attempting
   preload before binding permanently faults the held scope; constructing another
   instance must not be used to conceal a failed preflight.
4. `PreparePass()` then repeatedly call `PumpPreload()` from normal main-thread
   Updates until true. It issues original `CoTryPreloadVoice` at most once per
   declared ID in that warmup, honors provider NativeLoads ownership, waits for
   the actual cache clip's Loaded state, and pins both file and decoded PCM.
   It never clears caches or pending ownership. Failure is bounded at 20 wall
   seconds and must cancel the export. The native preload/watchdog policy is unchanged.
5. Warmup occurs before authored frame zero. Prefer it in the existing main
   preparation state before encoder/strict identity work; alternatively hold
   `_strictAwaitingStart` while pumping. Do not accumulate authored frames or
   authored sample coordinates during warmup. After readiness, finish the
   normal rational mixer primer and reset the raw audio epoch/counter.
6. Before releasing the first native author Advance, call
   `BeginPass(player, fps, sampleRate, positionProvider, expectedReceipt)`.
   The provider must initially report frame=0/sample=0 and later return
   `new CharacterVoicePosition(_captured, relativeRenderedSampleFrames, player.cur)`.
   The sample coordinate is the cumulative rendered PCM interval relative to
   the authored raw epoch, including legitimate zero-length ticks. It must not
   be `AudioSettings.dspTime`, a wall clock, a pointer, or fabricated samples.
7. Call `ValidateFrame()` before frame commitment and `SealPass(endPosition)`
   at the strict pass seal. The returned immutable receipt is retained between
   scene reloads. On replay repeat warmup, recheck the same asset manifest, reset
   clocks, and provide the preparation receipt to `BeginPass`. Every observed
   event and the final coordinates must match exactly; missing/extra events,
   changed asset/settings/gain/pitch or one-frame/sample drift fail closed.
8. `EndPass()` precedes native teardown on cancellation. It only invalidates
   this observer; `CharacterVoiceDelayClock.EndExport`, original voice reset,
   native resource ownership and RenderControlV1 restoration remain their owners'
   responsibilities. Dispose the retained session scope only when the entire
   export is done; failed unpatching remains retryable and blocks a new scope.

## Output publication barrier

The retained scope records its own successfully sealed preparation and matching
replay receipts. `BeginPass(expected)` cannot begin replay without that scope's
preparation, and a third pass cannot overwrite the proof. After both seals,
call **`SealForCommit()` on the main thread**. This freezes the voice resource,
provider DLL and controller DLL file digests, assembly identities and four Runtime
plus two delay-Gate managed Harmony graphs. It calls the real main-thread
`CharacterVoiceDelayClock.ValidateBinding()` before freezing those facts.

The encoder worker may then call **`ValidateForCommit()`** and obtain
**`AcquireCommitScope()`** immediately around final atomic publication. Neither
entry point dispatches to Unity, reads live Runtime/Gate/clip/player state, nor
calls a main-thread validator. They verify the immutable matching two-pass
receipts, managed assembly/Harmony identity, latched failure/disposal and pinned
files. The acquired scope retains a short monitor lock and read-only file handles
with `FileShare.Read`; failure and disposal share that lock. It must be disposed
synchronously on the same worker, with no await/mux/receipt-writing inside it.

The controller remains attached through final publication. `EndPass()` between
successful passes does not remove hooks; an unsealed pass end permanently faults
publication. Asset or hook change, cancellation, worker validation failure,
disposal, mismatched replay or an attempted replacement pass cannot be repaired
by calling SealForCommit again. Explicit no-owned-voice stories also require two
successful matching pass receipts before publication.

Unknown or uncached owned requests during authored capture are errors, not a
reason to freeze halfway through a line or replay its native SetVoice call.
Cold loading therefore cannot move an authored request in this supported path:
both passes start with the same declared, decoded native assets. The real
cached callback timing must still be checked by an actual whole-story run.

If enumeration of the actual frozen source produces zero owned IDs, BindStory
creates an explicit `NoOwnedVoiceExpected` scope. The four real observation hooks
stay retained and validated, and both full passes may seal empty-event receipts
with that explicit flag and matching final coordinates. Any actual owned request
or active owned Runtime Gate immediately faults the scope and prevents a receipt.
This is a proof about one frozen no-owned-voice story, not general CharacterVoice
compatibility and not a request to disable the Mod for ordinary stories.

The generic `StrictAudioCommandHooks` currently records native pointers and
absolute scheduled DSP arguments. It cannot be used as the stable voice identity
for a scene-reloaded replay. Host wiring must use this scoped voice receipt for
the known owned channel and retain the separate native/raw PCM ledger checks.
Do not weaken the general unknown-audio or unknown-Mod admission rules.

`NativeStart`/`NativeEnd` coordinates mean the original Runtime observed the
true native playing transition at that callback/poll. They are not synthesized
from clip duration and do not claim the first nonzero PCM sample is that same
coordinate. Full audible/muted PCM, sample positions, event/PTS identity and
final video still require the native end-to-end acceptance run. The bounded
native bridge supports at most 128 declared assets and 16,000,000 decoded float
values per clip; streaming/unreadable or changed clips are refused.

## Verification

```powershell
dotnet run --project tests/VideoExport.CharacterVoiceSession.Tests/VideoExport.CharacterVoiceSession.Tests.csproj -c Release
dotnet build tests/VideoExport.CharacterVoiceSession.Tests/VideoExport.CharacterVoiceSession.NativeCompile.csproj -c Release -p:AAInstallPath=F:/AzureArchive_101
./tests/VideoExport.CharacterVoiceSession.Tests/Verify-NativeMetadata.ps1 -AAInstallPath F:/AzureArchive_101
./tests/VideoExport.CharacterVoiceSession.Tests/Verify-CommitWorkerSurface.ps1 -AAInstallPath F:/AzureArchive_101
```

The first command exercises the production pure trace/binding modules. The second only
compiles the two new source files against real generated interop; it does not
build/stage the full Plugin or launch AA. The third checks original IL from the
installed DLL without initializing its Runtime. Logs live under
`artifacts/voice-session-20261030`. These results alone are not a native whole-story
or all-Mod compatibility certificate.

The pure tests also exercise the production two-pass publication state, real
temporary file pinning, worker validation, and concurrent fault/disposal blocked
by an acquired publication scope. The fourth command checks reachable local IL
from the two worker entry points for live native field reads, Unity calls and
main-thread validators. It is a scoped regression check, not an audit of all
external Harmony/BCL internals.

`tests/VideoExport.NativePath.Tests/CharacterVoiceManifestEvidence.cs` supplies a
native regression helper for the controlled mixed-story AAS. Root passes the real
frozen table, source path, independent recipe SHA-256 and ordered expected owned
occurrences into `Verify`. For the 2026-10-30 controlled recipe this means SHA
`4EF00F466296174A24125BBB20AFB7BCA9955182013C821F6D9E71488D142246`, seven actual
concrete rows and three occurrences of `aavt/voice/SE_BatSwing_01` producing one
distinct preload identifier. The helper calls the production extractor and records
every concrete row/voice result; it does not patch or fabricate native getters.
