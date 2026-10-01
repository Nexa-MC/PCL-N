# XSR-732 Ordered collection deltas

## Contract

Use one merge implementation for state-node publication and public delta application.
For ordered unique bases, sort changed/new entries only and merge them with retained
base order. Identity remains default key equality, independently of the order comparer.
Last upsert wins, removals win, and equal-order keys retain base/first-insertion position.
Unordered or duplicate-key public bases retain full normalization behavior. No stable-key
cache is introduced across snapshots, no old array is mutated, and a selector/comparer
failure must not advance the node revision or publish a change.
Repeated unchanged reads reuse their immutable snapshot/wrapper; delta publication
releases the node's previous cached view, and availability changes advance its identity.

Full coherent arrays still cost O(N) copying and validation. This closes repeated full
sorting, not every collection allocation, renderer traversal, or O(visible) UI acceptance.
The normative contract is [state-model.md](../state-model.md).

## Validation

800 deterministic randomized cases match the previous dictionary plus stable OrderBy
algorithm across duplicate/unsorted bases, repeated upserts/removals and equality/order
mismatch. Sparse changes in 10,000 ordered rows use fewer than 3N comparisons; 10,000
unchanged reads allocate zero bytes on the measured thread. Mutable keys, availability,
stale revisions, cancellation, old snapshot preservation and failure atomicity are covered.

Release builds have zero warnings/errors. Managed and Linux NativeAOT runtime suites pass
102 tests with one OS IPC test explicitly skipped because this sandbox rejects AF_UNIX
socket creation (EPERM); CI retains that test. Managed/Linux NativeAOT Services pass 469,
Desktop 105, UI.Next 88, Avalonia backend 9 and architecture checks 68. Changed-file
whitespace verification passes. This is not Windows/macOS NativeAOT evidence.

A real 60-second desktop-composition idle fixture passes with stable 285 entities and
210 state cells, but records one frame/render request. Its process metrics include harness
sampling/JIT and it creates no OS window; it does not certify native idle CPU, RAM or 8h
stability. The single wake remains unattributed and is not reported as zero-frame idle.
