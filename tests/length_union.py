"""3D curve-length union. Mirror of autocad/src/LengthUnion.cs (no AutoCAD).

Straight segments on the same line are projected to 1D intervals and merged.
Circular arcs on the same circle are merged as angular intervals (a circle is a
full turn). Splines and other sampled curves keep their exact length when they
do not overlap anything; exact copies (forward or reversed) count once; partial
overlaps use the union of the sampled chords.
"""
import math

ZERO = 1e-12
MERGE_GAP = 1e-9
FULL_SWEEP = 2 * math.pi - 1e-8
ARC_CHORD_MAX = math.radians(10.0)


class V(object):
    __slots__ = ('x', 'y', 'z')

    def __init__(self, x, y, z=0.0):
        self.x = float(x)
        self.y = float(y)
        self.z = float(z)

    def __repr__(self):
        return 'V(%s,%s,%s)' % (self.x, self.y, self.z)


def as_v(p):
    if isinstance(p, V):
        return p
    if len(p) == 2:
        return V(p[0], p[1], 0.0)
    return V(p[0], p[1], p[2])


def add(a, b):
    return V(a.x + b.x, a.y + b.y, a.z + b.z)


def sub(a, b):
    return V(a.x - b.x, a.y - b.y, a.z - b.z)


def mul(a, s):
    return V(a.x * s, a.y * s, a.z * s)


def dot(a, b):
    return a.x * b.x + a.y * b.y + a.z * b.z


def cross(a, b):
    return V(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x)


def length(a):
    return math.sqrt(dot(a, a))


def dist(a, b):
    return length(sub(a, b))


def unit(a):
    n = length(a)
    if n <= ZERO:
        return None
    return mul(a, 1.0 / n)


def canonical(v):
    eps = 1e-12
    if v.x < -eps or (abs(v.x) <= eps and v.y < -eps) or (abs(v.x) <= eps and abs(v.y) <= eps and v.z < 0):
        return mul(v, -1.0)
    return v


def basis(normal):
    """Right-handed in-plane axes. Angle 0 lies along +u, CCW about normal toward +v."""
    n = unit(normal)
    if n is None:
        return None, None
    helper = V(1.0, 0.0, 0.0) if abs(n.x) < 0.9 else V(0.0, 1.0, 0.0)
    u = unit(cross(n, helper))
    v = cross(n, u)
    return u, v


def parallel(d1, d2, ang):
    c = abs(dot(d1, d2))
    if c > 1.0:
        c = 1.0
    return c >= math.cos(ang)


def point_line_dist(p, origin, direction):
    return length(cross(sub(p, origin), direction))


def norm_ang(a):
    tw = 2.0 * math.pi
    a = math.fmod(a, tw)
    if a < 0.0:
        a += tw
    return a


def merge_intervals(intervals, gap=MERGE_GAP):
    """Union length of 1D intervals. Gaps larger than `gap` stay separate (the gap is not added)."""
    items = []
    for a, b in intervals:
        lo, hi = (a, b) if a <= b else (b, a)
        if hi - lo > ZERO:
            items.append((lo, hi))
    if not items:
        return 0.0, []
    items.sort()
    merged = []
    cs, ce = items[0]
    for a, b in items[1:]:
        if a <= ce + gap:
            if b > ce:
                ce = b
        else:
            merged.append((cs, ce))
            cs, ce = a, b
    merged.append((cs, ce))
    return sum(b - a for a, b in merged), merged


def overlap_length(intervals, merged):
    total = 0.0
    for a, b in intervals:
        lo, hi = (a, b) if a <= b else (b, a)
        for s, e in merged:
            x0 = lo if lo > s else s
            x1 = hi if hi < e else e
            if x1 > x0:
                total += x1 - x0
    return total


def merge_angles(intervals, gap=MERGE_GAP):
    length, merged = merge_intervals(intervals, gap)
    if not merged:
        return 0.0, []
    if len(merged) >= 2:
        wrap_gap = merged[0][0] + 2.0 * math.pi - merged[-1][1]
        if 0.0 <= wrap_gap <= gap:
            length += wrap_gap
    if length >= 2.0 * math.pi - gap:
        return 2.0 * math.pi, [(0.0, 2.0 * math.pi)]
    if length > 2.0 * math.pi:
        length = 2.0 * math.pi
    return length, merged


