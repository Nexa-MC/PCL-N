# XSR-764 — Default server and automatic file completion

`game.server` is a scoped, next-launch setting. A global default fills an otherwise empty
instance server; explicit instance metadata retains priority over that default. Instance
Custom overrides it, including an empty value meaning no automatic join. Inherit restores
the global/metadata fallback. An explicit “Join server” intent is temporary and wins for
that launch only. Server text is limited to 512 characters without whitespace, controls or
URI schemes, matching the existing launch command contract. It never becomes a shell command.

`game.auto-repair` controls the existing verify-and-complete-files stage, default true.
The service resolves scoped settings before that stage. Disabling it skips network file
repair but still resolves manifests, applies Java compatibility checks, builds the launch
plan and runs preflight. Explicit user-requested remediation remains independent. Missing
inheritance, invalid metadata and JVM failures must not become successful launches.

The global Game form adds the default-server field and uses its existing automatic-repair
position. Version Game inherits both controls. All mutations continue through sealed
Settings routes and use root-qualified instance directories. Tests cover actual launch
preparation, scoped repair enable/disable, server precedence, invalid input and inheritance.
