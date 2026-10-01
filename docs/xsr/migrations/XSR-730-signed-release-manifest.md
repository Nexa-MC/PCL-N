# XSR-730 Signed release identity admission

## Contract

Complete future distributions add `Nexa-Release.json` and its detached pinned-key
signature. The twenty signed files comprise eighteen packages, SHA256SUMS and this
manifest. Schema 1 binds exact version/channel, nativeaot-self-contained host variant,
Release configuration and the complete package set with RID/format/actual length/hash.
It accepts no download URLs or caller-provided paths as authorization. The normative
contract is [release-admission.md](../release-admission.md).

Package-set verification generates the manifest before signing; independent verification
first authenticates every file and then compares the original manifest against actual
package bytes. It does not regenerate a manifest and accidentally bless unsigned data.
Known signature outputs are removed on rerun, all signatures are cleared after failure,
and unknown or missing assets still fail the distribution gate. Historical release
attachments are untouched; their absent manifests cannot authorize a future updater.

`VerifiedReleasePackage` owns manifest/signature bytes before verification, parses the
verified UTF-8 bytes, rejects unknown/duplicate fields and validates all eighteen identities
before selecting a package. It compares canonical version against both installed and
highest accepted version, matches trusted channel/RID/native variant/Release configuration,
and refuses CI candidates. Package verification reads actual bytes with exact length and
SHA-256, stopping on overrun. The admitted object has no mutation setters or public
constructor. It does not grant replacement/elevation or accept caller tool/URL paths.

## Validation and remaining delivery

The generator/validator regressions cover all four channels, all eighteen files,
JSON formatting/order, malformed versions, duplicate/unknown/missing properties/assets,
boolean/float size aliases, version/channel/variant/configuration/RID/format changes,
actual byte modification and the 1 MiB budget. The runtime fixture matches the publisher's
generated contract. All 20 non-GnuPG Python release tests
pass locally. GnuPG integration needs its agent socket, unavailable in this execution
environment; the existing 3 signing integration tests remain enabled in CI and now
include manifest tampering and the forty-file distribution. No signing secret is required
or exposed by the local generator tests.

Runtime tests use the Python-generated embedded fixture and genuine GPG signatures,
with buffer mutation after verification, actual package corruption/underflow/overrun,
channel/RID/format/variant/configuration mismatches, installed/high-water replay,
CI refusal, malformed/duplicate/unknown fields and input budgets. Managed and Linux
NativeAOT Services each pass all 467 tests; Release has zero warnings/errors. The
68-project architecture check and changed-file whitespace check pass. These are
primitive/schema contracts, not real privileged transaction or OS trust evidence.

This unit does not implement protected storage of the highest accepted version,
helper handoff, staging/replacement/rollback or OS native trust. Desktop manual update
discovery is unchanged; the trusted helper must independently invoke admission.
`UpdateStaging.ApplyPlan` remains fail-closed. One immutable admission primitive is added
in the existing Updates assembly; existing APIs retain their behavior.
