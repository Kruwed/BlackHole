#!/usr/bin/env python3
"""Draws the Black Hole icons: a spiral galaxy seen from above, in shades of grey. Its core is a bright ring around a
black centre, and it has two arms of light.

The arms trail clockwise, so the galaxy looks as if it turned counterclockwise.

There are two drawings of the same galaxy:

  * the detailed one, for where there is room to see it: every arm is a bundle of strands with lanes of dust
    between them, it fades into faint outskirts and dissolves into stars at its tip, and there are stars around;
  * the plain one, for small sizes: only the core and the two arms, which are a bit bolder.

The vector logos of the application (`app_logo_dynamic` and `app_logo_small` in WalletWasabi.Fluent/Icons/Icons.axaml)
are these two drawings, as `generate-logo.py --xaml` prints them.

Usage: generate-logo.py <output folder>      draws the icon files (requires pycairo)
       generate-logo.py --xaml               prints the two vector logos

The icon files show the galaxy on a dark tile, except for the one of the tray, which is only the galaxy: the panel
of the desktop is its background.

BlackHoleLogo<size>.png belong to Contrib/Assets and WalletWasabi.Fluent.Desktop/Assets,
BlackHoleLogo.ico and BlackHoleTray.png to WalletWasabi.Fluent/Assets.
"""
import math
import struct
import sys
from pathlib import Path

BOX = 1080.0                  # The drawing is defined in a box of 1080 x 1080 units.
CENTRE = (540.0, 540.0)

ARM_COUNT = 2
ARM_START_ANGLE = 95.0        # Where the first arm starts, in degrees counterclockwise from the direction to the right.
STEP = 4.0                    # An edge is drawn with a straight piece for every so many degrees.


class Galaxy:
    """The measures of the core and of an arm. An arm starts inside the core, which covers its start, and ends in a tip."""

    def __init__(self, core, hole, sweep):
        self.core = core                  # The radius of the core: a bright ring ...
        self.hole = hole                  # ... around the black centre, which has this radius.
        self.sweep = sweep                # How far an arm goes around the core, in degrees.

    def point(self, arm, turned, radius):
        """The point at a distance from the centre, where the arm has gone the share `turned` of its way."""
        # Further out is further clockwise. The y axis of the drawing points down.
        angle = math.radians(ARM_START_ANGLE + arm * 360.0 / ARM_COUNT - self.sweep * turned)
        return (CENTRE[0] + radius * math.cos(angle), CENTRE[1] - radius * math.sin(angle))

    def edges(self, turned):
        """The distances of the inner and of the outer edge of an arm from the centre."""
        raise NotImplementedError

    def strand(self, arm, inner, outer, start=0.0, end=1.0, taper_start=0.0, taper_end=0.0):
        """A part of an arm: what lies between the shares `inner` and `outer` of its width, from `start` to `end` of its way.

        The strand narrows to a point over the shares `taper_start` and `taper_end` of its length.
        """
        steps = max(8, math.ceil(self.sweep * (end - start) / STEP))
        outer_edge, inner_edge = [], []
        for step in range(steps + 1):
            along = step / steps
            turned = start + (end - start) * along
            arm_inner, arm_outer = self.edges(turned)
            width = arm_outer - arm_inner

            fullness = 1.0
            if taper_start > 0 and along < taper_start:
                fullness = math.sin(0.5 * math.pi * along / taper_start)
            if taper_end > 0 and along > 1 - taper_end:
                fullness = min(fullness, math.sin(0.5 * math.pi * (1 - along) / taper_end))

            middle = arm_inner + width * (inner + outer) / 2
            half = width * (outer - inner) / 2 * fullness
            outer_edge.append(self.point(arm, turned, middle + half))
            inner_edge.append(self.point(arm, turned, middle - half))

        return outer_edge + inner_edge[::-1]

    def arm(self, arm):
        return self.strand(arm, 0.0, 1.0)


