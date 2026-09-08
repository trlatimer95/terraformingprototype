using System.Collections.Generic;
using UnityEngine;

namespace Terraform.Span
{
    /// <summary>
    /// One uniformly composed prism over a single column footprint.
    ///
    /// Bounds are millimetres: bottom inclusive, top exclusive, so a material interface is
    /// owned by exactly one span. Vertical position is NOT quantised to the column size --
    /// that is the whole point of spans over cubes. A 0.25 m column can still hold a
    /// soil/rock interface at 4.2 m.
    /// </summary>
    public struct MaterialSpan
    {
        public int BottomMm;
        public int TopMm;
        public byte Material;

        public MaterialSpan(int bottomMm, int topMm, byte material)
        {
            BottomMm = bottomMm;
            TopMm = topMm;
            Material = material;
        }

        public int HeightMm { get { return TopMm - BottomMm; } }
    }

    /// <summary>
    /// A grid of columns, each holding sorted non-overlapping solid spans. A gap in the
    /// list is air, and more than one gap can occur at different depths -- which is what
    /// lets a column describe a tunnel with intact ground above it.
    ///
    /// This is the appearance gate's store, not the production one. Every column gets a
    /// List allocated up front, which is exactly what the design says not to do at world
    /// scale; here it buys clarity for a few thousand columns that never stream.
    /// </summary>
    public sealed class SpanGrid
    {
        public const float MmPerMetre = 1000f;

        public readonly int ColumnsX;
        public readonly int ColumnsZ;
        public readonly float ColumnSize;
        public readonly Vector3 Origin;

        /// <summary>
        /// Bottom of the supported vertical domain. Bedrock, effectively: the mesher treats
        /// it as a boundary rather than a surface, so the world does not carry a downward
        /// face under every column that nothing can ever see.
        /// </summary>
        public readonly int FloorMm;

        readonly List<MaterialSpan>[] _columns;

        public SpanGrid(int columnsX, int columnsZ, float columnSize, Vector3 origin, int floorMm)
        {
            ColumnsX = columnsX;
            ColumnsZ = columnsZ;
            ColumnSize = columnSize;
            Origin = origin;
            FloorMm = floorMm;

            _columns = new List<MaterialSpan>[columnsX * columnsZ];
            for (int i = 0; i < _columns.Length; i++) _columns[i] = new List<MaterialSpan>(4);
        }

        public bool InBounds(int cx, int cz)
        {
            return cx >= 0 && cz >= 0 && cx < ColumnsX && cz < ColumnsZ;
        }

        /// <summary>The column's spans, sorted bottom to top. Never null in bounds.</summary>
        public List<MaterialSpan> Column(int cx, int cz)
        {
            return _columns[cz * ColumnsX + cx];
        }

        public static int ToMm(float metres) { return Mathf.RoundToInt(metres * MmPerMetre); }
        public static float ToMetres(int mm) { return mm / MmPerMetre; }

        public float WorldX(int cx) { return Origin.x + cx * ColumnSize; }
        public float WorldZ(int cz) { return Origin.z + cz * ColumnSize; }

