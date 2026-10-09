# XSR-832 — Retire launcher low-power mode and publish previews

2026-10-09. The user explicitly removes launcher low-power mode. This supersedes
XSR-776 and the low-power clauses of XSR-830. Remove the settings catalog/policy/default
entry and its settings-page description, presentation frame-rate consumer and startup
appearance projection. Old persisted `UiUltraLowPowerMode` or `appearance.low-power`
facts are inert; they must not change frame rate or startup motion and must not cause an
unrelated settings load to fail. No automatic rewrite/deletion of unrelated user data.

Reduced-motion preference and user-selected animation frame rate remain authoritative.
Native/system power capability reporting is unrelated to this retired launcher setting
and retains its existing contract. The new, unpublished startup appearance entry point
contains theme, reduced motion and product version only.

Produce inspectable static previews for the requested page scope, plus GIF recordings of
dynamic content. Previews must use the actual renderer/native window and identify controlled
fixture data when it supplies resources or states. They are not mocked replacement UI or
proof of live provider/network behavior. Give the user a browsable index and direct artifacts.
Use the existing software-rendering preference for Xvfb capture where necessary. Native
layering/focus observations do not establish physical compositor behavior on other systems.

## Resource native capture workflow

The independent `Nexa.Desktop.Tests --native-resource-preview light|dark <outputDirectory>`
entry composes actual retained resource controllers and a real native window with committed
Chinese-language/theme settings. Controlled typed catalog data provides three batches and
two detail versions; project descriptions/detail notices explicitly identify preview data.
It does not claim live provider responses or downloaded files. The existing native smoke
and ordinary managed regressions remain unchanged.

Native wheel events drive automatic batch admission; routed pointer events drive retry,
details and back navigation. An external capture runner receives atomic TSV markers with
window screen coordinates for `list-initial`, `append-loading`, `appended`, `next-loading`,
`failed`, `retry-loading`, `retry-success`, `detail` and `back`. The runner records real
X11 frames and saves each PNG before acknowledging its barrier. The fixed window remains
visible throughout each theme's GIF recording. Display/capture dependencies are explicit;
no mocked image generation or production screenshot APIs are introduced.

The self-contained runner is `eng/previews/capture_resource_previews.py`; its `--display`
option lets resource capture use an isolated Xvfb display. The GIF omits the loop extension
and plays once; the aggregate preview index owns explicit replay/download controls.

Real GIF capture exposed a first-show ordering defect: hidden preparation publishes an
inactive window, which suspends optional renderer motion before `OnOpened`. The finite native
entrance must use the explicit reduced-motion preference and startup snapshot, rather than
this pre-focus suspension. The startup trace is bounded to 420 ms; it does not reactivate
continuous scene/media clocks. Real foreground focus is supplied by the isolated X11 capture
probe only after the main window maps, to model a window manager before recording close.
The Splash remains passive throughout loading.

## Completed preview capture — 2026-10-09

The current-round gallery includes 30 actual native PNGs and 10 single-play GIFs,
in both Light and Dark: the Splash and initialized shell, Splash appearance,
window entrance/close, nine resource-list/detail/back states, and four settings
states including a real theme change. Resource recordings cover 20 → 40 → 60
retained results, pending append, failure, explicit retry, details and return.
The resources use explicitly labelled controlled typed data, not provider HTTP.
Settings captures show the retained frame-rate/reduced-motion controls and the
complete appearance-page bottom, with no retired launcher low-power entry.

Native captures use separate Xvfb displays and private DBus sessions. The existing
software-rendering preference is enabled; the managerless capture fixture disables
tray integration because it has no StatusNotifier watcher. This does not change the
product's rendering/tray defaults. Settings capture moves the existing native client
inside its 1280×800 capture screen when no primary working area is available, and
excludes the pointer and empty-screen frames after native close. Its PNGs are full
1040×740 X11 captures; attached-live-visual snapshots are distinct diagnostic files.

`eng/previews/` contains reusable capture and gallery scripts. The offline HTML
gallery embeds the selected PNG/GIF bytes, supports explicit GIF replay and individual
downloads, and the ZIP includes selected assets and capture receipts. Raw frames,
video intermediates, fixture user roots, logs and failed recordings are excluded.
Each GIF contains distinct actual frames and omits an infinite-loop extension.
Physical desktop/compositor acceptance remains the platform scope described above.

Chromium 151 parses the exact self-contained HTML without HTTP requests or page
errors. Theme/category filtering, new decoder URLs for GIF replay, byte-identical
PNG download, and zoom/close interactions pass. This cloud Chromium's managed policy
blocks file-URL navigation, so the browser check loads the HTML bytes with Playwright
`set_content`; no browser policy is altered and no file-origin navigation is claimed.

## Final runtime validation

Release Desktop tests pass 252/252 and Avalonia backend tests pass 28/28; Services
passes all 713 cases including retirement/preservation of the old settings. The
architecture checks pass for 70 projects. The final Desktop NativeAOT test executable
also passes 252/252 and its independent native resource-scrolling probe, including
the exact failed-page retry and stable entity/offset assertions.

The final Linux x64 NativeAOT and full-link-trim Desktop publishes complete without
compiler/linker warnings or errors, and both pass shell/setup validation. A separate
real NativeAOT run verifies initialized-window presentation, user-level `nexacl://`
registration, secondary-instance forwarding, safe-mode shared-root rejection and
clean close. Another verifies prepared first-run wizard handoff and close without
persisting incomplete setup. The isolated startup probes verify prolonged topmost
loading/retry, passive appearance, cancellation and hidden-window/raster retirement.
