# Backroads building builder: stylised houses, shops, warehouses and a barn.
#
#   blender -b --factory-startup --python tools/blender/buildings.py -- --out assets/buildings [--previews dir]
#
# Writes buildings.glb with one empty per model (House_A, House_B, House_C, Shop_A, Shop_B,
# Warehouse_A, Warehouse_B, Warehouse_C, Barn, Garage, Shed). Under each: a "Body" mesh whose faces use named
# materials (Wall, Roof, Door, Awning, Accent and Cladding are recoloured per building in the game;
# Trim, Frame, Stone, Wood, Concrete, Dark, Metal, Sign and Bulb keep their own colour) and one
# "Glass" object per window pane so the game can light windows one by one at night.
#
# Blender is Z-up and the fronts face -Y; the glTF exporter turns that into the game's Y-up with
# the front facing +Z. Every model sits centred on the origin with its floor at z = 0.

import bpy
import bmesh
import math
import os
import sys
from mathutils import Matrix, Vector


def arg(name, default=None):
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    if name in argv:
        i = argv.index(name)
        return argv[i + 1] if i + 1 < len(argv) else True
    return default


OUT = arg('--out', 'assets/buildings')
PREVIEWS = arg('--previews', None)

MATS = {
    'Wall': 0xe9dfc8, 'Roof': 0x7b3a2e, 'Door': 0x5a3b2a, 'Awning': 0xc0392b, 'Accent': 0x2d6cdf,
    'Cladding': 0x9aa3a8, 'Trim': 0xf2efe6, 'Frame': 0x3a3d40, 'Stone': 0x8c8579, 'Wood': 0x8b6a4a,
    'Concrete': 0xa8a49c, 'Dark': 0x2b2d30, 'Metal': 0x8d969b, 'Sign': 0xffffff, 'Bulb': 0xffffff,
    'Glass': 0x1d2a36,
}
ORDER = list(MATS)
_MAT = {}


def lin(c):
    c = c / 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def material(name):
    if name not in _MAT:
        m = bpy.data.materials.new(name)
        m.use_nodes = True
        h = MATS[name]
        bsdf = m.node_tree.nodes['Principled BSDF']
        bsdf.inputs['Base Color'].default_value = (lin(h >> 16 & 255), lin(h >> 8 & 255), lin(h & 255), 1)
        bsdf.inputs['Roughness'].default_value = 0.2 if name == 'Glass' else 0.8
        if name in ('Metal', 'Cladding'):
            bsdf.inputs['Metallic'].default_value = 0.5
        _MAT[name] = m
    return _MAT[name]


