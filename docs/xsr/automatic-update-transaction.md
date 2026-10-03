# Automatic update transaction (XSR-754)

The same-account attacker boundary in update-privilege-boundary.md remains mandatory.
Automatic updates use a preinstalled native helper, not the legacy path-based apply or
restart APIs. The helper never loads user plugins or starts the launcher as administrator.

## Installation and activation

The machine installer owns a stable bootstrap and helper. The application payload is
installed in immutable version slots. Windows uses Program Files/NexaCL; Linux uses
/usr/lib/nexacl; macOS uses /Library/Application Support/NexaCL, with a public application
entry in /Applications. macOS must not use the group-writable /Applications directory as
privileged mutation authority. Installer types stay setup.exe/MSI, DEB/RPM/AppImage and DMG
containing PKG, with portable archives. Portable or unsafe installations use manual updates.

The helper independently obtains the release manifest and detached signature from the
fixed Nexa GitHub repository, verifies the embedded publisher key, channel, RID, version,
exact package size and digest. Caller input is only a canonical public target version.
Network redirects cannot authorize unsigned bytes. All writes are relative to retained
admitted directory handles/descriptors. Staging is fresh and private; published payloads
are readable/executable, never writable, by ordinary users. Existing unsafe permissions
are refused rather than repaired.

A bounded, flushed append-only activation journal selects a completed slot. A partial
last frame preserves the preceding activation. A private transaction journal records
the authenticated manifest, package identity, previous activation and preparation phase.
Recovery re-verifies cached publisher evidence and actual package bytes. Accepting a
version advances the private high-water journal before activation; a persisted transaction
is the only authority to resume that exact accepted version. Rollback changes activation,
never lowers high-water. Old slots are retained; updates do not delete unknown files,
user configuration or files still used by a running game.

## Runtime boundary

Platform abstractions expose admitted update directory/file leases, exclusive durable state,
and create-new operations. Platform.Runtime implements Win32 object-relative and Unix
descriptor-relative access. Services.Updates implements signature and transaction policy.
Nexa.Update.Helper is an intentionally new privileged executable composition root. It has
no Desktop, renderer, accounts, Sidecar, telemetry or plugin references.

The unprivileged bootstrap reads only protected activation state before normal application
initialization. UI update requests run asynchronously through a typed update contract;
progress is projected from protected state, never from caller-owned staging. Restart occurs
under the original user identity. Failure to admit the helper or installation reports the
need for a trusted system installation/administrator authorization; it never falls back
to privileged user-writable code.

Rollback publishes the selected previous version with phase `rolledback`; it must not
leave a completed-new-version status after selecting old bytes. The UI offers restart
only when the selected complete/rolled-back version differs from the running version.
If exit occurs between rollback activation and status publication, projection uses the
activation version instead of advertising the superseded completed update.
Progress polling schedules a cancellation-bound 500 ms wake through presentation State;
it does not depend on input or continuous render frames. A still-running authorized helper
can finish after the GUI exits, and the next GUI continues observing protected status even
if a redundant recovery request encounters the exclusive transaction lock.
The helper has a 20-minute cancellation budget covering response bodies and preparation,
not just HTTP headers. Timeout records a recoverable pause before the short activation
commit. DEB/RPM metadata requires the fixed system pkexec tool used for Linux authorization.
Startup recovery and progress consumption run while the settings page is hidden as well;
opening settings is not a prerequisite for continuing an interrupted authorized update.

## Validation

Deterministic tests interrupt reception, extraction, durable preparation, high-water and
activation boundaries. They assert old-or-new complete activation, safe exact-version
resume, monotonic anti-rollback, bounded archive output and rejection of malformed paths,
links, special files and same-account mutation rights. Native CI runs real filesystem
admission and installer layouts on six RIDs, plus helper AOT smoke and Desktop trim/shell
checks. Native signing/notarization and physical power-loss testing remain separate external
acceptance; a green managed test is not evidence for either.
Installer smoke runs the installed helper's non-mutating `--validate-installation` mode,
which admits its exact fixed executable path and the entire real installation namespace.
