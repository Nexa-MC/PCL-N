# XSR-737 Shared dynamic raster budget

## Contract

One process-wide backend pool owns dynamic raster bitmaps. The typed key is encoded
identity, fit mode and decode width. Current and outgoing controls own leases, so the
same decoded image is shared without disposing pixels still used by another control.
No new assembly, public service dependency, worker, polling timer or Sidecar call is added.

The pool admits at most 512 entries and 64 MiB of pixel charge, including pinned images.
Its explicit accounting unit is eight bytes per pixel. Admission reserves a conservative
target rectangle before decoding, validates decoded dimensions and retains the actual
rectangle's charge. Only unleased entries are in the idle LRU; overflow disposes those
bitmaps, while a full pinned budget returns the existing image placeholder. Encoded
carriers are read without copying. Malformed decode does not poison retained accounting.

Completely clipped/off-viewport, zero-sized and zero-opacity nodes release leases and
do not decode. Changes, retirement, outgoing-layer completion and surface disposal
release ownership; disposal also trims all idle entries. Budget rejection may retry after
capacity changes during an already requested draw/commit, never by scheduling a frame.
Malformed decode is not retried until source/fit/width changes. Released controls cannot
reacquire through a late draw. Stable same-bucket draws do not allocate a new lease.
Native window minimization disables raster residency on its surface and trims idle
entries without disturbing another surface's live leases. Scene commits and late draws
while minimized cannot decode. Restoration requests a native redraw of the current image;
it does not suppress correctness-critical state observation or initialize stale images.

This is a dynamic bitmap ownership/accounting bound, not proof of physical CPU/native,
GPU texture or total process RAM. Decoder temporaries, compositor references and fixed
embedded assets are outside it. OS memory-pressure integration and physical long-run
acceptance remain open.

## Validation

Use real encoded pixels and the existing Headless/Skia backend. Exercise shared leases,
independent byte/count budgets, pinned overflow, LRU recency, released-control drawing,
clip/viewport/opacity changes, normal retry after capacity release, malformed decode,
and surface disposal. Preserve original skin sampling and the PNG/JPEG/WebP/DPI tests.
Run managed tests, architecture checks and NativeAOT product shell/trim validation.

Managed and Linux NativeAOT backend suites passed all 9 cases, including the real
PNG/JPEG/WebP/DPI scenarios and new sharing, independent budgets, LRU disposal, clipped
and off-viewport release, malformed decode suppression, capacity retry and minimized-window
restoration assertions. Direct RenderTargetBitmap checks explicitly arrange newly created
controls so a zero-size visual cannot masquerade as successful decode suppression.
69-project architecture and changed-file whitespace checks passed. NativeAOT backend and
product publishes emitted no warning/error diagnostics; the fresh product validated 52
shell semantic nodes and first-run validation exited 0. Neither fresh output ships the
compiler or Roslyn. These Headless/composition checks do not certify physical RAM/GPU,
OS pressure behavior or an eight-hour native-window run.
