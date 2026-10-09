# XSR-834 — Tray motion and screenshot waterfall preview

2026-10-09 (Asia/Shanghai). User-requested continuation of Firefly window motion
and the selected instance's screenshot page.

## Locked behavior

Hide-to-tray and restore-from-tray each use a finite, interruptible transition on
the retained shell. Hide completes before native hiding; restore shows the same
prepared window and repairs its native chrome before presenting the transition.
Repeated commands retain the newest visibility target and continue from the
currently presented value. Restore during hiding must cancel the pending hide;
hide during restoration must not leave a visible transparent or stranded window.
Explicit shutdown, startup cancellation and window disposal retire their tracks.
The explicit reduced-motion preference immediately settles to the requested
visibility. Background/hidden scene-motion suspension must not accidentally disable
the restore transition. Startup entrance remains once-only.

Do not recreate the HWND, change retained viewport/input coordinates, introduce a
layered Win32 window, or undo XSR-833's empty managed decorations and per-show DWM
repair. Keep normal/maximized restoration and no-tray fallback behavior. Native
window capabilities and compositor transitions remain enabled.

The instance screenshot gallery uses responsive waterfall columns and each image's
actual aspect ratio. Use bounded metadata and visible-card realization instead of
creating controls for every screenshot. Preserve filtering, refresh, scrolling,
date grouping in timeline mode, and placeholder behavior for unreadable images.
The content entry may carry optional `ImageWidth`/`ImageHeight` facts derived from
an admitted bounded PNG header read, separately from the full preview-byte budget.
Validate the PNG header and supported dimension bounds, retain its file identity,
and preserve the existing entry/scan limits. Missing or changed header facts remain
unknown rather than invented dimensions. These facts are reusable presentation data;
Desktop must not open files to obtain them. Visible images may use the existing typed
screenshot read route with bounded concurrency, an owned byte budget and retirement.
The lazy-thumbnail budget is 64 MiB across distinct encoded carriers retained by
the cache and realized cards, plus expected-size reservations for at most two
pending reads. Cache eviction must not discard the accounting for a still-visible
card. Borrowed snapshot icons retain the service's existing separate scan budget;
one enlarged original is bounded by the existing 16 MiB file limit. This is not
a cap on decoded textures or immutable frames retained briefly by the renderer.

Selecting a screenshot opens a window-local enlarged image preview with operation
buttons, rather than navigating to the generic content detail page. Preserve the
gallery and its scroll position beneath the preview. Provide close/Escape and the
existing copy, share, folder, removal and crop workflows through the same admitted
typed routes. Copy/share read the original screenshot using its file identity;
cropping creates a new file and removal retains confirmation/recovery semantics.
Native file sharing rechecks the selected size/time identity after asynchronous
storage lookup and before putting the file reference on the clipboard. This does
not promise that an externally editable file reference is immutable afterwards.
Preview callbacks retire when the page, instance or selected file identity changes;
stale reads cannot populate a later selection. Keep modal focus and input isolation.

## Validation

Focused regressions cover in-progress tray visibility, reversals, reduced motion,
same-window restoration and shutdown; gallery layout covers mixed aspect ratios,
responsive columns and bounded realization. Preview checks cover operation routes,
modal closing, unchanged gallery scroll and retirement. Run affected renderer,
backend/Desktop tests, architecture and format checks, plus relevant AOT/full-link
trimmed publish smokes. Windows native CI verifies that repeated restores retain
suppressed DWM decoration and native capability styles; Linux evidence is reported
separately from Windows desktop acceptance.

## Executed evidence

- Release Desktop build: no warnings or errors; all 259 composition regressions
  passed, including seven new layout/preview/thumbnail regressions.
- Backend: all 29 regressions passed; native Linux startup and cancellation probes
  passed. Service screenshot/content-metadata regressions: five passed.
- Architecture: 70 projects passed; solution IDE0055/IMPORTS formatting passed.
- Linux x64 NativeAOT and full-link trimmed Desktop publishes completed without
  warnings; both executables passed shell (53 semantic nodes) and first-run smokes
  using separate disposable data directories.
- Actual light/dark X11 recordings passed enlarged preview, visible crop fields,
  retained scroll and same-HWND animated hide/restore checks. The selected PNG/GIF
  evidence and controlled-fixture boundaries are in
  [the preview index](../previews/firefly-alpha.6-tray-gallery/README.md).
- The Windows native CI probe now requires intermediate tray opacity, both
  interruption directions and restoration while optional scene motion is paused,
  alongside XSR-833's actual HWND/style/DWM/viewport checks. Its per-run logs remain
  the Windows evidence; the Linux recordings are not Windows appearance evidence.
