# XSR-755 protected differential updates

## Contract

The XSR-754 helper may reconstruct a fresh immutable payload slot from a publisher-signed
differential bundle. The full eighteen-asset release envelope remains schema v1, preserving
compatibility with existing helpers. An optional `Nexa-Delta.json` and detached signature
bind each bundle to the exact target full-package digest, target version, RID, build variant,
configuration, and installed source version. No URL or local path is accepted from the caller.
The bundle is selected only when smaller than the authenticated full archive.

`nexa-file-delta-v1` bundles contain exactly `manifest.json` and `data`. The manifest lists
the complete target tree, executable bits, lengths and SHA-256 digests. Ordered chunks copy
bytes from the same relative file in the admitted installed slot or from the literal stream.
Content-defined chunking makes unchanged ranges reusable even after insertions. Deleted files
are absent from the fresh slot; the old installation and unknown files are never mutated.
All source and destination traversal retains admitted directory handles/descriptors.

The generator uses the existing `UpdateChunker` gear table and frozen V2 bounds/masks
(128 KiB minimum, 512 KiB average, 1 MiB maximum). Whole unchanged files bypass chunking;
the runtime applies signed byte ranges and needs neither native diff executables nor IPC.

The signed bundle hash and exact download length are checked before parsing. Actual manifest,
literal, per-file and aggregate output budgets are enforced while streaming. Paths, duplicate
names, special archive entries, chunk ranges, identity, final hashes and required executables
are validated before durable preparation. A failed optional delta cannot authorize activation;
the helper discards its fresh slot and attempts the separately authenticated full archive.
Cancellation does not trigger fallback. Neither signature failure nor source mismatch changes
the high-water journal. Recovery re-verifies cached release/index signatures and bundle bytes,
then reconstructs from the unchanged installed slot, including offline after durable reception.

## Publishing

The release workflow retrieves recent public same-channel releases from the fixed repository,
verifies their envelopes with the pinned publisher key and checks downloaded archives against
signed sizes/hashes. Generation processes uncompressed file content, not compressed TAR bytes.
Every published bundle is round-trip checked against the complete target archive tree and
omitted if it is not smaller. Index and bundles join distribution checksums, detached signing
and independent signature verification. Missing historical releases keep full-only publishing;
malformed or unauthenticated historical evidence fails generation rather than being trusted.
CI commit labels remain manual-only. No plugin, UI or user staging path gains update authority.

## Validation

Contract tests cover reduced transfers, initial installer and active-slot sources, wrong
signatures, source mismatch, truncated/corrupt bundles, malformed paths/ranges, actual byte
budgets, missing executables, full fallback, cancellation, offline recovery and rollback.
Python tests cover chunk reuse after shifts, unchanged/deleted/new files, archive traversal,
signed identity and reproducible complete-tree reconstruction. Native CI exercises differential
transactions through real admitted filesystem ports on all six RIDs; AOT/trim checks remain
mandatory. Physical power-loss and same-account replacement-race acceptance remains with Dots.
