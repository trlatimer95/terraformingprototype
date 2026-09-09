using System.Collections.Generic;
using UnityEngine;
using Terraform.Core;

namespace Terraform.View
{
    /// <summary>
    /// Per-cell terrain to mesh.
    ///
    /// Each cell fans eight triangles from its centre out to eight boundary points: four
    /// corners and four edge midpoints. The midpoints are what let a ridge run centre to
    /// centre between two raised cells -- a crest crosses the shared edge at its middle,
    /// and with corners alone the only way to lift it is to level the whole edge.
    ///
    /// Where two cells disagree along a shared edge a vertical face closes the seam. Only
    /// the higher side emits it, so each seam gets exactly one, and it is two-sided --
    /// a seam is a slit inside the surface, not the boundary of a solid, so culling one
    /// winding would leave a hole you can see through from the other side.
    /// </summary>
    public static class CellMeshBuilder
    {
        /// <summary>
        /// Cells handed over to another representation. Null in the plain demo, set in a
        /// hybrid world for cells sitting over a tunnel.
        ///
        /// A ceded cell drops BOTH its fan and its seam faces. The seam face rule is "only
        /// the higher side emits", and if the higher side has been ceded then whatever owns
        /// it emits that wall instead -- so leaving the calls in would draw it twice and
        /// z-fight along every ledge at the boundary.
        /// </summary>
        public static System.Func<int, int, bool> Ceded;

        static readonly List<Vector3> Verts = new List<Vector3>();
        static readonly List<Vector3> Normals = new List<Vector3>();
        static readonly List<Vector2> Uvs = new List<Vector2>();
        static readonly List<int> Tris = new List<int>();

        static readonly Vector3[] Ring = new Vector3[8];
        static readonly Vector3[] RingNormal = new Vector3[8];

        public static void Build(CellGrid g, Mesh mesh)
        {
            Verts.Clear();
            Normals.Clear();
            Uvs.Clear();
            Tris.Clear();

            for (int cz = 0; cz < g.CellsZ; cz++)
            {
                for (int cx = 0; cx < g.CellsX; cx++)
                {
                    if (Ceded != null && Ceded(cx, cz)) continue;

                    Vector3 centre = Local(g, cx + 0.5f, g.GetMetres(cx, cz), cz + 0.5f);
                    FillRing(g, cx, cz);

                    Vector3 nc = CellSurface.NormalForCell(g, cx, cz, -1);
                    for (int i = 0; i < 8; i++) RingNormal[i] = CellSurface.NormalForCell(g, cx, cz, i);

                    // Smooth-shaded, and the reason is worth keeping: a flat normal per
                    // triangle made the terrain read as blocky no matter how fine the mesh
                    // got, because every facet became a step in the lighting and the fan
                    // showed up as a diamond across every hill. The vertex normals come from
                    // CellSurface so the span mesher can read the same ones -- shading the
                    // seam differently would give back the whole point of matching it.
                    for (int i = 0; i < 8; i++)
                    {
                        int j = (i + 1) & 7;
                        AddSmoothTriangle(centre, Ring[j], Ring[i], nc, RingNormal[j], RingNormal[i]);
                    }

                    AddFace(g, cx, cz, cx + 1, cz, Vector3.right);
                    AddFace(g, cx, cz, cx, cz + 1, Vector3.forward);
                    AddFace(g, cx, cz, cx - 1, cz, Vector3.left);
                    AddFace(g, cx, cz, cx, cz - 1, Vector3.back);
                }
            }

            mesh.Clear();
            mesh.indexFormat = Verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(Verts);
            mesh.SetNormals(Normals);
            mesh.SetUVs(0, Uvs);
            mesh.SetTriangles(Tris, 0);
            mesh.RecalculateBounds();
        }

        /// <summary>Boundary ring, counter-clockwise from above so the fan faces up.</summary>
        static void FillRing(CellGrid g, int cx, int cz)
        {
            Ring[0] = Local(g, cx, g.CornerMetresForCell(cx, cz, cx, cz), cz);
            Ring[1] = Local(g, cx + 0.5f, g.EdgeMetresForCell(cx, cz, cx, cz - 1), cz);
            Ring[2] = Local(g, cx + 1, g.CornerMetresForCell(cx, cz, cx + 1, cz), cz);
            Ring[3] = Local(g, cx + 1, g.EdgeMetresForCell(cx, cz, cx + 1, cz), cz + 0.5f);
            Ring[4] = Local(g, cx + 1, g.CornerMetresForCell(cx, cz, cx + 1, cz + 1), cz + 1);
            Ring[5] = Local(g, cx + 0.5f, g.EdgeMetresForCell(cx, cz, cx, cz + 1), cz + 1);
            Ring[6] = Local(g, cx, g.CornerMetresForCell(cx, cz, cx, cz + 1), cz + 1);
            Ring[7] = Local(g, cx, g.EdgeMetresForCell(cx, cz, cx - 1, cz), cz + 0.5f);
        }

