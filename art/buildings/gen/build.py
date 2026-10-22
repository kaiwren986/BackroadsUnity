"""Building geometry from one instance record (plan.py): LOD0/LOD1/LOD2 body and pane meshes, UCX_ hulls, door markers.

Plain Python on top of bgeom (no bpy). Local frame: the instance origin (footprint centroid at floor height), x east,
y up, z north (Unity's axes). Units are metres.

Massing. Walls stand on the simplified footprint exactly (doors stay within the 0.1 m of Art.DoorsMatchSites). Roofs
are built over rectangles: the footprint is turned into its main axes, snapped to right angles (it is orthogonal within
15 deg for all but a handful of the town's buildings) and cut into the fewest largest rectangles; each rectangle carries
its own gable, hip, gambrel or lean-to roof at the same pitch, so L and T houses get cross gables, and every wall's top
follows the roof that covers it. Flat roofs (commercial blocks, some warehouses) stand behind a parapet on the whole
footprint.
"""
import hashlib
import math

import bgeom as G

TAN = lambda deg: math.tan(math.radians(deg))
OPENING_DEPTH = 0.12
WALL_T = 0.25

# LOD0 triangle budgets per family (card T3.4: a house 6k, a block or the mill 15k; barns and sheds as houses,
# warehouses as blocks). Art.BuildingsBudget checks them on the imported models.
BUDGET_LOD0 = {'house': 6000, 'block': 15000, 'mill': 15000, 'warehouse': 15000, 'barn': 6000}


def make(rec, palette):
    """The Building at the most detail that fits its family's LOD0 budget, with its LOD0 meshes."""
    for detail in (0, 1, 2):
        B = Building(rec, palette, detail)
        body, panes = B.build_lod(0)
        if body.tris + panes.tris <= BUDGET_LOD0[rec['family']] or detail == 2:
            return B, (body, panes)


def hash01(seed, key):
    h = hashlib.blake2b((seed + '#' + key).encode('utf-8'), digest_size=8).digest()
    return int.from_bytes(h, 'little') / 2.0 ** 64


# ------------------------------------------------------------------------------------------------ roof rectangles

class RoofRect:
    """A rectangle in the building's rotated frame with its own roof. h(p) is the roof's underside height."""

    def __init__(self, x0, z0, x1, z1, shape, along, eave, pitch, storeys):
        self.x0, self.z0, self.x1, self.z1 = x0, z0, x1, z1
        self.shape, self.along, self.eave, self.pitch, self.storeys = shape, along, eave, pitch, storeys
        self.over = {'x0': 0.0, 'x1': 0.0, 'z0': 0.0, 'z1': 0.0}
        self.ext = {'x0': 0.0, 'x1': 0.0, 'z0': 0.0, 'z1': 0.0}
        self.internal = set()
        self.upper = 25.0       # gambrel upper pitch
        self.lower = 60.0       # gambrel lower pitch

    @property
    def area(self):
        return (self.x1 - self.x0) * (self.z1 - self.z0)

    @property
    def half(self):
        return ((self.z1 - self.z0) if self.along == 'x' else (self.x1 - self.x0)) / 2.0

    @property
    def length(self):
        return (self.x1 - self.x0) if self.along == 'x' else (self.z1 - self.z0)

    def d_eave(self, x, z):
        """Distance inward from the nearer eave line (across the ridge)."""
        if self.along == 'x':
            return self.half - abs(z - (self.z0 + self.z1) / 2)
        return self.half - abs(x - (self.x0 + self.x1) / 2)

    def d_end(self, x, z):
        if self.along == 'x':
            return min(x - self.x0, self.x1 - x)
        return min(z - self.z0, self.z1 - z)

    def rise(self, d):
        if self.shape == 'gambrel':
            dk = self.half * 0.42
            if d <= dk:
                return d * TAN(self.lower)
            return dk * TAN(self.lower) + (d - dk) * TAN(self.upper)
        return d * TAN(self.pitch)

    def h(self, x, z):
        if self.shape == 'flat':
            return self.eave
        if self.shape == 'shed':
            if getattr(self, 'flip', False):
                t = (self.z1 - z) if self.along == 'x' else (self.x1 - x)
            else:
                t = (z - self.z0) if self.along == 'x' else (x - self.x0)
            return self.eave + t * TAN(self.pitch)
        d = self.d_eave(x, z)
        if self.shape == 'hip':
            d = min(d, self.d_end(x, z))
        return self.eave + self.rise(min(d, self.half))

    @property
    def ridge_h(self):
        if self.shape == 'shed':
            return self.eave + 2 * self.half * TAN(self.pitch)
        if self.shape == 'flat':
            return self.eave
        return self.eave + self.rise(self.half)

    def lines(self):
        """Lines (a, b, c) with a*x + b*z = c where h has a kink: edges, ridge, knees, hip diagonals."""
        L = [(1, 0, self.x0), (1, 0, self.x1), (0, 1, self.z0), (0, 1, self.z1)]
        xc, zc = (self.x0 + self.x1) / 2, (self.z0 + self.z1) / 2
        if self.along == 'x':
            L.append((0, 1, zc))
            if self.shape == 'gambrel':
                dk = self.half * 0.42
                L += [(0, 1, self.z0 + dk), (0, 1, self.z1 - dk)]
        else:
            L.append((1, 0, xc))
            if self.shape == 'gambrel':
                dk = self.half * 0.42
                L += [(1, 0, self.x0 + dk), (1, 0, self.x1 - dk)]
        if self.shape == 'hip':
            for (cx, cz) in ((self.x0, self.z0), (self.x0, self.z1), (self.x1, self.z0), (self.x1, self.z1)):
                L.append((1, -1, cx - cz))
                L.append((1, 1, cx + cz))
        return L

    def contains(self, x, z, eps=1e-6):
        return self.x0 - eps <= x <= self.x1 + eps and self.z0 - eps <= z <= self.z1 + eps

    def dist(self, x, z):
        dx = max(self.x0 - x, 0, x - self.x1)
        dz = max(self.z0 - z, 0, z - self.z1)
        return math.hypot(dx, dz)


# ------------------------------------------------------------------------------------------------ the building

