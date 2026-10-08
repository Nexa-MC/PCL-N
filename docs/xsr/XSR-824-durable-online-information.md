# XSR-824 Durable resource online information

Resource metadata is reusable across screens and launcher starts. Resources consumes the bounded Common shared state cache; it does not keep independent dictionaries for catalog detail, identification, fingerprints, or translations.

## Persistence and identity

The Resources domain owns a generated JSON metadata codec and atomic cache files. No credential, API key, authorization header, account record, token, or URL containing user info, a query, or a fragment is written. Project descriptions, project and version identities, file hashes, exact installed-file associations and validated translations are metadata. Download links without secret-bearing components may be retained; unsafe links are omitted from the persisted representation. Download commands always revalidate version/file metadata online through a forced request and never authorize a download from a stale or omitted cached link.

Keys include a domain scope, schema/parser revision, provider source policy, and exact query identity. File fingerprints include normalized absolute file identity, length and last-write ticks. Online associations include SHA-512 and source policy; presentation restores the current local name and enabled state, rather than reusing those of another equal-byte file. Translations include provider/project and the original description digest, so a changed description cannot reuse a mismatched translation. Changed schemas, provider policies, file identities or source descriptions invalidate reuse.

Every persisted or shared resource snapshot copies its collections into read-only collections at ingress, including project sources, search projects, versions, game and loader lists, and dependencies. Provider-owned arrays and deserialized lists cannot be mutated through a returned snapshot to contaminate later readers. Live metadata retains its validated links; link sanitization applies to the persisted representation.

Disk reads, directory creation, atomic writes, lock acquisition and pruning reject a link or junction anywhere in the existing ancestor chain. The Resources directory owns only uppercase 64-hex SHA-256 filenames ending in `.json`; pruning never counts or deletes other JSON files. A bounded exclusive file lease serializes publication and budget cleanup across cache instances and processes. Cache rejection leaves provider requests and memory reuse available.

Only verified positive results are persisted. Empty search/identity/translation results, exceptions, malformed provider rows and incomplete provider results must not replace a previously verified record. Refresh failure leaves the previous record available until its retention limit. File reads and provider matches still validate identity after asynchronous work. Matching a SHA-512 or CurseForge Murmur2 plus published SHA-1 must be verified against the actual local fingerprint; display names never establish online identity.

## Lifetime and resource budgets

The shared cache handles cancellation-isolated single-flight requests, bounded state and revision invalidation. A resource read checks its caller's cancellation again at completion, including immediate cache hits and synchronously completed shared requests; cancelling one waiter never cancels the shared producer. Resources explicitly performs stale-while-revalidate: a retained older record is returned immediately with a visible `缓存资料` notice, and one bounded background refresh runs for the same key. An explicit refresh joins an existing refresh. Retention expiry is a hard miss; there is no permanent offline truth or persisted negative-result cache.

Catalog search and detail are fresh for 10 minutes and retained for 7 days. Exact file/provider associations are fresh for 24 hours and retained for 30 days. Fingerprints and matching translations are fresh for 7 days and retained for 30 days. The domain disk budget is 256 records, 32 MiB total, and 1 MiB per record. Metadata arrays remain bounded by provider response and existing list/version budgets. Background refreshes have a 25-second deadline; local reads never wait for network before displaying retained metadata.

## Provider coverage and UI contract

Both Modrinth and CurseForge identification are attempted independently. A malformed or unavailable provider cannot discard a verified healthy match. Both `.jar.disabled` and `.disabled.jar` names are considered disabled mods. Resources and shaders use the same exact identity pipeline as mods, and full detail includes exact installed versions even when the compatible version list omits them. A source policy change alters cache identity and refreshes visible catalog content.

The installed-content list and detail display retained metadata notices; a stale record never claims authoritative update availability. NativeAOT persistence uses source-generated `JsonTypeInfo` for each owned metadata envelope and no reflection serializer.

Validation covers restart reuse, same-key concurrency and waiter cancellation, exact file/source/original-description separation, corrupted or mismatched records, positive-result preservation after failed refresh, offline stale fallback, and provider exception isolation.

Installed-content fixtures that change a provider response explicitly request `Refresh` for each changed phase, because identical bytes reuse the same verified association across resource pages. The unknown-file phase uses a separate owned cache with no prior positive association. These fixtures continue to assert that a Murmur fingerprint without the matching published hash proves nothing, and that a changed expected file identity is rejected before provider access; retained historical metadata is never evidence of a new match.

## Install catalog supplement

Installation catalog presentation also survives launcher restart. `InstallCatalogInformationCache`
owned by Minecraft.Install persists a generated, typed envelope of known `InstallCatalogVersion`
fields beneath the host cache directory. It imports into the same Common cache key used by
live catalog requests: domain `minecraft.install-catalog`, exact game/loader identity, and
provider/regional source policy. Unknown loader names, unsafe game/version/download names,
wrong identities or schema versions, future timestamps, malformed arrays, oversized records,
and linked paths are misses. Authentication records, provider keys, request headers and URLs
with user info, queries or fragments are not persisted. Empty or partial-provider catalogs do
not overwrite a previously verified positive record.

Catalogs are fresh for 10 minutes and retained for 7 days, with at most 32 records, 16 MiB
total and 1 MiB per record. Disk access happens on the queued service worker, uses a bounded
file read, write-through atomic publication, and a process-shared disk lease. Cache failures
never turn an otherwise successful provider request into an installation failure.

A fresh record publishes without HTTP after restart. Retained older data publishes immediately
with additive `InstallCatalogSnapshot.IsStale` and a visible cache notice, while one tracked
background request renews it. Renewal success removes stale status; failure retains versions
and states that reconnection failed. Explicit refresh and policy changes still use the existing
request-generation, cancellation and work admission rules. Display cache metadata is not an
installer recovery receipt: installation keeps its task-local authority and verifies downloads.

Changing the selected game revokes each old publication slot before cancelling its waiter.
Shared-query cancellation can complete synchronously, so cancellation callbacks must not find
an older request still current or repopulate the new game's loader list. The publication guard
also requires a loader request's game to equal the active game. The existing delayed-provider
stale-result regression retains its original newer-game and newer-loader assertions.

Validation adds restart/no-network reuse, stale immediate publication and failed/successful
renewal, policy separation, malformed/linked/oversized cache misses, and disk-budget coverage.
