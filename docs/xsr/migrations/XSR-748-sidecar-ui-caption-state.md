# XSR-748: Sidecar UI caption State adapter

UI Patch has an initial caption schema and explicit host target grants, documented in
`../sidecar-ui-patches.md`. A session activation owns its caption lease; registration,
global/per-target budgets and cancellation are checked before publication. Captions are
projected through sealed immutable XSR State, with coalesced serialized updates and no
observer callbacks under the patch business lock. Last activated live patch wins;
deactivation and terminal paths restore the earlier patch or original caption.

The resource search button is the first composition adapter. Its caption and accessibility
label change on the render thread while the original action, focus and entity identity remain.
Patch text is literal; retirement restores normal localization. No account, launch safety or
update authority message can be patched through this grant.

Tests cover registration atomicity, malformed/unknown fields, grants and limits, immutable
retained snapshots, precedence/retirement/reactivation, no-allocation caption reads,
a blocked observer racing a later activation, and an observer disposing the activating session.
The real resource-page test verifies caption/accessibility changes, unchanged action and
restoration. Architecture gates keep execution registries outside the canonical renderer.

New UI rendering and non-caption properties remain incomplete adapters; this unit does not
promote the whole Sidecar execution platform or Alpha 6 physical matrix to accepted.
