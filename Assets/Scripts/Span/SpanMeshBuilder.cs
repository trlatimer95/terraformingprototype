using System.Collections.Generic;
using UnityEngine;

namespace Terraform.Span
{
    /// <summary>
    /// Turns spans into geometry. Two modes.
    ///
    /// EXACT reproduces the occupied prisms with no displacement: the mesh encloses exactly
    /// the volume the spans claim, which makes it the reference for accounting. It is also
    /// the one that produces flat caps and a staircase underfoot on any slope.
    ///
    /// SMOOTHED tilts each open cap toward its neighbours, so a floor descending a column at
    /// a time becomes a ramp while walls stay hard-edged. Only caps with air above them move,
    /// and only toward neighbours within SmoothThresholdMm -- past that the edge is a wall or
    /// a step and must stay square.
    ///
    /// Smoothing breaks the exact volume correspondence: the rendered floor no longer matches
    /// the accounted spans. That is a bounded, documented tolerance rather than a redesign,
    /// but it is a real cost and the reason both modes exist.
    /// </summary>
    /// <summary>
    /// Where the span world's top meets a surface owned by something else.
    ///
    /// A span column in a hybrid world is not free to choose its own top. Its top IS the
    /// surface, and the surface is drawn by another mesher a few centimetres away. If the
    /// two disagree by a millimetre you get a crack; if they disagree by a smoothing rule
    /// you get a blocky scar across a smooth hillside. So the span mesher asks the surface
    /// rather than deciding.
    /// </summary>
    public interface ISurfaceCaps
    {
        /// <summary>Where this column's solid ends at the surface, or int.MinValue if it is
        /// not a surface column at all.</summary>
        int TopMm(int cx, int cz);

        /// <summary>Surface height in metres at one corner of a column, dx/dz in {0,1}.</summary>
        float CornerMetres(int cx, int cz, int dx, int dz);

        /// <summary>
        /// Which way to split this column's surface cap into triangles: false for the
        /// default south-west to north-east, true for the other diagonal.
        ///
        /// A quad with four corner heights is not a surface until you say how it folds, and
        /// picking the wrong fold is not a rounding error -- it flattens a crease the
        /// surface actually has. Four of the sixteen columns under a cell sit on the fan's
        /// anti-diagonal and need the other split, so the owner of the surface has to be the
        /// one that answers this.
        /// </summary>
        bool SwapCapDiagonal(int cx, int cz);

        /// <summary>
        /// Shading normal of the surface at one corner of a column, dx/dz in {0,1}.
        ///
        /// A ceded cap that matched the surrounding ground geometrically but shaded off its
        /// own flat faces would announce itself as a patch of differently-lit hillside --
        /// the seam solved in position and given straight back in light.
        /// </summary>
        Vector3 CornerNormal(int cx, int cz, int dx, int dz);
    }

    public static class SpanMeshBuilder
    {
        public static bool Smooth = true;

        /// <summary>
        /// Set in a hybrid world, null in a pure span world. When set, any cap sitting at the
        /// surface defers to it entirely and the clustering below is bypassed.
        /// </summary>
        public static ISurfaceCaps Surface;

        /// <summary>
        /// Steepest slope between neighbouring caps that still counts as one surface. Past
        /// this they are a step or a wall and must keep their hard edge.
        ///
        /// An ANGLE, not a height. A fixed height cannot work across resolutions: 350 mm is a
        /// 19-degree slope between 1 m columns and a 70-degree one between 0.125 m columns,
        /// so the same number smoothed almost nothing at the coarse end and smoothed straight
        /// through genuine steps at the fine end. That made the resolutions look far more
        /// different from each other than they are.
        /// </summary>
        public static float SmoothMaxAngle = 40f;

        /// <summary>Derived per build from the column size. See SmoothMaxAngle.</summary>
        static int SmoothThresholdMm;

        static readonly List<Vector3> Verts = new List<Vector3>();
        static readonly List<Vector3> Normals = new List<Vector3>();

