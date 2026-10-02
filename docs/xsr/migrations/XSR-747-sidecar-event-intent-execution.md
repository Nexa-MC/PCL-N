# XSR-747: bounded Sidecar Event/Intent adapters

EventCatch, EventListen, IntentCatch and IntentWait now have executable host-owned adapters,
with the binary predicate and notification contracts in `../sidecar-signals.md`. Numeric points
are resolved once; local catches do not round-trip to the child. Registration validates grants,
types and budgets before READY. A bounded ordered queue performs asynchronous notifications;
activation identity and retirement prevent reuse of a stale binding.

Desktop grants resource search button interception and search completion observation. Other
resource actions continue through their existing typed contracts. No raw search, path, account
or result data is sent by these adapters. UI.Next continues to emit intent to Desktop.

Regression tests exercise predicates, ordering, one-shot waits, reactivation, unauthorized and
malformed input, queue overflow behind a blocked writer, all terminal paths, disposal racing
activation and zero-allocation inactive dispatch. A composition test clicks actual resource
controls, verifies the query is caught only for search and resumes after retirement. Semantic
architecture validation forbids renderer access to Sidecar execution APIs, including aliases.

UI Patch/New UI rendering and further Function ABI shapes remain separate incomplete units.
This migration does not claim completion of the Alpha 6 physical acceptance matrix.
