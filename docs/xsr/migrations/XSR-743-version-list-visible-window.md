# XSR-743 Installed version list visible window

## Projection boundary

The installed-version selector retains the immutable logical filtered list while materializing
only a viewport-sized row window with overscan. Source/filter changes may rebuild logical
indexes; unchanged frames and scrolling must not enumerate or create every installed version.
Rows retain the existing 72-pixel height and 8-pixel spacing through exact logical spacers.
Overlapping rows keep entity handles and input state. A focused row may remain pinned outside
the visible window, bounded to one extra row, so wheel scrolling cannot silently retire focus.

Selection, transfer ranges and drag paths are defined by the complete filtered order, not by
currently materialized rows. Filtering still removes hidden transfer selections; directory
qualification and running-instance Move restrictions remain intact. Each newly keyboard-focused
row is brought into view before updating the window, allowing Tab traversal across windows;
unchanged focus does not force wheel scrolling back to an earlier row. No renderer/Services
dependency or assembly boundary is added.

The renderer adds an advisory `TryGetScrollSnapshot` query for the last produced scene. It
returns the existing immutable scroll value, rejects dead/non-scroll entities, and does not
perform layout or input authorization. The controller uses its viewport height when present
and a conservative initial window before that page has a scene.

An optional host-local `XsrUiFocusNavigation` component resolves logical next/previous focus
within a virtualized collection. The renderer only invokes it for a live, structurally visible
focused origin, then validates its returned target through the normal enabled/visible focus
path. An unassigned or rejected target falls back to scene order. The callback may materialize
an adjacent row on the UI thread; it must not perform service calls, I/O or rendering. This
does not expose clipped rows to pointer input or accessibility and does not bypass modal or
inactive-pager barriers. The version list uses it for row/action traversal in both directions.

## Acceptance

Regressions use 10,000 logical instances and verify bounded row/entity counts, end/middle
scrolling, exact content extent, stable overlapping handles, search, transfer ranges across
unmaterialized rows, running-instance drag restrictions and keyboard traversal. Existing
directory, selection, layout and filter regressions remain required. Managed/NativeAOT Desktop,
architecture, whitespace and independent product shell/trim checks close the implementation
unit; they do not certify physical OS/GPU frame tails or a 10,000-directory workload.

## Execution evidence

The pre-change regression materialized 1,000 rows for 1,000 logical instances and failed
the bounded-window assertion. The implementation passes both the 1,000- and 10,000-instance
fixtures, including middle/end extent, overlap identity, unmaterialized range selection,
running-instance Move restrictions, filtering, 100 forward/backward Tab transitions, wheel
focus retention and viewport resize. Managed and Linux NativeAOT Desktop each pass 110
cases; managed and Linux NativeAOT renderer each pass 90. The 69-project architecture and
included-source whitespace checks pass. This is deterministic composition coverage, not
physical frame-time, OS accessibility or a large filesystem scan result.

All compile/publish checks have no warning/error diagnostics. The independently published
NativeAOT product passes its 52-node shell and first-run checks; product and Desktop-test
outputs contain no Patch.Compiler/Roslyn files. No new project or dependency was added.
