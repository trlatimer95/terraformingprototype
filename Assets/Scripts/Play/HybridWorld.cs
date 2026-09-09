using System.Collections.Generic;
using UnityEngine;
using Terraform.Core;
using Terraform.Span;

namespace Terraform.Play
{
    /// <summary>
    /// The two representations joined.
    ///
    /// The rule is that they never both describe the same ground. The cell grid owns the
    /// surface everywhere. The span grid owns nothing at all until something is dug, and
    /// from then on it owns whole bricks -- surface included -- for as long as a void
    /// exists inside them. A brick in that state is CEDED: the surface mesher skips its
    /// cells and the span mesher draws them instead.
    ///
    /// Ceding a whole brick rather than a column is deliberate. The boundary is then a
    /// straight line on a coarse grid instead of a ragged outline that moves with every
    /// swing of a pick, and because the seam is exact its position does not matter
    /// visually. Coarse and exact beats tight and approximate.
    ///
    /// What makes the seam exact is that a ceded brick does not invent its surface. Every
    /// cap sitting at ground level takes its corner heights from CellSurface -- the same
    /// eight-triangle fan the surface mesher draws -- so a ceded patch is the identical
    /// surface, subdivided sixteen ways. Not matched to a tolerance. The same function.
    ///
    /// Columns materialise on first need. Ground nobody has touched is a height and a
    /// depth rule, not stored spans, which is the same "compute the geology, store the
    /// deviations" principle the architecture rests on -- exercised here rather than
    /// asserted.
    /// </summary>
    public sealed class HybridWorld : ISurfaceCaps
    {
        public const float TopsoilDepth = 0.3f;
        public const float SubsoilDepth = 1.6f;

        /// <summary>
        /// Thinnest roof a surface edit may leave over a void. Lowering ground until it
        /// meets a tunnel is a real event that needs real rules -- collapse, or a hole you
        /// can fall down. Until those exist the edit is refused, which is honest, whereas
        /// letting the surface pass through the roof is a representation error.
        /// </summary>
        public const float MinRoof = 0.4f;

        /// <summary>
        /// Cells whose drawn surface can move when one cell's height changes.
        ///
        /// Two, not one. A corner is derived from the four cells touching it and the tie
        /// breaker reads a four-by-four neighbourhood, so a single edit reaches two cells
        /// out. Getting this radius wrong is what made the cut/fill tally disagree with the
        /// mesh earlier; here it would show as a step at the edge of a ceded patch.
        /// </summary>
        public const int SurfaceInfluenceCells = 2;

        public readonly CellGrid Cells;
        public readonly SpanGrid Spans;

        public readonly int ColumnsPerCell;
        public readonly int CellsPerBrick;
        public readonly int ColumnsPerBrick;
        public readonly int BricksX;
        public readonly int BricksZ;

        /// <summary>
        /// What the SURFACE says this column's ground level is, in mm -- not necessarily
        /// where its topmost span actually ends.
        ///
        /// The two part company the moment somebody mines open ground: the pick cuts the
        /// span down, the cell grid still holds the old height, and the column stops being
        /// surface-capped. Keeping them as one number is a bug in slow motion -- an edit two
        /// cells away would find the column "lower than the surface" and quietly fill the
        /// pit back in.
        ///
        /// Separating them also gives the mesher its test for free: a cap is the surface
        /// exactly when its top equals this, so a mined cap declines the surface rule and
        /// smooths as dug ground instead.
        ///
        /// int.MinValue means the column has never been materialised.
        /// </summary>
        readonly int[] _surfaceMm;
        readonly bool[] _ceded;

        readonly HashSet<int> _dirty = new HashSet<int>();

        public int CededBricks { get; private set; }
        public int MaterialisedColumns { get; private set; }
        public HashSet<int> Dirty { get { return _dirty; } }

        /// <summary>Set when an edit was refused for leaving too little roof over a void.</summary>
        public bool LastEditHitRoof { get; private set; }

        /// <summary>How much roof the refused edit would have left, in metres.</summary>
        public float LastRoofMetres { get; private set; }

