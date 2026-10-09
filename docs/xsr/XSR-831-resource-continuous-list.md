# XSR-831 — Bounded continuous resource results

2026-10-09. Resource search becomes a pseudo-infinite list: approaching the current bottom
automatically requests and appends the next catalog page. This is not responsive column
count selection. Existing catalog search/detail/source/cache contracts stay unchanged.

## Query ownership

A list generation captures the submitted kind, text, game, loader, order and effective
network/source policy. Only one next-page request is admitted at a time. Appending does not
reread unsubmitted search drafts. A successful matching-generation/page response advances
the cursor; a failure preserves accumulated rows and scrolling and offers an explicit retry
of the same page. It does not retry on every render frame. Search, category, sort, compatibility
or source-policy replacement retires outstanding work and begins a fresh generation at page 0.
Leaving for details pauses admission while retaining the list for back navigation.

Provider `HasMore` is authoritative when supplied; otherwise the existing page/total contract
determines continuation. Raw empty pages terminate; hidden installed/library entries do not
pretend the provider reached its end. Provider identity/source facts qualify deduplication;
equal titles cannot collapse unrelated projects. An explicit Load more/retry affordance
remains available for keyboard, accessibility and automatic-loading failure recovery.

## Retained presentation and budgets

Successful pages extend the same list and preserve the current scroll offset. Existing
project entities and their interaction state are reused; appending does not clear/recreate
earlier rows or replay their entry animations. A clipped viewport window with top/bottom
spacers bounds attached row views and image/translation work. Only visible/overscan rows
own active media leases. The accumulated generation is bounded to 1000 distinct projects
and the existing 500-page service limit. Original per-page occurrences are retained separately
from the deduplicated projection, with an additional 16000-occurrence and 8 MiB estimated
UTF-16 text budget. These estimates bound retained catalog facts, not total process memory.
Reaching a budget shows an explicit refine-search notice rather than falsely claiming the remote result set ended.

Installed/library display filters rebuild the projection without discarding submitted search
identity or interpreting a filtered empty batch as a remote end. Favorites remain a local,
bounded list with their existing commands, and detail/install/favorite actions continue to
capture the correct project and source identity.

## Durable information and refresh

Fresh/stale state and background renewal belong to each retained page, including appended
pages. An eligible stale page renews once through the existing shared query/cache path with
its original captured filter and page. A late renewal cannot replace another page or another
generation. Failure keeps retained information and the stale notice; renewal can reconcile
that page's project facts while preserving unrelated pages and their rows. Download and
installation authority continue to use the existing live revalidation paths.

## Verification

Exercise near-bottom admission and single-flight, stable rows/scroll after append, exact-source
deduplication, HasMore/empty/end/budget states, same-page explicit retry, hidden-entry batches,
detail/back reuse, source/search replacement and late cancellation, independent stale-page
renewal, and the attached-row/media bound. Preserve existing provider, identity, Sidecar,
download and settings coverage. Native scrolling checks and relevant AOT/trim verification
must exercise the resulting renderer/controller path.

## Concrete consumer and closed limits

`ResourcesPageController.ContinuousList` owns the submitted query, next-page flight and
retained page metadata. `VirtualRows` projects a fixed 80-pixel row plus the existing
6-pixel spacing; four rows of overscan on either side and a hard 48-row attachment bound
keep image/translation leases finite. The ordinary list uses the renderer's existing
`XsrUiStableContent` marker, so virtual reattachment/back navigation does not replay entry
animations. The previous-page affordance is now Back to top; Next is Load more or Retry
loading. At either hard budget the visible `ResourceListBudget` notice asks for a narrower
search. No new service or renderer API is introduced.

