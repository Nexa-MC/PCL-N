# XSR-767 Region format settings

`general.region` retains its legacy text value and restart timing. `auto` uses
the system formatting culture captured before Desktop language setup;
`follow-language` (and the legacy alias `ui-language`) uses the resolved interface
language's formatting culture. Named cultures remain supported and validated.
This setting formats dates and numbers; it never changes RegionalPolicy,
authorization, source routing or telemetry geography.

Settings owns validation, persistence and the sealed effective projection.
DesktopLanguageSession consumes the committed startup value without reading a
service during frames. A new format preference takes effect at the next session;
when that startup preference follows language, live language changes also update
formatting. Session disposal restores the prior culture and text localizer.

The form reuses the right-aligned draggable segmented track: system, interface
language, China, Taiwan and US. A valid existing custom culture is appended using
its native name, preserving its value rather than silently selecting a preset.
The control states that changing the preference requires a restart.

Regression coverage includes independent system/interface cultures, explicit
culture and follow-language modes, restart timing, legacy aliases, custom culture
selection, validation/persistence and renderer intent routing. Existing XSR
architecture and NativeAOT/trim gates apply.
