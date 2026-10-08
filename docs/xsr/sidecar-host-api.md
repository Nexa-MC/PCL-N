# PCL-N Sidecar Host, Protocol and Transport API reference

This reference describes the executable Host surface delivered by
[XSR-790](XSR-790-sidecar-host-completion.md). It covers PCL-N's independently
consumable `Nexa.Sidecar.Protocol` and `Nexa.Sidecar.Transport` libraries, the
Host session, supervision and the composition API. The independently owned
`Nexa.Plugin.Sidecar` execution engine, Plugin SDK, plugin package/manifest
schema and stable Plugin UI IR remain in the Nexa.Plugin repository and their
own version axes. Consuming the wire libraries does not require a reference to
Host Runtime, Services, Desktop, Avalonia or renderer internals.

The protocol version remains 1, with append-only message and registration
numbers. Optional features are negotiated separately; this delivery does not
freeze Plugin SDK 1.0 or turn the Host's internal PXML IR into a plugin ABI.
See [architecture](architecture.md), [protocol](sidecar-protocol.md) and
[dependency rules](dependency-rules.md) for these boundaries.

## Host composition and routes

`SidecarHostApi(SidecarSupervisor supervisor, IXsrDispatchObserver? observer = null)`
exposes sealed `Commands` and `Queries` routers, `PackageSnapshots`, and direct
asynchronous methods. Resolve a `SidecarHostRoutes` semantic once with the
router's `TryResolve`; subsequent Host dispatch uses the returned numeric
`XsrCommandId` or `XsrQueryId`. A package identity is its discovered filename
without `.nsc`. It is not a filesystem path or permission to call arbitrary
business services.

The five Command routes return `XsrResult` through observed asynchronous
completion:

| Route constant | Semantic | Request | Effect |
|---|---|---|---|
| `Command` | `sidecar.command` | `SidecarCommandCall` | Execute a registered legacy text command |
| `BinaryCommand` | `sidecar.binary.command` | `SidecarBinaryCall` | Execute a registered binary command; the Command completion reports success/error |
| `Stop` | `sidecar.stop` | `SidecarPackageCall` | Stop an admitted package and disable its automatic recovery |
| `Restart` | `sidecar.restart` | `SidecarPackageCall` | Stop, re-verify and fully initialize a new session |
| `Reload` | `sidecar.reload` | `SidecarReloadCall` | Rescan the original directory and reload its current packages |

The nine Query routes return `XsrResult<TResponse>`:

| Route constant | Semantic | Request | Response |
|---|---|---|---|
| `Query` | `sidecar.query` | `SidecarQueryCall` | `string` |
| `BinaryQuery` | `sidecar.binary.query` | `SidecarBinaryCall` | `SidecarBinaryValue` |
| `State` | `sidecar.state.read` | `SidecarContractCall` | `SidecarStateRead` |
| `Resource` | `sidecar.resource.read` | `SidecarContractCall` | `SidecarCachedContent` |
| `UiModule` | `sidecar.ui.read` | `SidecarContractCall` | `SidecarCachedContent` |
| `Catalog` | `sidecar.catalog` | `SidecarCatalogQuery` | `IReadOnlyList<SidecarPackageSnapshot>` |
| `Session` | `sidecar.session` | `SidecarPackageCall` | `SidecarSessionInfo` |
| `Health` | `sidecar.health` | `SidecarHealthCall` | `TimeSpan` round-trip duration |
| `Stream` | `sidecar.stream.open` | `SidecarBinaryCall` | `SidecarHostStream` |

`SidecarCommandCall` and `SidecarQueryCall` carry `PackageName`, `Contract`,
optional text `Argument` and `Timeout`. `SidecarBinaryCall` carries the same
identity with an optional `SidecarBinaryValue` argument. `SidecarContractCall`
contains package and contract; `SidecarPackageCall` contains package;
`SidecarHealthCall` adds an optional timeout. Catalog and reload requests have
no fields.

