#!/usr/bin/env python3
"""Draw a Valheim world as one SVG map, with your own data on top.

    world-map.py <world.csv> <locations.csv> [--out map.svg]
        [--paths FILE.csv]... [--points FILE.csv]...
        [--zoom cx,cz,half [--zoom-only]] [--labels none|bosses|all|PREFIX,...]
        [--contour 10] [--px 0.1] [--min-radius 10] [--background finer.csv]
        [--title TEXT]
    world-map.py --self-test

The map shows land tinted by biome, sea, swamp water and rivers, contour
lines (the 30 m waterline heavy), every generated location with its
exterior radius, and the names of the places a player orients by (boss
altars, the start, the traders). Everything outside the world's disc
(10 km radius) is greyed out. North is up; x runs east, z runs north, as in the game.

Inputs come from the plugin's `cli_world_dump`, which samples the world
generator (no zone has to be loaded) and writes world.csv
(x,z,height,biome,river) and locations.csv (name,x,z,radius):

    valheim-cli devcommands              # cli_world_dump is a cheat command;
                                         # devcommands toggles, so check the
                                         # reply says it is now enabled
    valheim-cli cli_world_dump 50 /tmp/myworld

The step is metres between samples: 50 gives a 401 x 401 grid of the whole
map (a few MB), enough for a world view. A finer dump (8 m is over six
million rows) can be passed as --background to sharpen the land picture of
a zoomed window, and its contours where the window is small enough.
Without a directory the dump goes to valheimCLI/world-dumps/<world> in the
game's save folder. Either way the files are written on the machine the
game runs on: copy them to wherever you run this script (scp works); the
map needs nothing but the CSVs.

Your own data goes on top as CSV files with a header row, each option
repeatable:

    --paths FILE   id,x,z[,style]  Consecutive rows with the same id are one
                   line, drawn in file order. A row's style colours the
                   line from that row on; a blank style keeps the colour.
    --points FILE  x,z[,label][,style]  A marker, named when label is set.

A style is a palette name (black, red, orange, yellow, green, teal, blue,
purple, grey, white) or a #rrggbb colour. Unstyled rows take the file's
colour: the first file black, the second red, then blue, orange, purple,
teal, green, so two overlays can be told apart without styling either.

--zoom cx,cz,half draws a second, larger-scale window centred on (cx,cz)
with half-size `half` metres beside the world, and outlines it on the world
view; --zoom-only draws the window alone. --labels picks which locations
are named: none, bosses (the default), all (every drawn location; best
on a zoomed window), or a comma list of name prefixes such as
Crypt,TrollCave.

Python 3.8+ standard library only. Exit codes: 0 map written, 1 self-test
failed, 3 input file missing, 4 bad arguments or input.
"""
import argparse
import array
import base64
import csv
import math
import os
import struct
import sys
import tempfile
import zlib
from xml.etree import ElementTree
from xml.sax.saxutils import escape

SEA = 30.0              # water level: height below it is under water
WORLD_RADIUS = 10000.0  # the world is a disc; outside it is not ground
# Rivers carry their weight on under the sea. Drawn there they look like
# channels through open ocean, so river colour stops where water is deep.
RIVER_FLOOR = SEA - 8.0