class Crescents(Galaxy):
    """Arms between two logarithmic spirals that meet in the tip: they are the widest half way and narrow down slowly."""

    def __init__(self, core, hole, sweep, outer_start, inner_start, tip):
        super().__init__(core, hole, sweep)
        self.outer_start = outer_start    # The distance from the centre at which the outer edge of an arm starts ...
        self.inner_start = inner_start    # ... and the one at which its inner edge starts.
        self.tip = tip                    # The distance of the tip of an arm from the centre.

    def edges(self, turned):
        return (self.inner_start * (self.tip / self.inner_start) ** turned,
                self.outer_start * (self.tip / self.outer_start) ** turned)


class Bands(Galaxy):
    """Arms of an even width, which only narrow towards their tip. They gain nearly the same distance with every degree.

    So the arms and the gaps between them are about as wide all the way, which is what a picture of a few pixels needs.
    """

    def __init__(self, core, hole, sweep, start, tip, width, taper, ease):
        super().__init__(core, hole, sweep)
        self.start = start                # The distance from the centre at which the middle of an arm starts ...
        self.tip = tip                    # ... and the distance of its tip.
        self.width = width
        self.taper = taper                # The share of the way over which the arm narrows to its tip.
        self.ease = ease                  # How much slower than evenly the arm leaves the core: 0 is evenly.

    def edges(self, turned):
        middle = self.start + (self.tip - self.start) * ((1 - self.ease) * turned + self.ease * turned ** 2)
        left = (1 - turned) / self.taper
        half = self.width / 2 * (math.sin(0.5 * math.pi * left) ** 0.7 if left < 1 else 1.0)
        return (middle - half, middle + half)


# ---------------------------------------------------------------------------------------------------------------------
# The drawings. A drawing is a list of layers from the back to the front. A layer is one paint and the figures that
# are filled with it: polygons, which are lists of points, and circles, which are (x, y, radius).
#
# A paint is a shade of grey from 0 to 255 with an opacity, or a list of (distance from the centre, grey, opacity):
# then the shade depends on the distance from the centre of the galaxy.
# ---------------------------------------------------------------------------------------------------------------------

DETAILED = Crescents(core=150.0, hole=105.0, sweep=340.0, outer_start=140.0, inner_start=42.0, tip=510.0)

# In a picture of a few pixels thin arms turn grey and their tips disappear, so the plain galaxy has wide arms of an even
# width, which turn a little less far.
PLAIN = Bands(core=140.0, hole=90.0, sweep=305.0, start=80.0, tip=505.0, width=120.0, taper=0.55, ease=0.25)

# The stars around the galaxy: (x, y, radius, grey).
STARS = (
    (229, 137, 10, 240), (127, 259, 8, 240), (701, 412, 9, 240), (238, 463, 8, 240), (308, 574, 9.5, 240),
    (818, 610, 8.5, 240), (643, 218, 5.5, 190), (92, 421, 7.5, 190), (851, 584, 6, 190), (865, 634, 6.5, 190),
    (294, 140, 4, 150), (544, 175, 5.5, 150), (236, 309, 4, 150), (932, 348, 4, 150), (222, 350, 4.5, 150),
    (326, 368, 4.5, 150), (754, 443, 5, 150), (971, 573, 5, 150), (817, 755, 4, 150), (720, 760, 5, 150),
    (229, 832, 4.5, 150), (825, 863, 5.5, 150), (802, 910, 5, 150), (207, 164, 3.5, 118), (675, 170, 3, 118),
    (438, 179, 4, 118), (768, 179, 4, 118), (268, 180, 3, 118), (784, 270, 3, 118), (354, 335, 4, 118),
    (248, 407, 3, 118), (273, 474, 3.5, 118), (805, 545, 3.5, 118), (786, 630, 3.5, 118), (167, 671, 3.5, 118),
    (801, 710, 3.5, 118), (781, 749, 3.5, 118), (177, 760, 3, 118), (969, 778, 3.5, 118), (668, 797, 3, 118),
    (258, 855, 3, 118), (564, 917, 3, 118),
)

# The stars with four points: (x, y, radius).
SPARKLES = ((842.0, 236.0, 30.0), (214.0, 792.0, 24.0), (300.0, 190.0, 17.0))


def sparkle(x, y, radius, waist=0.16):
    """A star with four points."""
    points = []
    for corner in range(8):
        angle = math.pi / 4 * corner
        distance = radius if corner % 2 == 0 else radius * waist
        points.append((x + distance * math.cos(angle), y - distance * math.sin(angle)))
    return points


