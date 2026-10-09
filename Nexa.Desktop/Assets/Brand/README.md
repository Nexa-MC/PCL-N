# Supplied neon N artwork

The two transparent SVGs were supplied by the user on 2026-10-09 for NexaCL's
product identity. These are byte-for-byte copies of those attachments, not
redrawn artwork. The original 1254×1254 page, paths, gradients, filters and
transparency remain intact. The filenames identify the intended light and dark
variants; the page's rounded plate is part of the supplied artwork.

| Original attachment | SHA-256 |
| --- | --- |
| `neon-n-light-transparent.svg` | `22b862e78b842b7c6ee5952fef900041deb1e54cedc5a846849a4b864a8db818` |
| `neon-n-dark-transparent.svg` | `537bcee7b39f0070cdb07655f4716d863bb05397ecbc316062d4c2fb5b4c072d` |

The runtime PNGs are 512×512 RGBA exports of the entire corresponding SVG page,
with transparent backgrounds and corners. The existing product paths remain
available for static native packaging: `../icon.png` is a true 256×256 RGBA
export of the light SVG, and `../icon.ico` contains 16, 24, 32, 48, 64, 128 and
256px frames from that same original SVG. Every frame is rendered at its own
target size; the previous 255px image is not resized or reused. No SVG parser
or renderer is needed by the running launcher.

To regenerate from the repository root:

```sh
python eng/assets/generate_brand_icons.py
```

The recorded exports use Inkscape 1.4 (`e7c3feb100`, 2024-10-09) and Pillow
12.3.0. Inkscape preserves page bounds and uses a transparent RGBA8 export.
Pillow only packages the individually exported frames as lossless PNG payloads
inside the ICO; it does not resize, recolor, composite or otherwise edit them.
The script checks the source hashes, PNG dimensions and transparent corners,
and verifies every ICO directory entry and its unchanged RGBA pixels before
replacing the product resources. Different renderer versions may produce
different raster bytes and should be reviewed before replacing these exports.

| Generated resource | SHA-256 |
| --- | --- |
| `neon-n-light.png` | `0a8b4ab96df0c084993846922d99e139311c7db6249eab6f1b1e0ed00ec06000` |
| `neon-n-dark.png` | `c6022d1c67a314bc0429e5adc327f4d452a915ff1b45993f263a8b13e29590bb` |
| `../icon.png` | `63ebb7951e0485a0542a1bcd2725ab3524607b883760c2350b5a01c139b937ef` |
| `../icon.ico` | `f40d94ecbf7bb30893ad453d2c19fa9dac63ab194cdf7001335be7612f3218d3` |
