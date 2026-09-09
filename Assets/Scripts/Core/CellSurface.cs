using UnityEngine;

namespace Terraform.Core
{
    /// <summary>
    /// The height of the cell surface at an arbitrary point, evaluated exactly as
    /// CellMeshBuilder draws it.
    ///
    /// This is the piece that lets the two representations meet. The surface is not a
    /// formula -- it is eight triangles fanned from each cell's centre out to a ring of
    /// eight boundary points (four corners and four edge midpoints). Anything that wants
    /// to line up with it has to agree triangle for triangle, not approximately.
    ///
    /// The property that makes the seam free: the ring is a straight segment from a corner
    /// to the next edge midpoint. Sampling anywhere along a cell boundary -- at a quarter,
    /// a half, three quarters -- lands exactly on the line the surface mesh already draws.
    /// A span column whose top corners come from here is therefore the SAME surface,
    /// subdivided, with no gap and nothing to reconcile.
    ///
    /// It is also the same failure shape as both mesh tears and both cut/fill bugs: two
    /// things sharing an edge have to ask one question. Here the question is this function.
    /// </summary>
    public static class CellSurface
    {
        /// <summary>
        /// Surface height in metres above the grid origin, at a point given in CELL units
        /// (gx = 1.5 is the middle of the second cell along x).
        ///
        /// Points outside the grid clamp to the nearest cell, so a column in the halo of a
        /// boundary brick still gets a sensible answer instead of falling to zero.
        /// </summary>
        public static float RawAt(CellGrid g, float gx, float gz)
        {
            int cx, cz, wedge;
            float wc, wa, wb;

            if (!Locate(g, gx, gz, out cx, out cz, out wedge, out wc, out wa, out wb))
                return g.GetRaw(cx, cz);

            return RingRaw(g, cx, cz, wedge) * wa
                 + RingRaw(g, cx, cz, (wedge + 1) & 7) * wb
                 + g.GetRaw(cx, cz) * wc;
        }

        /// <summary>
        /// Which wedge of which cell a point falls in, and its barycentric weights over
        /// (centre, ring[wedge], ring[wedge+1]).
        ///
        /// Height and shading have to agree about this or they describe different surfaces,
        /// so both go through here rather than each working it out.
        ///
        /// Returns false at the exact centre, where there is no wedge to pick and the cell's
        /// own value is the answer. cx/cz are always set.
        /// </summary>
        static bool Locate(CellGrid g, float gx, float gz,
                           out int cx, out int cz, out int wedge,
                           out float wc, out float wa, out float wb)
        {
            cx = Mathf.Clamp(Mathf.FloorToInt(gx), 0, g.CellsX - 1);
            cz = Mathf.Clamp(Mathf.FloorToInt(gz), 0, g.CellsZ - 1);

            float u = Mathf.Clamp01(gx - cx);
            float v = Mathf.Clamp01(gz - cz);

            wedge = 0;
            wc = 1f; wa = 0f; wb = 0f;

            float dx = u - 0.5f;
            float dz = v - 0.5f;

            if (Mathf.Abs(dx) < 1e-6f && Mathf.Abs(dz) < 1e-6f) return false;

            // The ring runs counter-clockwise from the south-west corner, so wedge i covers
            // the 45 degrees starting at 225. Picking by angle is the whole triangle search.
            float deg = Mathf.Atan2(dz, dx) * Mathf.Rad2Deg;
            wedge = Mathf.FloorToInt(Repeat(deg - 225f, 360f) / 45f);
            if (wedge > 7) wedge = 7;

            float ax, az, bx, bz;
            RingPoint(wedge, out ax, out az);
            RingPoint((wedge + 1) & 7, out bx, out bz);

            const float qx = 0.5f, qz = 0.5f;

            float d = (az - bz) * (qx - bx) + (bx - ax) * (qz - bz);
            if (Mathf.Abs(d) < 1e-9f) return false;

            wc = ((az - bz) * (u - bx) + (bx - ax) * (v - bz)) / d;
            wa = ((bz - qz) * (u - bx) + (qx - bx) * (v - bz)) / d;

            // Clamped, because a point exactly on a wedge boundary can land a hair outside
            // the triangle the angle picked. Renormalising keeps it on the surface either
            // way rather than letting a rounding error show as a crack.
            wc = Mathf.Clamp01(wc);
            wa = Mathf.Clamp01(wa);
            wb = Mathf.Clamp01(1f - wc - wa);

            float sum = wa + wb + wc;
            if (sum <= 1e-9f) { wc = 1f; wa = 0f; wb = 0f; return false; }

            wa /= sum; wb /= sum; wc /= sum;
            return true;
        }