def detailed():
    g = DETAILED
    layers = []

    # The glow of the disc.
    layers.append(([(0.0, 255, 0.16), (250.0, 255, 0.06), (500.0, 255, 0.0)], [(*CENTRE, 500.0)]))

    # The faint outskirts of the arms. They reach further than the arms and fade away outwards.
    outskirts = Crescents(core=g.core, hole=g.hole, sweep=g.sweep + 62.0, outer_start=g.outer_start * 1.42, inner_start=g.inner_start * 0.9, tip=540.0)
    layers.append(([(g.core, 255, 0.30), (367.0, 255, 0.17), (545.0, 255, 0.0)], [outskirts.arm(arm) for arm in range(ARM_COUNT)]))

    # An arm is a bundle of strands with lanes of dust between them. From the outside to the inside:
    # a faint streak beyond the arm,
    layers.append(((104, 1.0), [g.strand(arm, 1.15, 1.26, 0.44, 0.90, 0.5, 0.5) for arm in range(ARM_COUNT)]))

    # the dim outer strand,
    layers.append(([(g.core, 190, 1.0), (g.tip, 104, 1.0)], [g.strand(arm, 0.86, 1.02, 0.14, 0.94, 0.40, 0.40) for arm in range(ARM_COUNT)]))

    # the body of the arm, which is the brightest near the core,
    layers.append(([(135.0, 255, 1.0), (304.0, 236, 1.0), (g.tip, 158, 1.0)], [g.strand(arm, 0.20, 0.78, 0.0, 1.0, 0.0, 0.05) for arm in range(ARM_COUNT)]))

    # and the thin inner strand.
    layers.append(((208, 1.0), [g.strand(arm, 0.0, 0.13, 0.30, 0.975, 0.12, 0.25) for arm in range(ARM_COUNT)]))

    # At its tip the arm dissolves into stars.
    trail = ((1.024, 511.0, 9.5, 235), (1.052, 514.0, 7.5, 200), (1.085, 518.0, 5.8, 165), (1.124, 522.0, 4.2, 130))
    for turned, distance, radius, grey in trail:
        layers.append(((grey, 1.0), [(*g.point(arm, turned, distance), radius) for arm in range(ARM_COUNT)]))

    for grey in sorted({star[3] for star in STARS}):
        layers.append(((grey, 1.0), [star[:3] for star in STARS if star[3] == grey]))

    layers.append(((235, 1.0), [sparkle(*star) for star in SPARKLES]))

    # The core: a bright ring that glows, around the black centre.
    layers.append(([(g.core - 12.0, 255, 0.34), (g.core + 80.0, 255, 0.0)], [(*CENTRE, g.core + 80.0)]))
    layers.append(((255, 1.0), [(*CENTRE, g.core)]))
    layers.append(((0, 1.0), [(*CENTRE, g.hole)]))

    return layers


def plain():
    g = PLAIN
    return [
        ([(g.core, 255, 1.0), (g.tip, 190, 1.0)], [g.arm(arm) for arm in range(ARM_COUNT)]),
        ((255, 1.0), [(*CENTRE, g.core)]),
        ((0, 1.0), [(*CENTRE, g.hole)]),
    ]


def is_circle(figure):
    return not isinstance(figure[0], tuple)


def clockwise(polygon):
    """The polygon with its points in the order of the hands of a clock, as the screen shows it.

    Figures of one layer may overlap. They are filled as one area as long as they all go around the same way.
    """
    area = sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(polygon, polygon[1:] + polygon[:1]))
    return polygon if area > 0 else polygon[::-1]


def stops(paint):
    """The paint as the stops of a gradient around the centre: its radius and a list of (offset, grey, opacity)."""
    radius = paint[-1][0]
    return radius, [(distance / radius, grey, opacity) for distance, grey, opacity in paint]


def is_gradient(paint):
    return isinstance(paint, list)


# ---------------------------------------------------------------------------------------------------------------------
# The vector logos.
# ---------------------------------------------------------------------------------------------------------------------

def color(grey, opacity):
    return f"#{round(opacity * 255):02X}{grey:02X}{grey:02X}{grey:02X}"


def number(value):
    return f"{value:.1f}".rstrip("0").rstrip(".")