class Model:
    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.glass = []

    def _tag(self, verts, mat):
        idx = ORDER.index(mat)
        faces = set()
        for v in verts:
            faces.update(v.link_faces)
        for f in faces:
            f.material_index = idx

    def box(self, mat, size, pos, rot=(0, 0, 0)):
        """Axis box: size (x, y, z) centred at pos, rotated (rx, ry, rz) radians about its centre."""
        sx, sy, sz = size
        m = Matrix.Translation(pos) @ Matrix.Rotation(rot[2], 4, 'Z') @ Matrix.Rotation(rot[1], 4, 'Y') @ Matrix.Rotation(rot[0], 4, 'X') @ Matrix.Diagonal((sx, sy, sz, 1))
        r = bmesh.ops.create_cube(self.bm, size=1, matrix=m)
        self._tag(r['verts'], mat)

    def cyl(self, mat, r, h, pos, seg=12, axis='Z', r2=None):
        rot = {'Z': Matrix(), 'X': Matrix.Rotation(math.pi / 2, 4, 'Y'), 'Y': Matrix.Rotation(math.pi / 2, 4, 'X')}[axis]
        m = Matrix.Translation(pos) @ rot
        res = bmesh.ops.create_cone(self.bm, cap_ends=True, segments=seg, radius1=r, radius2=r if r2 is None else r2, depth=h, matrix=m)
        self._tag(res['verts'], mat)

    def poly(self, mat, pts):
        vs = [self.bm.verts.new(p) for p in pts]
        f = self.bm.faces.new(vs)
        f.material_index = ORDER.index(mat)
        return f

    def prism(self, mat, pts2d, y0, y1):
        """Extrude a closed XZ outline (list of (x, z)) between y0 and y1."""
        a = [self.bm.verts.new((x, y0, z)) for x, z in pts2d]
        b = [self.bm.verts.new((x, y1, z)) for x, z in pts2d]
        idx = ORDER.index(mat)
        for f in (self.bm.faces.new(list(reversed(a))), self.bm.faces.new(b)):
            f.material_index = idx
        n = len(pts2d)
        for i in range(n):
            j = (i + 1) % n
            self.bm.faces.new((a[i], a[j], b[j], b[i])).material_index = idx

    def pane(self, w, h, pos, face):
        """A window pane on a wall facing 'face' (one of -y, +y, -x, +x)."""
        t = 0.06
        size = (w, t, h) if face in ('-y', '+y') else (t, w, h)
        bm = bmesh.new()
        bmesh.ops.create_cube(bm, size=1, matrix=Matrix.Translation(pos) @ Matrix.Diagonal((*size, 1)))
        me = bpy.data.meshes.new('Glass')
        bm.to_mesh(me)
        bm.free()
        me.materials.append(material('Glass'))
        ob = bpy.data.objects.new('Glass', me)
        self.glass.append(ob)

    def window(self, w, h, centre, face, frame='Trim', shutters=None, sill=True, mullion=True):
        """Framed window with optional shutters, centred on the wall surface."""
        x, y, z = centre
        out = {'-y': (0, -1), '+y': (0, 1), '-x': (-1, 0), '+x': (1, 0)}[face]
        along = (1, 0) if face in ('-y', '+y') else (0, 1)
        def at(a, o, zz):  # a along wall, o outward
            return (x + along[0] * a + out[0] * o, y + along[1] * a + out[1] * o, zz)
        def sz(a, o, zz):
            return (abs(along[0]) * a + abs(out[0]) * o, abs(along[1]) * a + abs(out[1]) * o, zz)
        self.pane(w, h, at(0, 0.02, z), face)
        fw = 0.1
        self.box(frame, sz(w + 2 * fw, 0.1, fw), at(0, 0.05, z + h / 2 + fw / 2))
        self.box(frame, sz(fw, 0.1, h), at(-w / 2 - fw / 2, 0.05, z))
        self.box(frame, sz(fw, 0.1, h), at(w / 2 + fw / 2, 0.05, z))
        if sill:
            self.box(frame, sz(w + 0.35, 0.2, 0.08), at(0, 0.1, z - h / 2 - 0.04))
        else:
            self.box(frame, sz(w + 2 * fw, 0.1, fw), at(0, 0.05, z - h / 2 - fw / 2))
        if mullion:
            self.box(frame, sz(0.05, 0.08, h), at(0, 0.06, z))
            self.box(frame, sz(w, 0.08, 0.05), at(0, 0.06, z + h * 0.1))
        if shutters:
            for s in (-1, 1):
                self.box(shutters, sz(w * 0.45, 0.06, h + 0.1), at(s * (w / 2 + fw + w * 0.24), 0.05, z))

    def finish(self):
        me = bpy.data.meshes.new(self.name + '_Body')
        bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
        self.bm.to_mesh(me)
        self.bm.free()
        for n in ORDER:
            me.materials.append(material(n))
        for p in me.polygons:
            p.use_smooth = False
        root = bpy.data.objects.new(self.name, None)
        bpy.context.scene.collection.objects.link(root)
        body = bpy.data.objects.new(self.name + '_Body', me)
        bpy.context.scene.collection.objects.link(body)
        body.parent = root
        for g in self.glass:
            bpy.context.scene.collection.objects.link(g)
            g.parent = root
        return root


# ---------------------------------------------------------------- shared pieces

def gable_roof(m, w, d, h, rise, over=0.45, ridge_along='x', thick=0.16, mat='Roof', fascia='Trim', ends='Wall'):
    """Two sloped slabs, fascia boards and gable-end triangles. w along x, d along y, walls end at h."""
    if ridge_along == 'x':
        half, run = d / 2, d / 2 + over
        ang = math.atan2(rise, d / 2)
        slope = math.hypot(run, rise * run / half)
        for s in (-1, 1):
            cy = s * run / 2
            cz = h + rise - rise * (run / 2) / half + thick / 2
            m.box(mat, (w + 2 * over, slope, thick), (0, cy, cz), (s * -ang, 0, 0))
            m.box(fascia, (w + 2 * over + 0.05, 0.08, 0.22), (0, s * (run - 0.02), h + rise - rise * run / half + 0.02))
        m.box(fascia, (w + 2 * over, 0.22, 0.12), (0, 0, h + rise + thick + 0.02))
        for s in (-1, 1):
            m.poly(ends, [(s * w / 2, -half, h), (s * w / 2, half, h), (s * w / 2, 0, h + rise)][::s])
    else:
        half, run = w / 2, w / 2 + over
        ang = math.atan2(rise, w / 2)
        slope = math.hypot(run, rise * run / half)
        for s in (-1, 1):
            cx = s * run / 2
            cz = h + rise - rise * (run / 2) / half + thick / 2
            m.box(mat, (slope, d + 2 * over, thick), (cx, 0, cz), (0, s * ang, 0))
            m.box(fascia, (0.08, d + 2 * over + 0.05, 0.22), (s * (run - 0.02), 0, h + rise - rise * run / half + 0.02))
        m.box(fascia, (0.22, d + 2 * over, 0.12), (0, 0, h + rise + thick + 0.02))
        for s in (-1, 1):
            m.poly(ends, [(-half, s * d / 2, h), (half, s * d / 2, h), (0, s * d / 2, h + rise)][::-s])


