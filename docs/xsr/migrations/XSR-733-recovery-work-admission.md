# XSR-733 Recovery work admission

## Contract

Extend the existing scheduler with conservative nonblocking maintenance admission.
No waiter, timer or worker thread is created; quiet, atomic resource masks and queued
work retain their authority. A null lease defers optional maintenance. The interface
default preserves older custom scheduler implementations by refusing opportunistic work.

Automatic success-baseline capture shares host Background CPU/disk budgets for actual
initial plan discovery and source processing. Admission for plan discovery ends before
workers acquire their own atomic masks. Metadata uses short leases and actual source
read/hash/compression yields between 80 KiB chunks, including when quiet begins mid-file.
Compression finalization, durable publication and failure cleanup retain their safe units.
Optional blob GC requests Idle admission in
32-entry scan/delete chunks
and stops when quiet/contention prevents admission; committed baseline identity is retained.
It does not pause explicit restore, authenticated byte verification, transaction recovery
or durable commit/rollback. No shared scheduler wait may park under a blob maintenance lock.
Final capture validation/manifest publication and history cleanup keep their bounded prior
behavior; this unit does not claim that every recovery IO path is scheduled.

The composition root supplies the host scheduler. Recovery.Storage adds only a reference
to existing Common.Contracts; no implementation, renderer or additional assembly boundary
is introduced. The normative contract is [work-scheduling.md](../work-scheduling.md).

## Validation

Five added contract cases cover nonblocking quiet/contention/cancellation, atomic masks,
queued-work priority and conservative legacy scheduler fallback. Actual file/Brotli tests
cover pause/cancel/resume, committed baseline/history preservation, journal-pinned objects,
32-entry cleanup deferral/retry and scheduler shutdown. A 256 KiB source enters quiet
after its first read: no second read occurs until resumed, foreground CPU/disk admission
remains available, cancellation removes the temporary object, and resumed bytes verify.
Automatic capture explicitly stays Background even when invoked from a Critical context;
a new root operation cancels its queued capture without leaking a reservation.

Release builds have zero warnings/errors. All 474 managed and Linux NativeAOT Services
tests pass, including previous failure/rollback/compensation cases. Desktop passes 105;
architecture checks pass for 68 projects and changed-file whitespace verification passes.
No assembly was added. These tests do not certify real Minecraft launch contention,
Windows/macOS native scheduling, or 8h process stability.
