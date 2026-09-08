using System.Collections.Generic;
using UnityEngine;
using Terraform.Core;

namespace Terraform.View
{
    /// <summary>
    /// Wireframe of the terrain cells: lines through the vertices, squares between them.
    ///
    /// Sculpt raises a corner of this grid; flatten levels whole squares of it. Both read
    /// off the same picture, which is what lets you raise a ridge and then centre a pad
    /// on it. Lines are subdivided and sampled so they follow terraced ground.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class GridOverlay : MonoBehaviour
    {
        const float Lift = 0.015f;    // nudge above the surface to avoid z-fighting
        const float Stride = 0.5f;    // sample spacing along each line, in cells

        ChunkView _chunk;
        Mesh _mesh;

        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<int> _indices = new List<int>();

        public void Initialise(ChunkView chunk)
        {
            if (_chunk != null) _chunk.MeshChanged -= Rebuild;   // safe to re-initialise
            _chunk = chunk;

            _mesh = new Mesh();
            _mesh.name = "GridOverlay";
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            transform.position = chunk.Grid.Origin;
            chunk.MeshChanged += Rebuild;
            Rebuild();
        }

        void OnDestroy()
        {
            if (_chunk != null) _chunk.MeshChanged -= Rebuild;
        }

        // At 64x64 the outline is tens of thousands of line vertices, rebuilt on every
        // edit. Skip it while hidden so Tab actually buys back the cost, and catch up
        // when it comes back.
        bool _stale;

        void OnEnable()
        {
            if (!_stale) return;
            _stale = false;
            Rebuild();
        }

        void Rebuild()
        {
            if (_chunk == null) return;
            if (!isActiveAndEnabled) { _stale = true; return; }

            HeightGrid g = _chunk.Grid;

            _verts.Clear();
            _indices.Clear();

            // Lines run through the vertices, so the squares are the quads between them.
            // One grid for every tool: sculpt moves a corner, flatten levels the cells.
            // Two overlapping grids offset by half a cell is what makes the tools feel
            // unrelated, and it makes a ridge impossible to centre a pad on.
            for (int i = 0; i <= g.CellsX; i++) Line(g, i, 0f, i, g.CellsZ);
            for (int j = 0; j <= g.CellsZ; j++) Line(g, 0f, j, g.CellsX, j);

            _mesh.Clear();
            _mesh.indexFormat = _verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            _mesh.SetVertices(_verts);
            _mesh.SetIndices(_indices, MeshTopology.Lines, 0);
            _mesh.RecalculateBounds();
        }

        void Line(HeightGrid g, float ax, float az, float bx, float bz)
        {
            float length = Mathf.Max(Mathf.Abs(bx - ax), Mathf.Abs(bz - az));
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / Stride));

            Vector3 previous = Point(g, ax, az);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector3 next = Point(g, Mathf.Lerp(ax, bx, t), Mathf.Lerp(az, bz, t));

                _indices.Add(_verts.Count); _verts.Add(previous);
                _indices.Add(_verts.Count); _verts.Add(next);

                previous = next;
            }
        }

        static Vector3 Point(HeightGrid g, float gx, float gz)
        {
            return new Vector3(gx * g.CellSize, g.SampleMetres(gx, gz) + Lift, gz * g.CellSize);
        }
    }
}