        /// <summary>
        /// Planar world XZ, matching CellMeshBuilder exactly. Without this a ceded patch of
        /// ground would sample the texture at a single point and read as a flat swatch let
        /// into a textured hillside -- the seam solved geometrically and then given straight
        /// back in shading.
        ///
        /// Offset by the brick's local origin so the mapping is continuous across bricks
        /// rather than restarting at each one.
        /// </summary>
        static readonly List<Vector2> Uvs = new List<Vector2>();
        static Vector3 UvOrigin;
        static readonly List<int>[] Tris = new List<int>[SpanMaterials.Count];

        /// <summary>Remnants of one span after a neighbour's solid is subtracted from it.</summary>
        static readonly List<Vector2Int> Exposed = new List<Vector2Int>(8);

        static readonly int[] StepX = { 1, -1, 0, 0 };
        static readonly int[] StepZ = { 0, 0, 1, -1 };

        static SpanMeshBuilder()
        {
            for (int i = 0; i < Tris.Length; i++) Tris[i] = new List<int>();
        }

        /// <summary>
        /// Build one brick. Columns outside [x0,x0+w) x [z0,z0+h) are read for side-face
        /// culling and corner smoothing but never contribute geometry -- that read-only halo
        /// is what stops two neighbouring bricks each drawing the wall between them, and what
        /// keeps a smoothed floor continuous across a brick boundary.
        /// </summary>
        public static void Build(SpanGrid grid, int x0, int z0, int w, int h, Mesh mesh, Vector3 localOrigin)
        {
            Verts.Clear();
            Normals.Clear();
            Uvs.Clear();
            for (int i = 0; i < Tris.Length; i++) Tris[i].Clear();

            UvOrigin = localOrigin;

            float size = grid.ColumnSize;

            SmoothThresholdMm = Mathf.RoundToInt(
                size * Mathf.Tan(SmoothMaxAngle * Mathf.Deg2Rad) * SpanGrid.MmPerMetre);

            for (int cz = z0; cz < z0 + h; cz++)
            {
                for (int cx = x0; cx < x0 + w; cx++)
                {
                    if (!grid.InBounds(cx, cz)) continue;

                    List<MaterialSpan> column = grid.Column(cx, cz);
                    if (column.Count == 0) continue;

                    float wx = grid.WorldX(cx) - localOrigin.x;
                    float wz = grid.WorldZ(cz) - localOrigin.z;

                    for (int i = 0; i < column.Count; i++)
                    {
                        MaterialSpan s = column[i];

                        float bottom = SpanGrid.ToMetres(s.BottomMm) - localOrigin.y;
                        float flat = SpanGrid.ToMetres(s.TopMm) - localOrigin.y;

                        // A cap only exists where the neighbour above or below is air.
                        // Touching spans of different materials are still one solid run.
                        bool solidAbove = i + 1 < column.Count && column[i + 1].BottomMm == s.TopMm;
                        bool solidBelow = i > 0 && column[i - 1].TopMm == s.BottomMm;

                        // Corner heights for this span's top. Equal to the flat top unless
                        // this is an open cap and smoothing is on.
                        float y00 = flat, y10 = flat, y11 = flat, y01 = flat;

                        if (!solidAbove && Smooth)
                        {
                            y00 = Corner(grid, cx, cz, 0, 0, s.TopMm, localOrigin.y);
                            y10 = Corner(grid, cx, cz, 1, 0, s.TopMm, localOrigin.y);
                            y11 = Corner(grid, cx, cz, 1, 1, s.TopMm, localOrigin.y);
                            y01 = Corner(grid, cx, cz, 0, 1, s.TopMm, localOrigin.y);
                        }

                        bool swap = Surface != null
                                 && Surface.TopMm(cx, cz) == s.TopMm
                                 && Surface.SwapCapDiagonal(cx, cz);

                        if (!solidAbove)
                        {
                            if (Surface != null && Surface.TopMm(cx, cz) == s.TopMm)
                                SurfaceCap(wx, wz, size, y00, y10, y11, y01, s.Material, swap, cx, cz);
                            else
                                Cap(wx, wz, size, y00, y10, y11, y01, s.Material, true, swap);
                        }

                        // No downward face on bedrock. It would be two triangles per column
                        // that nothing can ever see.
                        bool openBelow = !solidBelow && s.BottomMm > grid.FloorMm;

                        float b00 = bottom, b10 = bottom, b11 = bottom, b01 = bottom;

                        // Ceilings smooth on the same rule as floors. On a ramped tunnel the
                        // roof steps exactly as the floor does, and a stepped roof over a
                        // smooth floor reads worse than either on its own.
                        if (openBelow && Smooth)
                        {
                            b00 = CornerBottom(grid, cx, cz, 0, 0, s.BottomMm, localOrigin.y);
                            b10 = CornerBottom(grid, cx, cz, 1, 0, s.BottomMm, localOrigin.y);
                            b11 = CornerBottom(grid, cx, cz, 1, 1, s.BottomMm, localOrigin.y);
                            b01 = CornerBottom(grid, cx, cz, 0, 1, s.BottomMm, localOrigin.y);
                        }

                        if (openBelow) Cap(wx, wz, size, b00, b10, b11, b01, s.Material, false);

                        for (int d = 0; d < 4; d++)
                            Sides(grid, cx, cz, d, s, wx, wz, size, localOrigin.y,
                                  solidAbove, openBelow,
                                  y00, y10, y11, y01, b00, b10, b11, b01);
                    }
                }
            }

            mesh.Clear();
            mesh.indexFormat = Verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.SetVertices(Verts);
            mesh.SetNormals(Normals);
            mesh.SetUVs(0, Uvs);

            mesh.subMeshCount = Tris.Length;
            for (int i = 0; i < Tris.Length; i++) mesh.SetTriangles(Tris[i], i, false);

            mesh.RecalculateBounds();
        }

