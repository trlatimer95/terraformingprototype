using UnityEngine;

namespace Terraform.Core
{
    /// <summary>
    /// Per-cell terrain. Each cell stores ONE height, at its centre, plus whether it has
    /// been flattened.
    ///
    /// This is the crucial difference from a vertex heightfield. There, a quad's interior
    /// is interpolated from its four corners, so the middle can never rise above all of
    /// them -- a cell can't have a peak of its own. Here the centre is a stored point and
    /// the corners are DERIVED, so raising a cell builds a pyramid confined to that cell.
    ///
    /// Corners and edge midpoints are both derived. A corner ignores a lone outlier, so
    /// one raised cell stays a pile inside its own cell. An edge midpoint is the mean of
    /// just the two cells sharing it, so two raised cells meet at the middle of their
    /// shared edge and form a crest running centre to centre.
    /// A flattened cell overrides that and pins its own corners to its centre height,
    /// giving a genuinely flat face and a hard edge against whatever is next to it.
    ///
    /// So untouched terrain stays organic and only deliberately levelled ground goes
    /// crisp -- natural where nature made it, sharp where somebody built.
    /// </summary>
    public sealed class CellGrid
    {
        public const int UnitsPerMetre = 20;   // 5 cm, and every tenth of a metre is exact
        public const float MetresPerUnit = 1f / UnitsPerMetre;
        public const ushort MaxRaw = ushort.MaxValue;   // 3276 m of world height

        public readonly int CellsX;
        public readonly int CellsZ;
        public readonly float CellSize;
        public readonly Vector3 Origin;

        readonly ushort[] _height;
        readonly bool[] _flat;

        /// <summary>Height gap below which four cells count as one sloping group, in units.</summary>
        const int SplitThresholdUnits = 10;   // 0.5 m

        // Single-threaded scratch: this runs once per corner per cell on every rebuild,
        // so it must not allocate.
        static readonly float[] Scratch = new float[4];
        static readonly float[] Quad = new float[4];      // SW, SE, NW, NE around a corner
        static readonly bool[] QuadHas = new bool[4];

        public int Version { get; private set; }
        public float CellArea { get { return CellSize * CellSize; } }

        public CellGrid(int cellsX, int cellsZ, float cellSize, Vector3 origin, float fillMetres)
        {
            CellsX = cellsX;
            CellsZ = cellsZ;
            CellSize = cellSize;
            Origin = origin;

            _height = new ushort[cellsX * cellsZ];
            _flat = new bool[cellsX * cellsZ];

            Fill(fillMetres);
        }

