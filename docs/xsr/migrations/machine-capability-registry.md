# Machine Capability Registry 1.1 — foundation

Source: user-provided `PCL Nexa Machine Capability Registry 1.0.md` (body version 1.1, 2026-09-19).

The first-slice and 2026-09-19 records below retain their original delivery scope. Current
platform contracts are updated for the `565e5143` audit and the XSR-792 worktree; see
[XSR-792](../XSR-792-platform-capability-completion.md) and
[XSR-795](../XSR-795-unimplemented-inventory.md). Implemented providers, an unsupported
platform contract and missing physical acceptance are separate outcomes.

## Boundary

Machine facts, platform actions, environment facts, derived capabilities, estimates, policies, preflight issues and remediation are distinct. `Nexa.Services.Capabilities` owns typed definitions, immutable observations, the dependency graph and asynchronous provider collection. `Nexa.Services.Composition` seals the query/refresh routes; Desktop consumes only their DTOs and the separately declared revision state. Machine capabilities are observations, not executable Sidecar offers and not authorization grants. The existing Capability Fabric remains the authority for provider invocation.

## First delivered slice

- Stable namespace allowlist from Registry 1.1; typed `Capability<T>` and metadata (availability, source, provider, confidence, timestamp, requirements, permission, stability).
- Sealed registry rejects duplicate IDs, missing dependencies and cycles. Read-only snapshots have ordinal order and typed lookup. Registration never performs hardware probes.
- A broker coalesces concurrent refreshes, probes off the UI thread, isolates failed providers, enforces declared ownership and types, and publishes a revision only after a complete snapshot. Caller cancellation does not cancel shared work. Cached facts are refreshed explicitly or after a short TTL.
- A built-in provider observes OS/architecture, runtime, process-available CPU/ISA and physical/commit memory on supported systems. Physical and commit budgets are separate. Unknown hardware metrics remain unavailable, never synthesized from GC heap limits or GPU marketing VRAM.
- Settings gains a read-only Platform Features page with refresh, value, status, source and provider evidence. No cloud settings are reintroduced. No permissions are escalated and no platform mutation occurs during inspection.
- Preflight issue construction enforces: only verified hard constraints may block. Information does not raise overall severity. Estimator/compatibility/policy/launch wiring, per-instance environment providers, calibration, remediation execution and hardware-specific GPU/thermal providers remain separate follow-up slices; their behavior is not claimed as implemented.

## Validation

Contract tests cover registry sealing/type/ownership/dependency invariants, immutable snapshots, cached/coalesced queries, failure isolation, cancellation, separate memory budgets and preflight certainty. Desktop tests cover Platform Features navigation and sealed-query rendering. Architecture tests forbid direct broker/provider references in Desktop; NativeAOT shell and trim smoke remain required.

Windows memory evidence uses the documented [PERFORMANCE_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-performance_information) page counters and PageSize; the commit limit is not treated as a hard launch blocker. The built-in Linux provider reads named `/proc/meminfo` counters. Current macOS physical memory uses `sysctl` and `host_statistics64`; the available observation explicitly records its free+inactive page semantics. The three commit-budget facts remain PlatformUnsupported because macOS has no equivalent commit-budget contract; they are not NotImplemented or zero. Shared runtime facts work across all three platforms.

## Probe wiring record (2026-09-19, historical)

Every namespace the first slice deferred now answers through a real probe, split exactly on
provider ownership (the broker rejects any fact whose definition belongs to another provider):

- **display**: EnumDisplayMonitors + GetMonitorInfo + VREFRESH (Windows), CoreGraphics (macOS),
  and the original sysfs framebuffer fallback (Linux). The later Windows DisplayConfig and
  XSR-792 XRandR paths supersede the original internal-panel/multi-display limitations.
- **storage/filesystem**: instance-path existence, writability, and volume free space scope to
  the active Minecraft root; case sensitivity and symlink support come from platform semantics;
  reflink/clone stays unavailable until per-filesystem interop exists (a wrong "yes" silently
  duplicates data).
- **power**: AC/battery through GetSystemPowerStatus (Windows) and /sys/class/power_supply
  (Linux); the active scheme through PowerGetActiveScheme / cpufreq governor. macOS IOPS was
  deferred in this original slice and is supplied by XSR-792 below.
- **thermal**: CallNtPowerInformation(ThermalInformation) on Windows and hwmon on Linux.
  Sentinel/absent values publish Unknown — never a fabricated temperature.
- **gpu**: DXGI factory → EnumAdapters → IDXGIAdapter3::QueryVideoMemoryInfo with
  available = budget − currentUsage. Machines whose display driver predates Adapter3 (VMs,
  basic display) answer Unknown with the HRESULT — the contract forbids substituting static
  marketing VRAM.
- **java** / **minecraft.files**: the production LocalJavaRuntimeLocator and the shared
  MinecraftFileVerifier are the probes; without a resolved file plan the facts report
  unavailable instead of invented counts.

`tools/CapabilityProbe` (registered diagnostic project) composes the real foundation, forces a
refresh, and prints every fact with its availability and reason — the fast path for verifying
probe regressions on real hardware.

## Current platform completion (XSR-792)

- **Linux display** uses a direct, fixed-argument, deadline/output-bounded `xrandr --query`.
  Active outputs supply count; an explicit primary or one active output supplies the primary
  mode/refresh. Matching DRM connector type supplies built-in-panel evidence. Multiple outputs
  without a primary retain Unknown primary facts. Wayland is PlatformUnsupported for this
  contract; no graphical session or missing executable is DependencyMissing; failure/cancel
  retain their distinct outcomes. Framebuffer virtual dimensions are not a primary display.
- **macOS display** uses 32-bit `CGDirectDisplayID`, bounded active-display enumeration and
  geometry/refresh, `CGDisplayIsBuiltin`, and releases every copied display mode with
  `CFRelease`. Invalid values stay unavailable rather than being truncated into a valid fact.
- **Power** returns the five existing source/battery-present/level/charging/current-profile
  facts. Linux bounds sysfs enumeration/text, ignores device-scope batteries, weights comparable
  multi-battery energy capacity, and only uses direct capacity for one battery. macOS uses
  documented IOPowerSources and NSProcessInfo low-power state; Copy/Create objects are released,
  UPS is not an internal battery, and disabled low-power mode does not imply high performance.
- **GPU** budget/current-usage/available-budget preserves DXGI current-process dynamic budget
  semantics. NVML/AMD full-card capacity/usage and Metal recommended working set/allocations
  do not provide the same contract. Linux/macOS return all three PlatformUnsupported facts with
  a reason. This is an honest supported-platform correction, not a fabricated GPU adapter.
- **Temperature** remains hwmon where Linux supplies a readable sensor. macOS has no documented
  public driverless CPU-temperature API; private AppleSMC is outside this contract and the fact
  is PlatformUnsupported. Windows unavailable sensor evidence stays Unknown.

The local deterministic projection, NativeAOT/trim checks and physical CoreGraphics/IOPS/X11
device observations must be reported separately. See XSR-792 for actual validation; this
contract update alone does not certify hardware readings or physical acceptance.
