# XSR-793 — Account wardrobe

The launch-page wardrobe is a real account destination. Desktop reads credential-free
typed queries and sends typed commands; it never resolves the account store or sends
provider HTTP requests. Accounts owns authentication, validation, upload, cape ownership,
and durable profile updates. No renderer, Avalonia, or Sidecar dependency enters Accounts.

## Identity and cancellation

An operation captures the selected account and its roster/selection generations. A stale
selection, deletion, replacement, or removal/re-addition rejects admission and any local
commit. Provider calls use the captured identity; cancellation is checked before and after
each await. Leaving the page or changing accounts cancels the page generation and clears
pending drafts/results. Late queries, file pickers, and commands cannot update the next
account's page. A request already accepted by a remote provider may have taken effect
before cancellation; the next refresh obtains that provider's current truth.

## Skin and cape behavior

Microsoft accounts upload to Minecraft's authenticated skin endpoint and list, activate,
or clear owned capes through Minecraft's profile API. LittleSkin accounts reuse its
authenticated upload and closet/player APIs. Other account kinds expose their actual
read-only preview rather than pretend to support provider mutations.

Skin input is a bounded regular PNG file: at most 1 MiB, complete PNG chunks and CRCs,
bounded valid decompression, and exactly 64×64 or classic-only 64×32 pixels. Upload uses
the exact validated bytes and chosen classic/slim model. The page previews the validated
image before upload. Successful skin writes update the same admitted profile durably and
refresh the existing avatar projection. Provider credentials and response bodies are
excluded from UI state, feedback, and query results.

Capes are selected by an owned ID, never an arbitrary URL. Clearing a cape uses the
provider's real remove operation. A refresh after every attempted mutation obtains the
current admission stamp, including when credentials rotated before an HTTP failure.
Provider text responses are streamed with a 1 MiB limit, including unknown-length bodies;
cape inventories are limited to 256 entries and Desktop renders 12 entries per page.
LittleSkin player names follow the authenticated UUID's current session name.

## Validation

Contract tests cover PNG corruption and budgets, Microsoft upload/cape activation/removal,
unsupported accounts, stale roster/selection admission and late responses. Desktop tests
cover the real destination, typed dispatch, preview, cancellation and account isolation.
Architecture and AOT validation are performed with the repository's normal checks.

## Executed integration evidence

2026-10-05: Release build, Services 599 and Desktop 176 under CoreCLR and
NativeAOT, the 70-project architecture gate, and NativeAOT/trimmed Desktop
`--validate-shell` and `--validate-setup` all passed. The complete execution
record and platform/acceptance limits are in
[XSR-795](XSR-795-unimplemented-inventory.md#本轮集成交付与验证).
