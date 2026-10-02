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

Interactive pages, broader layout/node schemas, resource images and live plugin state bindings
remain separate work. This contract must not be described as arbitrary New UI support.
