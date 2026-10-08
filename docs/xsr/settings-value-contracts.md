# Settings value contracts

The original IA enumerated 566 positions. As of 2026-10-08, the catalog contains 543
positions and `SettingsPolicySchema` contains 97 value definitions. There are 96
mutable policy keys; the retained obsolete `java.compatibility` definition has no
mutable consumer. The export service admits 80 keys, excluding 16 local-only keys and
`java.compatibility`. These counts describe contracts, not physical platform acceptance.
Groups, choices, actions and facts do not own persisted values. Reserved settings remain
`NotImplemented` until a real consumer exists; platform and dependency limitations remain
explicit. See [settings migration status](settings-migration-status.md) for consumers and
[XSR-820](XSR-820-completion-closure.md) for integrated verification evidence.

The earlier 540 catalog positions, 50 value definitions and 49 available consumers are
historical foundation-slice figures. Earlier slice validation counts remain historical;
they are not the current integrated test totals.

The following foundation contracts are declared in `SettingsPolicySchema`. Owner: `Nexa.Services.Settings`. Scope `G/I` permits global and instance overrides; `G` is global only. `Auto` is a payload-free mode; reset means remove the override. Enum strings are stable encodings, not localized labels.

| Key | Type / domain | Builtin | Scope | Legacy source | Applies | Export |
|---|---|---|---|---|---|---|
| general.language | Enum (auto, zh-Hans, zh-Hant, en) | auto | G | UiLanguage | Immediate | Yes |
| general.region | Named formatting culture / auto / follow-language (legacy ui-language accepted) | auto | G | UiFormatCulture | Restart | Yes |
| general.single-instance | Bool | true | G | New SystemSingleInstance | Restart | Yes |
| general.tray | Bool | true | G | New UiTrayEnabled | Immediate | Yes |
| general.close-to-tray | Bool | false | G | New UiCloseToTray | Immediate | Yes |
| general.minimize-to-tray | Bool | false | G | New UiMinimizeToTray | Immediate | Yes |
| appearance.animations-disabled | Bool | false | G | SystemDisableUiAnimations | Immediate | Yes |
| appearance.animation-fps | Number, 1–240 actual fps | 60 | G | UiAniFPS + 1 (write fps - 1) | Immediate | Yes |
| appearance.lock-window | Bool | false | G | UiLockWindowSize | Immediate | Yes |
| appearance.low-power | Bool | false | G | UiUltraLowPowerMode | Immediate | Yes |
| appearance.theme-mode | 2 system / 0 light / 1 dark | 2 | G | UiDarkMode | Immediate | Yes |
| appearance.accent | blue / purple / green / orange | blue | G | New Text UiAccentColor; no guessed custom-palette conversion | Immediate | Yes |
| java.runtime | Fully qualified path / Auto | Auto | G/I | New | Next launch | No |
| java.auto-install | Bool | false | G/I | New | Next launch | Yes |
| java.vendor | Enum: empty means automatic; supported Java brand names | empty | G/I | New | Next launch | Yes |
| java.compatibility | Retained obsolete Bool; mandatory checks have no disable policy | true | G/I | New | Next launch | No; obsolete |
| recovery.keep-history | Bool | false | G/I | New | Next task | No |
| game.memory | Number, 256–1048576 MiB / Auto | Auto | G/I | LaunchRamType + LaunchRamCustom | Next launch | Yes |
| game.window-mode | windowed / fullscreen | windowed | G/I | LaunchArgumentWindowType | Next launch | Yes |
| game.width | Number, 1–32768 px | 854 | G/I | LaunchArgumentWindowWidth | Next launch | Yes |
| game.height | Number, 1–32768 px | 480 | G/I | LaunchArgumentWindowHeight | Next launch | Yes |
| game.title | Text | empty | G/I | LaunchArgumentTitle | Next launch | Yes |
| game.default-isolation | none / loaders / non-release / loaders-or-non-release / all | all | G | LaunchArgumentIndieV2 0/1/2/3; all is new | Next task | Yes |
| game.launcher-visibility | keep / minimize / hide / hide-and-close | keep | G/I | LaunchArgumentVisible 1/5 keep, 4 minimize, 3 hide, 0/2 hide-and-close | Next launch | Yes |
| game.jvm | Text | existing LauncherDefaults JVM string | G/I | LaunchAdvanceJvm | Next launch | No |
| game.arguments | Text | empty | G/I | LaunchAdvanceGame | Next launch | No |
| game.wrapper | Executable and quoted arguments, at most 32768 characters / 256 tokens; no shell expansion | empty | G/I | LaunchWrapperCommand | Next launch | No |
| game.pre-launch | Explicit user-authored platform shell command, at most 32768 characters, no NUL | empty | G/I | LaunchAdvanceRun | Next launch | No |
| game.pre-launch-wait | Bool | true | G/I | LaunchAdvanceRunWait; instance WaitForPreLaunchCommand | Next launch | Yes |
| game.auto-repair | Bool | true | G/I | LaunchAutoRepairGame | Next launch | Yes |
| game.process-priority | normal / below-normal / above-normal / high / real-time | normal | G/I | LaunchArgumentPriority 1/2/0/3/4 | Next launch | Yes |
| game.server | Server host with optional port, at most 512 characters, no whitespace/control/scheme | empty | G/I | New | Next launch | Yes |
| network.proxy-mode | 0 / 1 / 2 (none / system / custom) | 1 | G | SystemHttpProxyType | Next task | Yes |
| network.proxy-address | Text, absolute HTTP/HTTPS/SOCKS5 URI when custom | empty | G | SystemHttpProxy | Next task | No |
| network.proxy-user | Text | empty | G | SystemHttpProxyCustomUsername | Next task | No |
| network.proxy-password | Text | empty | G | SystemHttpProxyCustomPassword | Next task | No |
| network.doh | Bool | true | G | SystemNetEnableDoH | Next task | Yes |
| network.ip-stack | auto / ipv4 / ipv6 address-family preference with other-family fallback | auto | G | New | Next task | Yes |
| network.bandwidth-kib | Integer, 0–1048576 KiB/s application transfer-body budget; 0 unlimited | 0 | G | ToolDownloadSpeed with explicit slider conversion | Next task | Yes |
| network.file-concurrency | Number, 1–64 game files per batch | 8 | G | Nondefault ToolDownloadThread + 1, capped at 64 | Next task | Yes |
| network.file-retry | Bool, one additional file/asset-index attempt | true | G | New | Next task | Yes |
| network.game-source | official-first / mirrors-first / official-only | official-first | G | ToolDownloadSource 1 / 0 / 2 | Next task | Yes |
| install.inherit-vanilla | Bool | false | G | New | Next task | Yes |
| diagnostics.telemetry | Bool | false | G | TelemetryExperienceProgram | Immediate | Yes |
| diagnostics.log-level | auto / 0 error / 1 warn / 2 info / 3 debug / 4 realtime | auto | G | Nondefault SystemLogLevel, no guessed auto encoding | Immediate | Yes |
| diagnostics.log-lines | Integer, 50–2000 retained UI entries | 500 | G | SystemMaxLog piecewise slider conversion; unlimited bounded at 2000 | Immediate | Yes |
| diagnostics.disk-log-days | Integer, 1–90 days for owned disk archives | 7 | G | New | Immediate, asynchronously applied by disk worker | Yes |
| updates.channel | build / stable / alpha / beta / ci | build | G | New (no guessed numeric conversion) | Next task | Yes |
| updates.auto-check | Bool | true | G | SystemUpdateMode 3 disables discovery | Next task | Yes |
| developer.enabled | Bool | false | G | New (not SystemDebugMode) | Immediate | Yes |

