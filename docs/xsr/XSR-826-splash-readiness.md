# XSR-826 — Startup readiness and native handoff

## Contract

The first visible window is the native startup window. Its dispatcher remains responsive
while storage recovery, composition, local state reads, controller preparation and native
render preparation run. Closing it cancels the startup attempt. An initialization failure
stays on that window and offers retry; a failed attempt never reveals the product shell.

Startup has two terminal readiness branches. First-run readiness requires the setup status,
setup controller, language/appearance projection and a prepared native setup scene. Normal
readiness requires foundation settings/profiles/accounts and state declarations, runtime
routes, the initial local instance scan, settings catalog/effective metadata, all attached
controller structures and the selected startup destination's finite initial queries. Local
recovery receipts finish before the initial instance scan. Sidecar discovery and protocol
registration finish once; local custom appearance assets, media engine validation/first
video frame, initial updater receipt/channel policy and initial system policy finish before
the last native preparation pass. The real shell remains hidden throughout this work.

The native preparation pass uses the actual shell window and scene controls. It measures
and arranges the initial viewport, drains state/controller projections, resolves text fonts,
decodes raster assets, resolves icon geometry and draws an off-screen frame. Handoff shows
that prepared window, transfers the main-window lifetime, then closes the startup window.
No main window may be shown by activation or preference callbacks before handoff.

## Finite work and reuse

Readiness names a finite set of initial facts; it does not enumerate all worlds, download
every catalog, open every wardrobe page or wait for installation/download completion.
Settings recovery, world inventories, resource detail, search, translation, skin preview,
Java detection and similar queries execute only when the actual startup destination needs
them. Queries for other destinations remain normal user-driven work.

Reusable online facts are read through the same runtime/cache path used by controllers:
fresh local snapshots are reused, retained snapshots can be displayed with a stale/offline
notice, and missing online facts reach an explicit offline/timeout result within a bounded
startup budget. Refresh uses the shared cache's single-flight path in the background;
startup must not launch duplicate provider requests or persist credentials, access tokens,
device codes or authentication results. A network outage cannot keep the startup window
open indefinitely.

Policy workers, rollout refresh, telemetry, task recovery/downloads and hint/animation
clocks have application lifetimes. Readiness observes only their initial finite policy
application or local facts, never their infinite worker tasks. Retry owns a new composition
attempt and disposes the failed attempt before creating another one.

## Ownership and checks

`DesktopStartupReadiness` orders and reports named steps. `Program.Startup` contains the
normal and first-run preparation recipes. Controller startup entry points consume their
existing query tasks and commit results into their real retained pages. The Avalonia
startup session owns hidden native preparation, cancellation, retry and the visibility
handoff. Services stay independent of Desktop and Avalonia.

Contract tests cover step order/deduplication, bounded optional initialization, cancellation,
mandatory failure and retained settings metadata before navigation. Native checks cover
retry, hidden activation, prepared scenes, settled missing media and lifetime ownership.
Compilation, runtime and AOT validation outcomes are recorded with the change's review.