class Building:
    def __init__(self, rec, palette, detail=0):
        self.rec = rec
        self.pal = palette
        self.detail = detail      # 0 full; 1 no shutters, muntins or corner boards; 2 also flat window frames (budget)
        self.seed = rec['seed']
        self.family, self.model = rec['family'], rec['model']
        self.poly = [tuple(p) for p in rec['poly']]
        self.P, self.F, self.C = rec['params'], rec['features'], rec['colours']
        self.storeys = rec['storeys']
        self.ybot = round(rec['ground']['min'] - 0.6, 4)
        self.groundH = self.P.get('groundH', self.P.get('storeyH', 3.0))
        self.storeyH = self.P.get('storeyH', 3.0)
        self.eave = self.groundH + (self.storeys - 1) * self.storeyH
        self.roof_shape = rec['roof']
        self.party = {}
        for p in rec.get('party', []):
            self.party.setdefault(p['edge'], []).append((p['u0'], p['u1']))
        self.doors_by_edge = {}
        for d in rec['doors']:
            self.doors_by_edge.setdefault(d['edge'], []).append(d)
        self.theta = G.dominant_angle(self.poly)
        self.flat = self.roof_shape == 'flat'
        self.rects = []
        if not self.flat:
            self._massing()
        self.top = self.eave + (self.P.get('parapet', 0.35 if self.family == 'house' else 0.8) if self.flat else 0.0)
        self.pane_counter = 0
        self.markers = []
        self.porch_geom = None

    def u(self, key):
        return hash01(self.seed, key)

    # ---- frames

    def to_rot(self, p):
        return G.rotate(p, -self.theta)

    def from_rot(self, p):
        return G.rotate(p, self.theta)

    # ---- massing

    def _massing(self):
        pr = [self.to_rot(p) for p in self.poly]
        snapped = G.snap_orthogonal(pr)
        if snapped is not None and abs(G.area2(snapped)) > 1.0:
            rects = G.rect_decompose(G.ensure_ccw(snapped))
        else:
            xs = [p[0] for p in pr]
            zs = [p[1] for p in pr]
            if not G.is_convex(self.poly):
                self.flat = True
                self.roof_shape = 'flat'
                return
            rects = [(min(xs), min(zs), max(xs), max(zs))]
        # drop slivers into their neighbour by keeping them (they get a lean-to); order: largest first
        P = self.P
        main_area = (rects[0][2] - rects[0][0]) * (rects[0][3] - rects[0][1])
        for k, (x0, z0, x1, z1) in enumerate(rects):
            w, d = x1 - x0, z1 - z0
            along = 'x' if w >= d else 'z'
            if 0.87 < w / max(d, 1e-6) < 1.15 and k == 0:
                along = 'x' if self.u('ridge') < 0.5 else 'z'
            shape = self.roof_shape
            storeys = self.storeys
            narrow = min(w, d)
            if k > 0 and self.family == 'house':
                if narrow < 2.6:
                    shape, storeys = 'shed', 1
                elif (w * d) < 0.5 * main_area and narrow <= 6.5 and self.storeys >= 2 and self.u('wing%d' % k) < 0.5:
                    storeys = 1
            if shape == 'gambrel' and (k > 0 or self.model not in ('barn',)):
                shape = 'gable'
            if shape == 'shed' and k == 0 and self.family == 'house':
                shape = 'gable'
            eave = self.groundH + (storeys - 1) * self.storeyH
            pitch = P.get('pitch', 30)
            if shape == 'shed' and self.family == 'house':
                pitch = 14
            r = RoofRect(x0, z0, x1, z1, shape, along, eave, pitch, storeys)
            if shape == 'gambrel':
                r.upper = max(18.0, min(30.0, pitch - 10))
            if shape == 'shed' and k > 0:
                # the lean-to falls away from the rectangle it leans on
                main = rects[0]
                if along == 'x':
                    r.along = 'x'
                    if z0 >= main[3] - 1e-6:
                        r.flip = True
                else:
                    if x0 >= main[2] - 1e-6:
                        r.flip = True
            self.rects.append(r)
        self._junctions()
        oe, orake = P.get('overhang', 0.4), P.get('rake', 0.25)
        for r in self.rects:
            for side in ('x0', 'x1', 'z0', 'z1'):
                if side in r.internal:
                    continue
                gable_end = (side[0] == 'x') == (r.along == 'x')
                r.over[side] = orake if (gable_end and r.shape in ('gable', 'gambrel')) else oe
        # lean-tos lean against their neighbour: lower them so the high side meets the wall under its eave
        for r in self.rects:
            if r.shape == 'shed':
                hi = r.ridge_h
                lim = min(o.eave for o in self.rects if o is not r) - 0.2 if len(self.rects) > 1 else hi
                if hi > lim:
                    r.eave -= hi - lim

    def _shared(self, a, b):
        """Shared boundary between rects a and b: (side of a, side of b, s0, s1) along the line, or None."""
        e = 1e-4
        for sa, sb, ca, cb in (('x1', 'x0', a.x1, b.x0), ('x0', 'x1', a.x0, b.x1)):
            if abs(ca - cb) < e:
                s0, s1 = max(a.z0, b.z0), min(a.z1, b.z1)
                if s1 - s0 > 0.05:
                    return sa, sb, s0, s1
        for sa, sb, ca, cb in (('z1', 'z0', a.z1, b.z0), ('z0', 'z1', a.z0, b.z1)):
            if abs(ca - cb) < e:
                s0, s1 = max(a.x0, b.x0), min(a.x1, b.x1)
                if s1 - s0 > 0.05:
                    return sa, sb, s0, s1
        return None

    def _junctions(self):
        self.junctions = []
        n = len(self.rects)
        shared_len = [{'x0': 0.0, 'x1': 0.0, 'z0': 0.0, 'z1': 0.0} for _ in range(n)]
        for i in range(n):
            for j in range(i + 1, n):
                s = self._shared(self.rects[i], self.rects[j])
                if s is None:
                    continue
                sa, sb, s0, s1 = s
                self.junctions.append((i, j, sa, sb, s0, s1))
                shared_len[i][sa] += s1 - s0
                shared_len[j][sb] += s1 - s0
        for i, r in enumerate(self.rects):
            for side in ('x0', 'x1', 'z0', 'z1'):
                full = (r.z1 - r.z0) if side[0] == 'x' else (r.x1 - r.x0)
                if shared_len[i][side] >= 0.95 * full:
                    r.internal.add(side)
        # valleys: a rect whose ridge runs into the junction extends its roof into the other one when it is lower
        for (i, j, sa, sb, s0, s1) in self.junctions:
            for (k, m, sk) in ((i, j, sa), (j, i, sb)):
                R, N = self.rects[k], self.rects[m]
                if R.shape not in ('gable', 'gambrel'):
                    continue
                perp = (sk[0] == 'x') == (R.along == 'x')
                if not perp:
                    continue
                lower = R.ridge_h < N.ridge_h - 1e-3 or (abs(R.ridge_h - N.ridge_h) <= 1e-3 and R.area < N.area)
                if not lower:
                    continue
                depth = (N.x1 - N.x0) if sk[0] == 'x' else (N.z1 - N.z0)
                R.ext[sk] = min(R.half, depth)
                R.internal.add(sk)

    def owner(self, p_local, inward):
        """The rect whose roof covers a wall point (moved 0.2 m inside)."""
        q = self.to_rot(G.add(p_local, G.mul(inward, 0.2)))
        cands = [r for r in self.rects if r.contains(q[0], q[1])]
        if cands:
            return max(cands, key=lambda r: (r.area, -self.rects.index(r)))
        return min(self.rects, key=lambda r: (r.dist(q[0], q[1]), self.rects.index(r)))

    def h_at(self, r, p_local):
        q = self.to_rot(p_local)
        return r.h(q[0], q[1])

    def wall_top(self, a, b):
        """[(u, y)] along edge a->b, with the owner rect per stretch: ([(u, y)], [(u0, u1, rect)])."""
        L = G.length(G.sub(b, a))
        if self.flat or not self.rects:
            return [(0.0, self.top), (L, self.top)], [(0.0, L, None)]
        n = G.out_normal(a, b)
        inward = (-n[0], -n[1])
        ra, rb = self.to_rot(a), self.to_rot(b)
        ts = {0.0, 1.0}
        for r in self.rects:
            for (ca, cb, c) in r.lines():
                fa = ca * ra[0] + cb * ra[1] - c
                fb = ca * rb[0] + cb * rb[1] - c
                if (fa < -1e-9 < 1e-9 < fb) or (fb < -1e-9 < 1e-9 < fa):
                    t = fa / (fa - fb)
                    if 1e-6 < t < 1 - 1e-6:
                        ts.add(round(t, 9))
        ts = sorted(ts)
        pts, owners = [], []
        for k in range(len(ts) - 1):
            t0, t1 = ts[k], ts[k + 1]
            pm = G.lerp(a, b, (t0 + t1) / 2)
            r = self.owner(pm, inward)
            y0 = self.h_at(r, G.lerp(a, b, t0))
            y1 = self.h_at(r, G.lerp(a, b, t1))
            for u, y in ((t0 * L, y0), (t1 * L, y1)):
                if not pts or abs(pts[-1][0] - u) > 1e-6 or abs(pts[-1][1] - y) > 1e-6:
                    pts.append((u, y))
            if owners and owners[-1][2] is r:
                owners[-1] = (owners[-1][0], t1 * L, r)
            else:
                owners.append((t0 * L, t1 * L, r))
        return pts, owners

    # ---- pane ids

    def pane_colour(self, storey, kind):
        k = self.pane_counter
        self.pane_counter += 1
        rid = 0.02 + 0.96 * self.u('pane%d' % k)
        return (round(rid, 4), round(min(storey, 8) / 8.0, 4), {'window': 0.0, 'door': 0.5, 'shop': 1.0}[kind], 1.0)

    # ------------------------------------------------------------------------------------------------ openings (same at every LOD)

    def door_dims(self, d):
        kind, fam, m = d['kind'], self.family, self.model
        if kind == 'Main':
            h = {'house': 2.1, 'block': 2.3, 'mill': 2.4, 'warehouse': 2.1}.get(fam, 1.95 if m == 'shed' else 2.1)
        elif kind == 'Service':
            h = 2.4 if fam == 'mill' else 2.05
        elif kind == 'Garage':
            h = 2.15
        elif kind == 'Bay':
            h = min(4.2 if m == 'machineshed' else 4.0, self.eave - 0.45)
        elif kind == 'Dock':
            h = 3.0
        else:
            h = 1.2
        y0 = 1.0 if kind == 'DriveThroughWindow' else 0.0
        return y0, min(y0 + h, self.eave - 0.2)

    def plan_openings(self):
        """Per edge: (openings [{u0,u1,y0,y1,kind,storey,...}], shopfront regions, wall top, roof owners)."""
        if hasattr(self, '_open'):
            return self._open
        fam, m, F = self.family, self.model, self.F
        W = F.get('window', {'w': 0.9, 'h': 1.5, 'sill': 0.8, 'bay': 3.0})
        res = {}
        street = self.rec.get('streetEdge')
        n = len(self.poly)
        for i in range(n):
            a, b = self.poly[i], self.poly[(i + 1) % n]
            L = G.length(G.sub(b, a))
            tops, owners = self.wall_top(a, b)
            ops, regions = [], []
            party = self.party.get(i, [])
            blocked = [(p0 - 0.3, p1 + 0.3) for p0, p1 in party]

            def free(u0, u1):
                return all(u1 <= p0 or u0 >= p1 for p0, p1 in blocked)

            def top_min(u0, u1):
                ys = [y for u, y in tops if u0 - 1e-6 <= u <= u1 + 1e-6]
                return min(ys + [interp(tops, u0), interp(tops, u1)])

            door_iv = []
            for d in self.doors_by_edge.get(i, []):
                y0, y1 = self.door_dims(d)
                u0, u1 = d['u'] - d['width'] / 2, d['u'] + d['width'] / 2
                ops.append({'u0': u0, 'u1': u1, 'y0': y0, 'y1': y1, 'kind': 'door', 'door': d, 'storey': 0})
                door_iv.append((u0 - 0.45, u1 + 0.45))
                if fam == 'block' and d['kind'] == 'Main' and y1 + 0.55 < self.groundH - 1.0:
                    ops.append({'u0': u0, 'u1': u1, 'y0': y1 + 0.1, 'y1': y1 + 0.5, 'kind': 'transom', 'storey': 0})
                if fam == 'mill' and d['kind'] == 'Service':
                    for s in range(1, self.storeys):
                        fy = self.groundH + (s - 1) * self.storeyH
                        if fy + 2.3 < top_min(u0, u1) - 0.2:
                            ops.append({'u0': u0, 'u1': u1, 'y0': fy, 'y1': fy + 2.2, 'kind': 'loading', 'storey': s})
                    self.hoist = (i, d['u'])

            shopfront = fam == 'block' and i == street and not party
            if shopfront:
                gh = self.groundH
                pier = 0.5
                regions.append((pier, L - pier, 0.0, gh, self.C['front']))
                regions.append((pier, L - pier, gh - 1.05, gh - 0.3, self.C['sign']))
                spans = [(pier + 0.15, L - pier - 0.15)]
                for o in [o for o in ops if o['kind'] == 'door']:
                    new = []
                    for s0, s1 in spans:
                        if o['u1'] + 0.2 <= s0 or o['u0'] - 0.2 >= s1:
                            new.append((s0, s1))
                            continue
                        if o['u0'] - 0.2 - s0 > 0.6:
                            new.append((s0, o['u0'] - 0.2))
                        if s1 - (o['u1'] + 0.2) > 0.6:
                            new.append((o['u1'] + 0.2, s1))
                    spans = new
                for s0, s1 in spans:
                    k = max(1, int(math.ceil((s1 - s0) / 2.4)))
                    w = (s1 - s0 - 0.12 * (k - 1)) / k
                    for j in range(k):
                        u0 = s0 + j * (w + 0.12)
                        ops.append({'u0': u0, 'u1': u0 + w, 'y0': 0.55, 'y1': 2.75, 'kind': 'shop', 'storey': 0})
                        if 3.3 < gh - 1.1:
                            ops.append({'u0': u0, 'u1': u0 + w, 'y0': 2.85, 'y1': 3.25, 'kind': 'transom', 'storey': 0})

            for (su0, su1, owner) in owners:
                st = owner.storeys if owner is not None else self.storeys
                eave = owner.eave if owner is not None else self.eave
                margin = 0.7 if fam in ('house', 'block', 'mill') else 1.0
                for s in range(st):
                    if s == 0 and shopfront:
                        continue
                    fy = 0.0 if s == 0 else self.groundH + (s - 1) * self.storeyH
                    ww, wh, sill, bay = W['w'], W['h'], W['sill'], W['bay']
                    if fam == 'block' and i != street:
                        if s == 0:
                            ww, wh, sill, bay = 0.9, 1.5, 1.0, 3.4
                        else:
                            bay = 3.0
                    if fam == 'warehouse':
                        if s != st - 1 or L < 10 or i == street:
                            continue
                        fy = 0.0
                    if fam == 'barn' and m in ('garage', 'shed'):
                        if L < 3.0 or i in self.doors_by_edge or self.u('win%d' % i) > 0.6:
                            continue
                        bay = 99
                    if fam == 'barn' and m == 'barn' and i in self.doors_by_edge:
                        continue
                    y0 = fy + sill
                    y1 = min(y0 + wh, fy + (self.groundH if s == 0 else self.storeyH) - 0.25, eave - 0.3)
                    if y1 - y0 < 0.5:
                        continue
                    avail = (su1 - margin) - (su0 + margin)
                    if avail < ww:
                        continue
                    k = max(1, int(round(avail / bay)))
                    while k > 1 and avail / k < ww + 0.5:
                        k -= 1
                    for j in range(k):
                        uc = su0 + margin + avail * (j + 0.5) / k
                        u0, u1 = uc - ww / 2, uc + ww / 2
                        if not free(u0, u1):
                            continue
                        if s == 0 and any(not (u1 <= d0 or u0 >= d1) for d0, d1 in door_iv):
                            continue
                        if any(o['kind'] == 'loading' and not (u1 + 0.4 <= o['u0'] or u0 - 0.4 >= o['u1']) for o in ops):
                            continue
                        if y1 > top_min(u0, u1) - 0.15:
                            continue
                        ops.append({'u0': u0, 'u1': u1, 'y0': y0, 'y1': y1, 'kind': 'window', 'storey': s})
                # attic or loft window in a gable
                if owner is not None and fam in ('house', 'mill', 'barn') and m != 'shed' and su1 - su0 > 3.0:
                    peak_u, peak_y = max(((u, y) for u, y in tops if su0 - 1e-6 <= u <= su1 + 1e-6), key=lambda t: (t[1], -t[0]))
                    rise = peak_y - eave
                    if rise >= 2.2 and free(peak_u - 0.6, peak_u + 0.6):
                        if fam == 'barn' and m == 'barn':
                            w, h, kind = 1.8, min(1.6, rise - 1.0), 'hay'
                        else:
                            w, h = (0.7, min(1.0, rise - 1.1)) if fam == 'house' else (1.0, min(1.5, rise - 1.0))
                            kind = 'window'
                        y0 = eave + 0.35
                        u0, u1 = peak_u - w / 2, peak_u + w / 2
                        if h >= 0.5 and y0 + h <= min(interp(tops, u0), interp(tops, u1)) - 0.15 and su0 + 0.3 <= u0 and u1 <= su1 - 0.3:
                            ops.append({'u0': u0, 'u1': u1, 'y0': y0, 'y1': y0 + h, 'kind': kind, 'storey': st})
            ops.sort(key=lambda o: (o['storey'], o['u0'], o['y0'], o['kind']))
            res[i] = (ops, regions, tops, owners)
        self._open = res
        return res

    # ------------------------------------------------------------------------------------------------ swatches by height

    def wall_swatch(self, y, owner_eave):
        fam, C = self.family, self.C
        if y < (0.5 if fam == 'mill' else (0.6 if fam == 'warehouse' else 0.0)):
            return C['found']
        if fam == 'house' and self.lod < 2:
            if y < 0.18:
                return C['trim']
            if owner_eave - 0.25 <= y < owner_eave:
                return C['trim']
        if fam == 'barn' and self.model == 'garage' and self.lod < 2 and owner_eave - 0.2 <= y < owner_eave:
            return C['trim']
        return C['wall']

    def wall_splits(self, owner_eaves):
        ys = {self.ybot, 0.0}
        if self.family == 'mill':
            ys.add(0.5)
        if self.family == 'warehouse':
            ys.add(0.6)
        for e in owner_eaves:
            if self.family == 'house':
                ys.update({0.18, e - 0.25, e})
            if self.model == 'garage':
                ys.update({e - 0.2, e})
        return ys

    # ------------------------------------------------------------------------------------------------ LOD meshes

    def build_lod(self, lod):
        body = G.MeshBuilder(self.pal)
        panes = G.MeshBuilder(self.pal)
        self.pane_counter = 0
        self.lod = lod
        if lod == 0:
            self.markers = []
        plan = self.plan_openings()
        n = len(self.poly)
        for i in range(n):
            a, b = self.poly[i], self.poly[(i + 1) % n]
            ops, regions, tops, owners = plan[i]
            self.wall(body, panes, i, a, b, ops, regions, tops, owners, lod)
        if self.flat:
            self.flat_roof(body, lod)
        else:
            for r in self.rects:
                self.roof(body, r, lod)
            self.junction_walls(body)
        self.extras(body, panes, lod)
        return body, panes

    # ---- walls

    def wall(self, body, panes, i, a, b, ops, regions, tops, owners, lod):
        wf = WallFrame(a, b)
        holes = ops if lod == 0 else []
        eaves = [o[2].eave if o[2] is not None else self.eave for o in owners]

        def swatch(u, y):
            for (r0, r1, y0, y1, sw) in reversed(regions):
                if r0 <= u <= r1 and y0 <= y <= y1:
                    return sw
            e = self.eave
            for (u0, u1, o) in owners:
                if u0 - 1e-6 <= u <= u1 + 1e-6:
                    e = o.eave if o is not None else self.eave
                    break
            return self.wall_swatch(y, e)

        ys = self.wall_splits(eaves)
        if lod == 2:
            ys = {self.ybot, 0.0} | ({0.5} if self.family == 'mill' else set()) | ({0.6} if self.family == 'warehouse' else set())
        for (r0, r1, y0, y1, sw) in regions:
            ys.update({y0, y1})
        us = {0.0, wf.L}
        for (r0, r1, y0, y1, sw) in regions:
            us.update({r0, r1})
        for o in holes:
            us.update({o['u0'], o['u1']})
            ys.update({o['y0'], o['y1']})
        wall_grid(body, wf, self.ybot, tops, sorted(us), sorted(ys), holes, swatch)
        for o in ops:
            self.opening(body, panes, wf, o, lod)
        # corner boards on houses (LOD0) at convex corners
        if lod == 0 and self.family == 'house' and self.detail == 0:
            prev = self.poly[i - 1]
            if G.cross(G.sub(a, prev), G.sub(b, a)) > 1e-6:
                ve = min(interp(tops, 0.0), self.eave)
                pw = WallFrame(prev, a)
                wbox(body, wf, 0.0, 0.14, 0.0, ve, 0.0, 0.03, self.C['trim'], skip=('-v', '-u'))
                wbox(body, pw, pw.L - 0.14, pw.L, 0.0, ve, 0.0, 0.03, self.C['trim'], skip=('-v', '+u'))

    def opening(self, body, panes, wf, o, lod):
        u0, u1, y0, y1, kind = o['u0'], o['u1'], o['y0'], o['y1'], o['kind']
        fam, C = self.family, self.C
        glass = kind in ('window', 'shop', 'transom')
        pane_kind = 'shop' if kind in ('shop', 'transom') and fam == 'block' else 'window'
        if lod > 0:
            off = 0.02 if lod == 1 else 0.05
            if glass:
                if lod == 1 and kind == 'window':
                    fs = C['trim'] if fam != 'block' else C.get('front', C['trim'])
                    body.quad(wf.P(u0 - 0.08, y0 - 0.08, off * 0.5), wf.P(u1 + 0.08, y0 - 0.08, off * 0.5),
                              wf.P(u1 + 0.08, y1 + 0.08, off * 0.5), wf.P(u0 - 0.08, y1 + 0.08, off * 0.5), fs, wf.out)
                pane(panes, wf, u0, u1, y0, y1, off, self.pane_colour(o['storey'], pane_kind))
            else:
                body.quad(wf.P(u0, y0, off), wf.P(u1, y0, off), wf.P(u1, y1, off), wf.P(u0, y1, off), self.leaf_swatch(o), wf.out)
                if kind == 'door' and o['door']['kind'] == 'Main' and fam in ('house', 'block'):
                    self.pane_counter += 1   # keep pane ids aligned with LOD0's door glass
            return
        dep = 0.16 if kind in ('door', 'loading', 'hay') else (0.1 if kind in ('shop', 'transom') else OPENING_DEPTH)
        if fam in ('house', 'mill', 'barn'):
            rev = C['trim']
        elif kind in ('shop', 'transom') or (kind == 'door' and fam == 'block'):
            rev = C.get('front', C['trim'])
        else:
            rev = C['trim']
        body.quad(wf.P(u0, y1, 0), wf.P(u1, y1, 0), wf.P(u1, y1, -dep), wf.P(u0, y1, -dep), rev, (0, -1, 0))
        body.quad(wf.P(u0, y0, 0), wf.P(u0, y0, -dep), wf.P(u1, y0, -dep), wf.P(u1, y0, 0), rev, (0, 1, 0))
        body.quad(wf.P(u0, y0, 0), wf.P(u0, y1, 0), wf.P(u0, y1, -dep), wf.P(u0, y0, -dep), rev, wf.along)
        body.quad(wf.P(u1, y0, 0), wf.P(u1, y0, -dep), wf.P(u1, y1, -dep), wf.P(u1, y1, 0), rev, wf.back_along)
        if glass:
            pane(panes, wf, u0, u1, y0, y1, -dep, self.pane_colour(o['storey'], pane_kind))
            self.window_trim(body, wf, o, dep)
        else:
            body.quad(wf.P(u0, y0, -dep), wf.P(u1, y0, -dep), wf.P(u1, y1, -dep), wf.P(u0, y1, -dep), self.leaf_swatch(o), wf.out)
            self.door_trim(body, panes, wf, o, dep)
            if kind == 'door':
                d = o['door']
                self.markers.append(('DOOR_%s_%02d' % (d['kind'], d['spec']), wf.P((u0 + u1) / 2, y0, 0.0),
                                     {'kind': d['kind'], 'width': round(u1 - u0, 4), 'height': round(y1 - y0, 4), 'spec': d['spec']}))

    def leaf_swatch(self, o):
        C = self.C
        if o['kind'] == 'hay':
            return C['wall']
        if o['kind'] == 'loading':
            return C['door']
        k = o['door']['kind']
        if k in ('Garage', 'Bay', 'Dock') and self.model == 'barn':
            return C['wall']
        return C['door']

    def window_trim(self, body, wf, o, dep):
        """LOD0: frame ring, sill, muntins, lintel, shutters."""
        u0, u1, y0, y1, kind = o['u0'], o['u1'], o['y0'], o['y1'], o['kind']
        fam, C = self.family, self.C
        if kind in ('shop', 'transom'):
            return
        trim = C['trim']
        if fam == 'block':
            wbox(body, wf, u0 - 0.12, u1 + 0.12, y1, y1 + 0.22, 0.0, 0.04, trim)
            wbox(body, wf, u0 - 0.08, u1 + 0.08, y0 - 0.08, y0, 0.0, 0.07, trim)
            muntins(body, wf, u0, u1, y0, y1, -dep + 0.02, C.get('front', trim), rows=1, cols=0)
            return
        fw = 0.12 if fam == 'mill' else 0.09
        if self.detail >= 2:
            # budget fallback: a flat frame instead of the ring
            body.quad(wf.P(u0 - fw, y0 - fw, 0.012), wf.P(u1 + fw, y0 - fw, 0.012), wf.P(u1 + fw, y1 + fw, 0.012), wf.P(u0 - fw, y1 + fw, 0.012), trim, wf.out)
        else:
            ring(body, wf, u0, u1, y0, y1, fw, 0.035, trim)
        if fam in ('house', 'mill'):
            wbox(body, wf, u0 - fw - 0.04, u1 + fw + 0.04, y0 - fw - 0.06, y0 - fw, 0.0, 0.08, trim)
        if self.detail >= 1:
            return
        if fam == 'house':
            muntins(body, wf, u0, u1, y0, y1, -dep + 0.02, trim, rows=1, cols=1)
            if C.get('shutter') and o['storey'] < self.storeys:
                sw = (u1 - u0) / 2
                for s0, s1 in ((u0 - fw - sw, u0 - fw), (u1 + fw, u1 + fw + sw)):
                    wbox(body, wf, s0, s1, y0, y1, 0.0, 0.035, C['shutter'])
        elif fam == 'mill':
            muntins(body, wf, u0, u1, y0, y1, -dep + 0.02, trim, rows=2, cols=2)
        else:
            muntins(body, wf, u0, u1, y0, y1, -dep + 0.02, trim, rows=1, cols=1)

    def door_trim(self, body, panes, wf, o, dep):
        u0, u1, y0, y1, kind = o['u0'], o['u1'], o['y0'], o['y1'], o['kind']
        fam, C = self.family, self.C
        trim = C['trim']
        if kind in ('loading', 'hay'):
            ring(body, wf, u0, u1, y0, y1, 0.1, 0.03, trim)
            return
        k = o['door']['kind']
        if k in ('Main', 'Service') and fam in ('house', 'mill', 'barn'):
            ring(body, wf, u0, u1, y0, y1, 0.1, 0.035, trim)
        if k == 'Main' and fam == 'house':
            gu0, gu1 = u0 + 0.18 * (u1 - u0), u1 - 0.18 * (u1 - u0)
            pane(panes, wf, gu0, gu1, y1 - 0.85, y1 - 0.2, -dep + 0.01, self.pane_colour(0, 'door'))
        elif k == 'Main' and fam == 'block':
            pane(panes, wf, u0 + 0.12, u1 - 0.12, y0 + 0.9, y1 - 0.12, -dep + 0.01, self.pane_colour(0, 'door'))
        elif k in ('Garage', 'Bay', 'Dock'):
            nb = 5 if k == 'Bay' else 4
            for j in range(1, nb):
                yy = y0 + (y1 - y0) * j / nb
                body.quad(wf.P(u0 + 0.05, yy - 0.025, -dep + 0.01), wf.P(u1 - 0.05, yy - 0.025, -dep + 0.01),
                          wf.P(u1 - 0.05, yy + 0.025, -dep + 0.01), wf.P(u0 + 0.05, yy + 0.025, -dep + 0.01), 'interior_dark', wf.out)
            ring(body, wf, u0, u1, y0, y1, 0.1, 0.04, trim)
            if k == 'Dock':
                for s0 in (u0 - 0.35, u1 + 0.1):
                    wbox(body, wf, s0, s0 + 0.25, y0 - 0.55, y0 - 0.05, 0.0, 0.12, 'rubber')
                wbox(body, wf, u0 - 0.3, u1 + 0.3, y1 + 0.1, y1 + 0.45, 0.0, 0.5, 'rubber')
        if k == 'Bay' and self.model == 'barn':
            wbox(body, wf, u0 - 0.6, u1 + 0.6, y1 + 0.05, y1 + 0.25, 0.0, 0.08, trim)
            for (p0, p1) in (((u0, y0), (u1, y1)), ((u0, y1), (u1, y0))):
                strip(body, wf, p0, p1, 0.12, -dep + 0.02, trim)

    # ---- roofs

    def roof(self, body, r, lod):
        t = 0.0 if lod == 2 else {'house': 0.18, 'mill': 0.2}.get(self.family, 0.14 if self.model in ('garage', 'shed') else 0.12)
        sw = self.C['roof']
        under = self.C['trim'] if self.family in ('house', 'mill') or self.model in ('garage', 'shed') else self.C['wall']
        if r.shape == 'hip':
            self.hip_roof(body, r, t, sw, under, lod)
            return
        if r.along == 'x':
            s0, s1 = r.x0 - r.over['x0'] - r.ext['x0'], r.x1 + r.over['x1'] + r.ext['x1']
            w0, w1 = r.z0 - r.over['z0'] - r.ext['z0'], r.z1 + r.over['z1'] + r.ext['z1']
            P = lambda s, w, y: self.loc3(s, w, y)
            wa, wb = r.z0, r.z1
        else:
            s0, s1 = r.z0 - r.over['z0'] - r.ext['z0'], r.z1 + r.over['z1'] + r.ext['z1']
            w0, w1 = r.x0 - r.over['x0'] - r.ext['x0'], r.x1 + r.over['x1'] + r.ext['x1']
            P = lambda s, w, y: self.loc3(w, s, y)
            wa, wb = r.x0, r.x1
        wc = (wa + wb) / 2

        def under_y(w):
            if r.shape == 'shed':
                if getattr(r, 'flip', False):
                    return r.eave + (wb - w) * TAN(r.pitch)
                return r.eave + (w - wa) * TAN(r.pitch)
            d = r.half - abs(w - wc)
            if d < 0:
                return r.eave + d * TAN(r.lower if r.shape == 'gambrel' else r.pitch)
            return r.eave + r.rise(d)

        ws = [w0, w1]
        if r.shape in ('gable', 'gambrel'):
            ws.append(wc)
        if r.shape == 'gambrel':
            dk = r.half * 0.42
            ws += [wa + dk, wb - dk]
        ws = sorted(set(round(w, 9) for w in ws if w0 - 1e-9 <= w <= w1 + 1e-9))
        prof = [(w, under_y(w)) for w in ws]
        up, down = (0, 1, 0), (0, -1, 0)
        for k in range(len(prof) - 1):
            (wA, yA), (wB, yB) = prof[k], prof[k + 1]
            body.quad(P(s0, wA, yA + t), P(s1, wA, yA + t), P(s1, wB, yB + t), P(s0, wB, yB + t), sw, up)
            body.quad(P(s0, wA, yA), P(s0, wB, yB), P(s1, wB, yB), P(s1, wA, yA), under, down)
            if t > 0:
                for s, sgn in ((s0, -1), (s1, 1)):
                    body.quad(P(s, wA, yA), P(s, wA, yA + t), P(s, wB, yB + t), P(s, wB, yB), sw, self.dir3(sgn, 0, r.along))
        if t > 0:
            for (w, y), sgn in ((prof[0], -1), (prof[-1], 1)):
                body.quad(P(s0, w, y), P(s1, w, y), P(s1, w, y + t), P(s0, w, y + t), self.C['trim'], self.dir3(0, sgn, r.along))
        if lod == 0 and r.shape in ('gable', 'gambrel'):
            yR = under_y(wc) + t
            cap = 'metal_galv' if sw.startswith('metal') else sw
            hw = 0.16
            yl = under_y(wc - hw) + t
            body.quad(P(s0, wc - hw, yl), P(s1, wc - hw, yl), P(s1, wc, yR + 0.06), P(s0, wc, yR + 0.06), cap, up)
            body.quad(P(s0, wc, yR + 0.06), P(s1, wc, yR + 0.06), P(s1, wc + hw, yl), P(s0, wc + hw, yl), cap, up)

    def hip_roof(self, body, r, t, sw, under, lod):
        o = max(r.over.values()) if any(r.over.values()) else 0.4
        xa, xb, za, zb = r.x0 - o, r.x1 + o, r.z0 - o, r.z1 + o
        ye = r.eave - o * TAN(r.pitch)
        longx = (xb - xa) >= (zb - za)
        if longx:
            hw = (zb - za) / 2
            ra, rb = (xa + hw, (za + zb) / 2), (xb - hw, (za + zb) / 2)
        else:
            hw = (xb - xa) / 2
            ra, rb = ((xa + xb) / 2, za + hw), ((xa + xb) / 2, zb - hw)
        yr = ye + hw * TAN(r.pitch)
        c = [(xa, za), (xb, za), (xb, zb), (xa, zb)]
        if longx:
            planes = [[c[0], c[1], rb, ra], [c[1], c[2], rb], [c[2], c[3], ra, rb], [c[3], c[0], ra]]
        else:
            planes = [[c[0], c[1], ra], [c[1], c[2], rb, ra], [c[2], c[3], rb], [c[3], c[0], ra, rb]]
        for pl in planes:
            uniq = []
            for p in pl:
                y = yr if p is ra or p is rb else ye
                if not uniq or G.length(G.sub(p, uniq[-1][0])) > 1e-6:
                    uniq.append((p, y))
            if len(uniq) > 1 and G.length(G.sub(uniq[0][0], uniq[-1][0])) < 1e-6:
                uniq.pop()
            body.face([self.loc3(p[0], p[1], y + t) for p, y in uniq], sw, (0, 1, 0))
            body.face([self.loc3(p[0], p[1], y) for p, y in uniq], self.C['trim'] if lod < 2 else sw, (0, -1, 0))
        if t > 0:
            for k in range(4):
                p, q = c[k], c[(k + 1) % 4]
                body.quad(self.loc3(p[0], p[1], ye), self.loc3(q[0], q[1], ye), self.loc3(q[0], q[1], ye + t), self.loc3(p[0], p[1], ye + t),
                          self.C['trim'], self.rot_dir(G.out_normal(p, q)))

    def junction_walls(self, body):
        """Where one rectangle's roof stands above its neighbour's, the wall between them shows: build that part."""
        for (i, j, sa, sb, s0, s1) in getattr(self, 'junctions', []):
            for (k, m, sk) in ((i, j, sa), (j, i, sb)):
                R, N = self.rects[k], self.rects[m]
                if R.ext[sk] > 0:
                    continue
                c = getattr(R, sk)
                A, B = ((c, s0), (c, s1)) if sk[0] == 'x' else ((s0, c), (s1, c))
                ts = {0.0, 1.0}
                for rr in (R, N):
                    for (ca, cb, cc) in rr.lines():
                        fa = ca * A[0] + cb * A[1] - cc
                        fb = ca * B[0] + cb * B[1] - cc
                        if (fa < -1e-9 and fb > 1e-9) or (fa > 1e-9 and fb < -1e-9):
                            ts.add(fa / (fa - fb))
                ts = sorted(ts)
                out3 = self.rot_dir({'x0': (-1, 0), 'x1': (1, 0), 'z0': (0, -1), 'z1': (0, 1)}[sk])
                for q in range(len(ts) - 1):
                    pa, pb = G.lerp(A, B, ts[q]), G.lerp(A, B, ts[q + 1])
                    ta, tb = R.h(*pa), R.h(*pb)
                    ba, bb = min(N.h(*pa), ta), min(N.h(*pb), tb)
                    if ta - ba < 1e-3 and tb - bb < 1e-3:
                        continue
                    body.face([self.loc3(pa[0], pa[1], ba), self.loc3(pb[0], pb[1], bb), self.loc3(pb[0], pb[1], tb), self.loc3(pa[0], pa[1], ta)],
                              self.C['wall'], out3)

    def flat_roof(self, body, lod):
        C = self.C
        top = self.top
        inner = G.inset(self.poly, WALL_T)
        for tri in G.triangulate(inner):
            body.face([(inner[k][0], self.eave, inner[k][1]) for k in tri], C['roof'], (0, 1, 0))
        n = len(self.poly)
        for k in range(n):
            a, b = inner[k], inner[(k + 1) % n]
            nrm = G.out_normal(a, b)
            body.quad((a[0], self.eave, a[1]), (b[0], self.eave, b[1]), (b[0], top, b[1]), (a[0], top, a[1]), C['wall'], (-nrm[0], 0, -nrm[1]))
        cop = C['trim']
        if lod == 2:
            for k in range(n):
                a, b, ia, ib = self.poly[k], self.poly[(k + 1) % n], inner[k], inner[(k + 1) % n]
                body.quad((a[0], top, a[1]), (b[0], top, b[1]), (ib[0], top, ib[1]), (ia[0], top, ia[1]), cop, (0, 1, 0))
            return
        outer = G.inset(self.poly, -0.05)
        inner2 = G.inset(self.poly, WALL_T + 0.04)
        yc, yl = top + 0.06, top - 0.06
        for k in range(n):
            a, b = outer[k], outer[(k + 1) % n]
            ia, ib = inner2[k], inner2[(k + 1) % n]
            pa, pb = self.poly[k], self.poly[(k + 1) % n]
            iia, iib = inner[k], inner[(k + 1) % n]
            nrm = G.out_normal(pa, pb)
            body.quad((a[0], yc, a[1]), (b[0], yc, b[1]), (ib[0], yc, ib[1]), (ia[0], yc, ia[1]), cop, (0, 1, 0))
            body.quad((a[0], yl, a[1]), (b[0], yl, b[1]), (b[0], yc, b[1]), (a[0], yc, a[1]), cop, (nrm[0], 0, nrm[1]))
            body.quad((pa[0], yl, pa[1]), (pb[0], yl, pb[1]), (b[0], yl, b[1]), (a[0], yl, a[1]), cop, (0, -1, 0))
            body.quad((ia[0], yl, ia[1]), (ib[0], yl, ib[1]), (ib[0], yc, ib[1]), (ia[0], yc, ia[1]), cop, (-nrm[0], 0, -nrm[1]))
            body.quad((iia[0], yl, iia[1]), (iib[0], yl, iib[1]), (ib[0], yl, ib[1]), (ia[0], yl, ia[1]), cop, (0, -1, 0))
        if self.family == 'block' and lod == 0:
            self.cornice(body)
        if lod == 0 and self.family in ('block', 'warehouse'):
            c = G.centroid(inner)
            for j in range(2 if self.family == 'warehouse' else 1):
                ang = 2 * math.pi * self.u('vent%d' % j)
                rr = 0.25 * min(G.length(G.sub(p, c)) for p in inner)
                p = (c[0] + math.cos(ang) * rr, c[1] + math.sin(ang) * rr)
                body.box((p[0], self.eave + 0.35, p[1]), (1, 0), (0, 1), (0.6, 0.35, 0.6), 'vent_grey', skip=('-y',))

    def cornice(self, body):
        """Projecting cornice under the parapet top on the free edges (not on party walls)."""
        C = self.C
        n = len(self.poly)
        outer = G.inset(self.poly, -0.3)
        y0, y1 = self.top - 0.85, self.top - 0.5
        for k in range(n):
            if k in self.party:
                continue
            a, b = self.poly[k], self.poly[(k + 1) % n]
            nrm = G.out_normal(a, b)
            oa, ob = outer[k], outer[(k + 1) % n]
            prev_party, next_party = ((k - 1) % n) in self.party, ((k + 1) % n) in self.party
            if prev_party:
                oa = G.add(a, G.mul(nrm, 0.3))
            if next_party:
                ob = G.add(b, G.mul(nrm, 0.3))
            body.quad((oa[0], y0, oa[1]), (ob[0], y0, ob[1]), (ob[0], y1, ob[1]), (oa[0], y1, oa[1]), C['trim'], (nrm[0], 0, nrm[1]))
            body.quad((a[0], y1, a[1]), (oa[0], y1, oa[1]), (ob[0], y1, ob[1]), (b[0], y1, b[1]), C['trim'], (0, 1, 0))
            body.quad((a[0], y0, a[1]), (b[0], y0, b[1]), (ob[0], y0, ob[1]), (oa[0], y0, oa[1]), C['trim'], (0, -1, 0))
            d = G.norm(G.sub(b, a))
            if prev_party:
                body.quad((a[0], y0, a[1]), (oa[0], y0, oa[1]), (oa[0], y1, oa[1]), (a[0], y1, a[1]), C['trim'], (-d[0], 0, -d[1]))
            if next_party:
                body.quad((b[0], y0, b[1]), (ob[0], y0, ob[1]), (ob[0], y1, ob[1]), (b[0], y1, b[1]), C['trim'], (d[0], 0, d[1]))

    # ---- extras: porch, steps, chimney, cupola, awning, sign, hoist

    def extras(self, body, panes, lod):
        fam, C, F = self.family, self.C, self.F
        n = len(self.poly)
        plan = self.plan_openings()
        porch_door = None
        if fam == 'house' and F.get('porch'):
            mains = [d for d in self.rec['doors'] if d['kind'] == 'Main']
            if mains:
                porch_door = mains[0]
                self.porch(body, mains[0], lod)
        if fam == 'house' and lod < 2:
            for d in self.rec['doors']:
                if porch_door is not None and d is porch_door and self.porch_geom is not None:
                    continue
                a, b = self.poly[d['edge']], self.poly[(d['edge'] + 1) % n]
                wf = WallFrame(a, b)
                self.steps(body, wf, d['u'], d['width'] + 0.4, -0.02, self.ground_at(d), 1.0, 'concrete')
        if fam == 'house' and F.get('chimney') and self.rects and lod < 2:
            self.chimney(body, lod)
        if fam == 'mill' and F.get('cupola') and self.rects and lod < 2:
            self.cupola(body, lod)
        if fam == 'mill' and getattr(self, 'hoist', None) and lod == 0:
            e, uu = self.hoist
            wf = WallFrame(self.poly[e], self.poly[(e + 1) % n])
            ys = [o['y1'] for o in plan[e][0] if o['kind'] == 'loading']
            if ys:
                y = max(ys) + 0.35
                wbox(body, wf, uu - 0.12, uu + 0.12, y, y + 0.3, 0.0, 1.3, 'wood_dark')
        if fam == 'block' and lod < 2:
            st = self.rec.get('streetEdge')
            if st is not None and st not in self.party:
                wf = WallFrame(self.poly[st], self.poly[(st + 1) % n])
                gh = self.groundH
                if lod == 0:
                    wbox(body, wf, 0.65, wf.L - 0.65, gh - 1.0, gh - 0.35, 0.0, 0.06, C['sign'])
                    wbox(body, wf, 0.45, wf.L - 0.45, gh - 0.3, gh - 0.05, 0.0, 0.18, C['front'])
                if C.get('awning'):
                    self.awning(body, wf, plan[st][0], lod)

    def ground_at(self, door):
        """Ground just outside a door (local y, from the DEM 1.2 m out; plan.py), kept between the skirt and the floor."""
        g = door.get('ground', self.rec['ground']['min'])
        return max(self.ybot + 0.3, min(-0.05, g))

    def steps(self, body, wf, uc, width, top_y, ground_y, landing, sw):
        rise = top_y - ground_y
        if rise < 0.12:
            return
        nsteps = max(1, int(math.ceil(rise / 0.19)))
        h = rise / nsteps
        u0, u1 = uc - width / 2, uc + width / 2
        wbox(body, wf, u0, u1, ground_y - 0.1, top_y, 0.0, landing, sw)
        for k in range(1, nsteps):
            yt = top_y - k * h
            v0 = landing + (k - 1) * 0.28
            wbox(body, wf, u0, u1, ground_y - 0.1, yt, v0, v0 + 0.28, sw)

    def porch(self, body, door, lod):
        C, F = self.C, self.F
        n = len(self.poly)
        e = door['edge']
        wf = WallFrame(self.poly[e], self.poly[(e + 1) % n])
        D = F['porch']['depth']
        if D < 1.4:
            return
        L = wf.L
        if F['porch'].get('full') or L < 9:
            p0, p1 = 0.25, L - 0.25
        else:
            p0, p1 = max(0.25, door['u'] - 2.4), min(L - 0.25, door['u'] + 2.4)
        if p1 - p0 < 2.0:
            return
        ops, regions, tops, owners = self.plan_openings()[e]
        owner = None
        for (u0, u1, o) in owners:
            if u0 - 1e-6 <= door['u'] <= u1 + 1e-6:
                owner = o
        if owner is not None:
            dr = G.norm(G.sub(self.to_rot(wf.b), self.to_rot(wf.a)))
            eave_side = (owner.along == 'x') == (abs(dr[0]) > 0.7)
        else:
            eave_side = True
        if owner is not None and eave_side and owner.storeys == 1:
            y_att = owner.eave - 0.08
        else:
            y_att = self.groundH + 0.25
        y_att = min(y_att, interp(tops, p0) - 0.1, interp(tops, p1) - 0.1)
        slope = TAN(14)
        y_front = y_att - (D + 0.25) * slope
        if y_front - 0.12 < 2.2:
            return
        gy = self.ground_at(door)
        wbox(body, wf, p0, p1, -0.22, -0.04, 0.0, D, 'porch_floor' if lod < 2 else C['found'], skip=('-v', '-y'))
        if lod < 2:
            wbox(body, wf, p0 + 0.05, p1 - 0.05, gy - 0.1, -0.22, 0.0, D - 0.05, 'lattice', skip=('-v', '+y', '-y'))
        post_y1 = y_att - (D - 0.12) * slope
        k = max(1, int(math.ceil((p1 - p0) / 2.6)))
        us = [p0 + 0.1 + (p1 - p0 - 0.2) * j / k for j in range(k + 1)]
        if lod > 0:
            us = [us[0], us[-1]]
        for u in us:
            wbox(body, wf, u - 0.07, u + 0.07, -0.04, post_y1, D - 0.2, D - 0.06, C['trim'], skip=('-y',) if lod == 2 else ())
        if lod < 2:
            if lod == 0:
                for j in range(len(us) - 1):
                    ua, ub = us[j] + 0.07, us[j + 1] - 0.07
                    if ua - 0.8 < door['u'] < ub + 0.8 and (ub - ua) < 3.5:
                        continue
                    wbox(body, wf, ua, ub, 0.86, 0.93, D - 0.17, D - 0.09, C['trim'], skip=())
                    wbox(body, wf, ua, ub, 0.06, 0.12, D - 0.17, D - 0.09, C['trim'], skip=())
            self.steps(body, wf, door['u'], 1.4, -0.04, gy, D + 0.01, 'porch_floor')
        r0, r1 = p0 - 0.15, p1 + 0.15
        th = 0.0 if lod == 2 else 0.12
        A, B = wf.P(r0, y_att, 0.0), wf.P(r1, y_att, 0.0)
        Cc, Dd = wf.P(r1, y_front, D + 0.25), wf.P(r0, y_front, D + 0.25)
        lift = lambda p: (p[0], p[1] + th, p[2])
        body.quad(lift(A), lift(B), lift(Cc), lift(Dd), C['roof'], (0, 1, 0))
        body.quad(A, Dd, Cc, B, 'porch_ceiling', (0, -1, 0))
        if th > 0:
            body.quad(Dd, Cc, lift(Cc), lift(Dd), C['trim'], wf.out)
            body.quad(A, lift(A), lift(Dd), Dd, C['trim'], wf.back_along)
            body.quad(B, Cc, lift(Cc), lift(B), C['trim'], wf.along)
        self.porch_geom = (wf, p0, p1, D, gy, y_att)

    def chimney(self, body, lod):
        r = self.rects[0]
        side = 0.22 if self.u('chim.side') < 0.5 else 0.78
        if r.along == 'x':
            p = (r.x0 + (r.x1 - r.x0) * side, (r.z0 + r.z1) / 2)
        else:
            p = ((r.x0 + r.x1) / 2, r.z0 + (r.z1 - r.z0) * side)
        y_top = r.ridge_h + 0.9
        y_bot = r.h(*p) - 0.6
        c = self.from_rot(p)
        ax = self.rot_dir2((1, 0) if r.along == 'x' else (0, 1))
        nx = (-ax[1], ax[0])
        sw = 'brick_red' if self.C['found'] != 'brick_red' else 'brick_dark'
        body.box((c[0], (y_top + y_bot) / 2, c[1]), ax, nx, (0.4, (y_top - y_bot) / 2, 0.28), sw, skip=('-y',))
        if lod == 0:
            body.box((c[0], y_top + 0.06, c[1]), ax, nx, (0.47, 0.06, 0.35), 'chimney_cap', skip=())

    def cupola(self, body, lod):
        r = self.rects[0]
        c = self.from_rot(((r.x0 + r.x1) / 2, (r.z0 + r.z1) / 2))
        ax = self.rot_dir2((1, 0) if r.along == 'x' else (0, 1))
        nx = (-ax[1], ax[0])
        y0, y1 = r.ridge_h - 0.6, r.ridge_h + 1.5
        body.box((c[0], (y0 + y1) / 2, c[1]), ax, nx, (1.1, (y1 - y0) / 2, 1.1), self.C['trim'], skip=('-y',))
        if lod == 0:
            body.box((c[0], y1 - 0.6, c[1]), ax, nx, (1.12, 0.35, 1.12), 'vent_grey', skip=('-y', '+y'))
        o = 0.25
        corners = [(-1.1 - o, -1.1 - o), (1.1 + o, -1.1 - o), (1.1 + o, 1.1 + o), (-1.1 - o, 1.1 + o)]
        pts = [(c[0] + ax[0] * u + nx[0] * v, c[1] + ax[1] * u + nx[1] * v) for u, v in corners]
        apex = (c[0], y1 + 1.2, c[1])
        for k in range(4):
            pa, pb = pts[k], pts[(k + 1) % 4]
            body.face([(pa[0], y1, pa[1]), (pb[0], y1, pb[1]), apex], self.C['roof'], (0, 1, 0))
            body.face([(pa[0], y1, pa[1]), (pb[0], y1, pb[1]), (c[0], y1, c[1])], self.C['trim'], (0, -1, 0))

    def awning(self, body, wf, ops, lod):
        shops = [o for o in ops if o['kind'] == 'shop']
        if not shops:
            return
        u0, u1 = min(o['u0'] for o in shops) - 0.15, max(o['u1'] for o in shops) + 0.15
        y = 3.3 if self.groundH - 1.1 > 3.3 else 2.95
        dep, drop = 1.3, 0.55
        sw = self.C['awning']
        A, B = wf.P(u0, y, 0.0), wf.P(u1, y, 0.0)
        Cc, Dd = wf.P(u1, y - drop, dep), wf.P(u0, y - drop, dep)
        body.quad(A, B, Cc, Dd, sw, (0, 1, 0))
        body.quad(A, Dd, Cc, B, sw, (0, -1, 0))
        if lod == 0:
            E, Fp = wf.P(u0, y - drop - 0.25, dep), wf.P(u1, y - drop - 0.25, dep)
            body.quad(Dd, Cc, Fp, E, sw, wf.out)
            body.quad(Cc, Dd, E, Fp, sw, wf.inward)
            body.face([A, Dd, E], sw, wf.back_along)
            body.face([B, Fp, Cc], sw, wf.along)

    # ------------------------------------------------------------------------------------------------ colliders

    def colliders(self):
        """Convex hulls (lists of faces): prisms over the footprint's convex pieces up to the eave or parapet, one
        hull per roof rectangle, and the porch."""
        hulls = []
        for piece in G.convex_pieces(self.poly):
            poly = [self.poly[k] for k in piece]
            if self.flat or not self.rects:
                yt = self.top
            else:
                yt = self.owner(G.centroid(poly), (0.0, 0.0)).eave
            hulls.append(prism_faces(poly, self.ybot, yt))
        if not self.flat:
            for r in self.rects:
                hulls.append(self.roof_hull(r))
        if self.porch_geom is not None:
            wf, p0, p1, D, gy, y_att = self.porch_geom
            mb = G.MeshBuilder(self.pal)
            wbox(mb, wf, p0, p1, gy - 0.1, y_att, 0.02, D, None, skip=())
            hulls.append([f[0] for f in mb.faces])
        return hulls

    def roof_hull(self, r):
        e = r.eave
        if r.shape == 'hip':
            c = [(r.x0, r.z0), (r.x1, r.z0), (r.x1, r.z1), (r.x0, r.z1)]
            longx = (r.x1 - r.x0) >= (r.z1 - r.z0)
            hw = min(r.x1 - r.x0, r.z1 - r.z0) / 2
            if longx:
                ra, rb = (r.x0 + hw, (r.z0 + r.z1) / 2), (r.x1 - hw, (r.z0 + r.z1) / 2)
            else:
                ra, rb = ((r.x0 + r.x1) / 2, r.z0 + hw), ((r.x0 + r.x1) / 2, r.z1 - hw)
            yr = e + hw * TAN(r.pitch)
            p = [self.loc3(q[0], q[1], e) for q in c] + [self.loc3(ra[0], ra[1], yr), self.loc3(rb[0], rb[1], yr)]
            if longx:
                return [[p[0], p[1], p[2], p[3]], [p[0], p[1], p[5], p[4]], [p[1], p[2], p[5]], [p[2], p[3], p[4], p[5]], [p[3], p[0], p[4]]]
            return [[p[0], p[1], p[2], p[3]], [p[0], p[1], p[4]], [p[1], p[2], p[5], p[4]], [p[2], p[3], p[5]], [p[3], p[0], p[4], p[5]]]
        if r.along == 'x':
            S = lambda s, w, y: self.loc3(s, w, y)
            s0, s1, w0, w1 = r.x0, r.x1, r.z0, r.z1
        else:
            S = lambda s, w, y: self.loc3(w, s, y)
            s0, s1, w0, w1 = r.z0, r.z1, r.x0, r.x1
        if r.shape == 'shed':
            hi = r.ridge_h
            prof = [(w0, e), (w0, hi), (w1, e)] if getattr(r, 'flip', False) else [(w0, e), (w1, hi), (w1, e)]
        else:
            prof = [(w0, e), ((w0 + w1) / 2, r.ridge_h), (w1, e)]
        A = [S(s0, w, y) for w, y in prof]
        B = [S(s1, w, y) for w, y in prof]
        faces = [A, B]
        for k in range(len(prof)):
            k2 = (k + 1) % len(prof)
            faces.append([A[k], A[k2], B[k2], B[k]])
        return faces

    # ------------------------------------------------------------------------------------------------ frame helpers

    def loc3(self, xr, zr, y):
        p = self.from_rot((xr, zr))
        return (p[0], y, p[1])

    def rot_dir2(self, d):
        return G.rotate(d, self.theta)

    def rot_dir(self, d):
        q = G.rotate(d, self.theta)
        return (q[0], 0.0, q[1])

    def dir3(self, ds, dw, along):
        """A direction along s (the ridge) and w (across it), in local 3D."""
        return self.rot_dir((ds, dw)) if along == 'x' else self.rot_dir((dw, ds))


