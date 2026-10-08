# XSR-805 — Runtime initialization and native observations

2026-10-08 (Asia/Shanghai). Scope: PCL-N startup and the JVM Host boundary.

## Contract

Native startup owns its dispatcher before storage recovery and service composition. An
interactive startup window renders a real stage, offers cancellation/close, and remains
available on initialization failure. Semantic shell composition runs outside that dispatcher;
the completed shell is attached to the same application lifetime. Startup timing records the
first native render callback separately from window construction, stage changes, and shell
readiness. A render callback confirms submission, not scan-out: no unsupported physical
display latency or release SLA is claimed.

Native memory pressure is sampled from platform data (Linux MemAvailable plus cgroup limits,
Windows GlobalMemoryStatusEx, and macOS vm_stat). It trims idle bitmaps, lowers the bounded
decoded-image admission budget, and preserves leased visible resources. Pixel charge remains
an admission unit. GPU telemetry is nullable and provider-qualified; unknown GPU residency
never becomes zero or an assertion that the entire compositor fits a pixel budget.

JVM host controls and observations receive a native platform adapter. CPU sets and QoS use
actual supported OS mechanisms. The additive `IJvmHost` controls preserve existing host
implementations: their default interface methods return a finite `platform_unsupported`
failure. Only the production host overrides those methods to invoke its native adapter;
the production capability catalog describes that provider, not a custom host's defaults.
Process commit and process-tree resource aggregation retain
the metric definition and sample source. Linux process GPU memory comes only from DRM fdinfo
when exposed; Windows reads the driver-provided PDH GPU Process Memory Dedicated/Shared Usage
counters. These report driver memory usage and are not asserted to be per-texture physical
residency. Unsupported or inaccessible counters remain unknown. System-event correlation
is bounded to the session interval and PID, and absence of a collector is explicit. Runtime
percentiles are computed from their own measured sample series. Launch peak IDs cover an
explicit first-30-second observation window (shorter runs retain actual duration); this is
not a claim to detect entry into a Minecraft world. Full-run calibration retains its own
peaks. Heap/native/commit/GPU are
never inferred from working set or replaced by internal sentinel zero. Standard input remains
unavailable because the private bootstrap transport consumes it; this is a lifecycle contract,
not an unimplemented general-purpose terminal.

The four developer appearance features consume an explicit, read-only runtime capture.
The capture retains one bounded snapshot, refreshes only on demand, and is retired when
the page/developer mode closes. Renderer scene/version/layout visits and queued state
entries are actual last-pass/current facts; native committed scenes and node drawing
callbacks are process-wide submission counters. Motion diagnostics read the actual
shared scheduler's active tracks, timer state, configured target and a fixed 512-timestamp,
two-second tick-rate window. Scheduler ticks and drawing submissions are never labelled
physical display FPS. No provider for physical presentation rate means unavailable.
Raster diagnostics expose current decoded-pixel charges, effective admission budget,
leases, decode/disposal counters and the last real native pressure sample; pixel charge
is never substituted for physical GPU residency. The capture does not read state values,
private paths or log contents and introduces no polling clock.

Windows system correlation reads bounded System and Application event windows. GPU reset
and WHEA driver events can have PID 0, so their admitted provider/event IDs are explicitly
time-correlated facts, not asserted JVM causes. Application error/report records likewise
carry a time-window relation unless a matching process ID is actually present. XML parsing
retains only channel/provider/event ID/timestamp/relation metadata, never raw EventData or
messages with user paths. An absent/denied channel remains unavailable rather than an empty
successful result. JVM exit diagnostics attach the established instance-directory scope
hash and session identity to structured durable operation facts, never the raw directory.

The production XSR trace adds observers beside the existing operation-log observers, preserving
both delivery paths even when one diagnostic observer fails. A session-local fixed 256-entry
ring exposes a typed read query with retained/dropped counts. It retains only semantic and
correlation identities, monotonic timestamps, generated stage/duration/revision facts and
success flags: request/state/event payloads, event scope keys and exception messages never
enter the trace. Privacy developer cards show bounded 32-row capture pages for operation,
state and launch dispatch subsets. Explicit refresh retires the previous capture; no viewer
polling, replay/mutation command or raw trace export is added.

Hardware advice consumes the captured instance-scoped estimate and the typed preflight
evaluation of that same snapshot. Four read-only cards expose shader model budget, resource
load peak and enabled-pack count, captured render distance, and bounded current risk codes.
Missing shader/hardware/options inputs remain explicit; a zero model component is never
proof that no shader is active. Suggestions can ask the user to reduce settings and recapture
preflight, but do not modify options.txt, resource packs or shaders. Existing seven resource
estimate catalog states refer to the real Platform resource-estimate rows, with source and
confidence; historical calibration reports actual admitted history, not fabricated samples.

## Validation

The migrated launcher hardware-acceleration preference is a restart policy, separate from
the game's GPU/renderer environment. A bounded early settings read captures the existing
`SystemDisableHardwareAcceleration` Boolean through
`appearance.hardware-acceleration-disabled`. One typed native rendering configuration is
applied before initializing Avalonia in both early startup and standalone shell paths.
Windows and Linux select only the SDK Software rendering mode when disabled; the enabled
policy selects their native GPU modes with Software fallback. Windows software uses the
redirection composition surface. The early native lifetime's captured policy remains
authoritative through shell handoff, even if preferences change during composition. macOS
has no admitted software-force provider in this workflow and reports the preference
unsupported. Configuration and real native startup tests establish the selected SDK
options and lifecycle handoff; they do not claim physical GPU rendering or frame rate.

Java policy status follows the last committed effective `java.runtime` value and revision
while the global Java page or the instance's merged Game/Java page is visible. Both expose
the same fixed mandatory compatibility policy, without asserting that a preferred runtime
has already been verified. The retained status entity updates in place after
automatic, preferred-path, or inherited commits, preserving unrelated editors, focus,
drafts, scroll state and native entry transitions. No path is included in the status text;
an effective-value read that has not completed remains explicitly pending.

Contract tests cover pressure admission and lease preservation, unavailable observation
projection, measured zero, distinct GPU/commit percentiles, and native Linux process sampling.
[XSR-820](XSR-820-completion-closure.md) records build, architecture, NativeAOT and trim results. Physical
8-hour operation, real GPU/driver residency and cross-platform interactive acceptance remain
external evidence and are not fabricated by deterministic tests.
