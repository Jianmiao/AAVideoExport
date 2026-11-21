# Native audio boundary regressions

Run `dotnet run --project tests/VideoExport.Audio.Tests -c Release`.
Use `--repro` for the original zero-sample failure and its next nonempty frame.

The harness compiles the production NativeCaptureScope against minimal Unity/AA
test boundaries, without starting AA. Ten audio checks cover Render(0), no fabricated
or stale PCM, stereo interleaving, changing buffer sizes, invalid lengths, native
failure and restoration. It cannot prove a specific Unity runtime's native
zero-length render succeeds. The Core suite separately encodes and decodes PCM
after zero-length writes to verify sample content and timeline preservation.

Seven render-boundary checks additionally cover playback-button visibility after
native AA reactivation, explicit show/hide, replaced or missing controls, cached
NGUI geometry refreshed before rendering and after restoration, and camera/UI
restoration on failures. Widget scans are cached per control instance. These test
the real scope with boundary doubles; actual NGUI draw-call output still requires
a short native export with the menu/Auto option both off and on.