def hip_roof(m, w, d, h, rise, over=0.45, mat='Roof', fascia='Trim'):
    hw, hd = w / 2 + over, d / 2 + over
    ridge = max(0.0, hw - hd)
    z0 = h - over * rise / (d / 2)
    top = h + rise
    a, b, c, e = (-hw, -hd, z0), (hw, -hd, z0), (hw, hd, z0), (-hw, hd, z0)
    r0, r1 = (-ridge, 0, top), (ridge, 0, top)
    m.poly(mat, [a, b, r1, r0])
    m.poly(mat, [c, e, r0, r1])
    m.poly(mat, [b, c, r1])
    m.poly(mat, [e, a, r0])
    m.poly(mat, [a, e, c, b])  # soffit
    for (p, q) in ((a, b), (b, c), (c, e), (e, a)):
        mx, my = (p[0] + q[0]) / 2, (p[1] + q[1]) / 2
        lx, ly = abs(q[0] - p[0]), abs(q[1] - p[1])
        m.box(fascia, (max(lx, 0.1) + 0.05, max(ly, 0.1) + 0.05, 0.2), (mx, my, z0 - 0.08))


def walls(m, w, d, h, mat='Wall', base='Stone', base_h=0.45, corners='Trim'):
    m.box(mat, (w, d, h), (0, 0, h / 2))
    m.box(base, (w + 0.16, d + 0.16, base_h), (0, 0, base_h / 2))
    if corners:
        for sx in (-1, 1):
            for sy in (-1, 1):
                m.box(corners, (0.16, 0.16, h - base_h), (sx * w / 2, sy * d / 2, base_h + (h - base_h) / 2))


def door(m, w, h, x, y_face, z0=0.45, mat='Door', frame='Trim', glass=True):
    m.box(mat, (w, 0.1, h), (x, y_face - 0.03, z0 + h / 2))
    m.box(frame, (w + 0.24, 0.12, 0.12), (x, y_face - 0.05, z0 + h + 0.06))
    for s in (-1, 1):
        m.box(frame, (0.12, 0.12, h), (x + s * (w / 2 + 0.06), y_face - 0.05, z0 + h / 2))
    m.box('Metal', (0.06, 0.08, 0.06), (x + w * 0.35, y_face - 0.1, z0 + h * 0.47))
    if glass:
        m.pane(w * 0.45, h * 0.22, (x, y_face - 0.09, z0 + h * 0.78), '-y')


def steps(m, w, n, x, y_face, top_z, depth=0.32, mat='Concrete'):
    rise = top_z / n
    for i in range(n):
        m.box(mat, (w, depth * (n - i), rise), (x, y_face - depth * (n - i) / 2, rise * (i + 0.5)))


def roller_door(m, w, h, x, y_face, frame='Frame', bulb=True):
    m.box('Door', (w, 0.12, h), (x, y_face - 0.04, h / 2))
    for k in range(1, int(h / 0.3)):
        m.box('Dark', (w - 0.05, 0.14, 0.03), (x, y_face - 0.05, k * 0.3))
    m.box(frame, (w + 0.4, 0.2, 0.25), (x, y_face - 0.08, h + 0.12))
    for s in (-1, 1):
        m.box(frame, (0.2, 0.2, h), (x + s * (w / 2 + 0.1), y_face - 0.08, h / 2))
        m.box('Accent', (0.22, 0.22, 1.0), (x + s * (w / 2 + 0.55), y_face - 0.5, 0.5))   # bollards
    if bulb:
        m.box('Metal', (0.5, 0.3, 0.12), (x, y_face - 0.15, h + 0.6))
        m.box('Bulb', (0.4, 0.06, 0.08), (x, y_face - 0.25, h + 0.48))


def ribs(m, length, h, pos, face, gap=0.5, mat='Cladding', z0=0.0):
    """Vertical corrugation ribs along a wall."""
    x0, y0 = pos
    n = int(length / gap)
    for i in range(n + 1):
        a = -length / 2 + i * (length / n)
        if face in ('-y', '+y'):
            o = -0.04 if face == '-y' else 0.04
            m.box(mat, (0.08, 0.08, h), (x0 + a, y0 + o, z0 + h / 2))
        else:
            o = -0.04 if face == '-x' else 0.04
            m.box(mat, (0.08, 0.08, h), (x0 + o, y0 + a, z0 + h / 2))


# ---------------------------------------------------------------- models

