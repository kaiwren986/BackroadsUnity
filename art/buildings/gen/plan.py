"""Building instances for the generator (card T3.4 step 2 input): one record per building of the five modelled types.

Reads the town extract (TownData/<town>/v1: buildings, roads, sites, water, DEM), the measured building profile
(Game/Assets/Content/sites/buildingProfile.json) and the door specs (lane S's sites-S3 output when it exists, else our
placeholder doors written here by the design's rules, site-layouts 7a). Every choice is drawn from the building's own
seed, blake2b-64(TOWN_ID | osm id | part), one independent draw per named question, so a building always gets the same
answer and editing one never changes another.

Order of evidence (site-layouts 7a, research 08b section 6): the building's own OSM tags, then the North American
numbers of its profile key when that key has n >= 20, then the type key; eave heights and pitches from the [doc] table
of 08b section 5. Every instance records where each number came from (field "why").

  blender -b --factory-startup --python art/buildings/gen/plan.py -- --town TownData/<town>/v1 \
      --profile Game/Assets/Content/sites/buildingProfile.json --out art/buildings/millbrook [--doors <S3 doors.json>]
  (plain python works too: python art/buildings/gen/plan.py --town ... )
"""
import array
import hashlib
import json
import math
import os
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bgeom as G  # noqa: E402

PLAN_VERSION = 1
DOOR_KINDS = ['Main', 'Service', 'Garage', 'Dock', 'Bay', 'DriveThroughWindow']   # WorldBake.DoorKind order


# ------------------------------------------------------------------------------------------------ seeded draws

class Draw:
    """Independent draws per question: u('roof') never depends on how many other draws came before it."""

    def __init__(self, town_id, osm, part):
        self.base = ('%s|%s|%d' % (town_id, osm, part)).encode('utf-8')
        self.seed = hashlib.blake2b(self.base, digest_size=8).hexdigest()

    def u(self, key):
        h = hashlib.blake2b(self.base + b'#' + key.encode('utf-8'), digest_size=8).digest()
        return int.from_bytes(h, 'little') / 2.0 ** 64

    def range(self, key, lo, hi, step=0.01):
        v = lo + (hi - lo) * self.u(key)
        return round(round(v / step) * step, 4)

    def pick(self, key, weighted):
        """weighted: list of (value, weight) in a fixed order."""
        total = sum(w for _, w in weighted)
        x = self.u(key) * total
        for v, w in weighted:
            x -= w
            if x < 0:
                return v
        return weighted[-1][0]

    def chance(self, key, p):
        return self.u(key) < p


# ------------------------------------------------------------------------------------------------ town data

class Town:
    def __init__(self, town_dir):
        self.dir = os.path.abspath(town_dir)
        self.id = os.path.basename(os.path.dirname(self.dir.rstrip('\\/')))
        J = lambda f: json.load(open(os.path.join(self.dir, f), encoding='utf-8'))
        self.manifest = J('manifest.json')
        self.buildings = J('buildings.json')['buildings']
        roads = J('roads.json')
        self.nodes = {n['id']: (n['x'], n['z']) for n in roads['nodes']}
        self.ways = {w['osm']: [self.nodes[i] for i in w['nodes'] if i in self.nodes] for w in roads['ways']}
        sites = J('sites.json')
        self.service = [(s['osm'], s['tags'], [tuple(p) for p in s.get('pts', [])]) for s in sites.get('service', [])]
        water = J('water.json')
        self.weirs = []
        for s in water.get('structures', []):
            t = s.get('tags', {})
            if t.get('waterway') in ('weir', 'dam') or t.get('lock') == 'yes':
                for part in s.get('parts', []):
                    self.weirs.append([tuple(p) for p in part])
            if t.get('waterway') == 'lock_gate' and 'p' in s:
                self.weirs.append([tuple(s['p'])])
        dem = J('dem/dem.json')
        self.dem_n = dem['n']
        self.dem_x0, self.dem_z0, self.dem_res = dem['x0'], dem['z0'], dem['res']
        self.dem = array.array('f')
        with open(os.path.join(self.dir, 'dem', dem['file']), 'rb') as f:
            self.dem.frombytes(f.read())
        if sys.byteorder != 'little':
            self.dem.byteswap()
        if len(self.dem) != self.dem_n * self.dem_n:
            raise SystemExit('DEM size mismatch (is the LFS file checked out?)')

    def ground(self, x, z):
        fx = (x - self.dem_x0) / self.dem_res
        fz = (z - self.dem_z0) / self.dem_res
        n = self.dem_n
        i = max(0, min(n - 2, int(math.floor(fx))))
        j = max(0, min(n - 2, int(math.floor(fz))))
        tx, tz = min(1.0, max(0.0, fx - i)), min(1.0, max(0.0, fz - j))
        d = self.dem
        a = d[j * n + i] * (1 - tx) + d[j * n + i + 1] * tx
        b = d[(j + 1) * n + i] * (1 - tx) + d[(j + 1) * n + i + 1] * tx
        return a * (1 - tz) + b * tz


def _rect(x0, z0, w, d):
    return [[x0, z0], [x0 + w, z0], [x0 + w, z0 + d], [x0, z0 + d]]