        public HybridWorld(CellGrid cells, float columnSize, int cellsPerBrick, float floorMetres)
        {
            Cells = cells;

            ColumnsPerCell = Mathf.RoundToInt(cells.CellSize / columnSize);
            CellsPerBrick = cellsPerBrick;
            ColumnsPerBrick = ColumnsPerCell * cellsPerBrick;

            Spans = new SpanGrid(
                cells.CellsX * ColumnsPerCell,
                cells.CellsZ * ColumnsPerCell,
                cells.CellSize / ColumnsPerCell,
                cells.Origin,
                SpanGrid.ToMm(floorMetres));

            BricksX = Mathf.CeilToInt(cells.CellsX / (float)cellsPerBrick);
            BricksZ = Mathf.CeilToInt(cells.CellsZ / (float)cellsPerBrick);

            _surfaceMm = new int[Spans.ColumnsX * Spans.ColumnsZ];
            for (int i = 0; i < _surfaceMm.Length; i++) _surfaceMm[i] = int.MinValue;


            _ceded = new bool[BricksX * BricksZ];
        }

        // ---- indexing ----------------------------------------------------------

        int ColIndex(int colX, int colZ) { return colZ * Spans.ColumnsX + colX; }
        public int BrickIndex(int bx, int bz) { return bz * BricksX + bx; }

        public int BrickOfColumn(int col) { return col / ColumnsPerBrick; }
        public int BrickOfCell(int cell) { return cell / CellsPerBrick; }

        public bool BrickInBounds(int bx, int bz)
        {
            return bx >= 0 && bz >= 0 && bx < BricksX && bz < BricksZ;
        }

        public bool BrickCeded(int bx, int bz)
        {
            return BrickInBounds(bx, bz) && _ceded[BrickIndex(bx, bz)];
        }

        /// <summary>Whether the surface mesher should leave this cell to the span mesher.</summary>
        public bool CellCeded(int cx, int cz)
        {
            return BrickCeded(BrickOfCell(cx), BrickOfCell(cz));
        }

        // ---- the surface, as the span mesher sees it ---------------------------

        public int TopMm(int colX, int colZ)
        {
            if (!Spans.InBounds(colX, colZ)) return int.MinValue;
            return _surfaceMm[ColIndex(colX, colZ)];
        }

        public float CornerMetres(int colX, int colZ, int dx, int dz)
        {
            return CellSurface.MetresAt(Cells,
                (colX + dx) / (float)ColumnsPerCell,
                (colZ + dz) / (float)ColumnsPerCell);
        }

        /// <summary>
        /// Which way a surface cap folds.
        ///
        /// The cell's surface is eight triangles fanned from its centre, so it creases along
        /// BOTH of its diagonals -- centre to south-west and centre to south-east. At four
        /// columns per cell those creases run exactly along column diagonals, so a column
        /// sitting on either one has to fold the same way the cell does or it flattens a
        /// crease that is really there.
        ///
        /// The main diagonal (ix == iz) already matches the mesher's default. The
        /// anti-diagonal (ix + iz == n - 1) is the one that needs the other fold. With an
        /// even number of columns per cell the two sets never overlap.
        /// </summary>
        public bool SwapCapDiagonal(int colX, int colZ)
        {
            int ix = colX % ColumnsPerCell;
            int iz = colZ % ColumnsPerCell;

            return ix + iz == ColumnsPerCell - 1;
        }

        public Vector3 CornerNormal(int colX, int colZ, int dx, int dz)
        {
            return CellSurface.NormalAt(Cells,
                (colX + dx) / (float)ColumnsPerCell,
                (colZ + dz) / (float)ColumnsPerCell);
        }

        /// <summary>Surface at a column's centre, which is the height its spans stop at.</summary>
        public int SurfaceMmAt(int colX, int colZ)
        {
            float metres = CellSurface.MetresAt(Cells,
                (colX + 0.5f) / ColumnsPerCell,
                (colZ + 0.5f) / ColumnsPerCell);

            return SpanGrid.ToMm(metres);
        }

        // ---- materialising -----------------------------------------------------

