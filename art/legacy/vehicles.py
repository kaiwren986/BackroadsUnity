"""
Builds the Backroads vehicles in Blender and exports them as glTF binaries (.glb).

Run from the repo root (tools/build-vehicles.bat does this for you):
  blender -b --factory-startup --python tools/blender/vehicles.py -- --out assets/vehicles
Options:  --only hatch,rally   build a subset
          --previews DIR       also render a 3/4 preview PNG of each vehicle

Geometry is authored in GAME model space: x = left, y = up, z = forward, ground at y = 0,
the same space as DEFS in js/vehicle.js. Everything is converted to Blender axes just before
export, and the glTF exporter's +Y-up conversion turns it back into game space.

Object names the game looks for:
  Body                  everything that is bolted to the chassis
  Wheel                 one wheel at the origin, axle along x, rim face towards +x
  Caliper               brake caliper for one wheel (follows steering, does not spin)
  TrackL / TrackR       dozer track belts (uv.y runs along the belt so the tread can scroll)
  Blade                 dozer blade assembly (moves up and down)
Material names the game swaps at load time: Paint, Glass, Headlight, Taillight, Reverse,
Indicator, Tread.
"""
import bpy
import bmesh
import math
import os
import sys
from mathutils import Matrix, Euler, Vector

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []


def arg(name, default=None):
    if name in argv:
        i = argv.index(name)
        return argv[i + 1] if i + 1 < len(argv) else default
    return default


ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
OUT = os.path.abspath(arg('--out', os.path.join(ROOT, 'assets', 'vehicles')))
PREVIEWS = arg('--previews')
ONLY = set(arg('--only').split(',')) if arg('--only') else None
PI = math.pi

# --------------------------------------------------------------------------- materials


def lin(h):
    def c(v):
        v /= 255.0
        return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4
    return (c((h >> 16) & 255), c((h >> 8) & 255), c(h & 255))


MATDEF = {
    'Paint': dict(color=0x2e9fd4, metal=0.5, rough=0.3, coat=1.0),
    'Stripe': dict(color=0xf2f2ee, metal=0.15, rough=0.32, coat=1.0),
    'Livery': dict(color=0x2f6f4f, metal=0.3, rough=0.35, coat=0.6),
    'Livery2': dict(color=0xf2c94c, metal=0.3, rough=0.35, coat=0.6),
    'Trim': dict(color=0x121315, rough=0.55),
    'Under': dict(color=0x0c0c0d, rough=0.9),
    'Rubber': dict(color=0x151516, rough=0.92),
    'Chrome': dict(color=0xe8eaee, metal=1.0, rough=0.07),
    'Alloy': dict(color=0xc8ccd2, metal=1.0, rough=0.22),
    'DarkAlloy': dict(color=0x26282c, metal=0.9, rough=0.3),
    'Steel': dict(color=0x55595f, metal=0.85, rough=0.45),
    'Brake': dict(color=0x8d8f93, metal=0.9, rough=0.35),
    'Caliper': dict(color=0xb02a20, rough=0.38),
    'Glass': dict(color=0x10161b, rough=0.03, alpha=0.38),
    'Interior': dict(color=0x1d1e20, rough=0.85),
    'Seat': dict(color=0x2b2c30, rough=0.9),
    'Headlight': dict(color=0xf3f3f3, rough=0.05, emit=0xfff4dc),
    'Taillight': dict(color=0x6e0905, rough=0.12, emit=0xff2a1a),
    'Reverse': dict(color=0xe0e0e0, rough=0.12),
    'Indicator': dict(color=0xd57a14, rough=0.12, emit=0xff9a1f),
    'Plate': dict(color=0xe9e6da, rough=0.5),
    'Grille': dict(color=0x0d0e0f, metal=0.4, rough=0.45),
    'Mirror': dict(color=0xb8c4cc, metal=1.0, rough=0.02),
    'Tread': dict(color=0x1c1c1e, rough=0.9),
    'BoxPanel': dict(color=0xedede9, metal=0.15, rough=0.42),
    'Rib': dict(color=0xd5d6d2, metal=0.3, rough=0.4),
    'Bedliner': dict(color=0x1a1b1d, rough=0.95),
    'Yellow': dict(color=0xf2b20f, metal=0.3, rough=0.4, coat=0.4),
}
MATS = {}


def material(name):
    if name in MATS:
        return MATS[name]
    d = MATDEF[name]
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    p = m.node_tree.nodes['Principled BSDF']
    rgb = lin(d['color'])
    p.inputs['Base Color'].default_value = (*rgb, 1)
    p.inputs['Metallic'].default_value = d.get('metal', 0.0)
    p.inputs['Roughness'].default_value = d.get('rough', 0.5)
    if d.get('coat'):
        p.inputs['Coat Weight'].default_value = d['coat']
        p.inputs['Coat Roughness'].default_value = 0.05
    if d.get('emit') is not None:
        p.inputs['Emission Color'].default_value = (*lin(d['emit']), 1)
        p.inputs['Emission Strength'].default_value = 0.6
    if d.get('alpha', 1) < 1:
        p.inputs['Alpha'].default_value = d['alpha']
        for attr, val in (('surface_render_method', 'BLENDED'), ('blend_method', 'BLEND')):
            try:
                setattr(m, attr, val)
            except Exception:
                pass
    m.diffuse_color = (*rgb, 1)
    MATS[name] = m
    return m


# --------------------------------------------------------------------------- parts & primitives


def M(pos=(0, 0, 0), rot=(0, 0, 0), scale=(1, 1, 1)):
    return Matrix.Translation(Vector(pos)) @ Euler(rot, 'XYZ').to_matrix().to_4x4() @ Matrix.Diagonal((*scale, 1))


class Part:
    """Collects geometry for one exported object; each face carries a material name."""

    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.mats = []

    def mi(self, mname):
        if mname not in self.mats:
            self.mats.append(mname)
        return self.mats.index(mname)

    def add(self, tmp, mname=None, names=None):
        # names: per-face material names via a list indexed by tmp face material_index
        for f in tmp.faces:
            f.material_index = self.mi(mname if names is None else names[f.material_index])
        me = bpy.data.meshes.new('tmp')
        tmp.to_mesh(me)
        tmp.free()
        self.bm.from_mesh(me)
        bpy.data.meshes.remove(me)

    def to_object(self):
        me = bpy.data.meshes.new(self.name)
        bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
        self.bm.to_mesh(me)
        self.bm.free()
        for n in self.mats:
            me.materials.append(material(n))
        ob = bpy.data.objects.new(self.name, me)
        bpy.context.scene.collection.objects.link(ob)
        return ob


def box(part, mname, size, pos, rot=(0, 0, 0), bevel=0.0, seg=3):
    tmp = bmesh.new()
    bmesh.ops.create_cube(tmp, size=1.0, matrix=Matrix.Diagonal((size[0], size[1], size[2], 1)))
    if bevel > 0:
        b = min(bevel, min(size) * 0.49)
        bmesh.ops.bevel(tmp, geom=list(tmp.edges), offset=b, segments=seg, profile=0.5,
                        affect='EDGES', offset_type='OFFSET', clamp_overlap=True)
    bmesh.ops.transform(tmp, matrix=M(pos, rot), verts=tmp.verts)
    part.add(tmp, mname)


AXIS_ROT = {'x': (0, PI / 2, 0), 'y': (-PI / 2, 0, 0), 'z': (0, 0, 0)}


def cyl(part, mname, r, length, pos, axis='x', seg=24, r2=None, rot=None, bevel=0.0):
    tmp = bmesh.new()
    bmesh.ops.create_cone(tmp, cap_ends=True, cap_tris=False, segments=seg, radius1=r,
                          radius2=r if r2 is None else r2, depth=length)
    if bevel > 0:
        caps = [e for e in tmp.edges if all(abs(abs(v.co.z) - length / 2) < 1e-5 for v in e.verts)]
        bmesh.ops.bevel(tmp, geom=caps, offset=min(bevel, r * 0.4, length * 0.45), segments=2,
                        profile=0.5, affect='EDGES', clamp_overlap=True)
    bmesh.ops.transform(tmp, matrix=M(pos, AXIS_ROT[axis] if rot is None else rot), verts=tmp.verts)
    part.add(tmp, mname)


