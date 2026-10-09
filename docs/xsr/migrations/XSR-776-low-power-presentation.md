# XSR-776 Low-power presentation

Retired on 2026-10-09 at the user's request by
[XSR-832](../XSR-832-retire-low-power-previews.md). The record below describes the
former migration only. The launcher low-power catalog entry, schema/default and
runtime consumer have been removed. Old persisted `appearance.low-power` and
`UiUltraLowPowerMode` fields are inert and remain preserved user data. Current
regressions cover that retirement and retain user-selected fps and reduced motion.
Native/system power capability reporting keeps its separate contract.

`appearance.low-power` preserves the legacy `UiUltraLowPowerMode` preference and
the eligibility rule: enabled, window inactive/minimized, no active task, launch,
or account sign-in. Missing activity truth never permits suspension. Eligibility
is read from sealed window, task, launch and account state by the Desktop Host
projection, independently of settings navigation.

While eligible, cap the requested animation clock at 10 fps. Foreground activity,
work, or disabling the preference restores the exact user's rate immediately.
Do not change stored fps, elapsed-time motion, reduced-motion preference, native
window animation, network transfers or input/caret clocks. Existing optional
motion suspension and trivia visibility policy remain independently owned.

This intentionally uses the existing demand-driven renderer instead of hiding
the root or forcing GC/working-set trimming as the legacy implementation did.
There is no periodic poll, new idle timer or synchronous Service query.

Regression covers inactive eligibility, task/launch/login exclusions, minimized
windows, preference changes while suspended, invalidation coalescing, restoration
and disposal. Validate architecture, Desktop shell and NativeAOT/trim in CI.
