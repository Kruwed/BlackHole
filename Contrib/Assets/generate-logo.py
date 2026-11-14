#!/usr/bin/env python3
"""Draws the Black Hole icons: a ring of light around a dark centre with a dot of the accent colour in it.

The same shapes are drawn by the vector logo of the application (`app_logo_dynamic` in
WalletWasabi.Fluent/Icons/Icons.axaml); the numbers below are the ones of that drawing.

Usage: generate-logo.py <output folder>      (requires Pillow)

The PNG files belong to Contrib/Assets and WalletWasabi.Fluent.Desktop/Assets, the ICO file to WalletWasabi.Fluent/Assets.
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw

BOX = 1080.0                          # The drawing is defined in a box of 1080 x 1080 units.
RING_OUTER = (540.0, 540.0, 500.0)    # centre x, centre y, radius
RING_INNER = (575.0, 505.0, 370.0)    # The hole is off centre, so the ring is thicker on the lower left side.
DOT = (575.0, 505.0, 105.0)

TILE_TOP = (38, 38, 38)
TILE_BOTTOM = (12, 12, 12)
RING_COLOR = (255, 255, 255)
DOT_TOP_RIGHT = (0x00, 0xFF, 0x4D)
DOT_BOTTOM_LEFT = (0x17, 0xC0, 0x4A)

PNG_SIZES = (16, 24, 32, 48, 256)
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)

SUPERSAMPLING = 8
MARGIN = 0.14          # The share of the tile left free on each side of the drawing.
CORNER_RADIUS = 0.22   # The radius of the corners of the tile, as a share of its side.


def circle(draw, shape, scale, offset, fill):
    x, y, r = shape
    draw.ellipse(
        [offset + (x - r) * scale, offset + (y - r) * scale, offset + (x + r) * scale, offset + (y + r) * scale],
        fill=fill)


def gradient(size, start, end, direction):
    """A linear gradient: 'vertical' goes from the top to the bottom, 'diagonal' from the top right to the bottom left corner."""
    image = Image.new("RGB", (size, size))
    pixels = image.load()
    for y in range(size):
        for x in range(size):
            t = y / (size - 1) if direction == "vertical" else ((size - 1 - x) + y) / (2 * (size - 1))
            pixels[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(start, end))
    return image


def render(size):
    big = size * SUPERSAMPLING
    scale = big * (1 - 2 * MARGIN) / BOX
    offset = big * MARGIN

    tile_mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(tile_mask).rounded_rectangle([0, 0, big - 1, big - 1], radius=big * CORNER_RADIUS, fill=255)

    ring_mask = Image.new("L", (big, big), 0)
    draw = ImageDraw.Draw(ring_mask)
    circle(draw, RING_OUTER, scale, offset, 255)
    circle(draw, RING_INNER, scale, offset, 0)

    dot_mask = Image.new("L", (big, big), 0)
    circle(ImageDraw.Draw(dot_mask), DOT, scale, offset, 255)

    # Gradients are smooth already, so they are made small and stretched.
    tile = gradient(64, TILE_TOP, TILE_BOTTOM, "vertical").resize((big, big), Image.BILINEAR)
    dot = gradient(64, DOT_TOP_RIGHT, DOT_BOTTOM_LEFT, "diagonal").resize((big, big), Image.BILINEAR)

    # The colours cover the whole square and the shape of the tile is in the alpha channel, so
    # scaling down does not darken the edge of the tile.
    image = tile
    image.paste(Image.new("RGB", (big, big), RING_COLOR), (0, 0), ring_mask)
    image.paste(dot, (0, 0), dot_mask)

    result = image.resize((size, size), Image.LANCZOS)
    result.putalpha(tile_mask.resize((size, size), Image.LANCZOS))
    return result


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)

    output = Path(sys.argv[1])
    output.mkdir(parents=True, exist_ok=True)

    for size in PNG_SIZES:
        render(size).save(output / f"BlackHoleLogo{size}.png", optimize=True)

    images = [render(size) for size in ICO_SIZES]
    images[-1].save(output / "BlackHoleLogo.ico", sizes=[(size, size) for size in ICO_SIZES], append_images=images[:-1])


if __name__ == "__main__":
    main()
