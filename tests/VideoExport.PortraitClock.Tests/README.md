# Scoped Portrait clock tests

This independent console harness links the production adapter, uses the selected
AA Harmony binary (or pinned HarmonyX 2.10.2), and patches five managed fixtures,
including two genuine compiler-generated async state machines. It verifies
all-target conflict preflight, exact realtime call counts, non-mutating IL
replacement, and pending async continuation across cancellation and a second
export. Unrelated realtime calls remain raw.

Pass the installed `FlandreScarletPortrait.dll` path as the only application
argument to additionally verify its version, MVID, SHA-256, full method
signatures and seven exact IL offsets, recursively including nested types.
No other author's source or installed file is modified.

The accepted binary is 5.2.0.0, MVID
`984e19fc-d376-4133-bb4b-dcf8e30eb031`, SHA-256
`6DB74B5B48F13E2E9C81D650172A3561A41BD526EE1A0BF2D29604CCD93A2EB8`.
The five target methods are FLRuntime.AdvancePostfix, FLRuntime.EnterNodeCore,
FLSlots.ApplyOne, FLCommands.<Animate>d__10.MoveNext and
FLCommands.<Run>d__0.MoveNext. Together they contain seven realtime getter calls.

These tests do not run AA, Unity native hooks, actual rendering, or VideoPlayer.
They establish the managed adapter behavior, not full export synchronization.
The offset is retained outside export so pending async locals remain valid;
hot unload is refused while that offset is nonzero. A normal AA restart resets
the entire process and its pending state together.
