# Backroads tree builder: stylised low-poly trees with painted vertex colours, two LODs each.
#
#   blender -b --factory-startup --python tools/blender/trees.py -- --out assets/trees [--previews dir]
#
# Writes one trees.glb holding an object per species (Pine, Spruce, Oak, Birch, Poplar, Maple,
# Bush, Snag, Reeds) plus a *_LOD1 copy for distance. Blender is Z-up; the glTF exporter turns that
# into the game's Y-up. Colours are sRGB byte colours (COLOR_0); the game uses its own material.
#
# Canopies are clusters of spheres fused with a voxel remesh, roughened with a noise displace
# and decimated to a budget, so they read as soft, lumpy, hand-made shapes. Shading (darker
# underneath and inside, lighter sunlit tops) is painted straight into the vertex colours.

import bpy
import bmesh
import math
import os
import random
import sys
from mathutils import Matrix, Vector


def arg(name, default=None):
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    if name in argv:
        i = argv.index(name)
        return argv[i + 1] if i + 1 < len(argv) else True
    return default


OUT = arg('--out', 'assets/trees')
PREVIEWS = arg('--previews', None)


def srgb(h):
    return ((h >> 16) & 255) / 255, ((h >> 8) & 255) / 255, (h & 255) / 255


def mix(a, b, t):
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def clamp(v, a=0.0, b=1.0):
    return a if v < a else b if v > b else v


def hash3(v):
    h = math.sin(v.x * 12.9898 + v.y * 78.233 + v.z * 37.719) * 43758.5453
    return h - math.floor(h)


# ---------------------------------------------------------------- mesh plumbing

def obj_from_bm(name, bm):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def bake(ob):
    """Apply all modifiers by evaluating the object (no operator context needed)."""
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(ob.evaluated_get(dg))
    ob.modifiers.clear()
    old = ob.data
    ob.data = me
    bpy.data.meshes.remove(old)
    return ob


def remove(ob):
    me = ob.data
    bpy.data.objects.remove(ob)
    bpy.data.meshes.remove(me)


_TEX = {}


def noise_tex(scale):
    key = round(scale, 3)
    if key not in _TEX:
        t = bpy.data.textures.new(f'clouds{key}', 'CLOUDS')
        t.noise_scale = scale
        t.noise_depth = 1
        _TEX[key] = t
    return _TEX[key]