def sphere(part, mname, r, pos, scale=(1, 1, 1), rot=(0, 0, 0), seg=16):
    tmp = bmesh.new()
    bmesh.ops.create_uvsphere(tmp, u_segments=seg, v_segments=max(6, seg // 2), radius=r)
    bmesh.ops.transform(tmp, matrix=M(pos, rot, scale), verts=tmp.verts)
    part.add(tmp, mname)


def lathe(part, mname, profile, seg=32, pos=(0, 0, 0), rot=(0, 0, 0), closed=True):
    """Revolve a profile of (axial, radius) points around the x axis."""
    tmp = bmesh.new()
    vs = [tmp.verts.new((a, r, 0.0)) for a, r in profile]
    es = [tmp.edges.new((vs[i], vs[i + 1])) for i in range(len(vs) - 1)]
    if closed:
        es.append(tmp.edges.new((vs[-1], vs[0])))
    bmesh.ops.spin(tmp, geom=vs + es, cent=(0, 0, 0), axis=(1, 0, 0), angle=2 * PI, steps=seg,
                   use_merge=True, use_duplicate=False)
    bmesh.ops.remove_doubles(tmp, verts=tmp.verts, dist=1e-5)
    bmesh.ops.transform(tmp, matrix=M(pos, rot), verts=tmp.verts)
    part.add(tmp, mname)


def tube_arc(part, mname, R, r, center, a0, a1, seg=16, rseg=8, rot=None):
    """Part of a torus around the x axis (arch flares, steering wheels). rot: extra rotation matrix."""
    tmp = bmesh.new()
    ring = []
    for k in range(seg + 1):
        a = a0 + (a1 - a0) * k / seg
        row = []
        for j in range(rseg):
            b = 2 * PI * j / rseg
            rad = R + r * math.cos(b)
            row.append(tmp.verts.new((r * math.sin(b), math.sin(a) * rad, math.cos(a) * rad)))  # a=0 front, pi/2 top
        ring.append(row)
    for k in range(seg):
        for j in range(rseg):
            tmp.faces.new((ring[k][j], ring[k][(j + 1) % rseg], ring[k + 1][(j + 1) % rseg], ring[k + 1][j]))
    tmp.faces.new(ring[0])
    tmp.faces.new(list(reversed(ring[-1])))
    mat = Matrix.Translation(Vector(center)) @ (rot if rot is not None else Matrix.Identity(4))
    bmesh.ops.transform(tmp, matrix=mat, verts=tmp.verts)
    part.add(tmp, mname)


def tube(part, mname, a, b, r, seg=10):
    """Cylinder between two points."""
    a, b = Vector(a), Vector(b)
    d = b - a
    tmp = bmesh.new()
    bmesh.ops.create_cone(tmp, cap_ends=True, cap_tris=False, segments=seg, radius1=r, radius2=r, depth=d.length)
    q = Vector((0, 0, 1)).rotation_difference(d.normalized())
    bmesh.ops.transform(tmp, matrix=Matrix.Translation((a + b) / 2) @ q.to_matrix().to_4x4(), verts=tmp.verts)
    part.add(tmp, mname)


# --------------------------------------------------------------------------- lofted body shells


def section(st, stripe):
    """Half cross-section (x >= 0) of a body station, bottom centre to top centre."""
    z, w, yb, belt, top, wt, crown = st[:7]
    green = top - belt > 0.25
    pts = [(0.0, yb), (w * 0.86, yb), (w * 0.97, yb + 0.11), (w, (yb + belt) * 0.5 + 0.03), (w * 0.985, belt)]
    if green:
        pts += [(w * 0.93, belt + 0.035), (wt, top - 0.075), (wt * 0.86, top - 0.004)]
    else:
        pts += [(w * 0.95, belt + 0.025), (w * 0.88, top - 0.012), (max(w * 0.66, stripe + 0.08), top)]
    pts += [(stripe, top + crown), (0.0, top + crown)]
    return pts


def loft(stations, stripe=None, subdiv=2):
    """Station tuples: (z, halfWidth, bottom, belt, top, topHalfWidth, crown, side, topFlag).
    side: p paint, g glass, b black pillar.  topFlag: p paint, g glass.
    Returns (body_part_object, glass_object)."""
    sw = stripe if stripe else 0.18
    bm = bmesh.new()
    names = ['Paint', 'Trim', 'Under', 'Glass', 'Stripe']
    rows = []
    for st in stations:
        rows.append([bm.verts.new((x, y, st[0])) for x, y in section(st, sw)])
    nc = len(rows[0])
    for i in range(len(rows) - 1):
        side, topf = stations[i][7], stations[i][8]
        green = side in 'gb'
        for c in range(nc - 1):
            if c == 0:
                n = 'Under'
            elif c == 1:
                n = 'Trim'
            elif c in (2, 3):
                n = 'Paint'
            elif c == 4:
                n = 'Trim' if green else 'Paint'
            elif c == 5:
                n = {'g': 'Glass', 'b': 'Trim'}.get(side, 'Paint')
            elif c in (6, 7):
                n = 'Glass' if topf == 'g' else 'Paint'
            else:
                n = 'Glass' if topf == 'g' else ('Stripe' if stripe else 'Paint')
            f = bm.faces.new((rows[i][c], rows[i][c + 1], rows[i + 1][c + 1], rows[i + 1][c]))
            f.material_index = names.index(n)
    for cap in (rows[0], list(reversed(rows[-1]))):
        f = bm.faces.new(cap)
        f.material_index = 0
    bmesh.ops.mirror(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], axis='X', merge_dist=0.0005)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new('loft')
    bm.to_mesh(me)
    bm.free()
    for n in names:
        me.materials.append(material(n))
    ob = bpy.data.objects.new('loft', me)
    bpy.context.scene.collection.objects.link(ob)
    sub = ob.modifiers.new('sub', 'SUBSURF')
    sub.levels = subdiv
    sub.render_levels = subdiv
    apply_mods(ob)
    # split the glass off into its own object
    glass = ob.copy()
    glass.data = ob.data.copy()
    glass.name = 'glass'
    bpy.context.scene.collection.objects.link(glass)
    gi = names.index('Glass')
    keep_faces(glass, lambda f: f.material_index == gi)
    keep_faces(ob, lambda f: f.material_index != gi)
    return ob, glass


def keep_faces(ob, pred):
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if not pred(f)], context='FACES')
    bm.to_mesh(ob.data)
    bm.free()


def inner_shell(ob, thick=0.03):
    """Dark inside surface so you see an interior through the glass, not the far panels."""
    sh = ob.copy()
    sh.data = ob.data.copy()
    sh.name = 'inner'
    bpy.context.scene.collection.objects.link(sh)
    bm = bmesh.new()
    bm.from_mesh(sh.data)
    bm.normal_update()
    offs = [v.normal.copy() for v in bm.verts]
    for v, n in zip(bm.verts, offs):
        v.co -= n * thick
    bmesh.ops.reverse_faces(bm, faces=bm.faces)
    for f in bm.faces:
        f.material_index = 0
    bm.to_mesh(sh.data)
    bm.free()
    sh.data.materials.clear()
    sh.data.materials.append(material('Interior'))
    return sh


def apply_mods(ob):
    dg = bpy.context.evaluated_depsgraph_get()
    ev = ob.evaluated_get(dg)
    me = bpy.data.meshes.new_from_object(ev, preserve_all_data_layers=True, depsgraph=dg)
    ob.modifiers.clear()
    old = ob.data
    ob.data = me
    if old.users == 0:
        bpy.data.meshes.remove(old)


def cut_arches(obs, arches, x_in, x_out=1.7):
    """Boolean the wheel arches out of the given shells. arches: [(side_sign, z, y, radius)]"""
    cut = Part('cutter')
    for sx, z, y, r in arches:
        cyl(cut, 'Under', r, x_out - x_in, (sx * (x_in + x_out) / 2, y, z), seg=48)
    cutter = cut.to_object()
    for ob in obs:
        mod = ob.modifiers.new('arch', 'BOOLEAN')
        mod.operation = 'DIFFERENCE'
        mod.solver = 'EXACT'
        try:
            mod.use_hole_tolerant = True
            mod.material_mode = 'TRANSFER'
        except Exception:
            pass
        mod.object = cutter
        apply_mods(ob)
    bpy.data.objects.remove(cutter, do_unlink=True)


def liners(part, arches, x_in, x_out):
    """Black wheel-well liners so you can't see inside the car through the arch."""
    for sx, z, y, r in arches:
        x0, x1 = sorted((sx * x_in, sx * x_out))
        tmp = bmesh.new()
        seg = 20
        rows = []
        for k in range(seg + 1):
            a = -0.25 + (PI + 0.5) * k / seg
            yy, zz = y + math.sin(a) * r, z + math.cos(a) * r
            rows.append((tmp.verts.new((x0, yy, zz)), tmp.verts.new((x1, yy, zz))))
        for k in range(seg):
            tmp.faces.new((rows[k][0], rows[k][1], rows[k + 1][1], rows[k + 1][0]))
        # inner wall
        wall = [tmp.verts.new((x0 if sx > 0 else x1, y + math.sin(-0.25 + (PI + 0.5) * k / seg) * r,
                                z + math.cos(-0.25 + (PI + 0.5) * k / seg) * r)) for k in range(seg + 1)]
        tmp.faces.new(wall)
        part.add(tmp, 'Under')


