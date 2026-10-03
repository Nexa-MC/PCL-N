# XSR-769 Settings import and export

The global Storage and Migration page exposes the existing sealed Settings export,
import-preview and import-apply routes. The two catalog action IDs remain stable;
their group moves from Advanced to Storage. Instance Game settings expose the same
actions with the current instance directory as a separate scope parameter.

The explicit Host file picker selects a JSON document or an export destination.
Host reads at most 1 MiB of actual bytes using strict UTF-8 (optional BOM accepted).
Exports stage bytes in the destination directory and atomically replace the chosen
file only after write/flush and a cancellation check. Failed/cancelled staging never
replaces the previous destination. These are user-selected document effects, not
automatic filesystem discovery or Service persistence in UI.Next.

Desktop does not parse or mutate the settings store. Import preview runs off the
render thread and lists readable setting labels before confirmation. Invalid scope,
local-only data, invalid values and unsupported schemas are rejected by Settings.
Apply revalidates the unchanged document and expected revision. Scope, navigation
and operation lifetime checks discard late results and stale confirmation callbacks.
Cancel/no-change/failed operations do not report successful imports.

Export includes only the existing exportable policy values; credentials, executable
paths, JVM/hook arguments and instance directory identities are excluded. Mandatory
test-channel telemetry policy remains authoritative after importing preferences.

Contract tests cover bounded actual reads, invalid encoding, atomic replacement and
cancellation; Desktop regressions cover preview/confirmation, cancellation, revision
conflicts, stale scope/navigation results and filtering through sealed Service routes.
The complete architecture, NativeAOT and trim gates continue to apply.
