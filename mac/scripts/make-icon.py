#!/usr/bin/env python3
"""
Generates Voice.app's icon from the 1-bit mark in `mac/branding/voice-mark.png`.

Run after changing the mark:

    python3 mac/scripts/make-icon.py && cd mac && xcodegen generate

Why this is a script and not ten exported PNGs: the mark is a dithered 1-bit
image, and dithering is hostile to naive resizing. Scaling it with a smooth
filter averages neighbouring black and white pixels into grey, so the texture
turns to mush and the silhouette softens. Scaling it with nearest-neighbour to
a non-integer factor is worse in a different way — some source pixels land on
2 output pixels and some on 3, so the dot grid visibly stutters.

The pipeline below avoids both: crop to the ink, blow it up by a whole-number
factor with NEAREST (every dot stays a hard square), then come back down to
each icon size with LANCZOS. The one downscale is what produces clean edges at
512 px and a readable head-and-shoulders at 32 px, where the dither itself is
finer than a pixel and has to average away rather than alias into noise.
"""

from pathlib import Path
import json
import subprocess
import sys

from PIL import Image, ImageChops, ImageDraw

ROOT = Path(__file__).resolve().parent.parent          # mac/
SRC = ROOT / "branding" / "voice-mark.png"
ICONSET = ROOT / "branding" / "Voice.iconset"
APPICON = ROOT / "Voice" / "Assets.xcassets" / "AppIcon.appiconset"

CANVAS = 1024
# Apple's macOS grid: the rounded square occupies 824 of a 1024 canvas, leaving
# the surrounding space the system expects for shadow and optical alignment.
PLATE = 824
CORNER = 185.4 / 824          # superellipse radius as a fraction of the plate
SUPERSAMPLE = 4               # the plate is drawn 4x and averaged down for a clean edge

PLATE_FILL = (252, 252, 250)  # near-white, a touch warm so it is not a glare next to system icons
PLATE_EDGE = (223, 223, 216)  # hairline, so the plate still has an edge on a white background
INK_INSET = 78                # padding between the plate edge and the artwork
EDGE_WIDTH = 4                # hairline around the plate

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


def build_master() -> Image.Image:
    if not SRC.exists():
        sys.exit(f"missing source mark: {SRC}")

    mark = Image.open(SRC).convert("RGBA")

    # Crop to the ink. The supplied mark sits in a large white field; keeping
    # that field would shrink the figure to a dot once it is inset again below.
    grey = mark.convert("L")
    ink = grey.point(lambda p: 255 if p < 128 else 0)
    box = ink.getbbox()
    if box is None:
        sys.exit("source mark has no dark pixels")
    art = mark.crop(box)

    # Whole-number blow-up first: every source pixel becomes an exact square.
    target = PLATE - 2 * INK_INSET
    factor = max(1, -(-target * 3 // max(art.size)))     # ceil, then some headroom
    art = art.resize((art.width * factor, art.height * factor), Image.NEAREST)

    # Then one clean downscale to the size it actually occupies.
    scale = min(target / art.width, target / art.height)
    art = art.resize((max(1, round(art.width * scale)), max(1, round(art.height * scale))), Image.LANCZOS)

    # White in the mark is the plate, not ink — drop it so the plate shows through
    # and the figure keeps its dithered edge instead of sitting on a white block.
    rgb = art.convert("RGB")
    alpha = rgb.convert("L").point(lambda p: 255 - p)
    art.putalpha(alpha)
    black = Image.new("RGBA", art.size, (17, 17, 16, 0))
    black.putalpha(alpha)
    art = black

    plate = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    body = Image.new("RGBA", (PLATE, PLATE), PLATE_FILL + (255,))

    # The hairline has to follow the squircle, not a rectangle. Stroking a rect and
    # then masking it leaves the edge only where the two shapes happen to coincide —
    # four faint smudges near the corners and nothing along the sides.
    outer = squircle_mask(PLATE, CORNER)
    inner = Image.new("L", (PLATE, PLATE), 0)
    inner.paste(squircle_mask(PLATE - 2 * EDGE_WIDTH, CORNER), (EDGE_WIDTH, EDGE_WIDTH))
    ring = ImageChops.subtract(outer, inner)
    body.paste(Image.new("RGBA", (PLATE, PLATE), PLATE_EDGE + (255,)), (0, 0), ring)
    body.putalpha(outer)

    off = (CANVAS - PLATE) // 2
    plate.paste(body, (off, off), body)

    # Optically centred: a head-and-shoulders bust reads as low if it is placed
    # on the geometric centre, because the mass sits at the bottom.
    x = off + (PLATE - art.width) // 2
    y = off + (PLATE - art.height) // 2 - round(PLATE * 0.015)
    plate.paste(art, (x, y), art)
    return plate


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
        img = master.resize((px, px), Image.LANCZOS)
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
