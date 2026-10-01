# XSR-740 Resource page image ownership

## Contract

The resource controller owns only presentation descriptors (entity, URL and owning page),
and continues to obtain encoded images through the existing sealed Service query. It
does not own the Service cache, perform HTTP or introduce a renderer/service dependency.

The active resource page may retain its current encoded icons. On leaving that page,
the controller cancels icon reads, forgets their pending results and releases its raster
carriers. Inactive list/detail pages retain captions, navigation, search drafts and icon
placeholders, rather than pinning the Service cache's evicted images. Returning requests
only that page's existing descriptors; icon completion never rebuilds the search or rows.
Destroyed rows remove their descriptors. Controller disposal cancels and releases both
pages. A canceled generation cannot publish an image or wake the UI when it completes.

Admission, byte validation and encoded-cache LRU remain Service-owned. Normal frame
preparation handles navigation changes without timers/polling or a new assembly. This
unit bounds ownership by the existing search/detail page limits; it does not implement
viewport-driven network demand, OS memory pressure or certify native RAM/GPU budgets.

## Validation plan

Exercise list/detail switching, full navigation exit and return with preserved entities
and search draft. Verify canceled late reads cannot repopulate either hidden or current
icons, returning requests the current page only, removed rows leave no descriptors, and
dispose releases raster references. Run Desktop managed/NativeAOT, architecture,
whitespace and product trim/shell checks.

## Completed local validation

Release and Linux NativeAOT Desktop each passed 109 cases; compilation/publish had no
warnings or errors. The lifecycle case checks real controller/query paths for list/detail
switching, preserved draft/entities, canceled late results, only-current-page reads,
destroyed-row replacement and idempotent disposal with raster release. The 69-project
architecture and folder whitespace checks passed. An independent product NativeAOT
publish passed the 52-node shell and first-run checks, with no Patch.Compiler/Roslyn in
either installation output. This is composition/trim evidence, not a physical memory,
native-window, GPU-pressure or eight-hour performance acceptance result.
