# Media timeline verification regression

Run the production verifier and process wrapper without Unity:

```powershell
dotnet run --project tests/VideoExport.MediaVerification.Tests -c Release
dotnet run --project tests/VideoExport.MediaVerification.Tests -c Release -- --media <ffmpeg-and-ffprobe-directory>
```

The executable also acts as a deterministic ffprobe fixture. It reproduces the
old 262,144-character JSON-tail truncation with 200,000 packets, then verifies
the same complete timeline through the streaming path. Missing, duplicate,
negative, extra, malformed and off-grid timestamps must fail, including a
one-millisecond Matroska deviation that previously passed the loose tolerance.

Packet PTS are not necessarily monotonic in decode order when B-frames are in
use. The verifier maps each integer PTS to its exact rational display-grid
position and requires every position exactly once, with one bit per frame.
This validates a monotonic presentation timeline without decoding the movie
or retaining its JSON listing. The media option checks real ffprobe output
from B-frame MP4, MOV and MKV files, including 20,000 video frames at 60 fps.

These tests validate encoded video timeline integrity; they do not prove that
Unity scheduled audio events at the correct corresponding story times.

Strict synchronization tests also verify actual PCM16/PCM24 and AAC files at
44.1/48 kHz. The strict path requires audio start at sample zero, exact track
duration in a 1/sample-rate time base, and a contiguous packet timeline after
explicit codec skip/discard metadata. Both one-sample shortage and one-sample
excess fail, while the same files remain accepted by the legacy tolerance.
AAC decoder padding is not mistaken for intended audio: packet durations and
the container track edit must both retain the exact planned sample interval.

The suite reproduces MP4's default 1,000 Hz movie time base truncating an AAC
edit duration from 26,000 samples to 25,968 samples. Strict muxing must set the
movie time base to the least common multiple of video fps and output sample
rate. A container lacking sample-accurate track timing is rejected rather than
being represented as a proven strict result.
