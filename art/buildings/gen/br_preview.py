"""Quick look at generated buildings in Blender (Workbench, palette texture), for iterating on the generator.

  blender -b --factory-startup --python art/buildings/gen/br_preview.py -- --instances art/buildings/millbrook/instances.json \
      --only way/1,way/2 --png out.png [--lod 0] [--palette <png>] [--angle 35] [--size 1600x900]

The review renders in the handoff come from Unity (BuildingCapture), with the game's lights; this one is for speed.
"""
import json
import math
import os
import sys
import tempfile

import bpy
from mathutils import Vector

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, '..', '..', 'pipeline'))
import br_buildings as W  # noqa: E402
import build  # noqa: E402
import palette  # noqa: E402


def main(argv):
    args = dict(zip(argv[::2], argv[1::2]))
    recs = json.load(open(args['--instances'], encoding='utf-8'))['buildings']
    only = [s for s in args.get('--only', '').split(',') if s]
    if only:
        recs = sorted([r for r in recs if r['osm'] in only], key=lambda r: only.index(r['osm']))
    lod = int(args.get('--lod', '0'))
    bpy.ops.wm.read_factory_settings(use_empty=True)
    png = args.get('--palette') or os.path.join(tempfile.gettempdir(), 'br_palette_preview.png')
    if not args.get('--palette'):
        palette.write_png(png)
    img = bpy.data.images.load(png)
    atlas = bpy.data.materials.new('M_Bld_Atlas')
    atlas.use_nodes = True
    nt = atlas.node_tree
    tex = nt.nodes.new('ShaderNodeTexImage')
    tex.image = img
    tex.interpolation = 'Closest'
    nt.links.new(tex.outputs['Color'], nt.nodes['Principled BSDF'].inputs['Base Color'])
    nt.nodes.active = tex
    glass = W.material('M_Bld_Glass', palette.rgb(palette.GLASS), 0.1)
    mats = {'atlas': atlas, 'glass': glass}
    x = 0.0
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for rec in recs:
        B = build.Building(rec, palette)
        body, panes = B.build_lod(lod)
        xs = [p[0] for p in rec['poly']]
        w = max(xs) - min(xs)
        off = x - min(xs)
        obs = [W.mesh_object('Body_' + rec['file'], body, mats['atlas'], False)]
        if panes.faces:
            obs.append(W.mesh_object('Panes_' + rec['file'], panes, mats['glass'], True))
        for ob in obs:
            ob.location = W.to_blender((off, 0, 0))
            for v in ob.data.vertices:
                p = v.co + ob.location
                lo = Vector(map(min, lo, p))
                hi = Vector(map(max, hi, p))
        x += w + 6.0
    # ground
    bpy.ops.mesh.primitive_plane_add(size=1, location=((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z - 0.01))
    g = bpy.context.active_object
    g.scale = ((hi.x - lo.x) + 40, (hi.y - lo.y) + 40, 1)
    gm = bpy.data.materials.new('Ground')
    gm.diffuse_color = (0.33, 0.36, 0.28, 1)
    g.data.materials.append(gm)
    c = (lo + hi) / 2
    size = (hi - lo).length
    ang = math.radians(float(args.get('--angle', '35')))
    cam_data = bpy.data.cameras.new('Cam')
    cam_data.lens = 35
    cam = bpy.data.objects.new('Cam', cam_data)
    bpy.context.scene.collection.objects.link(cam)
    d = size * float(args.get('--dist', '0.95'))
    side = -1.0 if args.get('--side', 'south') == 'south' else 1.0   # town z (north) is Blender -y
    cam.location = c + Vector((math.sin(ang) * d * 0.5, -side * math.cos(ang) * d, d * 0.45))
    direction = c - cam.location
    cam.rotation_euler = direction.to_track_quat('-Z', 'Y').to_euler()
    sc = bpy.context.scene
    sc.camera = cam
    sc.render.engine = 'BLENDER_WORKBENCH'
    sh = sc.display.shading
    sh.light = 'STUDIO'
    sh.color_type = 'TEXTURE'
    sh.show_shadows = True
    sh.show_cavity = True
    sh.cavity_type = 'WORLD'
    sc.display.shadow_focus = 0.6
    world = bpy.data.worlds.new('Sky')
    world.color = (0.62, 0.68, 0.76)
    sc.world = world
    w, h = (int(v) for v in args.get('--size', '1600x900').split('x'))
    sc.render.resolution_x, sc.render.resolution_y = w, h
    sc.render.filepath = os.path.abspath(args['--png'])
    sc.render.image_settings.file_format = 'PNG'
    bpy.ops.render.render(write_still=True)
    print('br_preview: wrote', sc.render.filepath)


if __name__ == '__main__':
    main(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
