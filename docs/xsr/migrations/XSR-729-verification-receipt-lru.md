# XSR-729 Bounded verification receipts

## Contract

Successful Minecraft file receipts remain service-lifetime optimization only, with
the platform path comparer and unchanged length/mtime/expected hash conditions.
The default capacity stays 8192. Hits refresh recency; new successful paths at capacity
evict just the least recently used entry. Updating an existing path never flushes the
table or evicts an unrelated recent file. Dictionary/list transitions share one lock;
hash IO stays outside that lock. No timer, persistence, new assembly or public API.

Stale, missing/size-invalid and explicitly reverified receipts are removed before
verification. A failed explicit hash cannot leave the prior successful receipt reusable.
Explicit forceHash, cancellation and the existing content-addressed asset fast path
retain their meaning. Receipts do not establish same-user isolation or replace signed
release admission or explicit integrity verification.

## Verification

The real-file capacity regression seeds A/B/C, refreshes A, then adds D. A/C/D remain
usable while exclusively locked; evicted B must rehash and rejects same-size/stamp
corruption. Repeated forced verification of D retains C. Failed forced verification
of A revokes its old receipt. Existing stamp/hash/cancellation/content-addressed tests
also remain enabled. Managed Services passes all 464 tests; Release has zero warnings
or errors, changed-file whitespace and the 68-project architecture check pass.
Linux NativeAOT Services also passes all 464 tests. These tests establish bounded
retention semantics, not a measured real Minecraft startup or eight-hour memory SLA.
