# Actual native page previews

These helpers capture the current change's real product UI and build an offline
gallery. They do not build .NET projects. Supply an already built application or
the latest Desktop test executable. Resource records, provider failures and retry
responses are controlled typed demonstration data; settings use temporary local
files and an empty instance inventory. These previews do not prove live provider
or download behavior.

Run from the repository root. Set your own output root and built artifact paths;
none of the scripts requires a particular cloud workspace. Use a Python with
Pillow for capture; the index builder needs only Python's standard library.
Capture also needs X11, an existing Xvfb display, FFmpeg, and ImageMagick's `import`
for resource/settings screenshots. A private `dbus-run-session` keeps the native
system-theme edge available without reusing desktop session credentials.

```sh
PREVIEW_ROOT="$(pwd)/artifacts/previews"
DESKTOP_EXE="$(pwd)/Nexa.Desktop/bin/Release/net10.0/Nexa.Desktop"
DESKTOP_TESTS="$(pwd)/tests/Nexa.Desktop.Tests/bin/Release/net10.0/Nexa.Desktop.Tests.dll"
```

Use separate Xvfb displays to avoid another preview or test obscuring the actual
screen pixels. For example, start `Xvfb :98 -screen 0 1280x800x24` for native
startup and `:99`, `:100` for resource/settings previews. Provision fonts and
`DOTNET_ROOT` according to the execution environment. If sourcing a runtime setup
changes `python`, choose an explicit interpreter with Pillow.

```sh
dbus-run-session -- python eng/previews/capture_neon_previews.py \
  --binary "$DESKTOP_EXE" --display :98 --fps 60 --output "$PREVIEW_ROOT/native"

dbus-run-session -- python eng/previews/capture_resource_previews.py \
  --output "$PREVIEW_ROOT/resources-light" --theme light --display :99 -- \
  dotnet "$DESKTOP_TESTS" --native-resource-preview light "$PREVIEW_ROOT/resources-light"
dbus-run-session -- python eng/previews/capture_resource_previews.py \
  --output "$PREVIEW_ROOT/resources-dark" --theme dark --display :99 -- \
  dotnet "$DESKTOP_TESTS" --native-resource-preview dark "$PREVIEW_ROOT/resources-dark"

dbus-run-session -- python eng/previews/capture_settings_previews.py \
  --assembly "$DESKTOP_TESTS" --display :100 --output "$PREVIEW_ROOT/settings"

python eng/previews/build_preview_index.py --directory "$PREVIEW_ROOT"
```

The native capture requires a fresh output directory. It tracks a PID-qualified
hidden window before mapping, samples actual X11 root pixels, and checks that the
420ms N entrance appears in multiple distinct frames. Both themes include static
Splash/main PNGs and finite Splash-entry, main-entry and close GIFs. Source frames,
sampling times and source assembly hashes are retained in the capture directory.

Xvfb without a window manager uses the existing software-rendering preference.
The native helper requests real core focus for the mapped main window so ordinary
foreground motion can run; the Splash remains passive. Its isolated fixture
disables the tray because this environment has no tray host. The captured pixels
and focus are actual observations, while compositor stacking and normal desktop
WM activation remain platform-validation targets.

Resource/settings GIFs record the actual X11 window, including scrolling, retries,
detail navigation and confirmed theme changes. All GIFs are encoded with
`-loop -1`; the gallery provides a fresh decoder on each explicit replay rather
than an infinite animation loop. The builder requires multiple GIF frames and
rejects every loop extension, matching its single-play labels.

`index.html` embeds all allowlisted PNG/GIF data and works offline as one file.
It offers Chinese theme/category controls, zoom, replay and individual downloads.
`previews.zip` contains only these actual images, the HTML, capture metadata and
verification notes. It excludes fixture files, raw frames, video recordings,
logs, readiness markers, render-target-only settings screenshots and failed
exploration directories. `preview-catalog.json` records every selected image's
dimensions, bytes and SHA-256. Re-run the builder after the final captures are
frozen; it overwrites only generated index/catalog/ZIP outputs. By default all
expected assets must exist. `--allow-missing` is available for an explicitly
partial review during collection.