# ------------------------------------------------------------------------------------------------ module helpers

class WallFrame:
    """A wall edge a->b of the CCW footprint: u along the edge, y up, v outward."""

    def __init__(self, a, b):
        self.a, self.b = a, b
        self.L = G.length(G.sub(b, a))
        self.d = G.norm(G.sub(b, a))
        self.n = G.out_normal(a, b)
        self.out = (self.n[0], 0.0, self.n[1])
        self.inward = (-self.n[0], 0.0, -self.n[1])
        self.along = (self.d[0], 0.0, self.d[1])
        self.back_along = (-self.d[0], 0.0, -self.d[1])

    def P(self, u, y, v=0.0):
        return (self.a[0] + self.d[0] * u + self.n[0] * v, y, self.a[1] + self.d[1] * u + self.n[1] * v)


def interp(tops, u):
    """Wall top at u (the lower value at a step)."""
    best = None
    for (u0, y0), (u1, y1) in zip(tops, tops[1:]):
        if u0 - 1e-9 <= u <= u1 + 1e-9:
            y = y0 if u1 - u0 < 1e-9 else y0 + (y1 - y0) * (u - u0) / (u1 - u0)
            best = y if best is None else min(best, y)
    if best is None:
        best = tops[0][1] if u <= tops[0][0] else tops[-1][1]
    return best