Direct methods are `SendCommandAsync`, `SendQueryAsync`,
`SendBinaryCommandAsync`, `SendBinaryQueryAsync`, `PingAsync`, `OpenStreamAsync`,
`ReadState`, `ReadResource`, `ReadUiModule`, `ReadSession`, `StopAsync`,
`RestartAsync` and `ReloadAsync`. Asynchronous methods accept cancellation.
The direct binary Command method additionally returns its admitted binary
result; the Command router intentionally retains ordinary action-completion
semantics. There is no binary Command disguised as a Query route.

State, content, catalog and session reads are local and perform zero IPC.
`SidecarStateRead` contains `Revision`, `Availability`, `HasValue` and an owned
optional binary `Value`. `SidecarSessionInfo` contains `SessionId`, `State`,
negotiated `Features`, immutable `Contracts` and `Metrics`.
`SidecarCachedContent` exposes `Length`, read-only `Span`, SHA-256 `ContentHash`
and copy-producing `ToArray()`. Cached content follows registration budgets,
not the size limit of a single wire value. Array-returning compatibility cache
methods also return copies; callers cannot mutate admitted hashed storage.

## Session API

`SidecarHostSession(connection, pluginName, observer = null, timeProvider = null,
maxPending = 1024, limits = null)` owns one connection. Its public views are
`PluginName`, `State`, `FailureReason`, `SessionId`, `NegotiatedFeatures`,
`Registration`, `Mirror`, `Cache`, `Extensions` and `PendingCount`.
The four optional init-only grants are `FunctionPatchAdmission`,
`SignalAdmission`, `UiPatchAdmission` and `UiModuleAdmission`.

| Operation | Public methods | Contract |
|---|---|---|
| Initialization | `HandshakeAsync`, `AcceptRegistrationAsync`, `AcceptStateSnapshotAsync` | Ordered handshake, complete declarations, coherent private snapshot, then READY |
| Activation | `ActivateAsync`, `DeactivateAsync` | Active/Ready transitions; reactivation uses the existing registration |
| Legacy execution | `SendCommandAsync`, `SendQueryAsync` | Semantic contract, optional string argument, timeout, cancellation |
| Legacy numeric execution | `SendCommandByIdAsync`, `SendQueryByIdAsync` | Session-local per-kind `uint` contract ID |
| Binary execution | `SendBinaryCommandAsync`, `SendBinaryQueryAsync` | Semantic contract and owned, codec-validated argument/result |
| Binary numeric execution | `SendBinaryCommandByIdAsync`, `SendBinaryQueryByIdAsync` | Same behavior using the registered numeric table |
| Events | `AttachEventObserver`, `AttachBinaryEventObserver` | Ordered Host callbacks; observer failures are isolated |
| Receive | `RunReceiveLoopAsync` | One session reader handles results, state, events, controls and streams |
| Health | `PingAsync`, `ReadMetrics` | Correlated nonce round trip and bounded payload-free counters |
| Streaming | `OpenStreamAsync`, `OpenStreamByIdAsync` | Registered Stream contract and owned asynchronous consumer |
| Retirement | `UnregisterAsync`, `ShutdownAsync`, `Dispose` | Explicit negotiated retirement, ordered bounded shutdown, or immediate local cleanup |

Numeric methods deliberately use distinct `ByIdAsync` names: existing calls
such as `SendCommandAsync(default)` keep their source meaning. An ID is valid
only for its registration kind and session. Reconnect requires resolving the
new registration again. Pending exchanges bind correlation, expected result
message and declared result codec. Late results are ignored before decoding;
wrong result types or codecs cannot complete unrelated work.

`ISidecarSessionObserver.OnStateChanged` observes lifecycle transitions.
`ISidecarSessionEventObserver.OnEvent(XsrSemanticId, string)` receives text
events. `ISidecarSessionBinaryEventObserver.OnEvent(XsrSemanticId,
SidecarBinaryValue)` receives admitted binary values, including codec-0 text
values; text observers are invoked only for text events. These callbacks run
on the receive path and must finish promptly.

