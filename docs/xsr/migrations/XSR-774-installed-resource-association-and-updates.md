# XSR-774 — Installed resource association and updates

## Contract

Scanned mods, resource packs and shader packs are identified in background Services
by file fingerprints through the existing sealed batch query. Results are qualified
by instance directory, page, filename, size and modification time. Navigation or a
rescan retires pending work; unmatched files retain local metadata. Rendering must
not hash files or wait for HTTP. List icons use the existing bounded icon query,
only for visible entries, with at most four pending requests.

Online results project the installed provider/version identities and an optional
compatible update candidate. A different filename or display version is not proof
of an update: a candidate must have a valid publication timestamp newer than the
identified installed versions and must not already be installed on either source.
Unknown installed chronology does not become an update fact. Compatibility stays
owned by the catalog Service, not Desktop.

Lists keep the established detail-only row action. Pack/shader details expose an
explicit update action alongside the installed version and compatible version
choices. Resource pack local formatted filename and pack-format metadata remain
distinct from online project name and online release version. Local embedded icons
take precedence; online icons fill missing previews.

Updates reuse the existing Service command, authoritative re-identification,
compatible project/version validation, checked download and reversible replacement
transaction. No filename or destination can be supplied by the UI. A completed
update refreshes local content and association; failures retain the original file.

## Verification

Tests cover all three content kinds, exact installed identities, chronology and
cross-provider duplicate handling, visible icon adoption without focus loss,
stale instance retirement, pack/shader update dispatch, and existing update
transaction success/failure, stale file, wrong project and running-instance checks.
Architecture, formatting, shell smoke and NativeAOT/trim CI remain required.
