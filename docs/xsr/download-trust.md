# Minecraft download trust

Authoritative Minecraft metadata comes from recognized Mojang HTTPS origins. The
version manifest is never replaced by a regional mirror. Version JSON is verified
as raw bytes against the manifest entry's SHA-1 before parsing. An asset index is
verified against its version document before its contents select object downloads.
An HTTP redirect does not grant a different origin authority to supply metadata or
installer checksums. Failed authority checks stop installation without publication.

For vanilla client files and libraries, regional mirrors may supply artifacts whose
expected digest comes from authoritative metadata. A hashless client or library
retains its original HTTPS source without regional substitution. This boundary does
not certify every additional loader's remote catalog or installer. Explicit local
installer selection and user-maintained instance
manifests remain supported; local imports are not evidence of Mojang provenance.
An explicitly injected parsed-metadata provider remains responsible for establishing
its own trust. The existing `JsonObject` port cannot represent original byte identity;
the wrapper must not reserialize its output and claim that the new bytes match a
provider's raw-byte digest. Production HTTP metadata always uses raw-byte verification.

Install and launch must share the asset-index integrity facts. Installation must
not subsequently overwrite a verified index with a separate hashless transfer.
Previously cached metadata, installer receipts and interrupted publication records
do not acquire the new trust policy merely because their local bytes are unchanged.
Old incomplete tasks retain their data and rollback/cancel paths; resuming their
publication requires the current policy. Completed and imported instances are not
silently rewritten or presented as having been revalidated.
An old journal already in the authenticated committed phase may verify its published
files and repair a missing terminal marker without publishing any old staged output.

## Update archive modes

Supplied archive and scatter-manifest permissions are limited to existing ordinary
bits within octal `0755`. Setuid, setgid, sticky and group/other write permissions
cannot be propagated through extraction or subsequent mode restoration. This does
not establish a protected staging namespace or complete the privileged updater.
Absent mode metadata retains the existing platform default behavior.

## Audit follow-up

The review of `505b9f9f` is static evidence, not a test result. Its metadata ordering
description requires correction: the current code tries official sources first,
then regional mirrors. Forge/NeoForge `.sha1` requests already start at canonical
origins; redirect handling and cached acceptance still need enforcement. Publicly
distributed client certificates cannot be treated as secret caller authentication.
Certificate rotation and server-side admission/rate limiting require server changes;
the client alone cannot demonstrate that this boundary is fixed.
