# XSR-745: Alpha.6 acceptance ledger

The Alpha.6 review now has a machine-readable, fail-closed ledger at
`eng/acceptance/alpha6-status.json`. It records the nine independently reviewable
workstreams from the 2026-10-02 audit, links only repository evidence, and keeps
physical or credential-dependent work explicitly open. It is a status index, not
a substitute for raw evidence and not a mechanism for converting fixtures into
physical acceptance.

`audit_alpha6.py` validates unique identifiers, known priorities and states,
repository-relative evidence links, and consistency between item and overall
state. An accepted item cannot retain open work, and the overall state can become
accepted only when every item is accepted. `--require-accepted` is deliberately
nonzero today, making it suitable for a release gate without making routine
ledger validation fail.

The original audit found zero fully accepted workstreams. The current ledger was refreshed
on 2026-10-05 against `565e5143`: protected updates, server editing/Join, MRPack export and
the complete Sidecar Host API are delivered implementation slices, while remaining product,
physical and credential-dependent work stays open. See [XSR-795](../XSR-795-unimplemented-inventory.md)
for the deduplicated inventory; the ledger does not equate raw catalog markers with missing
features. In particular,
the 126 Minecraft candidates remain a queue rather than a support declaration;
the retained composition soaks do not become 8-hour native evidence; and GPG
package verification does not become Authenticode, Developer ID, notarization or
a protected update transaction.

No runtime boundary, service dependency, protocol, product behavior or support
claim changes in this unit.