# Synthetic buildings for the types the town lacks (no barn, warehouse or downtown row in the extract), so every
# generator path is built, previewed and tested: fixture FBXs in Game/Assets/Art/Buildings/_Fixtures/.
# (id, business, type, form, tags, polygon CCW, setback); a street runs along z = 0, every building faces it.
FIXTURES = [
    ('fixture/barn_gambrel', 'Farm', 'farm', None, {'building': 'barn', 'roof:shape': 'gambrel'}, _rect(-190, 30, 12, 20), 30),
    ('fixture/barn_gable', 'Farm', 'farm', None, {'building': 'barn', 'roof:shape': 'gabled'}, _rect(-170, 30, 11, 17), 30),
    ('fixture/machineshed', 'Accessory', 'accessory', None, {'building': 'shed'}, _rect(-150, 22, 24, 14), 22),
    ('fixture/warehouse_flat', 'Warehouse', 'warehouse', None, {'building': 'warehouse', 'roof:shape': 'flat'}, _rect(-120, 25, 48, 24), 25),
    ('fixture/warehouse_gable', 'Industrial', 'industrial', None, {'building': 'industrial', 'roof:shape': 'gabled'}, _rect(-66, 25, 30, 18), 25),
    ('fixture/row_shop', 'Retail', 'shop', 'streetfront', {'building': 'retail', 'building:levels': '2'}, _rect(0, 4, 8, 20), 2),
    ('fixture/row_restaurant', 'Restaurant', 'restaurant', 'streetfront', {'building': 'yes', 'amenity': 'restaurant', 'building:levels': '2'}, _rect(8, 4, 7, 20), 2),
    ('fixture/row_office', 'Office', 'office', 'streetfront', {'building': 'yes', 'office': 'yes', 'building:levels': '3'}, _rect(15, 4, 11, 20), 2),
    ('fixture/house_l', 'House', 'house', None, {'building': 'house', 'building:levels': '2', 'roof:shape': 'gabled'},
     [[40, 10], [52, 10], [52, 18], [47, 18], [47, 26], [40, 26]], 10),
    ('fixture/house_t', 'House', 'house', None, {'building': 'house', 'building:levels': '2', 'roof:shape': 'gabled'},
     [[62, 12], [74, 12], [74, 20], [70.5, 20], [70.5, 27], [65.5, 27], [65.5, 20], [62, 20]], 12),
    ('fixture/house_hip', 'House', 'house', None, {'building': 'house', 'building:levels': '2', 'roof:shape': 'hipped'}, _rect(84, 10, 10, 9), 10),
    ('fixture/cottage', 'House', 'house', None, {'building': 'house', 'building:levels': '1', 'roof:shape': 'gabled'}, _rect(102, 9, 9, 7), 9),
    ('fixture/garage', 'Accessory', 'accessory', None, {'building': 'garage'}, _rect(116, 14, 7, 6.5), 14),
    ('fixture/shed', 'Accessory', 'accessory', None, {'building': 'shed'}, _rect(128, 14, 3, 4), 14),
    ('fixture/mill', 'Mill', 'mill', None, {'building': 'industrial', 'craft': 'sawmill'},
     [[140, 12], [170, 12], [170, 28], [158, 28], [158, 36], [148, 36], [148, 28], [140, 28]], 12),
]


class FixtureTown:
    """A flat stand-in town for FIXTURES (same interface as Town)."""

    def __init__(self):
        self.id = 'fixture'
        self.manifest = {'townName': 'Fixtures'}
        self.ways = {'fixture/street': [(-300.0, 0.0), (300.0, 0.0)]}
        self.service = []
        self.weirs = []
        self.buildings = []
        for osm, bus, typ, form, tags, poly, setback in FIXTURES:
            b = {'osm': osm, 'business': bus, 'type': typ, 'tags': tags, 'poly': [list(map(float, p)) for p in poly],
                 'area': round(G.area(poly), 2), 'c': list(G.centroid([tuple(p) for p in poly])), 'setback': setback,
                 'street': 'fixture/street', 'inPlay': True}
            if form:
                b['form'] = form
            self.buildings.append(b)

    def ground(self, x, z):
        return 0.0


def dist_to_polyline(p, line):
    if len(line) == 1:
        return G.length(G.sub(p, line[0])), line[0]
    best = (1e18, None)
    for a, b in zip(line, line[1:]):
        t, d = G.seg_project(p, a, b)
        if d < best[0]:
            best = (d, G.lerp(a, b, t))
    return best


# ------------------------------------------------------------------------------------------------ profile

class Profile:
    def __init__(self, path):
        self.keys = json.load(open(path, encoding='utf-8'))['keys']

    def storeys(self, keys):
        """(histogram, key used, n) from the first key whose North American storey sample has n >= 20."""
        for k in keys:
            na = self.keys.get(k, {}).get('storeys', {}).get('na', {})
            if na.get('n', 0) >= 20:
                return {int(a): b for a, b in na['hist'].items()}, k, na['n']
        return None, None, 0

    def roof(self, keys):
        for k in keys:
            na = self.keys.get(k, {}).get('roofShape', {}).get('na', {})
            if na.get('n', 0) >= 20:
                return {a: b for a, b in na['shares'].items() if a != 'n'}, k, na['n']
        return None, None, 0


ROOF_MAP = {   # OSM roof:shape -> what the generator builds (08b section 6: "many" is complex, square hip-likes are hipped)
    'gabled': 'gable', 'many': 'gable', 'saltbox': 'gable', 'double_saltbox': 'gable', 'round': 'gable',
    'pyramidal': 'hip', 'quadruple_saltbox': 'hip', 'hipped': 'hip', 'half-hipped': 'hip', 'hipped-and-gabled': 'hip',
    'mansard': 'hip', 'gambrel': 'gambrel', 'flat': 'flat', 'skillion': 'shed', 'dome': 'hip',
}

# ------------------------------------------------------------------------------------------------ palettes (our swatch names)