OCEAN = '#B9D2E8'
# A swamp's shallows are wet ground with trees in them, not sea.
SWAMP_WATER = '#9FAF86'
RIVER_WATER = '#7FA6D4'
RIVER_BANK = '#D3E4D4'
OUTSIDE = '#E2E2E2'
BIOME_FILL = {
    # Light tints: whatever a mod draws on top must carry the contrast.
    'Meadows': '#E4EED3', 'BlackForest': '#C9DCC4', 'Swamp': '#DCD9C0',
    'Mountain': '#F3F5F7', 'Plains': '#F0EACB', 'Mistlands': '#DCDBE6',
    'AshLands': '#EACFC3', 'DeepNorth': '#EEF3F7', 'Ocean': OCEAN,
}
PALETTE = {
    'black': '#1f1f1f', 'red': '#d0202a', 'orange': '#e07a10', 'yellow': '#d8b000',
    'green': '#18853c', 'teal': '#0f8f88', 'blue': '#1f5fbf', 'purple': '#7a2fb0',
    'grey': '#707070', 'white': '#ffffff',
}
FILE_COLOURS = ('black', 'red', 'blue', 'orange', 'purple', 'teal', 'green')
# Labels are for orientation: the places every player goes. Prefixes, so
# numbered variants of a location match.
LABEL_SETS = {
    'none': (),
    'bosses': ('StartTemple', 'Eikthyr', 'GDKing', 'Bonemass', 'Dragonqueen',
               'GoblinKing', 'Mistlands_DvergrBossEntrance', 'FaderLocation',
               'Vendor', 'Hildir_camp', 'BogWitch'),
}
FINE_CONTOUR_CELLS = 250000  # most --background samples a view draws contours from
EXIT_FAILED, EXIT_MISSING, EXIT_BAD_INPUT = 1, 3, 4


class InputError(Exception):
    pass


def f(v):
    return f'{v:.1f}'


class Grid:
    """A dump held as flat arrays: an 8 m dump of the whole world is over six
    million samples, too many for a dictionary of tuples. Cells the file does
    not cover are NaN and draw as nothing."""

    def __init__(self, path):
        # Two passes over the file: the first learns the grid's shape, the
        # second fills it, so no row is held longer than it is read.
        xs, zs = set(), set()
        for x, z, *_ in read_rows(path, ('x', 'z', 'height', 'biome', 'river')):
            xs.add(x)
            zs.add(z)
        xs, zs = sorted(xs), sorted(zs)
        if len(xs) < 2 or len(zs) < 2:
            raise InputError(f'{path}: a map needs at least a 2 x 2 grid of samples')
        self.step = min(b - a for a, b in zip(xs, xs[1:]))
        self.x0, self.z0 = xs[0], zs[0]
        self.nx = int(round((xs[-1] - xs[0]) / self.step)) + 1
        self.nz = int(round((zs[-1] - zs[0]) / self.step)) + 1
        n = self.nx * self.nz
        self.heights = array.array('f', [math.nan]) * n
        self.biomes = bytearray(n)
        self.rivers = bytearray(n)
        self.names, ids = ['Ocean'], {'Ocean': 0}
        for x, z, height, biome, river in read_rows(path, ('x', 'z', 'height', 'biome', 'river')):
            i = self.index(x, z)
            if biome not in ids:
                ids[biome] = len(self.names)
                self.names.append(biome)
            self.heights[i] = height
            self.biomes[i] = ids[biome]
            self.rivers[i] = max(0, min(255, int(river * 255)))

    def index(self, x, z):
        return (int(round((z - self.z0) / self.step)) * self.nx
                + int(round((x - self.x0) / self.step)))

    def span(self, lo, hi, origin, count):
        """Grid indices covering world coordinates lo..hi on one axis."""
        return (max(0, int(math.floor((lo - origin) / self.step))),
                min(count - 1, int(math.ceil((hi - origin) / self.step))))


def read_rows(path, columns):
    """Rows of a dump as tuples in `columns` order. Streams: a line split is
    several times faster than csv.DictReader on millions of rows, and the
    dump never quotes. Extra columns are ignored."""
    with open(path, newline='') as fh:
        header = [c.strip() for c in fh.readline().split(',')]
        missing = [c for c in columns if c not in header]
        if missing:
            raise InputError(f'{path}: missing column(s) {", ".join(missing)}')
        at = [header.index(c) for c in columns]
        for n, line in enumerate(fh, 2):
            if not line.strip():
                continue
            cells = line.rstrip('\r\n').split(',')
            try:
                yield (float(cells[at[0]]), float(cells[at[1]]), float(cells[at[2]]),
                       cells[at[3]].strip(), float(cells[at[4]]))
            except (IndexError, ValueError):
                raise InputError(f'{path}:{n}: cannot read {line.strip()!r}')