## Completion value contracts

These 47 additional definitions complete the 97-key schema. `New` means no legacy-key
conversion is declared. An enum choice named `auto` is a custom string value; it does not
grant the payload-free `Auto` override mode. None of these additional keys supports that
mode. `G/I` also permits the bounded Profile/Temporary lifecycle described below.

| Key | Type / domain | Builtin | Scope | Legacy source | Applies | Export |
|---|---|---|---|---|---|---|
| game.gpu-preference | Enum: auto, secondary | auto | G/I | New | Next launch | Yes |
| game.renderer | Enum: auto, mesa-software | auto | G/I | New | Next launch | Yes |
| game.system-glfw | Bool | false | G/I | LaunchUseSystemGlfw | Next launch | Yes |
| appearance.hardware-acceleration-disabled | Bool | false | G | SystemDisableHardwareAcceleration | Restart | Yes |
| game.environment | Text, at most 32768 characters; at most 128 unique ASCII KEY=VALUE lines | empty | G/I | New | Next launch | No |
| game.classpath-head | Text, at most 32768 characters; at most 128 fully qualified path lines in order | empty | G/I | New | Next launch | No |
| game.post-exit | Explicit user-authored platform shell command, at most 32768 characters, no NUL | empty | G/I | New | Next launch; runs after actual game exit | No |
| game.safe-launch | Bool | false | G/I | New | Next launch | Yes |
| general.autostart | Bool | false | G | New | Immediate | Yes |
| general.file-association | Bool | false | G | New | Immediate | Yes |
| general.native-notifications | Bool | true | G | New | Immediate | Yes |
| general.clipboard-detection | Bool | false | G | New | Immediate | Yes |
| appearance.window-opacity | Integer, 40–100 percent | 100 | G | New | Immediate | Yes |
| appearance.window-blur | Bool | false | G | New | Immediate | Yes |
| network.provider-modrinth | Bool | true | G | New | Next task | Yes |
| network.provider-curseforge | Bool | true | G | New | Next task | Yes |
| network.provider-official | Bool | true | G | New | Next task | Yes |
| network.provider-mirror | Bool | true | G | New | Next task | Yes |
| network.trace | Bool | false | G | New | Next task | Yes |
| network.auto-diagnose | Bool | false | G | New | Next task | Yes |
| appearance.custom-theme | Text, empty or at most 4096 characters of JSON with exactly background/foreground/accent as #RRGGBB | empty | G | New | Immediate | Yes |
| appearance.logo-path | Text, empty or absolute local path, at most 4096 characters, no controls | empty | G | New | Immediate | No |
| appearance.background-path | Text, empty or absolute local path, at most 4096 characters, no controls | empty | G | New | Immediate | No |
| appearance.background-fit | Enum: cover, contain, stretch | cover | G | New | Immediate | Yes |
| appearance.background-color | Text: auto or #RRGGBB | auto | G | New | Immediate | Yes |
| appearance.reduced-motion | Bool | false | G | New | Immediate | Yes |
| appearance.background-opacity | Integer, 0–100 percent | 100 | G | New | Immediate | Yes |
| appearance.video-path | Text, empty or absolute local path, at most 4096 characters, no controls | empty | G | New | Immediate | No |
| appearance.video-auto-pause | Bool | true | G | New | Immediate | Yes |
| music.enabled | Bool | false | G | New | Immediate | Yes |
| music.path | Text, empty or absolute local path, at most 4096 characters, no controls | empty | G | New | Immediate | No |
| music.startup | Bool | false | G | New | Restart | Yes |
| music.autoplay | Bool | true | G | New | Immediate | Yes |
| music.shuffle | Bool | false | G | New | Immediate | Yes |
| music.volume | Integer, 0–100 percent | 50 | G | New | Immediate | Yes |
| music.media-controls | Bool | true | G | New | Immediate | Yes |
| general.jump-list | Bool | true | G | New | Immediate | Yes |
| general.notification-actions | Bool | true | G | New | Immediate | Yes |
| general.startup-page | Enum: launch, install, resources, settings, java, storage, about, tasks | launch | G | New | Next launch; first committed startup read | Yes |
| general.launch-hints | Bool | true | G | New | Immediate | Yes |
| network.auto-install-dependencies | Bool | true | G | New | Next task | Yes |
| network.resource-source | Enum: follow-request, official-first, mirrors-first | follow-request | G | New | Next task | Yes |
| music.auto-pause | Bool | true | G | New | Immediate | Yes |
| network.background-download | Bool | true | G | New | Next task | Yes |
| diagnostics.ai.enabled | Bool | false | G | New | Immediate | Yes |
| diagnostics.ai.reasoning | Enum: provider, low, medium, high | provider | G | New | Immediate | Yes |
| storage.backup-keep-count | Integer, 1–1024 backups | 32 | G | New | Immediate | Yes |