def merge_objects(name, obs):
    """Join objects into one (keeping all their materials)."""
    bm = bmesh.new()
    mats = []
    for ob in obs:
        me = ob.data
        idx = []
        for m in me.materials:
            if m.name not in mats:
                mats.append(m.name)
            idx.append(mats.index(m.name))
        tmp = me.copy()
        for p in tmp.polygons:
            p.material_index = idx[p.material_index] if idx else 0
        bm.from_mesh(tmp)
        bpy.data.meshes.remove(tmp)
    for ob in obs:
        bpy.data.objects.remove(ob, do_unlink=True)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for n in mats:
        me.materials.append(bpy.data.materials[n])
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


# --------------------------------------------------------------------------- shared components


def wheel(name, r, w, style='car', spokes=5):
    """A complete wheel at the origin: tyre, rim, brake disc. Axle along x, rim face to +x."""
    p = Part(name)
    hw = w / 2
    rb = r * (0.62 if style != 'offroad' else 0.56)
    # tyre: closed profile, revolved
    prof = [(-hw * 0.9, rb), (-hw * 0.98, rb + 0.035 * r), (-hw * 1.03, r * 0.8), (-hw * 0.96, r * 0.94),
            (-hw * 0.82, r * 0.995)]
    grooves = [-0.42, 0.0, 0.42] if style == 'car' else [-0.25, 0.25]
    for g in grooves:
        prof += [(hw * (g - 0.07), r * 0.995), (hw * (g - 0.05), r * 0.972), (hw * (g + 0.05), r * 0.972), (hw * (g + 0.07), r * 0.995)]
    prof += [(hw * 0.82, r * 0.995), (hw * 0.96, r * 0.94), (hw * 1.03, r * 0.8), (hw * 0.98, rb + 0.035 * r), (hw * 0.9, rb)]
    lathe(p, 'Rubber', prof, seg=36)
    if style in ('offroad', 'truck'):
        n = 26 if style == 'offroad' else 30
        for i in range(n):
            a = 2 * PI * i / n
            for side in (-1, 1):
                off = (i % 2) * 0.12
                ax = side * hw * (0.48 + off)
                rr = r * 0.995 + 0.012
                box(p, 'Rubber', (hw * 0.62, 0.035, r * 0.18), (ax, math.cos(a) * rr, math.sin(a) * rr), rot=(a, 0, 0))
    # rim barrel + face
    face_x = hw * 0.55
    rim = 'Alloy' if style == 'car' else ('DarkAlloy' if style == 'offroad' else 'Steel')
    lathe(p, rim, [(-hw * 0.88, rb * 0.97), (-hw * 0.92, rb * 1.02), (hw * 0.92, rb * 1.02), (hw * 0.97, rb * 1.07),
                   (hw * 1.0, rb * 1.03), (hw * 0.86, rb * 0.95), (-hw * 0.85, rb * 0.93)], seg=40)
    if style == 'truck':
        # steel disc wheel with hand holes
        lathe(p, rim, [(face_x - 0.02, 0.0), (face_x - 0.02, rb * 0.95), (face_x + 0.015, rb * 0.95), (face_x + 0.015, 0.0)], seg=40)
        for i in range(8):
            a = 2 * PI * i / 8 + PI / 8
            cyl(p, 'Under', rb * 0.12, 0.05, (face_x + 0.005, math.cos(a) * rb * 0.62, math.sin(a) * rb * 0.62), seg=12)
        cyl(p, 'Chrome', rb * 0.28, 0.09, (face_x + 0.03, 0, 0), seg=20, r2=rb * 0.22)
        for i in range(10):
            a = 2 * PI * i / 10
            cyl(p, 'Chrome', 0.017, 0.08, (face_x + 0.05, math.cos(a) * rb * 0.36, math.sin(a) * rb * 0.36), seg=6)
    else:
        cyl(p, rim, rb * 0.26, 0.07, (face_x, 0, 0), seg=24)
        for i in range(spokes):
            a = 2 * PI * i / spokes
            for twin in ((-0.09, 0.09) if style == 'car' else (0,)):
                aa = a + twin
                rm = rb * 0.6
                box(p, rim, (0.035, rb * 0.72, rb * (0.16 if style == 'car' else 0.3)),
                    (face_x - 0.01, math.cos(aa) * rm, math.sin(aa) * rm), rot=(aa, 0, 0), bevel=0.01, seg=2)
        cyl(p, 'Chrome' if style == 'car' else rim, rb * 0.14, 0.05, (face_x + 0.035, 0, 0), seg=20)
        nuts = 5 if style != 'offroad' else 6
        for i in range(nuts):
            a = 2 * PI * i / nuts + 0.3
            cyl(p, 'Chrome', 0.011, 0.04, (face_x + 0.03, math.cos(a) * rb * 0.19, math.sin(a) * rb * 0.19), seg=6)
    # brake disc + hat
    cyl(p, 'Brake', rb * 0.82, 0.024, (-hw * 0.15, 0, 0), seg=36)
    cyl(p, 'DarkAlloy', rb * 0.34, 0.08, (-hw * 0.05, 0, 0), seg=20)
    return p.to_object()


def wheel_low(name, r, w, style='car'):
    """Distance version of the wheel: a plain tyre and a flat rim disc."""
    p = Part(name)
    hw = w / 2
    rb = r * (0.62 if style != 'offroad' else 0.56)
    lathe(p, 'Rubber', [(-hw * 0.95, rb), (-hw, r * 0.85), (-hw * 0.85, r), (hw * 0.85, r), (hw, r * 0.85), (hw * 0.95, rb)], seg=16)
    rim = 'Alloy' if style == 'car' else ('DarkAlloy' if style == 'offroad' else 'Steel')
    cyl(p, rim, rb, w * 0.9, (0, 0, 0), seg=16)
    return p.to_object()


MINOR = {'Chrome', 'Alloy', 'DarkAlloy', 'Steel', 'Grille', 'Under', 'Rubber', 'Seat', 'Interior', 'Plate',
         'Mirror', 'Brake', 'Caliper', 'Bedliner'}


def lod_copy(ob, name, ratio):
    """Decimated copy with the minor materials folded into Trim (fewer triangles and draw calls)."""
    lo = ob.copy()
    lo.data = ob.data.copy()
    lo.name = name
    bpy.context.scene.collection.objects.link(lo)
    dec = lo.modifiers.new('dec', 'DECIMATE')
    dec.decimate_type = 'COLLAPSE'
    dec.ratio = ratio
    apply_mods(lo)
    names = [m.name for m in lo.data.materials]
    if 'Trim' not in names:
        lo.data.materials.append(material('Trim'))
        names.append('Trim')
    if 'BoxPanel' in names or 'Rib' in names:
        if 'BoxPanel' not in names:
            lo.data.materials.append(material('BoxPanel'))
            names.append('BoxPanel')
    ti = names.index('Trim')
    for poly in lo.data.polygons:
        n = names[poly.material_index]
        if n == 'Rib':
            poly.material_index = names.index('BoxPanel')
        elif n in MINOR:
            poly.material_index = ti
    return lo


def caliper(name, r, w, style='car'):
    p = Part(name)
    hw = w / 2
    rb = r * (0.62 if style != 'offroad' else 0.56)
    rr = rb * 0.7
    a = 2.0  # angle in the y/z plane: up and towards the rear
    box(p, 'Caliper' if style == 'car' else 'Steel', (0.075, 0.07, rb * 0.42),
        (-hw * 0.15, math.cos(a) * rr * -1, -math.sin(a) * rr), rot=(-a + PI / 2, 0, 0), bevel=0.012, seg=2)
    return p.to_object()


def mirror_pair(part, x, y, z, w=0.09, h=0.13, d=0.2, paint='Paint'):
    for sx in (1, -1):
        box(part, paint, (w, h, d * 0.55), (sx * x, y, z), bevel=0.03)
        box(part, 'Mirror', (0.012, h * 0.75, d * 0.42), (sx * (x + w * 0.5 + 0.004), y, z - 0.01))
        box(part, 'Trim', (0.12, 0.03, 0.05), (sx * (x - 0.07), y - 0.04, z + 0.02), bevel=0.01)