def read_csv(path, required):
    """A small overlay CSV as dictionaries, checked for its required columns."""
    with open(path, newline='') as fh:
        reader = csv.DictReader(fh)
        missing = [c for c in required if c not in (reader.fieldnames or [])]
        if missing:
            raise InputError(f'{path}: missing column(s) {", ".join(missing)}')
        return [{k: (v or '').strip() for k, v in row.items() if k} for row in reader]


def number(row, key, path):
    try:
        return float(row[key])
    except ValueError:
        raise InputError(f'{path}: {key}={row[key]!r} is not a number')


def colour(style, fallback, path):
    if not style:
        return PALETTE[fallback]
    if style.lower() in PALETTE:
        return PALETTE[style.lower()]
    if len(style) == 7 and style[0] == '#' and all(c in '0123456789abcdefABCDEF' for c in style[1:]):
        return style
    raise InputError(f'{path}: style {style!r} is neither a palette name nor #rrggbb')


def read_paths(path, fallback):
    """Runs of one colour, as (colour, [(x, z), ...]). Consecutive rows with
    the same id are one line. A row's style colours the line from that row
    on; a blank style keeps the line's colour, which starts as the file's.
    A colour change ends one run and starts the next at the same point, so
    the line stays unbroken."""
    runs, last_id = [], None
    for row in read_csv(path, ('id', 'x', 'z')):
        point = (number(row, 'x', path), number(row, 'z', path))
        style = row.get('style', '')
        if row['id'] != last_id:
            runs.append((colour(style, fallback, path), [point]))
            last_id = row['id']
            continue
        c = colour(style, fallback, path) if style else runs[-1][0]
        runs[-1][1].append(point)
        if c != runs[-1][0]:
            runs.append((c, [point]))
    return runs


def read_points(path, fallback):
    return [(number(r, 'x', path), number(r, 'z', path), r.get('label', ''),
             colour(r.get('style', ''), fallback, path))
            for r in read_csv(path, ('x', 'z'))]


def png_data_uri(width, height, rows):
    """An RGB PNG as a data URI. The land is one picture rather than a
    rectangle per cell: as vectors a world map is too large to open."""
    raw = b''.join(b'\x00' + bytes(row) for row in rows)

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data
                + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff))

    png = (b'\x89PNG\r\n\x1a\n'
           + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
           + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))
    return 'data:image/png;base64,' + base64.b64encode(png).decode('ascii')


def ground_colour(height, biome, river):
    """What one sample looks like. Water is three things: the sea, a river,
    and a swamp's shallows."""
    if height >= SEA:
        return RIVER_BANK if river > 0.5 else BIOME_FILL.get(biome, '#cccccc')
    if biome == 'Swamp':
        return SWAMP_WATER
    if river > 0.5 and height >= RIVER_FLOOR:
        return RIVER_WATER
    return OCEAN


class View:
    """A world rectangle mapped to pixels, north up."""

    def __init__(self, x0, z0, x1, z1, px):
        self.x0, self.z0, self.x1, self.z1, self.px = x0, z0, x1, z1, px
        self.w, self.h = (x1 - x0) * px, (z1 - z0) * px

    def X(self, x):
        return (x - self.x0) * self.px

    def Y(self, z):
        return (self.z1 - z) * self.px

    def inside(self, x, z, pad=0.0):
        return self.x0 - pad <= x <= self.x1 + pad and self.z0 - pad <= z <= self.z1 + pad


def crosses(view, pts):
    """Whether any segment's bounding box meets the view: a line can cross a
    window with none of its points inside it."""
    return any(min(p[0], q[0]) <= view.x1 and max(p[0], q[0]) >= view.x0
               and min(p[1], q[1]) <= view.z1 and max(p[1], q[1]) >= view.z0
               for p, q in zip(pts, pts[1:]))