        /// <summary>
        /// Close the seam along a shared edge when this cell stands higher. Sampled at
        /// three points -- corner, midpoint, corner -- so a face can taper along its
        /// length instead of being a flat slab.
        /// </summary>
        static void AddFace(CellGrid g, int cx, int cz, int nx, int nz, Vector3 outward)
        {
            if (!g.InBounds(nx, nz)) return;

            // Grid coordinates of the shared edge's two corners.
            float ax, az, bx, bz;

            if (nx > cx) { ax = cx + 1; az = cz; bx = cx + 1; bz = cz + 1; }
            else if (nx < cx) { ax = cx; az = cz; bx = cx; bz = cz + 1; }
            else if (nz > cz) { ax = cx; az = cz + 1; bx = cx + 1; bz = cz + 1; }
            else { ax = cx; az = cz; bx = cx + 1; bz = cz; }

            float mx = (ax + bx) * 0.5f;
            float mz = (az + bz) * 0.5f;

            float topA = g.CornerRawForCell(cx, cz, (int)ax, (int)az);
            float topB = g.CornerRawForCell(cx, cz, (int)bx, (int)bz);
            float topM = g.EdgeRawForCell(cx, cz, nx, nz);

            float botA = g.CornerRawForCell(nx, nz, (int)ax, (int)az);
            float botB = g.CornerRawForCell(nx, nz, (int)bx, (int)bz);
            float botM = g.EdgeRawForCell(nx, nz, cx, cz);

            if (topA <= botA && topB <= botB && topM <= botM) return;   // not the higher side

            Quad(g, ax, az, topA, botA, mx, mz, topM, botM, outward);
            Quad(g, mx, mz, topM, botM, bx, bz, topB, botB, outward);
        }

        static void Quad(CellGrid g,
                         float x0, float z0, float top0, float bot0,
                         float x1, float z1, float top1, float bot1,
                         Vector3 outward)
        {
            if (Mathf.Approximately(top0, bot0) && Mathf.Approximately(top1, bot1)) return;

            Vector3 t0 = Local(g, x0, top0 * CellGrid.MetresPerUnit, z0);
            Vector3 t1 = Local(g, x1, top1 * CellGrid.MetresPerUnit, z1);
            Vector3 b1 = Local(g, x1, bot1 * CellGrid.MetresPerUnit, z1);
            Vector3 b0 = Local(g, x0, bot0 * CellGrid.MetresPerUnit, z0);

            // Derive the winding rather than reasoning it out per edge direction.
            if (Vector3.Dot(Vector3.Cross(t1 - t0, b1 - t0), outward) < 0f)
            {
                Vector3 st = t0; t0 = t1; t1 = st;
                Vector3 sb = b0; b0 = b1; b1 = sb;
            }

            AddTriangle(t0, t1, b1);
            AddTriangle(t0, b1, b0);

            // Both windings. A face bounding a solid -- the wall around a raised pad --
            // could be single-sided, but a seam where two cells part company is a slit
            // inside the surface with both sides exposed. Backface culling turns the
            // unseen winding into a hole you can look straight through.
            AddTriangle(t0, b1, t1);
            AddTriangle(t0, b0, b1);
        }

        static Vector3 Local(CellGrid g, float gx, float metres, float gz)
        {
            return new Vector3(gx * g.CellSize, metres, gz * g.CellSize);
        }

        /// <summary>
        /// A fan triangle, each corner carrying the surface normal rather than the face one.
        /// Seam faces keep AddTriangle: a wall between two cells at different heights is a
        /// genuine hard edge and must not be smoothed into the ground it stands on.
        /// </summary>
        static void AddSmoothTriangle(Vector3 p0, Vector3 p1, Vector3 p2,
                                      Vector3 n0, Vector3 n1, Vector3 n2)
        {
            int i = Verts.Count;
            Verts.Add(p0); Verts.Add(p1); Verts.Add(p2);
            Normals.Add(n0); Normals.Add(n1); Normals.Add(n2);

            Uvs.Add(new Vector2(p0.x, p0.z));
            Uvs.Add(new Vector2(p1.x, p1.z));
            Uvs.Add(new Vector2(p2.x, p2.z));

            Tris.Add(i); Tris.Add(i + 1); Tris.Add(i + 2);
        }

        static void AddTriangle(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            Vector3 n = Vector3.Cross(p1 - p0, p2 - p0).normalized;

            int i = Verts.Count;
            Verts.Add(p0); Verts.Add(p1); Verts.Add(p2);
            Normals.Add(n); Normals.Add(n); Normals.Add(n);

            Uvs.Add(new Vector2(p0.x, p0.z));
            Uvs.Add(new Vector2(p1.x, p1.z));
            Uvs.Add(new Vector2(p2.x, p2.z));

            Tris.Add(i); Tris.Add(i + 1); Tris.Add(i + 2);
        }
    }
}
