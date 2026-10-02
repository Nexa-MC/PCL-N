# Host-owned UI caption patches

UI Patch execution begins with a bounded caption ABI. The host grants named presentation
targets, assigns numeric indices once, and publishes immutable caption snapshots into one
sealed XSR State cell. UI.Next consumes the resulting normal UI components; it never reads
the Sidecar session, patch registry or transport. Desktop projects snapshots on its render
thread. There are no arbitrary entity keys, action changes, scripts, URLs or service bindings.

`UiPatch` registrations use target TLV 8, zero flags/codec and a binary TLV payload with
1 schema U32 (=1), 2 caption Str. Unknown fields are skipped; fields must be ordered and unique.
Payload limit is 2048 bytes; each host target supplies its caption character limit (1–256).
An empty caption is rejected. Registration validates the entire batch before READY.
Limits: 64 host targets, 128 patches per session, 256 active patches and 32 per target.
The most recently activated live patch wins. Removing it restores the previous live patch
or the original caption. Snapshots never expose mutable backing arrays.

Activation/retirement changes are serialized into State publications. Observer callbacks run
outside the patch business lock; reentrant updates are queued and coalesced to the latest
snapshot, so a delayed old publication cannot overwrite a newer snapshot. All terminal session
paths retire the lease. No active patch means no caption-read allocation or IPC.

The first product target is `ui.resources.search-label.v1`, limited to 16 characters. Its text
and accessibility label change together; original localization resumes on retirement. Account
and release trust messages are not granted. This is the caption adapter, not permission to
replace the entire product tree. New UI module rendering and further visual properties remain
separate adapters with their own schema and capability review.
