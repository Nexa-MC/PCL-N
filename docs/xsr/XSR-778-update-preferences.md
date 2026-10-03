# XSR-778 — Update preferences

Settings owns the durable global `updates.channel` and `updates.auto-check` policy.
The default channel follows the immutable build identity; explicit existing Alpha,
Beta, Stable and CI choices are retained. CI follows the Alpha release feed, as does
the existing CI build identity. A preference never changes the running build's
diagnostic/rollout identity or enables a legacy 1.x update route.

Desktop reads the sealed effective-settings query asynchronously even when Settings
is closed. It checks once at session startup when enabled; manual checks remain
available when disabled. Channel changes retire/cancel pending discovery and clear
the old offer. A completed offer carries the channel captured for its discovery.
An active or recoverable automatic-update transaction retains its original channel,
regardless of subsequent preference changes. Downloads, signature checks, protected
activation, high-water checks and rollback keep their existing Service/Host paths.

Legacy automatic-update modes are migrated only as a check preference: mode 3 means
manual checks, modes 0–2 allow the startup check. Nexa never inherits a legacy
permission to install without confirmation. Existing stored layered channel choices
remain authoritative; the previous built-in Alpha default becomes follow-build.

Regression coverage: durable/default/legacy resolution, disabled startup checks,
startup without visiting Settings, channel-switch late responses and preserved
transaction channel. Validate Desktop shell, architecture, NativeAOT and trimming.
