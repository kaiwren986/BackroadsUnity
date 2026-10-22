"""Plain-Python geometry for the building generator (no bpy, so it runs and tests anywhere).

Frames. Plan points are (x, z) in the town frame (x east, z north, metres; ADR-006 TownFrame), 3D points are (x, y, z)
with y up: the same axes as Unity. Polygons are counter-clockwise seen from above with x right and z up, not closed.
Faces are written with their outward normal given by the right-hand rule on plain coordinates; MeshBuilder checks
every face against a hint and flips it if needed, and the Blender writer (br_buildings.py) maps and reverses them.
"""
import math

EPS = 1e-9


# ------------------------------------------------------------------------------------------------ 2D basics

def area2(poly):
    s = 0.0
    n = len(poly)
    for i in range(n):
        x0, y0 = poly[i]
        x1, y1 = poly[(i + 1) % n]
        s += x0 * y1 - x1 * y0
    return s


def area(poly):
    return abs(area2(poly)) * 0.5


def ensure_ccw(poly):
    return list(poly) if area2(poly) > 0 else list(reversed(poly))


def centroid(poly):
    a = area2(poly)
    if abs(a) < EPS:
        n = len(poly)
        return (sum(p[0] for p in poly) / n, sum(p[1] for p in poly) / n)
    cx = cy = 0.0
    n = len(poly)
    for i in range(n):
        x0, y0 = poly[i]
        x1, y1 = poly[(i + 1) % n]
        c = x0 * y1 - x1 * y0
        cx += (x0 + x1) * c
        cy += (y0 + y1) * c
    return (cx / (3 * a), cy / (3 * a))


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1])


def add(a, b):
    return (a[0] + b[0], a[1] + b[1])


def mul(a, s):
    return (a[0] * s, a[1] * s)


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1]


def cross(a, b):
    return a[0] * b[1] - a[1] * b[0]


def length(a):
    return math.hypot(a[0], a[1])


def norm(a):
    l = length(a)
    return (a[0] / l, a[1] / l) if l > EPS else (0.0, 0.0)


def lerp(a, b, t):
    return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)


def out_normal(a, b):
    """Outward normal of edge a->b of a CCW polygon (the interior is on the left)."""
    d = norm(sub(b, a))
    return (d[1], -d[0])


def edges(poly):
    n = len(poly)
    return [(poly[i], poly[(i + 1) % n]) for i in range(n)]


def seg_project(p, a, b):
    """(t in [0, 1], distance) of p's closest point on segment a-b."""
    ab = sub(b, a)
    l2 = dot(ab, ab)
    t = 0.0 if l2 < EPS else max(0.0, min(1.0, dot(sub(p, a), ab) / l2))
    q = lerp(a, b, t)
    return t, length(sub(p, q))


