"""Our building palette atlas (card T3.4 step 1): flat colour swatches on one texture, Synty-matched in style only.

Every colour here is our own pick for an Ohio river village (painted clapboard, red and buff brick, cut stone, asphalt
shingle, standing-seam and corrugated metal, barn red), kept inside the saturation and value ranges measured from the
Synty City buildings in Unity (art/buildings/style-guide.md section 2): saturation mostly under 0.5, value (sRGB) under
about 0.72, so our buildings sit in the same light as the Synty props. No colour is sampled from a Synty texture.

The atlas is 512 x 512 px: 16 x 16 swatches of 32 x 32 px. Faces put their UVs inside one swatch's inner 24 x 24 px
(4 px margin), so mip levels down to 4 px per swatch stay clean.

  python palette.py --png out.png [--json out.json]    (or inside Blender: blender -b --python palette.py -- ...)
"""
import colorsys
import json
import struct
import sys
import zlib

ATLAS = 512
CELL = 32
GRID = ATLAS // CELL
MARGIN = 4

# name, sRGB hex, family. Order is the grid order (row-major from the top-left); append only, so UVs stay stable.
SWATCHES = [
    # walls: painted clapboard and board siding
    ('siding_white', 0xb9b6ad, 'wall'), ('siding_cream', 0xb6aa8b, 'wall'), ('siding_yellow', 0xb09d68, 'wall'),
    ('siding_sage', 0x8f9a80, 'wall'), ('siding_greyblue', 0x7f8b97, 'wall'), ('siding_grey', 0x92918b, 'wall'),
    ('siding_tan', 0xa18b6d, 'wall'), ('siding_red', 0x855343, 'wall'), ('siding_teal', 0x617f7a, 'wall'),
    ('siding_brown', 0x705c4a, 'wall'), ('siding_green', 0x6f7d5d, 'wall'), ('siding_blue', 0x67778c, 'wall'),
    ('siding_rose', 0xa38578, 'wall'), ('siding_slate', 0x5f666d, 'wall'), ('siding_buff', 0xa99c80, 'wall'),
    ('siding_olive', 0x7f7a58, 'wall'),
    # brick and painted brick
    ('brick_red', 0x8a5141, 'brick'), ('brick_dark', 0x6c3f35, 'brick'), ('brick_buff', 0xa68f6d, 'brick'),
    ('brick_brown', 0x7b5b47, 'brick'), ('brick_orange', 0x96604a, 'brick'), ('brick_cream', 0xaea68d, 'brick'),
    ('brick_green', 0x67715b, 'brick'), ('brick_grey', 0x7e7c77, 'brick'), ('brick_maroon', 0x6f3b36, 'brick'),
    ('brick_white', 0xb2afa6, 'brick'),
    # stone, concrete, foundations
    ('stone_grey', 0x7e7b73, 'stone'), ('stone_tan', 0x958568, 'stone'), ('stone_dark', 0x5d5b56, 'stone'),
    ('concrete', 0x8d8b85, 'stone'), ('concrete_dark', 0x67655f, 'stone'), ('block_grey', 0x85837c, 'stone'),
    # roofs
    ('shingle_charcoal', 0x3e4043, 'roof'), ('shingle_grey', 0x5b5d5f, 'roof'), ('shingle_brown', 0x5d4e43, 'roof'),
    ('shingle_green', 0x46534b, 'roof'), ('shingle_red', 0x5e3a34, 'roof'), ('shingle_black', 0x2c2d2f, 'roof'),
    ('metal_galv', 0x8b9197, 'roof'), ('metal_red', 0x7b3b34, 'roof'), ('metal_green', 0x405b49, 'roof'),
    ('metal_charcoal', 0x3b3e41, 'roof'), ('metal_blue', 0x44566a, 'roof'), ('roof_tar', 0x3b3b3b, 'roof'),
    ('roof_membrane', 0x78787b, 'roof'), ('roof_gravel', 0x6a675f, 'roof'), ('metal_rust', 0x7b4d35, 'roof'),
    ('metal_white', 0xa9aba7, 'roof'),
    # trim and fascia
    ('trim_white', 0xbebbb3, 'trim'), ('trim_cream', 0xb1a790, 'trim'), ('trim_green', 0x35473b, 'trim'),
    ('trim_black', 0x27292b, 'trim'), ('trim_brown', 0x58453b, 'trim'), ('trim_maroon', 0x5f302d, 'trim'),
    ('trim_navy', 0x303b4b, 'trim'), ('trim_grey', 0x6e7175, 'trim'), ('trim_tan', 0x9b8a6c, 'trim'),
    ('trim_red', 0x7a3631, 'trim'),
    # doors
    ('door_red', 0x7f3631, 'door'), ('door_black', 0x25272b, 'door'), ('door_green', 0x3b5141, 'door'),
    ('door_wood', 0x7b5b3f, 'door'), ('door_white', 0xb6b3ab, 'door'), ('door_blue', 0x3d5067, 'door'),
    ('door_grey', 0x6c6f72, 'door'), ('door_steel', 0x8a8e91, 'door'),
    # metal wall panels
    ('panel_white', 0xaaaba7, 'panel'), ('panel_tan', 0xa49579, 'panel'), ('panel_green', 0x617b65, 'panel'),
    ('panel_red', 0x7e4037, 'panel'), ('panel_grey', 0x868a8d, 'panel'), ('panel_blue', 0x5c6e80, 'panel'),
    # barns
    ('barn_red', 0x7f3c32, 'barn'), ('barn_white', 0xb3aea3, 'barn'), ('barn_weathered', 0x6c645a, 'barn'),
    ('barn_board_dark', 0x4f453c, 'barn'),
    # shopfronts, signs and awnings
    ('awning_green', 0x3f5b47, 'shop'), ('awning_red', 0x7b3735, 'shop'), ('awning_navy', 0x34415b, 'shop'),
    ('awning_tan', 0x9b8665, 'shop'), ('awning_maroon', 0x5e2d33, 'shop'), ('sign_cream', 0xb4ab91, 'shop'),
    ('sign_dark', 0x303337, 'shop'), ('sign_green', 0x3f5646, 'shop'), ('sign_red', 0x7a3a34, 'shop'),
    # porch, wood, details
    ('porch_floor', 0x7b6b59, 'detail'), ('porch_ceiling', 0xa0a69b, 'detail'), ('lattice', 0x9a9285, 'detail'),
    ('wood_dark', 0x504034, 'detail'), ('wood_light', 0x9a8366, 'detail'), ('steel', 0x6c7176, 'detail'),
    ('rubber', 0x232323, 'detail'), ('safety_yellow', 0xa08c44, 'detail'), ('glass_far', 0x3c4b5b, 'detail'),
    ('interior_dark', 0x262626, 'detail'), ('chimney_cap', 0x55565a, 'detail'), ('gutter_white', 0xb4b2ab, 'detail'),
    ('vent_grey', 0x75787a, 'detail'),
]

