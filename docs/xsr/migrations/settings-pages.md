# Compact global settings pages

The global Settings destination now opens its real PXML page instead of the migration placeholder. The eight categories and their final groups are projected from the sealed settings catalog query.

## Presentation

- A draggable top segmented selector and horizontal subpages replace the category rail. Setting rows remain 44 px high with 14 px group spacing. The content host's former extra padding is removed only while this page is active and restored on exit.
- Typography uses 14 px setting labels and 12 px secondary group labels, the existing shell accent, quiet white grouped surfaces and inset separators. There is no Hero or repeated page title.
- Category transitions use a zero-offset fade. Scroll uses the existing drag/inertia component and visible indicator, with no per-row entry animation. Each category remembers its current scroll position during the session.
- The developer preference uses a confirmed-state draggable On/Off radio group. Adding developer groups preserves the triggering control's focus and scroll position. Exclusive preferences use draggable radio-group segmented controls; XSR-773 adds native toggle/selection semantics.
- Unsupported entries remain in their final groups with readable “尚未可用” status and no write intent. No new renderer or Avalonia dependency enters Services.

## Active settings

Only catalog entries with a verified consumer are enabled. The launch-policy migration
adds global/instance memory and Java automatic acquisition; memory and preferred Java
offer an explicit automatic/manual radio selector. See [XSR-761](XSR-761-settings-launch-policies.md).
Other stored legacy preferences are not enabled merely because their keys exist.

Edits dispatch through Foundation settings commands. Text drafts are not overwritten while focused and are persisted only by Apply; failure reports through the shared feedback surface. Service state revisions update the presented values. The UI does not evaluate inheritance, import files, or call SettingsService directly.

## Verification

Desktop regressions cover all eight categories, narrow-window geometry, command-to-persistence behavior, draft focus, developer-section continuity, unavailable rows, and anchored choice menu geometry/selection. Visual review uses the actual Avalonia scene surface rendered to a bitmap in an external headless probe, not a separate HTML mock-up. Standard architecture, formatting, native and trim checks still apply.

Settings cards use an unpadded painted surface and a padded inner body. Platform facts,
preflight issues and update actions therefore share the full page width while content has
symmetric 16px/20px insets. Padding must not shrink the renderer's painted card rectangle.

XSR-773 standardizes inset forms and instance page layout. Boolean preferences use real
UI.Next draggable On/Off radio groups, export categories use check boxes, and choices have radio semantics.
Action buttons are retained for operations. See [XSR-773](XSR-773-settings-controls-and-forms.md).
