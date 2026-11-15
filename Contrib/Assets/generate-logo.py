#!/usr/bin/env python3
"""Draws the Black Hole icons: a spiral galaxy seen from above, a core of the accent colour with two arms of light.

The arms trail clockwise, so the galaxy looks as if it turned counterclockwise.

The vector logo of the application (`app_logo_dynamic` in WalletWasabi.Fluent/Icons/Icons.axaml) is the same
drawing. Its geometries are the ones that `generate-logo.py --xaml` prints.

Usage: generate-logo.py <output folder>      draws the icon files (requires Pillow)
       generate-logo.py --xaml               prints the geometries of the vector logo

The PNG files belong to Contrib/Assets and WalletWasabi.Fluent.Desktop/Assets, the ICO file to WalletWasabi.Fluent/Assets.
"""
import math
import sys
from pathlib import Path

BOX = 1080.0                  # The drawing is defined in a box of 1080 x 1080 units.
CENTRE = (540.0, 540.0)
CORE_RADIUS = 150.0

# An arm is the area between two logarithmic spirals. They start inside the core, which covers the start of the
# arm, and they meet at its tip.
ARM_COUNT = 2
ARM_START_ANGLE = 95.0        # Where the first arm starts, in degrees counterclockwise from the direction to the right.
ARM_SWEEP = 340.0             # How far an arm goes around the core, in degrees.
ARM_OUTER_START = 140.0       # The distance from the centre at which the outer edge of an arm starts ...
ARM_INNER_START = 42.0        # ... and the one at which its inner edge starts.
ARM_TIP = 510.0               # The distance of the tip of an arm from the centre.
ARM_STEPS = 72                # The number of straight pieces that an edge of an arm is drawn with.

TILE_TOP = (38, 38, 38)
TILE_BOTTOM = (12, 12, 12)
ARM_COLOR = (255, 255, 255)
CORE_COLOR = (0xF7, 0x93, 0x1A)      # The accent colour of the application.

PNG_SIZES = (16, 24, 32, 48, 256)
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)

SUPERSAMPLING = 8
MARGIN = 0.10          # The share of the tile left free on each side of the drawing.
CORNER_RADIUS = 0.22   # The radius of the corners of the tile, as a share of its side.


def arm(index):
    """The outline of an arm: along its outer edge from the core to the tip, and back along its inner edge."""
    start = math.radians(ARM_START_ANGLE + index * 360.0 / ARM_COUNT)
    sweep = math.radians(ARM_SWEEP)
    edges = []

    for edge_start in (ARM_OUTER_START, ARM_INNER_START):
        growth = math.log(ARM_TIP / edge_start) / sweep
        edge = []
        for step in range(ARM_STEPS + 1):
            turned = sweep * step / ARM_STEPS
            radius = edge_start * math.exp(growth * turned)

            # Further out is further clockwise. The y axis of the drawing points down.
            angle = start - turned
            edge.append((CENTRE[0] + radius * math.cos(angle), CENTRE[1] - radius * math.sin(angle)))
        edges.append(edge)

    outer, inner = edges

    # Both edges end in the tip.
    return outer + inner[-2::-1]


def xaml():
    """The geometries of the vector logo, in the path syntax of Avalonia."""
    def point(p):
        return f"{p[0]:.1f},{p[1]:.1f}"

    arms = []
    for index in range(ARM_COUNT):
        outline = arm(index)
        arms.append(f"M{point(outline[0])} L" + " ".join(point(p) for p in outline[1:]) + " Z")

    left = point((CENTRE[0] - CORE_RADIUS, CENTRE[1]))
    right = point((CENTRE[0] + CORE_RADIUS, CENTRE[1]))
    radii = f"{CORE_RADIUS:g},{CORE_RADIUS:g}"

    return "F1 " + " ".join(arms), f"F1 M{left} A{radii} 0 1 0 {right} A{radii} 0 1 0 {left} Z"


def gradient(size, start, end):
    """A linear gradient from the top to the bottom."""
    from PIL import Image

    image = Image.new("RGB", (size, size))
    pixels = image.load()
    for y in range(size):
        for x in range(size):
            t = y / (size - 1)
            pixels[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(start, end))
    return image


def render(size):
    from PIL import Image, ImageDraw

    big = size * SUPERSAMPLING
    scale = big * (1 - 2 * MARGIN) / BOX
    offset = big * MARGIN

    tile_mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(tile_mask).rounded_rectangle([0, 0, big - 1, big - 1], radius=big * CORNER_RADIUS, fill=255)

    arm_mask = Image.new("L", (big, big), 0)
    draw = ImageDraw.Draw(arm_mask)
    for index in range(ARM_COUNT):
        draw.polygon([(offset + x * scale, offset + y * scale) for x, y in arm(index)], fill=255)

    core_mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(core_mask).ellipse(
        [offset + (CENTRE[0] - CORE_RADIUS) * scale, offset + (CENTRE[1] - CORE_RADIUS) * scale,
         offset + (CENTRE[0] + CORE_RADIUS) * scale, offset + (CENTRE[1] + CORE_RADIUS) * scale],
        fill=255)

    # The gradient is smooth already, so it is made small and stretched.
    tile = gradient(64, TILE_TOP, TILE_BOTTOM).resize((big, big), Image.BILINEAR)

    # The colours cover the whole square and the shape of the tile is in the alpha channel, so
    # scaling down does not darken the edge of the tile.
    image = tile
    image.paste(Image.new("RGB", (big, big), ARM_COLOR), (0, 0), arm_mask)
    image.paste(Image.new("RGB", (big, big), CORE_COLOR), (0, 0), core_mask)

    result = image.resize((size, size), Image.LANCZOS)
    result.putalpha(tile_mask.resize((size, size), Image.LANCZOS))
    return result


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)

    if sys.argv[1] == "--xaml":
        arms, core = xaml()
        print("arms:", arms)
        print("core:", core)
        return

    output = Path(sys.argv[1])
    output.mkdir(parents=True, exist_ok=True)

    for size in PNG_SIZES:
        render(size).save(output / f"BlackHoleLogo{size}.png", optimize=True)

    images = [render(size) for size in ICO_SIZES]
    images[-1].save(output / "BlackHoleLogo.ico", sizes=[(size, size) for size in ICO_SIZES], append_images=images[:-1])


if __name__ == "__main__":
    main()