def top_side(tops, u, side):
    """Wall top just right (side +1) or just left (-1) of u."""
    segs = list(zip(tops, tops[1:]))
    if side > 0:
        for (u0, y0), (u1, y1) in segs:
            if u0 - 1e-9 <= u < u1 - 1e-9:
                return y0 + (y1 - y0) * (u - u0) / (u1 - u0)
        return tops[-1][1]
    for (u0, y0), (u1, y1) in reversed(segs):
        if u0 + 1e-9 < u <= u1 + 1e-9:
            return y0 + (y1 - y0) * (u - u0) / (u1 - u0)
    return tops[0][1]


def wall_grid(mb, wf, ybot, tops, us, ys, holes, swatch):
    """The wall face between ybot and the top line, cut into a grid at every opening and band, minus the holes.
    No T-junctions: every column uses every height break, and every top-line vertex is a column break."""
    us = sorted(set([round(u, 6) for u in us] + [round(u, 6) for u, _ in tops]))
    us = [u for u in us if -1e-6 <= u <= wf.L + 1e-6]
    ymax = max(y for _, y in tops)
    ys = sorted(set(round(y, 6) for y in ys if ybot - 1e-6 <= y <= ymax + 1e-6) | {round(ybot, 6), round(ymax, 6)})
    for k in range(len(us) - 1):
        ua, ub = us[k], us[k + 1]
        if ub - ua < 1e-5:
            continue
        ta, tb = top_side(tops, ua, 1), top_side(tops, ub, -1)
        for j in range(len(ys) - 1):
            y0, y1 = ys[j], ys[j + 1]
            if y0 >= max(ta, tb) - 1e-6:
                break
            cl = G.clip_below([(ua, y0), (ub, y0), (ub, y1), (ua, y1)], (ua, ta), (ub, tb))
            if not cl:
                continue
            cu = sum(p[0] for p in cl) / len(cl)
            cy = sum(p[1] for p in cl) / len(cl)
            if any(o['u0'] + 1e-6 < cu < o['u1'] - 1e-6 and o['y0'] + 1e-6 < cy < o['y1'] - 1e-6 for o in holes):
                continue
            mb.face([wf.P(u, y) for u, y in cl], swatch(cu, cy), wf.out)


