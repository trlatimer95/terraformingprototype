using UnityEngine;

namespace Terraform.Core
{
    /// <summary>
    /// Fixed-point vertex heightfield for a single chunk.
    ///
    /// Heights live at grid VERTICES, not cells. A cell is the quad between four
    /// neighbouring vertices, so moving one vertex changes up to four cells and is
    /// shared with adjacent cells by construction. That sharing is what makes
    /// flattening propagate naturally instead of leaving seams between cells.
    ///
    /// Storage is fixed-point (1/20 m) rather than float. Repeated edits to a float
    /// accumulate drift, which is a save-corruption bug now and a desync later. The
    /// divisor need not be a power of two -- determinism comes from storing integers --
    /// so 20 is chosen to make every tenth of a metre land exactly.
    /// </summary>
    public sealed class HeightGrid
    {
        public const int UnitsPerMetre = 20;   // 5 cm, and every tenth of a metre is exact
        public const float MetresPerUnit = 1f / UnitsPerMetre;
        public const ushort MaxRaw = ushort.MaxValue;   // 3276 m of world height

        public readonly int CellsX;
        public readonly int CellsZ;
        public readonly float CellSize;
        public readonly Vector3 Origin;   // world position of vertex (0,0) at height zero

        readonly ushort[] _heights;

        public int VertsX { get { return CellsX + 1; } }
        public int VertsZ { get { return CellsZ + 1; } }
        public float CellArea { get { return CellSize * CellSize; } }
        public int Version { get; private set; }

        public HeightGrid(int cellsX, int cellsZ, float cellSize, Vector3 origin, float fillMetres)
        {
            CellsX = cellsX;
            CellsZ = cellsZ;
            CellSize = cellSize;
            Origin = origin;
            _heights = new ushort[(cellsX + 1) * (cellsZ + 1)];

            ushort fill = ToRaw(fillMetres);
            for (int i = 0; i < _heights.Length; i++) _heights[i] = fill;
        }

        public static ushort ToRaw(float metres)
        {
            int units = Mathf.RoundToInt(metres * UnitsPerMetre);
            return (ushort)Mathf.Clamp(units, 0, MaxRaw);
        }

        public static float ToMetres(int raw) { return raw * MetresPerUnit; }

        /// <summary>The step generated ground is snapped to: 0.1 m, matching the tools.</summary>
        public const int GenerationStepUnits = 2;

        /// <summary>
        /// ToRaw, but landing on a whole multiple of stepUnits.
        ///
        /// A height function puts ground wherever it likes -- 0.15 m, say -- while the
        /// tools move in 0.1 m steps. A player can then never bring a generated cell level
        /// with anything, because every click keeps the 0.05 m remainder. Snapping the
        /// generators onto the same ladder the tools climb makes every generated height
        /// reachable.
        /// </summary>
        public static ushort SnapRaw(float metres, int stepUnits)
        {
            if (stepUnits <= 1) return ToRaw(metres);

            int units = Mathf.RoundToInt(metres * UnitsPerMetre / (float)stepUnits) * stepUnits;

            // Clamp to the highest multiple that still fits, so the result stays on the
            // ladder even at the ceiling.
            int ceiling = (MaxRaw / stepUnits) * stepUnits;
            return (ushort)Mathf.Clamp(units, 0, ceiling);
        }


        public bool InBounds(int vx, int vz)
        {
            return vx >= 0 && vz >= 0 && vx < VertsX && vz < VertsZ;
        }

        int Index(int vx, int vz) { return vz * VertsX + vx; }

        public ushort GetRaw(int vx, int vz) { return _heights[Index(vx, vz)]; }
        public float GetMetres(int vx, int vz) { return ToMetres(_heights[Index(vx, vz)]); }

        public void SetRaw(int vx, int vz, ushort raw)
        {
            _heights[Index(vx, vz)] = raw;
            Version++;
        }