def interior(part, x_seat, y_floor, z_front_seat, rear=True, wheel_x=None, dash_z=0.6, dash_y=None, width=1.4):
    for sx in (1, -1):
        box(part, 'Seat', (0.5, 0.14, 0.5), (sx * x_seat, y_floor + 0.25, z_front_seat), bevel=0.05)
        box(part, 'Seat', (0.48, 0.62, 0.13), (sx * x_seat, y_floor + 0.62, z_front_seat - 0.27), rot=(0.18, 0, 0), bevel=0.05)
        box(part, 'Seat', (0.26, 0.14, 0.1), (sx * x_seat, y_floor + 1.0, z_front_seat - 0.33), rot=(0.18, 0, 0), bevel=0.04)
    if rear:
        box(part, 'Seat', (width * 0.95, 0.14, 0.48), (0, y_floor + 0.25, z_front_seat - 0.95), bevel=0.05)
        box(part, 'Seat', (width * 0.95, 0.55, 0.13), (0, y_floor + 0.6, z_front_seat - 1.2), rot=(0.12, 0, 0), bevel=0.05)
    dy = dash_y if dash_y is not None else y_floor + 0.62
    box(part, 'Interior', (width, 0.24, 0.42), (0, dy, dash_z), bevel=0.06)
    wx = x_seat if wheel_x is None else wheel_x
    # steering wheel faces the driver, tilted back
    rm = Matrix.Rotation(-0.45, 4, 'X') @ Matrix.Rotation(-PI / 2, 4, 'Y')
    tube_arc(part, 'Trim', 0.17, 0.018, (wx, dy + 0.18, dash_z - 0.28), 0, 2 * PI, seg=24, rseg=6, rot=rm)
    tube(part, 'Trim', (wx, dy + 0.18, dash_z - 0.28), (wx, dy + 0.02, dash_z - 0.05), 0.025)


# --------------------------------------------------------------------------- vehicles


def build_hatch():
    r, w, wx, fz, rz = 0.31, 0.21, 0.76, 1.26, -1.22
    st = [
        (1.94, 0.60, 0.33, 0.58, 0.62, 0.45, 0.00, 'p', 'p'),
        (1.88, 0.80, 0.25, 0.68, 0.73, 0.62, 0.01, 'p', 'p'),
        (1.62, 0.85, 0.22, 0.78, 0.84, 0.70, 0.02, 'p', 'p'),
        (1.10, 0.86, 0.21, 0.84, 0.91, 0.72, 0.03, 'p', 'p'),
        (0.82, 0.86, 0.21, 0.86, 0.95, 0.72, 0.03, 'p', 'g'),
        (0.22, 0.86, 0.21, 0.88, 1.40, 0.66, 0.03, 'g', 'p'),
        (-0.30, 0.86, 0.21, 0.89, 1.44, 0.66, 0.03, 'b', 'p'),
        (-0.40, 0.86, 0.21, 0.89, 1.44, 0.66, 0.03, 'g', 'p'),
        (-1.38, 0.85, 0.22, 0.90, 1.41, 0.64, 0.03, 'p', 'g'),
        (-1.80, 0.83, 0.25, 0.88, 1.19, 0.62, 0.02, 'p', 'p'),
        (-1.90, 0.78, 0.29, 0.80, 0.95, 0.56, 0.01, 'p', 'p'),
        (-1.95, 0.64, 0.35, 0.70, 0.78, 0.44, 0.00, 'p', 'p'),
    ]
    body, glass = loft(st)
    inner = inner_shell(body)
    arches = [(sx, z, r, r + 0.065) for sx in (1, -1) for z in (fz, rz)]
    cut_arches([body, inner], arches, 0.45)
    d = Part('details')
    liners(d, arches, 0.46, 0.84)
    # front
    box(d, 'Grille', (0.74, 0.16, 0.06), (0, 0.47, 1.905), bevel=0.03)
    box(d, 'Grille', (0.62, 0.05, 0.05), (0, 0.66, 1.905), bevel=0.02)
    box(d, 'Chrome', (0.18, 0.04, 0.02), (0, 0.665, 1.93), bevel=0.008)
    box(d, 'Trim', (1.4, 0.06, 0.12), (0, 0.3, 1.84), bevel=0.025)
    box(d, 'Plate', (0.46, 0.11, 0.02), (0, 0.47, 1.935), bevel=0.006)
    for sx in (1, -1):
        box(d, 'Headlight', (0.32, 0.12, 0.14), (sx * 0.6, 0.71, 1.83), rot=(0.05, sx * 0.32, 0), bevel=0.04)
        box(d, 'Indicator', (0.07, 0.04, 0.04), (sx * 0.8, 0.66, 1.73), rot=(0, sx * 0.9, 0), bevel=0.015)
        box(d, 'Grille', (0.16, 0.06, 0.05), (sx * 0.56, 0.39, 1.9), bevel=0.02)
        # tail lights wrap the rear corners
        box(d, 'Taillight', (0.13, 0.3, 0.1), (sx * 0.66, 0.98, -1.86), rot=(0, -sx * 0.5, 0), bevel=0.03)
        box(d, 'Reverse', (0.1, 0.06, 0.08), (sx * 0.66, 0.81, -1.86), rot=(0, -sx * 0.5, 0), bevel=0.02)
        # door handles, wipers
        for z in (0.05, -0.92):
            box(d, 'Trim', (0.025, 0.03, 0.13), (sx * 0.872, 0.82, z), bevel=0.01)
        box(d, 'Trim', (0.035, 0.03, 1.1), (sx * 0.6, 1.425, -0.6), bevel=0.012)
    for i, z in enumerate((0.74, 0.72)):
        box(d, 'Trim', (0.58, 0.012, 0.02), (0.28 - i * 0.56, 0.975, z), rot=(0, 0, 0.12 - i * 0.24))
    mirror_pair(d, 0.92, 1.0, 0.6)
    box(d, 'Trim', (1.52, 0.13, 0.12), (0, 0.38, -1.92), bevel=0.04)
    box(d, 'Plate', (0.46, 0.11, 0.02), (0, 0.6, -1.965), bevel=0.006)
    box(d, 'Trim', (1.0, 0.05, 0.08), (0, 1.22, -1.84), rot=(-0.3, 0, 0), bevel=0.02)  # hatch spoiler
    box(d, 'Taillight', (0.22, 0.03, 0.02), (0, 1.24, -1.865), bevel=0.008)
    cyl(d, 'Chrome', 0.035, 0.12, (-0.48, 0.27, -1.95), axis='z', seg=14)
    interior(d, 0.38, 0.22, -0.12, dash_z=0.58, width=1.4)
    det = d.to_object()
    obs = {'Body': merge_objects('Body', [body, glass, inner, det])}
    return obs, dict(r=r, w=w, style='car', spokes=6), dict(exhaust=(-0.48, 0.27, -2.0))


def build_rally():
    r, w, wx, fz, rz = 0.33, 0.26, 0.82, 1.38, -1.30
    st = [
        (2.10, 0.70, 0.26, 0.50, 0.53, 0.50, 0.00, 'p', 'p'),
        (2.03, 0.87, 0.19, 0.60, 0.65, 0.70, 0.01, 'p', 'p'),
        (1.72, 0.92, 0.17, 0.69, 0.74, 0.76, 0.03, 'p', 'p'),
        (1.10, 0.93, 0.16, 0.76, 0.81, 0.78, 0.04, 'p', 'p'),
        (0.58, 0.93, 0.16, 0.79, 0.86, 0.76, 0.04, 'p', 'g'),
        (-0.10, 0.93, 0.16, 0.81, 1.25, 0.66, 0.03, 'g', 'p'),
        (-0.72, 0.93, 0.16, 0.82, 1.27, 0.64, 0.03, 'p', 'g'),
        (-1.52, 0.93, 0.17, 0.83, 0.99, 0.72, 0.03, 'p', 'p'),
        (-1.95, 0.89, 0.20, 0.80, 0.90, 0.68, 0.02, 'p', 'p'),
        (-2.08, 0.80, 0.26, 0.71, 0.77, 0.58, 0.01, 'p', 'p'),
        (-2.12, 0.66, 0.32, 0.62, 0.66, 0.46, 0.00, 'p', 'p'),
    ]
    body, glass = loft(st, stripe=0.2)
    inner = inner_shell(body)
    arches = [(sx, z, r, r + 0.06) for sx in (1, -1) for z in (fz, rz)]
    cut_arches([body, inner], arches, 0.5)
    d = Part('details')
    liners(d, arches, 0.51, 0.9)
    for sx in (1, -1):
        for z in (fz, rz):
            tube_arc(d, 'Trim', r + 0.075, 0.035, (sx * 0.93, r, z), -0.15, PI + 0.15, seg=18, rseg=6)
        box(d, 'Headlight', (0.38, 0.1, 0.16), (sx * 0.63, 0.63, 1.96), rot=(0.12, sx * 0.3, 0), bevel=0.035)
        box(d, 'Taillight', (0.42, 0.09, 0.08), (sx * 0.58, 0.75, -2.07), rot=(0, -sx * 0.2, 0), bevel=0.025)
        box(d, 'Reverse', (0.1, 0.05, 0.06), (sx * 0.28, 0.75, -2.1), bevel=0.015)
        cyl(d, 'Stripe', 0.2, 0.01, (sx * 0.925, 0.52, 0.05), seg=28)
        box(d, 'Trim', (0.03, 0.3, 0.32), (sx * 0.86, 0.32, -1.62), bevel=0.01)  # mud flaps
        box(d, 'Trim', (0.025, 0.03, 0.13), (sx * 0.925, 0.73, -0.25), bevel=0.01)
        box(d, 'Trim', (0.06, 0.24, 0.05), (sx * 0.62, 1.12, -1.93), bevel=0.015)  # spoiler uprights
    box(d, 'Paint', (1.78, 0.05, 0.34), (0, 1.25, -1.98), rot=(-0.12, 0, 0), bevel=0.02)
    for sx in (1, -1):
        box(d, 'Paint', (0.04, 0.16, 0.38), (sx * 0.89, 1.2, -1.98), bevel=0.01)
    # hood scoop and vents
    box(d, 'Paint', (0.5, 0.08, 0.5), (0, 0.85, 1.0), rot=(-0.05, 0, 0), bevel=0.04)
    box(d, 'Grille', (0.4, 0.05, 0.03), (0, 0.86, 1.25), bevel=0.012)
    box(d, 'Grille', (1.2, 0.17, 0.05), (0, 0.38, 2.1), bevel=0.04)
    box(d, 'Trim', (1.5, 0.05, 0.12), (0, 0.2, 2.02), bevel=0.02)
    box(d, 'Trim', (1.6, 0.16, 0.12), (0, 0.32, -2.1), bevel=0.03)
    box(d, 'Plate', (0.46, 0.11, 0.02), (0, 0.52, -2.13), bevel=0.006)
    # roof light pod
    box(d, 'Trim', (0.95, 0.08, 0.14), (0, 1.33, 0.12), bevel=0.03)
    for i in range(4):
        cyl(d, 'Headlight', 0.07, 0.05, (-0.33 + i * 0.22, 1.33, 0.2), axis='z', seg=18)
    box(d, 'Caliper', (0.1, 0.06, 0.12), (0, 0.24, 2.17), bevel=0.02)  # tow hook
    cyl(d, 'Chrome', 0.055, 0.16, (-0.5, 0.3, -2.13), axis='z', seg=16)
    mirror_pair(d, 0.97, 0.95, 0.45, d=0.18)
    interior(d, 0.4, 0.16, -0.2, rear=False, dash_z=0.42, width=1.5)
    tube(d, 'Steel', (0.65, 1.2, -0.55), (0.65, 0.4, -0.55), 0.025)
    tube(d, 'Steel', (-0.65, 1.2, -0.55), (-0.65, 0.4, -0.55), 0.025)
    tube(d, 'Steel', (0.65, 1.2, -0.55), (-0.65, 1.2, -0.55), 0.025)
    det = d.to_object()
    obs = {'Body': merge_objects('Body', [body, glass, inner, det])}
    return obs, dict(r=r, w=w, style='offroad_low', spokes=5), dict(exhaust=(-0.5, 0.3, -2.2))


