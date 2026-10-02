# Sidecar Event and Intent execution

The host owns a finite catalog of numeric signal points. Each point declares Event or Intent
and whether a local catch is allowed. The composition root grants exact targets to Sidecar
sessions. Account ownership, release verification, updater authorization and other trust
decisions are never signal catch points. UI.Next continues to emit intent to Desktop; it has
no Sidecar dependency.

EventCatch and IntentCatch install a validated local predicate and optional consume action.
EventListen observes every matching event. IntentWait observes the first matching intent in
each activation, without consuming it. Waiting does not suspend the UI or synchronously call
the child. Reactivation creates a new one-shot wait and a new activation identity.

The initial signal ABI is an explicitly supplied, non-secret string (at most 1024 UTF-16
characters). Registration payload is `NXS1`, mode byte (0 observe, 1 consume), then strict
UTF-8 exact-match text; empty text matches any value. Match text is limited to 512 bytes.
Consume is valid only for Catch and only on a host point granting it. Flags and codec are zero.
When a host supplies an execution admission, unknown targets, mismatched kinds and malformed
predicates reject registration before READY. Hosts without an execution admission retain
declarations for inspection only, preserving the existing registration contract.

Append-only message 74, HookSignal, uses TLV fields: 1 activation GUID, 2 registration kind,
3 session-local contract ID, 4 monotonic activation sequence, 5 value. The child uses the
activation GUID to discard an already in-flight notification from a retired activation.
There is no reply, reflection dispatch, JSON or remote CLR object exchange.

Each activation has a 128-item bounded ordered queue and a single asynchronous sender.
Notification admission and one-shot consumption are serialized inside that queue, without
performing IO or invoking user callbacks. Overflow immediately disables that activation and
fails the session asynchronously; it never blocks the caller or silently loses notifications.
Catch consumes only when its notification was admitted. Runtime limits are 256 bindings total,
32 per point and 128 per session. Inactive points allocate nothing. Deactivation, disposal,
peer failure and shutdown retire all bindings, cancel writes and discard queued notifications.

Initial product adapters expose resource search button intent and resource search completion.
Only the search button can be caught. Automatic searches and resource download/install intent
are outside this grant. Search completion carries only `success` or `failure`, not search text,
paths, account information or result metadata.
