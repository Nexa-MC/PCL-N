# XSR-833 — Windows decoration creation and tray restoration

2026-10-09. Fix the Windows duplicate rectangular caption shown above the rounded
product titlebar and the DWM outer frame returning after hiding/restoring to tray.
The existing compositor, native resize/minimize/maximize capabilities, retained
prepared shell and once-only entrance contracts remain authoritative.

## Cause and locked behavior

Avalonia 12.1.0 creates managed decorations when extended client area is enabled.
The former constructor added an implicit empty decoration resource afterwards;
the already-attached decoration could retain its cached Fluent theme, including
the fullscreen/minimize/maximize/close strip visible in the reported screenshot.
Assign the empty theme through `WindowDecorationsTheme` before enabling extension.
The explicit theme must contribute no overlay, underlay or fullscreen popover.
Keep `WindowDecorations.Full` and the native caption/resize capability styles.

Avalonia's Win32 show path calls `ExtendClientArea()` and re-enables non-client
rendering. A hidden normal window has no required state or size change on restore;
the old `_hasOpened` early return skipped the frame repair. Reapply the existing
chrome/frame policy on every native opening, after platform show and before public
opened observers. Keep only the startup entrance behind the once-only guard. The
existing coalesced render-priority repair remains in place for later platform frame
updates; it must retire after close rather than introducing a polling timer.

Do not remove native caption/thick-frame/minimize/maximize styles, disable DWM
composition/transitions, rebuild the retained window, or replay startup on restore.
Do not change macOS native traffic lights or Linux decoration selection.

## Regression and platform evidence

Extend the real Windows corner probe with the same Fluent theme used by production,
explicit managed-decoration checks before/after show, repeated hidden-normal and
hidden-maximized restoration through platform actions, and forced stale DWM policy
while hidden. Assert the same HWND, viewport/input origin, native capability styles,
suppressed non-client rendering and once-only entrance. DWM composition is mandatory;
border/corner and transition attributes are checked when their getters are supported.
Documented setter-only attributes with unsupported getters are reported as unverified,
without relaxing non-client rendering, composition or capability checks.

Run the affected backend/Desktop regressions, architecture/format checks and relevant
AOT/trim publish smokes. This cloud environment runs Linux and has no Windows desktop;
compilation and Linux/headless tests cannot establish Windows compositor acceptance.
The Windows-only probe runs as a dedicated `windows-native-chrome` CI job and retains
its output, without skipping mandatory checks or hiding its exit code. Report the
actual runner OS and result separately from physical Windows 11 visual acceptance.

Local Release builds completed with zero warnings/errors. Backend tests (28 cases
plus the native/headless lifetime scenarios), Desktop tests (252), architecture gates
(70 projects), real X11 startup and cancelled-startup probes passed. Linux x64
NativeAOT and full-link trimmed publishes passed their shell (53 semantic nodes) and
first-run setup smokes. Three existing CI-reported indentation failures in catalog,
resource download and telemetry code are corrected by expanding chained lock/if/foreach
statements into explicit blocks with the same lock scope and conditional behavior.
