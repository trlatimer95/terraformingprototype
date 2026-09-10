using UnityEngine;

namespace Terraform.Core
{
    /// <summary>
    /// The vertex surface as a function: its height, its shading normal, and which way each
    /// cell folds. The heightfield counterpart of CellSurface.
    ///
    /// Simpler than the cell version in one important way. A heightfield stores ONE height
    /// per vertex, so neighbouring cells cannot disagree about a shared point -- there are no
    /// vertical faces to preserve and nothing to key normals by except position. That is not
    /// a limitation here, it is the requirement: two flat areas at different heights cannot
    /// sit directly against each other, because the vertices along their shared edge would
    /// have to hold two values at once. The transition has to slope. The rule is structural
    /// rather than enforced.
    ///
    /// The fold matters as much as it does for cells. ChunkMeshBuilder splits each cell along
    /// its FLATTER diagonal, so the surface creases differently from cell to cell, and a span
    /// cap that folded the other way would flatten a crease that is really there.
    /// </summary>
    public static class VertexSurface
    {
        /// <summary>
        /// Does this cell split along b-d (south-east to north-west) rather than a-c?
        ///
        /// Mirrors ChunkMeshBuilder exactly. Splitting along the flatter diagonal is what
        /// stops a level platform looking warped where its quad is non-planar, so the choice
        /// is visible and has to be reproduced rather than guessed.
        /// </summary>
        public static bool AntiDiagonal(HeightGrid g, int cx, int cz)
        {
            cx = Mathf.Clamp(cx, 0, g.CellsX - 1);
            cz = Mathf.Clamp(cz, 0, g.CellsZ - 1);

            float a = g.GetRaw(cx, cz);
            float b = g.GetRaw(cx + 1, cz);
            float c = g.GetRaw(cx + 1, cz + 1);
            float d = g.GetRaw(cx, cz + 1);

            return Mathf.Abs(a - c) > Mathf.Abs(b - d);
        }

        /// <summary>
        /// Height in raw units at a point in cell units, interpolated across whichever
        /// triangle actually contains it.
        /// </summary>
        public static float RawAt(HeightGrid g, float gx, float gz)
        {
            int cx = Mathf.Clamp(Mathf.FloorToInt(gx), 0, g.CellsX - 1);
            int cz = Mathf.Clamp(Mathf.FloorToInt(gz), 0, g.CellsZ - 1);

            float u = Mathf.Clamp01(gx - cx);
            float v = Mathf.Clamp01(gz - cz);

            float a = g.GetRaw(cx, cz);
            float b = g.GetRaw(cx + 1, cz);
            float c = g.GetRaw(cx + 1, cz + 1);
            float d = g.GetRaw(cx, cz + 1);

            if (!AntiDiagonal(g, cx, cz))
            {
                // Split a-c. Below the diagonal is (a, b, c); above is (a, c, d).
                return v <= u
                    ? a * (1f - u) + b * (u - v) + c * v
                    : a * (1f - v) + c * u + d * (v - u);
            }

            // Split b-d. Inside the lower-left triangle is (a, b, d); beyond it is (b, c, d).
            return u + v <= 1f
                ? a * (1f - u - v) + b * u + d * v
                : c * (u + v - 1f) + b * (1f - v) + d * (1f - u);
        }

        public static float MetresAt(HeightGrid g, float gx, float gz)
        {
            return RawAt(g, gx, gz) * HeightGrid.MetresPerUnit;
        }

        public static float MetresAtWorld(HeightGrid g, float worldX, float worldZ)
        {
            return MetresAt(g,
                (worldX - g.Origin.x) / g.CellSize,
                (worldZ - g.Origin.z) / g.CellSize);
        }

        // ---- smooth normals ----------------------------------------------------

        static HeightGrid _cached;
        static int _cachedVersion = -1;
        static Vector3[] _normals;
        static int _vertsX;

