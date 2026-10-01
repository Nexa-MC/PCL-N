# XSR Sidecar Fabric v2

## Host resource admission (SEC-04)

Executable discovery, pinned signature verification, bootstrap authentication and extension
registration are defined in [NSC startup and regional policy](nsc-and-regional-policy.md).
`.nsc` is a renamed native executable. New UI uses `UiModule` (5); extension kinds 7–12
are append-only, use target TLV 8 and are owned by their session. Existing wire kinds retain
their numbers and field meanings. Peers using new extension kinds require this Host revision;
older peers can continue declaring kinds 1–6.

Registration defaults to at most 4096 items, 256 characters per semantic identifier and
32 MiB of aggregate frame payloads. The initial state snapshot has an independent 32 MiB
budget and must declare exactly the registered state count. Each phase has a 30-second
deadline. Host composition may supply smaller budgets; a peer cannot increase them.
Limits are checked before item decoding or count-based allocation. Invalid, canceled or
over-budget registration terminates the session without publishing a partial cache/mirror.

Exchanges admit and register under the lifecycle lock. Their deadline includes transmission
and response (30 seconds by default). A response-wait timeout retains its stable timeout
result; the cancel notification has an independent 100 ms best-effort budget. Interrupted
frame writes poison the connection. Pre-canceled or write-lock-wait cancellation does not
poison an otherwise unused transport. Every terminal session path rejects admission and
completes all pending exchanges. Stopping the receive loop also ends its session.
Unix listener creation and disposal only touch its private generated socket directory;
the caller's logical pipe name is never treated as a file to delete.

## Purpose

The Sidecar is a dynamic plugin code-execution engine, not a remote object graph or Host UI process.

```text
register once -> execute by ID -> observe through state -> render locally
```

Host owns plugin metadata, capabilities, permissions, registries, local UI modules, resource cache, and state mirrors. Sidecar owns CoreCLR, assembly load contexts, runtime patching where permitted, plugin code, native plugin dependencies, dispatch tables, and private runtime state.

## Session lifecycle

```text
PROCESS_START
  -> HELLO
  -> WELCOME
  -> DISCOVER
  -> VERIFY
  -> LOAD
  -> REGISTER_BEGIN
  -> REGISTER_*
  -> REGISTER_END
  -> READY
  -> ACTIVE
  -> DEACTIVATE
  -> UNREGISTER
  -> SHUTDOWN
```

Plugin construction is lightweight and cannot call Host capabilities. Registration declares services, commands, queries, states, events, UI modules, resources, permissions, and health. Activation starts runtime behavior.

## Planes

Control plane messages include handshake, registration, unregistration, version negotiation, health, reload, crash, error, and shutdown.

Data plane messages include command/result, query/result, state delta, event, and stream. Message numbers and frozen semantics are append-only after protocol v1 release.

## Frame and codec

Every frame carries protocol version, message type, flags, correlation ID, and payload length. The payload codec supports optional fields, unknown-field skipping, schema generation, forward/backward compatibility, and low-allocation decoding.

Registration establishes session-local numeric contract IDs (per-kind ordinals); the data plane executes by contract ID, and semantic strings ride only registration, diagnostics, and tracing. Registration closes with a state snapshot (BEGIN/ITEM*/END) that the host validates completely — coverage, duplicates, codec shapes — commits atomically into the typed mirror, and acknowledges with READY, so reconnect replaces the mirror atomically. State values are typed by a frozen codec registry (UTF-8 string, Bool, Int32, Int64, Float64, Bytes, generated DTO blob); unknown codecs are rejected at registration. UiModule and Resource declarations carry content inline with SHA-256 hashes; the host caches them content-addressed, and registered pages open with zero IPC. CANCEL carries a correlation ID so host-side cancellation aborts sidecar operations.

JSON is allowed for manifests, diagnostics, and debug dumps. It is forbidden on the production data-plane hot path. A fixed, non-extensible CLR struct layout is also forbidden as the wire ABI.

## Dispatch

Source generation produces registration, codecs, numeric IDs, local stubs, and static dispatch tables. Runtime hot paths do not use `Type`, `MethodInfo`, `Activator`, reflection invocation, or repeated string dictionaries.