Graphics choices are revalidated before process creation. On Linux, `secondary` requires
observed DRM/PCI Mesa evidence and supplies the corresponding `DRI_PRIME` selector;
`mesa-software` requires local Mesa/DRI libraries and supplies `LIBGL_ALWAYS_SOFTWARE=1`.
Non-auto choices are unsupported on other platforms, missing evidence rejects the launch,
and conflicting explicit environment values are rejected. These are environment requests,
not proof of the game's actual rendering device. `game.system-glfw` only replaces the
request when its effective source is not Builtin, preserving explicit request/legacy
metadata otherwise; Safe Launch forces it false. See [XSR-813](XSR-813-launch-ia-consumers.md).

`appearance.hardware-acceleration-disabled` uses the negative legacy flag with positive
Hardware Acceleration UI wording through `InvertBoolean`. Its committed value is captured
before native startup and forces software rendering on Windows/Linux after restart;
macOS reports `PlatformUnsupported`. CLI Safe Mode forces that startup preference for the
current session without persisting a changed setting. It controls the launcher's native
UI backend, separately from the game's GPU/renderer policies.

OS preferences use current-user native adapters with explicit unsupported/dependency
results. Jump List is Windows-only; file associations cover `.mrpack`/`.nexapack` on supported
platforms, not arbitrary files. Clipboard detection reads on focus only when enabled and
requires confirmation of recognized `nexacl://` navigation. Window blur is a supported
compositor mode hint, without strength/sampling controls. Local PNG consumers enforce
16 MiB/4096×4096 budgets; audio/video require their local playback engines and own process
cleanup. Reduced motion pauses video and disables dynamic UI effects; music/video auto-pause
uses window inactivity or game quiet state. See [XSR-803](XSR-803-system-preferences-completion.md).