        // ---- smoothing ---------------------------------------------------------

        static readonly int[] Gathered = new int[4];

        /// <summary>
        /// Height of one corner of a cap.
        ///
        /// The four columns meeting at a corner are the SAME four whichever of them asks, so
        /// the answer has to depend only on that set -- otherwise two neighbours compute
        /// different heights for a shared corner and the surface tears open along the join.
        /// That is what an asymmetric rule does, and it is why "within the threshold of MY
        /// height" was wrong: each column measured the window from itself and so admitted a
        /// different set.
        ///
        /// Instead the gathered heights are sorted and split wherever consecutive values are
        /// further apart than the threshold. The answer is the mean of the group the caller's
        /// own height falls in. Two columns on one surface land in one group and agree
        /// exactly; two across a step land in different groups and keep their hard edge.
        /// </summary>
        static float Corner(SpanGrid grid, int cx, int cz, int dx, int dz, int ownTopMm, float localY)
        {
            // A cap at the surface is not this mesher's to shape. Deferring here rather than
            // at the call sites covers both places a top matters -- the cap itself, and the
            // foot of a neighbouring wall that has to land on it -- from one line.
            if (Surface != null && Surface.TopMm(cx, cz) == ownTopMm)
                return Surface.CornerMetres(cx, cz, dx, dz) - localY;

            int n = 0;

            for (int j = 0; j < 2; j++)
            {
                for (int i = 0; i < 2; i++)
                {
                    int nx = cx + dx - 1 + i;
                    int nz = cz + dz - 1 + j;

                    if (nx == cx && nz == cz) { Gathered[n++] = ownTopMm; continue; }
                    if (!grid.InBounds(nx, nz)) continue;

                    int top = NearestOpenTop(grid.Column(nx, nz), ownTopMm);
                    if (top != int.MinValue) Gathered[n++] = top;
                }
            }

            return SpanGrid.ToMetres(GroupMean(n, ownTopMm)) - localY;
        }

