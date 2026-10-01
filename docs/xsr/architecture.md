# XSR architecture

## Direction

XSR is the long-term architecture of Nexa:

```text
X = execution and exchange runtime
S = business services
R = semantic renderer
```

The implementation is a modular monolith with one deliberate out-of-process boundary for plugin execution. This preserves straightforward deployment and debugging while giving business capabilities explicit ownership. A module may be extracted later only when independent deployment, scaling, or ownership justifies the operational cost.

The legacy system is a source of behavior, algorithms, data formats, protocol knowledge, and verified platform handling. It is not the foundation of the new dependency graph.

## Clean-slate repository rule

`refactor/xsr` contains no legacy source projects, solution graph, native bootstrap, installer, release pipeline, or implementation submodule. The separate `dev` worktree is the reference corpus. XSR code may reproduce verified behavior or algorithms only after ownership and contracts are documented; it must not reference or copy the legacy project graph.

The first project in each family is created only when a closed migration unit needs it. Empty placeholder projects and broad compatibility assemblies are not architecture progress.

## Runtime shape

```text
Nexa.Desktop (composition root)
  ├─> Nexa.Services.Composition ──> Nexa.Services.*
  │                  └───────────> Nexa.Xsr.Runtime
  ├─> Nexa.UI.Next
  └─> platform backends

Nexa.Services.* + Nexa.Xsr.Runtime + Nexa.UI.Next
  └─> Nexa.Xsr.State / abstractions ──> Domain / Contracts / Core

        Host process                    Plugin process
  Nexa.Xsr.Runtime + UI.Next  <------>  Sidecar Fabric v2
```

X coordinates service registration, command/query routing, state, events, scopes, scheduling, capabilities, Sidecar sessions, and diagnostics. It does not own business rules or presentation truth. Two distinct scope abstractions exist by design: event ordering scopes bound and order event delivery inside the router, while runtime lifetime scopes (`IXsrScope`) form a disposal tree so a plugin, window, Sidecar session, or service group can be torn down atomically.

## Target project families

| Family | Responsibility |
|---|---|
| `Nexa.Core`, `Nexa.Domain`, `Nexa.Contracts` | portable primitives, domain rules, and stable cross-module contracts |
| `Nexa.Xsr.*` | runtime abstractions, routing, state, transport, diagnostics, and generated code |
| `tools/Nexa.Xsr.Patch.Compiler` | independent managed compile-time source rewriting tool; no product assembly reference or runtime code generation |
| `Nexa.Services.*` | business capabilities grouped by change ownership; `Nexa.Services.Composition` is the explicit edge that binds those capabilities to Runtime routers |
| `Nexa.UI.Next` | canonical semantic renderer |
| `Nexa.UI.Next.Backend.*` | platform rendering, windows, native input, IME, clipboard, and accessibility bridges |
| `Nexa.Pxml.*` | authoring language, compiler, IR, generators, and runtime loading |
| `Nexa.Sidecar.*`, `Nexa.Plugin.Sidecar` | protocol, transport, and dynamic plugin execution |
| `Nexa.Desktop` | process bootstrap and composition root |

`Nexa.Sidecar.Protocol` and `Nexa.Sidecar.Transport` live in this Host repository. The `Nexa.Plugin.Sidecar` executable lives in the independent Nexa.Plugin XSR repository and consumes released protocol surfaces; neither repository uses a workstation-relative source reference to the other.

The target list is not a requirement to create an empty project for every name. A project is introduced only when it has a clear owner and a dependency boundary worth enforcing.

PXML-visible control models are owned by UI.Next in an explicitly configured catalog directory, not by `Nexa.Pxml.Compiler`. `Nexa.Pxml.Generators` consumes that directory as build-time `AdditionalFiles` and expands the complete, validated catalog into compiler-generated source before semantic compilation. The compiler consumes only the generated table and generic typed-value rules; runtime never reads catalog files or performs reflection binding.

## Communication model

XSR has four distinct primitives:

