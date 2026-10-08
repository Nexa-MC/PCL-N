# XSR-827: launcher-aware mod process compatibility

## Evidence and scope

In this task, CA means [Crash Assistant](https://modrinth.com/mod/crash-assistant)
(Modrinth `ix1qq8Ux`). Its declared source is
[KostromDan/Crash-Assistant](https://github.com/KostromDan/Crash-Assistant), inspected
read-only at `93fd8960ed4f9e55a9f5c34ab2f8ce2c4308fc14` on 2026-10-08.
No downloaded mod JAR is executed by this investigation.

* `common_config/.../utils/JavaBinaryLocator.java` obtains the executable from
  `ProcessHelper.getCurrentProcessCommand()`. On Java 9+, that is the operating
  system executable reported by `ProcessHandle.current().info().command()`.
  [Exact source](https://github.com/KostromDan/Crash-Assistant/blob/93fd8960ed4f9e55a9f5c34ab2f8ce2c4308fc14/common_config/src/main/java/dev/kostromdan/mods/crash_assistant/common_config/utils/JavaBinaryLocator.java).
* `common_config/.../loading_utils/JarInJarHelper.java` starts that executable with
  JVM options, `-cp`, `dev.kostromdan.mods.crash_assistant.app.class_loading.Boot`,
  and `--args-file`. The normal XSR executable is the native JNI host, whose
  previous entry point accepted only `--jvm-host`; this child exited with code 2.
  [Exact source](https://github.com/KostromDan/Crash-Assistant/blob/93fd8960ed4f9e55a9f5c34ab2f8ce2c4308fc14/common_config/src/main/java/dev/kostromdan/mods/crash_assistant/common_config/loading_utils/JarInJarHelper.java).
* CA uses the game's PID/start time, `java.class.path`, RuntimeMXBean VM options,
  and `sun.java.command` for diagnosis. It uses cwd-relative `mods`, `config`,
  `logs`, `crash-reports`, and `local/crash_assistant` directories. The old planner
  used the instance directory as cwd even when `--gameDir` selected the shared
  Minecraft root, splitting these paths for nonisolated instances.
* CA respects the existing user environment
  `DisableEntirelyCrashAssistantModOnSystem=true`; the launcher must not invent
  or override this preference. Its log discovery has no NexaCL-specific launcher
  log path contract. CA's own `logs/stderr_stream.log` remains its own output;
  launcher credentials/logs are not copied into game directories for discovery.

Read-only `origin/dev` at `4755faee0a10686917213afe57cb772ade83d77f`
inspection of `MinecraftJvmArgumentService`,
`MinecraftProcessLaunchService`, and launcher JVM request/host behavior establishes
normal manifest launcher token replacement and argv construction, but no CA-specific
flag or environment override that repairs native executable detection.

Also inspected public [CleanroomRelauncher](https://github.com/CleanroomMC/CleanroomRelauncher)
at `49c5682a3a1a6c50bb6704c403848b063427c4e1` and
[lwjgl3ify](https://github.com/GTNewHorizons/lwjgl3ify)
at `c87172bedb89256cebe5e4442dad896e1aadf482`. Both obtain game arguments from
loader-owned state, select Java themselves, and start child JVMs. Cleanroom's
`RelaunchMainWrapper` watches its parent and the original process waits/forwards
the child exit code. lwjgl3ify's `forwardLogs` path waits/forwards; its graphical
console path intentionally ends the original JVM and continues in a detached
process. The launcher cannot silently reinterpret that detached process as a
managed XSR session. Their generated command/argument-file privacy is mod-owned;
this change never reconstructs parent credentials into public argv or files.

## Locked compatibility boundary

The ordinary managed game launch continues to expose exactly `--jvm-host` as
the native host's OS arguments. Its selected runtime, JVM options, main class,
and game arguments travel through the bounded private bootstrap pipe. No game
credential is added to OS argv, environment, bridge metadata, or disk files.

After the private request has been accepted, the isolated host records only the
canonical selected Java executable in `NEXACL_JVM_CHILD_JAVA`. A mod that invokes
the current native executable as a Java command can use a bounded Java child
bridge. The bridge requires this concrete existing java/javaw executable, rejects
host recursion and invalid/oversized vectors, and preserves each argument as a
separate argv item. It forwards only the mod's newly supplied child command to
real Java. It does not borrow or regenerate the managed parent's arguments.
The Java launcher parses its own flags. The bridge admits classpath/main and
`-jar` commands; module entry points and pre-main `@argfile` expansion are not
host APIs. Unsupported host/bootstrap calls remain rejected. No shell command
is constructed.

On Unix the bridge replaces itself with Java, preserving the process PID and
inherited pipes. On Windows it starts Java, waits, and returns its exit code;
the managed parent's existing tree cancellation owns both bridge and Java child.
Natural game exit leaves the CA analysis app available as the mod intends.
Created/running/terminal snapshots, cancellation, leases, exit codes and managed
session identity continue to describe the originally launched JVM.

CA's auxiliary Java app can outlive that managed game. The bridge independently
enrolls it in the existing managed-runtime use protocol before Java starts. It
derives the component from the canonical selected executable's containment under
a `.nexa-java-jobs` root and requires existing `.nexa-java.lock` and
`.nexa-java-uses/<component>` enrollment with a GUID lease. It never creates these
ownership directories or treats a directory location as permission to remove Java.
If those managed markers exist but the parent GUID enrollment has already retired,
the bridge refuses the auxiliary Java launch rather than continuing without a lease.
CA can request immediate game exit after spawning its bridge, so the bridge may first
run after the game's parent lease is removed. This conservative refusal closes that
scheduling race without inferring new enrollment authority from an empty directory.
External Java without an existing managed enrollment structure continues to launch
without a launcher-managed lease.
All admitted paths reject symbolic links. Creation takes the existing installer
root lock and creates a fresh exclusive GUID lease in that component. A shared
internal Contracts codec retains the exact `NXJAVA01`, PID and UTC-start-ticks
20-byte record already consumed by Java removal/replacement. No service reference
is added to the isolated native host.

Unix binds the bridge's actual PID/start time before `execv`; Java retains that
identity and its durable lease survives the close-on-exec file handle. Existing
removal checks reclaim the record only after exit or proved PID reuse. Windows
binds the actual Java child, retains uncertain records, and removes its record
after confirmed child exit. Preparation/failed exec without a created Java child
can remove its own fresh record. Abrupt host exit during child handoff leaves
the same conservative partial-record exclusion as ordinary managed launches.
Unregistered external Java receives no launcher-managed lease. This auxiliary
runtime exclusion does not create a second managed game session.

Linux CLR `Process.StartTime` calibrates the boot clock separately in each
process. A native bridge and a later removal worker can report slightly different
UTC ticks for the same kernel PID lifetime. The Linux lease reader conservatively
retains every running PID; independently calibrated UTC birthdays do not prove
PID reuse. Only a confirmed exited/absent PID permits automatic reclamation on
Linux. A stale lease whose PID has been reused remains excluded while that PID
is running. Windows/macOS retain the original native birthday comparison. This
prevents clock calibration from permitting removal of the CA child's live runtime.
The persistent record stays `NXJAVA01`/20 bytes.

The platform identity reader recognizes Linux kernel `Z` (zombie) and `X`/`x`
(dead) states as exited even when the OS has not reaped an orphan PID. These
processes have ended and cannot keep using Java. It reads a bounded `/proc/PID/stat`
snapshot, verifies the PID and closing command delimiter, and maps malformed or
unreadable state to Unknown. It never infers exit from birthday differences on
Linux. This lets the existing guard reclaim the completed CA auxiliary record
in containers whose PID 1 delays reaping.

The JNI VM receives a main-class-only `sun.java.command` default when no explicit
VM option already supplies the property. This supplies CA's main class diagnosis
without materializing access tokens or any game argument as JVM metadata. Existing
manifest `${launcher_name}` / `${launcher_version}` properties and VM arguments
remain unchanged. WorkingDirectory equals resolved GameDirectory; instance,
client JAR, classpath and native paths remain absolute and separately identified.

## Validation and limits

Contract tests cover shared/isolated directory identity and absolute inherited
client/classpath paths, bootstrap argv privacy, and exact argument/environment
roundtrip. The native JVM smoke fixture recreates CA's executable lookup and
child invocation using repository-owned Java source, verifies Java process
identity, stdout/stderr, Unicode/empty/spaced argv, child exit, normal parent exit,
and process-tree stop. CI already runs the native host on Windows/Linux Java
8/17/21/25 and macOS x64/arm64 Java 21.

These source and fixture checks do not establish a live CA GUI or a complete
Fabric/Forge/NeoForge/Cleanroom/LWJGL compatibility matrix. A real game crash,
CA GUI/upload, platform window behavior, and detached relauncher session adoption
remain runtime acceptance checks. No private CA API, launcher-specific log export,
mod-generated argument redaction guarantee, or automatic second session is claimed.
