# Publisher signatures for Nexa distributions

All complete distributions (CI and tagged releases) must contain the exact eighteen
packages, SHA256SUMS, Nexa-Release.json, and a detached armored signature for each of those twenty
files. Signing follows package-set validation. No complete distribution or release
upload runs if signing or independent verification fails.

The signing identity is the exact signing-key fingerprint
`5701218D69B531E1A7ED35BB6E31F5974A273AEE`, shared with UpdateGpgVerifier. GnuPG must
select this key explicitly, not an automatically selected subkey. The private key
and passphrase come from existing repository secrets through stdin in an isolated
temporary keyring. Verification uses a separate public-only keyring populated from
the checked-in GPG-PUBLIC-KEY.asc and checks the actual signing fingerprint.

These signatures establish publisher authenticity only when verified against the
pinned key. They do not provide Authenticode, Developer ID or notarization. The
current Cloudflare/browser download flow does not yet verify package signatures;
GitHub exposes the signatures for independent verification. An automatic updater
must verify signatures before applying files and cannot treat TLS or SHA256SUMS
alone as publisher authorization.

Runtime admission follows [update-signature-policy.md](update-signature-policy.md):
binary-document signatures, SHA-256/384/512, known revocation and expiry from the
trusted embedded keyring, authenticated signature expiry, and bounded input and
decompressed envelopes. The current SHA-256 publisher command stays compatible.
This does not complete signed release routing, replay policy or protected replacement.

`Nexa-Release.json` binds the canonical version and derived channel, native host runtime
variant, Release build configuration and each package's RID, format, exact length and
SHA-256. Independent distribution verification checks the signed manifest against the
actual eighteen files without regenerating the signed input. See
[release-admission.md](release-admission.md). Historical releases retain their previous
layout; missing manifests cannot authorize the future automatic updater. The runtime
admission primitive verifies owned original bytes, complete asset identity, installed/
high-water version constraints and actual package bytes. Protected helper consumption
and durable anti-replay state remain separate delivery steps.
