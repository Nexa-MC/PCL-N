# XSR-794 — Developer diagnostics

The advanced settings page exposes two existing developer-only IA positions as
read-only facts: `global.advanced.8d723d8dcdfb` (Renderer Diagnostics) and
`global.advanced.01c0534c8b37` (XSR State Inspector). They are `State` catalog
entries, not editable settings. No persisted value, settings mutation, additional
catalog position, platform effect or network request is introduced.

## Capture and presentation contract

The Desktop host captures the existing renderer's scene version, last layout
visit count, tree entity count, confirmed color scheme and effective motion
policy when the developer cards become visible. Their refresh button captures
the same fields again. Scene version and layout visits describe the previously
completed frame; tree count, color scheme and motion describe the host at capture
time. Building the cards must not render recursively to obtain another frame's
metrics.

The state inspector calls `XsrStateStore.CaptureSnapshot()` on first display,
when returning to the advanced page and on explicit refresh. It immediately
projects only semantic ID, owner, state kind, revision and availability into a
separate metadata snapshot, ordered by semantic ID. The existing store capture
may flush deferred coalesced publications or derive state, and its temporary raw
snapshot contains payload references or boxed values. The inspector does not
access, stringify, display or export an entry's `Value`, and does not retain the
raw snapshot after projection. Its retained metadata rows contain no payload
references. No state payload, CLR payload type, credential, account detail or
local path is added to the presentation. Snapshot failures show a fixed message,
without exception text. This read is not a promise that store capture has no
state-publication side effects; it never writes a product setting.

The inspector presents 32 metadata rows per page and retains at most 2,048 rows.
The total topology count and any truncation are stated explicitly. Previous/next
page operations reuse that captured snapshot; they do not capture again. The
refresh action resets to the first page. Both metadata and renderer fields remain
stable between explicit captures. No timer, polling loop or frame-driven snapshot
refresh is registered.

Cards and their focusable refresh/pagination actions exist only while the global
advanced page is selected and the committed `developer.enabled` preference is
true. Disabling the preference or leaving the page retires their action entities;
stale queued intents cannot refresh or page a hidden inspector. A later display
captures the current facts. No edit control or settings write route is used.

## Boundary and validation

These are host diagnostics over existing XSR state and renderer APIs. Services
and contract assemblies gain no renderer, Desktop or Avalonia dependency.
The two catalog positions gain availability only alongside their host consumer.
Reserved appearance debugging positions, operation tracing, native diagnostics,
raw state values and network probing remain outside this delivery.

Desktop behavior tests cover committed developer visibility, renderer capture
and explicit refresh, metadata paging without recapture, settings destination
leave/re-entry, hidden/stale actions, settings revision preservation and values
whose `ToString()` must never run.
The integrated Desktop harness, architecture gate and normal trim/AOT publication
remain required integration checks. This note records the contract; execution
results are reported separately after those checks run.

## Executed integration evidence

2026-10-05: Release build, Services 599 and Desktop 176 under CoreCLR and
NativeAOT, the 70-project architecture gate, and NativeAOT/trimmed Desktop
`--validate-shell` and `--validate-setup` all passed. The complete execution
record and platform/acceptance limits are in
[XSR-795](XSR-795-unimplemented-inventory.md#本轮集成交付与验证).
