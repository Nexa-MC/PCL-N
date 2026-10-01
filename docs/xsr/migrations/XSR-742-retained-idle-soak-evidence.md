# XSR-742 Retained idle soak evidence

## Evidence boundary

The [2026-10-01 evidence bundle](../evidence/2026-10-01-native-idle/README.md) retains
two completed actual-elapsed-time Linux x64 NativeAOT Desktop composition fixtures:
7,200 seconds under schema 3 and 1,800 seconds under schema 4. It contains exact run records,
build receipts, deterministic gzip-compressed ordinary samples and complete offline analyses.
No executable bytes, native OS window or Minecraft process are retained or implied.

Original base-commit-plus-worktree identities remain intact. Binary and recorded source-input
hashes bind the collection inputs; source reproduction remains unverified. A subsequent commit
must not promote them to clean-commit, Alpha.5/Alpha.6 or physical acceptance evidence. Both
were measured alongside development builds, not on a controlled performance runner.

## Observations

The two-hour run has 7,196 ordinary samples/24 complete five-minute windows; schema 4 has
1,799 ordinary samples/six complete windows. Each contains one initial logging collection
publication and one frame. Handles, scene entities, state cells and log entries are constant;
threads settle to four. Working set/private bytes rise initially and then level off. Overall
median slopes still include this initial growth, and ordinary managed-live estimates include
the allocation nursery. Final full-GC live estimates and retained GC committed memory are
separate endpoint observations, not a monotonic-growth gate.

Legacy sampler attribution stays null. Schema 4's final aligned allocation is 28,789,976
process bytes, 28,780,696 sampler bytes and 9,280 remaining bytes. The remaining bytes include
other fixture and background work; CPU observer cost remains unmeasured. None of these
figures certify product-only idle allocation or native/GPU memory targets.

## Validation and remaining acceptance

All 19 Python acceptance-tool regressions pass. Both compressed bundles were decompressed
into new temporary directories and reanalyzed against their matching frozen executable;
the entire JSON analyses exactly match the originals. Run/sample/receipt input hashes remain
available for replay without a binary, which deliberately cannot reassert binary verification.

This documentation/data-only unit changes no runtime contract or assembly graph. Native
window/game 8h runs, cross-platform hardware, controlled UI/input/GPU tails and unmeasured
IO/cache/Sidecar/recovery scenarios remain open in the Alpha.6/Beta acceptance checklist.
