# XSR-830 — Theme-aware neon identity and calm startup

2026-10-09. The two user-supplied transparent neon N SVGs replace the product icon.
The artwork is an input asset, not an instruction source. Its original paths, gradients,
transparency and filters remain intact in the retained SVG sources. Offline raster exports
provide bounded, immediately decodable native PNG/ICO assets; startup does not parse or
animate the thousand-path artwork or introduce an SVG runtime dependency.

Launcher low-power mode is retired by [XSR-832](XSR-832-retire-low-power-previews.md);
reduced motion remains the optional-animation preference.

## Theme and asset ownership

Desktop owns the supplied artwork and package icon. The Avalonia edge consumes its embedded
light/dark image streams without introducing a service-to-Desktop reference. Explicit Light
or Dark settings select the matching image; System follows the actual native theme. Window,
taskbar, tray and startup decoration share that selection. Native icon updates also refresh
an existing tray icon. Shell-close decoration uses the current artwork, not a captured
previous theme. Native bitmap ownership is bounded and cleaned up with its window/session.

The early startup appearance reads the existing bounded bootstrap settings representation,
including theme and reduced-motion preferences. It does not compose services or
wait for online work. Missing/corrupt settings retain System/default fallbacks. Recovered
authoritative settings replace the bootstrap projection before the prepared shell is shown.
Existing public startup/lifetime entry points keep their CLR signatures; any appearance
entry point is additive and named explicitly. No settings schema or product version changes.

## Long-lived native Splash

The native startup window remains topmost for the entire finite readiness/retry interval.
It is a compact frameless card with a crisp neon N, NexaCL identity, Firefly Alpha version,
a restrained mint/cyan accent and the current real initialization stage. It stays readable
on both themes. A fixed indeterminate decoration makes no completion-percentage claim.

The Splash does not periodically activate itself, steal focus when a stage changes, expand
across the screen, spin/flash the logo or repeatedly play an entrance. Its initial appearance
is finite; prolonged loading has no continuously scheduled decorative animation. Dragging,
cancel/close, keyboard escape, localized failures and retry remain available. Reduced motion suppresses optional startup motion. The window and any owned animation are
disposed on cancellation, terminal failure or handoff; retry preserves single-instance
ownership and does not reveal an abandoned prepared shell.

## Prepared-window entrance

The first visible product shell reuses the actual hidden, warmed window and its committed
scene. One brief entrance takes its visual cues from the N's diagonal geometry and cyan/mint
light, then removes every overlay/clip/transform. It begins only when the first scene exists;
no page navigation, resize, minimize/restore or tray restore restarts it. Input remains
available and the native window size/position is not animated. Reduced motion presents the
final scene directly. The finite first-show decoration uses the explicit reduced-motion
preference, rather than the renderer's optional-motion suspension caused by the warmed
window being inactive before native focus. Its first entrance must not be consumed during
hidden preparation or skipped before the window manager can grant focus. Other scene/media
activity policies and inactive-window close suppression retain their contracts. Closing during entrance cancels its tracks before the existing close
sequence, and native destruction releases all tracks. The topmost startup window relinquishes
ownership at the prepared-window handoff and cannot remain above the running launcher.

## Verification

Cover explicit/system theme selection, existing tray updates, finite/once-only motion,
reduced-motion final state, resize and mid-entrance close, startup stage/failure/retry/cancel,
hidden preparation and native lifetime handoff. Inspect both themes visually using the
actual native window. Run affected Desktop/backend tests, architecture checks and relevant
NativeAOT/trim publish smoke checks. Physical compositor results on other operating systems
remain platform CI/runtime targets rather than claims inferred from an X11 screenshot.

## Completed verification — 2026-10-09

.NET 10.0.100, Linux x64: the Release solution build has zero warnings/errors.
Desktop's 252 cases, UI.Next's 105, PXML's 40, the Avalonia backend's 28 and the
architecture checks for 70 projects pass. Startup preference tests use real settings
persistence and verify invalid/missing inputs remain read-only. Actual light/dark
512-pixel artwork, transparent corners, committed palette selection, same-tray icon
replacement and disposal are exercised by the backend lifetime suite.

Separate native startup processes pass `--native-startup-smoke` and
`--native-startup-cancel-smoke`. They retain the same topmost startup window beyond
two seconds and through failure/retry, retire only its decorative motion while an
unrelated owner remains active, reuse the warmed hidden shell and release raster leases
on Escape. Failure/retry assertions wait for actual text changes; a normal-priority
UI invocation is not used as a completion barrier for posted status updates. The entrance
scenario routes pointer/keyboard input while its trace is present, resizes, restores,
reduces motion, rejects/accepts close and observes final clock cleanup.

The Desktop NativeAOT and full-trim self-contained publishes pass both
`--validate-shell` (53 semantic nodes) and `--validate-setup`. The actual NativeAOT
launcher was also opened in separate persisted Light/Dark roots under Xvfb/X11 and a
private DBus session. Both 400×280 Splash cards and 850×500 initialized windows have
visible, inspected pixels with the corresponding artwork/palette. The Splash advertises
ABOVE and `_NET_WM_USER_TIME=0`; 84/85 loading-time focus samples keep the probe's prior
focus. Handoff removes the startup window and ordinary close exits 0. A separate run
checks actual secondary-instance activation, user-level protocol registration and first-run
wizard handoff/clean close without persisting incomplete setup.

The Xvfb GPU/Mesa route produced black frames (`Failed to attach to x11 shm`), so pixel
acceptance uses the existing `SystemDisableHardwareAcceleration=true` setting in isolated
test roots. Xvfb has no EWMH window manager: ABOVE and passive core-focus observations
are evidence about native requests, not proof of physical compositor stacking or another
operating system's activation policy. No extended physical-desktop soak is claimed.