def add_ang_interval(intervals, start, sweep):
    if sweep >= FULL_SWEEP:
        return True
    sweep = min(sweep, 2.0 * math.pi)
    a = norm_ang(start)
    b = a + sweep
    if b <= 2.0 * math.pi + 1e-15:
        if b - a > ZERO:
            intervals.append((a, min(b, 2.0 * math.pi)))
    else:
        if 2.0 * math.pi - a > ZERO:
            intervals.append((a, 2.0 * math.pi))
        end = b - 2.0 * math.pi
        if end > ZERO:
            intervals.append((0.0, end))
    return False


class _Piece(object):
    def __init__(self, kind, layer, exact):
        self.kind = kind
        self.layer = layer if layer else '0'
        self.exact = exact


class _LineGroup(object):
    def __init__(self, direction, origin):
        self.dir = direction
        self.origin = origin
        self.intervals = []
        self.merged = []
        self.parts = []


class _ArcGroup(object):
    def __init__(self, center, normal, radius):
        self.center = center
        self.normal = normal
        self.radius = radius
        self.u, self.v = basis(normal)
        self.intervals = []
        self.merged = []
        self.full = False


def _arc_point(center, normal, radius, theta):
    u, v = basis(normal)
    return add(center, mul(add(mul(u, math.cos(theta)), mul(v, math.sin(theta))), radius))


def _line_union(lines, tol, ang):
    groups = []
    for s in lines:
        d = unit(sub(s.b, s.a))
        if d is None:
            continue
        placed = False
        for g in groups:
            if not parallel(d, g.dir, ang):
                continue
            if point_line_dist(s.a, g.origin, g.dir) > tol or point_line_dist(s.b, g.origin, g.dir) > tol:
                continue
            t0 = dot(sub(s.a, g.origin), g.dir)
            t1 = dot(sub(s.b, g.origin), g.dir)
            g.intervals.append((t0, t1))
            placed = True
            break
        if not placed:
            g = _LineGroup(d, s.a)
            t1 = dot(sub(s.b, s.a), d)
            g.intervals.append((0.0, t1))
            groups.append(g)
    total = 0.0
    for g in groups:
        ln, merged = merge_intervals(g.intervals)
        g.merged = merged
        total += ln
    return total, groups


def _attach_arc(g, arc, same):
    if arc.full or arc.sweep >= FULL_SWEEP:
        g.full = True
        return
    sp = _arc_point(arc.center, arc.normal, arc.radius, arc.start)
    rel = sub(sp, g.center)
    a0 = norm_ang(math.atan2(dot(rel, g.v), dot(rel, g.u)))
    start = a0 if same else a0 - arc.sweep
    if add_ang_interval(g.intervals, start, arc.sweep):
        g.full = True


def _arc_union(arcs, tol, ang):
    groups = []
    for arc in arcs:
        n = unit(arc.normal)
        if n is None:
            continue
        placed = False
        for g in groups:
            if abs(arc.radius - g.radius) > tol or dist(arc.center, g.center) > tol:
                continue
            nd = dot(n, g.normal)
            if abs(nd) < math.cos(ang):
                continue
            _attach_arc(g, arc, nd > 0.0)
            placed = True
            break
        if not placed:
            cn = canonical(n)
            g = _ArcGroup(arc.center, cn, arc.radius)
            _attach_arc(g, arc, dot(n, cn) > 0.0)
            groups.append(g)
    total = 0.0
    for g in groups:
        if g.full:
            g.merged = [(0.0, 2.0 * math.pi)]
            total += 2.0 * math.pi * g.radius
            continue
        sweep, merged = merge_angles(g.intervals)
        g.merged = merged
        if sweep >= 2.0 * math.pi - MERGE_GAP:
            g.full = True
            g.merged = [(0.0, 2.0 * math.pi)]
            total += 2.0 * math.pi * g.radius
        else:
            total += g.radius * sweep
    return total, groups


def _pairs(curve):
    pts = curve.points
    n = len(pts)
    m = n if curve.closed else n - 1
    out = []
    for i in range(m):
        a = pts[i]
        b = pts[(i + 1) % n]
        if dist(a, b) > ZERO:
            out.append((a, b))
    return out


def _cyclic_match(pa, pb, tol):
    n = len(pa)
    for k in range(n):
        if all(dist(pa[i], pb[(i + k) % n]) <= tol for i in range(n)):
            return True
    rev = list(reversed(pb))
    for k in range(n):
        if all(dist(pa[i], rev[(i + k) % n]) <= tol for i in range(n)):
            return True
    return False


def _same_sampled(a, b, tol):
    if a.closed != b.closed or len(a.points) != len(b.points):
        return False
    if not a.closed:
        forward = all(dist(a.points[i], b.points[i]) <= tol for i in range(len(a.points)))
        backward = all(dist(a.points[i], b.points[-1 - i]) <= tol for i in range(len(a.points)))
        return forward or backward
    return _cyclic_match(a.points, b.points, tol)


