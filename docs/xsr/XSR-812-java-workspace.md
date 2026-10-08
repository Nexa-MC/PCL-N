# XSR-812: bounded Java diagnostic workspace

The existing Java inventory remains the authority for scanned runtimes and manual registrations.
Diagnostics reuse that inventory and registry revision rather than discovering executables from
arbitrary input. A typed properties query reads actual `-XshowSettings:properties -version`
output; a typed modules query reads `--list-modules`. Java 8 reports PlatformUnsupported for
modules, without trying an unsupported flag. Version, vendor, architecture, VM properties and
module names come from actual output, never synthetic success values.

A Platform port owns process effects. Services admit only live scanned inventory paths; the port
requires a fully qualified local executable, captures size, modified time and SHA-256, checks that identity immediately before execution and
after completion, and executes the two fixed argument sets without a shell. JAVA_TOOL_OPTIONS,
JDK_JAVA_OPTIONS, _JAVA_OPTIONS and CLASSPATH are omitted from the probe environment. Each run
has an eight-second deadline, at most 64 KiB from each output stream, and two process slots.
Identity hashing admits at most 128 MiB executables. Cancellation kills the owned process tree
and reaps the process with an independent two-second cleanup budget. An unconfirmed reap is
Failed and cannot use the “probe stopped” timeout/limit captions. Empty, malformed, changed, timed-out, oversize and
failed output retain distinct status and cannot be presented as successful facts.

Services return immutable typed facts and an explicitly redacted raw preview. The shared
diagnostic redactor removes credentials, private paths and identifiers; personal Java property
values are omitted before redaction. The UI only probes selected live inventory rows, shows
bounded fact/module/raw pages, and can copy only the shown redacted preview through an optional
native callback. Navigation, inventory changes and disposal cancel outstanding queries and
retire captured actions. Services contain no Process.Start or renderer references.
The diagnostic panel owns a direct inventory-child root separately from its nested form body.
Refreshing selection or facts replaces that body's children once; idle frames reuse the root.
Retirement destroys the whole owned root, including title and surface, so a nested form body is
never mistaken for a direct child and appended again.

The properties operation includes the actual `-version` query. Fingerprints are captured for
the requested probe, rather than claimed to be scan-time fingerprints. VM/vendor/architecture
fields and modules are bounded; raw display pages contain at most 32 lines of 1024 characters.
Properties with private values are stripped by key regardless of spacing around `=`.

The existing inventory scanner also uses this same bounded Platform properties probe, replacing
its former unbounded ReadToEnd process implementation. It keeps its discovery/parser/registered
runtime behavior and test inspection adapter. Cached discovery still observes caller cancellation,
and late noncooperative inspection results cannot bypass cancellation. This does not imply a
whole-filesystem Java scan or a new arbitrary command execution surface.

Tests cover admission, registry revision, fixed probe operations, actual properties/modules,
Java 8 unsupported behavior, malformed and empty output, redaction, file-identity replacement,
byte limits, cancellation/reaping and live UI selection/retirement. Root integrates routes,
controller hooks and optional clipboard effects, then records consolidated build/AOT evidence.
The executable fixture covers real POSIX process creation and cleanup. Windows/macOS device
acceptance remains a separate physical-platform check; these tests do not claim it ran here.
