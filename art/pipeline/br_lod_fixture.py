"""Test fixture for the Unity import rules (card T2.9, test Art.LodGroupFromNames).

Writes VH_LodFixture.fbx with the shared 07 section 3.2 flags:
  Crate_LOD0..2   a 1 m crate at three levels (cube subdivided 2, 1, 0 times), material M_Paint
  Lid_LOD0..1     a lid on top, two levels, material M_Trim: same-suffix siblings join one LOD level
  PART_Door       an empty (hinge) whose children Door_LOD0..1 get their own LOD Group, material M_Glass
  UCX_Crate_00    a convex collision hull: MeshCollider, renderer removed
Material names are the project slot names, so the remap is the identity one phase B uses (07 section 4.3).

  blender -b --factory-startup --python art/pipeline/br_lod_fixture.py -- --out art/fixtures/VH_LodFixture.fbx \
      [--unity-dir Game/Assets/Art/Vehicles/_Fixtures]
"""
import argparse
import os
import shutil
import sys

import bpy

sys.dont_write_bytecode = True  # no __pycache__ in art/pipeline
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from br_fbx import export_fbx, game_to_blender  # noqa: E402


def material(name):
    return bpy.data.materials.get(name) or bpy.data.materials.new(name)


def box(name, size, at_game, subdiv, mat, parent=None):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0, 0, 0))
    ob = bpy.context.active_object
    ob.name = name
    ob.data.name = name
    ob.scale = (size[0], size[2], size[1])           # game (x, y, z) extents -> Blender (x, -z, y)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if subdiv:
        bpy.ops.object.mode_set(mode='EDIT')
        bpy.ops.mesh.subdivide(number_cuts=subdiv)
        bpy.ops.object.mode_set(mode='OBJECT')
    if mat:
        ob.data.materials.append(material(mat))
    ob.parent = parent                                # local position under the parent (no parent inverse)
    ob.location = game_to_blender(at_game)
    return ob


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--unity-dir', default='')
    a = ap.parse_args(argv)
    out = os.path.abspath(a.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = 'METRIC'
    scene.unit_settings.scale_length = 1.0

    objs = []
    for i, cuts in enumerate((3, 1, 0)):
        objs.append(box('Crate_LOD%d' % i, (1, 1, 1), (0, 0.5, 0), cuts, 'M_Paint'))
    for i, cuts in enumerate((2, 0)):
        objs.append(box('Lid_LOD%d' % i, (1.1, 0.1, 1.1), (0, 1.05, 0), cuts, 'M_Trim'))

    bpy.ops.object.empty_add(type='PLAIN_AXES', location=game_to_blender((0.5, 0.5, 0.6)))
    hinge = bpy.context.active_object
    hinge.name = 'PART_Door'
    objs.append(hinge)
    for i, cuts in enumerate((2, 0)):
        objs.append(box('Door_LOD%d' % i, (0.6, 0.8, 0.05), (-0.3, 0, 0), cuts, 'M_Glass', parent=hinge))

    objs.append(box('UCX_Crate_00', (1, 1, 1), (0, 0.5, 0), 0, None))

    export_fbx(out, objs)
    if a.unity_dir:
        ud = os.path.abspath(a.unity_dir)
        os.makedirs(ud, exist_ok=True)
        shutil.copyfile(out, os.path.join(ud, os.path.basename(out)))
    print('br_lod_fixture: wrote %s (%d objects)' % (out, len(objs)))
    for ob in objs:
        tris = sum(len(p.vertices) - 2 for p in ob.data.polygons) if ob.type == 'MESH' else 0
        print('  %-14s parent=%-10s tris=%d' % (ob.name, ob.parent.name if ob.parent else '-', tris))


if __name__ == '__main__':
    main(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
