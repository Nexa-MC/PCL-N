# XSR-731 Image residency budgets

## Contract

Resource icons retain encoded bytes in a 32 MiB LRU with at most 256 entries.
Successful hits refresh recency; admission evicts only as many old entries as required
by actual encoded length and entry count. A single image exceeding an internal test
budget may still be returned to its active page but is not retained by the cache.
Different URLs retain their provider identity, even when encoded bytes happen to match.
Cancellation applies to hits and asynchronous work. Disposal closes admission and
clears retained nodes/bytes; late HTTP/decode completions cannot repopulate the cache.
The four-pipeline bound and shared HTTP/CPU admission remain unchanged.

The Avalonia bridge decodes FitToBounds images to a power-of-two width determined by
visible DIP size and current RenderScaling. Width never exceeds the source or the
1024-pixel preview dimension limit. Same-bucket updates reuse the bitmap; drawing
rechecks density even when immutable scene equality skipped Apply. Image key, fit mode
and decode width are compared without concatenating strings. A read-only stream uses
the encoded carrier's existing array rather than making a second whole-image copy.
Layered skin rendering keeps original pixel coordinates and nearest-neighbor sampling.
Replacing or retiring a presentation disposes its bitmap.

This unit adds no assembly or public interface. Cache ownership is separate from active
page ownership. It does not implement or certify a global decoded CPU/GPU budget,
memory-pressure adapter, native texture residency, or process idle RAM targets.

## Validation

HTTP-backed regressions exercise byte and entry budgets independently, recency under
overflow, retained active image ownership, canceled cache hits, uncached oversized
images, and disposal with four active plus two queued requests. Real Skia-encoded
PNG/JPEG/WebP pixels exercise 1024-to-64 thumbnail decoding, same-bucket reuse, 2x DPI
upscaling to 128 and return to 64 through actual drawing without an intervening Apply.
Existing skin, media format, lifetime, transition and accessibility checks remain required.

Release builds completed with zero warnings/errors. Managed and Linux NativeAOT
Services passed 469 tests; Avalonia backend passed its 9 tests, including the real
PNG/JPEG/WebP/DPI assertions above; architecture checks passed all 68 projects and
changed-file whitespace verification passed. Headless drawing proves these contracts,
not physical OS/GPU frame or RAM acceptance.
