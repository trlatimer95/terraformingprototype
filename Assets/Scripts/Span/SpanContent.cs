using UnityEngine;
using Terraform.Core;

namespace Terraform.Span
{
    /// <summary>
    /// Fills a span grid with everything the appearance gate needs to judge: a hill, layered
    /// ground, two building pads at different heights, and a tunnel with a branch.
    ///
    /// The surface comes from DemoTerrain -- the same height function the existing prototype
    /// uses -- so this is literally the same ground rendered a different way. That is the
    /// only honest way to compare the representations rather than the terrain.
    /// </summary>
    public static class SpanContent
    {
        /// <summary>DemoTerrain is authored against a 64-cell map; keep that shape.</summary>
        const int SourceCells = 64;

        public const float TopsoilDepth = 0.3f;
        public const float SubsoilDepth = 1.6f;

        /// <summary>
        /// Height a tunnel floor steps by when quantised. One metre matches the interaction
        /// scale, and is the case worth seeing fail: a metre is more than a character can
        /// step up, so a descending corridor becomes one-way.
        /// </summary>
        public const float FloorStep = 1f;

        public static void Fill(SpanGrid grid, float areaMetres, float floorMetres,
                                float baseMetres, bool quantiseFloor)
        {
            BuildGround(grid, areaMetres, floorMetres, baseMetres);

            // Two pads at different heights, sharing an edge. The step between them is the
            // thing to look at: at span resolution it is a real staircase, not a slope.
            Pad(grid, new Rect(-11f, 2f, 6f, 6f), baseMetres + 0.4f);
            Pad(grid, new Rect(-5f, 2f, 6f, 6f), baseMetres + 1.4f);

            // Axis-aligned, because a mine that may only advance along the grid is a rule
            // worth seeing before deciding whether to impose it. The adit runs north into the
            // mountain and descends 2 m on the way; the drift turns ninety degrees east and
            // breaks out on the far flank.
            //
            // The turn is the interesting part. On a diagonal the walls step in both axes at
            // once; on an axis-aligned pair they are flat until the corner and then flat
            // again, which is a very different thing to walk through.
            Corridor(grid, new Vector3(7.7f, baseMetres + 0.2f, -14.5f), new Vector3(7.7f, baseMetres - 1.8f, -8f),
                     2f, 2.25f, quantiseFloor);

            Corridor(grid, new Vector3(7.7f, baseMetres - 1.4f, -10f), new Vector3(13.5f, baseMetres - 1.4f, -10f),
                     2f, 2.25f, quantiseFloor);
        }

        // ---- ground ------------------------------------------------------------

        static void BuildGround(SpanGrid grid, float areaMetres, float floorMetres, float baseMetres)
        {
            int floorMm = SpanGrid.ToMm(floorMetres);

            for (int cz = 0; cz < grid.ColumnsZ; cz++)
            {
                for (int cx = 0; cx < grid.ColumnsX; cx++)
                {
                    // Column centre, mapped onto DemoTerrain's own coordinate space.
                    float u = (cx + 0.5f) * grid.ColumnSize / areaMetres;
                    float v = (cz + 0.5f) * grid.ColumnSize / areaMetres;

                    float surface = DemoTerrain.SampleMetres(
                        u * SourceCells, v * SourceCells, SourceCells, SourceCells, baseMetres);

                    int surfaceMm = SpanGrid.ToMm(surface);
                    int topsoilMm = surfaceMm - SpanGrid.ToMm(TopsoilDepth);
                    int subsoilMm = surfaceMm - SpanGrid.ToMm(SubsoilDepth);

                    // Bottom up, so Append can coalesce touching same-material runs.
                    byte deep = OreHere(grid, cx, cz) ? SpanMaterials.Ore : SpanMaterials.Rock;

                    grid.Append(cx, cz, floorMm, Mathf.Max(floorMm, subsoilMm), deep);
                    grid.Append(cx, cz, Mathf.Max(floorMm, subsoilMm), Mathf.Max(floorMm, topsoilMm), SpanMaterials.Subsoil);
                    grid.Append(cx, cz, Mathf.Max(floorMm, topsoilMm), Mathf.Max(floorMm, surfaceMm), SpanMaterials.Topsoil);
                }
            }
        }

