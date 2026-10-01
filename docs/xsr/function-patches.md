# Function patches

## First executable ABI

FunctionPatch kind 12 keeps its existing registration envelope and SHA-256 ownership.
Executable payload version 1 is opt-in through a host-owned admission object. A session
without admission retains declarations only. Admission copies an explicit target allowlist;
unknown targets, flags, payloads or phases fail registration before READY. Signing a Sidecar
does not automatically grant every host function.

The first ABI is a synchronous, non-null `string -> string` function, with strings limited
to 2,048 UTF-16 code units. Host declares versioned semantic targets and resolves them once
to instance-owned numeric handles. No host account, update, ownership or trust target is
exposed. Future scalar/async/ref/iterator ABIs require their own contracts; this ABI does not
support them. No plugin CLR object, delegate, reflection, dynamic code or synchronous IPC
is used by the interpreter.

Execution is HEAD, ARGS, REPLACE or original, TAIL, RETURN. Within each phase, activation
order then registration order is deterministic. HEAD can short-circuit only after setting a
return value; ARGS changes the argument; REPLACE must set a result and skip original;
The first successful REPLACE wins. TAIL runs after successful original/replacement;
RETURN transforms the final result. Oversize host arguments bypass patches, and a null host
result bypasses post-body patches, preserving out-of-contract host behavior.
An original exception propagates unchanged and skips TAIL/RETURN. Same-point synchronous
reentry invokes the original directly; calls to other points work normally, with a maximum
nesting depth of 32. A caller cancellation propagates and always releases interpreter buffers.

Each invocation captures one immutable program table. Each program works on local argument/
result/skip state and commits only on success. A runtime budget/length fault rolls that
program back and disables it for its activation, preserving host behavior. Retirement is
checked between instructions and immediately before committing; it prevents future calls
and discards an interrupted program, but cannot undo earlier completed programs or a host
body already running. There is no synchronous unload wait on a hot path.

Maximums: 64 instructions, stack depth 16, payload 8 KiB, constants 1 KiB of strict UTF-8,
32 programs per target, 256 programs per runtime, 512 instructions per invocation. Instructions
are forward-free straight-line operations: argument/return/constant loads, concatenation,
argument/return stores, skip-original and end. Stack/type/phase validation occurs during
registration; no jumps, loops, calls, I/O or allocation instructions are admitted. Concatenation
can allocate bounded strings; unchanged/empty patch calls do not rent a stack buffer.

## Binary payload

Little-endian header: ASCII `NFP1` (4 bytes), ABI 1 (byte), phase (byte: HEAD=1, ARGS=2,
TAIL=3, RETURN=4, REPLACE=5), instruction count (uint16). Each instruction is an opcode
byte. Constant (3) adds uint16 byte length and strict UTF-8 bytes; all other opcodes have
no operand. Opcodes: argument=1, return=2, constant=3, concatenate=4, set-argument=5,
set-return=6, skip-original=7, end=8. End must be the final instruction, with an empty stack;
trailing bytes are rejected. Return loads are allowed only in TAIL/RETURN, argument stores
only in ARGS, skip only in HEAD/REPLACE. HEAD skip and every REPLACE require a preceding
return store. The envelope flags must be zero.

## Lifecycle and delivery boundary

Registration decodes and validates the entire batch but publishes no executable programs.
Snapshot completion remains the READY prerequisite. Successful ACTIVATE publishes the batch;
DEACTIVATE and every terminal path retire it. Reactivation uses a fresh activation lease,
including a fresh runtime-fault state. Pending activation after disposal must not resurrect
programs. One session's failure/retirement does not remove another session's programs.

This first unit delivers an interpreter and real session execution tests in the existing
Runtime assembly, without adding projects. It does not itself rewrite existing method bodies
or complete UI Patch/Event/Intent adapters. The following compiler must rewrite explicit
opt-in source methods before CoreCompile, reject unsupported shapes, and connect a real
presentation-only host point; a source generator alone is insufficient.