def build_pickup():
    r, w, wx, fz, rz = 0.43, 0.30, 0.86, 1.62, -1.62
    st = [
        (2.64, 0.82, 0.62, 0.98, 1.04, 0.72, 0.00, 'p', 'p'),
        (2.58, 0.95, 0.56, 1.10, 1.16, 0.86, 0.01, 'p', 'p'),
        (2.25, 0.98, 0.55, 1.16, 1.22, 0.92, 0.015, 'p', 'p'),
        (1.30, 0.98, 0.55, 1.20, 1.27, 0.92, 0.015, 'p', 'p'),
        (0.98, 0.98, 0.55, 1.22, 1.30, 0.9, 0.015, 'p', 'g'),
        (0.48, 0.98, 0.55, 1.24, 1.86, 0.86, 0.015, 'g', 'p'),
        (-0.02, 0.98, 0.55, 1.24, 1.88, 0.86, 0.015, 'b', 'p'),
        (-0.12, 0.98, 0.55, 1.24, 1.88, 0.86, 0.015, 'g', 'p'),
        (-0.38, 0.98, 0.55, 1.24, 1.87, 0.86, 0.015, 'p', 'p'),
        (-0.47, 0.96, 0.58, 1.22, 1.82, 0.82, 0.0, 'p', 'p'),
    ]
    body, glass = loft(st)
    inner = inner_shell(body)
    arches_f = [(sx, fz, r + 0.02, r + 0.09) for sx in (1, -1)]
    arches_r = [(sx, rz, r + 0.02, r + 0.09) for sx in (1, -1)]
    cut_arches([body, inner], arches_f, 0.5)
    # bed: side walls get the arch cut, the rest is left alone (overlapping boxes upset booleans)
    sides = Part('bedsides')
    for sx in (1, -1):
        box(sides, 'Paint', (0.08, 0.6, 2.18), (sx * 0.94, 1.0, -1.6), bevel=0.03)
    sidesob = sides.to_object()
    cut_arches([sidesob], arches_r, 0.5)
    bed = Part('bed')
    for sx in (1, -1):
        box(bed, 'Trim', (0.12, 0.04, 2.2), (sx * 0.94, 1.31, -1.6), bevel=0.012)
    box(bed, 'Paint', (1.94, 0.5, 0.07), (0, 1.06, -0.53), bevel=0.02)
    box(bed, 'Paint', (1.9, 0.52, 0.07), (0, 1.03, -2.68), bevel=0.025)
    box(bed, 'Bedliner', (1.8, 0.05, 2.1), (0, 0.86, -1.6), bevel=0.01)
    box(bed, 'Under', (1.8, 0.16, 2.08), (0, 0.76, -1.6))
    bedob = bed.to_object()
    d = Part('details')
    liners(d, arches_f + arches_r, 0.51, 0.95)
    for sx in (1, -1):
        for z in (fz, rz):
            tube_arc(d, 'Trim', r + 0.11, 0.05, (sx * 0.985, r + 0.02, z), -0.1, PI + 0.1, seg=18, rseg=6)
        box(d, 'Headlight', (0.32, 0.2, 0.1), (sx * 0.68, 1.02, 2.6), bevel=0.03)
        box(d, 'Indicator', (0.08, 0.2, 0.08), (sx * 0.9, 1.0, 2.55), rot=(0, sx * 0.6, 0), bevel=0.02)
        box(d, 'Taillight', (0.06, 0.34, 0.1), (sx * 0.975, 1.1, -2.66), bevel=0.02)
        box(d, 'Reverse', (0.065, 0.09, 0.1), (sx * 0.975, 0.88, -2.66), bevel=0.015)
        box(d, 'Trim', (0.2, 0.05, 2.0), (sx * 1.05, 0.62, 0.0), bevel=0.02)  # running boards
        box(d, 'Trim', (0.03, 0.035, 0.14), (sx * 0.985, 1.12, 0.35), bevel=0.01)
        box(d, 'Steel', (0.12, 0.2, 7.0 * 0.62), (sx * 0.48, 0.62, -0.3), bevel=0.01)  # frame rails
    box(d, 'Grille', (1.38, 0.38, 0.06), (0, 0.99, 2.63), bevel=0.04)
    for i in range(4):
        box(d, 'Chrome', (1.32, 0.025, 0.03), (0, 0.86 + i * 0.09, 2.66), bevel=0.008)
    box(d, 'Chrome', (1.46, 0.46, 0.04), (0, 0.99, 2.615), bevel=0.05)
    box(d, 'Steel', (2.0, 0.22, 0.22), (0, 0.72, 2.66), bevel=0.04)
    box(d, 'Steel', (1.2, 0.08, 0.6), (0, 0.5, 2.3), rot=(-0.25, 0, 0), bevel=0.02)  # skid plate
    for sx in (-0.55, 0.55):
        tube(d, 'Steel', (sx, 0.72, 2.75), (sx, 1.3, 2.8), 0.045)
    tube(d, 'Steel', (-0.6, 1.25, 2.8), (0.6, 1.25, 2.8), 0.045)
    box(d, 'Plate', (0.46, 0.11, 0.02), (0, 0.62, 2.78), bevel=0.006)
    box(d, 'Steel', (2.0, 0.18, 0.22), (0, 0.72, -2.76), bevel=0.04)
    box(d, 'Plate', (0.46, 0.11, 0.02), (0, 0.98, -2.72), bevel=0.006)
    box(d, 'Trim', (0.3, 0.05, 0.03), (0, 1.22, -2.72), bevel=0.01)
    # rear cab window
    box(d, 'Glass', (1.24, 0.38, 0.02), (0, 1.58, -0.485), bevel=0.04)
    box(d, 'Trim', (1.32, 0.46, 0.015), (0, 1.58, -0.48), bevel=0.05)
    # roll bar + light bar
    for sx in (1, -1):
        tube(d, 'Steel', (sx * 0.86, 1.3, -0.66), (sx * 0.86, 1.98, -0.66), 0.035)
    tube(d, 'Steel', (0.86, 1.98, -0.66), (-0.86, 1.98, -0.66), 0.035)
    box(d, 'Trim', (1.1, 0.08, 0.12), (0, 1.94, 0.55), bevel=0.03)
    for i in range(4):
        box(d, 'Indicator', (0.18, 0.06, 0.03), (-0.36 + i * 0.24, 1.94, 0.615), bevel=0.012)
    mirror_pair(d, 1.06, 1.42, 0.85, w=0.08, h=0.24, d=0.24, paint='Trim')
    for i, z in enumerate((0.93, 0.91)):
        box(d, 'Trim', (0.7, 0.012, 0.02), (0.34 - i * 0.68, 1.3, z), rot=(0, 0, 0.1 - i * 0.2))
    cyl(d, 'Chrome', 0.04, 0.14, (-0.7, 0.62, -2.82), axis='z', seg=14)
    interior(d, 0.42, 0.62, 0.0, rear=False, dash_z=0.72, width=1.6)
    det = d.to_object()
    obs = {'Body': merge_objects('Body', [body, glass, inner, sidesob, bedob, det])}
    return obs, dict(r=r, w=w, style='offroad', spokes=6), dict(exhaust=(-0.7, 0.62, -2.88))


