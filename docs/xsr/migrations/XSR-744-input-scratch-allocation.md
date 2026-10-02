# XSR-744 Steady pointer input allocation

## Renderer-local work

Unchanged hover and native cursor queries reuse renderer-local hit-test scratch storage.
It contains only entity indexes, is cleared before every independent query, and never
crosses threads or changes pointer ordering, clipping or ancestor input resolution. A scene
rebuild clears it and trims oversized capacity against the newly materialized scene, so
visiting a large page cannot permanently retain its largest hit-test buffer.

Structural input validation scans live siblings in attach order without constructing child
or filtered-visibility arrays. Pager indexes count visible children; the last visible modal
overlay continues to block earlier siblings, including before the next scene render.
Existing public child snapshots keep their isolation semantics. Internal indexed child
reads are UI-thread-only and do not authorize mutation during a traversal.

Inactive segmented gestures return before entering the captured-query implementation.
Keeping its closure in the ordinary pointer method previously allocated even when no drag
was active. Active drag behavior remains unchanged and is outside this unit's zero-allocation
claim.

No service, IPC, assembly boundary or public API is added. This unit covers warm, unchanged
pointer queries, not all animation, scroll, focus, scene publication or native backend work.

## Acceptance

First reproduce allocations with the previous implementation. After initialization/warmup,
10,000 hover/cursor pairs over a nested hover-expand control, 10,000 stable edge pairs and
1,000 background hit-query pairs
must allocate zero bytes on the calling thread, keep the expected cursor, and leave the
tree clean. Regressions continue to cover enabled/visible targets, recycled handles,
modal barriers, inactive pager pages, clipped input and keyboard navigation.

Run managed and Linux NativeAOT renderer/Desktop suites, architecture, whitespace and
independent product AOT/trim validation. These deterministic checks do not certify physical
input latency, frame P95/P99, process-wide allocation or idle CPU/RAM.

## Execution evidence

The regression first ran against the unchanged renderer and reproduced these calling-thread
allocations after warmup:

| Unchanged query pairs | Previous implementation | Optimized managed implementation |
| --- | ---: | ---: |
| 10,000 nested hover/cursor | 2,480,000 B | 0 B |
| 10,000 stable capsule-edge hover/cursor | 36,720,000 B | 0 B |
| 1,000 background hover/cursor | 1,240,000 B | 0 B |

The intermediate scratch/sibling optimization still allocated 40 B per pair through the
inactive segmented-gesture closure. Moving closure creation behind the active-gesture guard
removed that residual. Managed renderer passes 92 cases, including unchanged tree state,
recycled handles, hidden pager siblings and newly staged stacked modals.
Linux NativeAOT renderer passes the same 92 cases and all three zero-allocation assertions.
The kernel benchmark's deterministic clean-render/dirty-layout/scene gates also pass;
its development-machine timing report is informational.

Managed and Linux NativeAOT Desktop each pass 110 cases; Avalonia backend passes 9.
The 69-project architecture and included-source whitespace checks pass. Release solution
and native test builds with .NET SDK 10.0.100 have no warning/error diagnostics.

Independent NativeAOT and linked trimmed products pass the 52-node shell and first-run
validation. Both product outputs and the native Desktop-test output contain no
Patch.Compiler/Roslyn files. Product publications have no warning/error diagnostics.
These headless checks do not close real OS/GPU interaction or eight-hour Minecraft acceptance.