`SidecarRegistrationEntry` records kind, semantic, numeric contract, flags,
argument/value `CodecId` and init-only `ResultCodecId`.
`SidecarRegistrationSet` owns immutable `Entries`, per-kind views `Commands`,
`Queries`, `States`, `Events`, `UiModules`, `Resources`, `Streams`, and lookup
methods `TryResolve(kind, semantic)` / `TryResolveId(kind, contractId)`.
`SidecarStateMirror` exposes `PluginName`, local `Store`, `TryResolve`, `Create`
and `PublishFromWire`. The accepted mirror identity is preserved while a
privately completed initial snapshot is adopted atomically. Terminal paths
invalidate availability while preserving the last known value.

`SidecarHostCache` exposes `ResourceCount`, `AddUiModule` (with optional
required-resource list), `AddResource`, `TryOpenUiModule`,
`TryGetRequiredUiResources`, `TryGetResource` by hash or semantic, and
`TryGetResourceHash`. Hashes and required-resource ownership are checked before
publication. `SidecarExtensionRegistry.Entries` returns owned copies of
`SidecarExtensionRegistration` (kind, semantic, target, ID, flags, payload).
Retirement removes session-owned executable extension declarations.

`SidecarExchangeOutcome` and Runtime `SidecarDataPlane` /
`ISidecarValueCodec` / `SidecarValueCodecs` remain compatibility entry points.
New independent peers use the Protocol codecs below. Runtime codec constants
0–6 retain their numbers; `Get`, `Validate`, `Decode` and `Encode` forward to
the shared primitive rules. `ISidecarValueCodec` retains `Id`, local
`ValueType`, `Validate`, `Decode` and `Encode`; `SidecarExchangeOutcome` retains
its Success/Value/ErrorCode record and `TimedOut` / `Cancelled` factories.

## Features, values and errors

HELLO TLV 3 advertises `SidecarFeatures`; WELCOME TLV 4 confirms the negotiated
intersection. Missing fields mean `None`. Unknown bits are not executable
grants, and unnegotiated APIs reject locally before sending request bytes.

| Feature | Bit | Enables |
|---|---|---|
| `BinaryPayloads` | 1 | Binary Command/Query/Event representations and their declared codecs |
| `Streams` | 2 | Stream registration and credit-based stream messages |
| `Health` | 4 | HealthPing/HealthPong |
| `Unregistration` | 8 | Unregister/Unregistered |

The bits are independent. Streams use their own codec-tagged payload and do
not additionally require `BinaryPayloads`. Existing typed State codecs also
do not require the binary Command/Query/Event feature. `All` is the union of
the current bits, not a promise that future peers implement every extension.

`SidecarBinaryValue(codecId, bytes)` validates and owns its storage. Its API is
`CodecId`, `Length`, read-only `Span` and `ToArray()`. `SidecarWireCodecs`
exposes `IsSupported`, `Validate`, object-based primitive `Encode`/`Decode` and
explicit generic adapter overloads. `ISidecarBinaryCodec<T>` supplies `CodecId`,
`Encode(T)` and `Decode(ReadOnlySpan<byte>)`; no reflection dispatch is involved.

| Codec ID | Constant | Value/shape |
|---|---|---|
| 0 | `Utf8String` | Strict UTF-8 |
| 1 | `Bool` | Exactly one byte, 0 or 1 |
| 2 | `I32` | Four little-endian bytes |
| 3 | `I64` | Eight little-endian bytes |
| 4 | `F64` | Eight little-endian bytes |
| 5 | `Bytes` | Owned bytes |
| 6 | `GeneratedDto` | Opaque contract-owned bytes; field codecs belong to the contract |

`MaximumValueLength` is 65,535 bytes, inherited from the 16-bit TLV field
length. DTO naming does not claim delivery of a Plugin SDK schema generator.
Legacy Command/Query/Event text fields retain their wire meaning. Mixed text
and binary representations, invalid UTF-8/UTF-16, unknown codecs and incorrect
primitive sizes are rejected.

