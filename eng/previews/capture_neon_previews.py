#!/usr/bin/env python3
"""Capture real neon startup, entrance and close previews from a PID-qualified X11 application.

No .NET builds, existing settings or credentials are required. Each
launch uses its own fresh data/config/cache directories under --output. The probe
does not install or replace a window manager. Images are actual X11 client pixels.
"""

import argparse
import ctypes as c
import json
import hashlib
import shutil
import os
from pathlib import Path
import subprocess
import time

from PIL import Image


class WindowAttributes(c.Structure):
    _fields_ = [
        ('x', c.c_int), ('y', c.c_int), ('width', c.c_int), ('height', c.c_int),
        ('border_width', c.c_int), ('depth', c.c_int), ('visual', c.c_void_p),
        ('root', c.c_ulong), ('window_class', c.c_int), ('bit_gravity', c.c_int),
        ('win_gravity', c.c_int), ('backing_store', c.c_int), ('backing_planes', c.c_ulong),
        ('backing_pixel', c.c_ulong), ('save_under', c.c_int), ('colormap', c.c_ulong),
        ('map_installed', c.c_int), ('map_state', c.c_int), ('all_event_masks', c.c_long),
        ('your_event_mask', c.c_long), ('do_not_propagate_mask', c.c_long),
        ('override_redirect', c.c_int), ('screen', c.c_void_p),
    ]


class XError(c.Structure):
    _fields_ = [('type', c.c_int), ('display', c.c_void_p), ('resource', c.c_ulong),
                ('serial', c.c_ulong), ('error', c.c_ubyte), ('request', c.c_ubyte),
                ('minor', c.c_ubyte)]


class XImage(c.Structure):
    _fields_ = [
        ('width', c.c_int), ('height', c.c_int), ('xoffset', c.c_int), ('format', c.c_int),
        ('data', c.c_void_p), ('byte_order', c.c_int), ('bitmap_unit', c.c_int),
        ('bitmap_bit_order', c.c_int), ('bitmap_pad', c.c_int), ('depth', c.c_int),
        ('bytes_per_line', c.c_int), ('bits_per_pixel', c.c_int),
        ('red_mask', c.c_ulong), ('green_mask', c.c_ulong), ('blue_mask', c.c_ulong),
        ('obdata', c.c_void_p), ('functions', c.c_void_p * 6),
    ]


class Message(c.Structure):
    _fields_ = [('type', c.c_int), ('serial', c.c_ulong), ('send_event', c.c_int),
                ('display', c.c_void_p), ('window', c.c_ulong), ('message_type', c.c_ulong),
                ('format', c.c_int), ('data', c.c_long * 5)]


class Event(c.Union):
    _fields_ = [('message', Message), ('pad', c.c_long * 24)]