Provider gates apply to new requests for the named content authorities, preserving login
and update endpoints. Trace contains bounded host/status/timing facts without credentials
or payloads; automatic probes are bounded anonymous requests. Dependency auto-install off
still validates the entire required graph and rejects missing required dependencies.
Background-download off rejects new Background/Idle transfers while allowing Interactive
requests. Startup-page applies once and explicit CLI/URI/file activations take precedence;
see [XSR-815](XSR-815-general-network-completion.md). AI enablement only admits explicit
preview/send, with ephemeral per-request credentials; reasoning is a previewed provider
parameter. Backup count is consumed by existing CAS prune previews and opt-in maintenance,
with policy revalidation before mutation; see [XSR-814](XSR-814-scoped-diagnostic-preferences.md).

The animation row is positive UI wording backed by a negative legacy flag; catalog `InvertBoolean` makes this explicit. Repeated title rows share `game.title`, and instance server defaults use `game.server`.

Legacy memory uses the existing piecewise slider-to-MiB conversion. A custom new MiB value is stored exactly; it is not rounded back into a lossy slider coordinate. Global Auto/reset clears the old manual policy. Stage 4 consumers must read the effective contract before exposing the new editor. Window mode maps fullscreen to legacy 0 and windowed to 1. Existing unchanged legacy keys remain byte-compatible.

Apply timing is not a claim that a setting has a working consumer.
Consumers now include memory/Java acquisition, animation/window lock, game-file batch
and source policy, default servers/automatic repair, and preferred Java distributions;
startup region formatting and the live animation tick rate; see XSR-761 through XSR-768
and the current migration ledger. Other unconnected entries remain unavailable.

Profile and Temporary have actual editing, resolution and launch consumers. Resolution is
Builtin → Global → Instance → selected Profile → Temporary. Named profiles bind to a
fully qualified instance identity and persist their values, selection and owned-overlay
configuration through revision-checked settings transactions. An instance has at most 32
profiles, each at most 128 values; identifiers use 1–64 ASCII letters/digits/`-`/`_`, and
names use 1–128 printable characters. Profiles only override instance-eligible keys.
Temporary values and overlay sources remain in memory for the current launcher lifecycle;
begin/end commands validate the expected revision and revoke the layer explicitly. Generic
`Set` supports Global/Instance/existing Profile; Temporary edits use its lifecycle commands.
Changes do not mutate a launch plan already captured.

