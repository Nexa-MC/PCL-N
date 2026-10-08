# XSR-817: bounded advanced workspace

Advanced IA distinguishes callable actions and captured facts from persisted settings. Existing
release notes, protected update signature/immutable-slot/recovery policy, nexacl:// activation,
CLI, launcher safe mode, command palette and operation trace already have consumers. Their
keyless historical Setting rows must be reconciled as Action/State individually, with their exact
supported scope. A raw JSON availability edit is insufficient for the computed catalog.

A dedicated UI.Next advanced workspace exposes the existing eight safe navigation routes and
an optional composition-owned command palette callback, actual bootstrap safe-mode state,
renderer/native counters, and finite composition feature facts. Actions retain the standard
live-source admission and do not run arbitrary shell or launch games automatically.

Developer raw settings are a captured typed effective-policy snapshot, rendered at most 24
rows per page and 256 entries total. Only validated Boolean/Number/Enum values are exposed;
Text and Path values and validation error bodies are redacted. User paths, credentials,
commands and custom JSON are never copied. Copy receives only this redacted captured snapshot.
The developer workspace separately exposes an optional composition-owned native file-open
callback for the actual local settings.json. The UI warns that the original file can contain
paths, accounts and credentials; it sends no file contents and opens it only on an admitted
user click. The callback captures the trusted backing file at composition, accepts no UI path
or shell command, and leaves the button disabled when unbound. Developer gating, live-source
admission and retirement apply independently from snapshot copy. Native ownership remains in
Platform; the Desktop controller does not start a process or read/upload the raw file.

Debug Delay is a fixed read-only 200 ms asynchronous scheduler probe, recording actual elapsed
monotonic time. It does not block the renderer or delay production operations. Page navigation
and disposal cancel the probe and retire the result. Debug Mode and Debug Animation are captured
facts from developer policy and the real animation scheduler, rather than extra mutable keys.

Unimplemented arbitrary backend/homepage selection, arbitrary
compatibility workarounds, copy/validation bypass and unrestricted feature-flag mutation remain
explicitly unsupported contracts. The workspace does not turn those controls on to clear labels.
The restart-bound hardware-acceleration preference has an actual Windows/Linux native rendering
consumer; its supported modes and macOS boundary are documented in
[XSR-805](XSR-805-runtime-performance-completion.md).
Existing AI suggestions and separately confirmed typed repairs do not establish arbitrary AI
repair execution. Root records these exact remaining distinct contracts in XSR-820.

Tests exercise finite-route callbacks, developer gating, redaction (including credentials,
paths and JVM command text), snapshot paging/copy, real scheduler completion, and source
retirement. Root owns integration hooks, complete suite and AOT/trim evidence.
