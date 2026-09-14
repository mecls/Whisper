#!/usr/bin/env python3
"""
Generates Spit.app's icon from the dot-matrix logo in `mac/branding/spit-logo.png`.

Run after changing the logo:

    python3 mac/scripts/make-icon.py && cd mac && xcodegen generate

Why this is a script and not ten exported PNGs: the logo is a full-bleed 416 px
tile, two colours, drawn on a grid of 4 px cells. Two things are wrong with
using it as-is. It fills its canvas edge to edge, but macOS expects the plate to
occupy 824 of a 1024 canvas, so dropped in raw it looks oversized beside every
other Dock icon. And 416 px is too small for the 1024 px slot: scaling it up
with a smooth filter turns every dot into a blurry blob, and nearest-neighbour
to a non-integer factor makes the dot grid stutter (some cells land on 7 output
pixels, some on 8).

So the logo is read back as what it is — a grid of on/off cells — and redrawn
from scratch: each lit cell becomes a square at its exact fractional position,
rendered at 4x and box-averaged down, which gives true sub-pixel edges instead
of resampling someone else's pixels. The plate is redrawn too, as Apple's
superellipse at the standard size, filled with the logo's own navy.
"""

from collections import Counter
from pathlib import Path
import json
import subprocess
import sys

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent          # mac/
SRC = ROOT / "branding" / "spit-logo.png"
ICONSET = ROOT / "branding" / "Voice.iconset"
APPICON = ROOT / "Voice" / "Assets.xcassets" / "AppIcon.appiconset"

CANVAS = 1024
# Apple's macOS grid: the rounded square occupies 824 of a 1024 canvas, leaving
# the surrounding space the system expects for shadow and optical alignment.
PLATE = 824
CORNER = 185.4 / 824          # superellipse radius as a fraction of the plate
SUPERSAMPLE = 4               # plate and dots are drawn 4x and averaged down for a clean edge

CELL = 4                      # the logo's dot grid: every cell is a 4x4 block of one colour

# (size, scale) pairs macOS wants in an .icns / .appiconset.
SIZES = [(16, 1), (16, 2), (32, 1), (32, 2), (128, 1), (128, 2), (256, 1), (256, 2), (512, 1), (512, 2)]


def squircle_mask(size: int, radius_frac: float) -> Image.Image:
    """Apple's icon outline is a superellipse, not a rounded rectangle.

    A plain rounded rect has a visible seam where the straight edge meets the
    arc; the superellipse curves continuously into the corner, which is what
    makes an icon sit correctly beside the system's own. n=5 is the standard
    approximation of the macOS shape.
    """
    n = 5.0
    big = size * SUPERSAMPLE
    mask = Image.new("L", (big, big), 0)
    draw = ImageDraw.Draw(mask)
    a = big / 2.0
    # Radius controls how boxy the shape is; fold it into the exponent so a
    # larger corner fraction reads as a rounder icon.
    exponent = n * (0.225 / radius_frac)
    for y in range(big):
        dy = abs((y + 0.5) - a) / a
        t = 1.0 - dy ** exponent
        if t <= 0:
            continue
        dx = t ** (1.0 / exponent)
        x0 = a - dx * a
        x1 = a + dx * a
        draw.line([(x0, y), (x1, y)], fill=255)
    return mask.resize((size, size), Image.LANCZOS)


def read_logo() -> tuple[list[list[bool]], tuple[int, int, int], tuple[int, int, int]]:
    """Returns the logo's lit cells plus its plate and ink colours."""
    if not SRC.exists():
        sys.exit(f"missing source logo: {SRC}")

    logo = Image.open(SRC).convert("RGBA")
    if logo.width != logo.height or logo.width % CELL:
        sys.exit(f"expected a square logo on a {CELL} px grid, got {logo.size}")

    # The logo is two colours; the corners are transparent and antialiased, so
    # only fully opaque pixels count. The darker of the two is the plate.
    opaque = Counter(p[:3] for p in logo.getdata() if p[3] == 255)
    if len(opaque) < 2:
        sys.exit("source logo needs a plate colour and an ink colour")
    plate, ink = sorted((c for c, _ in opaque.most_common(2)), key=sum)

    n = logo.width // CELL
    cells = []
    for gy in range(n):
        row = []
        for gx in range(n):
            block = {logo.getpixel((gx * CELL + dx, gy * CELL + dy))[:3]
                     for dx in range(CELL) for dy in range(CELL)}
            # A cell straddling two colours means the grid assumption is wrong,
            # and the redraw would silently shift or drop dots.
            if ink in block and len(block) > 1:
                sys.exit(f"cell ({gx}, {gy}) is not a solid {CELL}x{CELL} block — is the logo still on a {CELL} px grid?")
            row.append(block == {ink})
        cells.append(row)
    return cells, plate, ink


def build_master() -> Image.Image:
    cells, plate_rgb, ink_rgb = read_logo()
    n = len(cells)

    # Dots, drawn at 4x on integer edges and box-averaged down: each dot's edge
    # lands at its true fractional position (824 / 104 is not a whole number).
    big = PLATE * SUPERSAMPLE
    edge = [round(i * big / n) for i in range(n + 1)]
    dots = Image.new("L", (big, big), 0)
    draw = ImageDraw.Draw(dots)
    for gy, row in enumerate(cells):
        for gx, lit in enumerate(row):
            if lit:
                draw.rectangle([edge[gx], edge[gy], edge[gx + 1] - 1, edge[gy + 1] - 1], fill=255)
    dots = dots.resize((PLATE, PLATE), Image.BOX)

    body = Image.new("RGBA", (PLATE, PLATE), plate_rgb + (255,))
    body.paste(Image.new("RGBA", (PLATE, PLATE), ink_rgb + (255,)), (0, 0), dots)
    body.putalpha(squircle_mask(PLATE, CORNER))

    master = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    off = (CANVAS - PLATE) // 2
    master.paste(body, (off, off), body)
    return master


CONTENTS = {
    "images": [
        {"idiom": "mac", "size": f"{s}x{s}", "scale": f"{m}x", "filename": f"icon_{s}x{s}{'@2x' if m == 2 else ''}.png"}
        for s, m in SIZES
    ],
    "info": {"version": 1, "author": "xcode"},
}


def main() -> None:
    master = build_master()

    for d in (ICONSET, APPICON):
        d.mkdir(parents=True, exist_ok=True)
        for old in d.glob("*.png"):
            old.unlink()

    for size, mult in SIZES:
        px = size * mult
        name = f"icon_{size}x{size}{'@2x' if mult == 2 else ''}.png"
        img = master if px == CANVAS else master.resize((px, px), Image.LANCZOS)
        img.save(ICONSET / name)
        img.save(APPICON / name)

    (APPICON / "Contents.json").write_text(json.dumps(CONTENTS, indent=2) + "\n")
    master.save(ROOT / "branding" / "voice-icon-1024.png")

    # .icns is not used by the build (the asset catalog is), but it is what you
    # drag onto a Finder Get Info panel to preview the result.
    try:
        subprocess.run(["iconutil", "-c", "icns", str(ICONSET),
                        "-o", str(ROOT / "branding" / "Voice.icns")], check=True)
    except (subprocess.CalledProcessError, FileNotFoundError) as e:
        print(f"note: iconutil skipped ({e})")

    print(f"wrote {len(SIZES)} sizes to {APPICON.relative_to(ROOT.parent)}")


if __name__ == "__main__":
    main()