The PXML compiler produces a host-internal IR (`PxmlHostIr`): validated semantic IDs, typed presentation values, and binding records. It is deliberately not the Plugin UI IR v1 stable ABI. Plugin UI IR v1 is a separately versioned surface — format version, schema version, unknown-field skipping, resource references, serialization, compatibility, and security validation — delivered with the Plugin SDK; the compiler will gain that output alongside the host-internal one, and nothing in the current IR is frozen for plugin consumption.

- Command: request an action. It is asynchronous and may be accepted before business completion.
- Query: request a one-time result. It is asynchronous, cancellable, and absent from render paths.
- State: represent a durable current fact. It is the renderer's primary input.
- Event: represent a transient fact that already happened. It never substitutes for current state.

Development identifiers may be readable strings. Source generation resolves them to compact, stable runtime IDs for hot paths. Reflection and string dispatch are not runtime routing mechanisms.

The initial Wave 1 registry treats semantic IDs as opaque, case-sensitive values. After registration closes, it assigns contiguous nonzero runtime IDs by sorting the complete semantic-ID set with ordinal comparison. The sealed mapping is immutable; numeric lookup, rather than string lookup, is the runtime hot path. Generated code may cache these resolved IDs without owning a second registry.

Command and query routes are registered through closed generic adapters and sealed before use. Commands separate immediate route acceptance from asynchronous handler completion, and every completion is observed even when the caller does not await it. Queries are asynchronous, cancellable, correlated, and may apply an explicit timeout. Runtime failures cross the router boundary as stable XSR error codes rather than leaked handler or transport exceptions.

The sealed registry is part of the XSR abstractions kernel: routing, state, events, transport, and generated code resolve the identical deterministic ID mapping. State entries declared through the store follow the same rule — typed cells and ordered collections carry a monotonic revision and availability separate from the last value, collection deltas apply only against a matching base revision, and derived entries recompute only after an input revision changes.

## Ownership and composition

The service-boundary repair moves portable Minecraft identity, launch-plan and manifest
models into Domain while preserving their semantic contracts. The private JVM bootstrap
codec is owned by Contracts; the native JVM Host must not reference business Services.
Recovery storage/provenance is a lower module than installation and instance management.
Minecraft-specific capability adapters belong on the Minecraft/composition side; the
generic capability module must not invoke launch or installation coordinators.

Empty source projects are rejected by the architecture gate. Platform process, Java
discovery, secure-storage and restart implementations live behind portable platform
interfaces. UI shutdown signals cancellation immediately and exposes asynchronous joining;
it never blocks the render thread on command completion or background work. Architecture
checks must inspect type ownership as well as project references.

Business implementations are extracted into Common, Accounts, Settings, Capabilities,
Minecraft metadata/Java/installation/management/process/launch, Resources, Rollouts,
Updates, Telemetry and Setup assemblies. Their immutable messages and state declarations
live in corresponding `.Contracts` assemblies. The original `Nexa.Services` assembly is
an explicit compatibility/composition facade with type forwards, not a dependency of any
implementation module. Minecraft capability providers bind above those modules; generic
Capabilities does not reference Minecraft implementations. Recovery storage sits below
Java, installation and management. Desktop UI may reference contracts and Runtime adapters,
while concrete business construction is confined to Services.Composition.

- Services own business behavior and publish state/events.
- `Nexa.Services.Foundation` owns Foundation route IDs and typed handler factories; the separate
  `Nexa.Services.Composition` project owns Runtime-router registration so service assemblies do
  not reference `Nexa.Xsr.Runtime`.
- The state store owns observable system facts and their revisions.
- Renderers project state and emit intent without becoming a second source of truth.
- Platform projects own OS-specific implementations behind portable contracts.
- Desktop owns startup order and dependency composition, not business behavior.
- Sidecar owns plugin code execution and private plugin runtime state; Host owns the local mirror, UI module registry, permissions, and presentation continuity.

## Operability

The runtime must make `SessionId`, plugin identity, correlation ID, command/query ID, duration, queue depth, state coalescing, restarts, and activation time observable. Cancellation, backpressure, shutdown, crash recovery, and reconnect are correctness requirements, not later performance work. Wave 1 exposes these through bounded `XsrSessionTrace` diagnostics: every subsystem observation lands in one trace per session, correlation IDs are preserved wherever the subsystem contract carries them, and the trace observes behavior without changing it.