Host outcomes use `XsrResult` / `XsrResult<T>` and stable errors. Relevant codes
are `xsr.cancelled`, `xsr.timed_out`, `xsr.backpressure`, `xsr.unavailable`,
`xsr.feature_unavailable`, `xsr.route_not_found`, `xsr.target_not_found`,
`xsr.contract_mismatch` and `xsr.handler_faulted`. Well-formed contract-owned
remote rejection codes remain rejection outcomes; malformed codes normalize
to `xsr.handler_faulted`. Transport/protocol failures terminate the affected
session and retire pending work. Structured Error can fail its correlated
exchange, health check or stream without failing other admitted work; Crash
is terminal. Legacy empty Crash payload remains accepted. Remote data never
becomes a CLR exception type.

## Streams, lifecycle and budgets

`SidecarHostStream` exposes `CodecId`, `ReadAllAsync(CancellationToken)`,
`Completion : Task<XsrResult>` and `DisposeAsync`. It has exactly one
asynchronous consumer. Await `Completion` to distinguish successful remote
end from cancellation, timeout, rejection or session loss. Completed unread
chunks remain charged until drained, disposed, or detached by session retirement.
Retirement transfers completed, unread chunks to the existing stream handle;
they remain locally readable and no longer consume the next activation's quota.
Disposing or abandoning the
consumer releases ownership and sends bounded best-effort CANCEL when possible.

StreamOpen grants 16 initial credits. Sequence starts at **1**; each accepted
chunk increments the expected sequence, and StreamEnd supplies that next
expected sequence. Consuming a chunk returns one credit while the remote
stream is live. IDs/codecs/sequences/credit/queue budgets are checked before
queue admission. Events and chunks are never silently coalesced or dropped.

| Budget | Default/maximum |
|---|---|
| Native images / signatures | 512 MiB image; 64 KiB detached signature |
| Discovery | 64 top-level `.nsc` candidates |
| Bootstrap plus admission under Supervisor | 30 seconds per candidate |
| Frame | 32-byte header; at most 16 MiB payload |
| TLV string/bytes field or primitive value | 65,535 bytes |
| Transport pending writes | 64, rejected before wire admission |
| Registration | 4,096 items; 256 characters per semantic; 32 MiB aggregate payload |
| Initial state snapshot | Separate 32 MiB aggregate payload; exact registered-state coverage |
| Session handshake / registration / shutdown | 10 seconds / 30 seconds / 2 seconds |
| Command/Query pending | Constructor `maxPending`, default 1,024 |
| Command/Query timeout | 30 seconds default; positive values up to 4,294,967,294 ms, the .NET timer limit |
| Stream timeout | 30 seconds default; positive values up to 5 minutes |
| Health | 5 seconds default; positive values up to 5 minutes; at most 32 pending checks and shared exchange admission |
| Streams | 16 per session; 16 queued chunks each; 65,535 bytes per chunk; 4 MiB aggregate buffered bytes |
| Best-effort CANCEL / credit delivery | 100 milliseconds |

`SidecarSessionLimits` allows smaller registration/item/semantic/snapshot
budgets, `HandshakeTimeout`, `RegistrationTimeout` and `ShutdownTimeout`.
The current validator caps handshake/registration at 5 minutes and shutdown
at 30 seconds. A peer cannot increase Host limits. Presentation adapters have
their additional budgets in the linked contracts below.

Normal initialization is bootstrap, HELLO/WELCOME, REGISTER_BEGIN/ITEM*/END,
STATE_SNAPSHOT_BEGIN/ITEM*/END, READY, ACTIVATE. Deactivation cancels admission
for pending work, retires activation leases and stops State/Event delivery.
Reactivation reuses the registration. Normal shutdown rejects new work,
retires activation, sends DEACTIVATE when active, optionally sends UNREGISTER
and awaits its correlated acknowledgement, then sends SHUTDOWN and closes.
Shutdown is asynchronous, bounded and idempotent. Explicit `UnregisterAsync`
is terminal and requires the negotiated feature. A failed or disposed session
cannot be reactivated; reconnect creates a new authenticated session and a
full coherent snapshot.

`SidecarSessionMetrics` reports lifecycle, pending exchanges/health, stream
count/buffer bytes, sent/received frame and byte counters, exchange outcomes,
late results, State/Event/chunk counts, crashes, last activity and health
timestamps/outcome/round trip, and last stable remote error. It retains no
arguments, content, tokens, account information or private paths.

