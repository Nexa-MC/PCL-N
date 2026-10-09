#!/usr/bin/env python3
"""Record actual native Settings previews on an existing, dedicated Xvfb display."""
from __future__ import annotations

import argparse
import ctypes as c
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import time

from capture_neon_previews import X11


def descendants(pid: int) -> list[int]:
    result = [pid]
    for parent in result:
        try:
            children = Path(f"/proc/{parent}/task/{parent}/children").read_text().split()
        except FileNotFoundError:
            continue
        result.extend(int(child) for child in children if int(child) not in result)
    return result


def fit_native_window(x11: X11, process: subprocess.Popen, metadata: dict, screen_size: str) -> dict:
    def sized_windows():
        found = []

        def visit(parent):
            root, owner = c.c_ulong(), c.c_ulong()
            children, count = c.POINTER(c.c_ulong)(), c.c_uint()
            if not x11.x.XQueryTree(x11.display, parent, c.byref(root), c.byref(owner), c.byref(children), c.byref(count)):
                return
            try:
                for index in range(count.value):
                    child = children[index]
                    attributes = x11.attributes(child)
                    if attributes and attributes.map_state == 2 and attributes.width == metadata["width"] and attributes.height == metadata["height"]:
                        found.append((child, attributes))
                    visit(child)
            finally:
                if children:
                    x11.x.XFree(children)

        visit(x11.root)
        return found

    deadline = time.monotonic() + 0.75
    while True:
        windows = [item for pid in descendants(process.pid) for item in x11.windows_for_pid(pid)]
        matches = [item for item in windows
                   if item[1].width == metadata["width"] and item[1].height == metadata["height"]]
        if not matches:
            # The test host does not install all product WM identity properties. This
            # display is dedicated to one preview; admit its one exact-size mapped client.
            matches = sized_windows()
        if len(matches) == 1 or time.monotonic() >= deadline:
            break
        time.sleep(0.025)
    if len(matches) != 1:
        raise RuntimeError("Expected one actual native settings window at the captured size; observed "
                           + repr([(window, attributes.width, attributes.height) for window, attributes in windows]))
    window, attributes = matches[0]
    screen_width, screen_height = (int(value) for value in screen_size.split("x"))
    if attributes.width > screen_width or attributes.height > screen_height:
        raise RuntimeError("The dedicated Xvfb screen is smaller than the actual settings window")
    x = (screen_width - attributes.width) // 2
    y = (screen_height - attributes.height) // 2
    # A managerless Xvfb has no primary working-area provider. Move the existing real
    # window with X11 instead of changing the frozen product or rendering a smaller UI.
    if (attributes.x, attributes.y) != (x, y):
        x11.x.XMoveWindow.argtypes = [c.c_void_p, c.c_ulong, c.c_int, c.c_int]
        x11.x.XMoveWindow(x11.display, window, x, y)
        x11.x.XSync(x11.display, 0)
    controls = [dict(control, x=control["x"] + x - metadata["x"],
                     y=control["y"] + y - metadata["y"]) for control in metadata["controls"]]
    return dict(metadata, x=x, y=y, controls=controls, capture="Actual X11 window pixels", native_window=window)


