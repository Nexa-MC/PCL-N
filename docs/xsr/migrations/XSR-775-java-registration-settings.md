# XSR-775 — Custom Java registration and availability

Java management uses sealed inventory queries and commands. Services persist a bounded
registry in the existing durable settings store; Desktop does not enumerate directories,
probe executables or read registry files. External Java removal removes registration only.
Automatic candidates may be disabled without deleting their files. Disabling is respected
by automatic selection and by explicitly selected registered runtimes at the next launch.

Adding Java requires an explicit user-selected executable, real runtime inspection and a
revision-checked durable commit. Import/export of ordinary settings cannot register code
for execution. Registry changes invalidate the shared locator cache only after persistence
succeeds. Discovery merges paths with platform-aware identity and preserves actual probed
metadata; unavailable registrations remain removable without inventing runtime facts.

Host supplies the file picker; UI reads immutable inventory/registry projections and emits
intents. Async picking and probing retire when navigation or instance scope changes. Java
compatibility checks remain mandatory. Managed deletion is a separate consumer slice with
directory ownership, active process/install checks and filesystem transaction requirements.

Verification includes durable restart, stale revision and save failure, disabled discovery
and explicit selection, duplicate path identity, unknown metadata, cancellation, sealed route
dispatch, Desktop scope retirement, architecture checks and NativeAOT/trim CI.