class Tree:
    """Collects parts into one bmesh and remembers what each face range is, for painting."""

    def __init__(self, seed):
        self.bm = bmesh.new()
        self.parts = []
        self.rng = random.Random(seed)

    def add_mesh(self, me, kind, smooth=True, **params):
        self.bm.faces.ensure_lookup_table()
        f0 = len(self.bm.faces)
        self.bm.from_mesh(me)
        self.bm.faces.ensure_lookup_table()
        f1 = len(self.bm.faces)
        for f in self.bm.faces[f0:f1]:
            f.smooth = smooth
        self.parts.append((f0, f1, kind, params))

    def add_bm(self, bm, kind, smooth=True, **params):
        me = bpy.data.meshes.new('tmp')
        bm.to_mesh(me)
        bm.free()
        self.add_mesh(me, kind, smooth, **params)
        bpy.data.meshes.remove(me)

    # tapered limb between two points
    def limb(self, p0, p1, r0, r1, seg=7, color=0x5a4030, kind='bark', **kw):
        p0, p1 = Vector(p0), Vector(p1)
        d = p1 - p0
        L = d.length
        bm = bmesh.new()
        rot = d.normalized().to_track_quat('Z', 'Y').to_matrix().to_4x4()
        m = Matrix.Translation((p0 + p1) / 2) @ rot
        bmesh.ops.create_cone(bm, cap_ends=False, cap_tris=False, segments=seg, radius1=r0, radius2=r1, depth=L, matrix=m)
        self.add_bm(bm, kind, True, color=color, p0=p0, p1=p1, **kw)

    def trunk(self, pts, seg=7, color=0x5a4030, kind='bark', **kw):
        for a, b in zip(pts, pts[1:]):
            self.limb(a[:3], b[:3], a[3], b[3], seg, color, kind, **kw)

    # lumpy canopy from a cluster of spheres
    def canopy(self, blobs, color, faces=600, voxel=0.32, lumpy=0.35, squash=1.0, tip=None, flat=False):
        bm = bmesh.new()
        for (x, y, z, r) in blobs:
            m = Matrix.Translation((x, y, z)) @ Matrix.Diagonal((1, 1, squash, 1))
            bmesh.ops.create_icosphere(bm, subdivisions=3, radius=r, matrix=m)
        ob = obj_from_bm('canopy', bm)
        bm.free()
        rm = ob.modifiers.new('rm', 'REMESH')
        rm.mode = 'VOXEL'
        rm.voxel_size = voxel
        rm.adaptivity = 0
        rm.use_smooth_shade = True
        ds = ob.modifiers.new('ds', 'DISPLACE')
        ds.texture = noise_tex(1.1)
        ds.strength = lumpy
        ds.mid_level = 0.5
        ds.texture_coords = 'LOCAL'
        bake(ob)
        n = len(ob.data.polygons)
        if n > faces:
            dc = ob.modifiers.new('dc', 'DECIMATE')
            dc.decimate_type = 'COLLAPSE'
            dc.ratio = faces / n
            bake(ob)
        lo = Vector((min(b[0] - b[3] for b in blobs), min(b[1] - b[3] for b in blobs), min(b[2] - b[3] * squash for b in blobs)))
        hi = Vector((max(b[0] + b[3] for b in blobs), max(b[1] + b[3] for b in blobs), max(b[2] + b[3] * squash for b in blobs)))
        self.add_mesh(ob.data, 'leaf', not flat, color=color, lo=lo, hi=hi, centre=(lo + hi) / 2, tip=tip)
        remove(ob)

    # one droopy, jagged conifer tier
    def tier(self, z, r, h, seg, color, tip=None, droop=0.35):
        bm = bmesh.new()
        bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=True, segments=seg, radius1=r, radius2=0.04, depth=h,
                              matrix=Matrix.Translation((0, 0, z + h / 2)))
        rng = self.rng
        a0 = rng.random() * 6.3
        for v in bm.verts:
            if v.co.z < z + h * 0.05:
                rad = Vector((v.co.x, v.co.y, 0)).length
                if rad > r * 0.5:
                    ang = math.atan2(v.co.y, v.co.x)
                    k = 1 + 0.16 * math.sin(ang * 3 + a0) + rng.uniform(-0.1, 0.1)
                    v.co.x *= k
                    v.co.y *= k
                    v.co.z -= droop * h * (0.6 + rng.random() * 0.6)
                else:
                    v.co.z += h * 0.12          # the underside cap tucks up into the tier
        self.add_bm(bm, 'needle', False, color=color, lo=Vector((-r, -r, z - h * 0.3)), hi=Vector((r, r, z + h)), centre=Vector((0, 0, z + h * 0.3)), tip=tip)

    # ---------------------------------------------------------------- painting

    def finish(self, name):
        bm = self.bm
        bm.faces.ensure_lookup_table()
        col = bm.loops.layers.color.new('Col')
        for (f0, f1, kind, p) in self.parts:
            base = srgb(p['color'])
            tipc = srgb(p['tip']) if p.get('tip') else None
            if kind in ('leaf', 'needle'):
                lo, hi, c = p['lo'], p['hi'], p['centre']
                for f in bm.faces[f0:f1]:
                    for lp in f.loops:
                        v = lp.vert.co
                        hgt = clamp((v.z - lo.z) / max(0.01, hi.z - lo.z))
                        out = (v - c)
                        out = out.normalized() if out.length > 1e-4 else Vector((0, 0, 1))
                        n = f.normal if kind == 'needle' else lp.vert.normal
                        facing = clamp(n.dot(out) * 0.5 + 0.5)
                        sky = clamp(n.z * 0.5 + 0.5)
                        shade = (0.55 + 0.45 * hgt) * (0.72 + 0.28 * facing) * (0.86 + 0.22 * sky)
                        cc = mix(base, tipc, hgt * hgt) if tipc else base
                        j = 0.92 + 0.16 * hash3(v * 1.7)
                        lp[col] = (*(clamp(x * shade * j) for x in cc), 1.0)
            else:   # bark
                p0, p1 = p['p0'], p['p1']
                birch = p.get('birch')
                for f in bm.faces[f0:f1]:
                    for lp in f.loops:
                        v = lp.vert.co
                        shade = 0.8 + 0.2 * clamp(lp.vert.normal.dot(Vector((0.4, -0.5, 0.75))) * 0.5 + 0.5)
                        shade *= 0.78 + 0.22 * clamp(v.z / 2.0)           # roots sit in shadow
                        cc = base
                        if birch:
                            band = math.sin(v.z * 7.3 + math.sin(v.z * 2.1) * 2 + math.atan2(v.y, v.x) * 0.5)
                            if band > 0.82 or hash3(v * 5.1) > 0.93:
                                cc = srgb(0x2e2a26)
                        lp[col] = (*(clamp(x * shade) for x in cc), 1.0)
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0005)
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        attr = me.color_attributes.get('Col')
        if attr:
            me.color_attributes.active_color = attr
            me.color_attributes.render_color_index = me.color_attributes.find('Col')
        ob = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(ob)
        ob.data.materials.append(tree_material())
        return ob


