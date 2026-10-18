"""Axis canary (PLAN T0.8, docs/research/07-art-pipeline.md section 3.2).

Exports CANARY.fbx: three 10 cm cubes whose origins sit at game coordinates
  MARK_Front (0, 0, 2), MARK_Left (1, 0, 0), MARK_Up (0, 1, 0)
using the same FBX flags as every vehicle export. The EditMode test Art.Canary checks the import in Unity:
MARK_Front.z > 0, MARK_Left.x < 0 (Unity +X is right), MARK_Up.y > 0, root rotation identity, scale 1.

Game space (three.js, right-handed, +x = left) -> Unity = (-gx, gy, gz); from Blender space Unity = (-bx, bz, -by),
so a game point (gx, gy, gz) is Blender (gx, -gz, gy).

Run headless (no user add-ons or preferences):
  blender --background --factory-startup --python art/pipeline/br_canary.py -- --out art/canary/CANARY.fbx
"""
import argparse
import os
import sys

import bpy

MARKERS = {
    "MARK_Front": (0.0, 0.0, 2.0),
    "MARK_Left": (1.0, 0.0, 0.0),
    "MARK_Up": (0.0, 1.0, 0.0),
}
SIZE = 0.10  # metres


def game_to_blender(g):
    gx, gy, gz = g
    return (gx, -gz, gy)


def export_fbx(path, objs):
    """07 section 3.2 flags, unchanged (the canary exists to prove them)."""
    bpy.ops.object.select_all(action='DESELECT')
    for ob in objs:
        ob.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={'EMPTY', 'MESH'},
        apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',   # 1 Blender m = 1 Unity unit, scale 1
        axis_forward='-Z', axis_up='Y', bake_space_transform=True,   # "Apply Transform": no -90 deg root rotation
        use_mesh_modifiers=True, mesh_smooth_type='OFF',             # write normals only; Unity imports them
        use_triangles=True, use_tspace=True,                         # same triangulation and tangents as the bake
        colors_type='LINEAR',                                        # masks in FLOAT_COLOR attributes stay numeric
        use_custom_props=True, add_leaf_bones=False, bake_anim=False,
        path_mode='STRIP', embed_textures=False)                     # textures are written separately as PNG


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    a = ap.parse_args(argv)
    out = os.path.abspath(a.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = 'METRIC'
    scene.unit_settings.scale_length = 1.0

    objs = []
    for name, g in MARKERS.items():
        bpy.ops.mesh.primitive_cube_add(size=SIZE, location=game_to_blender(g))
        ob = bpy.context.active_object
        ob.name = name
        ob.data.name = name + "_Mesh"
        objs.append(ob)

    export_fbx(out, objs)
    print("br_canary: wrote %s (%d markers)" % (out, len(objs)))
    for ob in objs:
        print("  %s blender=%s" % (ob.name, tuple(round(v, 4) for v in ob.location)))


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv)