def path(figures):
    """The figures in the path syntax of Avalonia."""
    def point(p):
        return f"{number(p[0])},{number(p[1])}"

    parts = ["F1"]
    for figure in figures:
        if is_circle(figure):
            x, y, radius = figure
            left, right, radii = point((x - radius, y)), point((x + radius, y)), f"{number(radius)},{number(radius)}"
            parts.append(f"M{left} A{radii} 0 1 1 {right} A{radii} 0 1 1 {left} Z")
        else:
            polygon = clockwise(figure)
            parts.append(f"M{point(polygon[0])} L" + " ".join(point(p) for p in polygon[1:]) + " Z")
    return " ".join(parts)


def xaml(key, layers, indent="      "):
    lines = [f'<DrawingImage x:Key="{key}">', "  <DrawingGroup>",
             f'    <GeometryDrawing Brush="Transparent" Geometry="F1 M0,0 L{BOX:g},0 {BOX:g},{BOX:g} 0,{BOX:g} Z" />']
    for paint, figures in layers:
        if is_gradient(paint):
            radius, gradient = stops(paint)
            centre = f"{CENTRE[0]:g},{CENTRE[1]:g}"
            lines.append(f'    <GeometryDrawing Geometry="{path(figures)}">')
            lines.append("      <GeometryDrawing.Brush>")
            lines.append(f'        <RadialGradientBrush Center="{centre}" GradientOrigin="{centre}" RadiusX="{number(radius)}" RadiusY="{number(radius)}">')
            for offset, grey, opacity in gradient:
                lines.append(f'          <GradientStop Offset="{offset:.4f}" Color="{color(grey, opacity)}" />')
            lines.append("        </RadialGradientBrush>")
            lines.append("      </GeometryDrawing.Brush>")
            lines.append("    </GeometryDrawing>")
        else:
            lines.append(f'    <GeometryDrawing Brush="{color(*paint)}" Geometry="{path(figures)}" />')
    lines += ["  </DrawingGroup>", "</DrawingImage>"]
    return "\n".join(indent + line for line in lines)


# ---------------------------------------------------------------------------------------------------------------------
# The icon files.
# ---------------------------------------------------------------------------------------------------------------------

TILE_TOP = 38                 # The tile is dark grey at its top ...
TILE_BOTTOM = 12              # ... and nearly black at its bottom.
TILE_MARGIN = 0.10            # The share of the tile left free on each side of the galaxy.
TILE_CORNER_RADIUS = 0.22     # The radius of the corners of the tile, as a share of its side.

PNG_SIZES = (16, 24, 32, 48, 256)
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
PLAIN_UP_TO = 32              # Icons up to this size show the plain galaxy.

# The panel of GNOME shows a tray icon 16 pixels high, or 32 on a screen of double density. A picture of 32 pixels is
# shown as it is or halved, and halving is exact: every pixel shown is the average of four. A larger picture would
# depend on how well the panel scales it, and a plain scaling of a picture several times too large comes out ragged.
TRAY_SIZE = 32
TRAY_MARGIN = 0.02            # The share of the picture left free on each side of the galaxy.


def fill(context, layers):
    import cairo

    for paint, figures in layers:
        for figure in figures:
            if is_circle(figure):
                x, y, radius = figure
                context.new_sub_path()
                context.arc(x, y, radius, 0, 2 * math.pi)
            else:
                polygon = clockwise(figure)
                context.move_to(*polygon[0])
                for point in polygon[1:]:
                    context.line_to(*point)
                context.close_path()

        if is_gradient(paint):
            radius, gradient = stops(paint)
            source = cairo.RadialGradient(*CENTRE, 0, *CENTRE, radius)
            for offset, grey, opacity in gradient:
                source.add_color_stop_rgba(offset, grey / 255, grey / 255, grey / 255, opacity)
            context.set_source(source)
        else:
            grey, opacity = paint
            context.set_source_rgba(grey / 255, grey / 255, grey / 255, opacity)

        context.set_fill_rule(cairo.FILL_RULE_WINDING)
        context.fill()


