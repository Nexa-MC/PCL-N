# Privileged update boundary

The required attacker model includes a malicious unprivileged process running as
the same operating-system user as Nexa. It may keep replacing entries in user-owned
directories. An attacker already holding the updater's root/administrator authority
is outside this isolation boundary.

If isolation cannot be established, automatic replacement must stop and explain
that administrator authorization through a trusted installer is required. Do not
elevate an executable downloaded or staged in a user-writable directory.

## Admission requirements

A privileged update helper must be installed by the system installer in a protected
namespace. Verify the opened helper, installation and staging ancestors, including
owner, ACLs and parent deletion rights. An elevated token alone is not admission.
Fresh staging is created by the helper beneath its protected namespace. Changing
permissions on an existing user-owned tree cannot revoke pre-existing open handles.

Downloads, cached manifests, IPC requests and installation plans are untrusted data.
Inside the protected process, independently verify publisher signature, version and
channel, platform, exact lengths and hashes, destinations and the signed previous
inventory authorizing deletions. Never inherit plugin/tool paths or execute code
from caller-supplied staging. A helper must use handle-relative file operations and
keep verification and mutation bound to the admitted objects.

- Windows: protected ownership and DACLs, rejecting reparse points and user rights
  to write, rename/delete, change ownership or change DACLs, including parent
  FILE_DELETE_CHILD. Retain appropriate sharing restrictions through mutation.
- Linux/macOS: root-owned namespaces without user/group/ACL mutation grants,
  no-follow descriptor-relative access. Name-based rename/unlink after closing a
  verified file is not verification of the object that will be changed.

User-owned settings, accounts, logs and caches stay unprivileged. SafeFilePort is
not a guarantee that a same-user process cannot directly change those files.

## Current implementation status

XSR-754 adds a preinstalled helper, independent publisher verification, protected immutable
slots, monotonic high-water, journal activation/recovery, rollback and unprivileged restart.
Settings retains manual download links and enables automatic requests only for admitted
system installations. See automatic-update-transaction.md and the XSR-754 migration note.
Native CI and physical replacement/power-loss evidence remain acceptance gates; SEC-07 is
not declared closed by managed tests alone. Legacy path-based utilities stay disabled.

### Audit enforcement
UpdateStaging.ApplyPlan is now an explicit fail-closed compatibility entry point: it throws NotSupportedException before inspecting or mutating any install/staging path. There is no trusted object-bound privileged helper yet. Verified planning, download and manual installer flows remain usable; calling the legacy path-based apply API is not an authorization to replace files. A future helper must meet the same-account attacker contract before this capability is enabled.

The legacy restart scheduler must likewise refuse both scheduling and process-start-info
creation before any filesystem access or process launch. A signed download is not a
preinstalled helper, and a caller-supplied staged executable must never become the updater.
The existing public signatures remain compatibility entry points, not executable handoff
authority. Production composition is forbidden from invoking these entry points.

Package reception must copy and hash the same bytes in a single pass into a fresh,
helper-owned destination handle. Enforce the signed length before each write, reject short
or overlong input and digest mismatches, and flush successful output. Failure or cancellation
invalidates and truncates partial output; a cleanup failure is reported rather than hidden.
The destination is never obtained by reopening a caller-controlled path after verification.
This stream primitive does not prove directory protection: the future helper still owns
ancestor/ACL admission, exclusive handles, durable publication and transaction recovery.
It must discard a failed destination even if truncation fails, and cannot treat it as verified.

### Windows object admission

The Windows platform adapter walks a local fixed-drive path one component at a time,
opening children relative to the retained parent handle. Every object must have a trusted
owner (SYSTEM, Administrators or TrustedInstaller), a present non-null DACL, and no effective
write/ownership/delete grants to other principals. Unknown or conditional ACE shapes are
not proof of isolation and are rejected. Deny ACEs are not used to rescue an unsafe allow.
Inherit-only ACEs are ignored for the current object, but every descendant is checked anew.

An ancestor may allow creating unrelated children (the standard system-drive ACL does),
provided it cannot be changed or delete/replace existing children. The selected install or
staging root must also deny untrusted child creation. Reparse points, remote roots and
ambiguous leaf names are refused. Handles exclude write/delete sharing while held.

Fresh staging is created relative to the admitted directory with an explicit protected
SYSTEM/Administrators DACL and Administrators owner, at creation time. It never adopts or
repairs permissions on an existing user-owned directory. Leaf files use create-new semantics,
explicit protected security and retained handles; an existing name is a conflict. The adapter
does not accept arbitrary paths for mutation. Cleanup of an owned empty staging directory is
by handle, after owned child files are closed/deleted. This does not implement the updater
process, installation identity, recovery journal or Linux/macOS descriptor adapters.

### Windows high-water journal

Windows high-water state is accessed through the admitted directory lease, never through
a caller-supplied path. Open-or-create applies protected security only to a newly created
leaf, without truncating or repairing an existing file. The opened file is independently
admitted before use and exclusively held across read, monotonic comparison, append and
durable flush. Competing writers wait only for a sharing violation, with a bounded timeout.

The bounded append-only journal records canonical public versions with framing and a SHA-256
checksum. Complete corrupt records or non-increasing versions reject the entire operation.
Only an incomplete final record with a valid frame prefix is an interrupted append; it is
discarded by handle before the next append. Successfully flushed records are never compacted
or replaced by name. A full journal fails closed and requires a future protected maintenance
operation. This is not physical power-loss acceptance, or a completed update transaction.

The historical path-based UpdateHighWaterStore stays a test/compatibility primitive;
production construction is forbidden. WindowsUpdateHighWaterStore consumes the sealed
Windows directory lease. Neither store may authorize downloads, replacement or helper launch.