def land_image(view, grid):
    """One pixel per sample over the view, embedded as a PNG."""
    ix0, ix1 = grid.span(view.x0, view.x1, grid.x0, grid.nx)
    iz0, iz1 = grid.span(view.z0, view.z1, grid.z0, grid.nz)
    if ix1 < ix0 or iz1 < iz0:
        return ''
    cache = {}
    outside = bytes(int(OUTSIDE[i:i + 2], 16) for i in (1, 3, 5))
    rows = []
    for iz in range(iz1, iz0 - 1, -1):
        row = bytearray()
        for ix in range(ix0, ix1 + 1):
            i = iz * grid.nx + ix
            h = grid.heights[i]
            if h != h:  # NaN: a sample the dump lacks
                row += outside
                continue
            key = (h >= SEA, h >= RIVER_FLOOR, grid.biomes[i], grid.rivers[i] > 127)
            rgb = cache.get(key)
            if rgb is None:
                c = ground_colour(h, grid.names[grid.biomes[i]], grid.rivers[i] / 255.0)
                rgb = cache[key] = bytes(int(c[k:k + 2], 16) for k in (1, 3, 5))
            row += rgb
        rows.append(row)
    # Each pixel is centred on its sample, so the picture reaches half a
    # step past the outermost samples.
    half = grid.step / 2
    x0, z1 = grid.x0 + ix0 * grid.step - half, grid.z0 + iz1 * grid.step + half
    size = (ix1 - ix0 + 1) * grid.step * view.px, (iz1 - iz0 + 1) * grid.step * view.px
    return (f'<image x="{f(view.X(x0))}" y="{f(view.Y(z1))}" width="{f(size[0])}" '
            f'height="{f(size[1])}" preserveAspectRatio="none" '
            f'href="{png_data_uri(ix1 - ix0 + 1, iz1 - iz0 + 1, rows)}"/>')


def contours(view, grid, interval):
    """Path data per level, for the waterline and every `interval` metres
    above it. Each cell is visited once and emits only the levels that pass
    through it, so the cost follows the relief, not levels x cells."""
    ix0, ix1 = grid.span(view.x0, view.x1, grid.x0, grid.nx)
    iz0, iz1 = grid.span(view.z0, view.z1, grid.z0, grid.nz)
    s, h, nx = grid.step, grid.heights, grid.nx
    rim = (WORLD_RADIUS + 2 * s) ** 2  # cells wholly outside the disc are clipped anyway
    paths = {}
    for iz in range(iz0, iz1):
        z = grid.z0 + iz * s
        for ix in range(ix0, ix1):
            x = grid.x0 + ix * s
            if x * x + z * z > rim:
                continue
            i = iz * nx + ix
            hs = (h[i], h[i + 1], h[i + 1 + nx], h[i + nx])
            if any(v != v for v in hs):  # NaN: a sample the dump lacks
                continue
            lo, hi = min(hs), max(hs)
            if hi < SEA:
                continue
            corners = ((x, z), (x + s, z), (x + s, z + s), (x, z + s))
            k = max(0, int(math.floor((lo - SEA) / interval)) + 1)
            while SEA + k * interval <= hi:
                level = SEA + k * interval
                k += 1
                # Marching squares: where the level crosses each edge of the
                # cell. Two crossings are one segment. Four are a saddle, and
                # the cell's mean height decides which diagonal is joined.
                cut = []
                for p in range(4):
                    q = (p + 1) % 4
                    if (hs[p] >= level) != (hs[q] >= level):
                        t = (level - hs[p]) / (hs[q] - hs[p])
                        cut.append((view.X(corners[p][0] + (corners[q][0] - corners[p][0]) * t),
                                    view.Y(corners[p][1] + (corners[q][1] - corners[p][1]) * t)))
                if len(cut) == 4 and (hs[0] >= level) != (sum(hs) / 4 >= level):
                    cut = cut[1:] + cut[:1]
                d = paths.setdefault(level, [])
                for (ax, ay), (bx, by) in zip(cut[::2], cut[1::2]):
                    d.append(f'M{f(ax)} {f(ay)}L{f(bx)} {f(by)}')
    return paths


