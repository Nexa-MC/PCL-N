# XSR-807 — Local modpack file activation

2026-10-08 (Asia/Shanghai). User file associations target the existing confirmed modpack
import path for `.mrpack` and `.nexapack`; activation never performs an unattended install.
`.nexapack` is explicitly an extension alias for ZIP containers with an existing recognized
Modrinth or CurseForge manifest. It does not introduce an invented Nexa archive format;
unknown/missing/conflicting manifests remain rejected by the same archive validator.

The single-instance mailbox keeps version 1 destination-only peers compatible. Version 2
contains a destination byte, little-endian unsigned 16-bit UTF-8 path length, and a bounded
absolute local path (1–8192 encoded bytes). Control characters, malformed UTF-8, relative
paths, URI payloads, unsupported extensions and Windows UNC/device paths are rejected. Both
versions share the existing current-user pipe, bounded 16-item queue, shutdown retry and
receipt handshake. Queue admission acknowledges delivery for UI confirmation, not completed
installation. No arbitrary shell command or file-content payload crosses this boundary.

File activations restore the existing window. The integration queues them until a real
renderer frame and calls the existing drop inspector, which displays a confirmation dialog
before any import/install command. First-run setup retains file activations for the deferred
restart after releasing its single-instance lease. Disabling single-instance mode still
uses the same confirmation path for original process arguments.

Validation covers UTF-8 byte bounds and local path admission, version 1 compatibility,
version 2 forwarding from an actual peer process, renderer-frame deferred activation and
the existing explicit import confirmation boundary. Central build, AOT/trim and native
association evidence are recorded by the root integration validation.
