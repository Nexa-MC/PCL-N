# XSR-790 — Complete the PCL-N Sidecar Host API

This delivery covers the Host repository: independently consumable Protocol and
Transport libraries, Host sessions, supervision, and Desktop teardown. The
Nexa.Plugin execution engine, Plugin SDK/package/UI IR freeze and arbitrary
plugin UI remain owned by their separate repository and contracts.

## Compatibility and wire ownership

Existing message numbers, eight-field registration constructor, Runtime codec
entry points and string command/query/event methods remain compatible. Protocol
owns portable request/result/state/event, value and control/stream codecs;
Runtime retains forwarding entry points. No CLR objects, reflection dispatch,
JSON data plane or synchronous IPC are introduced.

HELLO optionally carries feature bits in TLV 3; WELCOME optionally carries the
negotiated bits in TLV 4. Missing fields mean legacy behavior. Bits are binary
payloads (1), streams (2), health (4), and unregistration (8). Host advertises its
supported features and uses only the negotiated intersection. Legacy string
fields and existing state codecs 0–6 retain their meaning. New APIs reject use
of an unnegotiated feature before sending any bytes.

Command/Query/Event declarations may carry codec 0–6 only when binary payloads
are negotiated; Stream is append-only registration kind 13. Streaming needs only
the streams bit, independently of binary command/query payloads. Registration
TLV 9 declares the result/chunk codec (default 0). Preserve existing positional CLR
signatures by adding this as an init property. String requests/events retain
TLV 1 contract and TLV 2 text. Binary requests/events add TLV 3 codec and TLV 4
bytes; nonzero codecs omit text. Results retain TLV 1 success, 2 legacy text,
3 stable error; binary results add TLV 4 codec and 5 bytes. Mixed representations,
missing required fields, codec mismatch and unsupported codecs are rejected.
DTO codec 6 remains an opaque contract-owned binary blob, not an invented SDK
schema generator. Explicit typed adapters perform field decoding without reflection.

Frame headers are validated before body allocation/read. Correlations are
nonempty; trait bits are validated as a bit mask. TLVs are ordered and unique,
all trailing fields are parsed, strings are strict UTF-8, booleans are 0/1 and
integer shapes are exact. Unknown fields remain skippable. A partially consumed
or written frame ends its connection; cancellation before IO admission does not.
Encoding occurs after bounded write admission. Failed connect/accept paths retire
their owned resources, and listener disposal cannot create a new endpoint.

## Complete session API

Session admission uses a sealed immutable semantic table and per-kind numeric
indexes. Pending requests bind correlation, expected result message and declared
result codec. Late results are ignored before payload decoding. Unavailable,
timeout, cancellation, backpressure and malformed remote errors return stable
XSR outcomes. Deactivation rejects/cancels pending work and stops state/event
admission; activation restores the same registered session. Terminal paths retire
all activation leases and invalidate mirrors. Initial values are validated and
published into a private candidate store before the original mirror store commits
the complete initial cell set and READY is sent. Both the mirror and store retain
their identity and existing Changed subscriptions. The State kernel supplies a
one-time initial snapshot commit for matching, cell-only, previously unpublished
topologies of string, Bool, Int32, Int64, Float64 and Bytes cells:
validation/cloning precedes one atomic node-table publication, followed
by notifications outside mutation locks. Ordinary writers exclude initial commit,
and candidate values do not alias the committed nodes. Callbacks cannot observe a
half-filled snapshot. This is not a general multi-writer transaction API.
Initial commit accepts at most 4,096 cells and owns at most 32 MiB of byte-array values.

HealthPing/Pong (16/17) exchange an ordered TLV nonce (field 1 U64), correlated
under a deadline; unsolicited ping receives pong, and unrelated/late pong is
ignored. Structured Crash (24) and append-only Error (25) carry bounded stable
code and non-secret diagnostic text. Errors never become arbitrary exception
types or unvalidated semantic IDs. Session metrics expose bounded counters and
last-known lifecycle/health without retaining arguments, secrets or payloads.

