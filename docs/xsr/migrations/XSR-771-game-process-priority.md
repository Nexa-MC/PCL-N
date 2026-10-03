# XSR-771 Captured game process priority

Game settings expose a global default and an instance override for the next launch.
The stable enum is normal, below-normal, above-normal, high and real-time. Legacy
LaunchArgumentPriority values 1, 2, 0, 3 and 4 respectively retain their meanings;
inheritance clears the override and restores legacy Normal (1). No running process
is changed by editing the setting.

The coordinator captures the effective value with the other launch settings. The
immutable request and plan carry an optional typed priority; hand-built plans that
omit it preserve their previous behavior. The executor requests adjustment through
the existing IJvmHost control after spawn, before returning the session. Rejected
or nonfatal throwing controls are logged and do not turn a started game into a
failed launch. No shell command or elevated helper is introduced. OS permission
and scheduling semantics remain authoritative.

Regressions cover legacy conversion, global/instance inheritance, immutable plan
propagation, readable draggable selectors, actual control invocation, and denied
or throwing controls preserving a live session. Architecture and AOT/trim gates
continue to apply.
