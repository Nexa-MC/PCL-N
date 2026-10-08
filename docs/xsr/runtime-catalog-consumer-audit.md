# Runtime catalog consumer audit

This audit evaluates `SettingsCatalog.Load()` after schema/consumer mapping, rather than the
raw JSON availability column. Group and Choice rows are excluded. The first full scan found
247 effective NotImplemented operation/preference/state entries; the later integration scan
found 119 while other owners were still updating mappings. Those are audit snapshots, not
acceptance counts. The root completion ledger remains authoritative for the final catalog.

The runtime domain distinguishes editable preferences, captured read-only facts, mandatory
launch policies and unavailable native providers. Mapping a real policy/fact does not create
a switch for disabling it. The following stable IDs have concrete consumers or a precise
conditional boundary.

| Stable ID | Original meaning | Concrete consumer and scope |
| --- | --- | --- |
| `global.appearance.d999201ab2ad` | Renderer info | Developer RuntimeDiagnostics captures the real semantic renderer scene, tree, layout and pending-state counters. |
| `global.appearance.6357798f5b55` | Layout / paint | Actual last layout visits, native scene commits and drawing callback counts; submission counters do not claim physical frames. |
| `global.appearance.d44cc0e413d3` | Animation scheduler | Actual shared motion tracks/timer/target and bounded measured scheduler tick rate. |
| `global.appearance.d1957652c919` | Performance | Real raster charge/admission/leases/decode/disposal and OS pressure snapshot; unsupported physical display FPS remains unavailable. |
| `global.java.118291439447` | Automatic compatible Java | Mandatory coordinator/Java selection policy and JavaPolicy read-only explanation; automatic selection is distinct from an explicit preferred executable. |
| `global.java.4defce197f9a` | Compatibility check | Mandatory validation; attempts to disable the old policy are rejected. This is a State, not an editable Boolean. |
| `global.java.dc5ff6fcd886` | Compatibility | Actual instance Java requirements/compatibility facts on Platform, plus JavaPolicy explanation of explicit incompatible-choice rejection. |
| `global.java.0f6d5bc77e9b` | Set preferred | Existing ChooseJava intent commits `java.runtime` for the current settings layer. |
| `global.java.7342ba416866` | Verify Java | Selected-inventory properties/version probe and captured executable fingerprint, with identity replacement, timeout and malformed output rejection. |
| `global.java.38e1f8df3aa0` | Verification | Same bounded selected-runtime diagnostic workflow; it does not claim whole-runtime-tree integrity verification. |
| `global.java.d503e51b6737` | Runtime capabilities | Actual VM/vendor/architecture/properties and module query output. The supported scope does not include invented JFR/GC/native-VM capability discovery. |
| `global.java.5a06021a0456` | Managed / external | Owned managed manifest inventory and guarded managed deletion; explicit runtime-source badge uses exact owned executable identity. |
| `global.java.27cb7147cd93` | Download Java | Separate manual acquisition owner implements the bounded installer workflow; launch-required acquisition alone was not a manual UI consumer. |
| `global.game.42cb17c26e70` | Memory strategy | Existing layered `game.memory` automatic/custom preference, real estimator recommendation and captured heap plan; policy facts are not additional editable strings. |
| `global.game.603d5db0bd30` | Launch preflight | Actual immutable-plan gate, blocking/confirmation behavior and typed Platform preflight/repair UI. |
| `global.game.dc34f9e868e6` | Resource budget check | Real RAM/commit/GPU estimate checks in that same gate. Missing measurements remain unknown. |
| `global.game.c7fd5c2fa7a3` | Compatibility check | Mandatory selected Java, platform/library metadata and declared content compatibility checks; no bypass switch. |
| `global.game.b670d35098d3` | Automatic repair plan | Installed-file completion runs before launch; other remediation plans require the existing explicit confirmation workflow. The title must preserve that narrower behavior. |
| `global.game.56d6a5a2ff73` | GPU preference | `game.gpu-preference`, next-launch captured and actually applied through XSR-816; conditional Linux Mesa secondary PCI selection. |
| `global.game.b4337614dfd8` | Renderer | `game.renderer`, actual Mesa software environment request with bounded local dependency evidence; other platforms report unsupported for explicit selections. |
| `global.game.2fc72ad5a99f` | Resource handoff | Hidden/minimized native window disables raster presentation and retires idle image resources while preserving active leases and the established optional-motion policy. Fixed behavior, not a second preference. |

