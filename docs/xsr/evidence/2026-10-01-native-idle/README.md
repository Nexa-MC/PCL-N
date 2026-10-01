# NativeAOT idle composition observations — 2026-10-01

These are actual elapsed-time Linux x64 runs of frozen NativeAOT Desktop **composition
fixtures**, with no native OS window, GPU rendering or Minecraft process. They were collected
during concurrent development builds, not on a controlled timing runner. They do not certify
the Alpha.6/Beta runtime performance targets, physical acceptance, or absence of all leaks.

| Evidence directory | Elapsed | Schema | Frames | Build identity |
| --- | --- | --- | --- | --- |
| `native-idle7200-schema3` | 7200.0198028 s | 3 | 1 | `05b303ab` plus XSR-736 source worktree |
| `native-idle1800-schema4` | 1800.011528 s | 4 | 1 | `896de842` plus XSR-739 source worktree |

Both receipts deliberately retain their original worktree build identities; neither is a
clean-commit or Alpha.5/Alpha.6 build. Recorded input hashes and binary hashes identify the
collected run, but source reproduction remains unverified. Executable bytes are not retained
here. A later commit cannot retroactively replace the build identity or supply unmeasured
metrics.

Each directory retains the exact `run.json`, build receipt, deterministic gzip-compressed
`samples.jsonl`, and the offline analyzer's `observations.json`. The analyzer includes raw
run/sample SHA-256 and receipt SHA-256, separately reports forced-GC endpoints, and excludes
those endpoints from ordinary five-minute windows. Its binary verification was performed
against the matching frozen executable while collecting these observations.

The two-hour run contains 7,196 ordinary samples and 24 complete windows; the 30-minute run
contains 1,799 ordinary samples and six complete windows. Both contain one `logging.entries`
collection publication at the beginning of the measured interval and one resulting frame.
Handles remain 5, scene entities 285, state cells 210 and log entries 2 throughout ordinary
samples. Threads settle from 7 to 4. Working set/private bytes rise during initial allocation
and then level off; retained managed live estimates after final full GC are about 10.6 MB,
similar to the baseline, while GC committed memory remains higher. These are distinct facts,
not interchangeable no-growth proofs.

Schema 3 did not measure sampler allocation; its sampler and remaining-allocation fields stay
null. Schema 4's aligned final counters contain 28,789,976 process bytes, 28,780,696 sampler
bytes and 9,280 remaining bytes. Ordinary per-second remaining allocation has median 0 and
maximum about 669 B/s. This remainder includes other fixture/background work; it is not a
measurement of product-only idle allocation. CPU observer cost was not separately measured.

To replay analysis, decompress one sample file into a new temporary directory alongside its
run and receipt, then invoke `eng/acceptance/analyze_soak.py --run-dir DIR --output NEW.json`.
Omitting `--binary` intentionally leaves binary byte verification unperformed in that replay;
provide the matching frozen executable to verify its receipt hash. The CLI refuses incomplete
runs, inconsistent counters and existing output paths. Compare raw input hashes and computed
metrics; do not replace the original observation file with a weaker replay.

Still required: 8h native-window idle and Minecraft-running evidence; Windows/macOS hardware;
controlled UI/input/GPU frame tails; platform commit/native/GPU metrics; externally sampled
idle CPU/IO; active Sidecar, HTTP/cache, watcher, task and recovery scenarios. The 19 acceptance
tool regressions pass, but fixture endpoint gates remain distinct from these product gates.