_MAT = []


def tree_material():
    if not _MAT:
        m = bpy.data.materials.new('Tree')
        m.use_nodes = True
        nt = m.node_tree
        bsdf = nt.nodes['Principled BSDF']
        bsdf.inputs['Roughness'].default_value = 0.85
        a = nt.nodes.new('ShaderNodeVertexColor')
        a.layer_name = 'Col'
        nt.links.new(a.outputs['Color'], bsdf.inputs['Base Color'])
        _MAT.append(m)
    return _MAT[0]


# ---------------------------------------------------------------- species
# Each builder takes lod (0 = close up, 1 = far) and returns the finished object.

def ring(n, r, z, rng, jitter=0.25, a0=0.0):
    out = []
    for i in range(n):
        a = a0 + i / n * math.tau + rng.uniform(-jitter, jitter)
        rr = r * rng.uniform(0.8, 1.1)
        out.append((math.cos(a) * rr, math.sin(a) * rr, z + rng.uniform(-0.4, 0.4)))
    return out


def pine(lod):
    t = Tree(11)
    seg = 7 if lod == 0 else 5
    t.trunk([(0, 0, -0.3, 0.34), (0, 0, 1.0, 0.26), (0.05, 0.02, 6.0, 0.15), (0.0, 0.05, 11.6, 0.05)], seg, 0x5b4231)
    n = 5 if lod == 0 else 4
    for i in range(n):
        f = i / (n - 1)
        t.tier(3.0 + i * 1.75, 2.75 * (1 - f) + 0.85, 2.7 - 0.5 * f, 11 if lod == 0 else 7, 0x2c5b2f, tip=0x3f7737, droop=0.32)
    t.tier(3.0 + n * 1.75 - 0.4, 0.55, 1.5, 7 if lod == 0 else 5, 0x2f6131, tip=0x45803b, droop=0.1)
    return t.finish('Pine' + ('' if lod == 0 else '_LOD1'))


def spruce(lod):
    t = Tree(12)
    seg = 6 if lod == 0 else 5
    t.trunk([(0, 0, -0.3, 0.3), (0, 0, 1.0, 0.22), (0, 0, 9.4, 0.05)], seg, 0x4f3a2c)
    n = 8 if lod == 0 else 5
    for i in range(n):
        f = i / (n - 1)
        t.tier(1.1 + i * (7.6 / n), 2.0 * (1 - f) + 0.45, 2.0 - 0.6 * f, 10 if lod == 0 else 6, 0x264f3f, tip=0x356a4e, droop=0.45)
    t.tier(1.1 + 7.6 - 0.2, 0.4, 1.3, 6 if lod == 0 else 4, 0x2c5a46, tip=0x3b745a, droop=0.05)
    return t.finish('Spruce' + ('' if lod == 0 else '_LOD1'))


