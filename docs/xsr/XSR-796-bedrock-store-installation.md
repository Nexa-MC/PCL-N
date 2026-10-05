# XSR-796 · Bedrock official Windows installation handoff

## Boundary and migration contract

The reachable Bedrock installation page becomes an explicit handoff to the official
Minecraft for Windows product. This slice does not add a Bedrock package service,
download/install transaction, purchase API or license handling. Microsoft Store
owns acquisition, entitlement, payment and installation. The launcher does not
claim that opening the store installs the game or that an installation completed.

`BedrockInstallPageController` loads the existing PXML page and handles only the
two commands emitted by its own live buttons while its page is current. UI.Next
continues to render presentation state and emit intent. Desktop composition supplies
the native effects; the renderer does not open external applications or resolve a
service. The controller defaults its store capability to `OperatingSystem.IsWindows()`;
the optional fixture override does not change production platform support.

The store effect has one fixed destination:
`ms-windows-store://pdp/?productid=9NBLGGH2JHXJ`. The browser effect has one fixed
HTTPS destination:
`https://www.xbox.com/en-US/games/store/minecraft-for-windows/9NBLGGH2JHXJ`.
Neither destination is taken from an intent payload, setting, query or user text.
The native store effect is supplied as a parameterless action so presentation
cannot select an arbitrary protocol URI. Native dispatch is synchronous and contains
no background task, completion callback, fabricated progress or polling.

## Product behavior

Windows users can choose “打开 Microsoft Store” to continue installation there,
or open the official product website. Other platforms display that Minecraft for
Windows installation is unsupported, disable the Microsoft Store action, and retain
the website action for reading the official product information. Website access on
another platform is not a claim that its Bedrock client can be installed there.

Opening or rendering the page never starts either native effect. Only the matching
live button on the current page authorizes its effect. A forged command with an
empty source, foreign entity, other button, hidden page or disposed controller has
no effect. Ordinary native dispatch failures produce a fixed, actionable message
on the page and shared error feedback; raw exception text and paths are not shown.
The page can be retried after a failure.

Presentation changes apply at the render boundary. Navigation retirement has no
pending operation to cancel: a synchronous handoff cannot later update a different
page. Disposing unsubscribes both intent and frame handlers and retires the owned
page. If a supplied effect causes navigation or disposal itself, its completion
cannot publish feedback or alter the retired page.

## Validation

Desktop contract tests exercise actual renderer button activation with native
effects replaced by recorders. They cover exact official website identity, no
automatic effects on page/render, source/page authorization, non-Windows rejection,
ordinary dispatch faults with private exception details, retry, navigation and
disposal retirement. Native Windows Store availability, entitlement, purchase and
the eventual install outcome require platform/user evidence and are not asserted
by the Linux fixture. This slice adds no dynamic code, service reference in UI.Next,
IPC or asynchronous native lifecycle.

## Executed integration evidence

2026-10-05: Release build, Services 599 and Desktop 176 under CoreCLR and
NativeAOT, the 70-project architecture gate, and NativeAOT/trimmed Desktop
`--validate-shell` and `--validate-setup` all passed. The complete execution
record and platform/acceptance limits are in
[XSR-795](XSR-795-unimplemented-inventory.md#本轮集成交付与验证).