| Instance resources stable ID | Original meaning | Actual captured source |
| --- | --- | --- |
| `instance-settings.resources.2e46ef3f04c1` | Recommended Xmx | Platform `estimate.heap.recommended`, with model confidence and source. |
| `instance-settings.resources.9927df5e4ef0` | RAM estimate | Platform `estimate.physical.launch` / `estimate.physical.runtime`. |
| `instance-settings.resources.dd69318b8bc1` | Startup peak | Explicit estimated launch resource components, distinct from measured JVM launch-window samples. |
| `instance-settings.resources.67976deb5cf9` | Commit estimate | Platform `estimate.commit.launch` / `estimate.commit.runtime`. |
| `instance-settings.resources.1dab25552608` | Historical calibration | Actual admitted resource history sample count, similarity, weight and calibrated estimate rows. Empty history is not fabricated data. |
| `instance-settings.resources.5be5eac17a86` | VRAM estimate | `estimate.graphics.*` model components; actual GPU residency is a separate optional native observation. |
| `instance-settings.resources.278b55f814ea` | iGPU shared memory | Captured UMA facts and `estimate.graphics.shared_system`; absence is unknown, not inferred from working set. |
| `instance-settings.resources.8c486af00d94` | GPU choice | Layered captured GPU preference and actual launch environment handoff, same supported boundary as the global preference. |
| `instance-settings.resources.7f105865bd84` | Renderer | Layered captured renderer preference and actual next-process environment handoff. |
| `instance-settings.resources.140aa6a41093` | Shader advice | HardwareAdvice shader model budget, explicit missing input and bounded current resource risk. |
| `instance-settings.resources.437184290d06` | Resource-pack advice | HardwareAdvice captured enabled-pack count and model load peak; no package names are copied into advice. |
| `instance-settings.resources.03f3222cf10f` | Render-distance advice | Captured options fact and resource-risk guidance; no options.txt mutation or guessed optimal distance. |
| `instance-settings.resources.b9b0340358d5` | Risk | Typed preflight evaluation of the same captured instance snapshot; bounded stable issue codes and unknown-state explanation. |
| `instance-settings.resources.cadfbbd79205` | Process priority | Actual layered preference and post-start JVM Host SetPriority result; unsupported/failed control is reported. |
| `instance-settings.resources.3a4bed17a856` | Resource handoff | Same native visibility-dependent fixed behavior as the global state. |

| Privacy stable ID | Original meaning | Concrete consumer and boundary |
| --- | --- | --- |
| `global.privacy.09cef506324c` | Crash report | Actual local JVM/Minecraft report-file evidence and scoped durable crash history; no report upload is implied. |
| `global.privacy.6b6d7c5093d2` | Diagnostic data | Curated typed diagnostic facts, retained history and explicit scoped bundle/export workflow. |
| `global.privacy.cde451802b77` | Safe copy | Process log redaction and explicit redacted diagnostic preview/copy; original credentials/paths are not copied. |
| `global.privacy.d3893574d03c` | Bundle privacy | Curated bundle excludes raw account/path/payload data; raw preview requires the established explicit scope workflow. |
| `global.privacy.4a4af3012aa4` | Crash analyzer | Typed fault-analysis route and process failure/repair presentation, with bounded evidence classification. |
| `global.privacy.d695f66ed522` | Launch diagnostics | Actual preflight plus immediately scoped durable GameLaunch and independent terminal GameExit facts. |
| `global.privacy.f48f3318e658` | Resource exhaustion | Actual OOM fault classification/repair and RAM/commit preflight; no causal conclusion from missing measurements. |
| `global.privacy.3350ed9a9f35` | Recent-change diagnosis | Typed baseline/current diff and explicit crash recovery change dialog. |
| `global.privacy.b28439ff7cc3` | Windows event correlation | Fixed bounded System/Application windows and instance/session-joined observation UI; denied collectors remain unavailable. |
| `global.privacy.04ecbba47ef4` | GPU / TDR | Actual Display provider 4101 metadata, tagged time-window unless a matching process ID is present. |
| `global.privacy.9958a9f23f56` | WHEA | Admitted WHEA provider error/warning event IDs with actual timestamp/relation metadata, without raw EventData or causal claims. |
| `global.privacy.7ac2f9bfa1ea` | Native crash correlation | JVM PID-named crash evidence and bounded Application error/report records, preserving temporal versus PID relation. |
| `global.privacy.c2d61e1dc584` | Realtime log | Existing bounded game stdout/stderr viewer. This does not imply a separate full launcher raw-log console. |
| `global.privacy.6c8db73e33b8` | Operation trace | Actual root dispatch observer plus typed bounded 256-entry query and 32-row manual-capture viewer. |
| `global.privacy.1e51861a3aee` | State trace | Actual root state publication metadata history, without state values. A current StateInspector snapshot alone was insufficient. |
| `global.privacy.25c2ac9fa4ed` | Launch trace | Actual minecraft/process dispatch subset of the same captured bounded session trace. |

The production root creates dispatch, state and lifecycle observation paths. Event and scheduler
observer composition APIs are implemented, but the root currently creates no corresponding
general event router or XSR scheduler publisher. No synthetic publisher is added to make a
counter appear. The native motion scheduler remains its own measured runtime diagnostic source.

