"""Shared FBX export for every Blender-to-Unity file (docs/research/07-art-pipeline.md section 3.2).

One function, so the axis canary (br_canary.py, test Art.Canary) proves exactly the flags every other export uses.
Unity's side is BackroadsModelPostprocessor: file scale 1, bakeAxisConversion off (Blender bakes the space transform).
Use one of the two, never both.
"""
import datetime
import hashlib

import bpy

FIXED_TIME = datetime.datetime(2000, 1, 1)


def _stable_hash(key):
    return int.from_bytes(hashlib.blake2b(repr(key).encode('utf-8'), digest_size=8).digest(), 'little', signed=True)


def _deterministic_exporter():
    """Two exports of the same scene differ only in the header's CreationTimeStamp and in the FBX object ids, which
    Blender derives from Python's per-process randomised str hash. Pin both, so re-running a converter on unchanged
    input writes the same bytes (and no new LFS object). Unity does not read either: its fileIDs come from names."""
    try:
        from io_scene_fbx import export_fbx_bin as m, fbx_utils as u
    except ImportError:
        return
    u.hash = _stable_hash  # module global: shadows the builtin inside fbx_utils only (_key_to_uuid uses hash(key))
    orig = m.fbx_header_elements
    if getattr(orig, '_br_fixed', False):
        return

    def header(root, scene_data, time=None):
        return orig(root, scene_data, FIXED_TIME)

    header._br_fixed = True
    m.fbx_header_elements = header


def export_fbx(path, objs):
    """07 section 3.2 flags, unchanged (the canary exists to prove them)."""
    _deterministic_exporter()
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


def game_to_blender(g):
    """Game space (three.js, right-handed, +x = left) to Blender (Z up): a game point (gx, gy, gz) is (gx, -gz, gy).
    Unity = (-gx, gy, gz) after the export above."""
    gx, gy, gz = g
    return (gx, -gz, gy)