def wbox(mb, wf, u0, u1, y0, y1, v0, v1, swatch, skip=('-v',)):
    """A box on a wall: u, y and v (outward) ranges."""
    if u1 - u0 < 1e-4 or y1 - y0 < 1e-4 or v1 - v0 < 1e-4:
        return
    c = wf.P((u0 + u1) / 2, (y0 + y1) / 2, (v0 + v1) / 2)
    mb.box(c, wf.d, wf.n, ((u1 - u0) / 2, (y1 - y0) / 2, (v1 - v0) / 2), swatch, skip=skip)


def ring(mb, wf, u0, u1, y0, y1, w, proud, swatch):
    """A frame around an opening: front face, outer and inner sides (24 triangles)."""
    O = [(u0 - w, y0 - w), (u1 + w, y0 - w), (u1 + w, y1 + w), (u0 - w, y1 + w)]
    I = [(u0, y0), (u1, y0), (u1, y1), (u0, y1)]
    for k in range(4):
        o0, o1, i0, i1 = O[k], O[(k + 1) % 4], I[k], I[(k + 1) % 4]
        mb.quad(wf.P(o0[0], o0[1], proud), wf.P(o1[0], o1[1], proud), wf.P(i1[0], i1[1], proud), wf.P(i0[0], i0[1], proud), swatch, wf.out)
        du, dy = o1[1] - o0[1], -(o1[0] - o0[0])          # outward of the outer rectangle in (u, y)
        l = math.hypot(du, dy)
        d3 = (wf.d[0] * du / l, dy / l, wf.d[1] * du / l)
        mb.quad(wf.P(o0[0], o0[1], 0), wf.P(o1[0], o1[1], 0), wf.P(o1[0], o1[1], proud), wf.P(o0[0], o0[1], proud), swatch, d3)
        mb.quad(wf.P(i0[0], i0[1], 0), wf.P(i0[0], i0[1], proud), wf.P(i1[0], i1[1], proud), wf.P(i1[0], i1[1], 0), swatch, (-d3[0], -d3[1], -d3[2]))


