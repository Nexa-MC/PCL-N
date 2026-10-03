# XSR-756 selector viewport and proportional drag scroll

Segment tracks retain their intrinsic content width but never measure wider than the
available slot or their explicit maximum. Top-level installation/settings tracks cap
at 960 content pixels. Hidden overflow is clipped and remains reachable by wheel, thumb
drag and keyboard. Resizing remeasures width-sensitive tracks without resetting selection.

Thumb dragging starts from its live presentation position. For a drag starting at the
left end, rightward auto-scroll starts after 20% of the available thumb travel and maps
the remaining 80% onto the hidden content distance. Re-grabbing a scrolled thumb preserves
the current offset; reversal scrolls back proportionally. The scroll is position-based,
not a fixed increment per pointer event, so input frequency cannot change the result.
Click, keyboard and accessibility activation use the same 20% proportional projection
as dragging, including fully visible options. The requested offset is bounded so the
clicked option stays visible; activation must never substitute another option just
because scrolling moved the labels. Leftward selection reveals preceding content.
Re-selecting an unchanged option does not drift. Active thumb drags continue to own
scrolling, and do not run this activation projection for every emitted intent.

Activation publishes a horizontal scroll target and motion revision in the immutable
scene, without changing the presented offset. The backend advances that offset with
the shared critically damped spring (.34 s response), retaining velocity on retarget.
Renderer hit testing, labels, clipping and the thumb all use the presented viewport.
Drag and wheel input cancel the target and retain the live offset; revision-checked
frame writes cannot override newer input. Reduced motion settles immediately. Resize
clamps both the presented offset and target to the current content extent.

Contract tests cover clipping, viewport shrink/grow, proportional scroll, repeated pointer
coordinates, reversing, cancellation, keyboard reachability, fully visible and partially
clipped pointer selection, actual settings-page mouse input, stale-frame rejection,
and native clock intermediate frames, reversal, wheel interruption and reduced motion.
UI.Next owns geometry and
gesture state; Desktop retains only selection intents. Existing reduced-motion behavior
and scroll/transition clocks remain unchanged.

The native clock regression waits for a presented frame with a bounded deadline;
it does not assume a dispatcher frame arrives within a fixed 32 ms. Intermediate
motion, reversal without a jump, wheel cancellation and reduced-motion settling
remain separate assertions.
