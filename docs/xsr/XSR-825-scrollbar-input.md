# XSR-825: scrollbar pointer input

UI.Next owns continuous scrolling and scrollbar input. Avalonia receives immutable scene
geometry and forwards native pointer events; it does not add a native scroll service or a
second renderer. `XsrUiScrollSnapshot` remains the source of viewport, content, offset and
maximum-offset facts. A scene node projects optional vertical and horizontal scrollbar
track, thumb and hit rectangles from those facts.

## Geometry and hit contract

- `ShowsVerticalIndicator` and `ShowsHorizontalIndicator` request their respective bars.
  Each requested axis reserves a 12 px gutter even before overflow, preserving stable
  content measurement. A bar exists only while that axis overflows.
- The paint track is 3 px wide, inset 6 px along its axis and 4 px from the trailing edge.
  The thumb is proportional to viewport/content, with a minimum length of 28 px capped
  by the track length. The hit width is the reserved gutter. When both bars exist, tracks
  end before the other gutter; the corner does not belong to either bar.
- Scene coordinates and the existing ancestor clip govern both drawing and hit testing.
  The innermost accessible bar under the point wins; later overlapping scene content
  occludes bars underneath. Outgoing pages, inactive pager pages, disabled containers,
  hidden/retired entities and content behind a modal cannot begin or continue input.

## Pointer ownership

- Pressing a thumb captures the scrollbar gesture immediately, before content clicks,
  graph/segment gestures, native text selection, file drag or ancestor DragPager input.
  Movement maps thumb travel proportionally to the current measured maximum offset.
  Movement outside the viewport still updates the captured bar, clamped at both ends.
- Pressing the track before/after its thumb moves one viewport toward that point and
  consumes the complete press/release. Holding a track does not start content dragging
  or repeatedly page. A release never emits a content command.
- Scrollbar input stops continuous-scroll inertia and segmented-track scroll motion.
  Thumb release has no fling velocity. Pointer cancellation/capture loss ends ownership
  without activation. Navigation, component removal, disabling and a new modal retire
  the gesture, including before a fresh scene has been produced.
- Wheel input keeps normal nested scroll chaining. A wheel event during a thumb drag
  updates the shared offset and rebases the drag so the next move cannot jump backward.
  Resize/content changes use current scene travel and maximum offsets, with offsets
  clamped by existing arrange rules. A zero travel or removed bar retires ownership.

## Validation

Existing scroll viewport cases retain gutter, offset and scene-query coverage. Pointer
cases cover both axes, minimum thumb bounds, paging, resize/content changes, nested
clips, cancellation, navigation, disabled/modal barriers and command/DragPager isolation.
Avalonia headless cases use actual mouse press/move/release events and scene geometry to
exercise capture, wheel coexistence and text/file-drag isolation through the native surface.
