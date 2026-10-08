# XSR-814 — Scoped diagnostic history and explicit workspace preferences

2026-10-08 (Asia/Shanghai). The final consumer audit closes three finite preferences and the
instance history boundary without guessing unknown launcher formats or physical storage support.

`diagnostics.ai.enabled` is a global Boolean, default false. Its committed value admits the explicit
AI preview/send UI; navigation, disablement and retired settings previews cancel requests. The
provider/model/API key remain explicit per-request inputs and API keys remain ephemeral.
`diagnostics.ai.reasoning` is a global provider/low/medium/high enum, default provider. Non-default
values become an explicitly previewed OpenAI-compatible `reasoning_effort` request parameter;
provider acceptance and inference quality remain external facts. It never increases token budgets.
Workspace refresh completion defers a section rebuild while a current choice/workspace intent is
queued. Each workspace rebuild restores the live editor bindings and selected values, so a committed
AI admission toggle remains usable before and after asynchronous refresh/preview transitions.

`storage.backup-keep-count` is a global integer, default 32, range 1..1024. Composition injects an
authoritative committed settings reader into the existing CAS owner. Manual prune previews and the
opt-in idle maintenance read that count; the owner file retains enablement/age and remains compatible
with old counts when no settings port is supplied. Invalid/unavailable policy rejects pruning rather
than choosing a silent default. Current policy previews are revalidated before mutation.

Durable instance records contain only a SHA-256 canonical instance scope, nonempty session ID,
bounded operation/stage/outcome, optional numeric exit/launch-duration facts, fixed failure code and
actual start/end timestamps. No absolute instance path, process arguments, raw stdout or account
credential enters the history file. Schema 2 preserves schema-1 global facts. Exact instance queries
filter by the canonical hash; unscoped old records are never assigned to an instance by heuristics.
The new `BeginInstanceOperation` entry point records typed metadata from the first breadcrumb onward.
The producer owns actual lifecycle facts; the UI cannot invent a terminal outcome or measurement.

The recovery change timeline uses multiple persisted snapshot timestamps/revisions/file counts and
the captured baseline/current comparison as separate facts. It does not imply a historical diff for
snapshots that have not been compared. Existing verified restore commands retain their fingerprint,
running-instance gate and preserved-original transaction semantics.

Validation extends the existing scope/budget/provider and CAS scenarios and adds bounded history
scope isolation/reopen coverage. Root performs the integrated build, architecture and AOT checks.

The global storage workspace also displays deduplication savings as
`max(0, LogicalBytes - ReferencedBytes)` for its immutable backup content only. This is not an
allocated-block measurement and excludes manifests/filesystem overhead. Optimization advice is
limited to the observed presence of unreferenced owned objects; the user previews and confirms the
existing reference-aware prune plan. Thin-backup metadata is the existing bounded manifest identity,
label, creation timestamp, relative paths, lengths and hashes, never an absolute source location.

Ordinary Minecraft-folder migration reuses the existing folder-import workflow: register a selected
game root or copy selected recognized version directories after manifest/path validation. It does
not infer another launcher's account/settings schema, copy unrelated foreign files, or relocate
unowned game roots. Launcher-data move checks remain restricted to their explicitly owned tree;
nonempty journals and recovery/loader receipts bound to their original paths reject that move.

The integrated Desktop regression
`DiagnosticAiWorkspaceRebuildsCommittedAdmissionAndCancelsRetiredSend` uses the real sealed
Foundation/workspace routes and a cancellable HTTP test port. It covers committed enablement and
reasoning selection, preview without HTTP, explicit send, cancellation after disablement, and an
empty ephemeral API-key draft after re-enablement. The shared section builder restores editor
bindings after rebuilds; the workspace updater defers refresh for queued choice/workspace intents
until the intent loop consumes the live control source.
Its locale coverage also checks translated scope/headers with literal model names and multi-line
user text, the actual renderer flag, and cancellation of raw export without creating a file. Root
executes the consolidated final suite. This validates the local UI/request lifecycle, not acceptance
by an external model provider.

AI workers return typed confirmation/suggestion data. The UI dispatcher translates the fixed
confirmation format, scope labels and suggestion prefix before appending the unchanged preview or
suggestion text. The feedback dialog's explicit literal-message port prevents recursive localization
of user model names, endpoints and multi-line diagnostic/AI bodies. Suggestions remain visible in
the literal workspace and a read-only message dialog; their raw body is not sent as a notification.
Raw-diagnostic export confirmation uses the same typed UI boundary: the translated fixed header is
formatted with observed bytes and a literal destination, then the unchanged redacted preview is
appended. It never relies on a length-dependent whole-message translation fallback.

Confirmation and result dialogs explicitly mark preformatted diagnostic/AI bodies as literal.
Their fixed headings and scope labels are localized on the UI dispatcher before combining the
captured original preview/reply. The Desktop dialog port retains its default translation behavior
for ordinary fixed messages and carries a separate message flag for these literal bodies; screen
reader dialog labels also preserve their contents. Translation must not rewrite user data merely
because a line matches a catalog key.