def label_keys(spec):
    if spec in LABEL_SETS:
        return LABEL_SETS[spec]
    return tuple(p.strip() for p in spec.split(',') if p.strip())


def render(view, clip_id, contour_grid, land_grid, locations, overlays, a, zoom_box=None):
    """One view as an SVG group, clipped to its own rectangle so nothing it
    draws spills onto its neighbour."""
    big = view.px >= 0.5
    cx, cz, r = view.X(0), view.Y(0), WORLD_RADIUS * view.px
    # The ground is clipped to the world's disc: a dump answers points past
    # the rim with values that are not a place anyone can stand.
    out = [f'<clipPath id="{clip_id}"><rect width="{f(view.w)}" height="{f(view.h)}"/></clipPath>',
           f'<clipPath id="{clip_id}-disc"><circle cx="{f(cx)}" cy="{f(cz)}" r="{f(r)}"/></clipPath>',
           f'<g clip-path="url(#{clip_id})">',
           f'<rect width="{f(view.w)}" height="{f(view.h)}" fill="{OUTSIDE}"/>',
           f'<g clip-path="url(#{clip_id}-disc)">', land_image(view, land_grid)]
    # Contours follow the finer dump where the view holds few enough of its
    # samples to stay a file a browser opens; the coarse one elsewhere.
    if (view.w / land_grid.step / view.px) * (view.h / land_grid.step / view.px) <= FINE_CONTOUR_CELLS:
        contour_grid = land_grid
    for level, d in sorted(contours(view, contour_grid, a.contour).items()):
        coast = level == SEA
        out.append(f'<path d="{" ".join(d)}" fill="none" stroke="{"#1f3a5f" if coast else "#5a4a30"}" '
                   f'stroke-width="{"1.2" if coast else "0.35"}" stroke-opacity="{"1" if coast else "0.6"}"/>')
    out.append('</g>')
    out.append(f'<circle cx="{f(cx)}" cy="{f(cz)}" r="{f(r)}" fill="none" stroke="#909090" '
               f'stroke-width="0.8" stroke-dasharray="6,4"/>')

    everything = a.labels == 'all'
    keys = () if everything else label_keys(a.labels)
    labels = []
    for name, x, z, radius in locations:
        if radius < a.min_radius or not view.inside(x, z, radius):
            continue
        named = everything or (bool(keys) and name.startswith(keys))
        X, Y = view.X(x), view.Y(z)
        out.append(f'<circle cx="{f(X)}" cy="{f(Y)}" r="{f(radius * view.px)}" fill="none" '
                   f'stroke="#8a4ab0" stroke-width="0.5" stroke-opacity="0.4"/>')
        out.append(f'<circle cx="{f(X)}" cy="{f(Y)}" r="{"2.8" if named else "1.8"}" '
                   f'fill="{"#3b1050" if named else "#8a4ab0"}" fill-opacity="0.85"/>')
        if named:
            labels.append((X + 4, Y - 3, name, '#3b1050'))

    for kind, data in overlays:
        if kind == 'paths':
            for c, pts in data:
                if not crosses(view, pts):
                    continue
                pl = ' '.join(f'{f(view.X(x))},{f(view.Y(z))}' for x, z in pts)
                out.append(f'<polyline points="{pl}" fill="none" stroke="{c}" stroke-width="{"2.6" if big else "2"}" '
                           f'stroke-linecap="round" stroke-linejoin="round"/>')
        else:
            for x, z, label, c in data:
                if not view.inside(x, z):
                    continue
                X, Y = view.X(x), view.Y(z)
                out.append(f'<circle cx="{f(X)}" cy="{f(Y)}" r="4" fill="{c}" stroke="#ffffff" stroke-width="1.2"/>')
                if label:
                    labels.append((X + 6, Y - 5, label, c))

    if zoom_box:
        x0, z0, x1, z1 = zoom_box
        out.append(f'<rect x="{f(view.X(x0))}" y="{f(view.Y(z1))}" width="{f((x1 - x0) * view.px)}" '
                   f'height="{f((z1 - z0) * view.px)}" fill="none" stroke="#111" stroke-width="1.2" '
                   f'stroke-dasharray="5,3"/>')
    # Labels last, with a white halo, so no line or circle covers a name.
    size = 11 if big else 9
    for X, Y, text, c in labels:
        out.append(f'<text x="{f(X)}" y="{f(Y)}" font-size="{size}" fill="{c}" stroke="#ffffff" '
                   f'stroke-width="2.5" stroke-opacity="0.85" paint-order="stroke">{escape(text)}</text>')
    out.append('</g>')
    out.append(f'<rect width="{f(view.w)}" height="{f(view.h)}" fill="none" stroke="#666" stroke-width="0.8"/>')
    return '\n'.join(out)


