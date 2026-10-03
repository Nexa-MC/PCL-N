# Settings migration status

This ledger records working consumers, rather than the number of stored legacy keys.
The read-only dev checkout supplies behavior requirements. The active catalog has ten
global categories, nine instance destinations and ten reserved instance setting groups.
Cloud and cloud sync are excluded. Version settings merge Java into Game.

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
Turning off automatic repair does not disable preflight or Java validation. The current
catalog has 535 positions, including the new default-server form field.

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

XSR-776 connects low-power presentation to window activity and the existing task, launch and sign-in state. Idle background windows request at most 10 fps; foreground activity restores the stored animation rate. The renderer remains demand-driven and no transfer, native window animation or input clock is suspended.

XSR-778 connects durable update channel preferences and an optional startup discovery check. Discovery follows the current build by default; active update transactions retain their captured channel. Switching preferences retires old offers and responses.

XSR-779 makes installed-content enrichment and update checks one incremental refresh operation. Visible rows receive small independent batches, partial provider matches are retained within the request, and refresh bypasses online caches. All Settings refresh controls are accessible icon buttons at the right of their toolbar.

XSR-780 applies local log verbosity and bounded UI history immediately from committed settings, without visiting Settings. Disk logs are retained independently; this slice does not implement disk log deletion.

XSR-781 captures scoped game window titles and launcher visibility in each immutable
launch plan and process snapshot. Windows title writes are bounded and best-effort;
hidden/minimized launchers restore after exit or failure, and overlapping sessions
cannot close each other's launcher supervision.

| Slice | Remaining integration |
|---|---|
| Java | Managed-runtime deletion; compatibility checks remain mandatory until a separate safe policy exists |
| Game | Isolation and wrapper/hooks; each needs scope-correct consumers and failure/cancellation semantics |
| Download and network | Bandwidth, proxy and DNS; other download kinds need their own budget and source policies |
| Appearance/general | Theme and supported system integration; platform actions belong to Host |
| Privacy/advanced | Disk log retention/export; existing telemetry policy and update actions retain their security boundaries |
| Storage | Storage-location migration preview and cleanup; use existing service commands and transaction contracts |

Reserved IA positions are not a statement that dev already implements every capability.
Each next slice must first define its value and application timing, then connect a real
consumer, then enable its control. No consumer-less toggles or duplicate Desktop stores.