def oak(lod):
    t = Tree(13)
    rng = t.rng
    seg = 8 if lod == 0 else 5
    t.trunk([(0, 0, -0.3, 0.55), (0, 0, 0.5, 0.42), (0.1, 0.05, 2.8, 0.34)], seg, 0x5e4632)
    # root flare
    if lod == 0:
        for a in range(4):
            ang = a * math.tau / 4 + 0.4
            t.limb((0, 0, 0.7), (math.cos(ang) * 0.8, math.sin(ang) * 0.8, -0.2), 0.2, 0.08, 5, 0x5e4632)
    limbs = []
    for k in range(3):
        ang = k * math.tau / 3 + 0.3
        end = (math.cos(ang) * 1.7, math.sin(ang) * 1.7, 4.9 + rng.uniform(-0.3, 0.3))
        t.limb((0.1, 0.05, 2.7), end, 0.24, 0.1, 6 if lod == 0 else 4, 0x5e4632)
        limbs.append(end)
    blobs = [(0, 0, 6.1, 2.5)]
    for (x, y, z) in limbs:
        blobs.append((x * 1.25, y * 1.25, z + 0.6, 1.9))
    for (x, y, z) in ring(5, 2.4, 6.6, rng):
        blobs.append((x, y, z, 1.5))
    blobs.append((0.3, -0.2, 7.6, 1.7))
    t.canopy(blobs, 0x447629, faces=760 if lod == 0 else 90, voxel=0.34, lumpy=0.55, squash=0.85, tip=0x5f9236)
    return t.finish('Oak' + ('' if lod == 0 else '_LOD1'))


def birch(lod):
    t = Tree(14)
    rng = t.rng
    seg = 7 if lod == 0 else 5
    pts = [(0, 0, -0.3, 0.22), (0, 0, 1.2, 0.17), (0.12, 0.05, 4.5, 0.12), (0.05, 0.1, 8.0, 0.06), (0.08, 0.1, 9.4, 0.03)]
    t.trunk(pts, seg, 0xe6e1d6, birch=True)
    # an upright, open crown: small clusters at the ends of short branches, all round the trunk
    blobs = [(0.08, 0.08, 6.4, 1.15), (0.08, 0.1, 8.6, 0.85)]
    for (z, rad, r, n, a0) in ((4.9, 1.25, 0.95, 4, 0.2), (6.4, 1.1, 0.9, 4, 1.0), (7.7, 0.8, 0.8, 3, 0.5)):
        for k in range(n):
            a = a0 + k * math.tau / n + rng.uniform(-0.25, 0.25)
            bx, by, bz = math.cos(a) * rad, math.sin(a) * rad, z + rng.uniform(-0.25, 0.25)
            blobs.append((bx, by, bz, r * rng.uniform(0.9, 1.1)))
            if lod == 0:
                t.limb((0.08, 0.08, bz - 0.9), (bx * 0.8, by * 0.8, bz - 0.15), 0.05, 0.022, 4, 0xd9d3c7, birch=True)
    t.canopy(blobs, 0x6f9a39, faces=640 if lod == 0 else 80, voxel=0.26, lumpy=0.38, squash=0.92, tip=0x8fb64e)
    return t.finish('Birch' + ('' if lod == 0 else '_LOD1'))


