# XSR-820 — Completion closure and consumer reconciliation

2026-10-08. Scope: PCL-N on `refactor/xsr`; external Nexa.Plugin SDK/executor and Cloud are excluded.
Completion closes existing behavior contracts and migration gaps. Name-only future capabilities
remain explicitly reserved roadmap positions.
The baseline audit is [XSR-799](XSR-799-remaining-work-audit.md). This document records its
implementation follow-up rather than rewriting historical audit results.

Catalog availability must follow a concrete consumer. Persisted preferences retain `Setting`
and a schema key; actual observations and mandatory policies use `State`; user operations use
`Action`. Group/Choice positions describe information architecture and do not count as independent
unfinished runtime capabilities. Reviewed keyless consumers are enumerated in the completion map
and the domain notes, rather than admitted solely by a JSON availability label.

Native blur strength/sampling have no supported portable parameter on the current native window
adapter and are `PlatformUnsupported`. Legacy internal JLW/RW/Log4j/unsafe-agent and compatibility
bypass switches have no XSR execution API; retaining their old labels must not expose ineffective
editors or imply that mandatory verification can be disabled. Likewise, undefined future backend,
foreign-launcher formats, clone/CoW providers and independent lockfile contracts are not implemented
by relabeling an unrelated observation. Explicit support boundaries remain visible.

Safe Launch moves all mods, not just recently added mods. Its catalog label is corrected to the
actual fixed behavior and presented as a policy fact. The existing `UseSystemGlfw` native-library
filter gains layered `game.system-glfw` preference consumption; an explicit committed value applies
on the next launch, a builtin value preserves an explicit request/legacy metadata choice, and Safe
Launch clears it. Unknown navigation and unbound page destinations reject with feedback while
preserving the current page; the production root attaches actual controllers for every finite route.
Settings rebuilds rebind every new editor before accepting input. Asynchronous fact completions
defer replacement of controls until queued input from those controls has been consumed; instance
retirement still cancels reads and rejects stale sources before any mutation.
Named/temporary launch controls require matching committed effective-value and profile revisions;
pending reads and writes disable their actions, and retired controls cannot revive a removed
temporary layer. Same-layer refreshes preserve name/directory drafts and input focus. Single-instance
shutdown retries the file lease between bounded pipe-connect attempts, so retiring the listener
cannot consume the entire bootstrap budget before the released lease is checked again.
Retiring a resource Sidecar surface removes its own queued sources and cancels its requests;
entering the ordinary resource detail page preserves queued download, external-link and optional
dependency actions. Leaving both resource pages retires the remaining ordinary actions as well.

## Delivered follow-up

- XSR-800/808/813: third-party launch identity, revision-checked instance identity and server
  policies, layered launch preferences and actual launch diagnostics.
- XSR-801/818: mod/bulk updates and rollback, world/datapack health and snapshots, config
  editing, screenshot crop/timeline/clipboard/share, operation history and actual content hashes.
- XSR-802/816: named/temporary launch configurations, owned file overlays and recovery, Safe
  Launch, hooks, system GLFW and supported game GPU/renderer environment policies.
- XSR-803/807/810/815: system startup/notifications/file associations, native media/theme
  preferences, jump lists, startup navigation and live global resource-source admission.
- XSR-804/814: reference-aware local CAS backups, thin backup/verification/restore/pruning,
  bounded legacy migration and offline readiness, durable diagnostics and explicit AI policy.
- XSR-805: usable native startup/error/cancel/handoff, pressure admission and measured native
  resource observations, hardware advice, renderer diagnostics and bounded XSR traces.
- XSR-806/809: finite typed Sidecar Function/UI extensions, numeric same-session bindings,
  retirement-safe interactive views, CLI/command palette and launcher safe mode.
- XSR-811/812/817/822: network and Java diagnostic workspaces, advanced read-only facts and
  native settings-file opening, manual Java license/preview/install/cancel/status workflow.
- XSR-819: fixed and bounded-template localization, including native picker and accessibility
  consumers; user paths, provider replies and raw diagnostics retain their original text.

## Final inventory

The catalog contains 543 positions and the schema has 97 definitions: 96 mutable and 80
exportable. Compatibility reads retain the obsolete definition but mutation/import/profile
admission and export do not expose a bypass of mandatory Java validation.

| Catalog | Available | PlatformUnsupported | NotImplemented |
| --- | ---: | ---: | ---: |
| Raw | 399 | 12 | 132 |
| Linux | 395 | 16 | 132 |
| Windows | 395 | 16 | 132 |
| macOS | 389 | 22 | 132 |

The 132 reserved positions consist of 78 Group/Choice structural positions and 54 functional
roadmap positions. Their exact IDs remain in [the entry map](settings-entry-map.md); the domain
notes above record their support boundaries.
No independent World Guardian, full foreign-launcher formats, clone/CoW provider, next renderer
backend or automatic AI repair contract is inferred from the delivered finite workspaces.

## Integrated validation

The integrated Release build completed with zero warnings and zero errors, including
warnings-as-errors. The architecture harness passed for all 70 projects. The following evidence
was collected from this Linux x64 cloud environment against the completed source, with published
outputs kept outside the checkout:

| Verification | Result |
| --- | --- |
| Services, managed and NativeAOT | 683 cases reported by each harness; five privileged scenarios explicitly skipped |
| Runtime, managed and NativeAOT | 170 passed; no skips |
| Sidecar, managed and NativeAOT | 35 passed; no skips; numeric codec allocation and RTT gates passed |
| UI.Next | 100 passed; renderer benchmark gates passed |
| PXML | 40 passed |
| Function Patch compiler | Compilation/execution and 18 rejection cases passed |
| Avalonia native backend | 25 passed, including real local media/MPRIS and native path admission |
| Acceptance tooling | 22 tests passed; ledger consistency passed |
| Release tooling | 31 tests passed, including macOS bundle metadata |
| Desktop controllers, managed and NativeAOT | 237 passed in each harness; no failures |
| Desktop application, NativeAOT | Published native executable; shell and first-run validation passed |
| Desktop application, trimmed | Published self-contained trimmed executable; shell and first-run validation passed |
| Native startup and handoff | Actual startup lifetime, hardware-policy capture and handoff smoke passed |
| Native X11 window | `NexaCL` title, protocol forwarding, safe-mode directory exclusivity and normal close/release passed |

The five Services skips require dedicated privileged native differential/transaction fixtures,
elevated Windows update/high-water writes, or an isolated unlocked native keyring. They are not
claimed as exercised by the Linux cloud run. The complete Desktop native harness exercises
cross-process forwarding, closing-owner lease transfer and the same controller cases as CoreCLR.

Physical client matrices, publisher signing, reviewed real-failure corpus, eight-hour soak,
authenticated online integration and independent Nexa.Plugin execution remain separate acceptance
evidence. The Alpha 6 ledger retains its actual partial/blocked/pending states; passing finite
contract tests does not mark these release gates accepted.
