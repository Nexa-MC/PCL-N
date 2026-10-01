# XSR dependency rules

Dependencies point toward portable policy and stable contracts. Platform frameworks and composition remain at the edge.

## Allowed direction

| Layer | Dependencies |
|---|---|
| Desktop UI | Contracts, Domain, Core, UI.Next, runtime routers and composition handles |
| Service contracts | Lower contracts, Domain, Core, XSR state/abstractions, Platform.Abstractions |
| Service implementations | Lower service contracts/implementations and portable shared algorithms |
| Services.Composition | Service implementations, XSR Runtime, platform implementations |
| Platform.Runtime | Platform ports, capability contracts and portable shared algorithms/logging |
| Domain | Core |
| Renderer backend | UI.Next and its platform framework |

The exact current direct references are locked in `project-references.json`. `Services` is a
compatibility facade and foundation composition entrypoint. Its consumers in the Desktop
entrypoint are an approved composition edge; Desktop/Ui cannot use concrete service types.
Native process creation, control, key-ring operations and capability probes live in Platform.


References not shown are denied by default for new XSR projects.

## Forbidden dependencies

| Source | Must not depend on |
|---|---|
| `Nexa.Domain` | Desktop, Application, renderer, Avalonia, concrete platform implementations |
| `Nexa.Xsr.*` core projects | Desktop, legacy Application, Avalonia, concrete services, renderer implementation/backend |
| `Nexa.Services.*` | Desktop, legacy Application, ViewModels, Avalonia, UI.Next, renderer backends |
| `Nexa.UI.Next` | Desktop, legacy Application, concrete services, Sidecar implementation, Avalonia |
| `Nexa.N.Plugin.*` public SDK | Host internals, Desktop, Application, concrete Platform, UI.Next internals, Avalonia, Sidecar implementation |
| Sidecar protocol/transport | plugin business assemblies and Host UI types |

`Nexa.Sidecar.Protocol` has no dependency on product Core, Contracts, or XSR assemblies. `Nexa.Sidecar.Transport` depends only on Protocol. Both sides of the process boundary consume these independently versioned surfaces without exchanging Host CLR objects.

`Nexa.Services.Foundation` must not reference `Nexa.Xsr.Runtime`. Its route IDs and handler
factories remain portable service contracts. `Nexa.Services.Composition` is the narrow approved
edge that takes a `FoundationHost`, registers those factories in the Runtime builders, and
returns the sealed command/query routers to Desktop.

`Nexa.UI.Next.Backend.Avalonia` is the only new-architecture project family allowed to expose Avalonia implementation types, and those types must not leak through UI.Next public contracts.

`Nexa.Pxml.Generators` is a build-only Roslyn component with no product-project reference. `Nexa.Pxml.Compiler` consumes it as an analyzer and receives UI.Next-owned control descriptors only through the configured `AdditionalFiles` catalog; generated code may use the compiler's existing UI.Next contract reference but the generator assembly must not reference UI.Next or Compiler.

## Enforcement

The actual reference graph is checked for cycles and empty projects. Portable contracts cannot
reference service implementations. Symbol-based checks resolve aliases and type forwards when
checking UI ownership, renderer leakage, blocking APIs and platform process creation. See
[migration-architecture-boundaries.md](migration-architecture-boundaries.md).

The initial project graph is defined in [migrations/XSR-002-project-graph.md](migrations/XSR-002-project-graph.md). `Nexa.Xsr.ArchitectureTests` scans every project, rejects unregistered or external project references, enforces the locked direct-reference graph, verifies generator and executable roles, and prevents Avalonia packages outside the backend.

The migration-branch CI builds the full solution, runs the architecture executable, validates the selected XSR product version during build, publishes and executes the NativeAOT Desktop `--validate-shell` smoke path, and publishes a trimmed Desktop composition. Source analyzers follow as compilable APIs are introduced.

The gate applies these rules strictly to new project families; there is no legacy-project exception list on this branch. Expanding an allowed set to acknowledge a new dependency is an architecture decision and requires a matching document change.

Roslyn analyzers will add source-level diagnostics for forbidden namespaces, synchronous waits, reflection dispatch, and unstable plugin APIs. Once source exists, architecture tests are mandatory even before the full analyzer set is complete.
