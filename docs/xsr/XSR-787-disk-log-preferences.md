# XSR-787 — Bounded disk logs and explicit log export

`diagnostics.disk-log-days` is global, defaults to 7 days, and accepts 1–90 days.
There is no legacy retention key: the legacy log page exported/cleared files but
did not persist an age policy. This preference is separate from the existing
in-memory `diagnostics.log-lines` control. Foundation applies only committed,
validated preferences to LogService, which forwards the policy to retention sinks
without holding the logging publication lock or performing filesystem IO.

FileLogSink keeps the active `launcher.log` and rotates before adding a record
that would exceed 4 MiB. Individual disk records are bounded. Archives use the
owned `launcher-archive-{UTC timestamp}-{GUID}.log` namespace. Cleanup is confined
to regular files with this exact generated name, without recursion or following
links. The active log and arbitrary `.log`/`.txt` files are preserved. Age cleanup
runs after durable policy initialization and after changes/rotation; an additional
32 archive / 64 MiB ceiling bounds disk usage even under repeated sessions.
Decreasing retention schedules cleanup on the sink worker, and increasing it
cannot restore deleted files. IO failures disable the sink rather than break the
logged operation. Cleanup never runs against real user data during development.

The privacy page offers opening the configured launcher log directory and an
explicit user-selected export of the active and owned retained disk logs. Export is
a small ZIP containing only `logs.json`, with validated local clock timestamps,
source last-write UTC, severity and an allowlist of built-in module names. Unknown
module text is replaced by `unknown`. Existing display lines contain no UTC date
or structured operation fields, so the export records this limitation and never
reconstructs a date, operation or stage from free text. Original message, exception,
operation context, accounts, paths, settings and raw file content are excluded,
matching the existing diagnostic export privacy boundary. This restriction replaces the
legacy raw-file archive, whose format could disclose unlabeled private values.
The page explains this export scope. Export does not upload data.

The directory chooser opens only after a user action. ZIP content and final size
are capped at 2 MiB, with at most 2000 recent entries, 33 source files and 68 MiB of
source input. A streaming prefix parser bounds memory even for enormous input lines.
The sink captures export inputs at a worker barrier: it first flushes admitted
records, then opens read-only handles to each selected source with delete sharing
and records their byte limits before resuming writes. Export parses those handles
without reopening mutable file names. Rotation or retention may rename/delete a
captured path without losing its admitted records; concurrent append cannot grow
the captured input. An active export may temporarily keep pruned file bytes alive
through its bounded read handles until it finishes; the archive limits govern
named retained files. All acquired handles are closed on success, IO failure and
cancellation, including cancellation while a snapshot request remains queued.
An oversized or unavailable export snapshot rejects that request without closing
the live writing worker; later writes may rotate/prune the input back into budget.
The writer uses Utf8JsonWriter for NativeAOT, a create-new temporary file in the
selected destination directory, cancellation checks, durable flush and a refusing-
overwrite atomic rename. Failure/cancellation removes the temporary file. The
controller suppresses duplicate requests, offers cancellation, cancels on exit,
and never displays success for cancellation/failure.

Contract coverage includes actual rotation/retention in temporary trees, age and
count/size bounds, unrelated files and symlink preservation, changes applied without
Settings rendering, failed-save isolation, export privacy, overwrite protection,
cancellation cleanup, live-worker recovery after an export budget rejection,
rotation/pruning after a captured export barrier, and Desktop
action dispatch/disposal. Parent integration runs
architecture checks, managed suites, NativeAOT and trim validation.