Owned mods/resourcepacks/shaderpacks/config overlays use exclusive directory leases,
bounded read-only sources and recovery journals. Failure, cancellation and actual game exit
restore the original directories; restart recovery checks actual process identity/liveness
before moving anything. Safe Launch clears custom JVM/game arguments, environment, classpath,
wrapper, pre/post hooks, graphics choices and System GLFW, and temporarily removes all four
directories. It retains required launch/authentication inputs and mandatory verification.
Launcher exit waits only for active owned recovery/post-exit effects. Profile-aware recovery
targets the selected durable layer, and active Temporary must be revoked before durable
baseline capture/restore. See [XSR-802](XSR-802-launch-profile-completion.md) for the complete
cancellation, rollback, recovery, ordinary-game mutual-exclusion and bounded hook contracts.

XSR-783–788 connect scoped launch commands/waiting, managed-runtime removal,
proxy/DoH/address-family and transfer budgets, committed theme/accent, bounded disk
retention/export, and previewed storage transactions. The compatibility definition
is retained obsolete, not an optional future bypass. Java compatibility checks stay
mandatory. `Set`, batch mutation, import preview/apply, and Profile/Temporary writes to
`java.compatibility` reject explicitly; exports omit it. Retained old data cannot disable
the checks, and the catalog does not expose a mutable toggle. The `Next task` network timing above
means capture at the next HTTP request or admitted transfer; active responses and
transfers retain their captured transport or bandwidth generation. See the slice
documents and ledger for consumer-specific limits and validation status.

`network.bandwidth-kib` reads legacy `ToolDownloadSpeed` coordinates 0–41 using
MiB/s formulas `(x + 1) * 0.1` for 0–14, `(x - 11) * 0.5` for 15–31, and
`x - 21` for 32–41, rounded to whole KiB/s after multiplying by 1024. Coordinate
42 maps to the new unlimited default. Exact new KiB/s values stay in the layered
document; reset restores the legacy unlimited coordinate without lossy reverse
rounding. Captured generations share one limiter across admitted download bodies;
the setting does not limit unrelated OS traffic, protocol overhead or helper processes.

Proxy mode/address/user/password apply as one revision-checked form batch. Custom
endpoints exclude embedded credentials, path/query/fragment and retain the existing
TLS and authority checks. Credentials remain local-only and sensitive UI fields do
not become scene text or clipboard copy. IP-family preference changes connection
attempt order with fallback; DoH failures retain system-DNS fallback.

Theme changes apply to the current scene and native requested theme from committed
settings, including OS notifications for System mode. Unknown legacy custom palettes
are retained for a future explicit import contract. Disk age retention is separate
from UI-entry retention; the sink also imposes 4 MiB active-file and 32 archive /
64 MiB ceilings. Its explicitly selected export includes bounded disk header facts,
with free-form text and arbitrary files excluded rather than exported after pattern
redaction. The writer barrier captures bounded read handles and lengths so later
rotation/pruning cannot invalidate the export snapshot. Local time has no inferred
UTC date in the existing disk format.

XSR-769 exposes the existing import/export transaction in Storage and Migration and
instance Game settings. Host-selected files are bounded by actual UTF-8 bytes;
export replaces its destination only after successful staging. Import requires a
preview and explicit confirmation, and navigation or scope loss retires the pending
operation. The JSON contract and local-only filtering below remain unchanged.

Import/export format: `{ "version": 1, "scope": "global" | "instance", "values": { "key": { "mode": "Custom" | "Auto" | "Inherit", "value": "..." } } }`. Auto/Inherit omit the payload. Instance imports use a directory identity supplied separately; exports do not carry machine-specific instance locations. Unknown keys, local-only keys, wrong scopes, invalid values and unsupported versions are rejected. Missing keys leave current values unchanged. Preview has no persistence or state effects; apply revalidates and checks its revision.

## Committed effective reads

Effective queries read one immutable committed revision and values snapshot without
acquiring the store's persistence gate. Ordinary pending saves leave readers on the previous
snapshot; failed saves do not publish a new snapshot. Profile/Temporary queries serialize
with their lifecycle transactions under the profile coherence lock, so they are not claimed
to be lock-free during those transactions. The durable writer publishes the complete
snapshot before revision notifications.


## Scoped reset
A sealed reset preview captures the committed revision and proposes Inherit mutations
only for available catalog-backed definitions overridden in the requested layer. Global
reset preserves reserved keys and all instances; instance reset preserves global and
other-instance values. Apply recomputes, checks the expected revision and commits once.
No-op previews do not write. Persistence failure leaves the complete prior snapshot.
