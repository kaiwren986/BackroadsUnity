"""Blender entry of the building generator (card T3.4): plan, build and write one FBX per building.

  blender -b --factory-startup --python art/buildings/gen/br_buildings.py -- \
      --town TownData/<town>/v1 --profile Game/Assets/Content/sites/buildingProfile.json \
      --plan-out art/buildings/millbrook --out-dir Game/Assets/Art/Buildings/Millbrook \
      [--doors <sites-S3 doors.json>] [--only way/1,way/2] [--index-out <dir>] [--palette-png <png>]
  (art/buildings/gen/run.ps1 finds the town folder and Blender for you.)

Each FBX (A1 pipeline flags, art/pipeline/br_fbx.py, deterministic bytes) holds, at the building origin (footprint
centroid at floor height, TownFrame axes = Unity axes, no rotation):
  Body_LOD0..2   opaque geometry on the palette atlas, material M_Bld_Atlas
  Panes_LOD0..2  window and door glass, material M_Bld_Glass; vertex colour per pane = (id, storey / 8, kind, 1):
                 id a stable random in 0.02..0.98 per pane, kind 0 window, 0.5 door glass, 1 shop window (M5 night lights)
  UCX_<file>_NN  convex collision hulls (BackroadsModelPostprocessor: convex MeshCollider, no renderer)
  DOOR_<Kind>_NN empties at each door opening's sill centre on the wall face; NN is the door spec's index for this
                 building, custom props kind, width, height, spec
"""
import hashlib
import json
import os
import sys

import bpy

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, '..', '..', 'pipeline'))
import build  # noqa: E402
import palette  # noqa: E402
import plan as planner  # noqa: E402
from br_fbx import export_fbx  # noqa: E402

GENERATOR_VERSION = 1
ROUND = 5


def to_blender(p):
    """Town/Unity frame (x east, y up, z north) to Blender (Z up). The FBX export maps it back (Art.Canary)."""
    x, y, z = p
    return (round(-x, ROUND) + 0.0, round(-z, ROUND) + 0.0, round(y, ROUND) + 0.0)


def material(name, rgb, rough):
    m = bpy.data.materials.get(name)
    if m is None:
        m = bpy.data.materials.new(name)
        m.diffuse_color = (rgb[0] / 255.0, rgb[1] / 255.0, rgb[2] / 255.0, 1.0)
        m.roughness = rough
    return m


