# Renamed executable Sidecars, regional profile rules and About

The desktop composition root now discovers top-level `.nsc` files next to Host. These files
are standard native executables renamed from their normal extension; the earlier encrypted
container proposal was superseded by the requested executable rename. No decryption,
manual image mapping or executable extraction is performed.

Each executable requires `<filename>.nsc.asc`, a detached OpenPGP signature made by the pinned
Nexa release key. The key is embedded in Host, independent of the discovery directory.
Release packaging must preserve the Unix executable bit. The Sidecar entrypoint accepts
`--nexa-sidecar --endpoint <endpoint>` and calls `SidecarBootstrap.ConnectAsync` with inherited
stdin before reading Host HELLO. Bootstrap authenticates the child without leaking its
challenge into arguments. Connections must proceed through registration and a full snapshot
before Host publishes an active session; a failed package is diagnosed independently.

The Host receives New UI through existing `UiModule` registration and all six additional
extension kinds through the same bounded binary transaction. Payload hashes and semantic
targets are checked before publication. A corrupt final declaration cannot publish earlier
extensions. Session retirement removes extension entries; cached UI continuity is preserved.
The extension table is a declaration surface; generated function patch execution, UI Patch
application and Event/Intent execution adapters are not claimed as delivered here.

Country policy is selected independently of UI language using `NEXA_COUNTRY` (ISO alpha-2),
otherwise the OS region. Only CN enables BMCLAPI/MCIM optimization. International resource
requests use official APIs and report missing CurseForge configuration without using MCIM.
OptiFine uses local installation from the official site outside CN. This is deployment/OS
region selection, not physical geolocation or tamper-proof license enforcement.

International account additions, imports and type-changing replacements require verified
Minecraft Java ownership. Raw/imported Microsoft kinds and tokens cannot authorize them.
Evidence comes from a successful Microsoft login or a fresh, generation-checked refresh;
it lasts only for the current Host session and is removed when that owner profile is deleted.
CN receives a purchase reminder after creation/import through the existing notification UI.

About body copy wraps without a line cap or ellipsis, including explicit line breaks in the
source/license statement; the page scrolls to keep the complete acknowledgements available.

Settings now has an About tab with copyright derived from the repository LICENSE, Apache
2.0 statement, trademark attribution, acknowledgements and source/license/contributor links.
The new copy is translated for English and Traditional Chinese.

## Validation

The solution build and architecture gate cover the unchanged project boundaries. Protocol
tests cover binary extension contracts and bootstrap rejection; runtime loopback tests cover
transactional admission and retirement. A real renamed-executable integration fixture is
included and runs by default in CI under CoreCLR and NativeAOT. Restricted environments can
explicitly use `--skip-os-ipc`; this reports skipped tests rather than treating physical IPC
as passed. Country tests cover CN, US, GB, HK, MO, TW and unknown country behavior, bypasses
through import/type replacement, and official-only API fallback. Desktop tests exercise
About navigation and scroll to acknowledgements.
Desktop account interaction tests also check the CN purchase reminder and an international
Microsoft login authorizing a later offline-profile addition while a cancelled login stays retired.

Local verification (Linux, .NET SDK 10.0.100): solution build completed with zero warnings
and errors; architecture checks passed for all 32 source projects; all 100 Desktop tests,
33 focused Services tests, 98 runtime tests and 20 Sidecar tests passed. One runtime and
three Sidecar tests were explicitly skipped because this execution environment denies
local Unix sockets (`SocketException: Permission denied`). The real process fixture therefore
still needs an unrestricted OS run; the test remains enabled by default.

The runtime test executable was published with NativeAOT and passed the same 98 tests
with one explicit physical-IPC skip. The environment also denies the socket needed for an
isolated MSBuild task host, so local publishing used a temporary
`CustomAfterMicrosoftCommonTargets` override to execute the existing ILLink tasks in process;
no such environment workaround is included in the repository.
The Desktop Release `linux-x64` publish with `PublishTrimmed=true` and `TrimMode=link`
also completed without warnings or errors using that local task-host override.