## Capability fabric target design

The following sections consolidate the earlier draft. They define future fabric extensions;
current implemented registration and wire compatibility rules above remain authoritative.
See [Capability fabric](capability-fabric.md) for provider resolution and permissions.

# 7. Capability as the dependency unit

Sidecar deployment identity and business dependency identity are separate.

Consumers should depend on:

```text
Capability
```

rather than:

```text
specific Sidecar implementation
```

Preferred:

```text
requires nexa.plugin.key.issue
```

instead of:

```text
requires com.pclnexa.cloud
```

A direct Sidecar dependency is allowed only when an implementation-specific coupling is intentional and documented.

The complete Capability model is defined by `capability-fabric.md`.

---

# 8. Multi-Sidecar model

The previous assumption:

```text
one deliberate out-of-process boundary
for plugin execution
```

is superseded.

The architecture now supports:

```text
N concurrent Sidecar Sessions
```

Each Sidecar:

- is independently discovered;
- has its own identity and version;
- negotiates protocol compatibility;
- declares provided and required capabilities;
- registers runtime contracts;
- owns private runtime state;
- can fail and restart independently.

One Sidecar crash must not terminate:

```text
the Host
unrelated Sidecars
unrelated Host capabilities
```

---

# 9. Plugin execution boundary

Managed third-party plugins execute in one or more:

```text
Nexa.Plugin.Sidecar
```

processes using CoreCLR.

Default topology:

```text
one CoreCLR Plugin Runtime
→ many plugins
→ one collectible ALC per plugin
```

Optional future isolation may start additional Plugin Runtime Sidecar instances.

ALC provides:

```text
dependency isolation
version isolation
unload
reload
```

ALC is not a security sandbox.

Crash isolation is provided by the Sidecar process boundary.

---

# 10. Plugin provider identity

Plugins hosted in `Nexa.Plugin.Sidecar` remain independent Capability Providers.

Their logical identity is:

```text
SidecarSessionId
+
NestedProviderId
```

For example:

```text
Session 18
NestedProvider com.example.worldmap
```

Capability ownership, permissions, diagnostics, State ownership and fault attribution must be associated with the nested plugin provider rather than only with the Plugin Runtime process.

---

# 11. DRM trust boundary

DRM-protected plugin plaintext must remain inside the Plugin Runtime Sidecar.

The Host must not receive:

```text
plaintext plugin assembly
directly usable content-encryption key
decrypted plugin package
```

NexaCloud acts as an entitlement/key-issuance Capability Provider.

Plugin Runtime acts as:

```text
package verifier
decryptor
CoreCLR execution boundary
ALC manager
```

The Host only brokers the Capability request.

Detailed DRM flow belongs to the Capability Fabric and Plugin Runtime specifications.

---

# 12. Sidecar independence

Sidecar implementation language is not part of XSR semantics.

A Sidecar may be implemented using:

```text
NativeAOT
CoreCLR
Rust
C++
other runtimes
```

provided it:

- can be launched by the Host;
- supports an approved transport;
- implements the Sidecar Protocol;
- passes trust and permission policy;
- obeys Capability contracts.

---

# 13. Dynamic discovery

The Host may dynamically discover Sidecar packages.

Discovery does not imply automatic execution.

The lifecycle is conceptually:

```text
discover
→ verify
→ resolve dependencies
→ activate when policy requires
```

Activation policies may include:

```text
AutoStart
OnDemand
Lazy
Manual
```

Detailed discovery, dependency and provider semantics are defined in `capability-fabric.md`.

---

# 16. Security boundaries

The architecture distinguishes:

```text
process isolation
runtime isolation
capability authorization
DRM
server-side authority
```

These are not interchangeable.

NativeAOT may increase reverse-engineering cost but is not a trust boundary.

ALC may isolate managed dependencies but is not a security sandbox.

Client DRM may increase extraction cost but is not the final authority for cloud/commercial capabilities.

Final commercial authorization remains server-side.

---

The current service and platform assembly boundaries are locked by [project-references.json](project-references.json).
The [boundary repair migration](migration-architecture-boundaries.md) records ownership and asynchronous lifetime rules.
