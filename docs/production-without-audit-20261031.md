# Production export without runtime auditing

User requirement: ordinary users must not run auditing. This supersedes the
previous requirement to re-prove unchanged code and publish audit receipts on
every export. The existing timing/execution contract and RenderControlV1 v1
remain requirements; audit removal must not remove actual Mod effects.

## Build separation

- Release and Debug are production execution builds. They do not initialize
  runtime provenance observers, acquire audit scopes, inspect IL/metadata or
  patch graphs, inventory/classify every loaded Mod, produce audit sidecars,
  or load/update repeat-verification baselines.
- Only the explicit DevelopmentAudit configuration enables the audit path.
  Environment variables cannot turn a production build into an audit build.
- The ordinary installer/launcher does not install/activate an audit
  bootstrap. An existing AAVE-owned audit bootstrap is backed up and removed
  by exact ownership/hash checks, with rollback support.

## Execution retained

Fixed frame clock, actual offline audio generation, native story/selection,
voice and background timing adapters, finite queues, RenderControlV1
ownership, cancellation and cleanup continue to execute. Production keeps
ordinary frame/sample counts, ordering, resource-readiness, encoding/write
errors and output completion checks. No forged successful audit object or
receipt substitutes for an absent audit scope.

The existing first pass creates the PCM used by the movie. This increment
does not simply delete that pass. Its frame/sample plan is execution data;
IL/assembly/patch provenance, per-frame transcript/PCM comparisons and
cross-export receipts are audit data and belong to DevelopmentAudit only.

Specific method binding and timing-call replacement needed to implement an
adapter are execution work. Hashing or traversing an entire binary/call graph
to certify that adapter is not part of the normal user's execution path.

## Verification

1. Source-linked tests make audit entry points fail if a production export
   reaches them; production must still prepare, capture, finish and cancel.
2. DevelopmentAudit retains the old mutation/provenance tests in a separate
   output configuration. It is not the performance or user acceptance path.
3. Production native tests run without either runtime-audit environment flag,
   without an audit bootstrap, and without audit output files. The test
   harness performs its own full content comparisons outside normal use.
4. Compare the same 1080p60 Intel QSV first-pass window and run complete
   repeated short movies, including cancellation and actual Azurite restore.
5. Do not label unknown third-party behavior as certified merely because
   normal production no longer performs whole-program auditing.