## Supervisor and Desktop ownership

`SidecarSupervisor(verify, diagnostic = null)` requires a trusted signature
verification delegate. The Desktop supplies its embedded pinned release key.
Do not substitute a package-provided key or the test fixture's hash verifier
for production publisher verification.

Its public API is `StartAsync(directory, cancellationToken)`, `Sessions`,
`PackageSnapshots`, `TryGetSession(packageName, out session)`,
`StopAsync(packageName, cancellationToken)`,
`RestartAsync(packageName, cancellationToken)`, `ReloadAsync(cancellationToken)`,
`ShutdownAsync(cancellationToken)`, `Dispose` and `DisposeAsync`.
Stop/Restart return `bool` (unknown identity or failed replacement returns
false); the Host facade maps these into stable outcomes. Start is single-use;
reload always uses that original directory. Paths supplied as package identities
never grant admission. Controls serialize, and cancellation/disposal prevents
old recovery work from resurrecting a package.

`SidecarPackageSnapshot` contains `PackageName`, `Status`, optional `SessionId`
and `ProcessId`, `RecoveryAttempts` and stable `FailureCode`.
`SidecarPackageStatus` values are Discovered, Starting, Active, Stopped, Failed,
Recovering, Quarantined and Removed. Package process status and session Active
availability are distinct; `TryGetSession` supplies only Active sessions.

Restart and recovery revalidate executable format/architecture and the pinned
signature, bind a new endpoint, generate a new stdin challenge and accept a
complete new registration/snapshot. One failed package does not terminate Host
or other packages. Unexpected disconnect recovery permits three attempts with
250/500/1,000 ms backoff, then quarantines the package. Manual restart/reload
resets that budget; manual stop disables automatic recovery. Rescanning retires
removed identities and rejects empty or ambiguous identities independently.

Supervisor owns the root Sidecar process and uses the OS process-tree API to
terminate its current descendants as a best effort. It cannot guarantee
termination of already orphaned descendants or plugin processes deliberately
detached from that tree. The independently owned execution engine is responsible
for its native child lifetimes. This is process failure isolation, not an OS
sandbox or a guarantee covering escaped forks.

Desktop disposes UI objects on their GUI thread before the first asynchronous
handoff, then awaits Sidecar shutdown and the startup diagnostic task before
releasing Host dependencies. `Dispose()` requests asynchronous Supervisor
shutdown; callers needing completed teardown must await `ShutdownAsync` or
`DisposeAsync`. Concurrent child shutdown prevents one slow package from
multiplying the overall child-exit deadline.

## Protocol API inventory

All these types live in `Nexa.Sidecar.Protocol` and have no Host dependencies.
Encode/Decode pairs use ordered unique TLVs, validate required known-field
shapes, consume all trailing fields and skip unknown fields by length.