        /// <summary>Total live spans, which is the number the fragmentation limit watches.</summary>
        public int SpanCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _columns.Length; i++) n += _columns[i].Count;
                return n;
            }
        }

        /// <summary>
        /// Append a span on top of a column. Coalesces only with an identical material that
        /// it exactly touches -- never merely because the material ids match, since in the
        /// real store provenance and allocation would differ.
        /// </summary>
        public void Append(int cx, int cz, int bottomMm, int topMm, byte material)
        {
            if (topMm <= bottomMm || !InBounds(cx, cz)) return;

            List<MaterialSpan> column = Column(cx, cz);

            if (column.Count > 0)
            {
                MaterialSpan last = column[column.Count - 1];
                if (last.TopMm == bottomMm && last.Material == material)
                {
                    last.TopMm = topMm;
                    column[column.Count - 1] = last;
                    return;
                }
            }

            column.Add(new MaterialSpan(bottomMm, topMm, material));
        }

        static readonly List<MaterialSpan> Scratch = new List<MaterialSpan>(8);

        /// <summary>
        /// Remove a vertical interval from a column. A cut through the middle of a span
        /// leaves two remnants; this is the operation that opens a tunnel under intact
        /// ground, and the reason a column needs a list rather than a height.
        /// </summary>
        public void Subtract(int cx, int cz, int bottomMm, int topMm)
        {
            if (topMm <= bottomMm || !InBounds(cx, cz)) return;

            List<MaterialSpan> column = Column(cx, cz);
            if (column.Count == 0) return;

            Scratch.Clear();

            for (int i = 0; i < column.Count; i++)
            {
                MaterialSpan s = column[i];

                if (s.TopMm <= bottomMm || s.BottomMm >= topMm)
                {
                    Scratch.Add(s);            // untouched
                    continue;
                }

                if (s.BottomMm < bottomMm)
                    Scratch.Add(new MaterialSpan(s.BottomMm, bottomMm, s.Material));

                if (s.TopMm > topMm)
                    Scratch.Add(new MaterialSpan(topMm, s.TopMm, s.Material));
            }

            column.Clear();
            column.AddRange(Scratch);
        }

        /// <summary>
        /// Is any solid stacked above this height in the column? True means standing here
        /// puts rock overhead -- which is the only reliable way to know you are in a tunnel
        /// rather than a trench.
        /// </summary>
        public bool SolidAbove(int cx, int cz, int mm)
        {
            if (!InBounds(cx, cz)) return false;

            List<MaterialSpan> column = Column(cx, cz);
            for (int i = column.Count - 1; i >= 0; i--)
                if (column[i].BottomMm >= mm) return true;

            return false;
        }

        /// <summary>
        /// Remove an interval, but only where the span is the given material. This is what
        /// lets a pick take the ore out of a face and leave the rock standing, instead of
        /// forcing the player to choose between the whole bite and none of it.
        /// </summary>
        public void SubtractMaterial(int cx, int cz, int bottomMm, int topMm, byte material)
        {
            if (topMm <= bottomMm || !InBounds(cx, cz)) return;

            List<MaterialSpan> column = Column(cx, cz);
            if (column.Count == 0) return;

            Scratch.Clear();

            for (int i = 0; i < column.Count; i++)
            {
                MaterialSpan s = column[i];

                if (s.Material != material || s.TopMm <= bottomMm || s.BottomMm >= topMm)
                {
                    Scratch.Add(s);
                    continue;
                }

                if (s.BottomMm < bottomMm)
                    Scratch.Add(new MaterialSpan(s.BottomMm, bottomMm, s.Material));

                if (s.TopMm > topMm)
                    Scratch.Add(new MaterialSpan(topMm, s.TopMm, s.Material));
            }

            column.Clear();
            column.AddRange(Scratch);
        }

        /// <summary>Material at a height in this column, or 255 for air.</summary>
        public byte MaterialAt(int cx, int cz, int mm)
        {
            if (!InBounds(cx, cz)) return 255;

            List<MaterialSpan> column = Column(cx, cz);
            for (int i = 0; i < column.Count; i++)
                if (mm >= column[i].BottomMm && mm < column[i].TopMm) return column[i].Material;

            return 255;
        }

        /// <summary>
        /// The open surface nearest a height -- the one you would be standing on. A column
        /// with a tunnel under a mountain has several, and the topmost is the summit.
        /// </summary>
        public int OpenTopNear(int cx, int cz, int targetMm)
        {
            if (!InBounds(cx, cz)) return int.MinValue;

            List<MaterialSpan> column = Column(cx, cz);

            int best = int.MinValue;
            int bestDelta = int.MaxValue;

            for (int i = 0; i < column.Count; i++)
            {
                if (i + 1 < column.Count && column[i + 1].BottomMm == column[i].TopMm) continue;

                int delta = Mathf.Abs(column[i].TopMm - targetMm);
                if (delta >= bestDelta) continue;

                bestDelta = delta;
                best = column[i].TopMm;
            }

            return best;
        }

        /// <summary>
        /// The open ceiling nearest a height -- the underside of a span with air below it.
        /// The mirror of OpenTopNear, and needed for the same reason: a wall that ends at a
        /// ceiling has to end where that ceiling actually is.
        /// </summary>
        public int OpenBottomNear(int cx, int cz, int targetMm)
        {
            if (!InBounds(cx, cz)) return int.MinValue;

            List<MaterialSpan> column = Column(cx, cz);

            int best = int.MinValue;
            int bestDelta = int.MaxValue;

            for (int i = 0; i < column.Count; i++)
            {
                if (i > 0 && column[i - 1].TopMm == column[i].BottomMm) continue;

                int delta = Mathf.Abs(column[i].BottomMm - targetMm);
                if (delta >= bestDelta) continue;

                bestDelta = delta;
                best = column[i].BottomMm;
            }

            return best;
        }

        /// <summary>Height of the topmost solid in this column, or int.MinValue if empty.</summary>
        public int SurfaceMm(int cx, int cz)
        {
            if (!InBounds(cx, cz)) return int.MinValue;

            List<MaterialSpan> column = Column(cx, cz);
            return column.Count == 0 ? int.MinValue : column[column.Count - 1].TopMm;
        }
    }
}
