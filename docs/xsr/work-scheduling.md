# Host resource admission and launch quiet lifetime

Alpha.6 / Beta architecture lock, 2026-10-01. Implementation stays in existing Common.Contracts,
Common, Services and Composition assemblies; no renderer service lookup or new assembly.
Rollouts adds a dependency only on Common.Contracts for admission; the implementation remains host-owned.

One Foundation host owns one scheduler. CPU, disk and HTTP admission have independent limits with atomic multi-resource admission and bounded
queues (initially 2/2/4). Critical, Interactive, Background and Idle use weighted
service (8/4/2/1); queued work is cancellable, total queue capacity is 512, and disposal retires
waiters. Leases release once. No dedicated worker thread or idle polling is created. Metrics
are queried on demand; only quiet lifetime changes publish UI state.

Priority context is host-instance-owned AsyncLocal, scoped lexically by trusted launch code.
It is not a public intent or plugin capability. Optional work explicitly chooses Background;
normal user work chooses Interactive. Queues reserve foreground headroom. An explicit catalog read cancels/replaces a lower-priority prefetch for that key so a paused prefetch cannot park a user request. Combined resource masks are admitted atomically to avoid hold-and-wait. A resource lease
must not be held across another acquire of that same resource. Already admitted work completes its safe unit; quiet mode cannot stop
an integrity check or a transaction midway.

Opportunistic maintenance uses nonblocking `TryAcquire`: null means no admission,
without creating a waiter or bypassing runnable queued work. Background/Idle still
cannot enter during quiet. Existing custom scheduler implementations may conservatively
return null through the interface default; this never authorizes work without a lease.
The production scheduler validates and reserves the same atomic resource masks as async
admission. This surface belongs to Common.Contracts/Common, with no new assembly.

Automatic recovery capture uses Background CPU/disk admission for initial plan discovery
and source processing; plan admission is released before worker admission. Source metadata
uses short leases; blob read/hash/compression admits at most 80 KiB per loop, releasing
before another acquire. A large file therefore yields between chunks when quiet begins.
Final compression disposal, durable object publication and failure cleanup remain safe
units; no lease is held across another acquire of the same resource. Necessary
restore, verification, transaction durability and compensation keep their existing behavior.
Optional blob scan/delete uses Idle try-admission in bounded chunks and can stop cleanup
after a committed baseline. A separate admitted probe obtains the object lock; each scan
chunk visits at most 32 entries with a nonblocking lease. It must not queue a shared
resource wait while holding the object lock, recreate an idle timer, or turn maintenance
cancellation into baseline rollback. A disposed scheduler also defers optional cleanup.
Recovery.Storage receives only the Common.Contracts scheduling boundary; production
composition supplies the existing host scheduler. This follow-up does not certify all
recovery/indexing/Sidecar adapters or real Minecraft contention.

The product launch command enters quiet after its exclusive pipeline registration, before
preparation, and scopes Critical priority over file completion/Java/JVM preparation. Quiet
prevents new Background/Idle admission while Critical/Interactive continue. Failure/cancellation
release it. A confirmed window hands the scope to a one-shot 15-second grace; a terminal
session releases earlier. Unsupported/timeout window fallback releases immediately and never
claims a stability confirmation. Multiple quiet scopes are reference-counted, so an older
launch cannot resume work beneath a newer one. Host disposal retires all scopes and timers.

Initial adapters defer optional resource icons, install catalog prefetch, telemetry flush and
background update/rollout checks. Download connections inherit trusted launch priority. Admission does
not authorize a URL, relax a hash/signature, change account policy, or replace cancellation.
The optional icon pipeline retains at most four encoded responses while decode waits, and releases HTTP before CPU admission; catalog local HTTP slots are released before normalization admission. Neither holds shared HTTP while waiting for a paused CPU unit.
Remaining adapters (indexing, Sidecar optional work and other recovery operations) are tracked separately;
the existence of the scheduler does not prove that every subsystem participates.

`runtime.work.quiet` is a typed host fact. Desktop reads it at frame preparation and sets a
UI-owned motion-suspension flag; user Reduced Motion remains independent. Native animation
callbacks read effective motion policy. Hint timers only run when their presentation is
visible, the launcher is active and no quiet/launch work is active; publishers never inspect
render-thread tree state. Native activation/minimization are UI-owned window facts, not services.

Acceptance covers bounded queues, cancellation/grant/disposal races, priority inheritance and
restoration, fair admission, reference-counted quiet, early terminal release, timer retirement,
paused optional work, critical file completion and UI thread ownership. Real game contention,
8h native runs and OS/GPU measurements remain independent evidence.