        /// <summary>
        /// Give a column real spans, layered down from the surface. Idempotent: a column
        /// already carrying stored state is left alone, because that state is the whole
        /// point of storing it.
        /// </summary>
        public void Materialise(int colX, int colZ)
        {
            if (!Spans.InBounds(colX, colZ)) return;

            int i = ColIndex(colX, colZ);
            if (_surfaceMm[i] != int.MinValue) return;

            int top = SurfaceMmAt(colX, colZ);
            int floor = Spans.FloorMm;

            if (top <= floor)
            {
                _surfaceMm[i] = floor;
                MaterialisedColumns++;
                return;
            }

            int soil = Mathf.Max(floor, top - SpanGrid.ToMm(TopsoilDepth));
            int sub = Mathf.Max(floor, soil - SpanGrid.ToMm(SubsoilDepth));

            if (sub > floor) Spans.Append(colX, colZ, floor, sub, RockOrOre(floor, sub, colX, colZ));
            if (soil > sub) Spans.Append(colX, colZ, sub, soil, SpanMaterials.Subsoil);
            if (top > soil) Spans.Append(colX, colZ, soil, top, SpanMaterials.Topsoil);

            _surfaceMm[i] = top;
            MaterialisedColumns++;
        }

        /// <summary>
        /// One ore body, so there is a reason to follow a vein rather than dig a box. Its
        /// shape is a pure function of position -- computed, never stored, which is what
        /// lets an untouched column stay a rule instead of data.
        /// </summary>
        public Rect OreFootprint = new Rect(4f, -14f, 9f, 8f);
        public float OreTopMetres = 3.6f;
        public float OreBottomMetres = 0.8f;

        byte RockOrOre(int bottomMm, int topMm, int colX, int colZ)
        {
            float wx = Spans.WorldX(colX) + Spans.ColumnSize * 0.5f;
            float wz = Spans.WorldZ(colZ) + Spans.ColumnSize * 0.5f;

            if (!OreFootprint.Contains(new Vector2(wx, wz))) return SpanMaterials.Rock;

            // A whole-span decision, so the band is approximate at its edges. Splitting the
            // rock into three spans would be exact and is what a real store should do; this
            // is a demo body and that difference is not what is being tested here.
            int mid = (bottomMm + topMm) / 2;
            return mid >= SpanGrid.ToMm(OreBottomMetres) && mid <= SpanGrid.ToMm(OreTopMetres)
                ? SpanMaterials.Ore
                : SpanMaterials.Rock;
        }

        // ---- ceding ------------------------------------------------------------

        /// <summary>
        /// Hand a brick to the span mesher. Materialises the brick and a one-column halo:
        /// the mesher reads one column past its range to decide which walls are hidden, and
        /// an unmaterialised neighbour reads as open air, which would draw a wall the full
        /// height of the hill along the boundary.
        /// </summary>
        public void Cede(int bx, int bz)
        {
            if (!BrickInBounds(bx, bz)) return;

            int x0 = bx * ColumnsPerBrick - 1;
            int z0 = bz * ColumnsPerBrick - 1;
            int x1 = (bx + 1) * ColumnsPerBrick;
            int z1 = (bz + 1) * ColumnsPerBrick;

            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                    Materialise(cx, cz);

            int b = BrickIndex(bx, bz);
            if (_ceded[b]) return;

            _ceded[b] = true;
            CededBricks++;
            MarkDirty(bx, bz);
        }

        public void MarkDirty(int bx, int bz)
        {
            if (BrickInBounds(bx, bz)) _dirty.Add(BrickIndex(bx, bz));
        }

        public void ClearDirty() { _dirty.Clear(); }

        // ---- surface edits -----------------------------------------------------