| Surface | Public API |
|---|---|
| `SidecarProtocol` | `Magic`, `Version`, `HeaderSize`, `MaxPayloadLength` |
| `SidecarFrame` | Version, message, traits, correlation, payload; `IsControlPlane`, `IsDataPlane` |
| `SidecarFrameCodec` | `GetFrameSize`, `Encode`, `Decode`, `ValidateHeader` |
| `SidecarCorrelationId` | `Value`, `Create`, `IsAssigned`, `ToString` |
| `SidecarFeatures` | `None`, `BinaryPayloads`, `Streams`, `Health`, `Unregistration`, `All` |
| `SidecarFrameTraits` | `None`, `Compressed`, `Final`; undefined bit masks are rejected |
| `SidecarPayloadWriter` | `Length`, `WriteBoolean`, `WriteUInt32`, `WriteUInt64`, `WriteInt64`, `WriteDouble`, `WriteGuid`, `WriteString`, `WriteBytes`, `ToArray`, `Dispose` |
| `SidecarPayloadReader` | Constructor from span, `HasMore`, `ReadNext` |
| `SidecarPayloadField` | `Id`, `Tag`; `ReadBoolean`, `ReadUInt32`, `ReadUInt64`, `ReadInt64`, `ReadDouble`, `ReadGuid`, `ReadString`, `ReadBytes` |
| `SidecarFieldTag` | Boolean, U32, U64, I64, F64, Str, Bytes, Id128 |
| `SidecarHandshake` | `EncodeHello` with optional features, `DecodeHello`, `DecodeHelloDetails`; `EncodeWelcome` with optional notice/features, `DecodeWelcome`, `DecodeWelcomeDetails` |
| `SidecarRegistration` | `EncodeBegin`/`DecodeBegin`, `EncodeItem`/`DecodeItem`, `EncodeEnd`/`DecodeEnd`, `IsExtension` |
| `SidecarStateSnapshot` | Begin/Item/End encode/decode pairs; legacy `EncodeCancel`/`DecodeCancel` |
| `SidecarDataMessages` | Request/Result/StateDelta/Event encode/decode pairs; BinaryRequest/BinaryResult/BinaryEvent encode/decode pairs; binary `Decode*Details` additionally reports `IsBinary` |
| `SidecarControlMessages` | Cancel, Health, Failure and Unregister encode/decode pairs; `IsStableErrorCode` |
| `SidecarStreamMessages` | Open/Chunk/End/Credit encode/decode pairs; `MaximumCredit` |
| `SidecarBinaryValue`, `SidecarWireCodecs`, `ISidecarBinaryCodec<T>` | Owned values and primitive/explicit typed codec operations described above |
| `SidecarHookSignal` | Activation GUID, registration kind, numeric contract, activation sequence, string value; `Encode`, `Decode` |
| `SidecarUiCaptionPatch` | `Encode(caption)`, `Decode(payload)` |
| `SidecarUiCard` | Slot, title and body; `Encode`, `Decode` |
| `SidecarProtocolException` | Stable protocol-shape exception for library callers |

`SidecarRegistrationItem` retains its existing positional constructor and
deconstruction signatures: kind, semantic, flags, codec, optional payload,
hash, required resources and target. `ResultCodecId` is an additive init
property carried by TLV 9. Command/Query arguments use `CodecId`, results use
`ResultCodecId`; Stream uses these for its open argument and chunks.
Kinds retain numbers 1–12; Stream is append-only kind 13.

| Registration kind | Number | Registration kind | Number |
|---|---|---|---|
| Command / Query | 1 / 2 | State / Event | 3 / 4 |
| UiModule / Resource | 5 / 6 | UiPatch | 7 |
| EventCatch / EventListen | 8 / 9 | IntentCatch / IntentWait | 10 / 11 |
| FunctionPatch | 12 | Stream | 13 |

| Message | Number | Message | Number |
|---|---|---|---|
| Hello / Welcome | 1 / 2 | RegisterBegin / Item / End | 8 / 9 / 10 |
| Ready / Activate / Deactivate | 11 / 12 / 13 | StateSnapshotBegin / Item / End | 14 / 15 / 18 |
| HealthPing / HealthPong | 16 / 17 | Cancel | 19 |
| Unregister / Unregistered | 20 / 21 | Crash / Error / Shutdown | 24 / 25 / 30 |
| CommandRequest / Result | 64 / 65 | QueryRequest / Result | 66 / 67 |
| StateDelta / Event / HookSignal | 72 / 73 / 74 | StreamChunk / Open / End / Credit | 80 / 81 / 82 / 83 |

Frame headers require an assigned correlation and valid version/message/trait
mask before payload allocation. Existing headers and field meanings are
unchanged. Traits are advisory; the current Transport does not introduce a
compression implementation. Use the defined message payload codecs rather
than serializing CLR objects or placing JSON on the data plane.

## Transport API inventory

`Nexa.Sidecar.Transport` depends only on Protocol. It owns reliable framed
local IPC and bootstrap authentication; it does not execute plugin business
code or reconnect sessions automatically.

