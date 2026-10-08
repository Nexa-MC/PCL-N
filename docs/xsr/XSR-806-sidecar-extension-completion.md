# XSR-806 — Sidecar extension completion

2026-10-08（Asia/Shanghai）。范围为 PCL-N Host；独立 Nexa.Plugin SDK/执行引擎和实机插件长期验收不属于仓库内实现。

## Boundary lock

The existing string Function Patch and text-card documents remain compatible. New Function
Patch ABI declarations explicitly name argument and result codec shapes. Bounded local bytecode
executes the same five phases against owned primitive values, with no reflection or runtime IPC.
The compile-time rewriter emits typed wrappers and cached delegates for supported primitive
signatures; unsupported generic, ref/out, caller-info, arbitrary CLR and async-void signatures
remain compile errors. Host points bind once to numeric indices, and host composition grants
exact targets. Cancellation and session retirement apply before publishing patch effects.

Interactive UI uses a versioned, finite document schema: text, action, text input, toggle and
image nodes inside a host-owned slot. Layout and styling stay under host ownership. Commands
and live values resolve at registration to the same session's numeric Command/State contracts.
No host service identifiers, arbitrary PXML, script, filesystem paths or network image URLs are
admitted. Images reference hash-verified registered resources, under byte and decoded-pixel
budgets. UI.Next only reads the resulting immutable presentation State and emits ordinary
intent. Desktop translates that intent to an asynchronous host dispatch; it does not place
session objects or child delegates in renderer components.

Each activation owns a distinct presentation identity. Deactivation, peer failure, disposal,
replacement and retired source views revoke intents and cancel pending dispatches. Live State
changes publish coalesced immutable views outside business locks. Repeated paint frames cannot
cause IPC, document parsing or entity reconstruction. Admission validates the whole document,
all bindings and resources before READY; partial invalid documents cannot expose interactive
controls.

## Validation

Contract and compiler tests accompany the implementation. Root integration records managed,
architecture and NativeAOT/trim results after all parallel work is combined. Real independent
plugin engine compatibility and multi-platform long-duration plugin interaction remain external
evidence, not inferred from a host fixture.

## Delivered contracts and consumers

- NFP2: String/Boolean/Int32/Int64/finite Float64 values, zero to eight arguments, primitive
  or Void result. Typed straight-line load/store/add/equality/not/skip/end instructions run
  locally through numeric points. Overflow rolls back one program and faults its activation;
  cancellation, reentry and concurrent retirement preserve the original host body boundary.
- Compile-time wrappers: static nongeneric primitive signatures in the established call-free
  return/if/block/expression subset. The compiler generates cached span delegates and a
  direct-original inactive path. General CLR objects, arbitrary method invocation, async,
  ref/out, iterator, reflection or generated remote delegates are not ABI contracts.
- Semantic binding: UI Command/State/resource names resolve once against the same session's
  accepted registration and verified cache. Runtime requests carry the existing numeric
  contract IDs. This is explicit session contract binding, not arbitrary C# semantic-model
  method binding. Wrong kinds/codecs, cross-session/host-service names and unavailable states
  reject admission or disable dispatch.
- UI schema 2: up to 64 nodes, depth eight, 32 KiB payload, nested Stack/Row and text,
  button, input-submit, toggle and static resource-image controls. Verified images are limited
  to 1 MiB and 1024×1024 each, at most 8 MiB reserved pixels and 8 MiB distinct encoded
  image content per module; registration documents share repeated resource image carriers. Live mirror changes
  update immutable views; unrelated frames preserve entities and input drafts.
- Desktop consumes this schema through the existing resource extension slot. The new
  `ui.resource.download-count.v1` Int64 presentation point complements the title point in
  both list and detail views; persisted provider data and executable downloads are unchanged.
- Every winning exposure has a unique GUID. Predecessor restoration assigns another GUID;
  late captured intents do not regain authority. Replacement, session retirement and source
  page navigation cancel pending commands. The UI tree stores only normal presentation data
  and semantic intent, never peer session objects.

Six Runtime integration cases cover typed phases and rollback, malformed shapes and retired
returns, numeric command/live-state exchange, unauthorized bindings and image payloads,
resource ownership/pixel budgets, and replacement/source cancellation. Two Desktop cases
exercise generated primitive presentation and real renderer input/toggle/submission/retired
source consumption. Compiler fixtures compile and execute Int32+Int64, Boolean, Float64 and
zero-argument Void wrappers. Final consolidated build and AOT evidence is recorded in
[XSR-820](XSR-820-completion-closure.md).
