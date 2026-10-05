# XSR-788: launcher data migration and conservative cleanup

Owner: Setup. Desktop presents intent; Common continues to own the fixed bootstrap
locator and task-center entries. This changes neither the persisted settings schema
nor the Minecraft, Java, install-journal, recovery, update or profile formats.

## Data-location transaction

The existing `AppFolders` root is selected by the environment override, the fixed
`storage.json` locator, or the existing platform default. A settings preview reports
the current root, proposed empty destination, file count and byte estimate. It rejects
disk roots, identical or nested roots, nonempty destinations and links anywhere in the
source/destination chains. Environment-selected locations cannot be migrated.

This action relocates launcher data only. Preview and startup both refuse populated
path-bound Minecraft/Java receipt or recovery trees inside that data root:
`.nexa-java-jobs`, `.nexa-install-jobs`, `.nexa-modify`, `.nexa-pack-jobs`,
`.nexa-rename`, `.nexa-content-trash`, instance `Nexa/Recovery` directories, and
the specific `.task/loader` installer cache with path-authorized receipts. Other
ordinary `.task` scratch directories are not rejected by this admission rule.
Completed Java receipts remain path-bound too. The check walks the existing source
with the same entry budget and link refusal as migration; it never rewrites private
intent, plan, authority or snapshot formats. A rejected move preserves the old
root/locator, destination and records, and a queued request remains cancelable.
Keep game and Java storage outside the launcher data root for this migration action.
Relocating those owned stores requires a separate ownership-aware transaction.

Confirmation queues a bounded bootstrap record next to the locator. It does not copy
data while the running host's services still own the current root. The launcher closes
after queueing; its next startup processes the record before constructing any root
consumer or opening logs. Queueing requires the host's idle guard (no game or task).
The preview revision binds protected source files and the destination. Append-only
launcher logs may change during shutdown; the startup copies their current bytes.

Startup copies every source file into an isolated sibling staging directory, verifies
each copied SHA-256, and checks the protected source revision again. Settings,
profiles, local records and launcher caches retain their bytes. Minecraft
roots and explicit machine paths stored in profiles are not rebased or moved. The
source is preserved. The destination publishes by a directory rename; only then is
the locator atomically replaced. Unix copies start with restricted permissions and
restore the source file/directory modes before publication, including account files.
Before that commit, cancellation/failure restores
the empty destination and leaves the locator/source unchanged. An interrupted copy
can retry from the persisted, transaction-owned stage; a published destination is
accepted only when its complete verified manifest matches. The pending record is
retired after the locator commit. The environment override always wins.
Failed or stale queued requests remain visible and can be explicitly canceled from
Storage settings; cancellation removes only the transaction request and its owned
incomplete copy, preserving the source and any unrelated destination data.
Malformed or unsupported bootstrap/recovery records return an expected rejected
result and remain intact. Startup retains the original root and reports the error;
it does not guess ownership or delete an unreadable transaction record.

## Cleanup boundary

Cleanup is explicit and previewed. The file scope contains only known atomic-write
temporaries inside launcher settings/profiles/cache folders, older than 24 hours:
`.settings.json.<32 hex GUID>.tmp` and SafeFilePort's `<name>.tmp-<32 hex GUID>`.
Unknown files, ordinary cache payloads, quarantine copies, credentials, user worlds,
assets, `.task`, Java jobs, updater state and recovery storage are never candidates.
Links are rejected and candidate identities/content are revalidated before mutation.
Candidates stage by rename under a durable cleanup record; failure or cancellation
before commit restores them. Startup recovers interrupted cleanup before migrations.

Finished task-card cleanup uses the current task-center registrations. It removes
only successfully `Finished` visible entries matching the preview, atomically under
the task-center gate. Running/waiting, paused, failed and canceled entries remain;
their install/download records and recovery files are untouched. File cleanup and
task-card cleanup are separate transactions and confirmations.

No reference-aware CAS garbage collection, cloud synchronization, profile relocation,
snapshot pruning, future package cache, or automatic deletion policy is claimed.
Existing import/export/reset stays independent and retains its existing scope rules.

## Validation

Contract tests use isolated temporary roots only. They cover preview side-effect
freedom, startup migration and hash-preserving source retention, stale/occupied/link
rejection, cancellation and locator-publication failure, interrupted-copy retry,
environment override, cleanup eligibility/rollback, and task-history state retention.
Migration admission tests cover each existing path-bound game journal tree and a
real completed Java installation nested inside launcher data; rejection preserves
its receipt, inventory and managed runtime lifecycle. External game/Java roots stay
outside the copy and continue to use their unchanged absolute paths.
Desktop tests cover preview confirmation, cancel, navigation retirement and queued
close. The normal architecture, Release build and trim checks cover the new routes.
