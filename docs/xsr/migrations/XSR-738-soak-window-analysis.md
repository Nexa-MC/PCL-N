# XSR-738 Soak observation windows

## Contract

Offline analysis consumes a completed schema-3 composition fixture `run.json` and its
bounded, chronological `samples.jsonl`. It requires fixture scope and explicitly false
real-desktop acceptance. Baseline and final samples follow forced full GC; both are kept
as endpoints but excluded from ordinary observation windows. Ordinary samples are grouped
into five-minute windows, with sample counts, elapsed ranges and min/median/max for each
memory, handle, thread and retained-object metric. Partial windows remain labeled partial.

Per-window CPU and allocation rates include the sampler and fixture. The analyzer does
not subtract an unmeasured observer cost, estimate native memory from working set, or
convert unavailable metrics to zero. Gaps and coverage are reported. At least three
sufficiently populated full windows are needed for a least-squares median trend, which
reports slope and first/last window medians rather than certifying absence of a leak.

Run and sample byte hashes bind the analyzed inputs. An optional frozen binary is checked
against a build receipt's SHA-256; historical source worktree identity is retained, never
relabeled as a later clean commit. Receipt contents attest only recorded build identity,
not physical execution provenance or successful source reproduction. Incomplete runs,
duplicate keys, non-finite data, decreasing clocks/counters and mismatched summary/sample
counts are rejected. Output is new and written only after validation completes.

This is observation tooling, not a Minecraft, OS/GPU, eight-hour or runtime KPI gate.
The existing fixture endpoint gate is reported separately; analysis success cannot upgrade
it into physical acceptance. No product assembly, scheduler, sampling timer or startup
policy changes are introduced.

## Validation

Synthetic analyzer tests explicitly remain synthetic: stable and linearly growing
windows, nursery sawtooth, large forced final-GC drop, partial/short runs, null handles,
gaps, malformed records and incorrect binary binding. CI analyzes its actual ten-second
fixture smoke without asserting a long-run trend. Analyze the existing frozen two-hour
run only after it finishes, preserving its base-commit-plus-worktree receipt.

The acceptance Python suite passed 16 tests (11 analyzer cases plus the existing five
Minecraft admission cases). The analyzer also consumed the three completed 60-second
managed idle/navigation and NativeAOT idle fixtures from XSR-734: each retained its
endpoint result, 59 ordinary samples, null long-run trends and false KPI certification.
Those initial executions prove short-run integration, not a physical run. The separately frozen
two-hour fixture subsequently completed; its exact records and replay are retained in
[XSR-742](XSR-742-retained-idle-soak-evidence.md) with the original worktree build identity.
