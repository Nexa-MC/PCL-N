# XSR-768 Animation frame rate

`appearance.animation-fps` is the actual requested presentation rate, 1–240 fps,
default 60. The legacy `UiAniFPS` stores fps minus one. Settings owns the explicit
conversion, persists new values without guessing slider semantics and restores
the legacy default 59 when an override is removed.

DesktopPresentationSession observes the committed integer cell, coalesces changes
and projects the requested rate through an explicit Host action. The action retains
preferences before native attachment and applies subsequent changes on the UI thread.
UI.Next does not resolve a settings service or depend on Avalonia.

The shared backend motion dispatcher adjusts its timer interval to 1/fps, including
active tracks. Elapsed-time integration and animation durations remain unchanged;
changing fps does not restart motion, override reduced motion or create idle work.
This is a requested animation tick rate, not a guarantee of display refresh rate.
OS/DWM animations, input dispatch, caret blinking and Service scheduling keep their
independent clocks. Low-power policy remains unavailable pending its own consumer.

Tests cover legacy conversion/reset, exact persistence and validation, live Desktop
projection and unchanged-frame coalescing, pre-attachment Host configuration and
the real dispatcher interval while a timeline continues through a rate change.
Architecture and NativeAOT/trim validation remain required.
