# XSR-783 — Captured launch wrapper and pre-launch hooks

Services resolve `game.wrapper`, `game.pre-launch`, and `game.pre-launch-wait`
for the root-qualified instance once during preparation. The request and immutable
plan retain that snapshot; subsequent settings edits affect the next launch only.
Explicit instance values, including empty commands, override legacy metadata.
Otherwise nonempty legacy instance commands take priority over global defaults.
Wait defaults to true and retains `LaunchAdvanceRunWait` and the legacy per-instance
`WaitForPreLaunchCommand` behavior. Commands remain local-only settings and are
excluded from transfer files. The Boolean wait preference may be transferred;
without a local command it does not cause any process to execute.

Desktop presents pre-launch scripts as ordered command lines, preserving exact LF,
CRLF, CR, indentation, blank lines and trailing separators when reading and applying
existing values. Each line uses a native single-line text control with opt-in tab
preservation; ordinary form and password fields retain their existing sanitization.
At most 32 lines are realized per page. Page navigation retains all drafts, and
explicit add/remove actions are the only operations that change line structure.
Control rebuilds preserve script drafts and focus. The wrapper stays a single-line
32,768-character executable-prefix field; it never shares argument-list splitting.
Restoring instance inheritance replaces the draft only after the setting write
succeeds. A rejected write preserves the draft across unrelated settings revisions
so the user can still apply it explicitly.

The pre-launch command runs through the platform shell in the game's working
directory after native extraction and before process creation. Shell syntax is an
explicit user-authored command contract. The launcher does not interpolate launch
tokens, account credentials, or settings into it. NUL and text exceeding 32,768
characters are refused. The platform owns the process and drains output without
storing or logging command text. Waiting hooks require exit code zero; creation
failure, nonzero exit, or cancellation prevents game startup. Cancellation kills
the owned process tree and joins it. A nonwaiting hook remains owned until game
startup succeeds, so startup failure or cancellation still kills it. On success
the hook detaches, is asynchronously reaped, and its later nonzero exit is reported
as a warning without changing the running game's state.

The platform executes hooks through a self-hosted `--nexa-launch-hook-worker`,
without a new project or public command-line script text. Before releasing the
command, the Unix worker creates a dedicated session/process group; Windows assigns
the idle worker to a kill-on-close Job. A private bounded binary request carries
the command and working directory. Fixed binary ready, started and exit messages
carry no output. The shell's stdin is closed, and its stdout/stderr are drained only
to the null stream. Shell exit is reported independently of descendant pipe EOF.
The worker remains alive while its owner pipe is held, retaining the group identity
even if the shell has already exited. Failure, cancellation or owner loss terminates
the full group/Job and joins the worker's physical exit. Explicit detach disables
Job kill-on-close and releases ownership; only that action allows background children
to continue after the shell exits. Detached output remains discarded until inherited
pipes reach EOF, so later writes do not terminate background commands. The detached
reaper joins the worker separately from its already reported shell exit. The worker
entry runs before Desktop composition.

Managed Java preparation acquires the installer's runtime-use lease before native
and hook execution. After game process creation, an asynchronous lifetime observer
holds it until the actual process exit, including cancelled sessions, rather than
releasing on the earlier cancellation state notification. Preparation failure
releases it directly. Java discovery and deletion remain owned by the Java service.

Wrapper syntax is an executable followed by tokenized arguments, with single or
double quotes, legacy backslash-before-double-quote rules, preserved Windows/UNC
paths, and empty quoted arguments. Single-quoted content remains literal.
Unbalanced quotes, NUL, newlines, excessive text, or more than 256 tokens are
refused during preparation. There is no shell expansion or launch-token expansion.
The wrapper receives the private JVM Host executable and `--jvm-host` as the final
arguments and must preserve standard input/output, wait for its child, and return
its exit code. Game credentials stay inside the existing private bootstrap stream;
there is never a fallback to a public Java command line. Injected private process
ports may receive the equivalent executable prefix, preserving their transport
guarantee. A launcher missing its private transport still refuses game process
creation. Wrapper spawn, bootstrap, and exit failures use the existing
process lifecycle and crash evidence contracts. JVM environment/capability state
reports the captured wrapper rather than claiming it is absent.

Regression coverage covers explicit-empty and root-specific overrides, legacy
fallback and wait selection, immutable planner capture, strict quoting, protected
wrapper argv, hook ordering, waited failure, cancellation, and nonwaiting ownership.
The architecture gate and existing NativeAOT/trim acceptance apply; no legacy
project, UI reference, runtime reflection, or new service locator is introduced.