        /// <summary>
        /// Mean of the cluster containing `own`, where clusters break at any gap wider than
        /// the threshold. Deterministic in the gathered set alone, which is the property that
        /// makes neighbouring columns agree.
        /// </summary>
        static int GroupMean(int n, int own)
        {
            if (n <= 1) return own;

            // Insertion sort; four elements at most.
            for (int i = 1; i < n; i++)
            {
                int v = Gathered[i];
                int j = i - 1;
                while (j >= 0 && Gathered[j] > v) { Gathered[j + 1] = Gathered[j]; j--; }
                Gathered[j + 1] = v;
            }

            int lo = 0;
            while (lo < n && Gathered[lo] != own) lo++;
            if (lo == n) return own;

            int hi = lo;
            while (lo > 0 && Gathered[lo] - Gathered[lo - 1] <= SmoothThresholdMm) lo--;
            while (hi + 1 < n && Gathered[hi + 1] - Gathered[hi] <= SmoothThresholdMm) hi++;

            long sum = 0;
            for (int i = lo; i <= hi; i++) sum += Gathered[i];

            return (int)(sum / (hi - lo + 1));
        }

        /// <summary>Corner height for a ceiling: the mirror of Corner, over span bottoms.</summary>
        static float CornerBottom(SpanGrid grid, int cx, int cz, int dx, int dz, int ownBottomMm, float localY)
        {
            int n = 0;

            for (int j = 0; j < 2; j++)
            {
                for (int i = 0; i < 2; i++)
                {
                    int nx = cx + dx - 1 + i;
                    int nz = cz + dz - 1 + j;

                    if (nx == cx && nz == cz) { Gathered[n++] = ownBottomMm; continue; }
                    if (!grid.InBounds(nx, nz)) continue;

                    int b = NearestOpenBottom(grid.Column(nx, nz), ownBottomMm);
                    if (b != int.MinValue) Gathered[n++] = b;
                }
            }

            return SpanGrid.ToMetres(GroupMean(n, ownBottomMm)) - localY;
        }

        static int NearestOpenBottom(List<MaterialSpan> column, int targetMm)
        {
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

        /// <summary>
        /// The open cap in this column nearest a target height. A column can hold several --
        /// a tunnel floor and the ground far above it -- and averaging the wrong one would
        /// pull a cave floor toward the sky.
        /// </summary>
        static int NearestOpenTop(List<MaterialSpan> column, int targetMm)
        {
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

        // ---- faces -------------------------------------------------------------

        /// <summary>Horizontal face, one height per corner so a smoothed cap can tilt.</summary>
        static void Cap(float wx, float wz, float size,
                        float y00, float y10, float y11, float y01, byte material, bool up,
                        bool swapDiagonal = false)
        {
            var a = new Vector3(wx, y00, wz);
            var b = new Vector3(wx + size, y10, wz);
            var c = new Vector3(wx + size, y11, wz + size);
            var d = new Vector3(wx, y01, wz + size);

            Vector3 facing = up ? Vector3.up : Vector3.down;

            // Starting the quad at b folds it along b-d instead of a-c. Winding is derived
            // downstream, so the order here only chooses the crease.
            if (swapDiagonal) Quad(b, a, d, c, facing, material);
            else Quad(a, d, c, b, facing, material);
        }

        /// <summary>
        /// A cap that is really the surface: same geometry as Cap, but every corner carries
        /// the surface normal so it shades as part of the hillside rather than as a facet.
        /// </summary>
        static void SurfaceCap(float wx, float wz, float size,
                               float y00, float y10, float y11, float y01,
                               byte material, bool swapDiagonal, int cx, int cz)
        {
            var a = new Vector3(wx, y00, wz);
            var b = new Vector3(wx + size, y10, wz);
            var c = new Vector3(wx + size, y11, wz + size);
            var d = new Vector3(wx, y01, wz + size);

            Vector3 na = Surface.CornerNormal(cx, cz, 0, 0);
            Vector3 nb = Surface.CornerNormal(cx, cz, 1, 0);
            Vector3 nc = Surface.CornerNormal(cx, cz, 1, 1);
            Vector3 nd = Surface.CornerNormal(cx, cz, 0, 1);

            if (swapDiagonal)
            {
                SmoothTriangle(b, a, d, nb, na, nd, material);
                SmoothTriangle(b, d, c, nb, nd, nc, material);
            }
            else
            {
                SmoothTriangle(a, d, c, na, nd, nc, material);
                SmoothTriangle(a, c, b, na, nc, nb, material);
            }
        }

        /// <summary>
        /// Winding is decided from the normals rather than derived from the geometry, because
        /// on a surface cap the normals are the authority on which way is up.
        /// </summary>
        static void SmoothTriangle(Vector3 p0, Vector3 p1, Vector3 p2,
                                   Vector3 n0, Vector3 n1, Vector3 n2, byte material)
        {
            if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), n0) < 0f)
            {
                Vector3 sp = p1; p1 = p2; p2 = sp;
                Vector3 sn = n1; n1 = n2; n2 = sn;
            }

