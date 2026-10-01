# Signed release admission

This contract adds a signed `Nexa-Release.json` to complete future distributions.
Historical releases keep their existing package/signature layout; an absent manifest
cannot authorize the future automatic updater. The UTF-8 JSON manifest is signed independently
with the pinned publisher key, after exact package-set validation and hashing.

Schema 1 binds `product: nexacl`, canonical dotted `version`, its exact derived `channel`
(`stable`, `alpha`, `beta`, `ci`), `runtimeVariant: nativeaot-self-contained`,
`configuration: Release`, and exactly the eighteen current package assets. Each asset
binds a basename, RID, packaging format, actual positive byte length and SHA-256.
The variant describes the native host/JVM-host distribution; it does not assert that a
CoreCLR Sidecar runtime is present. It is separate from the older patch planner's
SelfContained/NoRuntime normalization and must not pass through that permissive aliasing.

No URLs, staging paths, executables or caller-provided tool paths grant authority.
Package names must match the version, one of the six RIDs and that RID's supported
formats exactly. Reject duplicate/unknown/missing fields, assets and case aliases.
Manifest and signature inputs are each bounded to 1 MiB actual bytes. Copy input bytes
before verification and parse those same owned bytes only after publisher verification.
Do not reserialize JSON to establish signature identity.

Release admission takes the locally trusted installed identity, selected release
channel/RID/variant/configuration and highest accepted release version. Candidate
version must strictly increase beyond both installed and highest accepted versions;
same version and downgrade are refused. The signed release channel describes the
artifact's stage, not an unsigned discovery-feed alias. A Beta/Alpha feed may route a
stable release under an explicit trusted product policy; it must permit the exact signed
stable channel rather than relabeling manifest bytes. CI manifests are published for independent
verification, but CI hashes have no chronological order and cannot enter automatic
replacement. Channel changes and repairs require a separate explicit product policy.
Package bytes must match the admitted asset's exact length and hash, including actual
stream overruns, before extraction. Neither manifest admission nor hash verification
alone authorizes execution, elevation, deletion or replacement.

## Delivery boundary

The publisher generator, independent distribution validation and runtime admission
primitive must share the schema and package-set contract. Runtime admission is intended
for independent use inside the protected helper. Desktop discovery remains a manual
installer/download flow until an authenticated object-bound helper is implemented.
The highest accepted identity must ultimately live in protected helper storage; a
caller-owned preference is not anti-replay authority. Previous signed inventory remains
separate deletion authority. The updater must still meet
[update-privilege-boundary.md](update-privilege-boundary.md); ApplyPlan stays fail-closed.

OS trust signatures, trusted key rotation, protected handoff, staging/atomic replacement,
rollback/restart and three-platform replacement-race evidence remain distinct work.