def house_a():
    """Cottage: one storey, gable roof along the front, deep porch, chimney, shutters."""
    m = Model('House_A')
    w, d, h = 7.6, 8.4, 3.4
    walls(m, w, d, h)
    gable_roof(m, w, d, h, 2.4, ridge_along='x')
    # porch: deck, posts, small roof
    m.box('Wood', (w - 1.2, 1.9, 0.2), (0, -d / 2 - 0.95, 0.35))
    for x in (-w / 2 + 0.85, -1.1, 1.1, w / 2 - 0.85):
        m.box('Trim', (0.16, 0.16, 2.55), (x, -d / 2 - 1.75, 1.72))
    m.box('Roof', (w - 0.8, 2.3, 0.14), (0, -d / 2 - 1.05, 3.1), (0.22, 0, 0))
    m.box('Trim', (w - 0.8, 0.08, 0.2), (0, -d / 2 - 2.2, 2.85))
    for x in (-w / 2 + 0.85, w / 2 - 0.85):        # railings
        m.box('Trim', (0.06, 1.7, 0.08), (x, -d / 2 - 0.95, 1.2))
    steps(m, 1.6, 2, 0, -d / 2 - 1.9, 0.45)
    door(m, 1.0, 2.15, 0, -d / 2)
    for x in (-2.35, 2.35):
        m.window(1.1, 1.3, (x, -d / 2, 1.85), '-y', shutters='Door')
    for y in (-1.8, 1.8):
        m.window(1.0, 1.2, (w / 2, y, 1.85), '+x')
        m.window(1.0, 1.2, (-w / 2, y, 1.85), '-x')
    for x in (-1.8, 1.8):
        m.window(1.0, 1.2, (x, d / 2, 1.85), '+y')
    m.box('Stone', (0.9, 0.7, 2.0), (w * 0.27, 1.4, h + 2.2))
    m.box('Dark', (0.95, 0.75, 0.15), (w * 0.27, 1.4, h + 3.25))
    m.box('Bulb', (0.16, 0.12, 0.22), (0.85, -d / 2 - 0.08, 2.5))
    return m.finish()


def house_b():
    """Two storeys, gable end to the street, door canopy on brackets, attic window."""
    m = Model('House_B')
    w, d, h = 7.8, 9.0, 6.3
    walls(m, w, d, h)
    m.box('Trim', (w + 0.1, d + 0.1, 0.14), (0, 0, 3.25))        # floor band
    gable_roof(m, w, d, h, 3.0, ridge_along='y')
    door(m, 1.0, 2.2, -1.9, -d / 2)
    steps(m, 1.5, 2, -1.9, -d / 2, 0.45)
    m.box('Roof', (1.7, 1.1, 0.12), (-1.9, -d / 2 - 0.5, 3.0), (0.25, 0, 0))
    for s in (-1, 1):
        m.box('Trim', (0.1, 0.8, 0.1), (-1.9 + s * 0.7, -d / 2 - 0.4, 2.75), (0.6, 0, 0))
    m.window(1.6, 1.4, (1.3, -d / 2, 1.9), '-y', shutters=None)
    for x in (-2.0, 0.0, 2.0):
        m.window(1.0, 1.3, (x, -d / 2, 4.75), '-y', shutters='Door')
    m.window(0.8, 0.8, (0, -d / 2, 7.1), '-y', sill=False)
    for z in (1.9, 4.75):
        for y in (-2.4, 0, 2.4):
            m.window(0.95, 1.2, (w / 2, y, z), '+x')
            m.window(0.95, 1.2, (-w / 2, y, z), '-x')
        for x in (-1.8, 1.8):
            m.window(1.0, 1.2, (x, d / 2, z), '+y')
    m.box('Stone', (0.8, 0.8, 2.6), (-w / 2 + 0.9, 2.5, h + 2.0))
    m.box('Bulb', (0.16, 0.12, 0.22), (-1.05, -d / 2 - 0.08, 2.55))
    return m.finish()


def house_c():
    """Wide bungalow with a hip roof and an attached garage."""
    m = Model('House_C')
    w, d, h = 10.4, 8.0, 3.2
    walls(m, w, d, h)
    hip_roof(m, w, d, h, 2.0)
    door(m, 1.0, 2.15, -0.4, -d / 2)
    steps(m, 1.4, 2, -0.4, -d / 2, 0.45)
    m.box('Door', (2.8, 0.1, 2.3), (3.3, -d / 2 - 0.03, 1.15 + 0.1))       # garage door
    for k in range(1, 6):
        m.box('Dark', (2.75, 0.12, 0.03), (3.3, -d / 2 - 0.05, 0.1 + k * 0.4))
    m.box('Trim', (3.1, 0.12, 0.14), (3.3, -d / 2 - 0.05, 2.5))
    m.box('Concrete', (3.2, 3.0, 0.08), (3.3, -d / 2 - 1.5, 0.04))
    m.window(2.0, 1.2, (-3.2, -d / 2, 1.9), '-y', shutters='Door')
    for y in (-1.6, 1.6):
        m.window(1.0, 1.1, (w / 2, y, 1.9), '+x')
        m.window(1.0, 1.1, (-w / 2, y, 1.9), '-x')
    for x in (-3.4, -0.4, 2.6):
        m.window(1.0, 1.1, (x, d / 2, 1.9), '+y')
    m.box('Bulb', (0.16, 0.12, 0.22), (0.45, -d / 2 - 0.08, 2.5))
    return m.finish()


