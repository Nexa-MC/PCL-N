# XSR-772 Java inventory in settings

Global Java settings list discovered runtimes with their version, distribution,
architecture, availability and executable path. Select updates the existing global
java.runtime preference; it does not bypass the launch compatibility gate. Scan
explicitly invalidates the existing locator cache before discovery. Entering the
page queries current discovery without invalidating its cache. No second runtime
registry, recursive Desktop scan or local filesystem write is introduced.

Java owns a sealed inventory query and immutable read-only report. Foundation
composition supplies its existing locator. Locator work runs outside the render
thread; this read publishes no machine/settings revision. Leaving the page retires
pending results and cancellation, including stale results from noncooperative
locators. Failures show readable state and can be retried without reopening the app.
Unavailable or disabled candidates are visible but cannot be selected. UI path text
is literal and selection is root-qualified by its full executable path.

Tests verify worker execution, refresh invalidation, cancellation, candidate
deduplication, deterministic ordering, read-only snapshots, sealed routes and
selection persistence. Async completion updates only the runtime-list subtree;
form entity identity, text drafts, focus and selector scrolling remain stable.
Runtime deletion, custom-runtime management and additional
runtime acquisition actions remain reserved until their own consumers exist.
Architecture and NativeAOT/trim gates continue to apply.
