# XSR-759 instance server list

Read-only inspection of dev establishes servers.dat in the effective game directory,
including isolation. XSR implements its own bounded NBT codec, retaining unknown root and
per-server tags during add/edit/remove/reorder. No legacy types, fNbt assembly or UI controls
are imported. Input and decompressed NBT cap at 8 MiB, 512 servers and nesting depth 32.

Snapshots carry a SHA256 revision of the original bytes. Save requires that same revision,
uses the instance recovery gate, rejects active users of a shared game directory, and
publishes a flushed temporary file with an atomic replacement and servers.dat_old backup.
Edits operate on source indices from the admitted snapshot, preserving cached icons and
resource-pack preference tags. Malformed data is reported, never replaced with an empty list.
Desktop reads/writes sealed query/commands asynchronously, with add, edit, remove and order
controls. Network status polling is a separate opt-in operation, not a render side effect.

Joining uses an additive ServerAddress on MinecraftStartCommand and a per-attempt coordinator
override. It does not persist ServerToEnter or change later ordinary launches. Existing planner
version-specific --server/Quick Play behavior remains authoritative. Status probes read an
admitted server revision, use bounded Minecraft status packets and a three-second timeout.