Launch-page audit: all four default destinations have actual production controllers attached.
The old custom unknown-destination branch could enter a placeholder; `OpenBoundDestination`
now rejects an unknown or unbound page and preserves the current destination. Early
construction fallback pages do not establish production feature
availability. No fallback label is accepted as evidence of a completed consumer.

## Read-only dev contract comparison

The instance update/security names in `settings-ia-source.md:1040–1080` are IA positions,
not execution contracts; that document explicitly says `IA ≠ 当前实现状态`. In `origin/dev`,
`PageInstanceResourceRight.axaml.cs:790–847` captures Minecraft/loader hints to query
candidate versions; `:923–970` decides whether an identified file has a newer candidate.
`ApplyCatalogUpdateAsync` (`:1272–1312`) downloads and replaces the selected file directly,
and `UpdateSelectedAsync` (`:1845–1865`) loops over that operation. Neither path constructs
an update-impact preview. `CommunityResourceDependencyResolver.ResolveRequiredDownloadsAsync`
is called from the new-download orchestrator (`CommunityDownloadOrchestrator.cs:159`), not
the installed-file update path. Exact-name and Chinese-behavior searches across dev's
Desktop/Application/Portable source find no Source Lock, Unknown Content Policy or
pre-update risk-assessment setting/provider. These are concrete remaining planning contracts,
not omitted dev implementations.

| Stable IDs | Dev evidence and current boundary |
| --- | --- |
| `instance-settings.security.4a298edce3d5`, `.23f5aba95cca`, `.26f1b3f51bf2` | Dev scans when the resource workspace loads, but exposes no independent automatic-check schedule/update/source-lock policy. Current explicit update queries and fixed source/version ownership admission do not create those preferences. |
| `instance-settings.security.2493090ad0dc`, `.a0f9cc2b646e`, `.71af5e87c864`, `.0f3047cf5381` | No dev pre-confirmation dependency/addon/loader/risk preview contract. Current transaction revalidation and installed declared-content graph are narrower facts, not an editable impact policy. |
| `instance-settings.security.b585c2e079b2` | No dev unknown-content policy or defined ignore/block/trust semantics. Unknown content must not acquire an invented policy through catalog mapping. |
| `instance-settings.security.ef79c7f9b1a5`, `.a5339dbd7bc7` | Current XSR owned-file preimage journal and failed-update compensation are actual consumers. They can be labelled fixed affected-file snapshot/rollback states; they do not assert a whole-instance automatic CAS snapshot. |
| `global.advanced.2981dd4c9f21` | A real dev migration consumer: `DesktopRenderBootstrap.Configure` consumes `SystemDisableHardwareAcceleration` and selects Win32/X11 Software rendering at startup. XSR now reproduces that restart policy through `appearance.hardware-acceleration-disabled` and the same typed configuration in early StartupSession/standalone ShellHost. The root captures the bounded legacy Boolean before initializing native services. macOS has no software-force provider and reports the preference unsupported. |
| `global.advanced.f67f27634920` | Dev switches between its classic and experimental launcher layouts. The XSR branch has one production semantic layout; an old-layout toggle cannot be inferred from that current page. |
| `global.advanced.d9987876215e` | Dev explicitly disables its unimplemented ECS backend toggle. XSR already uses the semantic Next backend; a current architecture fact is valid, a selectable second backend is not. |
| `global.advanced.46c60fc2f8f9` | Dev marks the standalone shortcut toggle as retired and folds its dock into ExperimentalHomepage. Current bounded navigation commands do not recreate a pinned world/server dock. |
| `global.advanced.28957465d6fb` | Dev has a default for Debug Skip Copy but no executing source consumer. An actual copy-bypass implementation must not be invented. |
| `global.advanced.a4b638d31598` | The explicit advanced action now opens the actual persisted settings.json through the native local-file association adapter. It admits absolute regular local files, rejects controls/leaf links/directories/Unix devices, and passes Unix paths as one argv item. Its composition callback reports a fixed error for ordinary file/association failures instead of throwing through a render callback. The separate redacted settings capture remains read-only. |

The remaining global game descriptions need precise kinds: `global.game.b176ae552c3b`
refers to captured directory/isolation resolution, `.8238cd399639` to mandatory asset
verification, `.63ad38145ea7` to admitted OS/architecture/library metadata and native
selection, and `.722e1a55163d` to the actual executor-captured effective plan. These are
fixed read-only policies/facts when mapped, not newly editable global policy strings.
`global.game.a0a1c7543f09` must follow its actual startup-hint preference consumer, rather
than being declared complete merely because launch progress exists.

Other audit domains—content provenance/hash policy, instance Java UI placement, recovery,
storage cloning, server/network/identity/advanced policies—are assigned to their respective
owners and the final root closure. They are not made Available by an unrelated runtime counter.
