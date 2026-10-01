# Architecture boundary repair

This migration resolves the dependency and lifetime defects from the architecture review.
The public namespaces remain compatible through type forwarding; assembly ownership now
follows actual service boundaries rather than namespace conventions inside one assembly.

## Ownership

- Core owns path identity and containment checks. Domain owns Minecraft value models used
  by multiple modules, including version, loader, library and launch-plan data.
- Contracts owns the bounded native JVM bootstrap codec. Nexa.Jvm.Host references this
  assembly without loading service implementations or runtime dispatch.
- Each service publishes a separate Contracts assembly. Implementation assemblies may
  reference lower contracts; contracts may not reference implementations.
- Recovery.Storage owns recovery authority, operation exclusion and blob storage shared
  by installation and instance management. Metadata owns shared manifest algorithms.
- Platform.Abstractions owns process execution and secure-storage ports. Platform.Runtime
  implements OS key rings, process creation/control/sampling, Java discovery, native window
  detection and machine capability probes. Capability rules consume contracts; Minecraft
  adapters are composed above the generic capability engine.
- Services remains a compatibility facade and foundation composition entrypoint.
  Services.Composition owns router registration, network pools and update-policy lifetimes.
  Desktop UI consumes contracts; its entrypoint wires composition and presentation.

## State and lifetime

The install draft is a service-owned immutable snapshot, updated through an explicit command.
Addon compatibility is normalized in the service. Desktop has read-through projection helpers;
these helpers own no selection dictionaries or addon sets.

Launch progress includes the instance and root identities. Desktop derives busy state from
this snapshot and the pending command completion. Navigation and visual state remain in UI.
The main launch controller is divided into installation, account, launch-flow and styling files.

OS account-key initialization is asynchronous. A pending or unavailable store rejects writes;
reading errors never permit overwriting the original file. Linux secret-tool pipes are bounded,
concurrently drained and awaited with cancellation and a deadline. Keys retained for local
cryptography are zeroed when the account lifetime ends.

Java discovery caches belong to a host-scoped locator. Host consumers share that locator,
and a successful managed install invalidates it. A scan that predates invalidation cannot
repopulate the cache. There is no second provider cache hiding invalidation.

UI/recovery Dispose cancels and returns without waiting. DisposeAsync joins the owned work.
The application session starts its GUI portion on an explicit STA thread, then awaits teardown
once the GUI lifetime ends. Process pruning retires exited sessions asynchronously.

## Architecture lock and validation

`project-references.json` locks the explicit project matrix. Architecture tests inspect actual
project edges for cycles and reject empty source projects and contract-to-implementation edges.
Roslyn symbol checks resolve aliases and type forwards when rejecting concrete service types
in Desktop/Ui, renderer types in Services, blocking task/IPC waits, and process creation outside
Platform. Build the solution before running semantic tests, because they resolve built assemblies.

The existing sidecar protocol and signed executable discovery are unchanged. The capability
fabric document describes a future target; its protocol sections do not replace the implemented
wire contract. There is one architecture document and one protocol document; superseded
updated copies and empty future scaffolding projects have been removed.

## Verified migration results

- The complete 68-project solution builds with zero warnings and errors.
- Services: 431 passing tests; Desktop: 100; PXML: 40; architecture checks: all 68 projects.
- Runtime: 98 passing tests; sidecar protocol: 20. The four OS IPC integrations were
  explicitly skipped because the execution environment denies Unix socket creation;
  executable-to-host IPC must still run in the supported-platform CI jobs.
- Linux x64 native JVM host publishes with NativeAOT. The published executable loads
  a real JVM and completes a minimal main class; missing-runtime and malformed-bootstrap
  cases exit safely without exposing private arguments.
- Linux x64 Desktop publishes with trimming enabled. Its published executable completes
  `--validate-shell`, composes the services and validates 52 semantic nodes before shutdown.
- Windows/macOS key-ring, process and GUI behavior remains covered by contract/fake tests
  here; native integration for those systems requires their platform CI runners.
