# XSR-785 — Managed Java lifecycle and removal

Only a completed installer journal under a composition-supplied runtime root proves
ownership. Directory location, discovery source and custom registration are insufficient.
The immutable inventory carries the executable, component directory and verified plan
identity. Desktop confirms deletion and dispatches that captured identity and registry
revision through the existing Java management command. External removal unregisters only.

Deletion takes the installer root lease, validates completion, path containment and every
entry without following symbolic links, and verifies that the complete tree still matches
the verified upstream download plan's expected file sizes and SHA-1 hashes. Changed or
additional files, stale previews, ongoing installation and active launcher sessions reject
the operation. Launcher sessions hold independent cross-process use leases, and multiple
games may use the same runtime concurrently. An unlocked lease is not proof of exit: the
launcher can crash or dispose a session while its JVM remains alive.

`MinecraftLaunchExecutor` owns the use lease for every prepared launch, including the
public `MinecraftRouteIds.Launch` handler and launches prepared by the coordinator. It
acquires the lease before native extraction and pre-launch hooks, transfers it to the
actual JVM session as soon as process creation succeeds, before checking cancellation.
`BindProcess(int)` records the actual PID and UTC process-start ticks in a fixed 20-byte
little-endian record: eight-byte version magic `NXJAVA01`, four-byte positive PID and
eight-byte positive UTC ticks. The record is flushed to disk while its exclusive lock is
held. A bound lease closes its lock on disposal but retains its record whenever that
process remains alive or its termination cannot be proved. Preparation failure or
cancellation before process creation releases
the lease; failure or cancellation after creation first terminates the owned process.
The coordinator does not acquire a second lease. Hook waiting and a running JVM both
prevent managed deletion. Successful termination allows the record to be removed; a
timeout or failed termination leaves durable exclusion for the still-running process.

Deletion and installer replacement parse unlocked records with exact length, version,
PID and timestamp checks. They reclaim a bound record only when the PID no longer exists,
its process is proved exited, or a later process-start timestamp proves PID reuse. An
earlier observed start time does not establish reuse. Empty, partial, malformed and
unresolved records remain excluded: host death between child creation and binding cannot
establish that the child died. Such records require explicit recovery after the operator
has established that no game still uses that runtime; automatic sweeping cannot discard
them. Normal unbound preparation cancellation can remove its own record because no JVM
has been handed off. `AcquireAsync` continues to return null for unmanaged executables.

OS identity observation belongs to `Nexa.Platform.Abstractions.IPlatformProcessIdentity`
and its `Nexa.Platform.Runtime` implementation. The platform returns running with actual
UTC-start ticks, proved exited/absent, or unknown; it owns process lookup, handles and
permission/error interpretation. Services own the lease format and conservative decision
rules, and do not invoke OS process discovery. The default factory preserves existing
constructors and `AcquireAsync`/`BindProcess(int)` signatures.

The verified component is renamed to isolated quarantine before the durable registration
write. Failed persistence or cancellation before that write restores the component.
Successful persistence removes its registration and invalidates discovery; quarantine
cleanup follows commit and may be safely retried. Post-commit cancellation does not
roll back committed removal. No other runtime or user directory is traversed for deletion.

Required validation covers successful removal, external files, modified/extra files,
revision and plan changes, persistence rollback, cancellation, symbolic links, active-use
and installer exclusion, Desktop confirmation cancellation and navigation retirement.
Foundation route registration and actual launch lifespan bind the new lifecycle guard.
Direct executor regression coverage verifies the same exclusion during a waiting hook,
while a real child process runs, after process exit, and after failure or cancellation.
Persistent-lease coverage releases the launcher lock while a real child is alive, checks
both deletion and actual installer replacement, then proves reclamation after exit.
Malformed, unresolved and contradictory identities remain untouched; a later process
birthday safely retires an old PID lifetime.

Launcher data migration does not relocate Minecraft or Java runtime roots. Its preview
and startup admission reject data trees containing root-bound installation/recovery
journals, including completed Java receipts. These receipts retain their original absolute
root and target paths; copying or rebasing them cannot grant ownership at a new root.
An actual installer-to-storage regression verifies rejection preserves receipt bytes,
source inventory, process-use leases and subsequent managed removal.
