# XSR-735 Bounded Function Patch execution

The existing Runtime assembly implements the opt-in `string -> string` ABI defined in
[function-patches.md](../function-patches.md). Session registration owns and validates
decoded instructions, host target grants and phase/stack/binary budgets. Snapshot completion
does not activate programs. ACTIVATE publishes one immutable runtime table; DEACTIVATE,
shutdown, crash, malformed input and disposal retire the activation lease. A blocked
ACTIVATE cannot republish after disposal. Existing constructors and declaration-only
sessions retain their behavior; the supervisor forwards an optional host admission object.

HEAD/ARGS/REPLACE/original/TAIL/RETURN execute locally in deterministic order. Original
exceptions propagate, same-point synchronous reentry bypasses patches, and caller cancellation
clears buffers and reentry state. Each bounded program stages argument/result/skip mutations;
length or shared instruction-budget failure discards all of that program's mutations and
disables it until reactivation. Retired sessions are checked even by already captured tables.
There is no dynamic code, reflection, CLR payload or hot-path IPC.

Validation: Release and Linux NativeAOT compile with zero warnings/errors. Both executable
runtime suites pass 111 tests with the one existing OS executable/IPC test explicitly skipped
because this workspace rejects Unix socket bind; CI continues to run all 112. Nine new cases
cover real binary registration and local execution, every phase, short-circuit, exception/
reentry/cancellation, rollback/reactivation, two-session retirement, malformed program/type/
grant rejection, shared budget and failed activation atomicity, terminal retirement, buffered
activation/disposal, and 10,000 unchanged patched calls at zero thread allocation after warm-up.
The 68-project architecture gate and changed-source whitespace verification pass.

This is executable protocol/runtime delivery, not completion of the compiler or all extension
adapters. No product target is enabled yet. The subsequent compiler unit must rewrite real
opt-in bodies before CoreCompile and connect a presentation-only Desktop point. UI Patch,
Event and Intent adapters, other ABI shapes and real plugin/platform burn-in remain open.