def wrap(text, width_px, font_px):
    """Break a caption to the page width: a line running off the edge would
    be cut without the reader knowing."""
    limit = max(20, int((width_px - 12) / (font_px * 0.55)))
    lines, line = [], ''
    for word in text.split(' '):
        if line and len(line) + 1 + len(word) > limit:
            lines.append(line)
            line = word
        else:
            line = f'{line} {word}'.strip()
    return lines + ([line] if line else [])


def header(width, title, grid):
    """Caption and a swatch key above the map, not over it."""
    out, y = [], 16
    for line in wrap(title, width, 12):
        out.append(f'<text x="6" y="{y}" font-size="12" fill="#111">{escape(line)}</text>')
        y += 15
    keys = [(n, BIOME_FILL[n]) for n in BIOME_FILL if n in grid.names and n != 'Ocean']
    keys += [('sea', OCEAN), ('river', RIVER_WATER), ('swamp water', SWAMP_WATER),
             ('outside the world', OUTSIDE)]
    x, y = 6, y + 2
    for name, fill in keys:
        step = 22 + len(name) * 6
        if x + step > width:
            x, y = 6, y + 16
        out.append(f'<rect x="{x}" y="{y - 9}" width="12" height="10" fill="{fill}" stroke="#888" stroke-width="0.5"/>'
                   f'<text x="{x + 16}" y="{y}" font-size="10" fill="#333">{escape(name)}</text>')
        x += step
    return '\n'.join(out), y + 10


def parse_zoom(spec):
    try:
        cx, cz, half = (float(v) for v in spec.split(','))
    except ValueError:
        raise InputError(f'--zoom wants cx,cz,half in metres, got {spec!r}')
    if half <= 0:
        raise InputError('--zoom half-size must be positive')
    return cx, cz, half


