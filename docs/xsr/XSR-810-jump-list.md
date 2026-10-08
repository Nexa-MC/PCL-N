# XSR-810 — Windows launcher Jump List and macOS document declaration

2026-10-08 (Asia/Shanghai). Windows integration owns only AppUserModelID `NexaCL`.
The reversible `general.jump-list` policy publishes four static tasks: launch, install,
resources and settings. Tasks target the launcher executable (or dotnet plus the entry
assembly) with exactly one existing `nexacl://` route. There is no arbitrary argument,
external message or undocumented shell command. Disabling deletes only this AppID's list.

The adapter uses explicit native COM vtables with the documented interface identifiers for
ICustomDestinationList, IObjectCollection, IShellLinkW and IPropertyStore. COM apartment,
interface references, BeginList/CommitList/AbortList and pinned property values have explicit
ownership. No reflection or runtime COM callable wrapper is used. HRESULT failures remain
visible policy errors and never update the applied-setting receipt.

macOS release packaging declares `nexacl` and `.mrpack`/`.nexapack` in Info.plist before
signing. This immutable bundle declaration offers Open With; it is not a mutable user default
association toggle. Document events feed the same bounded local-file confirmation workflow
as command-line/single-instance activation. Runtime never edits the signed bundle.

Contract validation checks bounded absolute launcher commands, correct Windows argument
quoting including trailing separators, four allowed routes, non-Windows unsupported behavior,
and independently parsed macOS plist declarations. Windows shell interaction and signed
macOS Open With remain native acceptance evidence outside Linux deterministic validation.