            int i = Verts.Count;

            Verts.Add(p0); Verts.Add(p1); Verts.Add(p2);
            Normals.Add(n0); Normals.Add(n1); Normals.Add(n2);

            Uvs.Add(new Vector2(p0.x + UvOrigin.x, p0.z + UvOrigin.z));
            Uvs.Add(new Vector2(p1.x + UvOrigin.x, p1.z + UvOrigin.z));
            Uvs.Add(new Vector2(p2.x + UvOrigin.x, p2.z + UvOrigin.z));

            List<int> tris = Tris[material < Tris.Length ? material : 0];
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
        }

        /// <summary>
        /// Vertical faces toward one neighbour: this span's interval minus everything the
        /// neighbour has solid there. Where the neighbour is as tall, nothing is exposed and
        /// nothing is drawn -- which is what keeps flat ground from growing internal walls.
        ///
        /// The topmost exposed interval takes its top edge from the smoothed corners, so a
        /// tilted cap and the wall under it meet without a gap.
        /// </summary>
        static void Sides(SpanGrid grid, int cx, int cz, int direction,
                          MaterialSpan s, float wx, float wz, float size, float localY,
                          bool solidAbove, bool openBelow,
                          float y00, float y10, float y11, float y01,
                          float b00, float b10, float b11, float b01)
        {
            int nx = cx + StepX[direction];
            int nz = cz + StepZ[direction];

            Exposed.Clear();

            if (!grid.InBounds(nx, nz))
            {
                // Grid edge: close the world off rather than leaving a hole into it.
                Exposed.Add(new Vector2Int(s.BottomMm, s.TopMm));
            }
            else
            {
                Subtract(grid.Column(nx, nz), s.BottomMm, s.TopMm, Exposed);
            }

            for (int i = 0; i < Exposed.Count; i++)
            {
                float bottom = SpanGrid.ToMetres(Exposed[i].x) - localY;
                float top = SpanGrid.ToMetres(Exposed[i].y) - localY;
                if (top <= bottom) continue;

                float x0 = wx, x1 = wx + size, z0 = wz, z1 = wz + size;

                Vector3 outward;
                Vector2 p0, p1;
                float topA = top, topB = top;
                float botA = bottom, botB = bottom;

                // A wall edge must land exactly on whichever cap bounds it. There are four
                // ways that can happen and every one of them tears open if it is missed:
                // this span's own top, its own bottom (a ceiling), and the same two against
                // the neighbour's caps. The first pass only handled two of the four.
                bool crest = !solidAbove && Exposed[i].y == s.TopMm;
                bool trough = openBelow && Exposed[i].x == s.BottomMm;

                // The neighbour's corners on this same shared edge.
                int naX, naZ, nbX, nbZ;

                switch (direction)
                {
                    case 0:
                        outward = Vector3.right;
                        p0 = new Vector2(x1, z0); p1 = new Vector2(x1, z1);
                        if (crest) { topA = y10; topB = y11; }
                        if (trough) { botA = b10; botB = b11; }
                        naX = 0; naZ = 0; nbX = 0; nbZ = 1;
                        break;
                    case 1:
                        outward = Vector3.left;
                        p0 = new Vector2(x0, z0); p1 = new Vector2(x0, z1);
                        if (crest) { topA = y00; topB = y01; }
                        if (trough) { botA = b00; botB = b01; }
                        naX = 1; naZ = 0; nbX = 1; nbZ = 1;
                        break;
                    case 2:
                        outward = Vector3.forward;
                        p0 = new Vector2(x0, z1); p1 = new Vector2(x1, z1);
                        if (crest) { topA = y01; topB = y11; }
                        if (trough) { botA = b01; botB = b11; }
                        naX = 0; naZ = 0; nbX = 1; nbZ = 0;
                        break;
                    default:
                        outward = Vector3.back;
                        p0 = new Vector2(x0, z0); p1 = new Vector2(x1, z0);
                        if (crest) { topA = y00; topB = y10; }
                        if (trough) { botA = b00; botB = b10; }
                        naX = 0; naZ = 1; nbX = 1; nbZ = 1;
                        break;
                }

                // Own caps win: this face belongs to this span, and its own tilted cap is the
                // surface it has to meet. Otherwise follow the neighbour's cap, because
                // stopping at a raw span boundary leaves a slot beside a cap that has already
                // tilted away from it -- the see-through along a ramp edge, and the same
                // thing again along a tunnel ceiling.
                if (Smooth && grid.InBounds(nx, nz))
                {
                    if (!trough)
                    {
                        int nTop = Exposed[i].x;
                        if (grid.OpenTopNear(nx, nz, nTop) == nTop)
                        {
                            botA = Corner(grid, nx, nz, naX, naZ, nTop, localY);
                            botB = Corner(grid, nx, nz, nbX, nbZ, nTop, localY);
                        }
                    }

                    if (!crest)
                    {
                        int nBottom = Exposed[i].y;
                        if (grid.OpenBottomNear(nx, nz, nBottom) == nBottom)
                        {
                            topA = CornerBottom(grid, nx, nz, naX, naZ, nBottom, localY);
                            topB = CornerBottom(grid, nx, nz, nbX, nbZ, nBottom, localY);
                        }
                    }
                }

                var a = new Vector3(p0.x, topA, p0.y);
                var b = new Vector3(p1.x, topB, p1.y);
                var c = new Vector3(p1.x, botB, p1.y);
                var d = new Vector3(p0.x, botA, p0.y);

                Quad(a, b, c, d, outward, s.Material);
            }
        }