class X11:
    def __init__(self, name):
        self.x = c.CDLL('libX11.so.6')
        self.x.XOpenDisplay.argtypes = [c.c_char_p]
        self.x.XOpenDisplay.restype = c.c_void_p
        self.x.XDefaultRootWindow.argtypes = [c.c_void_p]
        self.x.XDefaultRootWindow.restype = c.c_ulong
        self.x.XQueryTree.argtypes = [c.c_void_p, c.c_ulong, c.POINTER(c.c_ulong),
            c.POINTER(c.c_ulong), c.POINTER(c.POINTER(c.c_ulong)), c.POINTER(c.c_uint)]
        self.x.XFetchName.argtypes = [c.c_void_p, c.c_ulong, c.POINTER(c.c_void_p)]
        self.x.XFree.argtypes = [c.c_void_p]
        self.x.XInternAtom.argtypes = [c.c_void_p, c.c_char_p, c.c_int]
        self.x.XInternAtom.restype = c.c_ulong
        self.x.XGetAtomName.argtypes = [c.c_void_p, c.c_ulong]
        self.x.XGetAtomName.restype = c.c_void_p
        self.x.XGetWindowProperty.argtypes = [c.c_void_p, c.c_ulong, c.c_ulong, c.c_long,
            c.c_long, c.c_int, c.c_ulong, c.POINTER(c.c_ulong), c.POINTER(c.c_int),
            c.POINTER(c.c_ulong), c.POINTER(c.c_ulong), c.POINTER(c.c_void_p)]
        self.x.XGetWindowProperty.restype = c.c_int
        self.x.XGetWindowAttributes.argtypes = [c.c_void_p, c.c_ulong, c.POINTER(WindowAttributes)]
        self.x.XGetInputFocus.argtypes = [c.c_void_p, c.POINTER(c.c_ulong), c.POINTER(c.c_int)]
        self.x.XGetImage.argtypes = [c.c_void_p, c.c_ulong, c.c_int, c.c_int, c.c_uint,
            c.c_uint, c.c_ulong, c.c_int]
        self.x.XGetImage.restype = c.POINTER(XImage)
        self.x.XDestroyImage.argtypes = [c.POINTER(XImage)]
        self.x.XGetPixel.argtypes = [c.POINTER(XImage), c.c_int, c.c_int]
        self.x.XGetPixel.restype = c.c_ulong
        self.x.XGrabServer.argtypes = [c.c_void_p]
        self.x.XUngrabServer.argtypes = [c.c_void_p]
        self.x.XSync.argtypes = [c.c_void_p, c.c_int]
        self.x.XFlush.argtypes = [c.c_void_p]
        self.x.XSendEvent.argtypes = [c.c_void_p, c.c_ulong, c.c_int, c.c_long, c.c_void_p]
        self.x.XCloseDisplay.argtypes = [c.c_void_p]
        self.x.XTranslateCoordinates.argtypes = [c.c_void_p, c.c_ulong, c.c_ulong, c.c_int, c.c_int, c.POINTER(c.c_int), c.POINTER(c.c_int), c.POINTER(c.c_ulong)]
        self.x.XCreateSimpleWindow.argtypes = [c.c_void_p, c.c_ulong, c.c_int, c.c_int,
            c.c_uint, c.c_uint, c.c_uint, c.c_ulong, c.c_ulong]
        self.x.XCreateSimpleWindow.restype = c.c_ulong
        self.x.XStoreName.argtypes = [c.c_void_p, c.c_ulong, c.c_char_p]
        self.x.XMapWindow.argtypes = [c.c_void_p, c.c_ulong]
        self.x.XSetInputFocus.argtypes = [c.c_void_p, c.c_ulong, c.c_int, c.c_ulong]
        self.x.XDestroyWindow.argtypes = [c.c_void_p, c.c_ulong]
        self.errors = []
        self.bad_windows = 0
        handler_type = c.CFUNCTYPE(c.c_int, c.c_void_p, c.POINTER(XError))

        @handler_type
        def on_error(_, error):
            if error.contents.error == 3:  # A window can disappear during startup handoff.
                self.bad_windows += 1
            else:
                self.errors.append({'code': error.contents.error,
                                    'request': error.contents.request})
            return 0

        self._error_callback = on_error  # Keep the ctypes callback alive.
        self.x.XSetErrorHandler.argtypes = [handler_type]
        self.x.XSetErrorHandler.restype = c.c_void_p
        self.x.XSetErrorHandler(on_error)
        self.display = self.x.XOpenDisplay(name.encode())
        if not self.display:
            raise RuntimeError(f'Cannot open X11 display {name}; start Xvfb first.')
        self.root = self.x.XDefaultRootWindow(self.display)
        self.sentinel = self.x.XCreateSimpleWindow(self.display, self.root, 5, 5, 8, 8, 0, 0, 0)
        self.x.XStoreName(self.display, self.sentinel, b'Neon GUI focus sentinel')
        self.x.XMapWindow(self.display, self.sentinel)
        self.x.XSync(self.display, 0)

    def atom(self, name):
        return self.x.XInternAtom(self.display, name.encode(), 0)

    def values(self, window, name):
        actual, count, remaining = c.c_ulong(), c.c_ulong(), c.c_ulong()
        fmt, data = c.c_int(), c.c_void_p()
        result = self.x.XGetWindowProperty(self.display, window, self.atom(name), 0, 256,
            0, 0, c.byref(actual), c.byref(fmt), c.byref(count), c.byref(remaining), c.byref(data))
        try:
            if result != 0 or not data.value or fmt.value != 32:
                return []
            return list(c.cast(data, c.POINTER(c.c_ulong))[:count.value])
        finally:
            if data.value:
                self.x.XFree(data)

    def atom_name(self, value):
        data = self.x.XGetAtomName(self.display, value)
        try:
            return c.string_at(data).decode(errors='replace') if data else hex(value)
        finally:
            if data:
                self.x.XFree(data)

    def focus(self):
        focus, revert = c.c_ulong(), c.c_int()
        self.x.XGetInputFocus(self.display, c.byref(focus), c.byref(revert))
        return focus.value

    def focus_sentinel(self):
        self.x.XSetInputFocus(self.display, self.sentinel, 2, 0)
        self.x.XSync(self.display, 0)

    def activate_window(self, window):
        # A WM normally activates the product's visible main window. Xvfb without
        # one requires a real core-focus request; the passive Splash is untouched.
        self.x.XSetInputFocus(self.display, window, 2, 0)
        self.x.XSync(self.display, 0)

    def attributes(self, window):
        attributes = WindowAttributes()
        if self.x.XGetWindowAttributes(self.display, window, c.byref(attributes)):
            return attributes
        return None

    def windows_for_pid(self, pid, include_hidden=False):
        found = []

        def visit(window):
            name = c.c_void_p()
            if self.x.XFetchName(self.display, window, c.byref(name)) and name.value:
                try:
                    title = c.string_at(name).decode(errors='replace')
                finally:
                    self.x.XFree(name)
                if title == 'NexaCL' and self.values(window, '_NET_WM_PID') == [pid]:
                    attributes = self.attributes(window)
                    if attributes and (include_hidden or attributes.map_state == 2):
                        found.append((window, attributes))
            root, parent, children, count = c.c_ulong(), c.c_ulong(), c.POINTER(c.c_ulong)(), c.c_uint()
            if self.x.XQueryTree(self.display, window, c.byref(root), c.byref(parent),
                                 c.byref(children), c.byref(count)):
                try:
                    for index in range(count.value):
                        visit(children[index])
                finally:
                    if children:
                        self.x.XFree(children)

        visit(self.root)
        return found

    def snapshot(self, window, attributes):
        states = [self.atom_name(atom) for atom in self.values(window, '_NET_WM_STATE')]
        hints = self.values(window, 'WM_HINTS')
        wm_check = self.values(self.root, '_NET_SUPPORTING_WM_CHECK')
        focus = self.focus()
        pids = self.values(window, '_NET_WM_PID')
        return {
            'id': hex(window), 'width': attributes.width, 'height': attributes.height,
            'pid': pids[0] if pids else None,
            'states': states, 'state_above_observed': '_NET_WM_STATE_ABOVE' in states,
            'wm_present': bool(wm_check),
            'wm_hints_input': bool(hints[1]) if len(hints) >= 2 and hints[0] & 1 else None,
            'user_time': self.values(window, '_NET_WM_USER_TIME'),
            'active_window': [hex(value) for value in self.values(self.root, '_NET_ACTIVE_WINDOW')],
            'input_focus': hex(focus), 'splash_has_input_focus': focus == window,
            'input_focus_kind': 'None' if focus == 0 else 'PointerRoot' if focus == 1 else 'Window',
            'sentinel_retained_focus': focus == self.sentinel,
        }

    def screenshot(self, window):
        # Avoid destruction/unmapping between the mapped check and XGetImage. The
        # short server grab encloses only X11 reads; PNG encoding runs after release.
        raw = None
        self.x.XGrabServer(self.display)
        try:
            attributes = self.attributes(window)
            if not attributes or attributes.map_state != 2:
                return None
            raw = self.x.XGetImage(self.display, window, 0, 0, attributes.width,
                                 attributes.height, c.c_ulong(-1).value, 2)
            if not raw:
                return None
            image = raw.contents
            data = c.string_at(image.data, image.bytes_per_line * image.height)
            if (image.red_mask, image.green_mask, image.blue_mask) == (0xff0000, 0xff00, 0xff):
                mode = {(32, 0): 'BGRX', (32, 1): 'XRGB', (24, 0): 'BGR', (24, 1): 'RGB'}
                decoder = mode.get((image.bits_per_pixel, image.byte_order))
                if decoder:
                    return Image.frombytes('RGB', (image.width, image.height), data,
                                           'raw', decoder, image.bytes_per_line, 1)
            # General TrueColor fallback; ordinary Xvfb screenshots use the fast path.
            def channel(pixel, mask):
                shift = (mask & -mask).bit_length() - 1
                maximum = mask >> shift
                return ((pixel & mask) >> shift) * 255 // maximum
            pixels = bytearray()
            for y in range(image.height):
                for x in range(image.width):
                    pixel = self.x.XGetPixel(raw, x, y)
                    pixels.extend(channel(pixel, mask) for mask in
                                  (image.red_mask, image.green_mask, image.blue_mask))
            return Image.frombytes('RGB', (image.width, image.height), bytes(pixels))
        finally:
            if raw:
                self.x.XDestroyImage(raw)
            self.x.XUngrabServer(self.display)
            self.x.XFlush(self.display)

    def rectangle(self, window, attributes):
        left, top, child = c.c_int(), c.c_int(), c.c_ulong()
        if not self.x.XTranslateCoordinates(self.display, window, self.root, 0, 0, c.byref(left), c.byref(top), c.byref(child)):
            raise RuntimeError("Native window disappeared while measuring its actual screen rectangle.")
        return (left.value, top.value, left.value + attributes.width, top.value + attributes.height)

    def close_window(self, window):
        event = Event()
        event.message = Message(type=33, display=self.display, window=window,
                                message_type=self.atom('WM_PROTOCOLS'), format=32)
        event.message.data[0] = self.atom('WM_DELETE_WINDOW')
        self.x.XSendEvent(self.display, window, 0, 0, c.byref(event))
        self.x.XFlush(self.display)

    def close(self):
        self.x.XDestroyWindow(self.display, self.sentinel)
        self.x.XCloseDisplay(self.display)


