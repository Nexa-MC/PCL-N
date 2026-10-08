# XSR-829 — Shared state, startup and compatibility closure

2026-10-08. This change closes the six requested state, startup, input, cleanup and
launcher-mod integration items in PCL-N on `refactor/xsr`. The independent Nexa.Plugin
executor/SDK and name-only roadmap capabilities remain outside this change.

## Delivered behavior

- [XSR-823](XSR-823-shared-state-cache.md): application composition owns one bounded
  reusable query cache. Concurrent readers share work, cancelled readers detach, and
  invalidation prevents older work from republishing. Installed-version display snapshots
  survive restart and offline roots; live reconciliation controls selection, launch and
  destructive commands. Launch preparation always reads authoritative manifests.
- [XSR-824](XSR-824-durable-online-information.md): resource identity, provider details,
  translations and installation catalogs retain verified positive metadata on disk.
  Source policy and exact file identity qualify reuse. Retained stale information is
  visible immediately and renews in the background; failed or incomplete providers cannot
  erase a verified record. Downloads revalidate live metadata. Persisted representations
  omit credentials and secret-bearing links, freeze owned collections and reject linked
  cache paths.
- [XSR-825](XSR-825-scrollbar-input.md): vertical and opt-in horizontal scrollbars accept
  thumb dragging and track paging through the renderer's clipped hit geometry. Nested
  scrolling, capture, cancellation, disabled controls and modal/navigation changes keep
  their existing input semantics.
- [XSR-826](XSR-826-splash-readiness.md): finite initialization and local installation
  recovery complete under the native Splash. The prepared hidden window is reused at
  handoff. Optional online preparation has a deadline; required local failures support
  retry or cancellation without losing the primary single-instance lease.
  Disabling owned OS startup entries also succeeds when their parent directory is absent.
- [XSR-828](XSR-828-code-consolidation.md): Common owns repeated atomic file-writing
  mechanics. Five namespace-only files, an obsolete resource cache and unused helpers
  are removed. Unique contract, platform and native tests remain; apparent similarity
  alone does not justify deleting coverage.
- [XSR-827](XSR-827-launcher-mod-compatibility.md): the native JVM bridge accepts the
  bounded executable-child invocation used by Crash Assistant and compatible relaunch
  paths. It preserves argument boundaries, working directory, output and exit status,
  and enrolls auxiliary Java processes in the managed-runtime use lease. Linux procfs
  exit evidence permits reclamation after the auxiliary process actually exits.
  A managed auxiliary invocation whose parent enrollment has already retired is refused
  before Java starts.

Existing public composition entry points retain their CLR signatures. New cache-directory
entry points have distinct names. No service acquires a Desktop/Avalonia dependency, and
Sidecar retains its numeric binary process boundary. Product identity remains NexaCL,
Firefly Alpha 6 / `2.0.0.alpha.6`.

## Scope and acceptance limits

The catalog's 54 functional roadmap reservations and 78 structural Group/Choice positions
remain explicit. This change does not invent World Guardian, complete foreign-launcher
formats, CoW providers, another rendering backend or automatic AI repair contracts.

Crash Assistant compatibility is backed by pinned upstream source inspection and an owned
Java fixture exercising its native executable-child call pattern. The fixture is not a
real Crash Assistant GUI or full loader/modpack acceptance run. Windows/macOS and the
Java-version matrix remain CI targets; local runtime evidence comes from Linux x64.
If the game exits before its auxiliary bridge obtains a managed-runtime enrollment, the
bridge reports a fixed failure and the auxiliary window may not open. This schedule does
not receive an implicit new ownership grant from an empty use directory.

Automated acceptance tooling and virtual-display probes do not certify physical-device
performance, a two-hour soak or the remaining external acceptance-ledger rows.

## Integrated validation

Validation uses Linux x64, .NET 10, the exact `2.0.0.alpha.6` product version and an X11
virtual display for native-window checks. Published artifacts are rebuilt after production
changes; test-only synchronization fixes preserve the existing business assertions.

| Check | Result |
| --- | --- |
| Full Release solution build; final Services and Desktop test builds | 0 warnings, 0 errors |
| Services, managed and NativeAOT | Each harness reports 713 passes; five conditional integration cases explicitly skip privileged work |
| Desktop, managed and NativeAOT | 242 passed, 0 failed in each harness |
| UI.Next, managed and NativeAOT | 105 passed in each harness |
| Avalonia backend | 26 passed; native startup failure/retry, hidden preparation and lifetime handoff smoke also passed |
| Architecture | All 70 projects satisfy the checked dependency and API boundaries |
| XSR Runtime / Sidecar / PXML | 170 / 35 / 40 passed; Runtime and Sidecar report no skips; both Sidecar performance gates passed |
| Python acceptance / release tooling | 22 / 31 tests passed |
| Renderer benchmark | Deterministic gates passed; the 521-entity clean redraw allocates 0 bytes |
| NativeAOT Desktop and JVM Host; trimmed Desktop | Published without warnings or errors; Desktop shell and setup checks exit 0 for both publish modes |
| Native Desktop windows | Prepared first-run wizard and normal shell become visible; protocol association, actual second-process activation, safe-mode exclusion and ordinary close pass |
| Native JVM executable-child fixture | Argument validation, normal/exit/tree-stop/orphan paths, live managed-use protection, exited-child reclamation and retired-parent refusal pass |
| Source formatting and Git whitespace | Verification passed |

The five Services skips are the dedicated privileged native differential and transaction
fixtures, Windows elevated updater and protected high-water writes, and an isolated unlocked
system keyring. Their harness pass lines do not imply those integration paths ran locally.
The acceptance tooling validates 126 candidates; the release ledger remains `not-accepted`
with 0 of 9 external rows accepted. Benchmark gates do not certify physical-device latency,
publisher trust, sustained soak behavior or complete loader/modpack compatibility.