        /// <summary>
        /// Area-weighted vertex normals over the whole field.
        ///
        /// No height key, unlike the cell version: a vertex has exactly one height, so every
        /// triangle meeting there is describing the same piece of ground and they all belong
        /// in the same average. Nothing to keep apart.
        /// </summary>
        public static void EnsureNormals(HeightGrid g)
        {
            if (_cached == g && _cachedVersion == g.Version && _normals != null) return;

            _cached = g;
            _cachedVersion = g.Version;
            _vertsX = g.VertsX;

            int count = g.VertsX * g.VertsZ;

            if (_normals == null || _normals.Length != count) _normals = new Vector3[count];
            for (int i = 0; i < count; i++) _normals[i] = Vector3.zero;

            for (int cz = 0; cz < g.CellsZ; cz++)
            {
                for (int cx = 0; cx < g.CellsX; cx++)
                {
                    Vector3 a = Local(g, cx, cz);
                    Vector3 b = Local(g, cx + 1, cz);
                    Vector3 c = Local(g, cx + 1, cz + 1);
                    Vector3 d = Local(g, cx, cz + 1);

                    if (!AntiDiagonal(g, cx, cz))
                    {
                        Add(g, cx, cz, cx, cz + 1, cx + 1, cz + 1, a, d, c);
                        Add(g, cx, cz, cx + 1, cz + 1, cx + 1, cz, a, c, b);
                    }
                    else
                    {
                        Add(g, cx, cz, cx, cz + 1, cx + 1, cz, a, d, b);
                        Add(g, cx + 1, cz, cx, cz + 1, cx + 1, cz + 1, b, d, c);
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                _normals[i] = _normals[i].sqrMagnitude < 1e-12f
                    ? Vector3.up
                    : _normals[i].normalized;
            }
        }

        /// <summary>Not normalised, so each face is weighted by twice its own area.</summary>
        static void Add(HeightGrid g, int ax, int az, int bx, int bz, int cx2, int cz2,
                        Vector3 p0, Vector3 p1, Vector3 p2)
        {
            Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);

            _normals[az * _vertsX + ax] += n;
            _normals[bz * _vertsX + bx] += n;
            _normals[cz2 * _vertsX + cx2] += n;
        }

        static Vector3 Local(HeightGrid g, int vx, int vz)
        {
            return new Vector3(vx * g.CellSize, g.GetMetres(vx, vz), vz * g.CellSize);
        }

        public static Vector3 VertexNormal(HeightGrid g, int vx, int vz)
        {
            EnsureNormals(g);

            vx = Mathf.Clamp(vx, 0, g.CellsX);
            vz = Mathf.Clamp(vz, 0, g.CellsZ);

            return _normals[vz * _vertsX + vx];
        }

        /// <summary>
        /// Shading normal at a point in cell units, blended across the containing triangle
        /// exactly as a renderer blends the vertex normals a mesh carries.
        /// </summary>
        public static Vector3 NormalAt(HeightGrid g, float gx, float gz)
        {
            EnsureNormals(g);

            int cx = Mathf.Clamp(Mathf.FloorToInt(gx), 0, g.CellsX - 1);
            int cz = Mathf.Clamp(Mathf.FloorToInt(gz), 0, g.CellsZ - 1);

            float u = Mathf.Clamp01(gx - cx);
            float v = Mathf.Clamp01(gz - cz);

            Vector3 na = VertexNormal(g, cx, cz);
            Vector3 nb = VertexNormal(g, cx + 1, cz);
            Vector3 nc = VertexNormal(g, cx + 1, cz + 1);
            Vector3 nd = VertexNormal(g, cx, cz + 1);

            Vector3 n;

            if (!AntiDiagonal(g, cx, cz))
            {
                n = v <= u
                    ? na * (1f - u) + nb * (u - v) + nc * v
                    : na * (1f - v) + nc * u + nd * (v - u);
            }
            else
            {
                n = u + v <= 1f
                    ? na * (1f - u - v) + nb * u + nd * v
                    : nc * (u + v - 1f) + nb * (1f - v) + nd * (1f - u);
            }

            return n.sqrMagnitude < 1e-12f ? Vector3.up : n.normalized;
        }
    }
}