def digest(image):
    return hashlib.sha256(image.tobytes()).hexdigest()


def rendered(image):
    return image is not None and any(high - low >= 24 for low, high in image.getextrema())


def crop_frames(samples, rectangle):
    return [(timestamp, image.crop(rectangle)) for timestamp, image in samples]


def region_digests(frames, rectangle):
    return {digest(image.crop(rectangle)) for _, image in frames}


def mint_pixels(image, settled):
    # Only the actual cyan/mint N's central native decoration is measured. The
    # default blue action buttons and neutral scene text do not meet this mask.
    width, height = image.size
    rectangle = (width // 2 - 58, height // 2 - 58, width // 2 + 58, height // 2 + 58)
    pixels = image.crop(rectangle).get_flattened_data()
    baseline = settled.crop(rectangle).get_flattened_data()
    # Relative chromatic changes exclude the scene's permanent blue antialiasing
    # and shadows. A scene-opacity fade reduces blue saturation; it does not add
    # this mint increment over the same settled pixel.
    return sum(1 for (red, green, blue), (base_red, base_green, base_blue) in zip(pixels, baseline)
               if green >= 120 and blue >= 80
               and green - red - (base_green - base_red) >= 12
               and green - blue - (base_green - base_blue) >= 8)


def write_animation(args, directory, name, frames, expected_ms=None):
    if len(frames) < 4 or len({digest(image) for _, image in frames}) < 3:
        raise RuntimeError(f'{name}: actual sampled pixels contain too few distinct frames; animation was missed.')
    destination = directory / 'frames' / name
    destination.mkdir(parents=True)
    records = []
    for index, (timestamp, image) in enumerate(frames):
        path = destination / f'{index:04d}.png'
        image.save(path)
        records.append({'file': str(path.relative_to(directory)),
                        'time_ms': round((timestamp - frames[0][0]) * 1000, 3),
                        'sha256_rgb': digest(image)})
    durations = [max(.01, frames[index + 1][0] - frames[index][0])
                 for index in range(len(frames) - 1)] + [1 / args.fps]
    concat = destination / 'frames.ffconcat'
    # The local frame names have no shell quoting or user-input interpolation.
    lines = ['ffconcat version 1.0']
    for index, duration in enumerate(durations):
        lines.extend([f"file '{index:04d}.png'", f'duration {duration:.6f}'])
    lines.append(f"file '{len(frames) - 1:04d}.png'")
    concat.write_text('\n'.join(lines) + '\n', encoding='utf-8')
    output = directory / (name + '.gif')
    maximum_width = min(args.gif_width, frames[0][1].width)
    filters = (f'[0:v]scale={maximum_width}:-1:flags=lanczos,split[a][b];'
               '[a]palettegen=max_colors=128:stats_mode=diff[p];'
               '[b][p]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle')
    subprocess.run([args.ffmpeg, '-hide_banner', '-loglevel', 'error', '-y',
        '-f', 'concat', '-safe', '0', '-i', str(concat), '-filter_complex', filters,
        '-fps_mode', 'vfr', '-loop', '-1', str(output)], check=True)
    with Image.open(output) as gif:
        # No NETSCAPE infinite-loop extension is permitted in review artifacts.
        if gif.info.get('loop') == 0:
            raise RuntimeError(f'{name}: encoder produced an infinite-loop GIF.')
        gif_frames = gif.n_frames
    return {'file': output.name, 'raw_frame_count': len(frames), 'gif_frame_count': gif_frames,
            'distinct_raw_frames': len({record['sha256_rgb'] for record in records}),
            'sample_duration_ms': round(sum(durations) * 1000, 3),
            'expected_motion_ms': expected_ms, 'playback': 'once',
            'bytes': output.stat().st_size, 'frames': records}


def launch_theme(args, probe, theme, value):
    directory = args.output / theme
    directory.mkdir()
    fixture = directory / 'fixture'
    runtime_keys = ('PATH', 'HOME', 'LANG', 'LC_ALL', 'DOTNET_ROOT', 'LD_LIBRARY_PATH',
                    'FONTCONFIG_FILE', 'FONTCONFIG_PATH', 'XDG_RUNTIME_DIR', 'DBUS_SESSION_BUS_ADDRESS')
    environment = {key: os.environ[key] for key in runtime_keys if key in os.environ}
    environment['DISPLAY'] = args.display
    for key, suffix in [('XDG_DATA_HOME', 'xdg/data'), ('XDG_CONFIG_HOME', 'xdg/config'),
                        ('XDG_CACHE_HOME', 'xdg/cache'), ('NEXA_DATA_DIR', 'launcher')]:
        path = fixture / suffix
        path.mkdir(parents=True)
        environment[key] = str(path)
    settings = fixture / 'launcher/settings'
    settings.mkdir()
    (settings / 'settings.json').write_text(json.dumps({
        'schemaVersion': 1,
        'booleanOptions': {'SystemDisableHardwareAcceleration': True,
                           'SystemDisableUiAnimations': False,
                           'UiTrayEnabled': False},
        'integerOptions': {'UiDarkMode': value}, 'textOptions': {'UiLanguage': args.language},
    }), encoding='utf-8')
    probe.focus_sentinel()
    report = {'theme': theme, 'UiDarkMode': value, 'animations': {}, 'assets': {},
              'focus_before_launch': hex(probe.focus()), 'software_rendering': True,
              'language': args.language, 'tray_enabled': False}
    splash_samples, splash_loading_samples, entrance_samples, close_samples = [], [], [], []
    with (directory / 'launcher-output.log').open('w') as log:
        primary = subprocess.Popen([str(args.binary)], env=environment, stdout=log, stderr=subprocess.STDOUT)
        report['pid'] = primary.pid
        try:
            splash = main = None
            splash_start = main_start = None
            splash_rectangle = main_rectangle = None
            next_splash = next_loading = next_main = 0
            watched_hidden = False
            deadline = time.monotonic() + args.timeout
            while time.monotonic() < deadline:
                if primary.poll() is not None:
                    raise RuntimeError(f'{theme}: application exited before main entrance; see {log.name}')
                now = time.monotonic()
                # Observe a prepared native XID while it is still hidden, then poll its
                # mapping every 5ms. GIF capture does not wait for the animation to end.
                candidates = probe.windows_for_pid(primary.pid, include_hidden=True)
                for window, attributes in candidates:
                    if attributes.width == 400 and attributes.height == 280:
                        splash = window
                        if attributes.map_state == 2 and splash_start is None:
                            splash_start = now
                            splash_rectangle = probe.rectangle(window, attributes)
                            report['splash'] = probe.snapshot(window, attributes)
                    elif attributes.width >= 700 and attributes.height >= 400:
                        main = window
                        if attributes.map_state != 2 and main_start is None:
                            watched_hidden = True
                        if attributes.map_state == 2 and main_start is None:
                            main_start = now
                            report['main_first_mapping'] = probe.snapshot(window, attributes)
                            report['hidden_prepared_xid_observed'] = watched_hidden
                            probe.activate_window(window)
                            report['main_foreground_focus_observed'] = probe.focus() == window
                splash_attributes = probe.attributes(splash) if splash is not None else None
                if splash_attributes is None:
                    splash = None  # Stop querying an XID already destroyed at handoff.
                splash_visible = splash_attributes is not None and splash_attributes.map_state == 2
                capture_splash = splash_visible and splash_start is not None and now - splash_start <= .60 and now >= next_splash
                capture_loading = splash_visible and now >= next_loading
                capture_main = main_start is not None and now - main_start <= .85 and now >= next_main
                if capture_splash or capture_loading or capture_main:
                    frame = probe.screenshot(probe.root)
                    if frame is not None:
                        timestamp = time.monotonic()
                        if capture_splash:
                            splash_samples.append((timestamp, frame))
                            next_splash = timestamp + 1 / args.fps
                        if capture_loading:
                            if len(splash_loading_samples) < 48:
                                splash_loading_samples.append((timestamp, frame))
                            screenshot = frame.crop(splash_rectangle)
                            if rendered(screenshot):
                                screenshot.save(directory / 'splash.png')
                                report['assets']['splash'] = 'splash.png'
                            next_loading = timestamp + .5
                        if capture_main:
                            entrance_samples.append((timestamp, frame))
                            next_main = timestamp + 1 / args.fps
                if main_start is not None and now - main_start >= .95:
                    attributes = probe.attributes(main)
                    if attributes is None or attributes.map_state != 2:
                        raise RuntimeError(f'{theme}: main vanished before entrance recording completed')
                    main_rectangle = probe.rectangle(main, attributes)
                    report['main'] = probe.snapshot(main, attributes)
                    main_image = probe.screenshot(probe.root).crop(main_rectangle)
                    if not rendered(main_image):
                        raise RuntimeError(f'{theme}: real native main pixels are blank')
                    main_image.save(directory / 'shell.png')
                    report['assets']['shell'] = 'shell.png'
                    break
                time.sleep(.005)
            if main_rectangle is None or not report['assets'].get('splash'):
                raise RuntimeError(f'{theme}: no real Splash/main handoff within {args.timeout}s')
            splash_id = int(report['splash']['id'], 16)
            if splash_id in {window for window, _ in probe.windows_for_pid(primary.pid)}:
                raise RuntimeError(f'{theme}: topmost Splash remains mapped after handoff')
            report['splash_removed_at_handoff'] = True

            # Capture real screen pixels at a fixed crop, so shrinking native window
            # shape and the final disappearance are visible rather than stale drawable pixels.
            frame = probe.screenshot(probe.root)
            close_samples.append((time.monotonic(), frame.crop(main_rectangle)))
            probe.close_window(main)
            close_start = time.monotonic()
            next_close = close_start
            last_mapped = close_start
            main_disappeared = False
            while time.monotonic() - close_start < 3:
                now = time.monotonic()
                if now >= next_close:
                    frame = probe.screenshot(probe.root)
                    close_samples.append((time.monotonic(), frame.crop(main_rectangle)))
                    next_close = time.monotonic() + 1 / args.fps
                attributes = None if main_disappeared else probe.attributes(main)
                if attributes is not None and attributes.map_state == 2:
                    last_mapped = now
                else:
                    main_disappeared = True
                    if now - last_mapped >= .16:
                        break
                time.sleep(.005)
            report['exit_code'] = primary.wait(timeout=20)
            if report['exit_code'] != 0:
                raise RuntimeError(f'{theme}: native close exited {report["exit_code"]}')
            report['assets']['frames'] = 'frames/'

            splash_frames = crop_frames(splash_samples, splash_rectangle)
            splash_header = (12, 12, 388, 130)
            header_changes = len(region_digests(splash_frames, splash_header))
            if header_changes >= 3:
                splash_kind, selected_splash = '180ms appearance', splash_frames
                splash_name, splash_motion_ms = 'splash-entrance', 180
            else:
                # Real loading stages can be reviewed when native mapping missed the
                # initial fade. This is labelled honestly and never substitutes a fake fade.
                splash_kind = 'actual loading-stage updates; initial fade not fully captured'
                selected_splash = crop_frames(splash_loading_samples, splash_rectangle)
                splash_name, splash_motion_ms = 'splash-loading', None
            report['animations']['splash'] = write_animation(args, directory, splash_name, selected_splash, splash_motion_ms)
            report['animations']['splash']['kind'] = splash_kind
            report['animations']['splash']['initial_header_distinct_frames'] = header_changes
            report['assets']['splashAnimation'] = splash_name + '.gif'

            entrance_frames = crop_frames(entrance_samples, main_rectangle)
            report['animations']['entrance'] = write_animation(args, directory, 'entrance', entrance_frames, 420)
            counts = [mint_pixels(image, main_image) for _, image in entrance_frames]
            trace_frames = sum(count >= 8 for count in counts)
            central = (main_image.width // 2 - 58, main_image.height // 2 - 58,
                       main_image.width // 2 + 58, main_image.height // 2 + 58)
            trace_distinct = len({digest(image.crop(central)) for (_, image), count in zip(entrance_frames, counts) if count >= 8})
            report['animations']['entrance']['visible_mint_trace_frames'] = trace_frames
            report['animations']['entrance']['distinct_mint_trace_frames'] = trace_distinct
            report['animations']['entrance']['mint_pixels_by_frame'] = counts
            report['assets']['entrance'] = 'entrance.gif'
            if len({digest(image) for _, image in close_samples if rendered(image)}) < 4:
                raise RuntimeError(f'{theme}: close collapse was not sampled in multiple visible frames')
            report['animations']['close'] = write_animation(args, directory, 'close', close_samples, 470)
            report['assets']['close'] = 'close.gif'
            if trace_frames < 3 or trace_distinct < 3:
                raise RuntimeError(f'{theme}: 420ms mint N entrance was not visible in at least 3 distinct actual frames: {counts}')
            print(f'PASS {theme}: actual Splash/{trace_frames}-frame mint entrance/close GIF; native exit 0', flush=True)
        finally:
            if primary.poll() is None:
                primary.terminate()
                try:
                    primary.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    primary.kill()
                    primary.wait()
            (directory / 'observations.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path, help='Fresh artifact directory; existing paths are rejected')
    parser.add_argument('--display', default=':97')
    parser.add_argument('--theme', choices=('both', 'light', 'dark'), default='both')
    parser.add_argument('--language', choices=('zh-Hans', 'zh-Hant', 'en', 'auto'), default='zh-Hans')
    parser.add_argument('--fps', type=int, default=30, choices=range(24, 61))
    parser.add_argument('--gif-width', type=int, default=640)
    parser.add_argument('--timeout', type=float, default=90)
    parser.add_argument('--ffmpeg', default='ffmpeg')
    args = parser.parse_args()
    if not args.binary.is_file():
        parser.error(f'Application binary missing: {args.binary}')
    args.binary = args.binary.resolve()
    args.ffmpeg = shutil.which(args.ffmpeg)
    if args.ffmpeg is None:
        parser.error('ffmpeg is required to encode actual frames')
    if not 320 <= args.gif_width <= 1280:
        parser.error('GIF width must be 320..1280')
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    probe = X11(args.display)
    managed_assemblies = {}
    for name in (args.binary.name + '.dll', 'Nexa.UI.Next.Backend.Avalonia.dll', 'Nexa.UI.Next.dll'):
        path = args.binary.parent / name
        if path.is_file():
            managed_assemblies[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    manifest = {'version': 1, 'scope': 'current neon identity/startup/close changes',
                'source': {'binary': str(args.binary),
                           'sha256': hashlib.sha256(args.binary.read_bytes()).hexdigest(),
                           'managed_assembly_sha256': managed_assemblies,
                           'capture_utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())},
                'capture': {'display': args.display, 'target_fps': args.fps,
                            'pixels': 'actual X11 root pixels cropped to PID-qualified native window',
                            'software_rendering': True, 'tray_enabled': False,
                            'main_core_focus_requested': True,
                            'gif_playback': 'once'}, 'themes': []}
    try:
        for theme, value in [('light', 0), ('dark', 1)]:
            if args.theme in ('both', theme):
                manifest['themes'].append(launch_theme(args, probe, theme, value))
        probe.x.XSync(probe.display, 0)
        manifest['capture']['unexpected_x11_errors'] = probe.errors
        manifest['capture']['bad_window_races_tolerated'] = probe.bad_windows
        manifest['capture']['wm_present'] = bool(probe.values(probe.root, '_NET_SUPPORTING_WM_CHECK'))
        if probe.errors:
            raise RuntimeError(f'Unexpected X11 errors: {probe.errors}')
        manifest['platform_limit'] = ('Xvfb without a window manager: software pixels and passive core focus '
            'are observed; physical compositor stacking and WM activation need platform validation.')
        print(f'Preview artifacts: {args.output}', flush=True)
    finally:
        (args.output / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
        probe.close()


if __name__ == '__main__':
    main()