def build_truck():
    r, w, wx, fz, rz = 0.5, 0.34, 1.0, 2.45, -2.15
    d = Part('details')
    cab = Part('cab')
    # cab-over cab
    box(cab, 'Paint', (2.34, 1.12, 2.15), (0, 1.36, 2.88), bevel=0.1)
    box(cab, 'Paint', (2.34, 1.28, 2.05), (0, 2.5, 2.83), bevel=0.16)
    box(cab, 'Paint', (2.2, 0.06, 1.85), (0, 3.16, 2.8), bevel=0.03)
    cabob = cab.to_object()
    cut_arches([cabob], [(sx, fz, r, r + 0.09) for sx in (1, -1)], 0.62)
    liners(d, [(sx, fz, r, r + 0.09) for sx in (1, -1)], 0.63, 1.15)
    # glass
    box(d, 'Glass', (2.06, 0.98, 0.04), (0, 2.42, 3.9), rot=(-0.07, 0, 0), bevel=0.06)
    box(d, 'Trim', (2.18, 1.1, 0.03), (0, 2.42, 3.885), rot=(-0.07, 0, 0), bevel=0.07)
    for sx in (1, -1):
        box(d, 'Glass', (0.03, 0.78, 1.0), (sx * 1.172, 2.38, 3.15), bevel=0.05)
        box(d, 'Trim', (0.02, 0.86, 1.08), (sx * 1.168, 2.38, 3.15), bevel=0.05)
        box(d, 'Trim', (0.025, 0.04, 0.18), (sx * 1.175, 1.82, 2.55), bevel=0.01)
        box(d, 'Steel', (0.4, 0.06, 0.32), (sx * 1.0, 0.86, 3.2), bevel=0.01)  # steps
        box(d, 'Steel', (0.4, 0.06, 0.32), (sx * 1.0, 0.52, 3.2), bevel=0.01)
        box(d, 'Headlight', (0.4, 0.2, 0.06), (sx * 0.8, 1.16, 3.94), bevel=0.025)
        box(d, 'Indicator', (0.14, 0.18, 0.06), (sx * 1.08, 1.16, 3.93), bevel=0.02)
        # mirror arms
        tube(d, 'Trim', (sx * 1.16, 2.7, 3.55), (sx * 1.42, 2.75, 3.62), 0.022)
        tube(d, 'Trim', (sx * 1.16, 2.2, 3.55), (sx * 1.42, 2.35, 3.62), 0.022)
        box(d, 'Trim', (0.1, 0.5, 0.2), (sx * 1.45, 2.5, 3.62), bevel=0.03)
        box(d, 'Mirror', (0.02, 0.42, 0.16), (sx * 1.45, 2.5, 3.5), bevel=0.01)
        # chassis rails, side guards, mud flaps
        box(d, 'Steel', (0.16, 0.26, 7.7), (sx * 0.5, 0.86, -0.45), bevel=0.01)
        for y in (0.62, 0.86):
            box(d, 'Alloy', (0.05, 0.08, 3.2), (sx * 1.16, y, 0.15), bevel=0.01)
        box(d, 'Rubber', (0.38, 0.45, 0.03), (sx * 1.0, 0.45, rz - 0.62))
        box(d, 'Rubber', (0.3, 0.3, 0.03), (sx * 1.0, 0.52, fz - 0.62))
        # box lights
        for i in range(4):
            box(d, 'Indicator', (0.03, 0.08, 0.14), (sx * 1.24, 2.62, -3.6 + i * 1.55), bevel=0.01)
        box(d, 'Taillight', (0.18, 0.42, 0.05), (sx * 1.02, 1.12, -4.07), bevel=0.02)
        box(d, 'Reverse', (0.14, 0.1, 0.05), (sx * 0.72, 0.98, -4.07), bevel=0.02)
    for i in range(5):
        box(d, 'Indicator', (0.16, 0.07, 0.05), (-0.8 + i * 0.4, 3.12, 3.78), bevel=0.02)
    box(d, 'Paint', (2.24, 0.06, 0.36), (0, 3.02, 3.96), rot=(0.35, 0, 0), bevel=0.02)  # sun visor
    box(d, 'Grille', (1.7, 0.46, 0.05), (0, 1.5, 3.93), bevel=0.04)
    for i in range(4):
        box(d, 'Chrome', (1.64, 0.03, 0.03), (0, 1.33 + i * 0.11, 3.96), bevel=0.008)
    box(d, 'Steel', (2.42, 0.34, 0.28), (0, 0.84, 3.97), bevel=0.05)
    box(d, 'Plate', (0.52, 0.12, 0.02), (0, 0.84, 4.115), bevel=0.006)
    # fuel tank + battery box
    cyl(d, 'Alloy', 0.28, 1.05, (0.93, 0.72, 0.6), axis='z', seg=24, bevel=0.03)
    box(d, 'Steel', (0.5, 0.42, 0.6), (-0.95, 0.72, 0.8), bevel=0.03)
    # exhaust stack
    cyl(d, 'Chrome', 0.075, 1.5, (-1.05, 2.75, 1.68), axis='y', seg=16)
    cyl(d, 'Steel', 0.12, 0.5, (-1.05, 2.3, 1.68), axis='y', seg=16)
    # box body
    bx = Part('box')
    box(bx, 'BoxPanel', (2.45, 2.7, 5.5), (0, 2.38, -1.25), bevel=0.05)
    for sx in (1, -1):
        for i in range(10):
            box(bx, 'Rib', (0.03, 2.6, 0.06), (sx * 1.232, 2.38, 1.35 - i * 0.58))
        box(bx, 'Livery', (0.02, 0.52, 5.4), (sx * 1.232, 1.55, -1.25))
        box(bx, 'Livery2', (0.02, 0.1, 5.4), (sx * 1.236, 1.86, -1.25))
    for y in (1.04, 3.72):
        for sx in (1, -1):
            box(bx, 'Alloy', (0.08, 0.08, 5.52), (sx * 1.22, y, -1.25), bevel=0.01)
    for sx in (1, -1):
        for z in (1.48, -3.98):
            box(bx, 'Alloy', (0.08, 2.72, 0.08), (sx * 1.22, 2.38, z), bevel=0.01)
    # rear doors
    for sx in (1, -1):
        box(bx, 'BoxPanel', (1.18, 2.5, 0.03), (sx * 0.6, 2.38, -4.015), bevel=0.01)
        for y in (1.7, 3.0):
            box(bx, 'Alloy', (0.12, 0.08, 0.03), (sx * 1.1, y, -4.04))
        tube(bx, 'Chrome', (sx * 0.3, 1.2, -4.05), (sx * 0.3, 3.55, -4.05), 0.018)
    box(bx, 'Steel', (2.36, 0.2, 0.32), (0, 0.78, -4.05), bevel=0.03)
    box(bx, 'Plate', (0.52, 0.12, 0.02), (0, 0.78, -4.22), bevel=0.006)
    boxob = bx.to_object()
    # simple cab interior (seen through the side glass)
    box(d, 'Interior', (2.1, 0.3, 0.4), (0, 1.85, 3.55), bevel=0.05)
    det = d.to_object()
    obs = {'Body': merge_objects('Body', [cabob, boxob, det])}
    return obs, dict(r=r, w=w, style='truck', spokes=8), dict(exhaust=(-1.05, 3.55, 1.68), exhaust_up=True)


