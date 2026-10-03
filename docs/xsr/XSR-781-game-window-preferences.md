# XSR-781 — Captured game window preferences

Window title and launcher visibility are next-launch settings resolved by Services
for the root-qualified instance. Explicit instance overrides take priority over
legacy instance metadata; legacy custom titles take priority over a global title.
An empty explicit title leaves the game's title unchanged. Titles are bounded to
512 characters and cannot contain controls. No shell expansion or secret-bearing
launch token substitution is performed.

The immutable request, plan and sealed process snapshot carry the resolved title
and launcher visibility. Desktop observes the existing sealed launch-success and
process state contracts, and Host performs native window effects. It never reads
settings to reinterpret a running session. Title integration is best-effort on
Windows, with a bounded native send; other platforms report it as unsupported.
Optional decoration cannot change launch success.

Visibility supports keep, minimize, hide/reopen and hide/close on successful exit.
The latter preserves legacy modes 0/2: supervision and crash analysis remain alive
while the game runs. A failed or cancelled session restores the launcher. Multiple
overlapping sessions are tracked by SessionId; one normal exit cannot close or
restore the launcher while another managed session is running. Startup history
does not replay terminal sessions. Native dispatch also works while hidden, without
requiring a render frame. Close still honors the existing installation exit guard.

Regression coverage must exercise scoped resolution, legacy values, immutable plan
and process projections, overlapping sessions, failure restoration, stale terminal
history, dispose and icon/title decoration isolation. Architecture, NativeAOT shell
and trim gates apply to the completed slice.