HOUSE_WALL = [('siding_white', 30), ('siding_cream', 10), ('siding_yellow', 6), ('siding_sage', 6), ('siding_greyblue', 8),
              ('siding_grey', 8), ('siding_tan', 6), ('siding_red', 2), ('siding_teal', 3), ('siding_brown', 3),
              ('siding_green', 3), ('siding_blue', 4), ('siding_rose', 2), ('siding_slate', 3), ('siding_buff', 4),
              ('siding_olive', 2)]
HOUSE_TRIM = [('trim_white', 75), ('trim_cream', 10), ('trim_black', 5), ('trim_green', 5), ('trim_brown', 5)]
WHITE_TRIM = [('trim_white', 45), ('trim_black', 20), ('trim_green', 15), ('trim_navy', 10), ('trim_maroon', 10)]
SHUTTER = [('trim_black', 40), ('trim_green', 30), ('trim_maroon', 10), ('trim_navy', 10), ('trim_brown', 10)]
HOUSE_ROOF = [('shingle_charcoal', 30), ('shingle_grey', 25), ('shingle_black', 10), ('shingle_brown', 15),
              ('shingle_green', 7), ('shingle_red', 5), ('metal_galv', 4), ('metal_charcoal', 2), ('metal_red', 2)]
FOUND = [('stone_grey', 40), ('stone_tan', 20), ('brick_red', 20), ('concrete', 20)]
DOOR = [('door_red', 15), ('door_black', 20), ('door_green', 10), ('door_wood', 20), ('door_white', 20), ('door_blue', 10),
        ('door_grey', 5)]
BRICK = [('brick_red', 35), ('brick_dark', 15), ('brick_buff', 12), ('brick_brown', 12), ('brick_orange', 8),
         ('brick_cream', 6), ('brick_green', 4), ('brick_grey', 4), ('brick_maroon', 4)]
BLOCK_TRIM = [('stone_tan', 30), ('stone_grey', 20), ('trim_white', 20), ('trim_cream', 15), ('trim_black', 15)]
SHOPFRONT = [('trim_green', 25), ('trim_black', 25), ('trim_maroon', 15), ('trim_navy', 15), ('trim_brown', 10), ('trim_white', 10)]
SIGN = [('sign_cream', 35), ('sign_dark', 30), ('sign_green', 20), ('sign_red', 15)]
AWNING = [('awning_green', 30), ('awning_red', 25), ('awning_navy', 20), ('awning_tan', 15), ('awning_maroon', 10)]
FLAT_ROOF = [('roof_tar', 40), ('roof_membrane', 35), ('roof_gravel', 25)]
BARN_WALL = [('barn_red', 55), ('barn_white', 25), ('barn_weathered', 15), ('barn_board_dark', 5)]
METAL_ROOF = [('metal_galv', 40), ('metal_red', 12), ('metal_green', 14), ('metal_charcoal', 14), ('metal_white', 10),
              ('metal_blue', 4), ('metal_rust', 6)]
PANEL = [('panel_white', 30), ('panel_tan', 20), ('panel_green', 15), ('panel_red', 15), ('panel_grey', 15), ('panel_blue', 5)]
PANEL_TRIM = {'panel_white': 'trim_green', 'panel_tan': 'trim_brown', 'panel_green': 'trim_white', 'panel_red': 'trim_white',
              'panel_grey': 'trim_navy', 'panel_blue': 'trim_white'}
MILL_WALL = [('siding_white', 25), ('siding_cream', 15), ('barn_red', 10), ('barn_white', 10), ('brick_red', 25), ('brick_dark', 15)]


# ------------------------------------------------------------------------------------------------ classification

def model_of(b, near_weir):
    """(family, model) for the five card types, or None (stays a grey box, T3.3, until phase 2)."""
    bus, tags, form = b['business'], b.get('tags', {}), b.get('form')
    a = b['area']
    if bus == 'Mill' or (near_weir and a >= 300 and bus not in ('House', 'Accessory')):
        return 'mill', 'mill'
    if tags.get('amenity') == 'shelter':
        return None
    if bus == 'House':
        return 'house', 'house'
    if bus in ('Retail', 'Restaurant', 'Office', 'Civic') and form == 'streetfront':
        return 'block', 'block'
    if bus in ('Warehouse', 'Industrial'):
        return 'warehouse', 'warehouse'
    if bus == 'Farm' or tags.get('building') in ('barn', 'farm_auxiliary', 'cowshed', 'stable'):
        return 'barn', ('barn' if a >= 60 else 'shed')
    if bus == 'Accessory':
        if a >= 80:
            return 'barn', 'machineshed'
        if a >= 18 or b.get('sub') == 'garage' or tags.get('building') in ('garage', 'garages'):
            return 'barn', 'garage'
        return 'barn', 'shed'
    return None


# ------------------------------------------------------------------------------------------------ planner