def track_belt(name, x, z0, z1, ry0, ry1, rf, rr, width, thick=0.07):
    """Stadium-shaped belt in the y/z plane. Front idler centre (z0, ry0, radius rf), rear sprocket
    (z1, ry1, radius rr). uv.y runs along the belt (1 unit = 1 m)."""
    pts = []
    n = 14
    # bottom run, rear curve, top run, front curve
    for k in range(n + 1):  # front idler, from bottom going up the front
        a = -PI / 2 + PI * k / n
        pts.append((z0 + math.cos(a) * rf, ry0 + math.sin(a) * rf))
    for k in range(n + 1):  # rear sprocket, from top going down the back
        a = PI / 2 + PI * k / n
        pts.append((z1 + math.cos(a) * rr, ry1 + math.sin(a) * rr))
    # resample to even spacing
    perim = []
    for i in range(len(pts)):
        a, b = pts[i], pts[(i + 1) % len(pts)]
        perim.append(math.hypot(b[0] - a[0], b[1] - a[1]))
    L = sum(perim)
    m = 96
    even = []
    acc, i, t = 0.0, 0, 0.0
    for k in range(m):
        target = L * k / m
        while acc + perim[i] < target:
            acc += perim[i]
            i += 1
        f = (target - acc) / perim[i]
        a, b = pts[i], pts[(i + 1) % len(pts)]
        even.append((a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, target))
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new('UVMap')
    # outward normal in the (z, y) plane for each point
    def outward(k):
        p0, p1 = even[(k - 1) % m], even[(k + 1) % m]
        tz, ty = p1[0] - p0[0], p1[1] - p0[1]
        l = math.hypot(tz, ty) or 1
        return (ty / l, -tz / l)
    hw = width / 2
    rings = []
    for k, (zz, yy, s) in enumerate(even):
        nz, ny = outward(k)
        # make sure the normal points away from the loop centre
        cz, cy = (z0 + z1) / 2, (ry0 + ry1) / 2
        if (zz - cz) * nz + (yy - cy) * ny < 0:
            nz, ny = -nz, -ny
        o = (zz + nz * thick, yy + ny * thick)
        rings.append([bm.verts.new((x - hw, yy, zz)), bm.verts.new((x + hw, yy, zz)),
                      bm.verts.new((x + hw, o[1], o[0])), bm.verts.new((x - hw, o[1], o[0])), s])
    for k in range(m):
        a, b = rings[k], rings[(k + 1) % m]
        sa, sb = a[4], b[4] if k < m - 1 else L
        for j in range(4):
            f = bm.faces.new((a[j], a[(j + 1) % 4], b[(j + 1) % 4], b[j]))
            u0, u1 = j / 4, (j + 1) / 4
            for loop, (uu, vv) in zip(f.loops, ((u0, sa), (u1, sa), (u1, sb), (u0, sb))):
                loop[uv].uv = (uu, vv)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(material('Tread'))
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def build_dozer():
    p = Part('Body')
    tx = 1.12
    for sx in (1, -1):
        x = sx * tx
        # track frame, idler, sprocket, rollers
        box(p, 'Steel', (0.36, 0.36, 2.9), (x, 0.42, 0.0), bevel=0.04)
        cyl(p, 'Steel', 0.36, 0.44, (x, 0.42, 1.55), seg=28, bevel=0.03)
        cyl(p, 'Steel', 0.4, 0.44, (x, 0.48, -1.55), seg=28, bevel=0.03)
        for i in range(12):
            a = 2 * PI * i / 12
            box(p, 'Steel', (0.46, 0.08, 0.08), (x, 0.48 + math.cos(a) * 0.4, -1.55 + math.sin(a) * 0.4), rot=(-a, 0, 0))
        for z in (-0.9, -0.3, 0.3, 0.9):
            cyl(p, 'Steel', 0.13, 0.4, (x, 0.2, z), seg=18)
        # fenders / walkways over the tracks
        box(p, 'Paint', (0.74, 0.06, 3.5), (x, 0.97, 0.0), bevel=0.02)
    # main body / engine bay
    box(p, 'Paint', (1.64, 0.95, 2.2), (0, 1.43, 0.72), bevel=0.06)
    box(p, 'Paint', (1.5, 0.12, 2.0), (0, 1.95, 0.75), bevel=0.04)
    for i in range(6):
        box(p, 'Grille', (1.1, 0.02, 0.06), (0, 2.02, 0.15 + i * 0.22))
    box(p, 'Grille', (1.2, 0.7, 0.04), (0, 1.43, 1.83), bevel=0.02)
    for i in range(5):
        box(p, 'Steel', (1.16, 0.03, 0.03), (0, 1.15 + i * 0.13, 1.86))
    for sx in (1, -1):
        box(p, 'Grille', (0.02, 0.4, 0.9), (sx * 0.825, 1.5, 0.9), bevel=0.01)
    # rear counterweight / ripper housing
    box(p, 'Paint', (1.9, 0.85, 0.5), (0, 1.15, -1.6), bevel=0.06)
    box(p, 'Steel', (1.3, 0.3, 0.3), (0, 0.65, -1.95), bevel=0.04)
    # cab (ROPS)
    cz, cw, cy0, cy1, cl = -0.55, 1.5, 1.88, 3.0, 1.3
    for sx in (1, -1):
        for sz in (1, -1):
            box(p, 'Paint', (0.09, cy1 - cy0, 0.09), (sx * cw / 2, (cy0 + cy1) / 2, cz + sz * cl / 2), bevel=0.02)
    box(p, 'Paint', (cw + 0.2, 0.12, cl + 0.22), (0, cy1 + 0.04, cz), bevel=0.04)
    box(p, 'Paint', (cw + 0.05, 0.2, cl + 0.05), (0, cy0 - 0.06, cz), bevel=0.03)
    for (gx, gz, gw, gd) in ((0, cz + cl / 2, cw - 0.08, 0.02), (0, cz - cl / 2, cw - 0.08, 0.02),
                             (cw / 2, cz, 0.02, cl - 0.08), (-cw / 2, cz, 0.02, cl - 0.08)):
        box(p, 'Glass', (gw, cy1 - cy0 - 0.1, gd), (gx, (cy0 + cy1) / 2, gz))
    box(p, 'Seat', (0.55, 0.14, 0.5), (0, 2.05, cz - 0.2), bevel=0.05)
    box(p, 'Seat', (0.52, 0.6, 0.14), (0, 2.42, cz - 0.46), bevel=0.05)
    box(p, 'Interior', (0.9, 0.5, 0.25), (0, 2.1, cz + 0.45), bevel=0.04)
    for sx in (1, -1):
        tube(p, 'Trim', (sx * 0.3, 2.2, cz + 0.2), (sx * 0.3, 2.55, cz + 0.28), 0.018)
    # exhaust, air cleaner, handrails, lights
    cyl(p, 'Steel', 0.08, 0.95, (0.55, 2.45, 1.1), axis='y', seg=16)
    cyl(p, 'Steel', 0.13, 0.04, (0.55, 2.94, 1.1), axis='y', seg=16)
    cyl(p, 'DarkAlloy', 0.14, 0.4, (-0.5, 2.2, 1.25), axis='y', seg=18, bevel=0.03)
    for sx in (1, -1):
        tube(p, 'Paint', (sx * 0.86, 1.95, 1.75), (sx * 0.86, 1.95, -0.1), 0.02)
        tube(p, 'Paint', (sx * 0.86, 1.95, 1.75), (sx * 0.86, 1.6, 1.8), 0.02)
        box(p, 'Headlight', (0.18, 0.12, 0.08), (sx * 0.55, 3.0, cz + cl / 2 + 0.1), bevel=0.02)
        box(p, 'Taillight', (0.12, 0.1, 0.06), (sx * 0.55, 3.0, cz - cl / 2 - 0.1), bevel=0.02)
        box(p, 'Headlight', (0.16, 0.12, 0.06), (sx * 0.45, 1.75, 1.86), bevel=0.02)
    cyl(p, 'Indicator', 0.09, 0.14, (0, 3.16, cz), axis='y', seg=16)
    body = p.to_object()
    tl = track_belt('TrackL', tx, 1.55, -1.55, 0.42, 0.48, 0.36, 0.4, 0.62)
    tr = track_belt('TrackR', -tx, 1.55, -1.55, 0.42, 0.48, 0.36, 0.4, 0.62)
    # blade assembly (moves as a unit; modelled at its lowered rest position)
    b = Part('Blade')
    bz, bw = 2.45, 3.5
    prof = []
    for k in range(9):  # moldboard curve, concave towards the front
        t = k / 8
        y = 0.04 + 1.2 * t
        z = bz + 0.12 - 0.36 * math.sin(t * PI * 0.62) + 0.22 * t * t
        prof.append((z, y))
    tmp = bmesh.new()
    fronts, backs = [], []
    xs = [-bw / 2, -bw / 4, 0, bw / 4, bw / 2]
    for x in xs:
        fronts.append([tmp.verts.new((x, y, z)) for z, y in prof])
        backs.append([tmp.verts.new((x, y + 0.02, z - 0.1)) for z, y in prof])
    for i in range(len(xs) - 1):
        for k in range(len(prof) - 1):
            tmp.faces.new((fronts[i][k], fronts[i + 1][k], fronts[i + 1][k + 1], fronts[i][k + 1]))
            tmp.faces.new((backs[i][k + 1], backs[i + 1][k + 1], backs[i + 1][k], backs[i][k]))
        tmp.faces.new((fronts[i][0], backs[i][0], backs[i + 1][0], fronts[i + 1][0]))
        tmp.faces.new((fronts[i][-1], fronts[i + 1][-1], backs[i + 1][-1], backs[i][-1]))
    for ends in (0, len(xs) - 1):
        loop = fronts[ends] + list(reversed(backs[ends]))
        tmp.faces.new(loop if ends == 0 else list(reversed(loop)))
    bmesh.ops.recalc_face_normals(tmp, faces=tmp.faces)
    b.add(tmp, 'Paint')
    box(b, 'Steel', (bw, 0.1, 0.08), (0, 0.06, bz + 0.13), bevel=0.01)
    for sx in (1, -1):
        box(b, 'Steel', (0.1, 0.5, 0.3), (sx * (bw / 2 - 0.02), 0.28, bz + 0.05), bevel=0.02)
        box(b, 'Paint', (0.06, 1.1, 0.5), (sx * (bw / 2 + 0.01), 0.62, bz - 0.06), bevel=0.02)
        # push arms back to the track frames
        tube(b, 'Paint', (sx * 1.18, 0.42, bz - 0.2), (sx * 1.18, 0.48, 0.75), 0.09)
        box(b, 'Paint', (0.14, 0.3, 0.3), (sx * 1.18, 0.45, bz - 0.25), bevel=0.03)
        # lift cylinders
        tube(b, 'Paint', (sx * 0.62, 1.05, bz - 0.25), (sx * 0.62, 1.6, 1.5), 0.075)
        tube(b, 'Chrome', (sx * 0.62, 0.9, bz - 0.12), (sx * 0.62, 1.12, bz - 0.32), 0.04)
    blade = b.to_object()
    return {'Body': body, 'TrackL': tl, 'TrackR': tr, 'Blade': blade}, None, dict(exhaust=(0.55, 2.95, 1.1), exhaust_up=True)


