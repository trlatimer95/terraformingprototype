using System.Collections.Generic;
using UnityEngine;
using Terraform.Core;

namespace Terraform.View
{
    /// <summary>
    /// Heightfield to triangle mesh.
    ///
    /// Six vertices per cell (two fully independent triangles) so every triangle gets
    /// its own flat normal. Shared vertices would be six times smaller but would smooth
    /// the terracing away, and crisp terraces are the entire point of a grid-quantised
    /// system. Memory is not the constraint at this scale; legibility is.
    /// </summary>
    public static class ChunkMeshBuilder
    {
        static readonly List<Vector3> Verts = new List<Vector3>();
        static readonly List<Vector3> Normals = new List<Vector3>();
        static readonly List<Vector2> Uvs = new List<Vector2>();
        static readonly List<int> Tris = new List<int>();

        public static void Build(HeightGrid g, Mesh mesh)
        {
            Verts.Clear();
            Normals.Clear();
            Uvs.Clear();
            Tris.Clear();

            for (int cz = 0; cz < g.CellsZ; cz++)
            {
                for (int cx = 0; cx < g.CellsX; cx++)
                {
                    // Corners in chunk-local space; the transform sits at grid.Origin.
                    Vector3 a = Local(g, cx, cz);          // -x -z
                    Vector3 b = Local(g, cx + 1, cz);      // +x -z
                    Vector3 c = Local(g, cx + 1, cz + 1);  // +x +z
                    Vector3 d = Local(g, cx, cz + 1);      // -x +z

                    // Split along the flatter diagonal. Picking a fixed diagonal puts a
                    // visible crease across terrace edges and makes level platforms look
                    // warped where the quad is non-planar.
                    bool acDiagonal = Mathf.Abs(a.y - c.y) <= Mathf.Abs(b.y - d.y);

                    if (acDiagonal)
                    {
                        AddTriangle(a, d, c);
                        AddTriangle(a, c, b);
                    }
                    else
                    {
                        AddTriangle(a, d, b);
                        AddTriangle(b, d, c);
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
            mesh.SetTriangles(Tris, 0);
            mesh.RecalculateBounds();
        }

        static Vector3 Local(HeightGrid g, int vx, int vz)
        {
            return new Vector3(vx * g.CellSize, g.GetMetres(vx, vz), vz * g.CellSize);
        }

        static void AddTriangle(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            // Clockwise winding viewed from above yields an upward normal in Unity.
            Vector3 n = Vector3.Cross(p1 - p0, p2 - p0).normalized;

            int i = Verts.Count;
            Verts.Add(p0); Verts.Add(p1); Verts.Add(p2);
            Normals.Add(n); Normals.Add(n); Normals.Add(n);

            // World-planar UVs so any tiling texture stays continuous across cells.
            Uvs.Add(new Vector2(p0.x, p0.z));
            Uvs.Add(new Vector2(p1.x, p1.z));
            Uvs.Add(new Vector2(p2.x, p2.z));

            Tris.Add(i); Tris.Add(i + 1); Tris.Add(i + 2);
        }
    }
}