INDEX = {name: i for i, (name, _, _) in enumerate(SWATCHES)}
assert len(SWATCHES) <= GRID * GRID
assert len(INDEX) == len(SWATCHES), 'duplicate swatch name'

# The glass material's colour (M_Bld_Glass, not on the atlas): a blue-grey in the measured glass range.
GLASS = 0x4b6074


def rgb(h):
    return ((h >> 16) & 255, (h >> 8) & 255, h & 255)


def hsv(h):
    r, g, b = rgb(h)
    return colorsys.rgb_to_hsv(r / 255.0, g / 255.0, b / 255.0)


def cell(name):
    """(column, row) of a swatch; row 0 is the top row of the image."""
    i = INDEX[name]
    return i % GRID, i // GRID


def uv_centre(name):
    """UV of the swatch centre (OpenGL/Unity convention: v = 0 at the bottom of the image)."""
    c, r = cell(name)
    return ((c + 0.5) * CELL / ATLAS, 1.0 - (r + 0.5) * CELL / ATLAS)


UV_HALF = (CELL / 2 - MARGIN) / ATLAS     # largest UV offset from the centre that stays inside the swatch


def _png(path, w, h, rows):
    def chunk(t, d):
        c = struct.pack('>I', len(d)) + t + d
        return c + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    raw = b''.join(b'\x00' + bytes(r) for r in rows)
    data = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
    data += chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')
    with open(path, 'wb') as f:
        f.write(data)


def write_png(path):
    """Flat swatches; unused cells are a neutral mid grey. Deterministic bytes (no timestamps)."""
    fill = rgb(0x6e6e6e)
    rows = []
    for y in range(ATLAS):
        r = y // CELL
        row = []
        for x in range(ATLAS):
            i = r * GRID + x // CELL
            row.extend(rgb(SWATCHES[i][1]) if i < len(SWATCHES) else fill)
        rows.append(row)
    _png(path, ATLAS, ATLAS, rows)


def table():
    out = []
    for name, h, fam in SWATCHES:
        hh, s, v = hsv(h)
        c, r = cell(name)
        out.append({'name': name, 'hex': '#%06x' % h, 'family': fam, 'cell': [c, r],
                    'h': round(hh * 360), 's': round(s, 2), 'v': round(v, 2)})
    return {'atlas': ATLAS, 'cell': CELL, 'margin': MARGIN, 'glass': '#%06x' % GLASS, 'swatches': out}


def check():
    """Our style limits (style guide section 2): every swatch inside the measured Synty ranges, with a little room."""
    bad = []
    for name, h, fam in SWATCHES:
        _, s, v = hsv(h)
        if s > 0.62 or v > 0.76:
            bad.append('%s s=%.2f v=%.2f' % (name, s, v))
    return bad


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else sys.argv[1:]
    args = dict(zip(argv[::2], argv[1::2]))
    problems = check()
    if problems:
        raise SystemExit('palette outside the style range: ' + ', '.join(problems))
    if '--png' in args:
        write_png(args['--png'])
        print('palette: wrote %s (%d swatches)' % (args['--png'], len(SWATCHES)))
    if '--json' in args:
        with open(args['--json'], 'w', newline='\n') as f:
            json.dump(table(), f, indent=1)
            f.write('\n')
        print('palette: wrote %s' % args['--json'])
