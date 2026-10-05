# XSR-786: committed appearance preferences

## Locked contract

The two global Theme positions now have real presentation consumers. The existing
experimental shell remains the only shell layout; light/dark preference and accent
do not introduce another renderer or persistent Desktop settings store.

| Position | Key | Values and default | Timing | Durable binding |
|---|---|---|---|---|
| `global.appearance.d7137f424ce4` | `appearance.theme-mode` | `2` System (default), `0` Light, `1` Dark | Immediate | legacy Int32 `UiDarkMode` |
| `global.appearance.dc9cc5ed3077` | `appearance.accent` | `blue` (default), `purple`, `green`, `orange` | Immediate | new Text `UiAccentColor` |

`UiDarkMode` retains its original integer encodings. Accent is a new XSR preference;
legacy LightColor/DarkColor/custom palettes are not silently interpreted as this
bounded palette. Existing unknown/custom color settings remain stored for a future
explicit import contract. Neither preference permits instance overrides.

Host observes committed raw setting cells, queues changes and projects them on the
render thread even while Settings is closed. Failed validation or durable saves do
not update the scene. Reopening a Desktop session reads the committed preferences,
including the first-run shell. System mode follows the native platform color
preference, falling back to Light when the platform supplies no usable preference.
Explicit Light/Dark does not change on an OS notification. Native requested theme
tracks the selected mode for native dialogs and controls; it never changes OS settings.

UI.Next owns deterministic color-scheme projection when producing scene snapshots.
The component tree retains its original product color tokens, so mode and accent
round trips cannot gradually rewrite custom colors or corrupt saved page styles.
Projection covers product backgrounds, ink, separators, hover tints, segmented
selection thumbs and styled text runs. Images and graph evidence colors retain
their original content. Inverse white text on solid colored actions remains white.
The version library's existing input/editor inset token `(240,244,250)` resolves
to the dark form inset `(42,49,62)`, and its hover token `(237,243,253)` resolves
to the current accent tint. Both retain their original alpha. These existing
product tokens must be projected together with the primary ink; otherwise a
dark-mode version search or directory editor would produce light text on a light
background. The additional owned tokens below follow the same exact-value
projection contract; this is an explicit product-token list, never a heuristic
that recolors arbitrary nearby RGB values.

| Existing product token and real consumer | Dark projection |
|---|---|
| Version selected row `(231,240,255)`; Task clear action `(239,244,251)`; task bubble track `(220,230,244)`; account hover `(115,158,220)` | Current accent tint |
| First-run unselected consent `(243,246,250)`; JVM argument and hook editors `(244,247,251)`; content graph canvas `(247,249,252)`; Task card track `(236,240,246)` | Form inset `(42,49,62)` |
| Task card title `(40,48,60)` | Primary ink `(232,237,245)` |
| Version secondary ink `(94,110,130)`; Task empty icon `(144,159,181)` | Secondary ink `(167,180,198)` |
| Task clear action ink `(48,87,145)` | Current accent text |
| Version add-path hover `(23,110,225)`; task bubble progress fill `(32,110,224)` | Current accent fill |
| Task card separator `(228,233,240)` | Neutral separator `(62,72,88)` |
| First-run error `(173,48,48)`; Task failure `(196,64,54)` | Error ink `(255,132,142)` |
| Task completion `(34,128,84)`; resource release channel `(40,135,90)` | Success ink `(136,215,151)` |
| Account delete hover `(255,224,224)` | Error surface `(58,32,40)` |

All replacements preserve alpha. Status foregrounds keep their semantic hue and
gain legibility on dark surfaces; graph node/evidence colors are still content,
not foreground palette tokens. Paired regressions use the real version row,
Task card, consent choice, script editor, clear action and status tokens and
require at least 4.5:1 text contrast in every dark accent. The default Light/Blue
scheme preserves all original token colors, and unknown custom colors remain
unchanged through theme and accent round trips.
Outgoing transition snapshots may keep the palette they captured until they retire;
the current interactive scene always uses the current scheme. Scheme changes
invalidate paint only and preserve focus, navigation, controls and layout caches.

The renderer publishes the confirmed `XsrUiColorScheme` as an immutable init
fact on every `XsrUiSceneNode`, including synthetic segmented thumbs. Existing
positional scene constructors and their defaults remain unchanged. Outgoing
nodes keep their captured scheme. Native drawing reads this scene fact and never
resolves a Service, settings store, live renderer or OS preference itself.
Native owned decorations obey these contracts:

| Backend-owned decoration | Drawing contract |
|---|---|
| Empty input placeholder | Canonical secondary ink `(96,108,124)`, resolved through `scheme.Foreground`; password masking and input contents are unchanged |
| Graph labels | Canonical secondary ink `(94,110,130)`, resolved through `scheme.Foreground`; node/evidence colors retain their original content |
| Input caret, selection, keyboard focus rings | Current `AccentText`; translucent selection preserves its overlay alpha |
| Checked checkbox/switch fill | Current `AccentFill` with inverse white mark/knob (at least 4.5:1 for every accent); explicitly styled unknown custom fills retain their color |
| Unchecked control surfaces and outlines | Owned white/inset/separator tokens projected through the same scheme |
| Capsule highlight gradient | Owned white highlight projected through the scheme, so Dark never introduces a white band behind light text |
| Scroll indicator | Projected secondary ink; dark thumb alpha `185` supplies at least 3:1 rendered contrast against both its track and dark form inset; original 3-pixel rail geometry is retained |

Native rendering regressions inspect the actual rendered control pixels, rather
than duplicating the projection algorithm. They cover placeholder and graph text,
caret/focus, checked fills and white marks, capsule highlights, custom fills and
mode round trips. The native drawing layer leaves arbitrary scene style colors
and graph content untouched.

Avalonia provides only the optional system preference and native requested-theme
edge. OS notifications schedule a demand-driven frame, with no polling or Services
dependency. The platform subscription is removed when the native owner closes.
Services and contract assemblies remain free of Avalonia and renderer references.

## Scope and verification

Contract tests cover legacy/default encodings, restart, invalid and failed writes,
live controls, native source notifications, explicit-mode priority, disposal,
original-color preservation, contrast and scene caching. Architecture gates and
the normal Desktop trimmed publish cover the expanded renderer/backend surface.

Autostart, tray/background lifetime, single-instance activation, `nexa://`, file
associations, native notifications/Jump Lists, arbitrary theme imports, custom
logos, multimedia backgrounds, blur and music remain independent capabilities.
The current Host has no registered consumers for those IA positions; they remain
explicitly reserved instead of becoming inert switches.