        /// <summary>
        /// Bring materialised columns back in line with the surface after a terraform edit.
        ///
        /// Only the TOP moves. Raising appends topsoil above what was there; lowering cuts
        /// down into whatever the column already holds and exposes it. That is not a
        /// convenience -- it is the layered-material behaviour the design calls for, and it
        /// falls out of trimming rather than needing rules of its own.
        ///
        /// Returns false if the edit would leave less than MinRoof over a void, in which
        /// case nothing has been changed and the caller should undo.
        /// </summary>
        public bool ResyncCells(int cx0, int cz0, int cx1, int cz1)
        {
            LastEditHitRoof = false;

            int colX0 = Mathf.Max(0, cx0 * ColumnsPerCell);
            int colZ0 = Mathf.Max(0, cz0 * ColumnsPerCell);
            int colX1 = Mathf.Min(Spans.ColumnsX - 1, (cx1 + 1) * ColumnsPerCell - 1);
            int colZ1 = Mathf.Min(Spans.ColumnsZ - 1, (cz1 + 1) * ColumnsPerCell - 1);

            int minRoofMm = SpanGrid.ToMm(MinRoof);

            // Check before touching anything, so a refusal leaves the store untouched
            // rather than half applied.
            //
            // Only a cut that makes a roof WORSE is refused. Testing the resulting thickness
            // on its own locked out far more ground than it protected: a resync reaches two
            // cells out in every direction, so one thin roof anywhere in that window vetoed
            // every edit near it -- including raising ground, which thickens the roof, and
            // including cells whose surface the edit never moved at all.
            //
            // Improve-or-leave-alone is the same rule the step limits already use, and it
            // needs no radius: an edit that does not lower this column cannot break it, and
            // one that does is judged on what it actually leaves behind.
            for (int cz = colZ0; cz <= colZ1; cz++)
            {
                for (int cx = colX0; cx <= colX1; cx++)
                {
                    int i = ColIndex(cx, cz);
                    if (_surfaceMm[i] == int.MinValue) continue;

                    List<MaterialSpan> column = Spans.Column(cx, cz);
                    if (column.Count < 2) continue;      // no void: nothing to break into
                    if (column[column.Count - 1].TopMm != _surfaceMm[i]) continue;   // not capped

                    int newSurface = SurfaceMmAt(cx, cz);
                    if (newSurface >= _surfaceMm[i]) continue;      // not being lowered

                    int roofBottom = column[column.Count - 1].BottomMm;

                    // A roof this thin is not a roof, it is the lip of a pit, and protecting
                    // it locks ground for no reason. Cutting a cube down from open ground
                    // leaves exactly this all round the rim: a few centimetres of solid over
                    // the air beside it, wherever the ground was higher than the top of the
                    // cube. Every surface dig made a ring of them, and each one vetoed
                    // lowering two cells out in every direction.
                    //
                    // So only a roof already worth keeping is defended, and it is defended
                    // against being cut below the same threshold. A lip is left unprotected,
                    // which also lets it be shovelled away -- the hole tidies up instead of
                    // freezing the ground around it.
                    if (_surfaceMm[i] - roofBottom < minRoofMm) continue;

                    if (newSurface - roofBottom < minRoofMm)
                    {
                        LastEditHitRoof = true;
                        LastRoofMetres = SpanGrid.ToMetres(newSurface - roofBottom);
                        return false;
                    }
                }
            }

            for (int cz = colZ0; cz <= colZ1; cz++)
            {
                for (int cx = colX0; cx <= colX1; cx++)
                {
                    int i = ColIndex(cx, cz);
                    if (_surfaceMm[i] == int.MinValue) continue;

                    int oldSurface = _surfaceMm[i];
                    int newSurface = SurfaceMmAt(cx, cz);
                    if (newSurface == oldSurface) continue;

                    List<MaterialSpan> column = Spans.Column(cx, cz);
                    bool capped = column.Count > 0 && column[column.Count - 1].TopMm == oldSurface;

                    // A column mined open from above is no longer the surface's to move.
                    // Its recorded surface still updates -- neighbouring walls are drawn
                    // against it -- but its spans stay where the pick left them.
                    if (capped)
                    {
                        if (newSurface > oldSurface)
                            Spans.Append(cx, cz, oldSurface, newSurface, SpanMaterials.Topsoil);
                        else
                            Spans.Subtract(cx, cz, newSurface, oldSurface);
                    }

                    _surfaceMm[i] = newSurface;
                    MarkDirty(BrickOfColumn(cx), BrickOfColumn(cz));
                }
            }

            return true;
        }

        // ---- mining ------------------------------------------------------------

