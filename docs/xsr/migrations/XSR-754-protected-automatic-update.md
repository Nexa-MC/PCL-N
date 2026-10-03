# XSR-754: protected automatic update transaction

The machine installer now preinstalls Nexa.Update.Helper. This executable initializes only
the fixed installation, publisher verifier, public GitHub source and transaction policy.
It does not initialize Desktop, user folders, accounts, plugins or Sidecars. Settings requests
OS administrator authorization asynchronously and reads protected progress state. Portable,
custom-path or unsafe installations retain manual installer/download links.

Platform directory leases retain each ancestor and use NtCreateFile-relative operations on
Windows and openat/no-follow descriptors on Unix. Windows creates explicit protected owner/
DACLs; Unix admits UID 0, rejects group/other mutation, links and unexpected ACL grants.
New public payloads receive read/execute permissions; private package/high-water files do not.
Existing unsafe objects are refused, never chmod/ACL-repaired. Fresh Unix objects get explicit
permissions through their descriptors so caller umask cannot hide the published payload.

Publisher manifest and detached signature are fetched independently of discovery. Signed
identity binds product, channel, RID, exact package set, length and digest. Reception verifies
the bytes written to a protected create-new file. Extraction retains descriptor-relative
directories, refuses links/special/duplicate/ambiguous entries, and bounds actual output.
The completed slot is selected by a flushed, framed activation journal. An incomplete last
frame leaves the old activation. High-water advances before activation and never rolls back.

Protected pending state binds the exact authenticated manifest digest and cached package.
An interrupted accepted version can resume only through that evidence; cached bytes are
reverified. Explicit rollback tombstones the resume authority before selecting the old slot.
Old payloads are retained, with no deletion authority over unknown/user/game files. Stable
bootstrap dispatches before application initialization; restart remains unprivileged.

macOS PKG now installs the actual bundle/helper under /Library/Application Support/NexaCL,
with a public /Applications entry. The updater never mutates through the public entry or
trusts group-writable /Applications. Existing conflicting entries require manual removal.
Windows and Linux keep their system locations. All previous installer and portable types
remain available; the helper is required in every release payload.

Managed tests inject interruption after receipt, preparation, high-water, acceptance and
activation; all recover offline, preserve complete activation and reject replay after rollback.
Additional tests reject bad package bytes and interrupted/corrupt journal frames. Native
public-journal tests hold the writer open while reading both progress and completion. Public
Windows journal writers do not request DELETE access, keeping read/write sharing coherent;
the original one-argument read API remains available for binary compatibility.
Native validation also exercises architecture-specific Linux ARM64 open flags and macOS
descriptor security-property queries. An absent ACL property is distinguished from a failed
filesystem query; permission/unsupported errors are never interpreted as absence.
release CI executes a real protected transaction/rollback on each of six RIDs and smoke-runs
the helper, alongside installer, Desktop NativeAOT and trim checks. Native CI results must be
reviewed before acceptance; fixtures are not physical power-loss or OS signing acceptance.
