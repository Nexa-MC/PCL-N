# XSR-780 — Local log preferences

Global `diagnostics.log-level` applies immediately to the existing LogService gate.
The automatic choice keeps the composition default (including verbose terminal/test
builds). Explicit Error/Warn/Info/Debug/RealTime values retain the legacy meanings
and do not change mandatory diagnostic consent or structured telemetry policy.

Global `diagnostics.log-lines` controls the bounded in-memory/state log history,
50–2000 entries (default 500). Reducing the limit immediately removes oldest entries;
increasing it affects subsequent writes without resurrecting discarded entries.
The logger's construction capacity remains a hard ceiling. Disk log retention is
separate and is delivered by [XSR-787](XSR-787-disk-log-preferences.md); this UI-history
control does not delete or archive disk files.

Settings stores and resolves these values through its existing sealed contracts.
The Foundation composition owns a disposable committed-settings subscriber that
updates LogService; neither rendering nor opening Settings is required. Failed or
stale writes cannot alter the active gate. Disposal removes the observer. Legacy
SystemLogLevel values and the SystemMaxLog slider formula are read compatibly,
with the old unlimited setting bounded at 2000. Exact new line limits live in the
layered document rather than being rounded back into the legacy slider.

Regression coverage: initial persisted policy, live changes without Settings,
failed-save isolation, reset/default restoration, observer disposal, ordered
retention trimming and capacity clamping. Validate architecture, AOT and trim.