def mesh_object(name, mb, mat, with_colour):
    verts, faces, uvs, cols = [], [], [], []
    for pts, uv, col in mb.faces:
        base = len(verts)
        bp = [to_blender(p) for p in pts][::-1]          # the axis map is a reflection: reverse to keep normals out
        verts.extend(bp)
        faces.append(tuple(range(base, base + len(bp))))
        if uv is not None:
            uvs.extend(list(reversed(uv)))
        else:
            q = [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)] if len(pts) == 4 else [(0.0, 0.0)] * len(pts)
            uvs.extend(list(reversed(q)))
        cols.extend([col or (1.0, 1.0, 1.0, 1.0)] * len(pts))
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    uvl = me.uv_layers.new(name='UVMap')
    for i, uv in enumerate(uvs):
        uvl.data[i].uv = (round(uv[0], 6), round(uv[1], 6))
    if with_colour:
        ca = me.color_attributes.new('Col', 'FLOAT_COLOR', 'CORNER')
        for i, c in enumerate(cols):
            ca.data[i].color = c
    for p in me.polygons:
        p.use_smooth = False
    if mat is not None:
        me.materials.append(mat)
    me.validate(verbose=False)
    me.update()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def hull_object(name, faces):
    verts, idx = [], []
    for f in faces:
        base = len(verts)
        verts.extend([to_blender(p) for p in f][::-1])
        idx.append(tuple(range(base, base + len(f))))
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], idx)
    me.validate(verbose=False)
    me.update()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def clear_scene():
    for ob in list(bpy.data.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    for me in list(bpy.data.meshes):
        bpy.data.meshes.remove(me)


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def write_building(rec, out_dir, mats):
    clear_scene()
    B, first = build.make(rec, palette)
    objs, lods = [], []
    for lod in (0, 1, 2):
        body, panes = first if lod == 0 else B.build_lod(lod)
        objs.append(mesh_object('Body_LOD%d' % lod, body, mats['atlas'], False))
        if panes.faces:
            objs.append(mesh_object('Panes_LOD%d' % lod, panes, mats['glass'], True))
        lods.append({'body': body.tris, 'panes': panes.tris, 'paneCount': len(panes.faces)})
    hulls = B.colliders()
    for k, faces in enumerate(hulls):
        objs.append(hull_object('UCX_%s_%02d' % (rec['file'], k), faces))
    for name, pos, props in B.markers:
        ob = bpy.data.objects.new(name, None)
        ob.empty_display_type = 'ARROWS'
        ob.location = to_blender(pos)
        for k, v in props.items():
            ob[k] = v
        bpy.context.scene.collection.objects.link(ob)
        objs.append(ob)
    path = os.path.join(out_dir, rec['file'] + '.fbx')
    export_fbx(path, objs, tangents=False)
    tops = [y for r in B.rects for y in (r.ridge_h,)] or [B.top]
    porch = None
    if B.porch_geom is not None:
        # the porch and its steps stand outside the map's footprint: town-frame outline for the site plan (lane S)
        wf, p0, p1, D, gy, y_att = B.porch_geom
        ox, oz = rec['origin'][0], rec['origin'][2]
        steps = 0.28 * max(1, int(-gy / 0.19))
        pts = [wf.P(p0 - 0.15, 0, 0), wf.P(p1 + 0.15, 0, 0), wf.P(p1 + 0.15, 0, D + 0.25 + steps), wf.P(p0 - 0.15, 0, D + 0.25 + steps)]
        porch = [[round(p[0] + ox, 3), round(p[2] + oz, 3)] for p in pts]
    return {
        'osm': rec['osm'], 'part': rec['part'], 'model': rec['model'], 'family': rec['family'], 'business': rec['business'],
        'fbx': rec['file'] + '.fbx', 'origin': rec['origin'], 'storeys': rec['storeys'], 'roof': B.roof_shape,
        'eave': round(B.eave, 3), 'height': round(max(tops), 3), 'groundMin': rec['ground']['min'], 'groundMax': rec['ground']['max'],
        'lod0Tris': lods[0]['body'] + lods[0]['panes'], 'lod1Tris': lods[1]['body'] + lods[1]['panes'],
        'lod2Tris': lods[2]['body'] + lods[2]['panes'], 'panes': lods[0]['paneCount'], 'colliders': len(hulls), 'detail': B.detail,
        'doors': [{'name': n, 'kind': p['kind'], 'spec': p['spec'], 'width': p['width'], 'local': [round(c, 4) for c in pos]} for n, p, pos in
                  [(m[0], m[2], m[1]) for m in B.markers]],
        'porch': porch,
        'sha256': sha256(path), 'bytes': os.path.getsize(path),
    }


def main(argv):
    args = {}
    k = 0
    while k < len(argv):
        args[argv[k]] = argv[k + 1] if k + 1 < len(argv) else ''
        k += 2
    town = planner.load_town(args['--town'])
    prof = planner.Profile(args['--profile'])
    P = planner.Planner(town, prof, args.get('--doors'))
    recs, specs = P.run()
    if '--plan-out' in args:
        planner.main(['--town', args['--town'], '--profile', args['--profile'], '--out', args['--plan-out']] +
                     (['--doors', args['--doors']] if args.get('--doors') else []))
    only = set(s for s in args.get('--only', '').split(',') if s)
    out_dir = os.path.abspath(args['--out-dir'])
    os.makedirs(out_dir, exist_ok=True)
    if args.get('--palette-png'):
        palette.write_png(args['--palette-png'])
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.unit_settings.system = 'METRIC'
    sc.unit_settings.scale_length = 1.0
    mats = {'atlas': material('M_Bld_Atlas', (200, 200, 200), 0.85), 'glass': material('M_Bld_Glass', palette.rgb(palette.GLASS), 0.1)}
    entries = []
    for rec in recs:
        if only and rec['osm'] not in only:
            continue
        entries.append(write_building(rec, out_dir, mats))
    index = {
        'format': 'backroads-building-models', 'version': 1, 'generator': 'art/buildings/gen (version %d)' % GENERATOR_VERSION,
        'town': town.manifest.get('townName', 'Millbrook'),
        'frame': 'TownFrame local metres = Unity axes (x east, y up, z north). Place each FBX at origin, no rotation.',
        'doors': 'sites-S3' if P.doors_src else 'placeholder',
        'doorsFile': P.doors_src or (args['--plan-out'].replace('\\', '/').rstrip('/') + '/doors.placeholder.json' if '--plan-out' in args else None),
        'fallbackDoors': P.fallback_doors,
        'lods': 'Body_LOD0..2 + Panes_LOD0..2 (one LOD Group on the root, BackroadsModelPostprocessor)',
        'buildings': entries,
    }
    index_dir = os.path.abspath(args.get('--index-out', out_dir))
    os.makedirs(index_dir, exist_ok=True)
    with open(os.path.join(index_dir, 'buildings.index.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(index, f, indent=1)
        f.write('\n')
    counts = {}
    for e in entries:
        counts[e['model']] = counts.get(e['model'], 0) + 1
    print('br_buildings: wrote %d FBX to %s %s' % (len(entries), out_dir, json.dumps(counts, sort_keys=True)))


if __name__ == '__main__':
    main(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