def render(size, layers, tile=True, margin=TILE_MARGIN):
    """The picture of the galaxy as a cairo surface: on the tile, or on nothing."""
    import cairo

    surface = cairo.ImageSurface(cairo.FORMAT_ARGB32, size, size)
    context = cairo.Context(surface)
    context.set_antialias(cairo.ANTIALIAS_BEST)

    if tile:
        radius = size * TILE_CORNER_RADIUS
        context.new_sub_path()
        context.arc(size - radius, radius, radius, -math.pi / 2, 0)
        context.arc(size - radius, size - radius, radius, 0, math.pi / 2)
        context.arc(radius, size - radius, radius, math.pi / 2, math.pi)
        context.arc(radius, radius, radius, math.pi, 3 * math.pi / 2)
        context.close_path()

        shade = cairo.LinearGradient(0, 0, 0, size)
        shade.add_color_stop_rgb(0, TILE_TOP / 255, TILE_TOP / 255, TILE_TOP / 255)
        shade.add_color_stop_rgb(1, TILE_BOTTOM / 255, TILE_BOTTOM / 255, TILE_BOTTOM / 255)
        context.set_source(shade)
        context.fill()

    scale = size * (1 - 2 * margin) / BOX
    context.translate(size * margin, size * margin)
    context.scale(scale, scale)
    fill(context, layers)

    return surface


def icon(size):
    return render(size, plain() if size <= PLAIN_UP_TO else detailed())


def tray():
    """The picture of the tray: the plain galaxy on nothing, with its edges made for the way to the panel.

    Avalonia hands the pixels to the panel multiplied with their opacity, and the panel, which expects plain pixels,
    multiplies them once more. A white pixel that the galaxy covers by a quarter would be shown at a sixteenth of the
    full brightness instead of a quarter, and the arms would look thin and dim. So the opacity is stored as its
    square root: the two multiplications then give the brightness that was meant. That holds on a dark panel, which
    is what a light galaxy needs anyway; on a light panel the edges come out darker than meant.
    """
    import cairo

    picture = render(TRAY_SIZE, plain(), tile=False, margin=TRAY_MARGIN)
    result = cairo.ImageSurface(cairo.FORMAT_ARGB32, TRAY_SIZE, TRAY_SIZE)
    source, target = picture.get_data(), result.get_data()

    # The pixels of cairo are blue, green, red and opacity, and the colours are multiplied with the opacity.
    for offset in range(0, picture.get_stride() * TRAY_SIZE, 4):
        blue, green, red, opacity = source[offset:offset + 4]
        if opacity == 0:
            continue
        stored = math.sqrt(opacity / 255)
        gain = stored / (opacity / 255)
        target[offset:offset + 4] = bytes(min(255, round(value * gain)) for value in (blue, green, red)) + bytes([round(stored * 255)])

    result.mark_dirty()
    return result


def png(surface):
    """The picture as a PNG file."""
    import io

    data = io.BytesIO()
    surface.write_to_png(data)
    data = data.getvalue()

    # cairo names white as the colour to show the picture on. A viewer that goes by that would put the transparent parts
    # on white, so the chunk that says it is left out.
    result, position = data[:8], 8
    while position < len(data):
        length, kind = struct.unpack(">I4s", data[position:position + 8])
        if kind != b"bKGD":
            result += data[position:position + 12 + length]
        position += 12 + length

    return result


def ico(pictures):
    """An ICO file of PNG pictures: a list of (size, bytes)."""
    header = struct.pack("<HHH", 0, 1, len(pictures))
    offset = len(header) + 16 * len(pictures)
    entries, body = b"", b""
    for size, data in pictures:
        # A size of 256 is written as 0.
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset + len(body))
        body += data
    return header + entries + body


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)

    if sys.argv[1] == "--xaml":
        print(xaml("app_logo_dynamic", detailed()))
        print()
        print(xaml("app_logo_small", plain()))
        return

    output = Path(sys.argv[1])
    output.mkdir(parents=True, exist_ok=True)

    for size in PNG_SIZES:
        (output / f"BlackHoleLogo{size}.png").write_bytes(png(icon(size)))

    (output / "BlackHoleLogo.ico").write_bytes(ico([(size, png(icon(size))) for size in ICO_SIZES]))
    (output / "BlackHoleTray.png").write_bytes(png(tray()))


if __name__ == "__main__":
    main()
