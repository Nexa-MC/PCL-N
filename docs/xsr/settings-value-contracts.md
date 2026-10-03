# Settings value contracts (stages 1–2)

The original IA enumerated 566 positions. The current catalog has 535 after removals and
the added default-server form field; see [settings migration status](settings-migration-status.md)
for working consumers. Groups, choices, actions and facts do not own persisted values.
Reserved settings remain `NotImplemented` until a real consumer exists.

The following foundation contracts are declared in `SettingsPolicySchema`. Owner: `Nexa.Services.Settings`. Scope `G/I` permits global and instance overrides; `G` is global only. `Auto` is a payload-free mode; reset means remove the override. Enum strings are stable encodings, not localized labels.

| Key | Type / domain | Builtin | Scope | Legacy source | Applies | Export |
|---|---|---|---|---|---|---|
| general.language | Enum (auto, zh-Hans, zh-Hant, en) | auto | G | UiLanguage | Immediate | Yes |
| general.region | Named formatting culture / auto / follow-language (legacy ui-language accepted) | auto | G | UiFormatCulture | Restart | Yes |
| appearance.animations-disabled | Bool | false | G | SystemDisableUiAnimations | Immediate | Yes |
| appearance.animation-fps | Number, 1–240 actual fps | 60 | G | UiAniFPS + 1 (write fps - 1) | Immediate | Yes |
| appearance.lock-window | Bool | false | G | UiLockWindowSize | Immediate | Yes |
| appearance.low-power | Bool | false | G | UiUltraLowPowerMode | Immediate | Yes |
| java.runtime | Fully qualified path / Auto | Auto | G/I | New | Next launch | No |
| java.auto-install | Bool | false | G/I | New | Next launch | Yes |
| java.vendor | Enum: empty means automatic; supported Java brand names | empty | G/I | New | Next launch | Yes |
| java.compatibility | Bool | true | G/I | New | Next launch | Yes |
| game.memory | Number, 256–1048576 MiB / Auto | Auto | G/I | LaunchRamType + LaunchRamCustom | Next launch | Yes |
| game.window-mode | windowed / fullscreen | windowed | G/I | LaunchArgumentWindowType | Next launch | Yes |
| game.width | Number, 1–32768 px | 854 | G/I | LaunchArgumentWindowWidth | Next launch | Yes |
| game.height | Number, 1–32768 px | 480 | G/I | LaunchArgumentWindowHeight | Next launch | Yes |
| game.title | Text | empty | G/I | LaunchArgumentTitle | Next launch | Yes |
| game.jvm | Text | existing LauncherDefaults JVM string | G/I | LaunchAdvanceJvm | Next launch | No |
| game.arguments | Text | empty | G/I | LaunchAdvanceGame | Next launch | No |
| game.wrapper | Text | empty | G/I | LaunchWrapperCommand | Next launch | No |
| game.pre-launch | Text | empty | G/I | LaunchAdvanceRun | Next launch | No |
| game.auto-repair | Bool | true | G/I | LaunchAutoRepairGame | Next launch | Yes |
| game.server | Server host with optional port, at most 512 characters, no whitespace/control/scheme | empty | G/I | New | Next launch | Yes |
| network.proxy-mode | 0 / 1 / 2 (none / system / custom) | 1 | G | SystemHttpProxyType | Next task | Yes |
| network.proxy-address | Text, absolute HTTP/HTTPS/SOCKS5 URI when custom | empty | G | SystemHttpProxy | Next task | No |
| network.proxy-user | Text | empty | G | SystemHttpProxyCustomUsername | Next task | No |
| network.proxy-password | Text | empty | G | SystemHttpProxyCustomPassword | Next task | No |
| network.doh | Bool | true | G | SystemNetEnableDoH | Next task | Yes |
| network.file-concurrency | Number, 1–64 game files per batch | 8 | G | Nondefault ToolDownloadThread + 1, capped at 64 | Next task | Yes |
| network.file-retry | Bool, one additional file/asset-index attempt | true | G | New | Next task | Yes |
| network.game-source | official-first / mirrors-first / official-only | official-first | G | ToolDownloadSource 1 / 0 / 2 | Next task | Yes |
| diagnostics.telemetry | Bool | false | G | TelemetryExperienceProgram | Immediate | Yes |
| updates.channel | stable / alpha / beta / ci | alpha | G | New (no guessed numeric conversion) | Next task | Yes |
| developer.enabled | Bool | false | G | New (not SystemDebugMode) | Immediate | Yes |

The animation row is positive UI wording backed by a negative legacy flag; catalog `InvertBoolean` makes this explicit. Repeated title rows share `game.title`, and instance server defaults use `game.server`.

Legacy memory uses the existing piecewise slider-to-MiB conversion. A custom new MiB value is stored exactly; it is not rounded back into a lossy slider coordinate. Global Auto/reset clears the old manual policy. Stage 4 consumers must read the effective contract before exposing the new editor. Window mode maps fullscreen to legacy 0 and windowed to 1. Existing unchanged legacy keys remain byte-compatible.

Apply timing is not a claim that a setting has a working consumer.
Consumers now include memory/Java acquisition, animation/window lock, game-file batch
and source policy, default servers/automatic repair, and preferred Java distributions;
startup region formatting and the live animation tick rate; see XSR-761 through XSR-768
and the current migration ledger. Other unconnected entries
remain unavailable. Profile and Temporary have resolution
semantics but reject public mutation.

Import/export format: `{ "version": 1, "scope": "global" | "instance", "values": { "key": { "mode": "Custom" | "Auto" | "Inherit", "value": "..." } } }`. Auto/Inherit omit the payload. Instance imports use a directory identity supplied separately; exports do not carry machine-specific instance locations. Unknown keys, local-only keys, wrong scopes, invalid values and unsupported versions are rejected. Missing keys leave current values unchanged. Preview has no persistence or state effects; apply revalidates and checks its revision.

## Nonblocking effective reads
Effective queries read one immutable committed revision and values snapshot. They never acquire the persistence gate. While Save is pending, readers retain the previous snapshot; failed saves do not publish a new snapshot. The durable writer publishes the complete snapshot before revision notifications.

