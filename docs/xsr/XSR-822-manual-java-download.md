# XSR-822: confirmed manual Java download

Manual Java acquisition reuses MinecraftRuntime's existing Mojang metadata provider, durable
installer, task center, trusted managed root, runtime locator and registration store. Both the
global Java page and the instance's merged Game/Java page expose this shared managed acquisition.
Downloading does not set a global or instance Java preference. Instance changes cancel owned
requests and retire pending license dialogs before another instance can accept them. Instance
switches follow the management controller's existing reset to Overview; opening the new
instance's Game page creates a new download workspace, with no previous preview or acceptance.
The UI
admits one provider (Mojang distribution) and Java majors 8/17/21/25. It does not label downloaded
binaries as a vendor before probing their actual vendor/version/architecture.

A typed preview query resolves the actual platform manifest and captures a bounded immutable
plan, complete file/byte totals, target and supplied license-file HTTPS references. A short-lived
single-use preview token binds this plan, registry revision and selected major. Install is a
command and requires explicit acceptance of the accompanying runtime license. The installer
revalidates the complete captured plan fingerprint before downloading or publishing; metadata
replacement cannot silently replace the user's reviewed plan. There is no arbitrary URL, root,
component or executable argument admitted by this manual command.

A typed status query returns real progress and terminal receipt. The service probes the returned
owned executable through the existing bounded locator, verifies selected major and registers a
managed (Custom=false) runtime with optimistic registry revision. Installed-but-unverified and
installed-but-unregistered remain distinct from Installed. Cancellation affects this operation's
linked token, not concurrent launch acquisition; durable partial downloads remain recoverable,
and the UI states that explicitly. Leaving the page and disposal cancel owned requests and
retire dialogs/actions so late acceptance cannot start another operation.

Mojang legacy version labels such as 8u202 are parsed as Java 8 without inventing a
semantic version label. Linux ARM64 is explicitly unsupported because the published Linux
index is x64; a misleading native ARM64 preview is rejected before acquisition.

Admission retains at most eight previews and sixteen receipts, expires previews after ten
minutes, permits one manual install at a time, and bounds manifests/plans/licenses.
The concrete limits are 4 MiB per metadata body, 4096 files, 512 MiB per file (matching the
durable journal admission), 2 GiB total and eight license references. Rejected new plans do
not create recoverable intents. Source/status polling uses a separate lifetime token so a
user-canceled install can still display its terminal cancellation receipt.
Preview and status observations also capture the workspace generation and selected preview
identity. Changing the selected major or requesting a new preview retires and cancels the old
observations; a delayed receipt cannot attach to the replacement preview. These actions remain
unavailable while an installation owns its cancellation token or a license dialog is pending.
Retiring observations after an installation completes does not substitute or cancel another
installation's token. Status receipts must match the requested preview identity before their
progress or executable facts enter the scene.
UI fixed captions are localized; license/file URLs, target paths, actual vendor/version and raw provider
progress remain literal. Root adds the helper to the existing Minecraft composition and wires
separate query/command routers into the Settings Java page. Tests cover confirmation, plan
replacement, receipt facts/registration, finite inputs, cancellation and retired UI sources.
