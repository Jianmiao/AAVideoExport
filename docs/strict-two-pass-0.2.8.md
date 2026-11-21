# AAVE 0.2.8 strict preflight candidate

This candidate retains the strict preparation/replay timing contract from 0.2.7 and adds explicit progress reporting: headless preparation rate, actual render rate, real encoder writer rate, pending queue, and clear stage labels. The preparation pass intentionally reports zero encoded frames because it is a headless timeline preflight; only the replay pass submits video frames.

The preflight receipt, PCM and transcript can be persisted for future sandbox-certified cache reuse. Cache reuse is currently fail-closed and disabled for uncertified Mod sets; a live replay mismatch must trigger a fresh preflight rather than silently using stale timing.

See the source package's strict timeline documentation for the sample-accurate MP4/MOV contract and its native-AA acceptance limits.
