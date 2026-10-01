# XSR-739 Sampler allocation attribution

## Contract

Composition-fixture schema 4 retains schema-3 clocks, scope, state attribution and
endpoint gate. Its process allocation counter uses `GC.GetTotalAllocatedBytes(true)`.
A fixture-thread meter separately counts the synchronous capture and sample-write paths
with `GC.GetAllocatedBytesForCurrentThread`; no delegate or worker is introduced. The
sample carrier becomes a value type, avoiding a per-sample peak-record clone outside
the measured sampler path. Capture/write meters reject nesting, unmatched completion
and thread migration rather than producing misleading counts.

Each sample's process and cumulative sampler counters are taken after capture, before
that sample's JSON write. The write is included in the next sample's aligned counters.
The report compares final/baseline aligned counters, before final serialization. Other
fixture work, navigation/frame allocations, background publications, waits and final GC
remain unattributed: their difference is not advertised as product-only allocation.
Negative or inconsistent differences remain invalid/null, never clamped to zero. CPU
observer cost is still not separately measured or subtracted.

The offline analyzer accepts schemas 3 and 4. Schema 3 has null sampler attribution;
historical runs cannot gain measured counters retroactively. Schema 4 requires monotonic
sampler counters bounded by the precise process counter, and reports per-window/global
sampler and unattributed rates explicitly. Neither zero unattributed bytes nor analysis
success certifies a native-window/Minecraft runtime KPI. Existing frozen schema-3 runs
remain valid within their original receipt and scope.

## Validation plan

Meter tests cover known allocations, idle zero-allocation meter reuse, nested/unmatched
calls and cross-thread rejection. Analyzer tests cover legacy null attribution, aligned
schema-4 rates, invalid/decreasing/oversized sampler counters and scope preservation.
Run managed/NativeAOT Desktop tests, real idle/navigation fixture smoke, architecture and
formatting gates; keep the ongoing two-hour schema-3 binary frozen and unchanged.
That frozen run and a separate 30-minute schema-4 run subsequently completed; their original
receipts, samples and exact analyses are retained in [XSR-742](XSR-742-retained-idle-soak-evidence.md).

## Completed local validation

Release build and Linux NativeAOT publish completed without warnings/errors; both
Desktop executables passed 108 cases. The staged source snapshot passed the 69-project
architecture gate and folder whitespace check. All 19 Python acceptance tests passed.
Managed/NativeAOT each completed actual 60-second idle and 10-second navigation fixtures;
the analyzer accepted their schema-4 counters and an existing schema-3 60-second run.

The idle aligned deltas were 2,123,768 total / 2,115,736 sampler / 8,032 unattributed bytes
(managed) and 1,077,888 / 1,069,280 / 8,608 bytes (NativeAOT). Both recorded one startup
`logging.entries` publication/frame; ordinary interval medians for unattributed
allocation were zero. These are process/fixture observations from an uncontrolled
development environment, not a zero-allocation product claim, OS/GPU measurement or
long-term SLA. The 30-minute frozen schema-4 idle fixture subsequently completed with its
original base-commit-plus-worktree receipt; the retained evidence is linked above.
