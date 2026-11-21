# Decoded media repeat-identity acceptance

```powershell
dotnet run --project tests/VideoExport.DecodedMedia.Tests -c Release -- "F:/AzureArchive_101/mods/AAVideoExport/0.2.4/tools"
```

This project source-links the pure-managed native-test helper
`VideoExport.NativePath.Tests/DecodedMediaFingerprint.cs`. It uses actual
FFmpeg to encode/decode tiny independently authored pixel and PCM fixtures.
It does not launch AA or modify a production Mod.

The suite checks independently known per-frame and PCM16 hashes, rejects one
changed RGB value in one frame and one changed PCM16 value at identical frame
and sample counts, accepts lossless remuxing despite changed container bytes,
and exercises H.264/YUV420P with PCM16 MOV. It also rejects wrong record/sample
counts and nonzero source PTS and refuses cancelled success evidence.

Each run writes a new `bin/Release/net6.0/artifacts/decoded-media-<guid>/`
directory containing fixture media, original raw inputs, framehash text,
per-frame TSV, PCM summaries, fingerprint summaries and `results.json`.
Root red/green/final logs are retained in
`artifacts/decoded-media-acceptance-20261030/`.

Native story acceptance now combines this content proof with its existing
ffprobe frame/PCM timestamps and exact counts. These media-helper tests alone
do not establish a successful real AA story repeat.
