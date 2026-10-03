# XSR-779 — Installed content refresh

Instance Mods, Resource Packs and Shaders share one read-only enrichment and update
check through the existing Resource Content Online Service. Refresh rescans local
files, invalidates page results and bypasses provider detail/identity caches for the
new pass. Desktop no longer starts a separate Modrinth-only management update scan.

Desktop queues at most two batches of two files, prioritizing visible rows. Each
completed batch is applied independently without losing search draft/focus. Discovery
and provider operations run outside rendering; late results are retired on refresh,
navigation, instance change and mutation. The list reports progress and partial
failures; an unrecognized/offline file remains locally represented, with unknown
update availability rather than false. Refresh controls are icon-only, labelled
accessibly and placed at the right end of the top toolbar.

Batch fingerprint identification reuses healthy provider matches even when another
provider fails. Partial matches are scoped to that request, not cached as globally
complete facts. Batch list enrichment skips description translation; detail pages
retain it. Per-file enrichment has a bounded deadline and verifies current file
identity before returning. Metadata/update download authority and hash checks remain
unchanged. Explicit refresh also bypasses merged detail snapshots; UI language is
still applied at projection time.
Production resource requests reuse the shared public HTTP pool with redirects/cookies
disabled and the existing redacted diagnostic handler, so provider failures and latency
remain observable without an additional transport or credential store.

Regression coverage includes partial-provider request counts, independent small batch
publication, explicit refresh cache bypass, late-response retirement, search focus,
combined update flags and icon-only right-aligned actions.