Semantic identifiers are stable across builds. Compact runtime IDs are negotiated per Host/Sidecar session and do not need to survive reconnect.

## Transport

The first transport is reliable local IPC: named pipes on Windows and Unix-domain sockets on Unix. Transport remains replaceable behind the protocol. Shared memory or a ring buffer is introduced only after benchmarks demonstrate a need, primarily for high-frequency streams.

## Correctness requirements

- commands and queries carry correlation, timeout, cancellation, and a stable error model;
- queues are bounded and expose backpressure;
- replaceable state updates may coalesce to the newest value;
- events are not silently coalesced;
- renderer state reads and registered UI opens perform zero IPC;
- shutdown is ordered and cannot block the UI thread;
- a Sidecar crash cannot terminate the Host.

## Reconnect

Reconnect starts a new session: handshake, complete registration, state snapshot, then activation. The Host marks old mirrors stale/unavailable, keeps locally registered UI renderable, rejects commands with a clear unavailable result, and replaces the mirror only after the new snapshot is coherent.

## Compatibility surfaces

Sidecar protocol, Plugin SDK, Plugin API, manifest schema, package format, Plugin UI IR, PXML language, and XSR product version are independent version axes. None may be inferred from another.


## Capability fabric target design

The following sections consolidate the earlier draft. They define future fabric extensions;
current implemented registration and wire compatibility rules above remain authoritative.
See [Capability fabric](capability-fabric.md) for provider resolution and permissions.

# 2. Transport topology

Physical transport is Host-centric:

```text
Sidecar A ─┐
Sidecar B ─┼── Host
Sidecar C ─┤
Sidecar D ─┘
```

Sidecars do not exchange private endpoint information.

A request from one Sidecar to a Capability provided by another Sidecar is sent to Host, resolved by the Capability Fabric, and routed to the active Provider Session.

---

# 8. Capability registration

A Sidecar may register one or more provided Capabilities.

Conceptually:

```text
REGISTER_CAPABILITY
{
    CapabilityId
    Version
    Cardinality
    ProviderMetadata
}
```

A Sidecar registration must not silently provide Capabilities absent from static discovery policy unless Host policy explicitly permits runtime extension.

Capability ownership becomes active only after the registration transaction commits.

---

# 11. Nested Providers

A Sidecar may host nested providers.

Primary example:

```text
Nexa.Plugin.Sidecar
├─ Plugin A
├─ Plugin B
└─ Plugin C
```

Nested provider identity is explicitly registered.

Conceptually:

```text
ProviderAddress
{
    SidecarSessionId
    NestedProviderId?
}
```

A nested Plugin must receive its own:

```text
Capability ownership
permissions
State ownership
diagnostics attribution
contract namespace
```

The Plugin Runtime Sidecar must not flatten every Plugin into one indistinguishable provider.

---

# 16. Cross-Sidecar Capability dispatch

A Sidecar Consumer never addresses another Sidecar by PID, Pipe or Session ID.

It sends a Capability request to Host.

Conceptual route:

```text
Consumer Sidecar
→ CapabilityId
→ ContractId
→ Host Capability Fabric
→ active Provider
→ Provider Session
→ Provider RuntimeId
```

Host performs:

```text
caller validation
permission authorization
Capability resolution
Provider health check
correlation translation
timeout binding
cancellation binding
backpressure
```

before forwarding.

---

# 19. Availability

Provider availability is independent from last-known State value.

If a Sidecar disconnects:

```text
State value
→ may remain last-known

State availability
→ stale / unavailable
```

Renderer and Services must not treat stale values as active provider truth.

---

# 24. Health

Sidecar Session health includes:

```text
process alive
transport alive
registration state
queue health
heartbeat/health state
crash count
activation duration
last failure
```

Health is visible to Sidecar Supervisor and diagnostics.

Health reporting does not itself create a business Capability.

---

# 29. Compatibility axes

The following versions are independent:

```text
Sidecar Protocol
Capability contract
Plugin SDK
Plugin API
Plugin Package
Plugin UI IR
PXML language
Host product version
DRM grant format
```

None may be inferred from another.

---
