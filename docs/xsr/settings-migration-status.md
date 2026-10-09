# Settings migration status

This ledger records working consumers, rather than the number of stored legacy keys.
The read-only dev checkout supplies behavior requirements. The active catalog has ten
global navigation categories (eight currently populated in the entry map), nine instance
destinations and ten instance-settings groups.
Cloud and cloud sync are excluded. Version settings merge Java into Game. The current
catalog has 542 positions. The value schema declares 96 definitions, with 95 mutable
contracts and 79 exportable contracts. `java.compatibility` is retained only for
compatibility reads; writes reject because checks are mandatory. Its catalog position
now displays that mandatory policy instead of exposing an ineffective editor.
The raw catalog contains 398 Available, 12 PlatformUnsupported and 132 NotImplemented
positions. The latter comprise 78 structural Group/Choice positions and 54 functional
roadmap positions without delivered contracts. Platform conditions can further restrict
available consumers. These counts describe IA positions, not 542 implemented capabilities.

[XSR-832](XSR-832-retire-low-power-previews.md) retires launcher low-power mode at the
user's request. Its catalog, schema and runtime consumer are removed. Existing
`UiUltraLowPowerMode` and `appearance.low-power` fields remain inert without an
automatic user-data rewrite. User-selected animation fps and both reduced-motion
and legacy animation-disable preferences keep their working consumers.

XSR-794 adds working consumers to the two existing developer diagnostics positions:
read-only renderer snapshots and paged state metadata. The catalog remains 540 positions,
with 83 available entries and 457 raw `NotImplemented` entries at that slice. XSR-798
subsequently enables four desktop policies: at that slice the raw counts were 87 Available
and 453 `NotImplemented`. These include structural
and duplicated positions and independent roadmap contracts; the complete inventory is
[XSR-795](XSR-795-unimplemented-inventory.md). XSR-794 introduced no additional persistent value;
XSR-798 adds four. Those counts are historical snapshots. The source audit is
[XSR-799](XSR-799-remaining-work-audit.md); its implementation follow-up is
[XSR-820](XSR-820-completion-closure.md).

## Completion follow-up: XSR-800–822

The follow-up connects layered launch/profile settings, actual platform policies, bounded
network/Java diagnostics, manual Java acquisition, content/world/screenshot transactions,
operation history, storage backups and migration preview, diagnostic AI admission, finite
command/safe-mode workflows, startup/pressure instrumentation and PCL-N Sidecar Host
extensions. The domain contracts in XSR-800–819 and XSR-822 describe each consumer and its
limits; XSR-820 records integrated validation. The entry map and value contracts now reflect
these consumers rather than the original reserved labels.

Undefined foreign-launcher migration formats, World Guardian policies, clone/CoW providers,
independent lockfiles and future rendering/automatic repair contracts remain roadmap work.
External plugin engines, publisher signing, physical clients and long-soak acceptance remain
separate evidence. No unused editor or unrelated observation is counted as their implementation.

## Delivered launch-policy slice

XSR-761 connects exact custom memory in MiB, instance Auto memory, and automatic missing
Java acquisition. Global and instance settings use the existing durable policy store.
Java defaults to asking before downloading. Explicit incompatible Java is not replaced.
The page exposes only these working contracts, with explicit Auto actions and localized
instructions. Tests exercise the prepared launch request, approval and automatic download,
restart, overrides, invalid input, and the Desktop intent routes.

## Next consumer slices

XSR-762 also delivers live animation preference and window-size lock. Both apply from
committed state without opening Settings. The positive animation selector has correct
disable-flag polarity; native maximize/resize observes the lock.

XSR-763 delivers game-file batch concurrency (1–64, default 8) and one optional retry.
Installation and launch completion capture the policy before work; failed integrity
checks still reject publication. These controls explicitly cover game files, rather
than claiming to change resource/Java/loader budgets or global HTTP admission.

XSR-764 delivers default-server policy and scoped automatic game-file repair. Launch
preparation captures its effective settings once. Instance metadata and explicit Join
server intents retain priority over a global default; empty instance Custom disables join.
Turning off automatic repair does not disable preflight or Java validation. Its historical
catalog baseline had 535 positions, including the new default-server form field.

XSR-765 delivers a scoped Java distribution preference as a soft tie-break among
compatible installed runtimes. Explicit Java paths remain authoritative. Long setting
selectors now fit a flexible right-aligned control slot and retain horizontal scrolling.