        /// <summary>[bottom,top) minus every solid interval the neighbouring column holds.</summary>
        static void Subtract(List<MaterialSpan> neighbour, int bottom, int top, List<Vector2Int> results)
        {
            int cursor = bottom;

            for (int i = 0; i < neighbour.Count && cursor < top; i++)
            {
                MaterialSpan n = neighbour[i];

                if (n.TopMm <= cursor) continue;
                if (n.BottomMm >= top) break;

                if (n.BottomMm > cursor) results.Add(new Vector2Int(cursor, n.BottomMm));
                if (n.TopMm > cursor) cursor = n.TopMm;
            }

            if (cursor < top) results.Add(new Vector2Int(cursor, top));
        }

        /// <summary>
        /// Winding derived from the intended facing rather than reasoned out per direction.
        /// Getting this wrong per-face is the classic way to end up with a mesh that looks
        /// right from one side and is invisible from the other.
        /// </summary>
        static void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 outward, byte material)
        {
            if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) < 0f)
            {
                Vector3 swap = p1; p1 = p3; p3 = swap;
            }

            Triangle(p0, p1, p2, outward, material);
            Triangle(p0, p2, p3, outward, material);
        }

        /// <summary>
        /// Normals come from the geometry, not the nominal facing, so a tilted cap shades as
        /// the slope it is rather than as the flat quad it used to be.
        /// </summary>
        static void Triangle(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 fallback, byte material)
        {
            Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0);

            normal = normal.sqrMagnitude < 1e-12f ? fallback : normal.normalized;
            if (Vector3.Dot(normal, fallback) < 0f) normal = -normal;

            int i = Verts.Count;

            Verts.Add(p0); Verts.Add(p1); Verts.Add(p2);
            Normals.Add(normal); Normals.Add(normal); Normals.Add(normal);

            Uvs.Add(new Vector2(p0.x + UvOrigin.x, p0.z + UvOrigin.z));
            Uvs.Add(new Vector2(p1.x + UvOrigin.x, p1.z + UvOrigin.z));
            Uvs.Add(new Vector2(p2.x + UvOrigin.x, p2.z + UvOrigin.z));

            List<int> tris = Tris[material < Tris.Length ? material : 0];
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
        }
    }
}
