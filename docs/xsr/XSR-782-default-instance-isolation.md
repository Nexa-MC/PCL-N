# XSR-782 — New-instance isolation policy

The global default is all versions, preserving the existing XSR metadata default
and legacy LaunchArgumentIndieV2=4. The other modes are none, loaders only,
non-release only and loaders or non-release. Legacy indices 0–4 retain their order.
This default applies only to new ordinary installations, not existing instance
launches, imports, edits or modpack installations. Those retain their own metadata
and directory contract; this slice never moves saves, mods or other user data.

Services captures the validated mode before creating the durable install journal.
Recovery reuses that captured mode even after settings change. Old authenticated
journals without the new field keep the previous all-version default. The trusted
vanilla manifest supplies release classification; filenames are not interpreted
as a release fact. Unknown classification in a mode requiring it rejects the
installation rather than making an undocumented guess.

For new instances, isolation metadata and managed addon paths are generated in
staging before the existing publication transaction. Isolated addons land in the
instance mods directory; shared addons land in the root mods directory. Receipts
record that same path, so subsequent edit/removal and launch use one identity.
Pack and edit commands keep their explicit mods paths and metadata. Existing
publication conflict checks and rollback/recovery authority remain mandatory.
Edits read explicit isolation metadata even when the mods directory has not yet
been created. Older metadata-free instances retain the previous directory fallback.
The publication allowlist admits only the selected instance's exact metadata
filename, never arbitrary files under its configuration directory. New installs
reject existing instance metadata instead of overwriting orphaned user settings.
Incidental parent manifests retain their existing directory contract; the default
applies to the selected instance, not an existing or reused inheritance parent.

Tests cover the five modes, legacy and restart semantics, release classification,
isolated/shared addon destinations, journal capture/recovery and unchanged edit
metadata. UI uses the existing content-sized draggable radio selector.
