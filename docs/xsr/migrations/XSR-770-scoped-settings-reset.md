# XSR-770 Scoped reset of working settings

The existing Restore Defaults catalog action becomes available in Storage and
Migration; instance Game settings expose Restore Inheritance. Its description
explicitly limits the action to settings with working consumers. This is not a
factory reset of the launcher or a filesystem cleanup.

Settings owns a sealed reset-preview query and revision-checked reset command.
It captures one immutable settings revision, selects available catalog-backed
definitions and previews removing the current layer's overrides. Instance reset
is restricted to instance-eligible local overrides; global values, other instances,
reserved settings and unknown retained legacy data are not reset.

Desktop displays readable labels and a confirmation using the same operation
generation and scope-lifetime guards as import. Apply recomputes the proposal,
checks its revision and uses the existing atomic policy mutation transaction.
Invalid combinations and persistence failures cannot partially restore values.
No-change previews cause no writes. Mandatory telemetry channel policy remains
authoritative. Reset changes preferences only; it does not uninstall Java, delete
instances or remove snapshot files.

Contract regressions cover global/instance isolation, reserved-value preservation,
preview purity, restart persistence, no-op reset, stale revisions, rollback on save
failure and sealed Foundation routes. Desktop tests cover cancel, confirmation,
late callbacks and instance default inheritance. Architecture, NativeAOT and trim
gates continue to apply.