def shop_a():
    """Single-storey shop: big storefront, awning, sign board, parapet with cornice."""
    m = Model('Shop_A')
    w, d, h = 11.0, 9.0, 4.8
    m.box('Wall', (w, d, h), (0, 0, h / 2))
    m.box('Trim', (w + 0.3, d + 0.3, 0.3), (0, 0, h + 0.15))
    m.box('Wall', (w + 0.1, d + 0.1, 0.7), (0, 0, h + 0.65))
    m.box('Trim', (w + 0.45, d + 0.45, 0.18), (0, 0, h + 1.05))
    m.box('Dark', (w - 0.2, d - 0.2, 0.1), (0, 0, h + 0.36))           # roof membrane inside the parapet
    m.box('Dark', (w * 0.78, 0.12, 0.5), (-0.6, -d / 2 - 0.02, 0.25))           # kickplate
    sw = w * 0.78
    for i in range(4):
        x = -0.6 - sw / 2 + sw / 8 + i * sw / 4
        m.pane(sw / 4 - 0.12, 2.3, (x, -d / 2 - 0.02, 1.65), '-y')
    for i in range(5):
        x = -0.6 - sw / 2 + i * sw / 4
        m.box('Frame', (0.1, 0.14, 2.4), (x, -d / 2 - 0.05, 1.65))
    m.box('Frame', (sw + 0.1, 0.14, 0.12), (-0.6, -d / 2 - 0.05, 2.86))
    m.box('Sign', (w * 0.62, 0.15, 0.85), (-0.6, -d / 2 - 0.08, 3.85))
    m.box('Frame', (w * 0.62 + 0.15, 0.12, 0.08), (-0.6, -d / 2 - 0.1, 4.31))
    # awning: slanted canvas with a valance
    m.box('Awning', (sw + 0.4, 1.7, 0.06), (-0.6, -d / 2 - 0.8, 3.12), (-0.33, 0, 0))
    m.box('Awning', (sw + 0.4, 0.04, 0.3), (-0.6, -d / 2 - 1.62, 2.7))
    door(m, 1.0, 2.2, w / 2 - 0.9, -d / 2, z0=0.0)
    for y in (-2.2, 2.2):
        m.window(1.2, 1.2, (w / 2, y, 2.1), '+x', frame='Frame', sill=False)
    for x in (-3, 0, 3):
        m.window(1.1, 1.0, (x, d / 2, 2.3), '+y', frame='Frame', sill=False)
    m.box('Metal', (1.6, 1.2, 0.9), (2.5, 1.5, h + 0.45))
    m.box('Bulb', (0.5, 0.1, 0.1), (-0.6, -d / 2 - 0.2, 4.45))
    return m.finish()


def shop_b():
    """Two storeys: shop on the ground floor, a flat above, cornice and sign."""
    m = Model('Shop_B')
    w, d, h = 10.0, 9.0, 7.2
    m.box('Wall', (w, d, h), (0, 0, h / 2))
    m.box('Trim', (w + 0.2, d + 0.2, 0.25), (0, 0, 3.9))
    m.box('Trim', (w + 0.4, d + 0.4, 0.3), (0, 0, h + 0.15))
    m.box('Wall', (w + 0.1, d + 0.1, 0.5), (0, 0, h + 0.55))
    m.box('Trim', (w + 0.5, d + 0.5, 0.15), (0, 0, h + 0.85))
    m.box('Dark', (w - 0.2, d - 0.2, 0.1), (0, 0, h + 0.32))
    m.box('Metal', (1.4, 1.0, 0.8), (-2.5, 2.0, h + 0.7))
    sw = 6.4
    for i in range(3):
        m.pane(sw / 3 - 0.12, 2.3, (-1.2 - sw / 2 + sw / 6 + i * sw / 3, -d / 2 - 0.02, 1.6), '-y')
    for i in range(4):
        m.box('Frame', (0.1, 0.14, 2.4), (-1.2 - sw / 2 + i * sw / 3, -d / 2 - 0.05, 1.6))
    m.box('Dark', (sw, 0.12, 0.42), (-1.2, -d / 2 - 0.02, 0.21))
    m.box('Sign', (w * 0.7, 0.14, 0.75), (-0.5, -d / 2 - 0.08, 3.3))
    m.box('Awning', (sw + 0.4, 1.4, 0.06), (-1.2, -d / 2 - 0.66, 2.95), (-0.35, 0, 0))
    door(m, 1.0, 2.2, 3.4, -d / 2, z0=0.0)
    for x in (-3.2, -0.6, 2.0):
        m.window(1.0, 1.4, (x, -d / 2, 5.4), '-y')
    for y in (-2.5, 0, 2.5):
        m.window(0.95, 1.3, (w / 2, y, 5.4), '+x')
        m.window(0.95, 1.3, (-w / 2, y, 5.4), '-x')
    for x in (-2.5, 2.5):
        m.window(1.0, 1.3, (x, d / 2, 5.4), '+y')
        m.window(1.0, 1.0, (x, d / 2, 2.2), '+y', frame='Frame', sill=False)
    m.box('Bulb', (0.5, 0.1, 0.1), (-0.5, -d / 2 - 0.2, 3.8))
    return m.finish()