        public static ushort ToRaw(float metres)
        {
            return (ushort)Mathf.Clamp(Mathf.RoundToInt(metres * UnitsPerMetre), 0, MaxRaw);
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


        public bool InBounds(int cx, int cz)
        {
            return cx >= 0 && cz >= 0 && cx < CellsX && cz < CellsZ;
        }

        int Index(int cx, int cz) { return cz * CellsX + cx; }

        public ushort GetRaw(int cx, int cz) { return _height[Index(cx, cz)]; }
        public float GetMetres(int cx, int cz) { return ToMetres(_height[Index(cx, cz)]); }
        public bool IsFlat(int cx, int cz) { return _flat[Index(cx, cz)]; }

        public void SetRaw(int cx, int cz, ushort raw)
        {
            _height[Index(cx, cz)] = raw;
            Version++;
        }

        public void SetFlat(int cx, int cz, bool flat)
        {
            _flat[Index(cx, cz)] = flat;
            Version++;
        }

        public void Fill(float metres)
        {
            ushort raw = ToRaw(metres);
            for (int i = 0; i < _height.Length; i++) { _height[i] = raw; _flat[i] = false; }
            Version++;
        }

        // ---- corners ----------------------------------------------------------

        /// <summary>
        /// The height every cell agrees on at grid corner (gx, gz), where gx runs
        /// 0..CellsX. Out-of-bounds cells are ignored, so chunk edges stay put.
        ///
        /// The cells are split at their widest gap and the larger group wins, so a lone
        /// raised cell never drags its own corners up and stays a pile confined to itself.
        /// An even split sits in the middle: corners are not what carries a ridge here,
        /// edge midpoints are.
        ///
        /// A flattened cell overrides it: the corner rises to meet the pad so the
        /// neighbours slope up to it rather than being cut off by a vertical wall. Highest
        /// wins, so ground always meets the top of a pad instead of leaving a gap.
        /// </summary>
        public float SharedCornerRaw(int gx, int gz)
        {
            int n = 0;
            bool anyFlat = false;
            int flatMax = 0;

            for (int s = 0; s < 4; s++)
            {
                int cx = gx - 1 + (s & 1);
                int cz = gz - 1 + (s >> 1);

                QuadHas[s] = InBounds(cx, cz);
                if (!QuadHas[s]) continue;

                ushort raw = GetRaw(cx, cz);
                Quad[s] = raw;

                if (IsFlat(cx, cz))
                {
                    anyFlat = true;
                    if (raw > flatMax) flatMax = raw;
                }

                Scratch[n++] = raw;
            }

            if (n == 0) return 0f;
            if (anyFlat) return flatMax;
            if (n == 1) return Scratch[0];

            Sort(Scratch, n);

            int split = -1;
            float widest = 0f;

            for (int i = 0; i < n - 1; i++)
            {
                float gap = Scratch[i + 1] - Scratch[i];
                if (gap > widest) { widest = gap; split = i; }
            }

            if (split < 0 || widest < SplitThresholdUnits) return Mean(Scratch, 0, n);

            int lowCount = split + 1;
            int highCount = n - lowCount;

            // A lone outlier loses to the majority, for everyone including itself: one
            // raised cell among three keeps its pile inside its own cell and leaves the
            // neighbours flat.
            if (lowCount > highCount) return Mean(Scratch, 0, lowCount);
            if (highCount > lowCount) return Mean(Scratch, lowCount, highCount);

            float lowMean = Mean(Scratch, 0, lowCount);
            float highMean = Mean(Scratch, lowCount, highCount);
            float middle = (lowMean + highMean) * 0.5f;

            if (n < 4) return middle;

            float splitPoint = (Scratch[lowCount - 1] + Scratch[lowCount]) * 0.5f;

            bool h0 = QuadHas[0] && Quad[0] > splitPoint;
            bool h1 = QuadHas[1] && Quad[1] > splitPoint;
            bool h2 = QuadHas[2] && Quad[2] > splitPoint;
            bool h3 = QuadHas[3] && Quad[3] > splitPoint;

            // Edge-adjacent pair: their shared edge has a MIDPOINT to carry the crest, so
            // the corner stays in the middle. Lifting it would level the shared edge end
            // to end and square the ridge off.
            bool diagonal = (h0 && h3 && !h1 && !h2) || (h1 && h2 && !h0 && !h3);
            if (!diagonal) return middle;

            // Diagonal pair: they share nothing but this corner, so it is the only thing
            // that can join them. It has to be one shared value -- splitting it per side
            // opens a slit, and the middle leaves a half-height saddle -- so it goes to
            // the feature, and the two low cells rise with it into a col between the
            // peaks. That corner belongs to all four cells; there is no way to raise it
            // for two of them alone.
            float reference = NeighbourhoodMeanRaw(gx, gz);

            return Mathf.Abs(highMean - reference) >= Mathf.Abs(lowMean - reference)
                ? highMean      // two piles joining over a col
                : lowMean;      // two pits joining over a channel
        }

        /// <summary>Mean of the 4x4 cells around a corner: what the surrounding ground is doing.</summary>
        float NeighbourhoodMeanRaw(int gx, int gz)
        {
            float sum = 0f;
            int n = 0;

            for (int j = -2; j <= 1; j++)
                for (int i = -2; i <= 1; i++)
                {
                    int cx = gx + i;
                    int cz = gz + j;
                    if (!InBounds(cx, cz)) continue;
                    sum += GetRaw(cx, cz);
                    n++;
                }

            return n == 0 ? 0f : sum / n;
        }

        /// <summary>
        /// Height at the midpoint of the edge between two cells.
        ///
        /// This is what carries a ridge. A crest runs centre to centre, so it crosses the
        /// shared edge at its MIDDLE -- lifting the two corners instead would level the
        /// whole edge end to end and turn a ridge into a wall.
        ///
        /// An edge midpoint belongs to exactly two cells, unlike a corner which belongs to
        /// four. Raising it therefore cannot reach a third cell, which is why this cannot
        /// produce the jutting corners a raised shared corner does.
        /// </summary>
        public float EdgeRawForCell(int cx, int cz, int nx, int nz)
        {
            if (IsFlat(cx, cz)) return GetRaw(cx, cz);
            if (!InBounds(nx, nz)) return GetRaw(cx, cz);      // chunk edge: hold, do not sag
            if (IsFlat(nx, nz)) return GetRaw(nx, nz);         // slope up to meet a pad

            return (GetRaw(cx, cz) + GetRaw(nx, nz)) * 0.5f;
        }

        public float EdgeMetresForCell(int cx, int cz, int nx, int nz)
        {
            return EdgeRawForCell(cx, cz, nx, nz) * MetresPerUnit;
        }

        static float Mean(float[] v, int start, int count)
        {
            float sum = 0f;
            for (int i = 0; i < count; i++) sum += v[start + i];
            return sum / count;
        }

        static void Sort(float[] v, int n)
        {
            for (int i = 1; i < n; i++)
            {
                float key = v[i];
                int k = i - 1;
                while (k >= 0 && v[k] > key) { v[k + 1] = v[k]; k--; }
                v[k + 1] = key;
            }
        }

        /// <summary>
        /// Height one cell draws at one of its own corners. A flattened cell pins to its
        /// own centre so its face is genuinely level; everything else takes the shared
        /// value. Two flattened cells at different heights therefore disagree, and that
        /// disagreement is the sharp step between terraces.
        /// </summary>
        public float CornerRawForCell(int cx, int cz, int gx, int gz)
        {
            return IsFlat(cx, cz) ? GetRaw(cx, cz) : SharedCornerRaw(gx, gz);
        }

        public float CornerMetresForCell(int cx, int cz, int gx, int gz)
        {
            return CornerRawForCell(cx, cz, gx, gz) * MetresPerUnit;
        }

        /// <summary>
        /// Mean height over a cell's surface, given it renders as four triangles from the
        /// centre out to its corners: centre/3 + corners/6. Exact for that mesh, which
        /// keeps the cut/fill tally honest rather than approximate.
        /// </summary>
        public float CellMeanRaw(int cx, int cz)
        {
            float boundary =
                  CornerRawForCell(cx, cz, cx, cz)
                + CornerRawForCell(cx, cz, cx + 1, cz)
                + CornerRawForCell(cx, cz, cx + 1, cz + 1)
                + CornerRawForCell(cx, cz, cx, cz + 1)
                + EdgeRawForCell(cx, cz, cx, cz - 1)
                + EdgeRawForCell(cx, cz, cx + 1, cz)
                + EdgeRawForCell(cx, cz, cx, cz + 1)
                + EdgeRawForCell(cx, cz, cx - 1, cz);

            // Eight triangles fanned from the centre: each contributes an eighth of the
            // area at (centre + two boundary points) / 3.
            return GetRaw(cx, cz) / 3f + boundary / 12f;
        }

        public float CellVolume(int cx, int cz)
        {
            return CellMeanRaw(cx, cz) * MetresPerUnit * CellArea;
        }

        // ---- world space ------------------------------------------------------

        public bool WorldToCell(Vector3 world, out int cx, out int cz)
        {
            cx = Mathf.FloorToInt((world.x - Origin.x) / CellSize);
            cz = Mathf.FloorToInt((world.z - Origin.z) / CellSize);
            return InBounds(cx, cz);
        }

        public Vector3 CellCentreWorld(int cx, int cz)
        {
            return new Vector3(
                Origin.x + (cx + 0.5f) * CellSize,
                Origin.y + GetMetres(cx, cz),
                Origin.z + (cz + 0.5f) * CellSize);
        }

        public Vector3 CornerWorldForCell(int cx, int cz, int gx, int gz)
        {
            return new Vector3(
                Origin.x + gx * CellSize,
                Origin.y + CornerMetresForCell(cx, cz, gx, gz),
                Origin.z + gz * CellSize);
        }
    }
}