XSR-766 delivers official-first, mirrors-first and official-only source policy for game
files. Installation and launch repair share it; metadata authority, digest requirements
and foreign regional original-source policy remain unchanged.

XSR-767 delivers region formatting independently of interface language and regional
network/authorization policy. System, follow-language and named cultures persist through
the same Service contract. New format preferences apply at the next Desktop session;
the form preserves existing custom cultures in its draggable selector.

XSR-768 delivers the live animation tick rate (1–240 fps, default 60), with explicit
legacy fps-minus-one conversion. The Host applies it before attachment and to active
motion without changing durations, reduced motion, native OS animation or idle work.

XSR-769 delivers user-selected JSON export and preview-confirmed import in Storage
and Migration, plus scoped transfers in instance Game settings. Sealed Service
routes own filtering, validation and revision-checked apply; Host owns bounded file
IO and atomic export. Navigation and instance changes retire pending results.

XSR-770 delivers revision-checked reset previews for working global settings and
current-instance overrides. Reserved settings and files are retained; instance reset
restores inheritance without changing other instances.

XSR-771 captures scoped process priority for the next launch and applies it through
Jvm.Host. A denied scheduling change cannot fail a started game. Legacy priority
encodings persist with their original meaning.

XSR-772 delivers the installed Java inventory, explicit cache-invalidating scan and
selection of a discovered runtime as the global Java preference. Discovery runs in
Services outside rendering and late results are retired on navigation.

XSR-773 replaces Boolean button pairs with confirmed-state draggable On/Off radio groups, exclusive choices
with accessible draggable radio groups, and export selections with check boxes. Version
game settings use compact inset launch/window, Java/memory, server and advanced forms;
management destinations retain Service-owned conditional availability. Overview, recovery,
server editor, export, content details and removed-content rows use consistent spacing and
operation placement. This is presentation parity, not implementation of the reserved
consumer slices below.
The installed Java radio chooser is also embedded in instance Java/memory settings,
using the existing inventory query and instance-scoped policy writes.

XSR-775 delivers durable custom Java registration, native executable selection, and runtime enable/disable. Removal unregisters external files without deleting them. Discovery and explicit launch selection honor disabled entries; successful writes invalidate discovery caches. Canceled pickers and probes, stale registry revisions, and persistence failures cannot publish registration changes.

XSR-776 previously connected low-power presentation to window activity and the existing
task, launch and sign-in state. That slice was retired on 2026-10-09 by
[XSR-832](XSR-832-retire-low-power-previews.md). Its migration record retains the former
behavior as history; no current setting or frame-rate consumer implements it.

XSR-778 connects durable update channel preferences and an optional startup discovery check. Discovery follows the current build by default; active update transactions retain their captured channel. Switching preferences retires old offers and responses.

XSR-779 makes installed-content enrichment and update checks one incremental refresh operation. Visible rows receive small independent batches, partial provider matches are retained within the request, and refresh bypasses online caches. All Settings refresh controls are accessible icon buttons at the right of their toolbar.

XSR-780 applies local log verbosity and bounded UI history immediately from committed settings, without visiting Settings. Disk logs are retained independently; this slice does not implement disk log deletion.

XSR-781 captures scoped game window titles and launcher visibility in each immutable
launch plan and process snapshot. Windows title writes are bounded and best-effort;
hidden/minimized launchers restore after exit or failure, and overlapping sessions
cannot close each other's launcher supervision.

XSR-782 captures the new-instance isolation policy in durable installation plans.
Managed addons and instance metadata are published together into the selected
shared or isolated directory; edits, packs and existing instances keep their own
layout. Killed-downloader recovery reuses the captured policy.

## Current-capability closure: XSR-783–789

[XSR-783](XSR-783-launch-hooks.md) captures scoped wrapper, pre-launch command and
wait preferences in each immutable launch plan. Wrapper arguments preserve the
private JVM bootstrap transport. Hooks run in the game working directory, waiting
hooks gate launch on a successful exit, and cancellation/startup failure stops owned
process groups/Jobs, including descendants of an already-exited shell. A nonwaiting
hook detaches only after successful game creation. Command text remains local-only
and is neither interpolated with credentials nor logged.

[XSR-784](XSR-784-network-preferences.md) connects direct/system/custom proxy, DoH,
IP-family preference and a captured shared transfer-body budget. Proxy fields commit
as one revision-checked batch; passwords use sensitive inputs and stay out of settings
exports. Request generations retain their transport for active responses. DoH retains
system-DNS fallback, the IP preference orders rather than disables address families,
and TLS, redirects, digests and regional source authority retain their existing rules.
The shared download consumer and independently streamed managed Java installer share
the application budget; safe loader/component/modpack paths capture existing source,
retry and concurrency policy where their planners support it. This does not claim
provider enable/disable, background-download policy, automatic network diagnostics,
OS traffic limits, external tools or update-helper traffic.