class Planner:
    def __init__(self, town, profile, doors_path=None):
        self.town = town
        self.profile = profile
        self.doors_src = None
        self.doors_in = None
        if doors_path:
            d = json.load(open(doors_path, encoding='utf-8'))
            self.doors_in = d['doors'] if isinstance(d, dict) else d
            # repo-relative, with the town folder written as <town> (no real name in shipped files; the test resolves it)
            src = os.path.relpath(os.path.abspath(doors_path), os.getcwd()).replace('\\', '/')
            self.doors_src = src.replace('/%s/' % town.id, '/<town>/')
        self.fallback_doors = 0
        self.by_osm = {}
        for b in town.buildings:
            self.by_osm[(b['osm'], b.get('part', 0))] = b
        self.simple = {k: G.simplify(b['poly']) for k, b in self.by_osm.items()}

    # ---- helpers on one building

    def street_dir(self, b):
        line = self.town.ways.get(b.get('street'))
        c = tuple(b['c'])
        if not line:
            return None, None
        d, q = dist_to_polyline(c, line)
        v = G.sub(q, c)
        return (G.norm(v) if G.length(v) > 1e-6 else None), d

    def facing_edge(self, poly, direction, min_len=2.5, exclude=()):
        best, best_s = None, -2.0
        for i, (a, b) in enumerate(G.edges(poly)):
            if i in exclude:
                continue
            L = G.length(G.sub(b, a))
            if L < min_len:
                continue
            s = G.dot(G.out_normal(a, b), direction) + 0.02 * min(L, 20) / 20
            if s > best_s:
                best, best_s = i, s
        return best

    def party_walls(self, key, poly):
        """Intervals of edges that touch a neighbour's wall (within 0.8 m, facing it, overlapping >= 1 m)."""
        b = self.by_osm[key]
        c = b['c']
        r = max(G.length(G.sub(p, c)) for p in poly)
        out = []
        for k2, b2 in self.by_osm.items():
            if k2 == key:
                continue
            c2 = b2['c']
            if G.length(G.sub(c2, c)) > r + 60:
                continue
            p2 = self.simple[k2]
            for i, (a, e) in enumerate(G.edges(poly)):
                n = G.out_normal(a, e)
                d = G.sub(e, a)
                L = G.length(d)
                if L < 1.0:
                    continue
                dn = G.norm(d)
                for a2, e2 in G.edges(p2):
                    n2 = G.out_normal(a2, e2)
                    if G.dot(n, n2) > -0.95:
                        continue
                    # distance of the other edge's ends from this edge's line, and the overlap along it
                    da, db = G.dot(G.sub(a2, a), n), G.dot(G.sub(e2, a), n)
                    if abs(da) > 0.8 or abs(db) > 0.8:
                        continue
                    ua, ub = G.dot(G.sub(a2, a), dn), G.dot(G.sub(e2, a), dn)
                    u0, u1 = max(0.0, min(ua, ub)), min(L, max(ua, ub))
                    if u1 - u0 >= 1.0:
                        out.append({'edge': i, 'u0': round(u0, 3), 'u1': round(u1, 3), 'with': b2['osm']})
        return out

    def near_weir(self, poly):
        for line in self.town.weirs:
            for p in poly:
                if dist_to_polyline(p, line)[0] <= 40.0:
                    return True
        return False

    def ground_stats(self, poly):
        pts = []
        for a, b in G.edges(poly):
            L = G.length(G.sub(b, a))
            n = max(1, int(math.ceil(L / 2.0)))
            for k in range(n):
                pts.append(G.lerp(a, b, k / n))
        pts.append(G.centroid(poly))
        hs = [self.town.ground(x, z) for x, z in pts]
        return min(hs), max(hs), sum(hs) / len(hs)

    # ---- doors

    def placeholder_doors(self, b, fam, model, poly_o, sdir, D):
        """DoorSpecs by the design's rules (site-layouts 7a), on the ORIGINAL polygon's edges (buildings.json)."""
        es = G.edges(poly_o)
        lens = [G.length(G.sub(e, a)) for a, e in es]
        out = []

        def edge_facing(direction, min_len, exclude=()):
            if direction is None:
                cands = [i for i in range(len(es)) if lens[i] >= min_len and i not in exclude]
                return max(cands, key=lambda i: (lens[i], -i)) if cands else None
            best, best_s = None, -9
            for i, (a, e) in enumerate(es):
                if lens[i] < min_len or i in exclude:
                    continue
                s = G.dot(G.out_normal(a, e), direction) + 0.05 * min(lens[i], 20) / 20
                if s > best_s:
                    best, best_s = i, s
            return best

        def add(kind, edge, offset, width):
            if edge is None:
                return
            L = lens[edge]
            lo, hi = width / 2 + 0.35, L - width / 2 - 0.35
            if hi < lo:
                return
            out.append({'building': b['osm'], 'part': b.get('part', 0), 'kind': kind, 'wallEdge': edge,
                        'offset': round(min(hi, max(lo, offset)), 3), 'width': round(width, 3), 'sillY': None})

        back = (-sdir[0], -sdir[1]) if sdir else None
        if fam == 'house':
            e = edge_facing(sdir, 3.0)
            if e is not None:
                L = lens[e]
                off = L / 2 if L < 7.5 else L * D.pick('door.side', [(0.32, 1), (0.68, 1)])
                add('Main', e, off, 0.95)
                if D.chance('door.back', 0.6):
                    eb = edge_facing(back, 2.5, exclude=(e,))
                    if eb is not None:
                        add('Service', eb, lens[eb] * D.range('door.back.u', 0.3, 0.7), 0.9)
        elif fam == 'block':
            e = edge_facing(sdir, 4.0)
            if e is not None:
                L = lens[e]
                off = L / 2 if L < 12 else L * D.pick('door.side', [(0.3, 1), (0.5, 1), (0.7, 1)])
                add('Main', e, off, 1.1 if L < 8 else 1.8)
                eb = edge_facing(back, 3.0, exclude=(e,))
                if eb is not None:
                    add('Service', eb, lens[eb] * D.range('door.back.u', 0.25, 0.75), 1.0)
        elif fam == 'mill':
            e = edge_facing(sdir, 4.0)
            if e is not None:
                add('Main', e, lens[e] / 2, 1.6)
                # loading door on a short end
                ends = sorted([i for i in range(len(es)) if i != e and lens[i] >= 4.0], key=lambda i: (lens[i], i))
                if ends:
                    add('Service', ends[0], lens[ends[0]] / 2, 1.8)
        elif fam == 'warehouse':
            e = edge_facing(sdir, 4.0)
            if e is not None:
                add('Main', e, min(3.0, lens[e] / 2), 1.0)
                cands = [i for i in range(len(es)) if i != e and lens[i] >= 9.0]
                if cands:
                    # docks on the side or rear, away from the street (doc): the longest edge facing away most
                    dock = max(cands, key=lambda i: (round(-G.dot(G.out_normal(*es[i]), sdir) if sdir else 0, 1), lens[i], -i))
                    n = max(1, min(6, int((lens[dock] - 8.0) // 4.0) + 1))
                    start = lens[dock] / 2 - (n - 1) * 2.0
                    for k in range(n):
                        add('Dock', dock, start + 4.0 * k, 2.7)
        elif model == 'barn':
            shorts = sorted(range(len(es)), key=lambda i: (lens[i], i))
            ends = [i for i in shorts if lens[i] >= 5.0][:2]
            for i in ends:
                add('Bay', i, lens[i] / 2, min(4.2, lens[i] * 0.45))
        elif model == 'machineshed':
            e = edge_facing(sdir, 6.0)
            if e is not None:
                L = lens[e]
                n = max(1, min(3, int(L // 6.0)))
                w = 4.2 if L >= 9 else 3.6
                for k in range(n):
                    add('Bay', e, L * (k + 0.5) / n, w)
                es2 = edge_facing(back, 2.5, exclude=(e,))
                add('Service', es2, lens[es2] / 2 if es2 is not None else 0, 0.9)
        elif model == 'garage':
            # face the driveway's end when one ends near the garage, else the street
            target = sdir
            c = tuple(b['c'])
            best = None
            for _, tags, pts in self.town.service:
                if tags.get('service') not in ('driveway', 'shared_driveway') or not pts:
                    continue
                for p in (pts[0], pts[-1]):
                    d = G.length(G.sub(p, c))
                    if d < 25 and (best is None or d < best[0]):
                        best = (d, p)
            if best is not None and best[0] > 1e-3:
                target = G.norm(G.sub(best[1], c))
            e = edge_facing(target, 3.3)            # a 2.6 m door needs 3.3 m of wall
            if e is not None:
                L = lens[e]
                if L >= 6.6 and D.chance('garage.double', 0.5):
                    add('Garage', e, L / 2, 4.9)
                else:
                    add('Garage', e, L / 2, 2.6)
                if D.chance('garage.side', 0.5):
                    side = edge_facing(None, 3.0, exclude=(e,))
                    add('Service', side, lens[side] * 0.6 if side is not None else 0, 0.85)
        elif model == 'shed':
            e = edge_facing(sdir, 1.6)
            if e is not None:
                add('Main', e, lens[e] / 2, 1.0 if lens[e] < 3.5 else 1.5)
        if not out:
            # every building gets at least one door: a plain one on the wall facing the street (or the longest wall)
            e = edge_facing(sdir, 1.7)
            add('Main', e, lens[e] / 2 if e is not None else 0, 0.9)
        return out

    def resolve_doors(self, specs, poly_o, poly_s):
        """Each spec's point on the original edge, projected onto the simplified wall: (edge, u) of the opening."""
        out = []
        for k, s in enumerate(specs):
            kind = s['kind'] if isinstance(s['kind'], str) else DOOR_KINDS[int(s['kind'])]
            i = int(s['wallEdge'])
            a, e = poly_o[i], poly_o[(i + 1) % len(poly_o)]
            p = G.add(a, G.mul(G.norm(G.sub(e, a)), float(s['offset'])))
            best = None
            for j, (sa, sb) in enumerate(G.edges(poly_s)):
                t, d = G.seg_project(p, sa, sb)
                if best is None or d < best[0] - 1e-9:
                    best = (d, j, t * G.length(G.sub(sb, sa)))
            out.append({'spec': k, 'kind': kind, 'width': float(s['width']), 'edge': best[1], 'u': round(best[2], 4),
                        'offWall': round(best[0], 4), 'plan': [round(p[0], 4), round(p[1], 4)],
                        'sillY': s.get('sillY')})
            if s.get('fallback'):
                out[-1]['fallback'] = True
        return out

    # ---- one building

    def plan(self, key):
        b = self.by_osm[key]
        poly_o = [tuple(p) for p in b['poly']]
        if G.area2(poly_o) < 0:
            raise SystemExit('%s: buildings.json polygon is not CCW' % b['osm'])
        poly_s = self.simple[key]
        near = self.near_weir(poly_o)
        m = model_of(b, near)
        if m is None:
            return None, []
        fam, model = m
        D = Draw(self.town.id, b['osm'], b.get('part', 0))
        tags = b.get('tags', {})
        why = {}
        sdir, sdist = self.street_dir(b)
        gmin, gmax, gmean = self.ground_stats(poly_s)

        # doors: S3's when given, else ours
        if self.doors_in is not None:
            specs = [s for s in self.doors_in if s['building'] == b['osm'] and int(s.get('part', 0)) == b.get('part', 0)]
            why['doors'] = 'sites-S3 (%s)' % self.doors_src
            if not specs:
                # S3 gives no door (accessory buildings get an apron only; buildings outside the play area get no plan):
                # lane A's own rule doors, marked, so no garage stands without one. They are not DoorSpecs of the site.
                specs = self.placeholder_doors(b, fam, model, poly_o, sdir, D)
                specs.sort(key=lambda s: (s['wallEdge'], s['offset'], s['kind']))
                for s in specs:
                    s['fallback'] = True
                self.fallback_doors += len(specs)
                why['doors'] = 'lane A rule doors: sites-S3 has none for this building'
        else:
            specs = self.placeholder_doors(b, fam, model, poly_o, sdir, D)
            # the file order (sorted like doors.placeholder.json), so a marker's index is the spec's index for its building
            specs.sort(key=lambda s: (s['wallEdge'], s['offset'], s['kind']))
            why['doors'] = 'placeholder (lane A, site-layouts 7a rules) until sites-S3'

        # origin: the footprint centroid, at floor height
        c = G.centroid(poly_s)
        rec = {'osm': b['osm'], 'part': b.get('part', 0), 'family': fam, 'model': model, 'business': b['business'],
               'type': b['type'], 'form': b.get('form'), 'seed': D.seed, 'inPlay': b.get('inPlay', True),
               'file': 'BLD_' + b['osm'].replace('/', '_') + ('' if b.get('part', 0) == 0 else '_p%d' % b['part'])}

        # storeys
        lv = tags.get('building:levels')
        if lv and str(lv).replace('.', '', 1).isdigit():
            storeys = max(1, int(round(float(lv))))
            why['storeys'] = 'OSM building:levels'
        else:
            keys = {'house': ['type:house'], 'block': ['type:downtown_' + b['type'], 'type:downtown_shop'],
                    'warehouse': ['type:warehouse', 'type:industrial'], 'barn': [], 'mill': []}[fam]
            hist, used, n = self.profile.storeys(keys)
            if fam == 'mill':
                storeys = 4 if b['area'] >= 400 else 3
                why['storeys'] = '[doc] 08b: multi-storey mill (no measured sample)'
            elif fam == 'barn':
                storeys = 1
                why['storeys'] = '[doc] one storey with a loft'
            elif fam == 'warehouse' and hist is None:
                storeys = 1
                why['storeys'] = '[doc] one tall storey'
            else:
                cap = {'house': 3, 'block': 4, 'warehouse': 2}[fam]
                ws = [(k, v) for k, v in sorted(hist.items()) if k <= cap]
                storeys = D.pick('storeys', ws)
                why['storeys'] = '%s na hist (n %d), capped at %d' % (used, n, cap)
                if fam == 'house' and b['area'] < 60:
                    storeys = 1
                    why['storeys'] += '; 1 under 60 m2'
                elif fam == 'house' and b['area'] < 90 and storeys > 2:
                    storeys = 2

        # roof shape
        rs = tags.get('roof:shape')
        if rs:
            roof = ROOF_MAP.get(rs, 'gable')
            why['roof'] = 'OSM roof:shape=' + rs
        elif fam == 'house':
            shares, used, n = self.profile.roof(['type:house'])
            roof = ROOF_MAP.get(D.pick('roof', sorted(shares.items())), 'gable')
            why['roof'] = '%s na shares (n %d)' % (used, n)
        elif fam == 'block':
            roof = 'flat'
            why['roof'] = '[doc] 08b 5: flat or low slope behind a parapet'
        elif fam == 'warehouse':
            roof = D.pick('roof', [('flat', 6), ('gable', 4)])
            why['roof'] = '[doc] 08b 5: flat or 1/12-2/12'
        elif fam == 'mill':
            roof = 'gable'
            why['roof'] = '[doc] gabled mill'
        else:
            roof = {'barn': D.pick('roof', [('gambrel', 5), ('gable', 5)]), 'machineshed': 'gable', 'garage': 'gable',
                    'shed': D.pick('roof', [('gable', 6), ('shed', 4)])}[model]
            why['roof'] = '[doc] 08b 5: barn gambrel or gabled; sheds gabled or lean-to'

        # heights and pitch ([doc] 08b section 5)
        P = {}
        if fam == 'house':
            P['storeyH'] = D.range('storeyH', 3.0, 3.4)
            P['plinth'] = D.range('plinth', 0.5, 0.9)
            if roof == 'hip':
                P['pitch'] = D.range('pitch', 28, 38, 1)
            elif storeys == 1:
                P['pitch'] = D.range('pitch', 26, 40, 1)
            else:
                P['pitch'] = D.range('pitch', 34, 45, 1)
            P['overhang'] = D.range('overhang', 0.35, 0.5)
            P['rake'] = D.range('rake', 0.2, 0.3)
            P['groundH'] = P['storeyH']
        elif fam == 'block':
            P['groundH'] = D.range('groundH', 4.0, 4.6)
            P['storeyH'] = D.range('storeyH', 3.3, 3.6)
            P['plinth'] = 0.15
            P['parapet'] = D.range('parapet', 0.8, 1.1)
            P['pitch'] = 0
        elif fam == 'mill':
            P['groundH'] = P['storeyH'] = D.range('storeyH', 3.6, 4.0)
            P['plinth'] = 0.4
            P['pitch'] = D.range('pitch', 24, 30, 1)
            P['overhang'] = 0.45
            P['rake'] = 0.3
        elif fam == 'warehouse':
            eave = D.range('eave', 6.0, 8.0)
            P['groundH'] = P['storeyH'] = eave / storeys
            P['plinth'] = 0.15
            P['parapet'] = D.range('parapet', 0.7, 1.0)
            P['pitch'] = D.range('pitch', 5, 10, 1)
            P['overhang'] = 0.3
            P['rake'] = 0.2
        else:
            eave = {'barn': D.range('eave', 4.5, 5.5), 'machineshed': D.range('eave', 4.0, 4.8),
                    'garage': D.range('eave', 2.6, 3.0), 'shed': D.range('eave', 2.3, 2.6)}[model]
            P['groundH'] = P['storeyH'] = eave
            P['plinth'] = 0.1
            P['pitch'] = {'barn': D.range('pitch', 33, 38, 1), 'machineshed': D.range('pitch', 14, 18, 1),
                          'garage': D.range('pitch', 27, 34, 1), 'shed': D.range('pitch', 18, 30, 1)}[model]
            P['overhang'] = {'barn': 0.45, 'machineshed': 0.35, 'garage': 0.35, 'shed': 0.25}[model]
            P['rake'] = {'barn': 0.35, 'machineshed': 0.25, 'garage': 0.25, 'shed': 0.2}[model]
        why['heights'] = '[doc] 08b section 5 (North American construction; lidar check open, design 14 #5)'

        # floor height from the ground under the footprint
        relief = gmax - gmin
        if fam in ('house', 'mill'):
            floor = gmax + P['plinth'] - max(0.0, relief - 1.5) * 0.6
        elif fam == 'barn':
            floor = gmin + min(relief, 0.6) * 0.5 + P['plinth']
        else:
            floor = gmax + P['plinth'] - max(0.0, relief - 0.8) * 0.8
        floor = round(floor, 3)
        rec['origin'] = [round(c[0], 4), floor, round(c[1], 4)]
        rec['ground'] = {'min': round(gmin - floor, 3), 'max': round(gmax - floor, 3), 'mean': round(gmean - floor, 3),
                         'centreY': round(self.town.ground(c[0], c[1]), 3)}
        rec['poly'] = [[round(x - c[0], 4), round(z - c[1], 4)] for x, z in poly_s]
        rec['storeys'] = storeys
        rec['roof'] = roof
        rec['params'] = P

        # street side, neighbours, doors on the simplified walls
        poly_l = [tuple(p) for p in rec['poly']]
        rec['streetEdge'] = self.facing_edge(poly_l, sdir) if sdir else None
        rec['streetDist'] = round(sdist, 2) if sdist is not None else None
        rec['setback'] = b.get('setback')
        rec['party'] = self.party_walls(key, poly_s)
        doors = self.resolve_doors(specs, poly_o, poly_s)
        for d in doors:
            a, e = poly_s[d['edge']], poly_s[(d['edge'] + 1) % len(poly_s)]
            nrm = G.out_normal(a, e)
            q = G.add(G.add(a, G.mul(G.norm(G.sub(e, a)), d['u'])), G.mul(nrm, 1.2))
            d['ground'] = round(self.town.ground(q[0], q[1]) - floor, 3)   # local y of the ground outside the door
        rec['doors'] = doors

        # colours
        col = {}
        if fam == 'house':
            col['wall'] = D.pick('c.wall', HOUSE_WALL)
            col['trim'] = D.pick('c.trim', WHITE_TRIM if col['wall'] == 'siding_white' else HOUSE_TRIM)
            col['roof'] = D.pick('c.roof', HOUSE_ROOF)
            col['found'] = D.pick('c.found', FOUND)
            col['door'] = D.pick('c.door', DOOR)
            col['shutter'] = D.pick('c.shutter', SHUTTER) if D.chance('shutters', 0.35) else None
        elif fam == 'block':
            col['wall'] = D.pick('c.wall', BRICK)
            col['trim'] = D.pick('c.trim', BLOCK_TRIM)
            col['front'] = D.pick('c.front', SHOPFRONT)
            col['sign'] = D.pick('c.sign', SIGN)
            col['awning'] = D.pick('c.awning', AWNING) if D.chance('awning', 0.5) else None
            col['roof'] = D.pick('c.roof', FLAT_ROOF)
            col['found'] = 'stone_grey'
            col['door'] = D.pick('c.door', [('door_wood', 3), ('door_black', 3), ('door_green', 2), ('door_red', 1)])
        elif fam == 'mill':
            col['wall'] = D.pick('c.wall', MILL_WALL)
            col['trim'] = 'trim_white' if not col['wall'].startswith('brick') else 'stone_tan'
            col['roof'] = D.pick('c.roof', [('metal_galv', 4), ('shingle_charcoal', 3), ('metal_charcoal', 2), ('metal_red', 1)])
            col['found'] = 'stone_grey'
            col['door'] = D.pick('c.door', [('door_wood', 3), ('barn_red', 1), ('door_green', 1)])
        elif fam == 'warehouse':
            if D.chance('c.metal', 0.55):
                col['wall'] = D.pick('c.wall', PANEL)
                col['trim'] = PANEL_TRIM[col['wall']]
            else:
                col['wall'] = D.pick('c.wall', [('brick_red', 3), ('brick_buff', 2), ('block_grey', 3), ('brick_brown', 2)])
                col['trim'] = 'concrete'
            col['roof'] = D.pick('c.roof', METAL_ROOF) if roof != 'flat' else D.pick('c.roof', FLAT_ROOF)
            col['found'] = 'concrete'
            col['door'] = D.pick('c.door', [('door_steel', 4), ('door_grey', 3), ('door_white', 2), ('door_blue', 1)])
        else:
            if model == 'machineshed':
                col['wall'] = D.pick('c.wall', PANEL)
                col['trim'] = PANEL_TRIM[col['wall']]
                col['roof'] = D.pick('c.roof', METAL_ROOF)
            elif model == 'barn':
                col['wall'] = D.pick('c.wall', BARN_WALL)
                col['trim'] = 'barn_white' if col['wall'] in ('barn_red', 'barn_board_dark') else D.pick('c.trim', [('trim_red', 1), ('trim_white', 1)])
                col['roof'] = D.pick('c.roof', METAL_ROOF + [('shingle_charcoal', 15)])
            else:
                house = self.nearest_house(b)
                if house is not None and D.chance('c.match', 0.7):
                    col.update({k: v for k, v in house['colours'].items() if k in ('wall', 'trim', 'roof')})
                    why['colours'] = 'matches house ' + house['osm']
                else:
                    col['wall'] = D.pick('c.wall', HOUSE_WALL + BARN_WALL[:2])
                    col['trim'] = D.pick('c.trim', HOUSE_TRIM)
                    col['roof'] = D.pick('c.roof', HOUSE_ROOF)
            col['found'] = 'concrete'
            col['door'] = D.pick('c.door', [('door_white', 6), ('door_wood', 2), ('door_grey', 1), ('door_green', 1)])
        rec['colours'] = col

        # features
        F = {}
        if fam == 'house':
            F['window'] = {'w': D.range('win.w', 0.8, 0.95), 'h': D.range('win.h', 1.45, 1.7), 'sill': D.range('win.sill', 0.75, 0.85),
                           'bay': D.range('win.bay', 2.5, 3.2)}
            F['chimney'] = D.chance('chimney', 0.7)
            main = [d for d in doors if d['kind'] == 'Main']
            sb = b.get('setback')
            if main and (sb is None or sb >= 4.0) and D.chance('porch', 0.7):
                F['porch'] = {'depth': round(min(D.range('porch.depth', 2.0, 2.4), (sb - 1.5) if sb is not None else 9), 2),
                              'full': D.chance('porch.full', 0.5)}
        elif fam == 'block':
            F['window'] = {'w': D.range('win.w', 0.95, 1.05), 'h': D.range('win.h', 1.9, 2.1), 'sill': 0.8, 'bay': D.range('win.bay', 2.3, 2.6)}
        elif fam == 'mill':
            F['window'] = {'w': 1.0, 'h': D.range('win.h', 1.7, 1.9), 'sill': 0.9, 'bay': D.range('win.bay', 2.5, 2.9)}
            F['cupola'] = True
        elif fam == 'warehouse':
            F['window'] = {'w': 1.2, 'h': 0.9, 'sill': P['groundH'] - 1.6, 'bay': 4.0}
        else:
            F['window'] = {'w': 0.7, 'h': 0.7, 'sill': 1.3, 'bay': 4.0}
            F['hayDoor'] = model == 'barn'
        rec['features'] = F
        rec['why'] = why
        return rec, specs

    def nearest_house(self, b):
        best = None
        for r in self.out:
            if r['family'] != 'house':
                continue
            d = G.length(G.sub((r['origin'][0], r['origin'][2]), b['c']))
            if d < 40 and (best is None or d < best[0]):
                best = (d, r)
        return best[1] if best else None

    def run(self):
        self.out = []
        specs_all = []
        keys = sorted(self.by_osm, key=lambda k: (k[0], k[1]))
        # houses first so garages can match a neighbour's colours
        order = [k for k in keys if self.by_osm[k]['business'] == 'House'] + [k for k in keys if self.by_osm[k]['business'] != 'House']
        for k in order:
            rec, specs = self.plan(k)
            if rec is not None:
                self.out.append(rec)
                specs_all.extend(specs)
        self.out.sort(key=lambda r: (r['osm'], r['part']))
        specs_all.sort(key=lambda s: (s['building'], s['part'], s['wallEdge'], s['offset'], s['kind']))
        return self.out, specs_all


def load_town(path):
    return FixtureTown() if path == 'fixtures' else Town(path)


def main(argv):
    args = dict(zip(argv[::2], argv[1::2]))
    town = load_town(args['--town'])
    prof = Profile(args['--profile'])
    planner = Planner(town, prof, args.get('--doors'))
    recs, specs = planner.run()
    out = args['--out']
    os.makedirs(out, exist_ok=True)
    counts = {}
    for r in recs:
        counts[r['model']] = counts.get(r['model'], 0) + 1
    inst = {'format': 'backroads-building-instances', 'version': PLAN_VERSION, 'town': town.manifest.get('townName', 'Millbrook'),
            'seed': 'blake2b-64(TOWN_ID|osm|part) per named draw', 'doors': planner.doors_src or 'placeholder',
            'fallbackDoors': planner.fallback_doors,
            'counts': counts, 'buildings': recs}
    with open(os.path.join(out, 'instances.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(inst, f, indent=1, sort_keys=False)
        f.write('\n')
    if planner.doors_in is None:
        doc = {'format': 'backroads-doorspecs', 'version': 1,
               'note': 'Placeholder DoorSpecs (Backroads.Core.WorldBake.DoorSpec fields) by lane A, T3.4, from the rules of '
                       'site-layouts 7a, until sites-S3 writes the real ones. wallEdge indexes the building polygon of '
                       'buildings.json; offset is metres from that vertex to the door centre; sillY null = NaN.',
               'doors': specs}
        with open(os.path.join(out, 'doors.placeholder.json'), 'w', encoding='utf-8', newline='\n') as f:
            json.dump(doc, f, indent=1)
            f.write('\n')
    print('plan: %d instances %s, %d doors (%s; %d lane A rule doors where it has none)' % (
        len(recs), json.dumps(counts, sort_keys=True), len(specs), planner.doors_src or 'placeholder', planner.fallback_doors))


if __name__ == '__main__':
    main(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else sys.argv[1:])