def poplar(lod):
    t = Tree(15)
    rng = t.rng
    seg = 7 if lod == 0 else 5
    t.trunk([(0, 0, -0.3, 0.3), (0, 0, 1.0, 0.24), (0, 0, 3.0, 0.2)], seg, 0x6a5440)
    blobs = []
    for k in range(6):
        z = 3.6 + k * 1.75
        r = 1.55 * math.sin(math.pi * (k + 0.8) / 6.8) + 0.45
        blobs.append((rng.uniform(-0.15, 0.15), rng.uniform(-0.15, 0.15), z, r))
    t.canopy(blobs, 0x4a7a2f, faces=560 if lod == 0 else 70, voxel=0.3, lumpy=0.4, squash=1.0, tip=0x66983a)
    return t.finish('Poplar' + ('' if lod == 0 else '_LOD1'))


def maple(lod):
    t = Tree(16)
    rng = t.rng
    seg = 7 if lod == 0 else 5
    t.trunk([(0, 0, -0.3, 0.4), (0, 0, 0.6, 0.3), (-0.1, 0.1, 2.4, 0.24)], seg, 0x4e3b2e)
    ends = []
    for k in range(4):
        ang = k * math.tau / 4 + 0.7
        e = (math.cos(ang) * 1.2, math.sin(ang) * 1.2, 3.9)
        t.limb((-0.1, 0.1, 2.3), e, 0.15, 0.07, 5 if lod == 0 else 4, 0x4e3b2e)
        ends.append(e)
    blobs = [(0, 0, 5.0, 2.2), (0, 0, 6.2, 1.6)]
    for (x, y, z) in ends:
        blobs.append((x * 1.3, y * 1.3, z + 0.9, 1.5))
    t.canopy(blobs, 0xc9612a, faces=640 if lod == 0 else 80, voxel=0.32, lumpy=0.5, squash=0.9, tip=0xe8a03c)
    return t.finish('Maple' + ('' if lod == 0 else '_LOD1'))


def bush(lod):
    t = Tree(17)
    rng = t.rng
    blobs = [(0, 0, 0.55, 0.85)]
    for (x, y, z) in ring(4, 0.7, 0.45, rng, a0=0.3):
        blobs.append((x, y, max(0.3, z), rng.uniform(0.5, 0.7)))
    t.canopy(blobs, 0x40652f, faces=340 if lod == 0 else 40, voxel=0.14, lumpy=0.34, squash=0.8, tip=0x5f8a3a)
    return t.finish('Bush' + ('' if lod == 0 else '_LOD1'))


def snag(lod):
    t = Tree(18)
    rng = t.rng
    seg = 7 if lod == 0 else 5
    t.trunk([(0, 0, -0.3, 0.32), (0, 0, 0.8, 0.24), (0.15, 0.0, 3.5, 0.17), (0.05, 0.2, 6.2, 0.08)], seg, 0x6b6157)
    limbs = [((0.1, 0, 2.6), (1.3, 0.4, 3.9)), ((0.12, 0.05, 3.3), (-1.2, -0.3, 4.6)), ((0.1, 0.15, 4.4), (0.5, -1.1, 5.4)),
             ((0.08, 0.18, 5.1), (-0.6, 0.9, 6.0)), ((1.0, 0.3, 3.6), (1.6, 0.1, 4.6))]
    if lod == 1:
        limbs = limbs[:3]
    for a, b in limbs:
        t.limb(a, b, 0.07, 0.025, 5 if lod == 0 else 4, 0x766b60)
    return t.finish('Snag' + ('' if lod == 0 else '_LOD1'))


