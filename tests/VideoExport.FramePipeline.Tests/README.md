# Frame pipeline ownership tests

Run `dotnet run --project tests/VideoExport.FramePipeline.Tests -c Release`.

This harness links the production `FramePipeline.cs` and replaces Unity and the
encoder with controlled test boundaries. It exercises the actual ring, FIFO,
bounded writer queue and cancellation cleanup: GPU completion can be reordered,
readbacks can fail, and releasing a still-pending texture throws.

The six cases check frame order, pending-target ownership, final draining,
cancellation without committing pending pixels, invalid request rejection and
synchronous fallback. A cleanup regression also makes the GPU wait return with
the request still pending: its target must remain allocated, completed targets
are released, and a restart warning is reported. They do not validate a real graphics driver, image colour
or orientation, audio synchronisation, IL2CPP bindings, or actual throughput.
Those require the native AA export acceptance run.
