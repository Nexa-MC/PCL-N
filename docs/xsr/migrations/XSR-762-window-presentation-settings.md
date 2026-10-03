# XSR-762 — Live window presentation settings

The existing sealed settings routes own durable global `appearance.animations-disabled`
and `appearance.lock-window` values. Desktop observes their published typed state cells
and projects changes at `FramePreparing`, including values restored before Host attachment.
No settings page needs to be open and no synchronous query or disk access runs in a frame.

Animation preference drives UI.Next `ReducedMotion`; the independent launch/window-activity
`OptionalMotionSuspended` policy remains intact. Host reveal, close and control animations
already consume the effective renderer preference. The positive “Enable animations” control
inverts only its displayed labels, not the stored disable flag.

Window size lock calls an explicit Host effect. Host retains the pending preference until
attachment, applies native `CanResize`, and prevents custom maximize and title-bar double
click from bypassing it. Minimize, close, native chrome, transparency and system animations
retain their existing contracts. Low power, frame rate and theme stay reserved until their
own consumers are connected.

Regression covers immediate state publication, restored preferences without the settings
page, unchanged idle frames, selector polarity, and the native window effect before and
after attachment. Architecture checks and managed shell smoke apply; NativeAOT and trim
are validated by the existing XSR CI pipeline.