def warehouse_a():
    """Big portal-frame warehouse: corrugated cladding, three roller doors, loading dock, office annex."""
    m = Model('Warehouse_A')
    w, d, h = 22.0, 30.0, 8.0
    m.box('Cladding', (w, d, h), (0, 0, h / 2))
    m.box('Concrete', (w + 0.2, d + 0.2, 0.6), (0, 0, 0.3))
    m.box('Accent', (w + 0.06, d + 0.06, 0.7), (0, 0, h - 1.0))
    ribs(m, w, h - 1.4, (0, -d / 2), '-y', 0.55, z0=0.6)
    ribs(m, w, h - 1.4, (0, d / 2), '+y', 0.55, z0=0.6)
    ribs(m, d, h - 1.4, (-w / 2, 0), '-x', 0.6, z0=0.6)
    gable_roof(m, w, d, h, 1.6, over=0.5, ridge_along='y', thick=0.2, mat='Roof', fascia='Frame', ends='Cladding')
    for k in range(3):
        x = -7 + k * 7
        roller_door(m, 4.4, 4.8, x, -d / 2)
    # loading dock + canopy
    m.box('Concrete', (w - 2, 2.6, 1.2), (0, -d / 2 - 1.3, 0.6))
    for k in range(3):
        for s in (-1, 1):
            m.box('Dark', (0.25, 0.3, 0.5), (-7 + k * 7 + s * 1.6, -d / 2 - 2.65, 0.85))
    m.box('Roof', (w - 1, 3.4, 0.16), (0, -d / 2 - 1.7, 5.9), (0.08, 0, 0))
    for x in (-w / 2 + 1, -3.5, 3.5, w / 2 - 1):
        m.box('Frame', (0.14, 0.14, 1.4), (x, -d / 2 - 2.9, 5.2), (0.7, 0, 0))
    # office annex on the +x side
    ox = w / 2 + 2.5
    m.box('Wall', (5, 9, 3.6), (ox, -d / 2 + 6, 1.8))
    m.box('Frame', (5.3, 9.3, 0.3), (ox, -d / 2 + 6, 3.75))
    door(m, 1.0, 2.2, ox - 1, -d / 2 + 1.5, z0=0.0, frame='Frame')
    for y in (-d / 2 + 3.5, -d / 2 + 6, -d / 2 + 8.5):
        m.window(1.6, 1.1, (ox + 2.5, y, 2.0), '+x', frame='Frame', sill=False)
    m.window(1.6, 1.1, (ox + 0.9, -d / 2 + 1.5, 2.0), '-y', frame='Frame', sill=False)
    # side door, high windows, downpipes, roof skylights
    door(m, 1.0, 2.2, -w / 2 + 0.02, 5, z0=0.0, frame='Frame', glass=False)
    for y in range(-12, 13, 6):
        m.pane(2.4, 0.7, (w / 2 + 0.02, y, h - 2.0), '+x')
        m.box('Frame', (0.1, 2.6, 0.9), (w / 2 + 0.01, y, h - 2.0))
        m.pane(2.4, 0.7, (-w / 2 - 0.02, y, h - 2.0), '-x')
    for (x, y) in ((-w / 2 - 0.1, -d / 2 + 0.3), (w / 2 + 0.1, d / 2 - 0.3), (-w / 2 - 0.1, d / 2 - 0.3)):
        m.box('Metal', (0.16, 0.16, h), (x, y, h / 2))
    for y in range(-10, 11, 5):
        for s in (-1, 1):
            m.box('Frame', (2.0, 1.2, 0.12), (s * 5.5, y, h + 1.6 * (1 - 5.5 / 11) + 0.25), (0, s * math.atan2(1.6, 11), 0))
    return m.finish()


