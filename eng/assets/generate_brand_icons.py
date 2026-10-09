#!/usr/bin/env python3
"""Export the retained user artwork without changing its SVG or raster pixels.

Requires Inkscape 1.4 and Pillow 12.3.0 for the recorded exports. Pillow only
packages separately rendered sizes into ICO; it does not resize the artwork.
"""

import argparse
import hashlib
import io
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile

from PIL import Image


ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "Nexa.Desktop" / "Assets"
BRAND = ASSETS / "Brand"
SOURCES = {
    "light": (
        "neon-n-light-transparent.svg",
        "22b862e78b842b7c6ee5952fef900041deb1e54cedc5a846849a4b864a8db818",
    ),
    "dark": (
        "neon-n-dark-transparent.svg",
        "537bcee7b39f0070cdb07655f4716d863bb05397ecbc316062d4c2fb5b4c072d",
    ),
}
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def validate_png(path, size):
    with Image.open(path) as image:
        image.load()
        if image.format != "PNG" or image.mode != "RGBA" or image.size != (size, size):
            raise ValueError(f"Unexpected transparent PNG format or dimensions: {path}")
        if any(image.getpixel(point)[3] != 0 for point in (
            (0, 0), (size - 1, 0), (0, size - 1), (size - 1, size - 1)
        )):
            raise ValueError(f"Artwork has an opaque corner: {path}")
        if image.getchannel("A").getbbox() is None:
            raise ValueError(f"Artwork export is empty: {path}")
        return image.copy()


def export_png(inkscape, source, destination, size):
    print(f"Exporting {source.name} at {size}x{size}", flush=True)
    subprocess.run([
        inkscape, str(source),
        "--export-type=png", "--export-area-page",
        "--export-background-opacity=0", "--export-png-color-mode=RGBA_8",
        f"--export-width={size}", f"--export-height={size}",
        f"--export-filename={destination}",
    ], check=True)
    return validate_png(destination, size)


def validate_ico(path, frames):
    data = path.read_bytes()
    if struct.unpack_from("<HHH", data) != (0, 1, len(ICO_SIZES)):
        raise ValueError("Unexpected ICO directory header")
    seen = []
    for index in range(len(ICO_SIZES)):
        width, height, _, _, _, bits, length, offset = struct.unpack_from(
            "<BBBBHHII", data, 6 + index * 16
        )
        size = width or 256
        if (height or 256) != size:
            raise ValueError("ICO frame is not square")
        if size not in frames or bits != 32 or offset + length > len(data):
            raise ValueError("Unexpected ICO frame dimensions, depth or payload")
        payload = data[offset:offset + length]
        if not payload.startswith(PNG_SIGNATURE):
            raise ValueError("ICO frame is not lossless PNG")
        with Image.open(io.BytesIO(payload)) as image:
            image.load()
            if image.mode != "RGBA" or image.size != (size, size):
                raise ValueError("ICO payload dimensions differ from its directory")
            if image.tobytes() != frames[size].tobytes():
                raise ValueError("ICO packaging changed artwork pixels")
        seen.append(size)
    if tuple(seen) != ICO_SIZES:
        raise ValueError(f"Unexpected ICO directory sizes: {seen}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inkscape", default="inkscape", help="Inkscape executable")
    args = parser.parse_args()
    executable = shutil.which(args.inkscape)
    if executable is None:
        parser.error("Inkscape is required to render each size directly from the SVG")
    for name, expected_hash in SOURCES.values():
        path = BRAND / name
        if hashlib.sha256(path.read_bytes()).hexdigest() != expected_hash:
            raise ValueError(f"Original supplied SVG differs from its recorded SHA-256: {path}")

    # Validate all staged exports before replacing any product resource.
    with tempfile.TemporaryDirectory(prefix="nexacl-brand-") as directory:
        stage = Path(directory)
        exports = []
        for theme, (name, _) in SOURCES.items():
            destination = stage / f"neon-n-{theme}.png"
            export_png(executable, BRAND / name, destination, 512)
            exports.append((destination, BRAND / destination.name))

        source = BRAND / SOURCES["light"][0]
        icon = stage / "icon.png"
        frames = {256: export_png(executable, source, icon, 256)}
        for size in ICO_SIZES[:-1]:
            frames[size] = export_png(executable, source, stage / f"icon-{size}.png", size)
        ico = stage / "icon.ico"
        # Every requested size has its own original-SVG export. Pillow selects the
        # matching supplied image, so its fallback thumbnail path is never used.
        frames[256].save(
            ico, format="ICO", sizes=[(size, size) for size in ICO_SIZES],
            append_images=[frames[size] for size in ICO_SIZES[:-1]],
        )
        validate_ico(ico, frames)
        exports.extend(((icon, ASSETS / icon.name), (ico, ASSETS / ico.name)))
        for generated, destination in exports:
            shutil.copyfile(generated, destination)
            print(f"{destination.relative_to(ROOT)} SHA-256 "
                  f"{hashlib.sha256(destination.read_bytes()).hexdigest()}", flush=True)


if __name__ == "__main__":
    main()
