# XSR-791 · Process output viewer

## Boundary and migration contract

The process dock's disabled `日志（尚未实现）` action becomes a real output viewer.
`Nexa.Services.Minecraft.Process` owns pipe draining, redaction, session identity and
retention. An additive typed `minecraft.process.output.read` query returns a detached
snapshot for one session; Desktop never reads OS pipes, filesystem log paths or a
concrete process service. `Nexa.UI.Next` renders text, selection and commands only.
Clipboard access remains a Desktop native effect invoked by an explicit copy intent.

The existing crash evidence queues and JVM observation contracts retain their
semantics. The viewer keeps a separate ordered tail of at most 512 entries, each
at most 2048 UTF-16 code units before redaction. Every displayed and copied entry
passes through the existing `LogRedactor`. An entry records its sequence and
stdout/stderr source. Dropped-entry counts explain tail truncation to the user.
No timer, background polling or per-line state publication is introduced.

## Product behavior

Opening a running session's log dock loads that exact session. The latest finished
session keeps a log entry point when no game is running. The viewer can switch
between all sessions retained by the existing 32-session / 12-hour policy. Output
remains readable after normal exit, failure or cancellation; pruning is surfaced
as an unavailable session, without showing another session's output.

The viewer supports case-insensitive literal search, stdout/stderr/all filtering,
24-entry pages, previous/next pages, explicit refresh, per-entry selection,
selection clearing, and copy selected entries or the current page. Copy uses the
original redacted entry text with source labels; presentation does not interpret
localization markers or markup in game output. Empty output, no search results,
query failure and a missing clipboard effect each have a concrete explanation.
Process dock bubbles hide while the viewer is open so they do not cover its rows
or copy controls; returning restores the appropriate running or latest-ended dock.

Reads are dispatched off the render thread. Leaving the page, choosing a different
session, or disposing the launch controller cancels the read and retires its
generation. Late completion cannot project or copy output into a later page.
UI work and entity counts are bounded by the current 24-entry page, regardless of
the size of retained output or number of retained sessions.

## Validation

Desktop contract tests cover distinct session identity, stdout/stderr source,
secret redaction before display/copy, search/filter/page reachability, selection
copy, ended-session reads and stale read retirement on navigation/session change.
Process tests exercise actual redirected child stdout/stderr and the bounded tail
without changing crash evidence retention. Existing process dock tests are updated
to require an enabled log action. The root task runs the relevant Desktop and
Services suites and architecture/AOT checks; this slice adds no reflection, IPC,
renderer service reference or runtime dynamic code.

## Executed integration evidence

2026-10-05: Release build, Services 599 and Desktop 176 under CoreCLR and
NativeAOT, the 70-project architecture gate, and NativeAOT/trimmed Desktop
`--validate-shell` and `--validate-setup` all passed. The complete execution
record and platform/acceptance limits are in
[XSR-795](XSR-795-unimplemented-inventory.md#本轮集成交付与验证).
