# XSR-734 Fixture wake attribution

## Contract

The desktop-composition soak records state-publication metadata during the measured
interval and distinguishes state-bridge requests from tree invalidations. Attribution
retains at most 64 semantic-ID/reason pairs, counts excess pairs without retaining them,
and includes no state payload, account, path or log message. Warm-up changes are excluded;
observation itself does not flush coalesced state, request frames or insert a wait.

State changes and requests are correlated evidence, not proof that each publication
caused a particular frame. A legitimate startup publication must be reported rather than
silently flushed or hidden to obtain a zero-frame result. The historical XSR-732 single
wake remains unattributed until a new run supplies evidence.

This is test-harness instrumentation, not a production poller or new assembly. Fixture
CPU/allocation still include instrumentation and sampling. Native-window, GPU and real
Minecraft 8h acceptance remain separate.

## Validation

The complete managed and Linux NativeAOT Desktop suites pass 106 tests with zero build
warnings/errors; the 68-project architecture checks and changed-file whitespace verification
pass. Warm-up exclusion, repeated-change aggregation,
64-pair overflow, retirement/restart and separate state/tree counters are covered.

A real 60-second idle composition fixture records zero frames, zero state/tree requests
and zero measured publications. It does not explain the previous XSR-732 single wake.
A separate 60-second navigation run passes with 3,631 frames and 111,327 requests:
1,210 state-bridge / 110,117 tree requests. Its two retained publication pairs are
`launch.instance.summary` and `minecraft.library` (`ValuePublished`, 2,420 each); no
overflow occurs. Composition P95/P99 are 0.6/1.1 ms in this uncontrolled environment,
without OS/GPU timing. These results are fixture evidence, not product idle or native SLA.

A separate real 60-second Linux NativeAOT idle composition run records one frame and one
state request (zero tree requests), alongside one `logging.entries` / `CollectionDeltaApplied`
publication at 0.0003304 seconds. This identifies the publication associated with this run's
startup wake without hiding it or flushing before measurement. No later publication is
recorded during the interval. It does not retrospectively attribute the XSR-732 run.

The preceding `60fa833c` XSR CI passes managed/native runtime, Services, Desktop, UI/backend
and architecture checks, then fails exact imports formatting on `InstanceRecoveryService`.
This unit corrects that ordering; the preceding Launcher Build passes all six RIDs.
