# Alpha 6 physical acceptance handoff to Dots

User decision, 2026-10-02: no Windows/macOS/Linux test devices are currently available to this
coding session. Physical acceptance is assigned to Dots. The user has no Apple Developer
account; production Developer ID signing and notarization therefore remain externally blocked.
No Windows publisher identity was provided. No signing credential or private key is requested
in this handoff.

The current session has recorded this handoff; it has not located or dispatched a callable
Dots session. Acceptance remains open until Dots supplies reviewed evidence. Hosted CI and
headless shell smoke results must not be relabeled as physical device evidence.

Dots should use the candidate matrix in `../../eng/acceptance/minecraft-matrix.json` and
`alpha6-beta-acceptance.md`, retaining the exact clean commit and binary hashes, OS/architecture,
Java and Loader identities, launch logs, world entry, normal exit and failure/cancellation
results. Each combination must retain its actual outcome, including unsupported and failed
cases. Record 8h native-window idle and 8h running-game sessions on each available physical
platform, plus navigation/search/switch/launch/install pause/resume stress with input latency,
frame tails, CPU/private/physical memory and measured GPU data. Missing metrics stay unknown.

Recovery cases require interruption and conflict evidence for install/modify/snapshot/update
transactions. At the `565e5143` implementation baseline, XSR-754/755 supplies the preinstalled
protected helper, independent verification and transaction/recovery paths. Its physical
replacement/power-loss acceptance is still open; the historical missing-helper condition
must not be treated as a current implementation gap. Record screen-reader, high-contrast, touch/pen/controller and
language/DPI/font results separately. OS publisher trust, SmartScreen and Gatekeeper are
separate from repository GPG integrity. An unsigned macOS build must not be labeled notarized.

Return evidence paths and reviewed outcomes for updates to the acceptance ledger. Do not
change `overall` to accepted while implementation gaps, physical cases or release trust remain.
