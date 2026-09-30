# Resources page

The shell Resources destination replaces the migration placeholder. Legacy Community
is a read-only behavior reference: category browsing, text/game/loader filters,
sorting, pagination, and a separate project/version detail view.

## Dual-provider downloads and Chinese discovery

Search now combines Modrinth and CurseForge through bounded Service adapters. MCIM
is the domestic API route, with official fallback; only official CurseForge requests
receive the optional API key. The UI offers mirror-first and official-first ordering.
Provider failures produce partial-result notices rather than erasing usable results.
Cross-provider deduplication requires an explicit MC百科 slug mapping; names alone
are not identity. File alternatives are equivalent only with matching hash and size.

The legacy mcmod.buf is migrated as data only (no implementation dependency), into
a JSON name/slug index. The schema records wiki id, Chinese name, CurseForge slug,
Modrinth slug. Chinese queries resolve to provider slugs, then provider metadata is
filtered normally. MCIM summary translations are separate, cancellable queries,
bounded and checked against the original summary. Missing translations retain the
original text. Index attribution: MC百科 via the legacy launcher data asset (22,155 rows).
Source mcmod.buf SHA256: e09ac3d7cefb32142605189e6896614c59dd64b5eaba7931284fbc709fff28b0.
A shared wiki ID alone is insufficient to merge forks; the provider slug pair must match.

Downloads use a sealed command containing provider/project/version identifiers and
a user-selected destination folder. Services re-fetch authoritative file metadata,
reuse DownloadService and TaskCenterService, enforce actual byte count and hashes,
and publish by non-overwriting rename only after verification. Metadata URLs and
filenames from UI are not download authority. A denied/missing third-party CF URL
is not synthesized. This action saves the archive; the existing local import flow
continues to own dependency installation and modpack extraction.

`ResourceCatalogContract` defines sealed search and detail queries. Services owns
HTTP, provider facets, metadata validation and response budgets. Desktop renders
immutable results and emits intents; it never performs HTTP or calls a concrete
catalog. Requests are cancellable; superseded results cannot replace current UI.
The search field survives result publication, preserving selection and focus.
Resource icons use a separate cancellable sealed query, so image latency never
delays search results. Only HTTPS cdn.modrinth.com/data/ and Forge CDN /avatars/ assets are accepted, with
redirects disabled, four concurrent reads, a 1 MiB actual-byte limit and a bounded
32-entry cache. The existing encoded raster carrier gains an explicit resource-icon
factory for static PNG/WebP/JPEG (1024px maximum); existing PNG-only factories retain
their contract. Decoding stays in the backend and invalid images keep a placeholder.
WebP header reference: https://developers.google.com/speed/webp/docs/riff_container.
JPEG frame-header reference: https://github.com/libjpeg-turbo/libjpeg-turbo/blob/main/src/jdmarker.c.
The current-instance filter reuses `MinecraftInstallEditContract.Query` through
the existing install catalog router; Desktop does not parse version JSON or infer
Minecraft/loader versions from filenames. A changed selection invalidates its result.

Both providers cover mods, modpacks, resource packs, shaders and data packs.
Favorites and automatic dependency installation use the follow-up contracts below.
Project links and files without third-party download permission open the provider
website through the existing host HTTPS action. Installation remains owned by existing install/content Services, rather
than an independent installer inside this page.

Each provider search returns at most 20 entries; Chinese aliases expand to at most
four search terms per provider, then exact cross-provider mappings are merged. Detail version pages contain at most 20
rows. Metadata responses are limited to 8 MiB of actual bytes. JSON parsing uses
explicit fields, without reflection serialization, for NativeAOT. Only canonical
provider links assembled from validated identifiers leave the Service.

Reference: https://docs.modrinth.com/api/operations/searchprojects/ and
https://docs.modrinth.com/api/operations/getprojectversions/.

## Verification

