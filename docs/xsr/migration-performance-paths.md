# Performance path repair

This migration addresses the supplemental static performance review. Its timing estimates
are not measured benchmarks. Existing integrity, cancellation and state ownership contracts
remain authoritative.

- Routine launch may reuse content-addressed assets by existence and expected size. Libraries
  reuse only successful hash-verification receipts keyed by normalized path, expected hash,
  length and modification time, scoped to the service lifetime. Explicit verification and
  newly downloaded content still perform full hashing.
- Installation and launch completion deduplicate destinations in linear time and use eight
  file workers. Progress is aggregated across workers; retries cannot move it backwards.
  Loader execution and final instance publication remain after all workers have joined.
- Recovery reuses unchanged source/blob receipts, bounds concurrent compression, and keeps
  manifest publication under the cross-process lease. Restore still hashes every blob.
  Durability flushes for new objects are retained: flushing only the manifest would not ensure
  its referenced objects survived a power loss. Resource/shader packs are limited to 64 MiB
  per file and 256 MiB total per snapshot;
  larger cosmetic files remain outside automatic recovery. The overall 8 GiB bound remains.
- Backend scene commits skip identical nodes, reuse drawing resources and preserve child
  order without per-node linear index searches. Renderer component access uses typed slots.
- Logs append to a bounded ring and publish batches; file output drains asynchronously.
  Shutdown awaits the final log flush. State projections consume queued change notifications
  on the UI thread, and cached metadata remains bounded by file stamps and read budgets.
- Default public HTTP clients share a connection pool; custom TLS, proxy and cookie policies
  retain dedicated handlers. Java discovery already limits concurrent probes to four; the
  supplemental claim that it was unbounded does not match the current branch.

Concurrent shutdown testing also exposed a pre-existing sidecar race: ending pending work can
close the transport before the final shutdown frame is sent. Shutdown now tolerates that closed
transport and its internal deadline while still completing cleanup and retiring pending work.

Scene collection still walks the visible tree; native controls now consume only changed nodes.
Per-subtree scene patches remain a later renderer change. Account, language and launch business
projections are change-driven; lightweight pager/input/motion checks remain frame-driven.

Validation:

- The complete solution builds with zero warnings and zero errors.
- Services: 436 cases; desktop: 101; renderer: 88; Avalonia backend: 9 top-level cases;
  runtime: 99; PXML: 40; sidecar protocol: 20. Architecture checks cover 68 projects.
- Four OS IPC cases are explicitly skipped because this execution environment does not
  permit the required socket operations. Native keyring isolation requires an unlocked
  platform keyring and remains unavailable here.
- Linux x64 desktop NativeAOT publication, including trimming, succeeds. The published
  native executable passes headless shell validation with 52 semantic nodes and exit code 0.
- Focused regressions verify worker bounds and cancellation joining, warm verification
  receipts, snapshot/blob reuse and corruption repair, metadata budget accounting, batched
  logs, idle projection suppression, unchanged native nodes and closed-peer shutdown.
  These are correctness/work-avoidance checks, not end-user timing benchmarks.
