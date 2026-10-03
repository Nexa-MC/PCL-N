# XSR-773 — Settings controls and version forms

## Locked contract

Version settings keep the existing Service-owned page availability, root-qualified instance
identity, sealed queries/commands, validation, and transactional content operations. Legacy
`PageInstanceSetupRight`, resource, server, export and tools views are inspected read-only for
behavior and grouping; their implementation and assembly graph are not imported.

UI.Next gains additive `Switch`, `CheckBox`, `RadioGroup` and `RadioButton` semantic roles,
and an `XsrUiToggle` component projected as nullable `IsChecked` in the immutable scene.
The native backend paints switches/check boxes and exposes toggle/selection automation
patterns. Activation always emits an intent; the native backend never mutates setting state.
`XsrUiSelectionGroup` projects whether a radio group requires a selection. Runtime inventory
allows none when automatic Java selection is active; ordinary exclusive preferences and
primary navigation require a selected item.
Disabled controls cannot emit an intent. Switch presentation follows confirmed state, uses
interruptible motion, and honors reduced motion. Exclusive choices retain the existing
draggable, horizontally scrollable segmented track rather than independent action buttons.

Boolean settings use draggable On/Off radio groups, with labels 开启 / 关闭;
exclusive settings and content filters also use radio groups;
export categories use check boxes. Commands such as apply, delete, restore and export remain
buttons. Instance game settings are grouped into launch/window, Java/memory, connection,
and advanced arguments with compact inset forms. Management pages share consistent inset
spacing, fact alignment, toolbars, and content/action separation. Conditional content pages
continue to originate in the management Service. No unavailable legacy setting is made
available by this presentation migration.

The installed-runtime chooser also appears inside instance Java/memory settings, using
the existing sealed Java inventory query. Choosing a runtime writes only the current
instance override through settings policy. Global preferences and launch compatibility
validation remain authoritative. Async results replace only the inventory subtree.

## Verification

Renderer scene/keyboard tests, native toggle and radio automation tests, Desktop scoped
setting and export/detail control tests, full relevant suites, architecture checks, formatting,
Desktop shell validation, and exact-head CI NativeAOT/trim execution are required.
