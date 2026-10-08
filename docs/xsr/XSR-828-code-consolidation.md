# XSR-828 — evidence-based code consolidation

## Scope and compatibility lock

The cleanup consolidates repeated persistence mechanics and removes source that cannot
contribute runtime behavior. It does not replace the service ownership graph, alter public
API baselines, remove contract/native/AOT coverage, or implement features reserved for later
roadmap work.

`Nexa.Services.Common` owns an internal atomic file writer, consumed through existing project
references by Accounts and Settings. Each consumer continues to own its JSON schema, load
recovery, quarantine, path synchronization, and serialization. The shared writer owns only
same-directory unique temporary files, write-through streams, bounded replacement retries,
and failure cleanup. Accounts retains six linear-backoff retries of I/O/access failures;
Settings retains five quadratic-backoff retries of I/O failures. Existing exception messages
and cleanup exception filters remain intact. No public boundary or file format changes.

## Audit evidence

The audit inspected source declarations, repository references, SDK compile inclusion, and
executable test registrations. All source projects use default SDK inclusion for the files
below; no build script, generator, embedded resource, or explicit include refers to them.
Each file contains only whitespace and a file-scoped namespace, so deleting it removes no
symbol, implementation, or test:

- `Nexa.Services.Accounts.Contracts/Accounts/ProtectedLaunchProfilePort.cs`
- `Nexa.Services.Common/Tasks/TaskCenterStateContract.cs`
- `Nexa.Services.Minecraft.Install/Minecraft/Install/InstallCatalogStateContract.cs`
- `Nexa.Services.Minecraft.Process/Minecraft/Process/MinecraftProcessService.cs`
- `Nexa.Services.Updates.Contracts/Updates/HDiffPatchTool.cs`

The following obsolete helper declarations have no call sites in repository source, tests,
architecture checks, or scripts:

- `MinecraftInstallService.CopyAtomicAsync`: publication now uses the durable installation
  publication journal; the unused copy helper cannot execute.
- `SettingsPolicyService.MoveInstanceSettings`: rename uses `PrepareRenameSettings` and
  `ApplyRenameSettings` through `InstanceRenameJournal`, preserving compensation and conflicts.
- `UpdateStaging.RestoreUnixMode`: extraction/patching use `UpdateUnixMode` directly.
- `InputCapabilityProvider.CountXInputControllers` and its exclusive `XInputGetState` /
  `XInputState` chain: active collection uses `ReadXInputControllers` and
  `XInputGetCapabilities`, including controller subtype and haptics information.

## Test retention and verification

No duplicate executable test registrations or unreferenced test entry points were found.
Similar test filenames across service, desktop, and renderer projects exercise distinct
boundaries and stay. Platform-conditional privileged update tests and explicit OS IPC skips
remain because they cover unique supported CI fixtures. Test count or a skip is not deletion
evidence.

Existing profile persistence, schema quarantine, settings recovery/unknown-field round trip,
rename transaction, controller capability, installation recovery, and updater Unix mode
regressions must continue to pass. A focused shared-writer test adds observable guarantees:
serializer failure preserves the previous destination and removes scratch files; replacement
failure removes scratch files and preserves its I/O cause; a successful write replaces the
whole destination. Root integration runs the existing executable service and architecture
suites and the relevant trim/AOT validation after all concurrent changes land.