| Surface | Public API and ownership |
|---|---|
| `SidecarExecutable` | `MaximumImageBytes`, `Validate(Stream)` for the current OS/architecture's native PE/ELF/thin Mach-O |
| `SidecarBootstrap` | `ChallengeSize = 32`, `ConnectAsync(endpoint, bootstrapInput, cancellationToken)`, `AuthenticateAsync(stream, expected, cancellationToken)` |
| `SidecarIpcListener` | `IsSupported`, `Bind(pipeName)`, `Endpoint`, `AcceptAsync`, `Dispose` |
| `SidecarIpcConnector` | `ConnectAsync(endpoint, cancellationToken)`; failed connect paths dispose their streams/sockets |
| `SidecarFrameTransport` | Constructor from duplex stream; `MaximumPendingWrites = 64`, `SendAsync`, `ReceiveAsync`, `Dispose` |
| `SidecarTransportBackpressureException` | Local write budget exhausted before any frame bytes are sent |
| `SidecarConnection` | Owns stream; `State`, `FailureReason`, `SendAsync`, `ReceiveAsync`, `Close`, `Dispose` |
| `SidecarConnectionState` | Connected, Closed, Failed |
| `SidecarLoopbackStream` | `CreatePair`, asynchronous Stream reads/writes and disposal for fixtures; no physical-IPC claim |

Windows pipes are current-user only. Unix sockets live in a generated 0700
directory with a 0600 socket. Use the listener's actual `Endpoint` on Unix;
the logical pipe name is not a deletion target. A transferred accepted stream
remains owned by its caller when the listener is disposed. An outstanding
accept is canceled/retired on disposal, which cannot create a new endpoint.

The child entrypoint receives `--nexa-sidecar --endpoint <endpoint>`, calls
`SidecarBootstrap.ConnectAsync` with inherited standard input and only then
begins framed traffic. The one-use challenge never appears in arguments.
Writes serialize and encoding occurs after bounded admission. Frame reads are
serialized; the session still supplies one logical receive loop. Cancellation
before IO admission preserves the unused connection. Once frame IO begins,
interruption closes the stream instead of permitting a partially consumed
frame to corrupt later traffic.

## Host presentation grants and remaining boundaries

Host composition explicitly supplies `XsrSignalAdmission`,
`XsrUiPatchAdmission`, `XsrUiModuleAdmission` and `XsrFunctionPatchAdmission`.
The local runtimes expose catalog constructors, `Resolve` and the relevant
`Emit`/`Invoke` or immutable State projection. `XsrSignalDefinition` and
`XsrSignalKind` define permitted Event/Intent points; `XsrUiCaptionTarget`
defines the caption budget. `XsrUiPatchSnapshot.CaptionAt` and
`XsrUiModuleSnapshot.CardAt` expose sealed presentation values.
`XsrFunctionPatchPoint.SemanticId` identifies an explicitly granted point;
`XsrFunctionPatchPhase` defines Head, Args, Tail, Return and Replace.
`XsrFunctionPatchAttribute(Target)` marks the compile-time Host point, and
`XsrFunctionPatchRuntime.Invoke` executes the compatible string ABI; `InvokeValues` executes
explicit primitive and multiargument shapes around the supplied original Host function with
optional cancellation. `HasPatches` supports generated allocation-free inactive wrappers.
`XsrUiModuleSnapshot.ModuleAt` provides immutable node views with an exposure GUID;
`XsrUiModuleRuntime.DispatchAsync` re-admits an intent and translates it to numeric session
Command dispatch with source and retirement cancellation.

The current product targets are resource search intent/completion, its search
caption, one text/interactive module slot and resource title/download-count Function patches. They preserve
Host ownership of search/download/account/release trust decisions. Detailed
schemas, public runtime types and execution budgets are in
[signals](sidecar-signals.md), [caption patches](sidecar-ui-patches.md),
[text cards](sidecar-ui-modules.md) and [Function patches](function-patches.md).
Renderer reads remain local; UI.Next never calls a Sidecar.

Interactive finite plugin forms, verified resource images/live plugin State bindings and
primitive Function ABI shapes are provided by [XSR-806](XSR-806-sidecar-extension-completion.md).
Arbitrary scripts/CLR signatures and product-tree replacement are outside the Host grant.
Nested provider permission fabric and the independent Plugin SDK/package/UI IR freeze remain
external contracts. The real OS test peer validates the Host workflow and API; it is a
fixture rather than the independently owned execution engine or an authored
plugin SDK.
