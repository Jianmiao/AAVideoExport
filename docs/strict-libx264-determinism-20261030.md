# Strict libx264 encoding profile

`strict-libx264-single-thread-v1` applies only when `StrictSynchronization` is
enabled and the selected encoder is `libx264`. Production `EncoderArguments.Video`
emits `-threads:v 1` and `-x264-params lookahead-threads=1`. CBR combines the latter
with its existing `nal-hrd=cbr:force-cfr=1` parameters in one option.

The `superfast` speed preset, pixel conversion, target bitrate, maximum rate,
and VBV buffer remain fixed. At 4000 Kbps, VBR remains target 4000k,
maximum 6000k, buffer 8000k; CBR remains minimum/target/maximum 4000k and
buffer 8000k. Non-strict libx264 and every other software/hardware encoder retain
their existing thread policy. The preset changes speed/quality, not the
single-thread deterministic contract.

## Evidence and boundary

Run50 movies `native-story-59680-02.mov` and `native-story-59680-03.mov` first
diverged at zero-based frame 173, then differed in every frame through 452.
The initial differences were overwhelmingly one code-value in Y, U, or V and
spread across detailed character/UI regions without visible geometry changes.
Both movies used frame threads 11, lookahead threads 2, ABR 4000k and VBV 6000k/8000k.

Three offline encodes of one identical decoded input with that profile differed
in 413 and 415 frames versus the first encode. Reducing lookahead alone to one
still differed in 417 and 411 frames. Keeping both thread counts but removing
VBV produced exact repeats, as did keeping VBV with one frame/lookahead thread.
The production fix retains VBV rather than relaxing the user's rate contract.

Diagnostic files and the original RED/GREEN logs are under
`artifacts/pixel-drift-20261030/run50_02_vs_03_223839/`.

This isolates an encoder nondeterminism mechanism. It does not establish that
independent AA captures are identical. Real AA repeated export, Mod-effect
preservation, and Azurite binding/FPS/VSync restoration still require their own
acceptance. Decoded comparison tolerances and baseline identities are unchanged.

## Regression commands

Run the parameter contract regression:

```powershell
dotnet run --project tests/VideoExport.Core.Tests -c Release -- --strict-software-arguments-only
```

Run the opt-in three-encode media regression using a 30 fps movie fixture:

```powershell
dotnet run --project tests/VideoExport.Core.Tests -c Release -- <ffmpeg-tools-directory> --strict-software-repeat --repeat-input=<movie-path>
```

The media regression freezes the decoded RGBA fixture once and passes the real
production argument builder's output to FFmpeg for all three encodes. It retains
the complete commands, actual FFmpeg logs, source/frozen-input hashes, movie
hashes, and exact decoded frame hashes under the reported test artifact folder.
Its `production-parameter-receipt.json` describes only the encoder boundary and
does not create a new repeat-acceptance baseline key. The large temporary raw
fixture is removed after its hash is recorded; the source movie is preserved.