        /// <summary>
        /// Cut a cube out of the world. Returns the volume removed, or zero if the swing
        /// found nothing.
        /// </summary>
        public float Mine(Vector3 point, float bite, byte material, bool selective)
        {
            float half = bite * 0.5f;

            int x0 = Mathf.FloorToInt((point.x - half - Spans.Origin.x) / Spans.ColumnSize);
            int x1 = Mathf.FloorToInt((point.x + half - Spans.Origin.x) / Spans.ColumnSize);
            int z0 = Mathf.FloorToInt((point.z - half - Spans.Origin.z) / Spans.ColumnSize);
            int z1 = Mathf.FloorToInt((point.z + half - Spans.Origin.z) / Spans.ColumnSize);

            int bottomMm = SpanGrid.ToMm(point.y - half);
            int topMm = SpanGrid.ToMm(point.y + half);

            // Cede first: the columns have to exist before anything can be taken out of
            // them, and ceding is what materialises them.
            CedeColumnRange(x0, z0, x1, z1);

            float before = SolidVolume(x0, z0, x1, z1);

            for (int cz = z0; cz <= z1; cz++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!Spans.InBounds(cx, cz)) continue;

                    if (selective) Spans.SubtractMaterial(cx, cz, bottomMm, topMm, material);
                    else Spans.Subtract(cx, cz, bottomMm, topMm);
                }
            }

            float removed = before - SolidVolume(x0, z0, x1, z1);

            // The recorded surface is deliberately NOT touched here. A cut that reaches open
            // ground leaves the column below the surface, and that mismatch is the signal --
            // it is what tells the mesher this cap is dug rather than natural, and what stops
            // a later terraform edit nearby from filling the hole back in.
            //
            // It is also the one place the two representations genuinely disagree about the
            // world rather than about how to draw it. See the note in Docs/decisions.md.

            MarkColumnRange(x0 - 1, z0 - 1, x1 + 1, z1 + 1);
            return removed;
        }

        void CedeColumnRange(int colX0, int colZ0, int colX1, int colZ1)
        {
            int bx0 = BrickOfColumn(Mathf.Clamp(colX0, 0, Spans.ColumnsX - 1));
            int bz0 = BrickOfColumn(Mathf.Clamp(colZ0, 0, Spans.ColumnsZ - 1));
            int bx1 = BrickOfColumn(Mathf.Clamp(colX1, 0, Spans.ColumnsX - 1));
            int bz1 = BrickOfColumn(Mathf.Clamp(colZ1, 0, Spans.ColumnsZ - 1));

            for (int bz = bz0; bz <= bz1; bz++)
                for (int bx = bx0; bx <= bx1; bx++)
                    Cede(bx, bz);
        }

        void MarkColumnRange(int colX0, int colZ0, int colX1, int colZ1)
        {
            int bx0 = BrickOfColumn(Mathf.Clamp(colX0, 0, Spans.ColumnsX - 1));
            int bz0 = BrickOfColumn(Mathf.Clamp(colZ0, 0, Spans.ColumnsZ - 1));
            int bx1 = BrickOfColumn(Mathf.Clamp(colX1, 0, Spans.ColumnsX - 1));
            int bz1 = BrickOfColumn(Mathf.Clamp(colZ1, 0, Spans.ColumnsZ - 1));

            for (int bz = bz0; bz <= bz1; bz++)
                for (int bx = bx0; bx <= bx1; bx++)
                    MarkDirty(bx, bz);
        }

        float SolidVolume(int colX0, int colZ0, int colX1, int colZ1)
        {
            float area = Spans.ColumnSize * Spans.ColumnSize;
            float total = 0f;

            for (int cz = colZ0; cz <= colZ1; cz++)
            {
                for (int cx = colX0; cx <= colX1; cx++)
                {
                    if (!Spans.InBounds(cx, cz)) continue;

                    List<MaterialSpan> column = Spans.Column(cx, cz);
                    for (int i = 0; i < column.Count; i++)
                        total += SpanGrid.ToMetres(column[i].HeightMm) * area;
                }
            }

            return total;
        }
    }
}