        /// <summary>
        /// Ground area a vertex is responsible for. An interior vertex contributes a
        /// quarter of each of its four adjacent cells, so it owns exactly one cell's
        /// worth; edge and corner vertices own a half and a quarter of that. Getting
        /// this right keeps the cut/fill tally honest at the chunk boundary.
        /// </summary>
        /// <summary>
        /// Volume of one cell, measured the way the mesh is actually built.
        ///
        /// NOT the mean of the four corners. ChunkMeshBuilder splits each quad along its
        /// flatter diagonal, and the two choices enclose different volumes whenever the
        /// quad is non-planar:
        ///
        ///     split a-c  ->  (A/12)(4a + 2b + 4c + 2d)
        ///     split b-d  ->  (A/12)(2a + 4b + 2c + 4d)
        ///
        /// A per-vertex area sum returns (A/12)(3a+3b+3c+3d) -- exactly the average of the
        /// two -- so it reports earth the ground does not contain, by up to
        /// (A/12)|a-b+c-d|. One corner raised a metre on a 1 m cell is 0.083 m3 of that,
        /// and it does not cancel out across a slope.
        /// </summary>
        public float CellVolume(int cx, int cz)
        {
            float a = GetRaw(cx, cz);
            float b = GetRaw(cx + 1, cz);
            float c = GetRaw(cx + 1, cz + 1);
            float d = GetRaw(cx, cz + 1);

            // Same test as the mesh builder, so the two never disagree about a diagonal.
            float sum = Mathf.Abs(a - c) <= Mathf.Abs(b - d)
                ? (a + d + c) + (a + c + b)
                : (a + d + b) + (b + d + c);

            // Two triangles, each covering half the cell, each at the mean of its corners.
            return sum / 3f * 0.5f * MetresPerUnit * CellArea;
        }

        public float VertexArea(int vx, int vz)
        {
            int nx = (vx > 0 ? 1 : 0) + (vx < CellsX ? 1 : 0);
            int nz = (vz > 0 ? 1 : 0) + (vz < CellsZ ? 1 : 0);
            return CellArea * (nx * nz) * 0.25f;
        }

        /// <summary>Flatten the whole grid to one height. P0 reset; also the P4 gen seed.</summary>
        public void Fill(float metres)
        {
            ushort raw = ToRaw(metres);
            for (int i = 0; i < _heights.Length; i++) _heights[i] = raw;
            Version++;
        }

        /// <summary>World position of a grid vertex, including its height.</summary>
        public Vector3 VertexWorld(int vx, int vz)
        {
            return new Vector3(
                Origin.x + vx * CellSize,
                Origin.y + GetMetres(vx, vz),
                Origin.z + vz * CellSize);
        }

        /// <summary>Nearest grid vertex to a world position. False if outside this chunk.</summary>
        public bool WorldToNearestVertex(Vector3 world, out int vx, out int vz)
        {
            vx = Mathf.RoundToInt((world.x - Origin.x) / CellSize);
            vz = Mathf.RoundToInt((world.z - Origin.z) / CellSize);
            return InBounds(vx, vz);
        }

        /// <summary>
        /// Continuous grid coordinates for a world position: 1.0 == one cell. Unsnapped,
        /// so a brush can be centred where the player is actually aiming rather than
        /// jumping to the nearest vertex.
        /// </summary>
        public Vector2 WorldToGridPoint(Vector3 world)
        {
            return new Vector2((world.x - Origin.x) / CellSize, (world.z - Origin.z) / CellSize);
        }

        /// <summary>Bilinear height at continuous grid coordinates, in metres.</summary>
        public float SampleMetres(float gx, float gz)
        {
            gx = Mathf.Clamp(gx, 0f, CellsX);
            gz = Mathf.Clamp(gz, 0f, CellsZ);

            int x0 = Mathf.Min(Mathf.FloorToInt(gx), CellsX - 1);
            int z0 = Mathf.Min(Mathf.FloorToInt(gz), CellsZ - 1);

            float tx = gx - x0;
            float tz = gz - z0;

            float a = Mathf.Lerp(GetMetres(x0, z0), GetMetres(x0 + 1, z0), tx);
            float b = Mathf.Lerp(GetMetres(x0, z0 + 1), GetMetres(x0 + 1, z0 + 1), tx);
            return Mathf.Lerp(a, b, tz);
        }

        /// <summary>
        /// Cell containing a world position. Cell (cx,cz) spans vertices cx..cx+1, cz..cz+1.
        /// </summary>
        public bool WorldToCell(Vector3 world, out int cx, out int cz)
        {
            cx = Mathf.FloorToInt((world.x - Origin.x) / CellSize);
            cz = Mathf.FloorToInt((world.z - Origin.z) / CellSize);
            return cx >= 0 && cz >= 0 && cx < CellsX && cz < CellsZ;
        }
    }
}
