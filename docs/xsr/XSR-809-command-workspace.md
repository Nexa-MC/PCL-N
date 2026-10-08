# XSR-809: bounded command workspace

## Architecture lock

Desktop owns a finite command catalog shared by command-line parsing and a UI.Next command
palette. Commands only navigate existing product pages: `launch`, `install`, `resources`,
`settings`, `tasks`, `java`, `storage`, and `about`. The latter three identify existing settings
sections. The composition root binds each admitted catalog entry to its existing controller.
They never launch Minecraft, execute a shell, mutate settings, or invoke a plugin automatically.

`--command=<id>` admits exactly one catalog entry; malformed, duplicate, and unknown command
options fail before UI startup. `--list-commands` prints the same finite catalog and exits before
starting native services. `--safe-mode` is an explicit process bootstrap policy owned by the
composition root: it suppresses optional Sidecar/custom media/native preference sessions while
retaining mandatory Java, file integrity, and account checks. It is not a persisted preference.
Single-instance activation preserves the admitted destination, including settings sections.
Safe mode acquires the normal exclusive bootstrap lease. If another process owns that lease,
it fails explicitly and asks the user to close that process before restarting in safe mode;
the flag is never discarded into an ordinary navigation activation, and two primaries do not
write the same settings or installation directory.
Unrelated existing bootstrap options remain available to their current consumers.

The palette is a modal UI.Next subtree, opened by Ctrl+Shift+P and closed by Escape or its close
button. It does not open over an existing modal decision. Search drafts are bounded at 128
characters and filter the eight entries by stable ID or localized label. Rows keep their entity
identity while filtering. Execution requires a live, visible, enabled row belonging to the
current exposure; stale rows and intents cannot navigate. Closing removes the subtree and
restores a still-live previous focus target. The native adapter translates only the two keyboard
gestures to explicit product callbacks and does not know product routes or renderer entities.

## Verification

Contract tests cover parser rejection, finite listing, safe mode admission, localized filtering,
live-row ownership, closure/disposal retirement, modal exclusion, focus restoration, and the
native keyboard gesture consumer. Root composition binds CLI and palette to the same controller
routes and runs the integrated build, architecture tests, and relevant product suites.