def pane(mb, wf, u0, u1, y0, y1, v, colour):
    mb.face([wf.P(u0, y0, v), wf.P(u1, y0, v), wf.P(u1, y1, v), wf.P(u0, y1, v)], None, wf.out, colour)


def muntins(mb, wf, u0, u1, y0, y1, v, swatch, rows=1, cols=1):
    bw = 0.035
    for k in range(1, cols + 1):
        u = u0 + (u1 - u0) * k / (cols + 1)
        mb.quad(wf.P(u - bw, y0, v), wf.P(u + bw, y0, v), wf.P(u + bw, y1, v), wf.P(u - bw, y1, v), swatch, wf.out)
    for k in range(1, rows + 1):
        y = y0 + (y1 - y0) * k / (rows + 1)
        mb.quad(wf.P(u0, y - bw, v + 0.002), wf.P(u1, y - bw, v + 0.002), wf.P(u1, y + bw, v + 0.002), wf.P(u0, y + bw, v + 0.002), swatch, wf.out)


def strip(mb, wf, p0, p1, width, v, swatch):
    (ua, ya), (ub, yb) = p0, p1
    dx, dy = ub - ua, yb - ya
    l = math.hypot(dx, dy)
    nx, ny = -dy / l * width / 2, dx / l * width / 2
    mb.quad(wf.P(ua + nx, ya + ny, v), wf.P(ua - nx, ya - ny, v), wf.P(ub - nx, yb - ny, v), wf.P(ub + nx, yb + ny, v), swatch, wf.out)


def prism_faces(poly, y0, y1):
    n = len(poly)
    faces = [[(p[0], y0, p[1]) for p in poly], [(p[0], y1, p[1]) for p in poly]]
    for k in range(n):
        a, b = poly[k], poly[(k + 1) % n]
        faces.append([(a[0], y0, a[1]), (b[0], y0, b[1]), (b[0], y1, b[1]), (a[0], y1, a[1])])
    return faces