        /// <summary>
        /// One blocky ore body under the south-east. Deliberately a rectangle: noisy veins
        /// are a generator question, and this test is about whether an exposed seam reads.
        /// </summary>
        static bool OreHere(SpanGrid grid, int cx, int cz)
        {
            float x = grid.WorldX(cx);
            float z = grid.WorldZ(cz);
            return x > 4f && x < 14f && z > -13f && z < -4f;
        }

        // ---- edits -------------------------------------------------------------

        /// <summary>Level a footprint by removing everything above a height. No fill.</summary>
        static void Pad(SpanGrid grid, Rect footprint, float heightMetres)
        {
            int cutMm = SpanGrid.ToMm(heightMetres);

            for (int cz = 0; cz < grid.ColumnsZ; cz++)
                for (int cx = 0; cx < grid.ColumnsX; cx++)
                {
                    if (!footprint.Contains(new Vector2(grid.WorldX(cx), grid.WorldZ(cz)))) continue;
                    grid.Subtract(cx, cz, cutMm, int.MaxValue / 2);
                }
        }

        /// <summary>
        /// Carve a corridor of rectangular cross-section between two points.
        ///
        /// The walls quantise to the column grid and look cubed. The floor does not: its
        /// height is millimetres, so a descending corridor gets a genuine ramp rather than a
        /// staircase. That combination is the whole argument for spans over cubes -- blocky
        /// where blocky is fine, continuous where it decides whether you can walk back out.
        ///
        /// With quantiseFloor the floor snaps to whole steps instead, which is the cubed
        /// behaviour and shows what the ramp is buying.
        /// </summary>
        static void Corridor(SpanGrid grid, Vector3 from, Vector3 to,
                             float width, float height, bool quantiseFloor)
        {
            var flatFrom = new Vector2(from.x, from.z);
            var flatTo = new Vector2(to.x, to.z);

            Vector2 axis = flatTo - flatFrom;
            float length = axis.magnitude;
            if (length < 0.001f) return;

            Vector2 direction = axis / length;
            float half = width * 0.5f;

            // Bounding box of the swept rectangle, plus a column of slack.
            float pad = half + grid.ColumnSize;
            int x0 = Column(grid, Mathf.Min(from.x, to.x) - pad, grid.Origin.x);
            int x1 = Column(grid, Mathf.Max(from.x, to.x) + pad, grid.Origin.x);
            int z0 = Column(grid, Mathf.Min(from.z, to.z) - pad, grid.Origin.z);
            int z1 = Column(grid, Mathf.Max(from.z, to.z) + pad, grid.Origin.z);

            for (int cz = z0; cz <= z1; cz++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!grid.InBounds(cx, cz)) continue;

                    var centre = new Vector2(grid.WorldX(cx) + grid.ColumnSize * 0.5f,
                                             grid.WorldZ(cz) + grid.ColumnSize * 0.5f);

                    Vector2 offset = centre - flatFrom;

                    float along = Mathf.Clamp(Vector2.Dot(offset, direction), 0f, length);
                    Vector2 nearest = flatFrom + direction * along;

                    if (Vector2.Distance(centre, nearest) > half) continue;

                    float floor = Mathf.Lerp(from.y, to.y, along / length);
                    float ceiling = floor + height;

                    if (quantiseFloor)
                    {
                        // The floor snaps DOWN and the ceiling snaps UP. Snapping both the
                        // same way is the obvious thing and it does not work: the ceiling
                        // drops with the floor, so at a one-metre step the surviving opening
                        // is only (height - step) tall. You can see through to the next
                        // level and not fit through it. Rounding them apart keeps at least
                        // a full envelope of headroom across every transition, which is the
                        // same thing as removing the block above the lower step.
                        floor = Mathf.Floor(floor / FloorStep) * FloorStep;
                        ceiling = Mathf.Ceil(ceiling / FloorStep) * FloorStep;
                    }

                    grid.Subtract(cx, cz, SpanGrid.ToMm(floor), SpanGrid.ToMm(ceiling));
                }
            }
        }

        static int Column(SpanGrid grid, float world, float origin)
        {
            return Mathf.FloorToInt((world - origin) / grid.ColumnSize);
        }
    }
}
