# XSR-823 Shared query state and installed-version snapshots

## Locked contract

Application composition owns one `ISharedStateCache`. Services receive that instance; pages
consume service state and never create private copies of query caches. Keys include an explicit
domain scope, request identity and revision (provider policy or disk identity). The cache has
bounded entry, estimated byte and pending-work budgets, uses LRU retention, and starts no timer.
Freshness and maximum retention are separate. Reading a retained stale value requires an explicit
opt-in and cannot turn it into a current observation.

Concurrent work for the same typed key shares one factory. Cancelling a caller cancels its wait,
not another caller's work. The owner may cancel all factories on disposal. Explicit refresh joins
an existing factory; invalidation prevents an older factory from repopulating the cache. Factory
errors and cancellation are never cached. Auth sessions, access tokens, process observations,
dynamic hardware measurements, integrity receipts and write-command results are excluded. Cached
values are immutable query snapshots; a cache hit is never evidence that a command succeeded.

`Store` seeds query state and preserves an active producer. An imported original timestamp cannot
replace a newer retained observation after asynchronous disk I/O. Explicit `Invalidate` and
`InvalidateScope` revoke entries and detach the older producer from future publication; seeding
an offline hint never breaks concurrent query coalescing.

Installed-version persistence is a separate, domain-owned schema with source-generated JSON
metadata, bounded documents and atomic replacement. It stores only the installed catalog and an
allowlisted display projection, not launch commands, auth settings or credentials. A normalized
root identity qualifies every entry. Persisted paths stay within that root, unsafe references and
linked paths are rejected. Corrupt, unsupported or over-budget snapshots are ignored and do not
overwrite the underlying game files.

Snapshot publication and its 32-root/32 MiB directory budget share a bounded cross-process
directory lease, including writes for different roots and different store instances. The lease
uses a persistent exclusive lock file with cancellation-aware retries for at most five seconds;
failure to acquire it only drops the optional cache write. Pruning reserves the incoming payload
before atomic publication and touches only this owner's exact 64-uppercase-hexadecimal `.json`
filenames. Other JSON files, temporary files and lock files are neither charged nor deleted.

A stored snapshot is immediately usable as last-known display state, including when a root is
offline. It is explicitly pending reconciliation and cannot authorize launch or overwrite the
remembered selection. Reconciliation captures the current file set and file identities, verifies
the stored fingerprint, and reuses unchanged catalog parsing. Changed, added or removed version
manifests and JARs invalidate the catalog revision. Instance metadata is read fresh, and its file
identity is included in the before/after reconciliation check without forcing manifest parsing.
Display icons are excluded from persistent snapshots; memory reuse requires their current path,
length, modification and creation identity, checks links, and charges every query's icon budget. An
explicit refresh can bypass reuse. A capture that changes while discovery runs is not persisted
as a verified snapshot. Metadata is read from the authoritative atomic store for each reconciled
result; persisted display fields never become launch configuration.

Injected shared discovery distinguishes an unavailable root from an existing empty installation.
Missing or unreadable roots, and unreadable existing `versions` directories, fail reconciliation
and preserve the last-known display hint. Snapshot hydration itself still works while the game
root is offline. The probe only confirms that the root and an existing `versions` directory can
be enumerated; it performs no recursive scan. An existing accessible root with no `versions`
directory is an authoritative empty catalog. A root disappearing after Library's precheck cannot
turn a failed identity observation into a successful empty scan. The original standalone discovery
overload retains its existing missing-root behavior and creates no implicit cache owner.

Launch preparation explicitly calls the snapshot source's fresh `RefreshAsync` path and never
uses a persisted catalog to select its manifest, Java fallback or launch arguments.
Disk-catalog reuse for display also checks the primary manifest path against current discovery;
a recomputed cache checksum is not permission to redirect manifest selection.
Fresh parsing uses a separate flight identity from ordinary hint restoration, so an overlapping
launch cannot join a producer that restores disk hints. Common's normal publication guard retains
the live catalog within the same global budgets; ordinary discovery prefers that entry after
checking current file identity. Successful refresh revokes the older hint producer's publication
slot without seeding values outside the producer's invalidation guard.