def point_in_poly(p, poly):
    x, y = p
    inside = False
    n = len(poly)
    j = n - 1
    for i in range(n):
        xi, yi = poly[i]
        xj, yj = poly[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def rotate(p, ang):
    c, s = math.cos(ang), math.sin(ang)
    return (p[0] * c - p[1] * s, p[0] * s + p[1] * c)


def is_convex(poly):
    n = len(poly)
    for i in range(n):
        if cross(sub(poly[(i + 1) % n], poly[i]), sub(poly[(i + 2) % n], poly[(i + 1) % n])) < -1e-7:
            return False
    return True


# ------------------------------------------------------------------------------------------------ simplification

def simplify(poly, tol=0.08, min_edge=0.15):
    """Drops vertices within tol of the line through their neighbours and merges vertices closer than min_edge.
    Returns the CCW polygon. The design allows 0.2 m (site-layouts 7a); 0.08 keeps doors within the 0.1 m test."""
    p = ensure_ccw([(float(x), float(z)) for x, z in poly])
    changed = True
    while changed and len(p) > 3:
        changed = False
        n = len(p)
        for i in range(n):
            a, b, c = p[i - 1], p[i], p[(i + 1) % n]
            if length(sub(b, a)) < min_edge:
                p.pop(i)
                changed = True
                break
            t, d = seg_project(b, a, c)
            if d < tol and 0.0 < t < 1.0:
                p.pop(i)
                changed = True
                break
    return p


def inset(poly, d):
    """Offsets every edge of a CCW polygon inward by d (mitred corners). Fine for d well under the edge lengths."""
    n = len(poly)
    lines = []
    for i in range(n):
        a, b = poly[i], poly[(i + 1) % n]
        nrm = out_normal(a, b)
        lines.append((add(a, mul(nrm, -d)), sub(b, a)))
    out = []
    for i in range(n):
        p0, d0 = lines[i - 1]
        p1, d1 = lines[i]
        den = cross(d0, d1)
        if abs(den) < 1e-9:
            out.append(p1)
        else:
            t = cross(sub(p1, p0), d1) / den
            out.append(add(p0, mul(d0, t)))
    return out


# ------------------------------------------------------------------------------------------------ orientation and rectangles

def dominant_angle(poly):
    """Angle (radians, in [0, pi/2)) of the polygon's main axes: the edge direction (mod 90 deg) that the most edge length
    agrees with within 10 deg."""
    es = edges(poly)
    best, best_w = 0.0, -1.0
    for a, b in es:
        ang = math.atan2(b[1] - a[1], b[0] - a[0]) % (math.pi / 2)
        w = 0.0
        for c, d in es:
            a2 = math.atan2(d[1] - c[1], d[0] - c[0]) % (math.pi / 2)
            dev = abs((a2 - ang + math.pi / 4) % (math.pi / 2) - math.pi / 4)
            if dev < math.radians(10):
                w += length(sub(d, c)) * (1 - dev / math.radians(10))
        if w > best_w + 1e-6:
            best, best_w = ang, w
    return best


def snap_orthogonal(poly_r, tol_deg=15.0):
    """poly_r: polygon already rotated into its main axes. Returns the axis-aligned (rectilinear) version with each
    horizontal edge at the length-weighted mean z of its ends and each vertical edge at the mean x, or None when an edge
    is more than tol_deg off both axes."""
    n = len(poly_r)
    kinds = []
    for i in range(n):
        a, b = poly_r[i], poly_r[(i + 1) % n]
        ang = math.degrees(math.atan2(b[1] - a[1], b[0] - a[0])) % 180.0
        if min(ang, 180.0 - ang) <= tol_deg:
            kinds.append('H')
        elif abs(ang - 90.0) <= tol_deg:
            kinds.append('V')
        else:
            return None
    # merge runs of the same kind (collinear after snapping)
    runs = []   # (kind, [vertex indices from start to end])
    start = 0
    while start < n and kinds[start] == kinds[start - 1]:
        start += 1
    if start == n:
        return None
    i = start
    while True:
        k = kinds[i % n]
        idx = [i % n]
        j = i
        while kinds[(j + 1) % n] == k and (j + 1) % n != start:
            j += 1
            idx.append(j % n)
        runs.append((k, idx + [(j + 1) % n]))
        i = j + 1
        if i % n == start:
            break
    if len(runs) < 4 or len(runs) % 2:
        return None
    coord = []
    for k, idx in runs:
        pts = [poly_r[v] for v in idx]
        c = sum(p[1] if k == 'H' else p[0] for p in pts) / len(pts)
        coord.append(c)
    out = []
    m = len(runs)
    for r in range(m):
        k0, c0 = runs[r - 1][0], coord[r - 1]
        k1, c1 = runs[r][0], coord[r]
        out.append((c1, c0) if k0 == 'H' else (c0, c1))   # vertex between run r-1 and run r
    return out


def rect_decompose(poly_o):
    """Axis-aligned rectangles that tile an axis-aligned polygon, largest first (greedy maximal rectangles on the
    grid of its vertex coordinates). Each rect is (x0, z0, x1, z1)."""
    xs = sorted(set(round(p[0], 6) for p in poly_o))
    zs = sorted(set(round(p[1], 6) for p in poly_o))
    nx, nz = len(xs) - 1, len(zs) - 1
    inside = [[point_in_poly(((xs[i] + xs[i + 1]) / 2, (zs[j] + zs[j + 1]) / 2), poly_o) for j in range(nz)] for i in range(nx)]
    used = [[False] * nz for _ in range(nx)]
    rects = []
    while True:
        best = None
        for i0 in range(nx):
            for j0 in range(nz):
                if not inside[i0][j0] or used[i0][j0]:
                    continue
                for i1 in range(i0, nx):
                    if not inside[i1][j0] or used[i1][j0]:
                        break
                    for j1 in range(j0, nz):
                        ok = all(inside[i][j1] and not used[i][j1] for i in range(i0, i1 + 1))
                        if not ok:
                            break
                        a = (xs[i1 + 1] - xs[i0]) * (zs[j1 + 1] - zs[j0])
                        key = (round(a, 4), -i0, -j0, -i1, -j1)
                        if best is None or key > best[0]:
                            best = (key, i0, j0, i1, j1)
        if best is None:
            break
        _, i0, j0, i1, j1 = best
        for i in range(i0, i1 + 1):
            for j in range(j0, j1 + 1):
                used[i][j] = True
        rects.append((xs[i0], zs[j0], xs[i1 + 1], zs[j1 + 1]))
    return rects


# ------------------------------------------------------------------------------------------------ triangulation

def triangulate(poly):
    """Ear clipping of a simple CCW polygon; returns index triples. Deterministic."""
    n = len(poly)
    if n < 3:
        return []
    idx = list(range(n))
    tris = []
    guard = 0
    while len(idx) > 3 and guard < 10000:
        guard += 1
        m = len(idx)
        found = False
        for k in range(m):
            i0, i1, i2 = idx[k - 1], idx[k], idx[(k + 1) % m]
            a, b, c = poly[i0], poly[i1], poly[i2]
            if cross(sub(b, a), sub(c, b)) <= 1e-10:
                continue
            ear = True
            for j in idx:
                if j in (i0, i1, i2):
                    continue
                p = poly[j]
                if cross(sub(b, a), sub(p, a)) >= -1e-12 and cross(sub(c, b), sub(p, b)) >= -1e-12 and cross(sub(a, c), sub(p, c)) >= -1e-12:
                    ear = False
                    break
            if ear:
                tris.append((i0, i1, i2))
                idx.pop(k)
                found = True
                break
        if not found:   # degenerate: drop the flattest vertex
            k = min(range(m), key=lambda k: abs(cross(sub(poly[idx[k]], poly[idx[k - 1]]), sub(poly[idx[(k + 1) % m]], poly[idx[k]]))))
            idx.pop(k)
    if len(idx) == 3:
        tris.append(tuple(idx))
    return tris


def convex_pieces(poly):
    """Hertel-Mehlhorn: triangulate, then remove diagonals while both sides stay convex. Lists of vertex indices (CCW)."""
    if is_convex(poly):
        return [list(range(len(poly)))]
    pieces = [list(t) for t in triangulate(poly)]
    merged = True
    while merged:
        merged = False
        for a in range(len(pieces)):
            for b in range(a + 1, len(pieces)):
                pa, pb = pieces[a], pieces[b]
                shared = [v for v in pa if v in pb]
                if len(shared) != 2:
                    continue
                u, v = shared
                # rotate pa to end with the shared edge, pb to start after it
                ia = pa.index(u)
                if pa[(ia + 1) % len(pa)] != v:
                    u, v = v, u
                    ia = pa.index(u)
                if pa[(ia + 1) % len(pa)] != v:
                    continue
                ra = pa[ia + 1:] + pa[:ia + 1]            # starts at v, ends at u
                ib = pb.index(u)
                rb = pb[ib + 1:] + pb[:ib + 1]            # starts after u ... ends at u
                if rb[-1] != u or rb[0] != v:
                    rb = pb[pb.index(u):] + pb[:pb.index(u)]
                cand = ra + [x for x in rb if x not in (u, v)]
                if len(set(cand)) != len(cand):
                    continue
                if is_convex([poly[i] for i in cand]) and area2([poly[i] for i in cand]) > 0:
                    pieces[a] = cand
                    pieces.pop(b)
                    merged = True
                    break
            if merged:
                break
    return pieces


# ------------------------------------------------------------------------------------------------ clipping

def clip_below(poly_uy, line_a, line_b):
    """Keeps the part of a convex polygon (u, y) on or below the line through line_a and line_b (u increasing)."""
    (ua, ya), (ub, yb) = line_a, line_b

    def f(p):
        if abs(ub - ua) < EPS:
            return p[1] - max(ya, yb)
        return p[1] - (ya + (yb - ya) * (p[0] - ua) / (ub - ua))

    out = []
    n = len(poly_uy)
    for i in range(n):
        p, q = poly_uy[i], poly_uy[(i + 1) % n]
        fp, fq = f(p), f(q)
        if fp <= 1e-9:
            out.append(p)
        if (fp < -1e-9 and fq > 1e-9) or (fp > 1e-9 and fq < -1e-9):
            t = fp / (fp - fq)
            out.append((p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t))
    # drop duplicates
    res = []
    for p in out:
        if not res or abs(p[0] - res[-1][0]) > 1e-7 or abs(p[1] - res[-1][1]) > 1e-7:
            res.append(p)
    if len(res) > 1 and abs(res[0][0] - res[-1][0]) < 1e-7 and abs(res[0][1] - res[-1][1]) < 1e-7:
        res.pop()
    return res if len(res) >= 3 and abs(area2(res)) > 1e-8 else []


# ------------------------------------------------------------------------------------------------ 3D mesh builder

def v3sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def v3cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def v3dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def v3norm(a):
    l = math.sqrt(v3dot(a, a))
    return (a[0] / l, a[1] / l, a[2] / l) if l > EPS else (0.0, 0.0, 0.0)


def face_normal(pts):
    n = (0.0, 0.0, 0.0)
    m = len(pts)
    for i in range(m):   # Newell's method (right-hand rule on plain coordinates), robust for any planar polygon
        a, b = pts[i], pts[(i + 1) % m]
        n = (n[0] + (a[1] - b[1]) * (a[2] + b[2]), n[1] + (a[2] - b[2]) * (a[0] + b[0]), n[2] + (a[0] - b[0]) * (a[1] + b[1]))
    return v3norm(n)


class MeshBuilder:
    """Faces with a swatch (atlas UVs) or with a vertex colour (panes). Vertices are not shared between faces:
    the look is flat shaded, and the writer merges nothing, so the output is fully deterministic."""

    def __init__(self, palette, uv_scale=0.0004):
        self.palette = palette
        self.uv_scale = uv_scale
        self.faces = []      # (points, uvs, colour or None)

    @property
    def tris(self):
        return sum(len(f[0]) - 2 for f in self.faces)

    def face(self, pts, swatch=None, out=None, colour=None):
        """pts: planar convex polygon (3D). out: a vector the face must point along (flipped if needed)."""
        pts = [tuple(float(c) for c in p) for p in pts]
        # drop repeated points (closer than 0.2 mm: the writer rounds to 0.01 mm, and clipping leaves near-twins)
        clean = []
        for p in pts:
            if not clean or max(abs(p[i] - clean[-1][i]) for i in range(3)) > 2e-4:
                clean.append(p)
        while len(clean) > 2 and max(abs(clean[0][i] - clean[-1][i]) for i in range(3)) <= 2e-4:
            clean.pop()
        # drop points on a straight run (they would make zero-area triangles, which Unity removes on import)
        changed = True
        while changed and len(clean) > 3:
            changed = False
            for k in range(len(clean)):
                a, b, c = clean[k - 1], clean[k], clean[(k + 1) % len(clean)]
                cr = v3cross(v3sub(b, a), v3sub(c, b))
                if v3dot(cr, cr) < 1e-14:
                    clean.pop(k)
                    changed = True
                    break
        if len(clean) < 3:
            return
        nrm = face_normal(clean)
        if nrm == (0.0, 0.0, 0.0):
            return
        n2 = v3cross(v3sub(clean[1], clean[0]), v3sub(clean[2], clean[0]))
        if len(clean) == 3 and v3dot(n2, n2) < 1e-14:
            return
        if out is not None and v3dot(nrm, out) < 0:
            clean.reverse()
            nrm = (-nrm[0], -nrm[1], -nrm[2])
        uvs = None
        if swatch is not None:
            cu, cv = self.palette.uv_centre(swatch)
            # planar projection on the two axes the normal is least aligned with, scaled into the swatch
            ax = max(range(3), key=lambda i: abs(nrm[i]))
            i0, i1 = [i for i in range(3) if i != ax]
            cx = sum(p[i0] for p in clean) / len(clean)
            cy = sum(p[i1] for p in clean) / len(clean)
            lim = self.palette.UV_HALF
            uvs = [(cu + max(-lim, min(lim, (p[i0] - cx) * self.uv_scale)), cv + max(-lim, min(lim, (p[i1] - cy) * self.uv_scale))) for p in clean]
        self.faces.append((clean, uvs, colour))

    def quad(self, a, b, c, d, swatch=None, out=None, colour=None):
        self.face([a, b, c, d], swatch, out, colour)

    def box(self, centre, ax_u, ax_v, half, swatch, skip=()):
        """Oriented box: centre (3D), horizontal unit axis ax_u (plan, 2D), its plan normal ax_v (2D), half = (hu, hy, hv).
        skip: faces to leave out among '-u', '+u', '-y', '+y', '-v', '+v'."""
        cx, cy, cz = centre
        hu, hy, hv = half
        U = (ax_u[0], 0.0, ax_u[1])
        V = (ax_v[0], 0.0, ax_v[1])
        Y = (0.0, 1.0, 0.0)

        def P(su, sy, sv):
            return (cx + U[0] * hu * su + V[0] * hv * sv, cy + hy * sy, cz + U[2] * hu * su + V[2] * hv * sv)

        faces = {
            '+u': ([P(1, -1, -1), P(1, -1, 1), P(1, 1, 1), P(1, 1, -1)], U),
            '-u': ([P(-1, -1, -1), P(-1, 1, -1), P(-1, 1, 1), P(-1, -1, 1)], (-U[0], 0, -U[2])),
            '+y': ([P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1)], Y),
            '-y': ([P(-1, -1, -1), P(-1, -1, 1), P(1, -1, 1), P(1, -1, -1)], (0, -1, 0)),
            '+v': ([P(-1, -1, 1), P(-1, 1, 1), P(1, 1, 1), P(1, -1, 1)], V),
            '-v': ([P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1)], (-V[0], 0, -V[2])),
        }
        for k in ('+u', '-u', '+y', '-y', '+v', '-v'):
            if k in skip:
                continue
            pts, out = faces[k]
            self.face(pts, swatch, out)

    def prism(self, poly, y0, y1, swatch, top=True, bottom=False, sides=True, top_swatch=None):
        """Vertical prism over a CCW plan polygon."""
        n = len(poly)
        if sides:
            for i in range(n):
                a, b = poly[i], poly[(i + 1) % n]
                nrm = out_normal(a, b)
                self.quad((a[0], y0, a[1]), (b[0], y0, b[1]), (b[0], y1, b[1]), (a[0], y1, a[1]), swatch, (nrm[0], 0, nrm[1]))
        for flag, y, out, sw in ((top, y1, (0, 1, 0), top_swatch or swatch), (bottom, y0, (0, -1, 0), swatch)):
            if not flag:
                continue
            for t in triangulate(poly):
                self.face([(poly[i][0], y, poly[i][1]) for i in t], sw, out)
