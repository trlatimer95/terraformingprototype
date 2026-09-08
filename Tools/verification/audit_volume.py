"""Does the reported cut/fill figure equal the volume that actually changed?

Two separate questions:
  A. Cell model  -- does the 3x3 / 5x5 accounting region cover every cell an edit moves?
  B. Vertex model -- does the VertexArea sum match the volume of the mesh actually built?
"""

import random

exec(open("sim_terrace.py").read().split('print("Two flattened')[0])

CELL_AREA = 1.0
UNIT = 0.05                      # MetresPerUnit
RING = [(-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1)]


# ---------------------------------------------------------------- cell model

def cell_mean_raw(g, cx, cz):
    b = (g.corner_for(cx, cz, cx, cz)
         + g.corner_for(cx, cz, cx + 1, cz)
         + g.corner_for(cx, cz, cx + 1, cz + 1)
         + g.corner_for(cx, cz, cx, cz + 1)
         + g.edge_for(cx, cz, cx, cz - 1)
         + g.edge_for(cx, cz, cx + 1, cz)
         + g.edge_for(cx, cz, cx, cz + 1)
         + g.edge_for(cx, cz, cx - 1, cz))
    return g.raw(cx, cz) / 3.0 + b / 12.0


def cell_volume(g, cx, cz):
    return cell_mean_raw(g, cx, cz) * UNIT * CELL_AREA


def total_volume(g):
    return sum(cell_volume(g, x, z) for z in range(H) for x in range(W))


def region_volume(g, cx, cz, r):
    t = 0.0
    for j in range(-r, r + 1):
        for i in range(-r, r + 1):
            if g.inb(cx + i, cz + j):
                t += cell_volume(g, cx + i, cz + j)
    return t


def audit_cell(trials=4000, seed=7):
    rng = random.Random(seed)
    worst_sculpt = 0.0
    worst_flatten = 0.0

    for _ in range(trials):
        g = Grid(90)
        for z in range(H):
            for x in range(W):
                g.set(x, z, rng.randrange(60, 130))
                if rng.random() < 0.25:
                    g.f[z * W + x] = True

        cx, cz = rng.randrange(1, W - 1), rng.randrange(1, H - 1)

        # --- sculpt: move one centre, accounted over 3x3 -------------------
        before_all, before_reg = total_volume(g), region_volume(g, cx, cz, 1)
        g.set(cx, cz, g.raw(cx, cz) + rng.choice([-20, -2, 2, 20]))
        g.f[cz * W + cx] = False
        after_all, after_reg = total_volume(g), region_volume(g, cx, cz, 1)
        worst_sculpt = max(worst_sculpt,
                           abs((after_all - before_all) - (after_reg - before_reg)))

        # --- flatten with release: accounted over 5x5 ----------------------
        before_all, before_reg = total_volume(g), region_volume(g, cx, cz, 2)
        g.f[cz * W + cx] = True
        here = g.raw(cx, cz)
        for dx, dz in RING:
            nx, nz = cx + dx, cz + dz
            if g.inb(nx, nz) and g.flat(nx, nz) and g.raw(nx, nz) != here:
                g.f[nz * W + nx] = False
        after_all, after_reg = total_volume(g), region_volume(g, cx, cz, 2)
        worst_flatten = max(worst_flatten,
                            abs((after_all - before_all) - (after_reg - before_reg)))

    return worst_sculpt, worst_flatten


# -------------------------------------------------------------- vertex model

def vertex_area(vx, vz, n):
    """HeightGrid.VertexArea: half on an edge, quarter at a corner."""
    fx = 0.5 if (vx == 0 or vx == n) else 1.0
    fz = 0.5 if (vz == 0 or vz == n) else 1.0
    return fx * fz * CELL_AREA


def vertex_area_volume(h, n):
    return sum(h[z][x] * UNIT * vertex_area(x, z, n)
               for z in range(n + 1) for x in range(n + 1))


def mesh_volume(h, n):
    """ChunkMeshBuilder: each quad splits along its FLATTER diagonal."""
    total = 0.0
    for cz in range(n):
        for cx in range(n):
            a = h[cz][cx]; b = h[cz][cx + 1]
            c = h[cz + 1][cx + 1]; d = h[cz + 1][cx]
            if abs(a - c) <= abs(b - d):
                total += (a + d + c) / 3.0 + (a + c + b) / 3.0     # split a-c
            else:
                total += (a + d + b) / 3.0 + (b + d + c) / 3.0     # split b-d
            # each triangle covers half the cell
    return total * 0.5 * UNIT * CELL_AREA


def audit_vertex(n=8, trials=3000, seed=11):
    rng = random.Random(seed)
    worst = 0.0
    worst_case = None

    for _ in range(trials):
        h = [[rng.randrange(60, 130) for _ in range(n + 1)] for _ in range(n + 1)]
        va, mv = vertex_area_volume(h, n), mesh_volume(h, n)
        if abs(va - mv) > worst:
            worst, worst_case = abs(va - mv), (va, mv)

    return worst, worst_case


print("A. Cell model -- reported figure vs whole-grid change\n")
s, f = audit_cell()
print("   sculpt, accounted over 3x3 : worst error %.9f m3" % s)
print("   flatten, accounted over 5x5: worst error %.9f m3" % f)

print("\nB. Vertex model -- VertexArea sum vs the mesh actually built\n")
w, case = audit_vertex()
print("   worst disagreement over an 8x8 grid: %.4f m3" % w)
print("   (VertexArea said %.3f m3, the mesh holds %.3f m3)" % case)

# Single-quad worst case, to show where it comes from.
n = 1
h = [[0, 0], [0, 20]]      # one corner 1.0 m up
print("\n   one 1 m cell, a single corner raised 1.0 m:")
print("     VertexArea sum %.4f m3   mesh %.4f m3   gap %.4f m3"
      % (vertex_area_volume(h, n), mesh_volume(h, n),
         abs(vertex_area_volume(h, n) - mesh_volume(h, n))))
