# XSR-793 — Account wardrobe

## Dev parity rewrite contract

The initial XSR delivery was a working upload/cape form, not a migration of the
complete `dev` wardrobe. This revision uses read-only `origin/dev` commit
`4755faee0a10686917213afe57cb772ade83d77f` as its behavioral reference:
`PageSkinAppearanceRight`, `PageSkinLibraryRight`, `MainWindow.Appearance`,
`SkinAppearanceHistoryStore`, and `SkinSiteCatalog`.

The wardrobe must present the current full player in a profile rail, with local
skin and library actions, and separate horizontally scrolling skin/cape card
tracks. Cards include previews, names, sources, and provider-permitted apply
actions. Current textures include the cape and classic/slim model. History is a
bounded 80-entry MRU of texture addresses, including other profiles, with optional
atomic persistence; history failures cannot turn a successful provider mutation
into a failure. LittleSkin closet skins and capes use independent OAuth, verify
the selected player and provider readback, and preserve public texture references.
Microsoft cape loading, failure, and empty ownership are distinct; its active
owned cape takes precedence for the current preview. Offline appearance remains
read-only. Third-party operations explain the independent site authorization and
offer a validated browser handoff. NCloud operations use an injected provider
port; an absent external capability remains explicitly unavailable.

The library must provide the LittleSkin site rail, search, skin/cape filters,
time/likes ordering, page navigation, loading/error/empty states, preview cards,
details, documentation, and site links. Public capes cannot be applied to Microsoft
or NCloud accounts. Catalogue/texture HTTP, history I/O, provider admission, and
mutation orchestration remain in Accounts. Desktop owns presentation, file picker
and browser effects; UI.Next receives immutable PNGs and drawing recipes and
performs no account or network I/O. Late reads, writes, pickers, and image replies
must retire on navigation or account generation changes.

Authenticated LittleSkin calls share one forced OAuth refresh retry for 401/403
within a logical operation. Rotated credentials are persisted before subsequent
session or inventory calls; anonymous catalogue/image failures never rotate OAuth.
Provider display-name changes from an admitted read or write do not cancel that
same account's operation. The captured principal and original selection event epoch
must remain unchanged; a successful read must also match the currently published
profile. Mutations retain service admission, and selection ABA or deletion followed
by re-addition retires the original request.

Public catalogue previews use a separate credential-free query identified only
by the fixed site, texture ID, and kind. They remain available when account
authorization fails. Mutations and wardrobe history-card reads retain selected
account admission; a public preview never grants mutation authority.

Legacy source is inspected only. No legacy project, type graph, Avalonia control,
or implementation is copied wholesale into XSR. Tests cover documented behavior,
including provider failure fallbacks and selection isolation, and the normal
architecture, NativeAOT, and trimming gates close this migration.

### Full-player drawing boundary

The player preview is a pure Desktop projection of the documented Minecraft UV
layout and seven `dev` camera views: isometric, front, back, left, right, top and
bottom, in that cycling order. Modern skins have independent left limbs and
expanded transparent overlays; legacy 64×32 skins reuse mirrored right-limb
textures and retain the hat overlay. Slim arm width is three pixels. A cape uses
its own immutable PNG and cuboid UVs. Embedded Steve/Alex fallback bytes are
loaded once, without per-frame file, account or network I/O.

UI.Next's existing rectangle-layer recipe gains optional immutable source-image
overrides, normalized affine transforms, surface shading and ground ellipses.
The image's logical aspect ratio preserves full-player proportions in rectangular
hosts. The Avalonia edge acquires all source leases from its existing bounded
raster pool (at most eight sources and 128 layers per recipe), renders nearest
neighbour faces in supplied depth order, and retires every lease on invisibility,
navigation and surface disposal. Native bitmaps remain exclusively backend-owned;
the existing primary PNG and rectangular-layer constructors remain compatible.
Successful unchanged source leases survive camera changes and capacity retries.
Blocked controls retry missing sources only when pool capacity changes, without
mutually invalidating each other's retry revision on idle frames.

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

Microsoft capes are selected by an owned ID. LittleSkin closet selections and public
references are resolved through the fixed site's authoritative texture metadata before
application. Clearing a cape uses the provider's real remove operation. A refresh after
every attempted mutation obtains the current admission stamp, including when credentials
rotated before an HTTP failure. Provider text responses are streamed with a 1 MiB limit,
including unknown-length bodies; cape inventories are limited to 256 entries. Desktop
retains the complete bounded inventory in horizontal tracks and loads preview images for
the visible card window. LittleSkin player names follow the authenticated UUID's current
session name.

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

The later dev-parity rewrite and its validation are tracked separately in
[XSR-797](XSR-797-wardrobe-dev-parity.md).