def warehouse_b():
    """Arched storage shed (Quonset style) with one roller door."""
    m = Model('Warehouse_B')
    w, d, h = 12.0, 18.0, 6.2
    m.box('Concrete', (w + 0.4, d + 0.4, 0.4), (0, 0, 0.2))
    seg = 14
    r = w / 2
    pts = [(math.cos(math.pi * i / seg) * r, 0.4 + math.sin(math.pi * i / seg) * (h - 0.4)) for i in range(seg + 1)]
    for i in range(seg):
        (x0, z0), (x1, z1) = pts[i], pts[i + 1]
        cx, cz = (x0 + x1) / 2, (z0 + z1) / 2
        L = math.hypot(x1 - x0, z1 - z0)
        ang = math.atan2(z1 - z0, x1 - x0)
        m.box('Cladding', (L + 0.05, d, 0.12), (cx, 0, cz), (0, -ang, 0))
        for k in range(-8, 9, 2):
            m.box('Accent' if k in (-8, 8) else 'Cladding', (L + 0.06, 0.12, 0.16), (cx, k, cz), (0, -ang, 0))
    for s in (-1, 1):
        outline = [(x, z) for x, z in pts]
        m.prism('Wall', outline, s * d / 2 - 0.1, s * d / 2 + 0.1)
    roller_door(m, 4.2, 4.0, -1.5, -d / 2 - 0.1)
    door(m, 0.95, 2.1, 3.6, -d / 2 - 0.1, z0=0.4, frame='Frame', glass=False)
    for x in (-3.5, 3.5):
        m.window(1.2, 0.8, (x, d / 2 + 0.1, 3.4), '+y', frame='Frame', sill=False)
    return m.finish()


def warehouse_c():
    """Long, low garage block with four roller doors and a mono-pitch roof."""
    m = Model('Warehouse_C')
    w, d, h = 26.0, 10.0, 5.6
    m.box('Cladding', (w, d, h), (0, 0, h / 2))
    m.box('Concrete', (w + 0.2, d + 0.2, 0.4), (0, 0, 0.2))
    m.box('Accent', (w + 0.06, d + 0.06, 0.55), (0, 0, h - 0.6))
    ribs(m, w, h - 1.0, (0, d / 2), '+y', 0.55, z0=0.4)
    m.box('Roof', (w + 0.8, d + 1.2, 0.2), (0, -0.2, h + 0.45), (math.atan2(0.9, d), 0, 0))
    m.box('Frame', (w + 0.85, 0.12, 0.3), (0, -d / 2 - 0.8, h + 0.9))
    for k in range(4):
        roller_door(m, 4.2, 4.0, -9.75 + k * 6.5, -d / 2)
    for x in (-w / 2 - 0.1, w / 2 + 0.1):
        m.box('Metal', (0.16, 0.16, h), (x, -d / 2 + 0.3, h / 2))
    return m.finish()


def barn():
    """Red barn: gambrel roof, white trim, X-braced doors, hay loft door."""
    m = Model('Barn')
    w, d, h = 12.0, 18.0, 5.4
    m.box('Wall', (w, d, h), (0, 0, h / 2))
    m.box('Stone', (w + 0.2, d + 0.2, 0.5), (0, 0, 0.25))
    for sx in (-1, 1):
        for sy in (-1, 1):
            m.box('Trim', (0.25, 0.25, h), (sx * w / 2, sy * d / 2, h / 2))
    # gambrel: steep lower slopes, shallow upper ones
    lx, lz = w / 2 - 1.6, h + 2.6
    top = lz + 1.8
    prof = [(-w / 2 - 0.4, h - 0.2), (-lx, lz), (0, top), (lx, lz), (w / 2 + 0.4, h - 0.2)]
    for (x0, z0), (x1, z1) in zip(prof, prof[1:]):
        L = math.hypot(x1 - x0, z1 - z0)
        ang = math.atan2(z1 - z0, x1 - x0)
        m.box('Roof', (L, d + 0.8, 0.18), ((x0 + x1) / 2, 0, (z0 + z1) / 2 + 0.09), (0, -ang, 0))
    for s in (-1, 1):
        m.prism('Wall', [(-w / 2, h), (w / 2, h), (lx, lz), (0, top), (-lx, lz)], s * d / 2 - 0.05, s * d / 2 + 0.05)
    # big doors with white X bracing
    for s in (-1, 1):
        x = s * 1.2
        m.box('Door', (2.4, 0.12, 4.0), (x, -d / 2 - 0.06, 2.0))
        m.box('Trim', (2.5, 0.14, 0.18), (x, -d / 2 - 0.1, 4.0))
        m.box('Trim', (2.5, 0.14, 0.18), (x, -d / 2 - 0.1, 0.1))
        for t in (-1, 1):
            m.box('Trim', (0.18, 0.14, 4.5), (x, -d / 2 - 0.11, 2.0), (0, t * 0.53, 0))
        m.box('Trim', (0.18, 0.14, 4.0), (x + s * 1.2, -d / 2 - 0.1, 2.0))
    m.box('Door', (1.6, 0.12, 1.4), (0, -d / 2 - 0.06, h + 1.4))
    m.box('Trim', (1.8, 0.14, 0.14), (0, -d / 2 - 0.1, h + 2.15))
    m.box('Metal', (0.16, 1.2, 0.16), (0, -d / 2 - 0.6, top - 0.3))
    for y in (-5, 0, 5):
        m.window(0.9, 0.9, (w / 2, y, 2.6), '+x', sill=False)
        m.window(0.9, 0.9, (-w / 2, y, 2.6), '-x', sill=False)
    m.box('Bulb', (0.4, 0.15, 0.2), (0, -d / 2 - 0.2, 4.6))
    return m.finish()


