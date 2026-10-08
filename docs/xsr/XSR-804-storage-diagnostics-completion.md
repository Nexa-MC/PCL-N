# XSR-804 — Storage, migration and durable diagnostics

2026-10-08 (Asia/Shanghai). Scope: PCL-N, `refactor/xsr`.

The Setup boundary owns a local immutable SHA-256 object store and explicitly named backup
manifests. It never hardlinks writable instance files to CAS: captures copy and hash bytes,
restores publish verified staging files, and garbage collection follows all committed manifests
under the same interprocess lease. A pending/stale preview cannot delete a newly referenced object.
Pruning selects whole manifests by age/count, previews retained references, and revalidates before
commit. All inputs, enumerations and read budgets are bounded; links are rejected.

Thin backups carry paths, content identities and lengths. Export writes only the manifest; the
import service and explicit offline-verification query report which objects are locally available,
instead of asserting offline readiness from a cached index. Imported manifests cannot escape the
restore directory. Restores use an empty destination and do not overwrite live data.

Legacy migration is a previewed conversion of bounded JSON settings and credential-free account
profiles. Recognized scalar settings are retained, tokens/passwords are excluded, online accounts
require reauthentication, and source files remain untouched. Apply accepts an unchanged preview
and an empty destination. Existing restart-based launcher root migration remains authoritative.

The Common logging boundary retains bounded structured launch/crash history and a redacted raw
workspace. Durable entries contain operation facts and curated classifications, not account tokens,
free-form process arguments or arbitrary paths. Raw text is explicitly selected for export, passes
credential/path redaction, has an independent byte budget, and never becomes automatic AI input.
History retention reads and deletes only its timestamp/GUID-named records; foreign JSON files in
the owned directory remain untouched and still count toward the bounded enumeration budget.
AI requests, if supplied by an explicit HTTP port, require an explicit data scope, provider/model,
request budget and cancellation; suggestions cannot execute arbitrary generated commands.

UI controllers emit typed intents and submit observed, cancellable router requests outside rendering. Services reference
neither Desktop nor Avalonia. Root composition injects storage/history dependencies and native
folder selection. Contract validation covers object reuse, reference-safe collection, preview retirement,
restore path admission, bounded imports and raw diagnostic redaction; external provider behavior and
physical power-loss/platform certification remain distinct acceptance work.

## Implemented routes and presentation

`ContentWorkspaceRuntime.Register` adds eight typed command routes and ten base query routes to the
foundation builders before sealing. Supplying the AI service adds an eleventh query in production.
The storage controller owns only a typed router adapter;
backup capture, manifest import/export, restore, retention edits, pruning, legacy migration and raw
export retain dispatch observation and cancellation. Native directory/file pickers and the local raw
snapshot capture are composition callbacks, separate from rendering.

The storage workspace shows physical/logical/reclaimable bytes and the latest 16 manifests, with
capture, offline verification, fresh-directory restore, thin import/export and stale-preview-safe
pruning. Retention defaults to disabled and is enabled only by explicit confirmation; the durable
owner policy defaults to 32 backups/90 days, consumes the committed `storage.backup-keep-count`
for its actual count, and always retains the newest backup. The optional
maintenance session wakes after 30 seconds, then every 15 minutes, and admits work only while idle.

The migration workspace previews recognized scalar legacy JSON settings, offline account identities
and the number of online accounts requiring reauthentication. It writes an independent, nonexistent
`NexaCL-import` destination; setting that directory as the launcher location remains a separate
restart-based operation. Arbitrary undocumented launcher formats are rejected rather than guessed.

AI uses an explicit OpenAI-compatible HTTPS endpoint and model, an ephemeral masked API key,
separate input-byte/output-token budgets, explicit structured-facts versus redacted-text scopes and
an immutable send preview. Results remain suggestions. The separate storage repair action reuses the
known prune preview/command; provider text cannot construct commands or delete arbitrary files.
Provider credentials, credits, provider/model availability and the quality of external suggestions
remain external facts, not claims made by these routes.

## Validation contracts

Seven new executable service scenarios cover content reuse, pinned-reference collection, retired
previews, tampered-object offline/restore rejection, imported path admission, cancellation/idle/links,
legacy credential exclusion, durable history privacy, bounded raw export, AI scope/budget/cancellation,
and typed route observation. [XSR-820](XSR-820-completion-closure.md) records the final build,
test and AOT/trim evidence.
The tests exercise controlled providers and filesystem fixtures; they do not certify physical
power-loss behavior, every undocumented launcher format or production AI-provider responses.

## Selected-instance offline readiness contract

A separate explicit query inspects the selected installed instance without downloading or logging in.
It reads a bounded inheritance/alias chain, resolves the same current-platform library/native rules,
checks client JAR and library artifacts, verifies the asset-index hash and all required asset-object
hashes, and asks an injected local Java selector for the existing scoped runtime preference plus the
shared compatibility range. Metadata/scan/hash budgets are explicit and cancellation retires the
query. Unknown integrity metadata is reported as present-but-unverified, never converted into a
verified zero or a ready result. Missing, corrupt, inaccessible or budget-retired data remain facts.
Each allowed current-platform native mapping must yield its required token even when the ordinary
library artifact is present; invalid native paths and resolver omissions cannot produce readiness.
Client selection gives the original instance's terminal-reference JAR priority over the base
version directory, using the same bounded path-selection helper as local protocol inspection.
A corrupt local override cannot be hidden by a valid base JAR. Selection is rechecked at the end
of the query; an override appearing or disappearing during capture prevents a ready result.
Readiness certifies those local prerequisites at capture time; it neither bypasses the launch gate nor
promises online-account refresh, multiplayer connectivity or future filesystem immutability.

## Explicit world snapshot history

The world detail workspace captures, lists and verifies immutable snapshots and restores only as a
new world under the current instance's admitted `saves` directory. The scope is a SHA-256 namespace
of the canonical instance/world identity, so equal world folder names in different instances cannot
share history and thin manifests never expose absolute source paths. Snapshot listing is bounded by
the CAS manifest budget. Captures are explicit; no automatic world capture policy is implied.

Capture and restore reuse `InstanceWorldService` admission: current world revision, no active game,
instance operation gate and an open session-lock lease throughout all effects. `session.lock` and
the launcher edit marker are excluded from capture. Verification and restore must prove that a
manifest belongs to the requested world scope. Restore validates world metadata in a temporary
world, publishes to a previously nonexistent safe name, and never overwrites the live source world.
Queries and mutations use typed observed routes, cancellation and retired UI targets. Path-based
scope changes after a world/instance relocation; no heuristic basename matching merges histories.

`WorldSnapshotRuntime.Register` receives the existing CAS and native lease ports and registers two
commands plus two queries. The world detail controller owns only typed routers, shows up to 16
recent snapshots, confirms the generated fresh destination, and cancels retired world targets.
An independent route scenario covers equal basenames across instances, pinned scope verification,
session-control exclusion, restore collision/traversal rejection, stale revision, active game,
tampered object, cleanup and cancellation. Native macOS/power-loss certification remains external.