def reeds(lod):
    """A clump of tall reeds with a few cattail heads, for bog and lake edges."""
    t = Tree(19)
    rng = t.rng
    n = 16 if lod == 0 else 7
    for k in range(n):
        a = rng.random() * math.tau
        r0 = rng.random() * 0.35
        x0, y0 = math.cos(a) * r0, math.sin(a) * r0
        hgt = rng.uniform(1.1, 1.9)
        lean = rng.uniform(0.1, 0.35)
        top = (x0 + math.cos(a) * lean, y0 + math.sin(a) * lean, hgt)
        bm = bmesh.new()
        d = Vector(top) - Vector((x0, y0, 0))
        rot = d.normalized().to_track_quat('Z', 'Y').to_matrix().to_4x4()
        m = Matrix.Translation((Vector((x0, y0, 0)) + Vector(top)) / 2) @ rot
        bmesh.ops.create_cone(bm, cap_ends=False, segments=3, radius1=0.035, radius2=0.004, depth=d.length, matrix=m)
        t.add_bm(bm, 'leaf', False, color=0x5b7a34, lo=Vector((-0.5, -0.5, 0)), hi=Vector((0.5, 0.5, 1.9)), centre=Vector((0, 0, 0.6)), tip=0xa8a35a)
    for k in range(4 if lod == 0 else 2):
        a = k * 1.7 + 0.4
        x0, y0 = math.cos(a) * 0.2, math.sin(a) * 0.2
        h = 1.5 + 0.12 * k
        t.limb((x0, y0, 0), (x0 * 1.3, y0 * 1.3, h), 0.012, 0.01, 4, 0x6b7a3a, kind='bark')
        t.limb((x0 * 1.3, y0 * 1.3, h - 0.02), (x0 * 1.3, y0 * 1.3, h + 0.24), 0.045, 0.04, 6 if lod == 0 else 4, 0x5a3a22, kind='bark')
    return t.finish('Reeds' + ('' if lod == 0 else '_LOD1'))


SPECIES = [pine, spruce, oak, birch, poplar, maple, bush, snag, reeds]


# ---------------------------------------------------------------- export & preview

def export(objs):
    os.makedirs(OUT, exist_ok=True)
    bpy.ops.object.select_all(action='DESELECT')
    for ob in objs:
        ob.select_set(True)
    path = os.path.join(OUT, 'trees.glb')
    kw = dict(filepath=path, export_format='GLB', use_selection=True, export_yup=True, export_apply=False,
              export_cameras=False, export_lights=False)
    try:
        bpy.ops.export_scene.gltf(**kw, export_vertex_color='ACTIVE')
    except TypeError:
        bpy.ops.export_scene.gltf(**kw)
    for ob in objs:
        tris = sum(len(p.vertices) - 2 for p in ob.data.polygons)
        print(f'[trees] {ob.name}: {tris} tris')
    print(f'[trees] wrote {path} ({os.path.getsize(path) / 1024:.0f} KB)')


def preview(objs):
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 32
    scene.cycles.device = 'CPU'
    scene.render.resolution_x, scene.render.resolution_y = 1600, 620
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
    sun.energy = 3.2
    so = bpy.data.objects.new('sun', sun)
    so.rotation_euler = (math.radians(45), 0, math.radians(30))
    scene.collection.objects.link(so)
    near = [o for o in objs if not o.name.endswith('_LOD1')]
    far = [o for o in objs if o.name.endswith('_LOD1')]
    x = 0
    for a, b in zip(near, far):
        a.location = (x, 0, 0)
        b.location = (x, 9, 0)
        x += 9
    gm = bpy.data.meshes.new('ground')
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=60)
    bm.to_mesh(gm)
    bm.free()
    g = bpy.data.objects.new('ground', gm)
    g.location = (x / 2 - 4.5, 0, 0)
    gmat = bpy.data.materials.new('groundmat')
    gmat.use_nodes = True
    gmat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (0.2, 0.3, 0.12, 1)
    gm.materials.append(gmat)
    scene.collection.objects.link(g)
    cam = bpy.data.cameras.new('cam')
    cam.lens = 35
    co = bpy.data.objects.new('cam', cam)
    co.location = (x / 2 - 4.5, -66, 10)
    co.rotation_euler = (math.radians(84), 0, 0)
    scene.collection.objects.link(co)
    scene.camera = co
    os.makedirs(PREVIEWS, exist_ok=True)
    scene.render.filepath = os.path.join(os.path.abspath(PREVIEWS), 'trees.png')
    bpy.ops.render.render(write_still=True)
    for ob in objs:
        ob.location = (0, 0, 0)


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    objs = []
    for fn in SPECIES:
        for lod in (0, 1):
            objs.append(fn(lod))
    export(objs)
    if PREVIEWS:
        preview(objs)


main()