StreamOpen (81) starts a negotiated Stream contract; existing StreamChunk (80)
has sequence (1 U64), codec (2 U32), value (3 Bytes); chunk sequence starts at 1
and increments without gaps. StreamEnd (82) has next sequence (1 U64), success
(2 Bool), stable error (3 Str). StreamCredit (83) has
credit count (1 U32). StreamOpen adds initial credit in TLV 5 to the request
shape. A stream has one asynchronous consumer, at most 16 queued chunks, each
at most 65,535 bytes; a session owns at most 16 streams and 4 MiB buffered stream
bytes. Consuming a chunk returns one credit. Sequence, codec, credit and byte
limits are enforced before queue admission. Events/chunks are never silently
coalesced or dropped. Cancellation, disposal, timeout and session loss complete
the stream and send bounded best-effort CANCEL where possible.
Completed, unread chunks survive session retirement in the existing stream
handle; their ownership leaves the session quota and they remain locally readable.

Unregister (20) and Unregistered (21) are append-only control messages with
bounded reason text. Negotiated peers can retire their registration explicitly;
legacy shutdown still retires Host-owned declarations locally. Normal shutdown
rejects new work, retires activation, sends DEACTIVATE, optionally UNREGISTER,
then SHUTDOWN before closing the transport. It is bounded, asynchronous and
idempotent; failure/crash cleanup remains authoritative.

## Supervision and usable Host facade

Supervisor exposes immutable package/session/status snapshots and asynchronous
stop, restart, reload/rescan and shutdown. Restart always verifies the image,
authenticates a new endpoint and completes full registration/snapshot before
publishing a replacement session; failed replacement preserves no active old
session. Paths are restricted to admitted top-level packages. Concurrent controls
serialize and cannot resurrect disposal. Recovery is bounded by retry/backoff
policy and independently attributed per package; one failure does not end Host
or other packages. Desktop requests ordered asynchronous shutdown and observes
startup completion before disposing logging/state dependencies.
Process cleanup owns the root process and makes a best-effort attempt to terminate
its current descendants. Already orphaned or escaping native descendants require
execution-engine or operating-system containment beyond these Host interfaces.

A Host API resolves package identity and a registered semantic once, then uses
the session's numeric table. It provides command/query, typed payloads, local
state/resource reads, health/stream and lifecycle operations without inventing
permissions to call unrelated business services. UI/resource opens remain local
and perform zero IPC. Verified cache storage and old byte-array APIs use owned
copies so callers cannot mutate admitted hashed content or registrations.

## Acceptance

Run the complete Protocol/Transport and Runtime executable harnesses, including
real OS IPC with a test peer, full activation/reactivation, commands/queries,
state/event/streams, cancellation/overflow, crash/restart and graceful shutdown.
Exercise independent Protocol/Transport consumption, preserve legacy peers and
existing UI/Function/signal lease regressions, then run architecture, formatting,
NativeAOT and trim checks. Test peers are fixtures, not a replacement Plugin SDK.
Report local platform evidence and hosted CI separately.

### Local delivery evidence (2026-10-05)

The completed patch was validated on Debian 13, Linux x64, with .NET SDK
10.0.100 and build channel `ci` / version seed `a59ec3`:

| Check | Result |
|---|---|
| Complete solution build | Pass; zero warnings and errors |
| Protocol/Transport executable harness | 35 tests, zero skips; Managed and NativeAOT |
| Runtime/Host executable harness | 164 tests, zero skips; Managed and NativeAOT, including real `.nsc` processes |
| Independent DLL consumer | 31 assertions each under Managed and NativeAOT; references only Protocol/Transport |
| Foundation Services regression | 583 tests each under Managed and NativeAOT |
| Desktop composition regression | 161 Managed tests |
| Renderer / Avalonia backend / PXML | 97 / 15 / 40 Managed tests |
| UI.Next benchmark gates | Pass; timings remain informational |
| Architecture / source formatting | 70 projects; `IDE0055` and `IMPORTS` verification pass |
| Desktop publication | NativeAOT and trimmed shell plus first-run smoke checks pass |

The existing lease fixtures now use valid HELLO correlations and intentional
protocol violations to test retirement, rather than malformed late results
that must be ignored. Pending-request deactivation fixtures wait for completed
Host writes. The proxy regression waits for completed asynchronous saves and
confirmed UI values while retaining its original assertions and deadline.

The `sidecar-ipc` workflow now runs the complete NativeAOT Host harness on
Windows, macOS and Linux alongside Protocol/Transport. These are added hosted
gates; the local results above do not claim those remote runs have completed.
See the [complete API reference](sidecar-host-api.md) for call signatures,
route types, capabilities, limits and execution-engine ownership boundaries.