def build(a):
    """The whole SVG document for parsed arguments, and a one-line summary."""
    grid = Grid(a.world)
    land = Grid(a.background) if a.background else grid
    locations = [(r['name'], number(r, 'x', a.locations), number(r, 'z', a.locations),
                  number(r, 'radius', a.locations))
                 for r in read_csv(a.locations, ('name', 'x', 'z', 'radius'))]
    overlays, n = [], 0
    for kind, files, reader in (('paths', a.paths, read_paths), ('points', a.points, read_points)):
        for path in files:
            overlays.append((kind, reader(path, FILE_COLOURS[n % len(FILE_COLOURS)])))
            n += 1

    zoom = parse_zoom(a.zoom) if a.zoom else None
    if a.zoom_only and not zoom:
        raise InputError('--zoom-only needs --zoom cx,cz,half')
    if zoom:
        cx, cz, half = zoom
        zoom_box = (cx - half, cz - half, cx + half, cz + half)

    shown = sum(1 for l in locations if l[3] >= a.min_radius)
    title = a.title or (
        f'{os.path.basename(a.world)}: {len(locations)} locations ({shown} with radius >= '
        f'{a.min_radius:g} m drawn, circle = exterior radius); land at {land.step:g} m, contours every '
        f'{a.contour} m, heavy line the {SEA:g} m waterline'
        + (f'; {n} overlay file(s)' if n else ''))

    views = []
    if zoom and a.zoom_only:
        views.append((View(*zoom_box, min(2.0, 1400 / (2 * half))), None))
        title += f'; window ({cx:g},{cz:g}) +-{half:g} m'
    else:
        # Frame the land, not the whole disc, so islands fill the page.
        idx = [i for i, h in enumerate(grid.heights) if h >= SEA]
        if idx:
            xs = [grid.x0 + (i % grid.nx) * grid.step for i in idx]
            zs = [grid.z0 + (i // grid.nx) * grid.step for i in idx]
            box = (min(xs) - 300, min(zs) - 300, max(xs) + 300, max(zs) + 300)
        else:
            box = (grid.x0, grid.z0, grid.x0 + (grid.nx - 1) * grid.step, grid.z0 + (grid.nz - 1) * grid.step)
        views.append((View(*box, a.px), zoom_box if zoom else None))
        if zoom:
            views.append((View(*zoom_box, min(2.0, 700 / (2 * half))), None))
            title += f'; dashed box the window at ({cx:g},{cz:g}) +-{half:g} m, drawn on the right'

    width = sum(v.w for v, _ in views) + 20 * (len(views) - 1)
    head, top = header(width, title, land)
    body, x = [], 0.0
    for k, (view, box) in enumerate(views):
        body.append(f'<g transform="translate({f(x)},{f(top)})">'
                    + render(view, f'view{k}', grid, land, locations, overlays, a, box) + '</g>')
        x += view.w + 20
    height = top + max(v.h for v, _ in views)
    svg = ('<?xml version="1.0" encoding="UTF-8"?>\n'
           f'<svg xmlns="http://www.w3.org/2000/svg" width="{f(width)}" height="{f(height)}" '
           f'viewBox="0 0 {f(width)} {f(height)}" font-family="sans-serif">\n'
           f'<rect width="100%" height="100%" fill="#ffffff"/>\n{head}\n' + '\n'.join(body) + '\n</svg>\n')
    return svg, f'{len(locations)} locations, {n} overlay file(s)'


def parser():
    class Parser(argparse.ArgumentParser):
        def error(self, message):  # bad input is exit 4 here, not argparse's 2
            self.print_usage(sys.stderr)
            print(f'{self.prog}: error: {message}', file=sys.stderr)
            sys.exit(EXIT_BAD_INPUT)

    ap = Parser(description='Draw a world dump from cli_world_dump as an SVG map.',
                epilog='world-map.py --self-test draws a small synthetic world and checks the result.')
    ap.add_argument('world', help='world.csv from cli_world_dump')
    ap.add_argument('locations', help='locations.csv from cli_world_dump')
    ap.add_argument('--out', help='SVG to write (default: map.svg next to world.csv)')
    ap.add_argument('--paths', action='append', default=[], metavar='FILE',
                    help='CSV id,x,z[,style]: lines to draw; repeatable')
    ap.add_argument('--points', action='append', default=[], metavar='FILE',
                    help='CSV x,z[,label][,style]: markers to draw; repeatable')
    ap.add_argument('--zoom', metavar='CX,CZ,HALF', help='a window beside the world, in metres')
    ap.add_argument('--zoom-only', action='store_true', help='draw only the --zoom window')
    ap.add_argument('--labels', default='bosses', metavar='SET',
                    help='none, bosses (default), all, or comma-separated name prefixes')
    ap.add_argument('--contour', type=int, default=10, help='metres between contour lines (default 10)')
    ap.add_argument('--px', type=float, default=0.1, help='pixels per metre on the world view (default 0.1)')
    ap.add_argument('--min-radius', type=float, default=10.0,
                    help='skip locations with a smaller exterior radius (default 10; 0 draws all)')
    ap.add_argument('--background', metavar='FILE', help='a finer dump for the land picture')
    ap.add_argument('--title', help='caption to use instead of the generated one')
    return ap


def main(argv):
    a = parser().parse_args(argv)
    if a.contour <= 0 or a.px <= 0:
        raise InputError('--contour and --px must be positive')
    svg, summary = build(a)
    out = a.out or os.path.join(os.path.dirname(a.world) or '.', 'map.svg')
    with open(out, 'w') as fh:
        fh.write(svg)
    print(f'{out}: {summary}, {len(svg) // 1024} KB')


def well_formed(text):
    try:
        ElementTree.fromstring(text.encode('utf-8'))
        return True
    except ElementTree.ParseError:
        return False


def self_test():
    """Draw a tiny synthetic world through the real entry point and check the
    SVG holds what each layer should have put there."""
    with tempfile.TemporaryDirectory() as d:
        def write(name, text):
            with open(os.path.join(d, name), 'w') as fh:
                fh.write(text)
            return os.path.join(d, name)

        # A 5 x 5 grid, 100 m apart: sea round a two-step hill in the middle.
        rows = ['x,z,height,biome,river,base_height']
        for z in range(-200, 201, 100):
            for x in range(-200, 201, 100):
                height = {0: 55.0, 100: 38.0}.get(max(abs(x), abs(z)), 10.0)
                rows.append(f'{x},{z},{height},{"Meadows" if height > SEA else "Ocean"},0.00,0.1')
        world = write('world.csv', '\n'.join(rows) + '\n')
        locs = write('locations.csv', 'name,x,z,radius\nStartTemple,0.0,0.0,25.0\nTinyThing,50,50,2\n')
        paths = write('paths.csv', 'id,x,z,style\na,-150,-150,\na,0,0,red\na,150,150,\nb,0,100,#123abc\nb,100,0,\n')
        points = write('points.csv', 'x,z,label,style\n50,-50,Camp,blue\n')
        out = os.path.join(d, 'map.svg')
        main([world, locs, '--paths', paths, '--points', points, '--zoom', '0,0,150', '--out', out])
        svg = open(out).read()
        checks = {
            'land picture': 'data:image/png;base64,' in svg,
            'waterline contour': 'stroke="#1f3a5f"' in svg,
            'contour above it': 'stroke="#5a4a30"' in svg,
            'location radius circle': 'r="2.5"' in svg,
            'small location skipped': 'TinyThing' not in svg,
            'boss label': '>StartTemple</text>' in svg,
            'path, file colour': f'stroke="{PALETTE["black"]}"' in svg,
            'path, style change': f'stroke="{PALETTE["red"]}"' in svg,
            'path, hex style': 'stroke="#123abc"' in svg,
            'point marker': f'fill="{PALETTE["blue"]}" stroke="#ffffff"' in svg,
            'point label': '>Camp</text>' in svg,
            'world view and window': 'id="view0-disc"' in svg and 'id="view1-disc"' in svg,
            'window outlined': 'stroke-dasharray="5,3"' in svg,
            'well-formed XML': well_formed(svg),
        }
    for name, ok in checks.items():
        print(f'{"ok  " if ok else "FAIL"} {name}')
    return 0 if all(checks.values()) else EXIT_FAILED


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        sys.exit(self_test())
    try:
        main(sys.argv[1:])
    except FileNotFoundError as e:
        sys.exit(print(f'world-map.py: {e.filename}: no such file', file=sys.stderr) or EXIT_MISSING)
    except InputError as e:
        sys.exit(print(f'world-map.py: {e}', file=sys.stderr) or EXIT_BAD_INPUT)
