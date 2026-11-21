# Standalone native 0.2.4 / unified benchmark (TEST ONLY)

This plugin has **no assembly reference to AAVideoExport or its Core**. It uses the loaded provider's actual `ExportHost.Prepare(ExportOptions)`, `Probe`, `ResolveStory`, `Cancel`, and `ReportCompleted` entrypoints. Native catalog `Select(false)` chooses the exact existing story; the normal exporter owns loading and playback. No substitute player, frame clock, admission check or output publisher is installed.

Build: `dotnet build tests/VideoExport.LegacyBenchmark.Tests -c Release -p:AAInstallPath=F:/AzureArchive_101`

Contract checks: `dotnet run --project tests/VideoExport.LegacyBenchmark.Contract.Tests -c Release`

The root guarded launcher owns child AA lifetime, staging, global settings/PlayerPrefs restoration, same Mod profile, and source/fixture workspace selection. **Do not copy this into a running AA installation. Do not ship it.** Never replace original installed 0.2.4 binaries.

In a clean timing child, temporarily stage only `AAVideoExport.LegacyBenchmark.Tests.dll` as the test plugin, removing the separate NativePath acceptance harness and RuntimeBootstrap/audit patcher from that child's staging. Set:

- `AA_VIDEOEXPORT_LEGACY_BENCHMARK=1`
- `AA_VIDEOEXPORT_BENCHMARK_MODE=whole` for one naturally complete short fixture, or `window` for a user-story benchmark cancelled after actual observed frame 360 (warmup ends at observed frame 60).
- `AA_VIDEOEXPORT_TEST_STORY` to the absolute exact native `.aas` path.
- `AA_VIDEOEXPORT_TEST_OPTIONS_JSON` to one absolute existing options file. Explicit required fields: Width=1920, Height=1080, Fps, Container, Codec, EncodingMode, Encoder (not `auto`), BitrateKbps, RateControl, AudioQuality, AudioSampleRate, CaptureMode, SuperResolutionEnabled. Both runs must use the same file/settings. Candidate requires StrictSynchronization=true; legacy 0.2.4 has no such property, and the receipt explicitly records it as absent rather than pretending to enable it. Other unknown options fail.
- `AA_VIDEOEXPORT_TEST_OUTPUT` to a fresh absolute evidence directory.
- `AA_VIDEOEXPORT_TEST_TIMEOUT_SECONDS` (30–7200; default 1200) and `AA_VIDEOEXPORT_TEST_QUIT=1`.
- Optional `AA_VIDEOEXPORT_BENCHMARK_CONTEXT_JSON` to the root runner's exact host/Mod-profile/config/fixture manifest; it is hashed in the receipt.

No special command-line argument is required. All original runtime audit/attribution/trace activation variables must be absent. A provider with a modern build profile must be non-audit and single-pass. Original 0.2.4 is recognized by its actual pre-build-profile API, not labeled as a modern development-audit provider.

`legacy-benchmark-start.json` records hashes before timing (provider/Core/harness, host executables, native graphics runtime, actual FFmpeg tools, loaded Mod assemblies, native config/settings files, exact story and input options). `legacy-benchmark.json` records exact effective options, original result, counters, focus changes, actual installed Azurite binding and main-thread v1 acquire/release/restoration evidence, and failures.

The measured interval requires both `Application.isFocused == true` and the
Win32 foreground window to belong to the current AA process. Whole mode keeps
the full elapsed contract: after exact native story selection, it activates its
own AA window once and waits up to five seconds for focus before normal `Prepare`
and its timer. Window mode submits the same original `Prepare` without treating
excluded preparation/scene loading as measured work. It requests own-window
activation once at the first actual `Capturing` observation below frame 60,
using the current scene's actual HWND. Both focus signals must be confirmed at
frame 60; failure or a missed boundary rejects the run without shifting the
interval or pausing production. Window focus enforcement spans only actual
frames 60 through 360. Pre-capture and warmup focus changes are recorded separately
in `SetupFocusChanges`, with the activation phase and observed capture frame.
Each activation uses one `ShowWindow(SW_RESTORE)` / `SetForegroundWindow` request.
The runner must launch this explicitly interactive benchmark with a normal
window. No other window is activated, no input is injected, and activation is
never retried. Whole-mode acquisition failure starts no timed export; window-mode
acquisition failure follows normal cancellation and cannot produce a comparable
window. Any focus loss during the measured interval, including native focus events between update
polls or loss at the completion callback, invalidates the benchmark and uses the
normal cancellation/restoration path. Receipts retain pre-measurement and measured
focus transitions, the single activation result, and the foreground contract.
Transition-only diagnostics record the foreground HWND, PID, process name,
window class and own-process identity, with at most 64 transitions plus one
failure snapshot. Window titles and command lines are never read. These reads
do not activate any window or retry the one permitted own-window request.
Focus changes after the measured completion boundary remain recorded as restoration
context. Mixed foreground/background receipts must not be compared as throughput.

Whole-export time starts immediately before normal `Prepare` and ends in the original `ReportCompleted` callback. It includes preparation, native story load/playback, encoding and finalization. Probe, hashing and external result inspection are outside that timer; their configuration and tools are recorded. The exporter's own phase durations remain separately labeled, since old internal `_elapsed` can exclude preparation. Full benchmark output must be externally checked for resolution, fps, frames, duration, PCM/PTS and authored-content equivalence before claiming a speedup.

Window mode observes real capture, accepted and encoder-written counter deltas and monotonically timed boundaries. Legacy observes actual `ExportSession.AudioSampleFrames`; unified capture additionally observes its actual `RawAudioPass.RawSampleFrames`, whose raw interval includes original primer/tail and is reported separately from committed session audio. A zero-video first preparation pass is rejected. The normal Cancel path must finish, leave no movie or temporary media, and restore actual Azurite mapping and Unity FPS/VSync/capture settings. This mode is explicitly **not** whole-export performance or content-equivalence evidence.

The harness reports a failure if the loaded old exporter cannot use the present AA host, current fixture, encoder, catalog or Azurite. Such a refusal is a compatibility limitation, never a benchmark win. Compilation and contract tests alone are not native execution acceptance.