[XSR-785](XSR-785-managed-java-removal.md) delivers confirmed managed-runtime deletion
only for completed installer-owned components. Revalidated plan identity, tree hashes,
registration revision and runtime-use leases protect changed files, active sessions
and ongoing installs. The executor acquires the use lease before native extraction
and hooks, and binds it to the actual process identity after creation. Durable bound
records retain exclusion when the launcher lock closes while the game is alive;
malformed or unresolved records cannot prove exit. External runtime removal
unregisters without deleting files. Quarantine precedes the durable write;
pre-commit failure/cancellation restores the
component, and committed cleanup can retry. Mandatory Java compatibility remains.

[XSR-786](XSR-786-appearance-preferences.md) applies system/light/dark mode and a
bounded accent palette from committed settings while Settings is closed. UI.Next
projects colors into scenes without rewriting stored component colors, including
backend-owned text, focus, selection and control decorations. Native system
notifications wake a frame and explicit mode remains authoritative. Native requested
theme applies to dialogs/controls. Arbitrary imported themes, multimedia backgrounds
and unrelated OS integrations remain reserved.

[XSR-787](XSR-787-disk-log-preferences.md) connects committed 1–90 day retention to
the real file sink, which rotates active logs at 4 MiB and bounds owned archives to
32 files / 64 MiB. Cleanup preserves the active log, unrelated files and links. The
privacy page opens the configured directory and explicitly exports current plus
retained disk log facts to a user-selected ZIP. Export captures bounded read handles
at the writer barrier, preserving admitted records across rotation or pruning.
Bounded parsing retains validated local time/severity/built-in modules, excludes
free text and paths, and reports that
the existing disk format has no UTC date or typed operation facts. Export is cancellable,
stages atomically and refuses overwrite. The existing diagnostic bundle remains separate.

[XSR-788](XSR-788-storage-preferences.md) delivers preview-confirmed launcher data
location migration through a queued bootstrap transaction. The Host must be idle and
environment-pinned roots cannot move. The next startup hashes and stages the copy before
changing the locator; the source is preserved and stored Minecraft paths are not rebased.
Preview and startup refuse populated root-bound installation and recovery records
inside launcher data, including completed Java receipts. External game and Java
roots retain their original paths and ownership records.
Stale queued requests can be canceled explicitly. Separate conservative cleanup previews
cover only known old atomic-write temporaries or successfully finished visible task cards;
user files, failed/canceled tasks, install/recovery records and ordinary caches remain.

[XSR-789](XSR-789-settings-consumer-closure.md) integrates those six consumer slices
into the existing sealed contracts, forms, routes and composition. These implementations
do not mark the remaining future IA positions available. The integrated Linux x64
worktree passed the managed Services (583), Desktop (161), renderer (97), backend
(15), architecture (70 projects), formatting and renderer benchmark gates. Full
NativeAOT Services (583), NativeAOT Desktop shell/first-run probes and trimmed
Desktop shell/first-run probes also passed; see the
[integration evidence](XSR-789-settings-consumer-closure.md#integration-evidence). Native backend rendering
checks and display-free probes are reported separately; Windows/macOS manual GUI
and live online acceptance remain unverified.

| Slice | Remaining integration |
|---|---|
| Java | Optional compatibility policy needs a separate safe contract; current compatibility checks remain mandatory |
| Game | Arbitrary launch profiles/temporary overlays and future hooks need their own ownership and cancellation contracts |
| Download and network | Content-provider enable/disable, background-download policy, automatic diagnostics and additional source policies need consumers; transfer budgets do not bound all network traffic |
| Appearance/general | XSR-798 closes tray lifetime, single-instance activation and nexacl:// navigation/registration. Autostart, file associations, native notifications, arbitrary themes and multimedia remain open |
| Privacy/advanced | Persistent richer diagnostic histories and future diagnostic tools remain reserved; existing telemetry and update security boundaries remain authoritative |
| Storage | Reference-aware CAS collection, cloud synchronization, profile relocation, snapshot pruning and automatic deletion are independent capabilities |

Reserved IA positions are not a statement that dev already implements every capability.
Each next slice must first define its value and application timing, then connect a real
consumer, then enable its control. No consumer-less toggles or duplicate Desktop stores.