## Integration and verification

Composition passes the shared cache and the application cache directory into
`MinecraftInstanceDiscovery`. Library state may hydrate the last-known display projection before
awaiting `DiscoverAsync`; it publishes a reconciliation marker and retains the original selection
until live discovery succeeds. Its existing generation check prevents an obsolete root from
publishing over a newer directory choice. Provider caches reuse the same owner with independent
scopes and policies; their persistence contracts are documented in XSR-824.

`FoundationHost` owns and disposes the shared cache and accepts an optional cache directory
at composition. Desktop supplies its application data cache path. Library publications add
`IsProvisional`; `SelectedInstance` is unavailable until reconciliation succeeds. Provisional
entries remain visible after an offline failure, but selection and destructive commands reject
them. Loading preserves same-root display entries rather than clearing the version list.

`FoundationComposer.Compose` and `ComposeWithJavaRuntimeRoot` retain their original public
CLR parameter signatures and default values. They forward with no persistent cache directory;
the separately named `ComposeWithCacheDirectory` and
`ComposeWithJavaRuntimeRootAndCacheDirectory` entry points admit the new cache-directory
argument. Appending an optional parameter to an existing method would preserve source call
syntax but break already compiled callers, so the new capability does not replace old members.

The installation catalog uses this same application owner for its immutable provider result.
Keys qualify game, loader and provider-policy revision. Interactive publications and cancellation
remain owned by `InstallCatalogService`; one cancelled waiter cannot cancel another query's
shared provider work. Explicit refresh bypasses a fresh result and policy changes cannot reuse
the old request. Standalone service fixtures own and dispose their fallback cache.

Contract tests cover shared concurrent work and cancellation isolation, invalidation during a
flight, LRU/byte retention, persistence reuse after restart, same-stamp content changes and
malformed snapshot rejection. Managed and NativeAOT validation belongs to the final integration
gate; no reflection codec, UI dependency or legacy implementation is introduced.

## Capability and content consumers

Instance capability adapters no longer retain full metadata, auth settings or mutable manifest
objects in a process-wide static dictionary. They resolve authoritative instance metadata and
options for each query while sharing only the discovery catalog through the injected application
cache. Official installed-loader compatibility queries share the normalized installer catalog
scope and regional provider revision. Custom providers are isolated and failures are not retained.

Archive display metadata uses the same owner when supplied by application composition. The key
qualifies normalized file identity and page format; the producer gets its own bounded archive read
budget and caller cancellation only affects its waiter. Returned read cost is charged to each
caller's aggregate admission budget. Worlds, processes, auth, writes and verification receipts
remain live and are excluded from that display cache.

The pre-existing standalone archive helper overload remains a fresh query with no implicit
global owner. Tests of reuse explicitly inject and dispose the same application-cache fixture;
their file-lock, aggregate-budget and changed-stamp assertions remain unchanged.

Preflight instance-resolution tests likewise inject the shared owner. A damaged manifest must
become unavailable on both ordinary reads and explicit refresh; the removed static full-instance
cache may no longer keep the old manifest usable for ten seconds. Restoring a valid changed
manifest must immediately expose its new main class through both paths.

Resource icon services share admitted static PNG/WebP/JPEG display results through an optional
injected application cache. Keys include the exact allowlisted CDN URL and the image admission revision. Concurrent reads
share HTTP, encoded-byte limiting and decode admission; cancelling one reader only cancels that
reader's wait. Failed/invalid images are not retained. Application entries reserve the bounded
1 MiB encoded maximum per icon, with explicit one-day freshness and retention; decoded pixels are
not retained in this cache. Disposing a consumer does not dispose or invalidate the application
owner, and a shared producer can finish for another live consumer.

Standalone icon services own the same cache implementation with their existing entry and encoded
byte limits. They charge the accepted image's actual encoded size before completing its private
flight and disable automatic fixed-size retention. This direct store is restricted to the private
owner, which exposes no invalidation API; application flights retain the shared cache's guarded
publication/invalidation behavior. Standalone disposal cancels queued/admitted work and rejects
late results while an already returned image remains valid after eviction.
