# Completion capability audit

This follow-up reads production C# and settings IA after XSR-800 through XSR-809. Searches exclude
generated `bin`/`obj` files, structural Group/Choice entries, external Nexa.Plugin execution and
SDK work, Cloud features, and physical platform acceptance. Error messages about interrupted
operations are not implementation placeholders. No production `TODO`, `FIXME`, or
`NotImplementedException` remains in the searched Desktop, service, native UI, Sidecar protocol,
and XSR Runtime source paths.

## Implemented controller paths awaiting catalog reconciliation

`SettingsCatalog.Load` computes availability from schema keys and an explicit ID allowlist;
setting a raw JSON row to Available alone does not change effective availability. The following
keyless entries have concrete consumers. They should be mapped deliberately rather than making
all keyless entries available. IDs below retain their full scope prefix.

| Catalog IDs | Concrete consumer |
| --- | --- |
| `instance-settings.basic.79e47f9ac834`, `.3a0d5d5aa2ac`, `.b557a4bf44aa`, `.771f820442b1`, `.595a4ca570a5`, `.1588c496fbc8`, `.fecf8ceb8303` | `SettingsPageController.Identity.cs`: revision-checked name, description, PNG icon, starred, tags/group, notes, custom launch caption editing; `InstanceIdentityService` applies persisted identity to discovery. |
| `instance-settings.servers.693438d90d7c`, `.d6c3e6592b58`, `.da73ed798ad8`, `.5a8d8adc344f` | The same controller edits login requirement and bounded authentication/register HTTPS endpoints/display name; launch identity resolution applies policy. The separate lock row is an honored read-only lock, not an unrestricted editor. |
| `global.java.dc1399b7a8ba`, `.4c869c02a251`, `.8236e49ebcbe`, `.1f8d2f784a60`, `.aef617d61a94` | `SettingsPageController.Java.cs` shows scanned Java version, vendor, architecture and literal executable path with managed/runtime actions. Automatic choice, compatibility and runtime probes require their own exact consumer mapping. |
| `global.java.90551624db0d`, `.2fb41ba03e43`, `.ea5ffef60a9e` | `SettingsPageController.JavaDiagnostics.cs` + XSR-812: actual fixed properties/version probe, Java 9+ module list, and bounded redacted raw preview/copy. Java 8 module status is explicitly unsupported. These facts do not establish every JVM runtime capability. |
| `instance.worlds.17db261a8c32`, `.777fa6b3b8a5`, `.3fff1c03d28a`, `.ee27261308f3`, `.0f68adbfffa2`, `.ae94fe88e32c`, `.faad5f3cce5e`, `.22effc520481` | `Management.cs` + `Worlds.cs`: bounded world listing, level.dat metadata/size, open, backup, copy and recycle actions with captured file identity. World health and snapshot semantics require separate evidence. |
| `instance.screenshots.3eae7fd3de23`, `.998b17a3bbc7`, `.0a4632048b4e`, `.b4a843861188`, `.22effc520481` | `Management.cs` + `Screenshots.cs`: bounded image gallery, folder, native clipboard/share and recycle operations; timeline is not inferred from sorting alone. |
| `instance.content.05803a333d18`, `.d0c2287854d6`, `.fc3ff537cf56`, `.2e3fd2c9385f`, `.22b442dcb594` | Installed mods/resource packs/shaders, online update identity joining, and selected transactional updates. Data packs are consumed per-world and must keep that scope explicit. |
| `instance.files.8f0192eaa51c`, `.3f21274a1129`, `.0133bf26089d` | `FileWorkspace.cs`: bounded config/log/crash report listing and text viewing; config writes require read identity, preview and atomic preserved-original save. Other arbitrary directories are not granted by this contract. |
| `instance-settings.profiles.f209065b1798`, `.813507d1c795`, `.5932fb33f692`, `.2f0ae4b3f9a0`, `.8eec3283a0a3`, `.141700b7dc2e`, `.ca34ed2f75fe`, `.636077328a1e` | `LaunchProfiles.cs` + `MinecraftLaunchOverlayLease`: named and temporary mods/resources/shader/config sources with exclusive ownership, journaled restoration and immutable per-run policy. Ordinary per-instance JVM/memory editing uses named/temporary Settings layers. |

## Gaps resolved during the follow-up

- Windows Jump Lists were absent during the first search; `DesktopJumpList` now provides owned
  Windows integration and a schema preference with platform availability.
- Network traces initially had a service producer only; `SettingsPageController.NetworkDiagnostics`
  now queries and displays the bounded diagnostic trace.
- Config overlay sources initially had no lease consumer; launch profile and temporary overlays
  now include the fourth `config` directory.
- World health has a separate `SettingsPageController.WorldHealth` consumer for bounded region
  allocation/chunk NBT checks; it does not infer health from the world metadata list.
- World snapshots now have a separate read/capture/verify/restore consumer in
  `SettingsPageController.WorldSnapshots`; verified restoration creates a new world and preserves
  the current world. Screenshot timeline groups actual file modification dates independently of
  the gallery, with bounded pages in `SettingsPageController.ScreenshotTimeline`.
- Java properties/version and module diagnostics now have the typed queries and real fixed
  Platform process port in XSR-812. Only admitted inventory executables are probed; raw previews
  are redacted, empty/malformed output is rejected, Java 8 modules are explicitly unsupported,
  and navigation cancels queries. Catalog claims must use these exact consumers.
- Manual Java download now has its own XSR-822 preview/license-confirm/install/cancel/status
  workflow, reviewed-plan fingerprint revalidation, bounded metadata, real post-install probe
  and managed registration receipt. Launch-required acquisition alone was not a sufficient
  consumer. Inventory rows label managed/external from actual ownership records.
- The advanced workspace in XSR-817 exposes finite navigation, actual bootstrap mode, captured
  composition/runtime facts, a cancelable asynchronous scheduler probe and a redacted typed
  settings snapshot. A separate developer action opens the trusted raw settings file through
  the composition-owned native callback, with an explicit credential warning and live-source
  admission. It does not claim arbitrary backend or feature mutation.
- Developer appearance diagnostics have an explicit `SettingsPageController.RuntimeDiagnostics`
  consumer. Physical presentation frame rate and absent GPU counters stay unavailable rather
  than displaying estimated measurements as verified facts.
- CLI, command palette, and launcher safe mode now have the explicit bounded contracts in
  XSR-809. Root composition and integrated tests establish actual wiring, including single-instance
  destinations and safe-mode suppression.
- `java.compatibility` is mandatory; attempts to mutate its obsolete preference fail explicitly
  instead of persisting an unused value.
- Sidecar extension completion is the finite Host contract in XSR-806. Semantic binding resolves
  registered same-session numeric Command and codec-typed State identities; it does not support
  arbitrary CLR type invocation or arbitrary C# method binding.

Root composition now admits reviewed keyless Setting consumers and dedicated State/Action
workspaces alongside schema-backed editors, while retaining unavailable rows with their actual
reason. Unknown navigation rejects without retiring the current page. Settings rebuilds bind
new editors before input, and asynchronous fact updates defer rebuilding until queued input has
been consumed. Instance retirement cancels scoped reads and rejects stale sources. Integrated
validation and the remaining roadmap/acceptance boundaries are recorded in
[XSR-820](XSR-820-completion-closure.md); visibility alone is not a completion claim.
