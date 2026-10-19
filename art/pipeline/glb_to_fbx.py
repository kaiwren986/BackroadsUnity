"""Convert one of today's browser-game GLBs to FBX for Unity, names preserved (card T2.9 step 1, PLAN 4.5 A1).

  blender -b --factory-startup --python art/pipeline/glb_to_fbx.py -- \
      --in <truck.glb> --out art/vehicles/truck/VH_Truck.fbx \
      [--unity-dir Game/Assets/Art/Vehicles/Truck] [--glb-name truck.glb] [--source "<where the GLB came from>"]

The GLB is read with Blender's own glTF importer (glTF +Y up -> Blender +Z up, so a game point (gx, gy, gz) lands at
Blender (gx, -gz, gy)), then written with the shared 07 section 3.2 flags (br_fbx.export_fbx), so Unity sees the same
node names, transforms, bounds and material slots as glTFast does with the GLB (test Art.TruckFbxMatchesGlb).

Nothing is renamed, merged, decimated or re-triangulated: object names are the glTF node names, material slots are the
glTF materials in primitive order. The script fails if Blender changed a name.

--unity-dir also copies the FBX (and, with --glb-name, the untouched GLB) into the Unity project.
A report (<out>.report.json) lists every node with its triangles, materials and Unity-space bounds.
"""
import argparse
import hashlib
import json
import os
import shutil
import struct
import sys

import bpy

sys.dont_write_bytecode = True  # no __pycache__ in art/pipeline
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from br_fbx import export_fbx  # noqa: E402


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def read_glb_json(path):
    with open(path, 'rb') as f:
        magic, version, _length = struct.unpack('<4sII', f.read(12))
        if magic != b'glTF' or version != 2:
            raise SystemExit('glb_to_fbx: %s is not a glTF 2.0 binary' % path)
        clen, ctype = struct.unpack('<I4s', f.read(8))
        if ctype != b'JSON':
            raise SystemExit('glb_to_fbx: first GLB chunk is not JSON')
        return json.loads(f.read(clen).decode('utf-8'))


def expected_nodes(gltf):
    """{node name: [material names in primitive order]} for every node of the default scene (depth first)."""
    out = {}
    scene = gltf['scenes'][gltf.get('scene', 0)]

    def walk(i):
        n = gltf['nodes'][i]
        mats = []
        if 'mesh' in n:
            for p in gltf['meshes'][n['mesh']]['primitives']:
                mats.append(gltf['materials'][p['material']]['name'] if 'material' in p else '')
        out[n.get('name', 'node%d' % i)] = mats
        for c in n.get('children', []):
            walk(c)

    for r in scene['nodes']:
        walk(r)
    return out


def unity_bounds(ob):
    """World-space AABB of the evaluated mesh in Unity space (Unity = (-bx, bz, -by)), metres."""
    deps = bpy.context.evaluated_depsgraph_get()
    me = ob.evaluated_get(deps).to_mesh()
    try:
        mw = ob.matrix_world
        pts = [mw @ v.co for v in me.vertices]
        tris = sum(len(p.vertices) - 2 for p in me.polygons)
    finally:
        ob.evaluated_get(deps).to_mesh_clear()
    if not pts:
        return None, 0
    us = [(-p.x, p.z, -p.y) for p in pts]
    lo = [round(min(u[k] for u in us), 6) for k in range(3)]
    hi = [round(max(u[k] for u in us), 6) for k in range(3)]
    return {'min': lo, 'max': hi}, tris


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument('--in', dest='src', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--unity-dir', default='')
    ap.add_argument('--glb-name', default='')
    ap.add_argument('--source', default='')
    a = ap.parse_args(argv)
    src = os.path.abspath(a.src)
    out = os.path.abspath(a.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)

    gltf = read_glb_json(src)
    want = expected_nodes(gltf)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = 'METRIC'
    scene.unit_settings.scale_length = 1.0
    bpy.ops.import_scene.gltf(filepath=src, merge_vertices=False, import_shading='NORMALS')

    objs = [ob for ob in scene.objects if ob.type in ('MESH', 'EMPTY')]
    got = {ob.name: [s.material.name if s.material else '' for s in ob.material_slots] for ob in objs}
    problems = []
    if sorted(got) != sorted(want):
        problems.append('node names changed: glTF %s, Blender %s' % (sorted(want), sorted(got)))
    for name, mats in want.items():
        if name in got and got[name] != mats:
            problems.append('%s: material slots changed: glTF %s, Blender %s' % (name, mats, got[name]))
    if problems:
        raise SystemExit('glb_to_fbx: ' + '; '.join(problems))

    export_fbx(out, objs)

    nodes = []
    for ob in sorted(objs, key=lambda o: o.name):
        b, tris = unity_bounds(ob) if ob.type == 'MESH' else (None, 0)
        nodes.append({'name': ob.name, 'type': ob.type, 'mesh': ob.data.name if ob.data else None,
                      'parent': ob.parent.name if ob.parent else None, 'tris': tris,
                      'materials': got[ob.name], 'unityBounds': b})
    report = {
        'tool': 'art/pipeline/glb_to_fbx.py', 'blender': bpy.app.version_string,
        'source': a.source or src, 'glbSha256': sha256(src),
        'fbx': os.path.basename(out), 'fbxSha256': sha256(out),
        'nodes': nodes,
    }
    rep = os.path.splitext(out)[0] + '.report.json'
    with open(rep, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(report, f, indent=2)
        f.write('\n')

    if a.unity_dir:
        ud = os.path.abspath(a.unity_dir)
        os.makedirs(ud, exist_ok=True)
        shutil.copyfile(out, os.path.join(ud, os.path.basename(out)))
        if a.glb_name:
            shutil.copyfile(src, os.path.join(ud, a.glb_name))
    print('glb_to_fbx: wrote %s (%d nodes, %d tris) from %s' % (out, len(nodes), sum(n['tris'] for n in nodes), src))
    for n in nodes:
        print('  %-12s tris %6d  mats %d  %s' % (n['name'], n['tris'], len(n['materials']), n['unityBounds']))


if __name__ == '__main__':
    main(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
