"""Replicates CellGrid's corner/edge rules and CellMeshBuilder.AddFace's face test,
to check whether a vertical wall appears between two pads at different heights."""

SPLIT_THRESHOLD = 10          # CellGrid.SplitThresholdUnits, 0.5 m
W, H = 5, 4


class Grid(object):
    def __init__(self, base):
        self.h = [base] * (W * H)
        self.f = [False] * (W * H)

    def inb(self, x, z):
        return 0 <= x < W and 0 <= z < H

    def raw(self, x, z):
        return self.h[z * W + x]

    def flat(self, x, z):
        return self.f[z * W + x]

    def set(self, x, z, v, flat=None):
        self.h[z * W + x] = v
        if flat is not None:
            self.f[z * W + x] = flat

    def neighbourhood_mean(self, gx, gz):
        vals = []
        for j in range(gz - 2, gz + 2):
            for i in range(gx - 2, gx + 2):
                if self.inb(i, j):
                    vals.append(self.raw(i, j))
        return sum(vals) / float(len(vals)) if vals else 0.0

    def shared_corner(self, gx, gz):
        quad, has, scratch = [0] * 4, [False] * 4, []
        any_flat, flat_max = False, 0

        for s in range(4):
            cx = gx - 1 + (s & 1)
            cz = gz - 1 + (s >> 1)
            has[s] = self.inb(cx, cz)
            if not has[s]:
                continue
            r = self.raw(cx, cz)
            quad[s] = r
            if self.flat(cx, cz):
                any_flat = True
                flat_max = max(flat_max, r)
            scratch.append(r)

        n = len(scratch)
        if n == 0:
            return 0.0
        if any_flat:
            return float(flat_max)
        if n == 1:
            return float(scratch[0])

        scratch.sort()
        split, widest = -1, 0
        for i in range(n - 1):
            gap = scratch[i + 1] - scratch[i]
            if gap > widest:
                widest, split = gap, i

        mean = lambda a, b: sum(scratch[a:a + b]) / float(b)

        if split < 0 or widest < SPLIT_THRESHOLD:
            return mean(0, n)

        low_count = split + 1
        high_count = n - low_count
        if low_count > high_count:
            return mean(0, low_count)
        if high_count > low_count:
            return mean(low_count, high_count)

        low_mean, high_mean = mean(0, low_count), mean(low_count, high_count)
        middle = (low_mean + high_mean) * 0.5
        if n < 4:
            return middle

        sp = (scratch[low_count - 1] + scratch[low_count]) * 0.5
        h0 = has[0] and quad[0] > sp
        h1 = has[1] and quad[1] > sp
        h2 = has[2] and quad[2] > sp
        h3 = has[3] and quad[3] > sp
        diagonal = (h0 and h3 and not h1 and not h2) or (h1 and h2 and not h0 and not h3)
        if not diagonal:
            return middle

        ref = self.neighbourhood_mean(gx, gz)
        return high_mean if abs(high_mean - ref) >= abs(low_mean - ref) else low_mean

    def corner_for(self, cx, cz, gx, gz):
        return float(self.raw(cx, cz)) if self.flat(cx, cz) else self.shared_corner(gx, gz)

    def edge_for(self, cx, cz, nx, nz):
        if self.flat(cx, cz):
            return float(self.raw(cx, cz))
        if not self.inb(nx, nz):
            return float(self.raw(cx, cz))
        if self.flat(nx, nz):
            return float(self.raw(nx, nz))
        return (self.raw(cx, cz) + self.raw(nx, nz)) * 0.5


def face_height(g, cx, cz, nx, nz):
    """Tallest part of the seam face CellMeshBuilder would emit, in raw units. 0 = none."""
    if not g.inb(nx, nz):
        return 0.0

    if nx > cx:
        ax, az, bx, bz = cx + 1, cz, cx + 1, cz + 1
    elif nx < cx:
        ax, az, bx, bz = cx, cz, cx, cz + 1
    elif nz > cz:
        ax, az, bx, bz = cx, cz + 1, cx + 1, cz + 1
    else:
        ax, az, bx, bz = cx, cz, cx + 1, cz

    top_a = g.corner_for(cx, cz, ax, az)
    top_b = g.corner_for(cx, cz, bx, bz)
    top_m = g.edge_for(cx, cz, nx, nz)

    bot_a = g.corner_for(nx, nz, ax, az)
    bot_b = g.corner_for(nx, nz, bx, bz)
    bot_m = g.edge_for(nx, nz, cx, cz)

    if top_a <= bot_a and top_b <= bot_b and top_m <= bot_m:
        return 0.0     # not the higher side; AddFace returns early

    return max(top_a - bot_a, top_b - bot_b, top_m - bot_m)


def wall_between(g, a, b):
    """Tallest seam face on the shared edge, checked from both sides, in metres."""
    both = max(face_height(g, a[0], a[1], b[0], b[1]),
               face_height(g, b[0], b[1], a[0], a[1]))
    return both * 0.05


def scenario(release):
    g = Grid(90)                      # 4.5 m surrounding ground
    g.set(1, 1, 100, True)            # pad A, 5.0 m, flattened
    g.set(2, 1, 80, True)             # pad B, 4.0 m, flattened
    if release:
        g.f[1 * W + 1] = False        # the new rule: A gives up its flag
    return g


print("Two flattened pads, 5.0 m beside 4.0 m\n")
for label, rel in (("terrace (old behaviour)", False), ("released (new rule)", True)):
    g = scenario(rel)
    print("%-26s wall between them: %.2f m" % (label, wall_between(g, (1, 1), (2, 1))))
    print("%-26s A edge toward B:   %.2f m   (A centre %.2f)" %
          ("", g.edge_for(1, 1, 2, 1) * 0.05, g.raw(1, 1) * 0.05))
    print("%-26s B edge toward A:   %.2f m   (B centre %.2f)" %
          ("", g.edge_for(2, 1, 1, 1) * 0.05, g.raw(2, 1) * 0.05))
    print()

# A pad built cell by cell at ONE height must not dissolve as it grows.
g = Grid(90)
for x in (1, 2, 3):
    g.set(x, 1, 100, True)
print("Three-cell pad, all at 5.0 m")
print("  wall inside the pad: %.2f m / %.2f m" %
      (wall_between(g, (1, 1), (2, 1)), wall_between(g, (2, 1), (3, 1))))
print("  wall at its outer edge: %.2f m" % wall_between(g, (3, 1), (4, 1)))