def _dedup(samples, tol):
    unique = []
    for s in samples:
        hit = None
        for u in unique:
            if _same_sampled(s, u, tol):
                hit = u
                break
        if hit is None:
            unique.append(s)
        elif s.exact > hit.exact:
            hit.exact = s.exact
    return unique


def _line_covered_length(a, b, groups, tol, ang):
    d = unit(sub(b, a))
    if d is None:
        return 0.0
    seg_len = dist(a, b)
    best = 0.0
    for g in groups:
        if not parallel(d, g.dir, ang):
            continue
        if point_line_dist(a, g.origin, g.dir) > tol or point_line_dist(b, g.origin, g.dir) > tol:
            continue
        t0 = dot(sub(a, g.origin), g.dir)
        t1 = dot(sub(b, g.origin), g.dir)
        ov = overlap_length([(t0, t1)], g.merged)
        if ov > best:
            best = ov
    if best > seg_len:
        best = seg_len
    return best


def _arc_covered_length(a, b, groups, tol):
    seg_len = dist(a, b)
    if seg_len <= ZERO:
        return 0.0
    mid = mul(add(a, b), 0.5)
    half = seg_len * 0.5
    best = 0.0
    for g in groups:
        if half > g.radius + tol:
            continue
        if abs(dist(a, g.center) - g.radius) > tol or abs(dist(b, g.center) - g.radius) > tol:
            continue
        expected = math.sqrt(max(0.0, g.radius * g.radius - min(half, g.radius) ** 2))
        if abs(dist(mid, g.center) - expected) > tol:
            continue
        span = 2.0 * math.asin(min(1.0, half / g.radius)) if g.radius > ZERO else 0.0
        if span > ARC_CHORD_MAX:
            continue
        ta = norm_ang(math.atan2(dot(sub(a, g.center), g.v), dot(sub(a, g.center), g.u)))
        tb = norm_ang(math.atan2(dot(sub(b, g.center), g.v), dot(sub(b, g.center), g.u)))
        ccw = norm_ang(tb - ta)
        if ccw <= math.pi:
            start, sweep = ta, ccw
        else:
            start, sweep = tb, norm_ang(ta - tb)
        if sweep > ARC_CHORD_MAX + 1e-8:
            continue
        if sweep <= ZERO:
            continue
        if g.full:
            covered_ang = sweep
        else:
            pieces = []
            add_ang_interval(pieces, start, sweep)
            covered_ang = overlap_length(pieces, g.merged)
        frac = covered_ang / sweep
        if frac < 0.0:
            frac = 0.0
        if frac > 1.0:
            frac = 1.0
        got = seg_len * frac
        if got > best:
            best = got
    return best


def _covered_length(a, b, line_groups, arc_groups, tol, ang):
    seg_len = dist(a, b)
    line_cov = _line_covered_length(a, b, line_groups, tol, ang)
    arc_cov = _arc_covered_length(a, b, arc_groups, tol)
    cov = line_cov + arc_cov
    if cov > seg_len:
        cov = seg_len
    return cov


def _covered_subsegments(a, b, line_groups, arc_groups, tol, ang):
    """Portions of segment ab already counted by the line or arc union, as point pairs."""
    d = unit(sub(b, a))
    seg_len = dist(a, b)
    if d is None:
        return []
    portions = []
    for g in line_groups:
        if not parallel(d, g.dir, ang):
            continue
        if point_line_dist(a, g.origin, g.dir) > tol or point_line_dist(b, g.origin, g.dir) > tol:
            continue
        sign = dot(d, g.dir)
        if abs(sign) < 0.5:
            continue
        t_a = dot(sub(a, g.origin), g.dir)
        for t0, t1 in g.merged:
            s0 = (t0 - t_a) / sign
            s1 = (t1 - t_a) / sign
            lo = s0 if s0 < s1 else s1
            hi = s1 if s1 > s0 else s0
            if lo < 0.0:
                lo = 0.0
            if hi > seg_len:
                hi = seg_len
            if hi - lo > ZERO:
                portions.append((lo, hi))
    arc_cov = _arc_covered_length(a, b, arc_groups, tol)
    if arc_cov >= seg_len - 1e-9:
        portions.append((0.0, seg_len))
    elif arc_cov > 1e-8:
        portions.append((0.0, arc_cov))
    _ln, merged = merge_intervals(portions)
    out = []
    for lo, hi in merged:
        out.append((add(a, mul(d, lo)), add(a, mul(d, hi))))
    return out