BUILDERS = {
    'hatch': build_hatch,
    'rally': build_rally,
    'pickup': build_pickup,
    'truck': build_truck,
    'dozer': build_dozer,
}

# --------------------------------------------------------------------------- export & preview

TO_BLENDER = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))


def smooth_by_angle(ob, deg=38):
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    thr = math.radians(deg)
    for f in bm.faces:
        f.smooth = True
    for e in bm.edges:
        if len(e.link_faces) == 2:
            if e.calc_face_angle(0) > thr:
                e.smooth = False
        else:
            e.smooth = False
    bm.to_mesh(ob.data)
    bm.free()


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    MATS.clear()


def export(vid, objs):
    os.makedirs(OUT, exist_ok=True)
    for ob in objs:
        ob.data.transform(TO_BLENDER)
        smooth_by_angle(ob)
    bpy.ops.object.select_all(action='DESELECT')
    for ob in objs:
        ob.select_set(True)
    path = os.path.join(OUT, vid + '.glb')
    bpy.ops.export_scene.gltf(filepath=path, export_format='GLB', use_selection=True, export_yup=True,
                              export_apply=False, export_cameras=False, export_lights=False)
    tris = sum(sum(len(p.vertices) - 2 for p in ob.data.polygons) for ob in objs)
    print(f'[vehicles] {vid}: {os.path.getsize(path) / 1024:.0f} KB, {tris} tris')
    return path


def render_preview(vid, objs, wheel_layout):
    scene = bpy.context.scene
    for eng in ('BLENDER_EEVEE_NEXT', 'BLENDER_EEVEE', 'CYCLES'):
        try:
            scene.render.engine = eng
            break
        except Exception:
            continue
    if scene.render.engine == 'CYCLES':
        scene.cycles.samples = 48
    scene.render.resolution_x, scene.render.resolution_y = 1200, 700
    scene.render.film_transparent = False
    try:
        scene.view_settings.view_transform = 'AgX'
    except Exception:
        pass
    world = bpy.data.worlds.new('w')
    scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes['Background']
    bg.inputs['Color'].default_value = (0.55, 0.65, 0.8, 1)
    bg.inputs['Strength'].default_value = 0.9
    sun = bpy.data.lights.new('sun', 'SUN')
    sun.energy = 3.5
    so = bpy.data.objects.new('sun', sun)
    so.rotation_euler = (math.radians(50), 0, math.radians(35))
    scene.collection.objects.link(so)
    gm = bpy.data.meshes.new('ground')
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=30)
    bm.to_mesh(gm)
    bm.free()
    g = bpy.data.objects.new('ground', gm)
    gmat = bpy.data.materials.new('groundmat')
    gmat.use_nodes = True
    gmat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (0.32, 0.31, 0.29, 1)
    gmat.node_tree.nodes['Principled BSDF'].inputs['Roughness'].default_value = 0.8
    gm.materials.append(gmat)
    scene.collection.objects.link(g)
    # bounding box of the vehicle (Blender space)
    mn = Vector((1e9, 1e9, 1e9))
    mx = Vector((-1e9, -1e9, -1e9))
    for ob in objs:
        for v in ob.data.vertices:
            co = ob.matrix_world @ v.co
            mn = Vector(map(min, mn, co))
            mx = Vector(map(max, mx, co))
    c = (mn + mx) / 2
    size = (mx - mn).length
    cam = bpy.data.cameras.new('cam')
    cam.lens = 50
    co = bpy.data.objects.new('cam', cam)
    co.location = c + Vector((size * 0.95, -size * 1.15, size * 0.42))
    scene.collection.objects.link(co)
    tgt = bpy.data.objects.new('tgt', None)
    tgt.location = c
    scene.collection.objects.link(tgt)
    con = co.constraints.new('TRACK_TO')
    con.target = tgt
    con.track_axis = 'TRACK_NEGATIVE_Z'
    con.up_axis = 'UP_Y'
    scene.camera = co
    os.makedirs(PREVIEWS, exist_ok=True)
    scene.render.filepath = os.path.join(os.path.abspath(PREVIEWS), vid + '.png')
    try:
        bpy.ops.render.render(write_still=True)
    except Exception as e:  # EEVEE needs a GPU context; fall back to Cycles on the CPU
        print('[vehicles] EEVEE render failed, using Cycles:', e)
        scene.render.engine = 'CYCLES'
        scene.cycles.samples = 48
        bpy.ops.render.render(write_still=True)


def place_wheels_for_preview(vid, wheel_obj, caliper_obj, wdata):
    """Instances of the wheel at the real positions so the preview shows a complete vehicle."""
    defs = {
        'hatch': (0.76, [1.26, -1.22], False),
        'rally': (0.82, [1.38, -1.3], False),
        'pickup': (0.86, [1.62, -1.62], False),
        'truck': (1.0, [2.45, -2.15], True),
    }
    x, zs, dual = defs[vid]
    r, w = wdata['r'], wdata['w']
    out = []
    for z in zs:
        for sx in (1, -1):
            for extra in ((0,) if not (dual and z < 0) else (0, w + 0.04)):
                ob = wheel_obj.copy()
                bpy.context.scene.collection.objects.link(ob)
                xx = sx * (x - extra)
                ob.matrix_world = TO_BLENDER @ Matrix.Translation((xx, r, z)) @ Matrix.Diagonal((sx, 1, 1, 1))
                out.append(ob)
    return out


def main():
    for vid, fn in BUILDERS.items():
        if ONLY and vid not in ONLY:
            continue
        reset_scene()
        objs, wdata, meta = fn()
        objs = dict(objs)
        if wdata:
            st = wdata['style']
            style = 'car' if st == 'car' else ('offroad' if st.startswith('offroad') else 'truck')
            objs['Wheel'] = wheel('Wheel', wdata['r'], wdata['w'], style, wdata.get('spokes', 5))
            objs['Caliper'] = caliper('Caliper', wdata['r'], wdata['w'], style)
            objs['Wheel_LOD1'] = wheel_low('Wheel_LOD1', wdata['r'], wdata['w'], style)
        for nm in ('Body', 'Blade'):
            if nm in objs:
                objs[nm + '_LOD1'] = lod_copy(objs[nm], nm + '_LOD1', 0.14 if nm == 'Body' else 0.3)
        export(vid, list(objs.values()))
        if PREVIEWS:
            extra = []
            if wdata:
                # export() already moved Wheel into Blender space; build preview instances from game space copies
                w = objs['Wheel']
                w.data.transform(TO_BLENDER.inverted())
                extra = place_wheels_for_preview(vid, w, objs['Caliper'], wdata)
                w.hide_render = True
                objs['Caliper'].hide_render = True
            for nm, o in objs.items():
                if nm.endswith('_LOD1'):
                    o.hide_render = True
            render_preview(vid, [o for o in objs.values() if not o.hide_render] + extra, None)


main()