        public static float MetresAt(CellGrid g, float gx, float gz)
        {
            return RawAt(g, gx, gz) * CellGrid.MetresPerUnit;
        }

        /// <summary>Surface height in metres at a WORLD x/z, relative to the grid origin.</summary>
        public static float MetresAtWorld(CellGrid g, float worldX, float worldZ)
        {
            return MetresAt(g,
                (worldX - g.Origin.x) / g.CellSize,
                (worldZ - g.Origin.z) / g.CellSize);
        }

        /// <summary>Position of ring point i in cell-local [0,1] coordinates.</summary>
        static void RingPoint(int i, out float x, out float z)
        {
            switch (i)
            {
                case 0: x = 0f;   z = 0f;   return;
                case 1: x = 0.5f; z = 0f;   return;
                case 2: x = 1f;   z = 0f;   return;
                case 3: x = 1f;   z = 0.5f; return;
                case 4: x = 1f;   z = 1f;   return;
                case 5: x = 0.5f; z = 1f;   return;
                case 6: x = 0f;   z = 1f;   return;
                default: x = 0f;  z = 0.5f; return;
            }
        }

        /// <summary>Height at ring point i. Mirrors CellMeshBuilder.FillRing exactly.</summary>
        public static float RingRaw(CellGrid g, int cx, int cz, int i)
        {
            switch (i)
            {
                case 0: return g.CornerRawForCell(cx, cz, cx, cz);
                case 1: return g.EdgeRawForCell(cx, cz, cx, cz - 1);
                case 2: return g.CornerRawForCell(cx, cz, cx + 1, cz);
                case 3: return g.EdgeRawForCell(cx, cz, cx + 1, cz);
                case 4: return g.CornerRawForCell(cx, cz, cx + 1, cz + 1);
                case 5: return g.EdgeRawForCell(cx, cz, cx, cz + 1);
                case 6: return g.CornerRawForCell(cx, cz, cx, cz + 1);
                default: return g.EdgeRawForCell(cx, cz, cx - 1, cz);
            }
        }

        // ---- smooth normals ----------------------------------------------------

        /// <summary>
        /// Shading normal at a point in cell units, interpolated across the wedge exactly as
        /// a renderer interpolates the vertex normals the mesh carries. This is what lets a
        /// ceded span cap shade identically to the ground around it.
        /// </summary>
        public static Vector3 NormalAt(CellGrid g, float gx, float gz)
        {
            EnsureNormals(g);

            int cx, cz, wedge;
            float wc, wa, wb;

            if (!Locate(g, gx, gz, out cx, out cz, out wedge, out wc, out wa, out wb))
                return NormalForCell(g, cx, cz, -1);

            Vector3 n = NormalForCell(g, cx, cz, wedge) * wa
                      + NormalForCell(g, cx, cz, (wedge + 1) & 7) * wb
                      + NormalForCell(g, cx, cz, -1) * wc;

            return n.sqrMagnitude < 1e-12f ? Vector3.up : n.normalized;
        }

        /// <summary>Local position of a fan vertex. Ring index -1 is the cell centre.</summary>
        public static Vector3 VertexLocal(CellGrid g, int cx, int cz, int ring)
        {
            float lx, lz;

            if (ring < 0) { lx = 0.5f; lz = 0.5f; }
            else RingPoint(ring, out lx, out lz);

            float raw = ring < 0 ? g.GetRaw(cx, cz) : RingRaw(g, cx, cz, ring);

            return new Vector3((cx + lx) * g.CellSize,
                               raw * CellGrid.MetresPerUnit,
                               (cz + lz) * g.CellSize);
        }

        /// <summary>
        /// Vertex normals for the whole surface, so it shades as a surface rather than as a
        /// heap of triangles.
        ///
        /// Flat shading was what made the terrain read as blocky, not the density of the
        /// mesh. Every triangle carried its own normal, so every facet was a step in the
        /// lighting and the fan showed up as a diamond pattern across every hill. A cell
        /// stores one height and the fan's nine values are all derived from it, so more
        /// boundary points could never have helped -- there is no further shape information
        /// to draw. Shading those nine values as one surface is the whole fix.
        ///
        /// Normals are keyed by position AND height. Two cells that agree at a shared point
        /// land in the same slot and blend; two that disagree -- a levelled pad against the
        /// ground beside it -- land in different slots and keep their hard edge. That is the
        /// same test the mesh already uses to decide where to put a vertical face, so the
        /// shading and the geometry cannot drift apart.
        ///
        /// At most four cells touch any point, so four slots per point is always enough.
        /// </summary>
        const int SlotsPerPoint = 4;

        static CellGrid _cached;
        static int _cachedVersion = -1;
        static int _pointsX, _pointsZ;
        static float[] _slotRaw;
        static Vector3[] _slotNormal;
        static bool[] _slotUsed;

