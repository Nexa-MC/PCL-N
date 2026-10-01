# XSR-728 Detached signature admission

## Contract

The existing exact signing fingerprint remains pinned. Admission accepts only binary
document signatures using SHA-256/384/512, rejects known revocation/current key expiry
from the trusted composition-root keyring, and evaluates authenticated signature expiry
after verification. Absent/zero expiry preserves historical non-expiring signatures.
Unhashed timing fields do not change admission. Input and decompressed envelopes each
have a 1 MiB actual byte budget; payload verification stays streaming. Cancellation
remains cancellation and expected parser/crypto failures become InvalidDataException.

The production publisher command remains compatible. No new assembly, public API,
key selection or unrelated key-size restriction is introduced. See
[update-signature-policy.md](../update-signature-policy.md) for the trusted-keyring
assumption and explicitly separate release identity/protected replacement boundary.
The earlier XSR-511 note now accurately distinguishes primitives from an unwired updater.

## Verification

Generated genuine signatures cover the three accepted hashes, rejected SHA-1/text,
historical non-expiry, key expiry/revocation, signature expiry, forged unhashed timing,
compressed input, actual decompressed padding, malformed packets and cancellation.
Managed Services passes 463 tests, including 5 GPG tests; Release has zero warnings/errors.
Linux NativeAOT Services also passes all 463 tests, including the same GPG controls;
the 68-project architecture check and changed-file whitespace check pass. Independent
read-only candidate review found no concrete remaining bypass in this scope; its isolated
malformed-packet probe is supporting evidence, not exhaustive parser verification.

Signed release routing/replay, protected staging/replacement/rollback and OS signing
remain open. This unit does not authorize automatic update replacement.