def _segment_groups(tagged, tol, ang):
    """tagged: (owner, a, b). Groups collinear segments and records (owner, lo, hi) parts."""
    groups = []
    for owner, a, b in tagged:
        d = unit(sub(b, a))
        if d is None:
            continue
        placed = False
        for g in groups:
            if not parallel(d, g.dir, ang):
                continue
            if point_line_dist(a, g.origin, g.dir) > tol or point_line_dist(b, g.origin, g.dir) > tol:
                continue
            t0 = dot(sub(a, g.origin), g.dir)
            t1 = dot(sub(b, g.origin), g.dir)
            lo, hi = (t0, t1) if t0 <= t1 else (t1, t0)
            g.parts.append((owner, lo, hi))
            placed = True
            break
        if not placed:
            g = _LineGroup(d, a)
            t1 = dot(sub(b, a), d)
            lo, hi = (0.0, t1) if t1 >= 0.0 else (t1, 0.0)
            g.parts.append((owner, lo, hi))
            groups.append(g)
    return groups


def _sampled_net(samples, line_groups, arc_groups, tol, ang):
    unique = _dedup(samples, tol)
    if not unique:
        return 0.0
    tagged = []
    for i, c in enumerate(unique):
        for a, b in _pairs(c):
            tagged.append((i, a, b))
    sgroups = _segment_groups(tagged, tol, ang)

    parent = list(range(len(unique)))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def unite(i, j):
        ri, rj = find(i), find(j)
        if ri != rj:
            parent[rj] = ri

    self_ov = set()
    for g in sgroups:
        parts = sorted(g.parts, key=lambda p: (p[1], p[2]))
        for i in range(len(parts)):
            oi, a0, b0 = parts[i]
            for j in range(i + 1, len(parts)):
                oj, a1, b1 = parts[j]
                if a1 > b0 + MERGE_GAP:
                    break
                ov = (b0 if b0 < b1 else b1) - (a0 if a0 > a1 else a1)
                if ov <= 1e-8:
                    continue
                if oi == oj:
                    self_ov.add(oi)
                else:
                    unite(oi, oj)

    clusters = {}
    for i in range(len(unique)):
        clusters.setdefault(find(i), []).append(i)

    total = 0.0
    for members in clusters.values():
        multi = len(members) > 1 or any(m in self_ov for m in members)
        if not multi:
            i = members[0]
            chord = 0.0
            covered = 0.0
            for a, b in _pairs(unique[i]):
                sl = dist(a, b)
                chord += sl
                covered += _covered_length(a, b, line_groups, arc_groups, tol, ang)
            if chord <= ZERO:
                continue
            frac = covered / chord
            if frac < 0.0:
                frac = 0.0
            if frac > 1.0:
                frac = 1.0
            total += unique[i].exact * (1.0 - frac)
            continue
        segs = []
        covered_segs = []
        for i in members:
            for a, b in _pairs(unique[i]):
                if dist(a, b) <= ZERO:
                    continue
                segs.append((a, b))
                for pa, pb in _covered_subsegments(a, b, line_groups, arc_groups, tol, ang):
                    covered_segs.append((pa, pb))
        u_all = _union_pairs(segs, tol, ang)
        u_cov = _union_pairs(covered_segs, tol, ang)
        extra = u_all - u_cov
        if extra > 0.0:
            total += extra
    return total


def _union_pairs(pairs, tol, ang):
    fake = []
    for a, b in pairs:
        L = dist(a, b)
        if L <= ZERO:
            continue
        p = _Piece('line', '0', L)
        p.a = a
        p.b = b
        fake.append(p)
    net, _groups = _line_union(fake, tol, ang)
    return net


def _chord_length(pts, closed):
    if len(pts) < 2:
        return 0.0
    total = 0.0
    m = len(pts) if closed else len(pts) - 1
    for i in range(m):
        total += dist(pts[i], pts[(i + 1) % len(pts)])
    return total


def evaluate(pieces, tol, ang):
    raw = 0.0
    lines = []
    arcs = []
    samples = []
    for p in pieces:
        raw += p.exact
        if p.kind == 'line':
            lines.append(p)
        elif p.kind == 'arc':
            arcs.append(p)
        else:
            samples.append(p)
    line_net, line_groups = _line_union(lines, tol, ang)
    arc_net, arc_groups = _arc_union(arcs, tol, ang)
    samp_net = _sampled_net(samples, line_groups, arc_groups, tol, ang)
    net = line_net + arc_net + samp_net
    if net < 0.0:
        net = 0.0
    overlap = raw - net
    if overlap < 0.0 and overlap > -1e-6:
        overlap = 0.0
        net = raw
    return {'raw': raw, 'net': net, 'overlap': raw - net if overlap >= 0.0 else overlap, 'count': len(pieces)}