def garage():
    """Detached single garage with a gable roof and an up-and-over door."""
    m = Model('Garage')
    w, d, h = 4.2, 6.4, 2.7
    walls(m, w, d, h, base_h=0.2)
    gable_roof(m, w, d, h, 1.3, over=0.3, ridge_along='y')
    m.box('Door', (2.6, 0.1, 2.15), (0, -d / 2 - 0.03, 0.2 + 1.075))
    for k in range(1, 5):
        m.box('Dark', (2.55, 0.12, 0.03), (0, -d / 2 - 0.05, 0.2 + k * 0.43))
    m.box('Trim', (2.85, 0.12, 0.12), (0, -d / 2 - 0.05, 2.42))
    m.window(0.7, 0.6, (w / 2, 1.2, 1.6), '+x', sill=False, mullion=False)
    return m.finish()


def shed():
    """Small garden shed."""
    m = Model('Shed')
    w, d, h = 2.6, 3.2, 2.1
    m.box('Wood', (w, d, h), (0, 0, h / 2))
    for x in (-0.6, 0, 0.6):
        m.box('Trim', (0.06, 0.04, h), (x * w / 1.6, -d / 2 - 0.02, h / 2))
    gable_roof(m, w, d, h, 0.8, over=0.2, ridge_along='y', fascia='Trim', ends='Wood')
    m.box('Door', (0.9, 0.08, 1.8), (0, -d / 2 - 0.04, 0.9))
    return m.finish()


BUILDERS = [house_a, house_b, house_c, shop_a, shop_b, warehouse_a, warehouse_b, warehouse_c, barn, garage, shed]


def export(roots):
    os.makedirs(OUT, exist_ok=True)
    bpy.ops.object.select_all(action='DESELECT')
    for r in roots:
        r.select_set(True)
        for c in r.children:
            c.select_set(True)
    path = os.path.join(OUT, 'buildings.glb')
    bpy.ops.export_scene.gltf(filepath=path, export_format='GLB', use_selection=True, export_yup=True,
                              export_apply=False, export_cameras=False, export_lights=False)
    for r in roots:
        tris = sum(sum(len(p.vertices) - 2 for p in c.data.polygons) for c in r.children)
        print(f'[buildings] {r.name}: {tris} tris, {len(r.children) - 1} panes')
    print(f'[buildings] wrote {path} ({os.path.getsize(path) / 1024:.0f} KB)')


def preview(roots):
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 24
    scene.cycles.device = 'CPU'
    scene.render.resolution_x, scene.render.resolution_y = 1800, 700
    try:
        scene.view_settings.view_transform = 'AgX'
    except Exception:
        pass
    world = bpy.data.worlds.new('w')
    scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes['Background']
    bg.inputs['Color'].default_value = (0.6, 0.7, 0.85, 1)
    bg.inputs['Strength'].default_value = 0.8
    sun = bpy.data.lights.new('sun', 'SUN')
    sun.energy = 3.4
    so = bpy.data.objects.new('sun', sun)
    so.rotation_euler = (math.radians(50), 0, math.radians(-30))
    scene.collection.objects.link(so)
    # 3 x 3 grid, fronts towards the camera
    for i, r in enumerate(roots):
        col, row = i % 4, i // 4
        r.location = (col * 40, row * 40, 0)
    gm = bpy.data.meshes.new('ground')
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=120)
    bm.to_mesh(gm)
    bm.free()
    g = bpy.data.objects.new('ground', gm)
    g.location = (40, 40, 0)
    gmat = bpy.data.materials.new('groundmat')
    gmat.use_nodes = True
    gmat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (0.25, 0.32, 0.16, 1)
    gm.materials.append(gmat)
    scene.collection.objects.link(g)
    scene.render.resolution_x, scene.render.resolution_y = 1600, 1100
    cam = bpy.data.cameras.new('cam')
    cam.lens = 38
    co = bpy.data.objects.new('cam', cam)
    co.location = (-12, -78, 62)
    co.rotation_euler = (math.radians(55), 0, math.radians(-18))
    scene.collection.objects.link(co)
    scene.camera = co
    os.makedirs(PREVIEWS, exist_ok=True)
    scene.render.filepath = os.path.join(os.path.abspath(PREVIEWS), 'buildings.png')
    bpy.ops.render.render(write_still=True)
    for r in roots:
        r.location = (0, 0, 0)


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    roots = [fn() for fn in BUILDERS]
    export(roots)
    if PREVIEWS:
        preview(roots)


main()