Service tests cover provider facets, page offsets, invalid project identifiers,
cross-project version rejection, exact game/loader filtering, cancellation, and
actual response bytes exceeding a deliberately false Content-Length. Data packs
use `all_project_types:datapack`; the old mod-only category filter misses projects.

Desktop acceptance tests cover shell navigation, padded card bounds at multiple
window sizes, persistent search input, version links, current-instance query reuse
(including vanilla without a loader), superseded requests, and cancellation on exit.
The catalog holds only one search page; version rows are paged rather than building
thousands of UI entities at once. Offline results expose a retry action.

## Resource management follow-up

Favorites are a local Service-owned JSON collection, addressed by provider identity;
the default category remains Mods. UI-only filters may hide library/API projects and
projects identified in the selected instance by verified provider file hashes. Unknown
local files remain visible. Scan and network work stays off the render thread.

Mod download commands target a root-qualified instance. Services derive Minecraft and
loader compatibility from the existing install-edit service, resolve required dependency
edges (including pinned versions), reject version conflicts/budget overflow and group cyclic edges, and verify all
archives before importing dependencies first and the requested mod last through the
existing local-JAR import service. Optional edges are presented as unchecked installation choices. Selected optional
branches are re-planned so their required and optional children become reachable; embedded
edges are already carried in the archive. Existing
conflicting or disabled mods are not overwritten/enabled silently. If an import fails,
already added dependencies remain and the failure reports this; the main mod is last.
Non-mod resources still use the chosen-folder archive download. Downloaded JAR metadata is re-read, including nested archives, and its required mod
IDs/version constraints are validated against the staged and enabled installed inventory
before importing. Platform Java/loader constraints remain owned by launch preflight.
Unknown or unparseable actual dependency tables are reported rather than claimed complete.
Fabric `provides` aliases retain their owner's version; Quilt provided IDs retain an
explicit version or inherit the owner's version. These bounded, validated identity facts
participate in both actual dependency verification and shared-dependency removal checks.
Malformed or unsupported provided declarations make the dependency inventory incomplete.
Reference: https://docs.fabricmc.net/develop/loader/fabric-mod-json and
https://github.com/QuiltMC/rfcs/blob/main/specification/0002-quilt.mod.json.md.

No UI metadata grants
filesystem or download authority.

Name lookup uses a prebuilt character index and provider-pair dictionaries. Merging is
linear in the result count, and successful search/detail snapshots use short bounded
caches; cancellation never populates a cache with incomplete results.

Mod removal previews derive file-to-mod identity from SHA256 inventory facts. Only direct
and transitive dependencies of the removed mod are candidates. A fixed-point pass excludes
files still referenced by remaining mods, including cycles. Incomplete inventory suppresses
orphan suggestions. Chosen removals reuse the content-trash service with expected file
size/mtime checks; files stay recoverable, and the batch is revalidated before mutation.
The requested mod is removed before its orphan dependencies, so a later failure cannot
leave it installed after removing its prerequisites. Any completed removals remain in
recoverable trash and are reported by the refreshed content view.

## Installed content online information

Mods, resource packs and shaders use a sealed root-qualified content query with page,
filename and expected size/mtime. Services derive the isolated/shared content directory,
reject links and changed files, then identify the exact archive by Modrinth SHA512 or
CurseForge fingerprint plus SHA1. Names are never used to guess an installed identity.
Hashing and both provider requests run in the background; unknown/offline files retain
their local details. Chinese project metadata, matched version, compatible versions,
download count and canonical source links are projected into a separate detail section.
UI cancels superseded reads and checks the current instance/page/file before publication.
No network request runs during rendering, and opening a project uses the existing HTTPS host action.

Regression coverage includes dual-provider partial failures, private-key routing, Chinese
identity mapping, favorites persistence, corrupt/short/long downloads, optional choices,
cyclic dependency order, actual missing/wrong-version JAR dependencies, online archive
hash identification (including CF fingerprint collisions), stale detail cancellation,
and shared/orphan removal with recoverable trash. Complete Service/Desktop suites,
architecture gates and NativeAOT shell validation remain required for this migration.
