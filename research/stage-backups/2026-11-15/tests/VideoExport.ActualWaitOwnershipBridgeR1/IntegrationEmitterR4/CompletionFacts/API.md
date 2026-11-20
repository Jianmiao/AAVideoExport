# CompletionFacts R3 source contract

Assembly: `AAVE.WaitOwnership.CallerSource.R3`; namespace:
`Aave.WaitOwnership.CallerSource`. This successor does not replace sealed R2.
`Evidence/cold-final.json` is the development fixture's exact method plan, operand
ledger, local-type ledger and public injected-hook signature table. Plan ids and
saved module identities belong to this fixture, not a runtime Mod whitelist.

## Injected ABI

Own public nominal types: `CallerSourceHooks.SourceFrame`, `FactArguments`,
`CompletionCallToken`. Tokens have no public constructor. Nullable annotations
have no CLR signature effect. Existing CallerSourceHooks ABI is retained.

```text
CallerSourceHooks.EnterGeneral(int, object, FactArguments) -> SourceFrame
CallerSourceHooks.ReturnedValue(SourceFrame, object) -> void
CompletionFactsHooks.NewArguments(int) -> FactArguments
CompletionFactsHooks.SetArgument(FactArguments, int, object) -> void
CompletionFactsHooks.BoxBoolean(bool) -> object
CompletionFactsHooks.BoxInt32(int) -> object
CompletionFactsHooks.BoxInt64(long) -> object
CompletionFactsHooks.BoxSingle(float) -> object
CompletionFactsHooks.BoxDouble(double) -> object
CompletionFactsHooks.BeforeCall(SourceFrame,int,int,int,object,FactArguments) -> CompletionCallToken
CompletionFactsHooks.AfterCall(CompletionCallToken,object) -> void
CompletionFactsHooks.AbandonCall(CompletionCallToken) -> void
CompletionFactsHooks.FieldRead(SourceFrame,int,object,object) -> void
CompletionFactsHooks.FieldWritten(SourceFrame,int,object,object) -> void
```

Normal publication substitutes the internal generation-bound original entry and
installs the before-foreign whole-chain scope. Public raw source hooks are
observational. Ordinary publication never sets the normal-provider flag.
No source method gets `newarr` or `box`: helper builders and primitive boxers do
transport. Int32 enums retain their typed original locals and use `conv.i4` plus
BoxInt32. Other value-type returns are rejected.

## Facts and live checks

`ICompletionFactsSink` exposes `OnFact(CompletionFact)` and
`OnInvocationExit(CompletionInvocation,bool)`. CallerSourceKind.Observation=2
covers generic closed original signatures. Entry.Instance/Arguments are the
operands at original-body entry after prefixes; CompletionFact.Receiver/Arguments
are the callsite operands before dispatch. OriginalCallObservation holds original
entry, return and body facts. EffectiveValue is the caller-side value after the
whole chain. These values are never collapsed.

* HasQualifiedOriginalReturn: current exact caller + completed exact original
  call relation + active generation, usable in ordinary controls too.
* HasQualifiedNormalOriginalReturn: the above plus caller and callee normal
  publication. This is the production predicate during OnFact.
* IsUsableFor(release): same parent frame, reliable observation, current exact
  Release delivery/thread and active normal parent/Release generations.
* HasQualifiedOriginalReturnFor(release): also requires exact returned original
  and its active normal generation. Mixed ordinary/normal registrations cannot
  upgrade evidence.
* IsUsableWithin(root[,release]) and HasQualifiedOriginalReturnWithin(root[,release]):
  verify membership in the root's actual original-body fact tree and every
  intermediate returned call, active normal generation and exact link. Callee
  frames have exited, so their IsCurrentVerifiedOriginal is not required.

History loses live root/Release qualification. There is no stored acceptance bool.
Sink exceptions invalidate observation reliability and increment a failure count;
they do not replace the original business exception. The helper retains at most
256 facts per invocation and does not write logs or serialize runtime facts.
Consumers must release completed invocation references rather than accumulating
an export-long audit. Method return/field types are cached during template capture;
AfterCall/Field hooks do no per-event reflection metadata lookup.

## Opcode and source identities

The emitter validates input MVID, method token, fingerprint and exact site member.
It preserves actual business opcodes and spills/reloads their typed operands.
Incoming branch/switch targets and EH boundaries move to the observation spill
block. Existing catch/filter/finally entries revoke outstanding call tokens.
Return and finally paths keep original and effective results separate. AddedOperands
includes legacy CallerSourceRewriter additions as well as completion additions.

Constructor/List.Add facts carry actual constructed object, container and added
object. They do not prove native originals ran. Production must independently
establish current unique membership and lifetime. ParsePostfix's incoming
__runOriginal is preserved and is not native-parser original-execution evidence.
Missing PendingQuery means no query happened, not a false result.

The cold fixture observes Arm/Tick/getters/setters/Cancel original bodies in Core
and caller methods in the caller module, plus InteropObjectGuard.IsAlive(Object).
Tick caller sites 0x119 and 0x1EA stay distinct; only the latter is the decision
call preceding Release 0x218. Catch Release 0x28E stays distinct.

Gate return snapshots are exact direct reads before its original returns:

| Site | Field |
|---|---|
| 65536 | `<Sequence>k__BackingField` |
| 65537 | `<Status>k__BackingField` |
| 65538 | `<DueAt>k__BackingField` |
| 65539 | `_dispatchBy` |
| 65540 | `_sealed` |

Arm returning does not prove intercepted setters ran. Check raw postconditions
and/or linked setter original facts. A snapshot describes that original-return
point, not arbitrary later state after an Arm postfix or a later mutation. Gate
write version, capture/operation/actor ownership and coroutine epoch remain the
consumer's separate identities. Gate.Cancel does not prove native Cancel ran.

## Evidence boundary

The helper and emitter are implemented; ordinary generated methods and cold actual
metadata are tested. Actual normal publication of these new generic signatures,
multi-module profiler deployment, native lifetimes, native cancellation and AA
export/preview acceptance have not been executed here. Root owns those gates.

