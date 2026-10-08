# Host-owned New UI text cards

The first executable New UI ABI is a versioned text card, not PXML source or the compiler's
internal IR. `UiModule` continues to transfer hashed content at registration. With execution
admission, the whole batch is validated before READY and projected into sealed immutable XSR
State only during an active session. Without admission the existing cache remains inspection
only. UI.Next never reads the session, registry, transport or module runtime.

Payload: ordered unique binary TLV fields 1 schema U32 (=1), 2 host slot Str, 3 title Str,
4 body Str. Unknown fields are skipped. Payload <=4096 bytes; title 1–80 UTF-16 characters,
body 1–512 characters, no control characters. Slot is an exact host semantic ID. Flags and
codec are zero; resources, URLs, actions, bindings and scripts are outside this schema. The
host grants slots explicitly; no plugin entity names are exposed to the product tree.

Limits: 16 host slots, 32 modules per session, 64 active modules total, 8 per slot. The latest
live activation wins for each slot. Retirement restores its predecessor or hides the slot.
Snapshots retain immutable documents. State publications are serialized/coalesced outside
business locks. Activation owns its lease before publishing, including observer reentrancy;
all terminal paths retire it, and a delayed activation cannot resurrect a stopped session.

Desktop grants `ui.resources.extension-card.v1`. It renders a compact title and wrapping body
below the resource toolbar using existing typography and colors, with a bounded scrolling
viewport. The slot occupies no space without a live module. Accessibility and visual text
are literal together. The search, result list, focus, navigation and download actions retain
their host ownership. There is no render-time parsing, IPC, service lookup or plugin code.

## Interactive document schema 2 (XSR-806)

Schema 1 text cards stay compatible. Schema 2 uses the same ordered TLV fields 1 schema U32
(=2), 2 host slot Str, 3 title Str, 4 body Str, and field 5 Bytes containing the node sequence.
The entire payload is limited to 32 KiB. The node sequence begins with a u16 node count and
then u16-length-prefixed ordered TLV node records. Fields: 1 Id U32, 2 Parent U32, 3 Kind U32,
4 Label Str, 5 Command Str, 6 ValueState Str, 7 EnabledState Str, 8 VisibleState Str,
9 Resource Str, 10 Argument Str. Unknown ordered fields are skipped. IDs are contiguous 1–64,
parent zero denotes the slot root, otherwise the parent precedes the child and is a Stack/Row.
Maximum depth is eight. Kinds: Text=1, Button=2, TextInput=3, Toggle=4, Image=5, Stack=6,
Row=7. Label is at most 256 characters, button argument at most 512; controls are rejected.
The constructor copies nodes and snapshots expose read-only collections.

Button, TextInput and Toggle require a Command registered by this same session. Bindings
resolve once to the same session's numeric State contract and typed mirror cell. Buttons and
inputs accept string arguments; toggles require Boolean Command and ValueState codecs and
negotiated BinaryPayloads. Inputs submit through a host-owned submit button, with a 512
character draft. State can update text/input values, toggles, visibility and enabled status.
Unavailable bound values disable interaction. Ancestor visibility/enabled predicates are
rechecked at dispatch. State updates retain entities and uncommitted drafts unless the bound
input value itself changed. There is no parsing, reconstruction or IPC on unchanged frames.

Image nodes refer only to Resource semantic IDs listed in the module's RequiredResources.
Registration requires the hash-verified cache entry and a supported static PNG/WebP/JPEG
resource image, at most 1 MiB encoded and 1024×1024 pixels each. The sum of rendered node
pixel reservations is at most 8 MiB per module, and distinct encoded image content is also
limited to 8 MiB per module. Repeated references share one admitted image carrier across
registration documents. No URL, path or unregistered resource is read.
The immutable local image carrier crosses presentation State; only bytes crossed the peer IPC.
Native decoding stays in the backend with its existing decode-failure handling and cache.

A current module view has a fresh exposure GUID. Replacing or retiring it revokes captured
intents and cancels pending exchanges. Restoring a predecessor creates a new exposure GUID,
so stale intents from its earlier exposure do not regain authority. Session failure,
deactivation and disposal remove the view and unsubscribe live mirror bindings. Source-page
navigation cancels its requests and retires its control entities before the page is exposed
again. Command/query effects use the existing asynchronous numeric data plane, timeout,
cancellation and backpressure; no child delegate or session object enters UI.Next.

The existing resource extension slot is the production consumer for interactive documents.
It supports plugin-authored finite forms and nested layouts while keeping product navigation,
service execution, trust decisions, palette and viewport sizing under host ownership. Arbitrary
scripts, host service bindings and replacing the product tree are outside this Host contract.
The independent Plugin SDK/compiler/package UI IR freeze and real-engine long-duration
compatibility still require the external repository and physical evidence.