class LengthUnion(object):
    def __init__(self, tol=0.001, ang=1e-6):
        self.tol = float(tol)
        self.ang = float(ang)
        self.pieces = []

    def add_line(self, a, b, layer='0'):
        a, b = as_v(a), as_v(b)
        L = dist(a, b)
        if L <= ZERO:
            return False
        p = _Piece('line', layer, L)
        p.a = a
        p.b = b
        self.pieces.append(p)
        return True

    def add_arc(self, center, normal, radius, start, sweep, layer='0'):
        center = as_v(center)
        normal = as_v(normal)
        n = unit(normal)
        radius = float(radius)
        if n is None or radius <= ZERO:
            return False
        start = float(start)
        sweep = float(sweep)
        if sweep < 0.0:
            start = start + sweep
            sweep = -sweep
        full = sweep >= FULL_SWEEP
        if full:
            sweep = 2.0 * math.pi
        else:
            sweep = min(sweep, 2.0 * math.pi)
        if sweep <= ZERO:
            return False
        p = _Piece('arc', layer, radius * sweep)
        p.center = center
        p.normal = n
        p.radius = radius
        p.start = start
        p.sweep = sweep
        p.full = full
        self.pieces.append(p)
        return True

    def add_circle(self, center, normal, radius, layer='0'):
        return self.add_arc(center, normal, radius, 0.0, 2.0 * math.pi, layer)

    def add_sampled(self, points, exact, closed=False, layer='0'):
        pts = [as_v(p) for p in points]
        if len(pts) < 2:
            return False
        if closed and dist(pts[0], pts[-1]) <= self.tol:
            pts = pts[:-1]
        clean = [pts[0]]
        for q in pts[1:]:
            if dist(clean[-1], q) > ZERO:
                clean.append(q)
        if closed and len(clean) >= 2 and dist(clean[0], clean[-1]) <= ZERO:
            clean.pop()
        if len(clean) < 2:
            return False
        if closed and len(clean) < 3:
            closed = False
        chord = _chord_length(clean, closed)
        exact = chord if exact is None or float(exact) <= ZERO else float(exact)
        p = _Piece('sampled', layer, exact)
        p.points = clean
        p.closed = closed
        self.pieces.append(p)
        return True

    def result(self):
        overall = evaluate(self.pieces, self.tol, self.ang)
        names = sorted({p.layer for p in self.pieces})
        layers = []
        for name in names:
            sub = [p for p in self.pieces if p.layer == name]
            r = evaluate(sub, self.tol, self.ang)
            layers.append({'name': name, 'count': r['count'], 'raw': r['raw'], 'net': r['net']})
        overall['layers'] = layers
        return overall


def arc_point(center, normal, radius, theta):
    """Point on an arc using the same basis as the union. For tests and callers."""
    return _arc_point(as_v(center), as_v(normal), float(radius), float(theta))


def bulge_arc(p1, p2, bulge, normal=(0.0, 0.0, 1.0)):
    """LWPolyline / Polyline2d bulge (tan of sweep/4) to a circular arc.

    Returns (center, normal, radius, sweep) with sweep positive CCW about the
    returned normal, or None when the segment is straight. Same formula as
    CadAiDesigner.LengthUnion.BulgeArc.
    """
    p1, p2 = as_v(p1), as_v(p2)
    n = unit(as_v(normal))
    if n is None:
        n = V(0.0, 0.0, 1.0)
    b = float(bulge)
    dvec = sub(p2, p1)
    d = length(dvec)
    if d <= ZERO or abs(b) < 1e-10:
        return None
    sweep = 4.0 * math.atan(b)
    direction = mul(dvec, 1.0 / d)
    left = cross(n, direction)
    ln = length(left)
    if ln < 1e-12:
        return None
    left = mul(left, 1.0 / ln)
    offset = (d * 0.5) * ((1.0 - b * b) / (2.0 * b))
    mid = mul(add(p1, p2), 0.5)
    center = add(mid, mul(left, offset))
    radius = abs(d * (b * b + 1.0) / (4.0 * b))
    arc_n = n if sweep >= 0.0 else mul(n, -1.0)
    return center, arc_n, radius, abs(sweep)
