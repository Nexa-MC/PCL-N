# Detached signature admission

The verifier authenticates bytes with the exact pinned signing-key fingerprint.
Only binary-document signatures and SHA-256, SHA-384 or SHA-512 are admitted. The
current publisher command (`--digest-algo SHA256 --armor --detach-sign`) is retained;
no unrelated key-size minimum or new key selection is introduced in this unit.

The composition root supplies a trusted public keyring, embedded in the installed
application. Fingerprint pinning does not authenticate arbitrary added key policy
packets. Known revocation and current key expiration from that trusted keyring must
reject admission. Offline embedded material cannot learn later revocations without
a trusted publisher update; this check is not an online revocation service.

After mathematical verification, signature expiration is read exclusively from
hashed, authenticated subpackets, with its authenticated creation time. Unhashed
expiration/creation packets cannot extend or shorten the policy. Zero/absent
expiration means no expiry, preserving non-expiring historical signatures.

Armored input and any decompressed signature envelope have a 1 MiB actual byte
budget. Content remains streaming. Expected parser/crypto failures become
InvalidDataException; cancellation remains cancellation. Failure never authorizes
Sidecar launch, update inventory, execution or replacement.

## Separate release and privilege boundary

A detached package signature authenticates package bytes, not independently claimed
version, platform or channel routing. The published signed SHA256SUMS binds its listed
filenames and package hashes, but the current automatic updater does not consume it
as a complete release admission manifest. VerifiedUpdateInventory binds signed
TargetVersion, RuntimeId, RuntimeVariant and Configuration to the installed identity;
it is deletion authority, not authorization for a new replacement transaction.

Signed release identity, channel, exact package lengths/hashes, replay/downgrade
policy and protected helper independent verification remain required by
[update-privilege-boundary.md](update-privilege-boundary.md). ApplyPlan remains
fail-closed. This signature policy does not supply Authenticode, Developer ID,
notarization or a protected staging namespace.
