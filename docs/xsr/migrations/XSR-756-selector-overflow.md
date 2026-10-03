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

Contract tests cover clipping, viewport shrink/grow, proportional scroll, repeated pointer
coordinates, reversing, cancellation, keyboard reachability, fully visible and partially
clipped pointer selection, and actual settings-page mouse input. UI.Next owns geometry and
gesture state; Desktop retains only selection intents. Existing reduced-motion behavior
and scroll/transition clocks remain unchanged.
