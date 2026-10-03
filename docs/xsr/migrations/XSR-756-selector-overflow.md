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
Click, keyboard and accessibility activation reveal the selected segment independently of
the drag threshold. Revelation happens in the common activation path once, in both
directions, without disturbing an active thumb drag.

Contract tests cover clipping, viewport shrink/grow, proportional scroll, repeated pointer
coordinates, reversing, cancellation, keyboard reachability and partially clipped pointer selection. UI.Next owns geometry and
gesture state; Desktop retains only selection intents. Existing reduced-motion behavior
and scroll/transition clocks remain unchanged.
