# XSR-789 — Settings consumer coverage

This delivery closes the remaining current-capability slices in the settings
migration ledger. Each available position must have a durable Service contract,
an actual consumer, an accessible Host editor/action, and behavior tests. Group,
choice, operation and fact positions are not duplicate persisted settings. The
catalog records 540 IA positions and 46 value definitions, of which 45 have available
consumers. The optional `java.compatibility` policy remains unavailable; required
compatibility checks cannot be disabled through its retained definition.

The independent consumer contracts are locked in XSR-783 through XSR-788 before
their implementation: launch hooks, network preferences, managed Java removal,
appearance, disk logging and storage transactions. Existing Builtin → Global →
Instance resolution, durable-first publication, scope-qualified instance identity,
revision-checked batch writes and local-only export filtering remain authoritative.
Existing public composition signatures stay source and binary compatible. The
additional managed-Java root uses a separately named composition entry point so
an existing positional `null` observer keeps its original overload resolution.
The storage-aware runtime entry point is likewise named `ComposeWithStorage`,
leaving the original `Compose(host, null)` call source-compatible.

Additional values introduced by this delivery:

| Key | Domain/default | Scope | Applies | Legacy | Export |
|---|---|---|---|---|---|
| game.pre-launch-wait | Boolean / true | Global + Instance | Next launch | LaunchAdvanceRunWait | Yes |
| network.bandwidth-kib | Integer KiB/s, 0–1048576 / 0 unlimited | Global | Next transfer | ToolDownloadSpeed with explicit slider conversion | Yes |
| network.ip-stack | auto, ipv4, ipv6 / auto | Global | Next HTTP request | New (independent of Java address preference) | Yes |
| appearance.theme-mode | 2 system, 0 light, 1 dark / 2 | Global | Immediate | UiDarkMode | Yes |
| appearance.accent | blue, purple, green, orange / blue | Global | Immediate | UiAccentColor | Yes |
| diagnostics.disk-log-days | Integer days, 1–90 / 7 | Global | Immediate | New | Yes |

Proxy mode/address/credentials are edited and applied as one revision-checked
batch. The password uses the renderer's sensitive text-input contract and is
never part of scene text, clipboard copy or ordinary settings export. Proxy
credentials and local commands retain their existing local-only classification.
Each transfer captures its bandwidth generation; active transfers retain their
budget while subsequent transfers share the newly committed generation.
Pre-launch editors preserve command lines and indentation without placing local
commands in exported settings.

Java compatibility checks, file authenticity, regional authority, update
signatures and recovery ownership remain required. A setting must never turn an
unimplemented capability into a claimed working toggle. Future Cloud, CAS,
arbitrary profiles, system integrations without Host consumers and reserved
diagnostic features retain explicit unavailability and their final IA positions.

Managed builds and executable Service/Desktop behavior harnesses are required.
The architecture gate, renderer gates, NativeAOT Service/Desktop publication and
trimmed desktop shell checks validate the affected boundaries; interactive native
platform behavior is reported separately from display-free shell probes.

## Integration evidence

The integrated worktree was validated on 2026-10-05 on Debian 13, Linux x64, with
.NET SDK 10.0.100, before the delivery commit. Builds used `XsrVersionChannel=ci`
and the base-revision version seed `XsrCommitShort=4023d5`; this seed does not claim
that the validation binaries were built from the eventual delivery commit. Native
backend checks use the X11 display supplied by Xvfb. The executable harnesses below
must be run directly; `dotnet test` does not execute these cases.

| Gate | Confirmed result | Evidence scope |
|---|---|---|
| Release solution rebuild | Passed, zero warnings and errors | Full `-t:Rebuild`, integrated projects and public source-compatible composition calls |
| Managed Services harness | 583 passed | Settings contracts, actual consumer behavior, failure/cancellation, storage and process-ownership regressions |
| NativeAOT Services publication and full harness | Publication exited 0 without warnings/errors; 583 passed | Unfiltered native executable, including actual hook workers, durable Java leases, stable log snapshots and storage transactions |
| Managed Desktop harness | 161 passed | Confirmed forms/intents, failed writes, navigation retirement and production composition |
| UI.Next renderer harness | 97 passed | Scene projection, input ownership, layout and render invariants |
| Avalonia scene backend harness | 15 passed | Xvfb native backend, including actual rendered bitmap pixels for Light/Dark and all four accents |
| UI.Next benchmark gate | Passed | Allocation, reuse and bounded-work invariants; reported timing percentiles are informational |
| Architecture harness | 70 projects passed | Sealed contracts and Service/Host/renderer dependency boundaries |
| Whole-solution formatting verification | Exited 0, no formatting differences | `dotnet format NexaCL.slnx --no-restore --verify-no-changes --diagnostics IDE0055 IMPORTS` |
| Managed Desktop `--validate-shell` and `--validate-setup` | Both exited 0 | Independent temporary data root; display-free shell and first-run composition probes |
| NativeAOT Desktop publication and probes | Publication exited 0 without warnings/errors; shell and first-run probes both exited 0 | Linux x64 native executable, independent temporary data root |
| Trimmed Desktop publication and probes | Publication exited 0 without warnings/errors; shell and first-run probes both exited 0 | `PublishAot=false`, `PublishTrimmed=true`, `TrimMode=link`, self-contained Linux x64 executable, separate temporary data root |

The controlled 120 Hz timing budget was not executed. These Linux checks do not
establish Windows/macOS manual GUI acceptance, a real JVM game session, native
system keyring behavior or live online authentication. They also do not establish
the status of hosted CI for the eventual delivery commit. Display-free
`--validate-shell` / `--validate-setup` checks must be reported separately from the
actual backend rendering checks and cannot replace platform interaction evidence.
