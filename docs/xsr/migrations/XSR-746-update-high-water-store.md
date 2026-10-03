# XSR-746: durable update high-water store

Historical compatibility primitive: production construction of this path-based store is
now forbidden. Windows protected persistence is superseded by
`XSR-753-windows-update-high-water-journal.md`. The tests below preserve the old contract;
they do not prove directory admission or authorize use in the privileged helper.

The protected updater needs a durable monotonic version before automatic replacement can
be enabled. `UpdateHighWaterStore` is the helper-side persistence primitive for that value.
It accepts only canonical public release versions, serializes competing writers through a
separate lock file, writes a same-directory temporary file with write-through and a durable
flush, and atomically replaces the committed value. A same-version or older candidate is
rejected while holding the lock. Orphaned temporary files are never read as authority.

The containing directory is deliberately an input rather than something the library creates.
The future installer/helper must establish and verify the protected owner, ACL and ancestor
chain described by `update-privilege-boundary.md` before constructing this store. This unit
does not claim that a user-writable directory is protected, does not enable
`UpdateStaging.ApplyPlan`, and does not grant replacement authority to Desktop.

Contract tests cover first commit, restart persistence, downgrade rejection, corrupt state,
orphan recovery and competing writers. The existing native account-security GitHub Actions
matrix executes the update-mode entry point on Windows, macOS and Linux; that entry point also
executes this contract. Hosted-runner results verify filesystem semantics only;
they do not replace administrator-installed helper, power-loss or physical-device evidence.
