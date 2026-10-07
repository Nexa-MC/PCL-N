# Settings value contracts

The original IA enumerated 566 positions. The current catalog has 540 after removals and
consumer-backed additions; see [settings migration status](settings-migration-status.md)
for working consumers. Groups, choices, actions and facts do not own persisted values.
Reserved settings remain `NotImplemented` until a real consumer exists. There are
50 value definitions and 49 available consumers; the retained `java.compatibility`
definition remains unavailable while compatibility checks stay mandatory.

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
| java.compatibility | Bool | true | G/I | New | Next launch | Yes |
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

The animation row is positive UI wording backed by a negative legacy flag; catalog `InvertBoolean` makes this explicit. Repeated title rows share `game.title`, and instance server defaults use `game.server`.

Legacy memory uses the existing piecewise slider-to-MiB conversion. A custom new MiB value is stored exactly; it is not rounded back into a lossy slider coordinate. Global Auto/reset clears the old manual policy. Stage 4 consumers must read the effective contract before exposing the new editor. Window mode maps fullscreen to legacy 0 and windowed to 1. Existing unchanged legacy keys remain byte-compatible.

Apply timing is not a claim that a setting has a working consumer.
Consumers now include memory/Java acquisition, animation/window lock, game-file batch
and source policy, default servers/automatic repair, and preferred Java distributions;
startup region formatting and the live animation tick rate; see XSR-761 through XSR-768
and the current migration ledger. Other unconnected entries
remain unavailable. Profile and Temporary have resolution
semantics but reject public mutation.

XSR-783–788 connect scoped launch commands/waiting, managed-runtime removal,
proxy/DoH/address-family and transfer budgets, committed theme/accent, bounded disk
retention/export, and previewed storage transactions. The compatibility definition
above is retained for the future optional policy; mandatory Java compatibility is
not disabled or exposed as a working toggle. The `Next task` network timing above
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

## Nonblocking effective reads
Effective queries read one immutable committed revision and values snapshot. They never acquire the persistence gate. While Save is pending, readers retain the previous snapshot; failed saves do not publish a new snapshot. The durable writer publishes the complete snapshot before revision notifications.


## Scoped reset
A sealed reset preview captures the committed revision and proposes Inherit mutations
only for available catalog-backed definitions overridden in the requested layer. Global
reset preserves reserved keys and all instances; instance reset preserves global and
other-instance values. Apply recomputes, checks the expected revision and commits once.
No-op previews do not write. Persistence failure leaves the complete prior snapshot.