def capture(args: argparse.Namespace, theme: str) -> None:
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    prefix = f"settings-{theme}"
    phases = ("top", "motion", "bottom", "theme-switch")
    for phase in phases:
        (output / f"{prefix}-{phase}.ready").unlink(missing_ok=True)
    env = dict(os.environ, DISPLAY=args.display)
    x11 = X11(args.display)
    recording = output / f"{prefix}-native.mp4"
    started = time.monotonic()
    first_ready = None
    coordinates = None
    seen: set[str] = set()
    with (output / f"{prefix}-record.log").open("w") as video_log, (output / f"{prefix}-preview.log").open("w") as app_log:
        recorder = subprocess.Popen([
            "ffmpeg", "-y", "-hide_banner", "-loglevel", "warning", "-f", "x11grab",
            "-framerate", "30", "-video_size", args.screen_size, "-draw_mouse", "0", "-i", args.display,
            "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-pix_fmt", "yuv420p",
            str(recording),
        ], env=env, stdout=video_log, stderr=subprocess.STDOUT)
        preview = None
        try:
            command = [args.dotnet, str(args.assembly.resolve()), "--native-settings-preview", theme, str(output)]
            # The application gets its own session bus; other parallel preview displays
            # and the parent verification session retain their independent native lifetimes.
            preview = subprocess.Popen(["dbus-run-session", "--", *command], env=env,
                                       stdout=app_log, stderr=subprocess.STDOUT, start_new_session=True)
            deadline = time.monotonic() + 55
            while preview.poll() is None:
                if recorder.poll() is not None:
                    raise RuntimeError(f"X11 recording failed; see {prefix}-record.log")
                if time.monotonic() >= deadline:
                    raise TimeoutError("Native settings preview did not exit within its deadline")
                available = [(output / f"{prefix}-{phase}.ready", phase) for phase in phases
                             if (output / f"{prefix}-{phase}.ready").exists()]
                if available:
                    # If the runner was descheduled, never label a later screen as an
                    # earlier phase. The attached-native-tree PNG remains available too.
                    _, phase = max(available, key=lambda item: item[0].stat().st_mtime_ns)
                    if phase not in seen:
                        seen.update(item[1] for item in available)
                        metadata = fit_native_window(x11, preview,
                            json.loads((output / f"{prefix}-{phase}.json").read_text()), args.screen_size)
                        coordinates = metadata
                        if first_ready is None:
                            first_ready = time.monotonic() - started
                        # The ready marker follows live-visual rendering, while the native
                        # presentation is scheduled separately. Give that real screen frame
                        # time to arrive inside the explicit 1.2 s preview display hold.
                        time.sleep(0.35)
                        crop = f"{metadata['width']}x{metadata['height']}+{metadata['x']}+{metadata['y']}"
                        subprocess.run(["import", "-display", args.display, "-window", "root", "-crop", crop,
                                        "+repage", str(output / f"{prefix}-{phase}-native.png")],
                                       env=env, check=True, timeout=5)
                        (output / f"{prefix}-{phase}-native.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2))
                time.sleep(0.025)
            if preview.returncode != 0:
                raise RuntimeError(f"Native settings preview exited {preview.returncode}; see {prefix}-preview.log")
        finally:
            if preview is not None and preview.poll() is None:
                os.killpg(preview.pid, signal.SIGTERM)
                try:
                    preview.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(preview.pid, signal.SIGKILL)
                    preview.wait()
            if recorder.poll() is None:
                recorder.send_signal(signal.SIGINT)
                try:
                    recorder.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    recorder.kill()
                    recorder.wait()
            x11.x.XDestroyWindow(x11.display, x11.sentinel)
            x11.x.XCloseDisplay(x11.display)
    if first_ready is None or coordinates is None:
        raise RuntimeError("The real settings window never published a ready phase")
    crop = f"crop={coordinates['width']}:{coordinates['height']}:{coordinates['x']}:{coordinates['y']}"
    detection = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "info", "-i", str(recording),
        "-vf", crop + ",blackdetect=d=0.1:pix_th=0.02:pic_th=0.99", "-an", "-f", "null", "-"],
        check=True, capture_output=True, text=True, timeout=15)
    duration = float(subprocess.check_output(["ffprobe", "-v", "error", "-show_entries", "format=duration",
        "-of", "default=noprint_wrappers=1:nokey=1", str(recording)], text=True, timeout=5))
    black = [(float(start), float(end)) for start, end in
             re.findall(r"black_start:([0-9.]+) black_end:([0-9.]+)", detection.stderr)]
    # Keep all real scrolling/theme/category frames, and stop before the application
    # closes. The standalone GIF must finish on the actual page rather than the empty Xvfb.
    tail = next((start for start, end in reversed(black) if duration - end < 0.1 and start > first_ready), duration)
    filter_graph = (f"trim=start={max(0, first_ready - 0.1):.3f}:end={tail:.3f},setpts=PTS-STARTPTS,{crop},fps=15,split[a][b];"
                    "[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a")
    subprocess.run(["ffmpeg", "-y", "-hide_banner", "-loglevel", "warning", "-i", str(recording),
                    "-filter_complex", filter_graph, "-loop", "-1", str(output / f"{prefix}-native.gif")],
                   check=True, timeout=45)
    print(f"Captured {prefix}: actual native PNGs, GIF, coordinates and logs in {output}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", type=Path, required=True, help="Latest managed Nexa.Desktop.Tests.dll")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--display", default=":100", help="Existing dedicated Xvfb display (default :100)")
    parser.add_argument("--screen-size", default="1280x800")
    parser.add_argument("--theme", choices=("light", "dark", "both"), default="both")
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    for theme in ("light", "dark") if args.theme == "both" else (args.theme,):
        capture(args, theme)


if __name__ == "__main__":
    main()