        static readonly Vector3[] Ring = new Vector3[8];
        static readonly float[] RingHeights = new float[8];

        public static void EnsureNormals(CellGrid g)
        {
            if (_cached == g && _cachedVersion == g.Version && _slotUsed != null) return;

            _cached = g;
            _cachedVersion = g.Version;

            _pointsX = g.CellsX * 2 + 1;
            _pointsZ = g.CellsZ * 2 + 1;

            int slots = _pointsX * _pointsZ * SlotsPerPoint;

            if (_slotUsed == null || _slotUsed.Length != slots)
            {
                _slotRaw = new float[slots];
                _slotNormal = new Vector3[slots];
                _slotUsed = new bool[slots];
            }
            else
            {
                for (int i = 0; i < slots; i++)
                {
                    _slotUsed[i] = false;
                    _slotNormal[i] = Vector3.zero;
                }
            }

            for (int cz = 0; cz < g.CellsZ; cz++)
            {
                for (int cx = 0; cx < g.CellsX; cx++)
                {
                    Vector3 centre = VertexLocal(g, cx, cz, -1);
                    float centreRaw = g.GetRaw(cx, cz);

                    for (int i = 0; i < 8; i++)
                    {
                        Ring[i] = VertexLocal(g, cx, cz, i);
                        RingHeights[i] = RingRaw(g, cx, cz, i);
                    }

                    for (int i = 0; i < 8; i++)
                    {
                        int j = (i + 1) & 7;

                        // Same winding as CellMeshBuilder's fan, and deliberately NOT
                        // normalised: the raw cross product is twice the triangle's area, so
                        // summing them weights each face by how much surface it represents.
                        Vector3 n = Vector3.Cross(Ring[j] - centre, Ring[i] - centre);

                        Accumulate(cx, cz, -1, centreRaw, n);
                        Accumulate(cx, cz, j, RingHeights[j], n);
                        Accumulate(cx, cz, i, RingHeights[i], n);
                    }
                }
            }

            for (int i = 0; i < slots; i++)
            {
                if (!_slotUsed[i]) continue;

                _slotNormal[i] = _slotNormal[i].sqrMagnitude < 1e-12f
                    ? Vector3.up
                    : _slotNormal[i].normalized;
            }
        }

        /// <summary>Half-grid index of one of a cell's fan vertices. Ring -1 is the centre.</summary>
        static void PointIndex(int cx, int cz, int ring, out int gx2, out int gz2)
        {
            if (ring < 0) { gx2 = cx * 2 + 1; gz2 = cz * 2 + 1; return; }

            float lx, lz;
            RingPoint(ring, out lx, out lz);

            gx2 = cx * 2 + Mathf.RoundToInt(lx * 2f);
            gz2 = cz * 2 + Mathf.RoundToInt(lz * 2f);
        }

        static void Accumulate(int cx, int cz, int ring, float raw, Vector3 n)
        {
            int gx2, gz2;
            PointIndex(cx, cz, ring, out gx2, out gz2);

            int b = (gz2 * _pointsX + gx2) * SlotsPerPoint;

            for (int s = 0; s < SlotsPerPoint; s++)
            {
                if (_slotUsed[b + s])
                {
                    // Exact equality on purpose. Two cells that agree at a shared point
                    // compute the same value from the same inputs, bit for bit; anything not
                    // exactly equal is a real step and must not be blended across.
                    if (_slotRaw[b + s] != raw) continue;
                }
                else
                {
                    _slotUsed[b + s] = true;
                    _slotRaw[b + s] = raw;
                }

                _slotNormal[b + s] += n;
                return;
            }
        }

        /// <summary>Shading normal one cell uses at one of its fan vertices, -1 for centre.</summary>
        public static Vector3 NormalForCell(CellGrid g, int cx, int cz, int ring)
        {
            EnsureNormals(g);

            float raw = ring < 0 ? g.GetRaw(cx, cz) : RingRaw(g, cx, cz, ring);

            int gx2, gz2;
            PointIndex(cx, cz, ring, out gx2, out gz2);

            if (gx2 < 0 || gz2 < 0 || gx2 >= _pointsX || gz2 >= _pointsZ) return Vector3.up;

            int b = (gz2 * _pointsX + gx2) * SlotsPerPoint;

            for (int s = 0; s < SlotsPerPoint; s++)
            {
                if (!_slotUsed[b + s]) break;
                if (_slotRaw[b + s] == raw) return _slotNormal[b + s];
            }

            return Vector3.up;
        }

        static float Repeat(float t, float length)
        {
            float r = t - Mathf.Floor(t / length) * length;
            return r < 0f ? r + length : r;
        }
    }
}
