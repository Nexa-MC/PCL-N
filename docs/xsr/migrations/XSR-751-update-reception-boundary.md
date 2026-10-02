# XSR-751: refuse staged helpers and receive verified bytes once

The old restart API launched a caller-selected staged binary, even though automatic
replacement already refuses execution without a protected helper. Both restart-scheduling
entry points and process-info creation now refuse before reading paths, creating working
directories or calling a process port. Manual installer discovery/download remains intact.
A semantic architecture gate prevents production composition from restoring this route.

`VerifiedReleasePackage.CopyVerifiedPackageAsync` receives actual bytes from an untrusted
source into an already-open, initially empty, writable/seekable helper-owned destination.
It hashes precisely the copied bytes, checks the signed limit before writing, rejects short
reads and hash mismatches, and flushes successful output. It neither closes nor reopens either
handle. Partial output is truncated on failure/cancellation; cleanup failures preserve the
original error in an aggregate and never return success. Consumers must discard failed output.

Regression cases cover both legacy APIs with missing and present staging, absence of process
launches/work-directory creation, real release signatures, a source changed after the old
verify-only pass, single-pass non-seekable input, actual length boundaries, partial transfer,
destination failure, cancellation, flush failure and cleanup failure. The three-platform
NativeAOT update gate runs these cases as well as the existing archive/high-water contracts.

This is a closed admission unit, not a completed automatic updater. Protected namespace/ACL
and helper installation, authenticated requests, file replacement/rollback/restart and
power-loss acceptance remain open; `UpdateStaging.ApplyPlan` stays disabled.