Exact provider references can join cross-provider association facts. A provider-local ID
without source facts belongs to its own unqualified namespace. Deduplication only shapes the projection: each retained page keeps its own raw
membership and facts, so renewal/removal in another page cannot silently delete them. The projection
stays bounded to 1000 distinct projects; page cursor/notice/stale metadata stays bounded
to 500. Two additional raw-retention guards cap duplicate-inclusive occurrences at 16000
and the sum of retained UTF-16 project/source text at 8 MiB. The latter is a text estimate,
not a process-heap claim. Reaching either guard uses the same explicit refine-search
notice and stops admission, rather than claiming a remote end. Budget checks replace a
renewed page within the existing reservation so repeated refresh does not inflate usage.
A retained row view keeps its original entity identity when outside the viewport, but owns no active media until
it reenters the window. Removed facts/new generations destroy their former views.
Favorites use the same bounded local projection and do not dispatch catalog pagination.

A response must match the requested page before advancing. Renewal failure keeps the
stale facts and stops claiming an active refresh; only the most recently admitted page's
renewal can alter continuation. Provider empty-batch detection uses raw returned data,
before display filtering or source deduplication. Neither a short merged batch nor a
duplicate-only batch counts as an implicit end. A failed primary batch waits for the
explicit retry action. Existing detail/install/download routes continue their own live
validation and capture the selected provider-qualified project.

Leaving both resource pages cancels pending list work and all viewport media while retaining
completed rows, scroll and the next cursor. Details pause further page admission; a flight
already admitted is held until return. Generation replacement discards the former flight
and renewals, including providers that complete after cancellation. Startup offline handling
pauses viewport media until navigation or an explicit retry/submitted search.

Seven focused UI tests use controlled typed queries and real renderer scroll snapshots. They
cover retained entity/scroll/drafts, single-flight admission, same-page failure and mismatched
response retries, source/search/global-policy retirement, equal IDs under different sources,
hidden batches, nullable HasMore/empty termination, 1000-project/500-page limits, clipped
media cancellation and late completions, plus independent per-page stale renewals. They are
registered beside the existing cached-information test. Their integration results are recorded below.

## Native routed-input acceptance

`Nexa.Desktop.Tests --native-resource-scroll-smoke` runs in an independent process. It
starts the real Avalonia native lifetime through `AvaloniaUiStartupSession`, warms and
presents the same product shell/window, and reuses the controlled typed catalog fixture.
Public routed wheel events target the displayed surface at translated native-window
coordinates, exercising Backend wheel conversion, UI.Next scrolling and continuous-list
admission. The retry uses actual routed pointer press/release on the footer action.
Assertions cover one pending admission, retained project entities and scroll offsets,
the exact 0/1/2/2 retry cursor, the attached-row bound and a clean native-lifetime exit.
All controller/tree work belongs to the dispatcher; this test does not pump an unrelated
headless viewport or require provider HTTP. This is native-window routed Backend input
acceptance; it does not claim physical-device or XTest delivery. The ordinary managed
regression contains 252 tests and already passed before this independent entry was added;
root integration still owns building/running the native entry and AOT verification.

## Completed integration — 2026-10-09

Release Desktop regression passes all 252 cases, including the seven continuous-list
cases. Controlled typed-provider fixtures cover mismatched-page retries, cancelled/late
responses, hidden/empty batches, 1000-project and 500-page bounds, 16000 duplicate-inclusive
occurrences and the 8 MiB text estimate. Overlap/bridge/renewal tests retain source facts
when an earlier owner changes or empties; deduplication never rewrites per-page membership.
The existing cached-information, provider identity, Sidecar and download consumers remain
covered by the Desktop suite. UI.Next 105, PXML 40, backend 28 and architecture 70 checks pass.

`--native-resource-scroll-smoke` passes in its own real native X11 lifetime under Xvfb,
software rendering and a private DBus session. Routed wheel events enter the actual
Avalonia surface handler and trigger the controller's near-bottom admission. Routed press/
release on Retry produces the captured page sequence 0,1,2,2. The probe observes single-flight
admission, stable row entities/offset after append, suppressed repeated entry animation,
the 48-row bound and clean native exit. This exercises native window/backend routing;
it is not a physical mouse/XTest or live remote-provider acceptance claim.

Desktop NativeAOT and full-trim publishes and their shell/setup validation pass. The source
catalog search, cache and installation authorization contracts remain unchanged.
