# XSR-749 New UI text-card State adapter

New UI now has a bounded text-card execution contract: immutable versioned binary documents,
exact host slot grants, whole-batch admission, active-session leases and sealed XSR State.
Desktop projects a compact scrolling card into the resource page on its render thread. The
canonical renderer sees normal components; it has no Sidecar lookup or render-time IPC.
The original opaque UI-module cache remains inspection-only without execution admission.
No plugin actions, bindings, service routes or PXML source are admitted by this schema.

Parity/regression covers immutable retained snapshots, predecessor restoration, grants,
resources/flags/codec rejection, malformed fields and byte/character/session/active budgets,
reentrant disposal, blocked observer ordering, every terminal path and buffered activation
races. A mixed caption/module activation failure now always drains the earlier deferred
caption publication after retiring its lease; later sessions cannot inherit a stuck publisher.
Desktop validation covers literal visual/accessibility text, long-body scroll extent, absence
of plugin actions, unchanged search/navigation identity and a hidden slot with no retained
children. Reading the empty immutable module projection allocates zero bytes after warmup.

This is the initial New UI document/slot adapter. Interactive pages, additional node shapes,
images and live plugin State bindings remain open; the complete Sidecar platform and Alpha 6
acceptance are not claimed. NativeAOT/trim and architecture evidence accompany this unit.
