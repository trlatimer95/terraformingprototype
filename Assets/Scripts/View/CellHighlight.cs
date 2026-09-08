using System.Collections.Generic;
using UnityEngine;
using Terraform.Core;

namespace Terraform.View
{
    /// <summary>
    /// Line overlay for showing what an action is about to affect: the brush footprint,
    /// and the ramp axis while one is being placed.
    ///
    /// Outlines follow the terrain vertex by vertex rather than drawing a flat quad, so
    /// on sloped or terraced ground the marquee sits on the surface instead of cutting
    /// through it.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class CellHighlight : MonoBehaviour
    {
        const float Lift = 0.03f;   // above both the surface and the grid overlay

        HeightGrid _grid;
        Mesh _mesh;
        MeshRenderer _renderer;

        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<int> _indices = new List<int>();

        public void Initialise(HeightGrid grid)
        {
            _grid = grid;

            _mesh = new Mesh();
            _mesh.name = "CellHighlight";
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            _renderer = GetComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            transform.position = grid.Origin;
            Begin();
            End();
        }

        public void Begin()
        {
            _verts.Clear();
            _indices.Clear();
        }


        /// <summary>Straight grid line, subdivided so it follows terraced ground.</summary>
        void DualLine(float ax, float az, float bx, float bz)
        {
            const float stride = 0.5f;

            float length = Mathf.Max(Mathf.Abs(bx - ax), Mathf.Abs(bz - az));
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / stride));

            Vector3 previous = DualPoint(ax, az);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector3 next = DualPoint(Mathf.Lerp(ax, bx, t), Mathf.Lerp(az, bz, t));
                Push(previous);
                Push(next);
                previous = next;
            }
        }


        Vector3 DualPoint(float gx, float gz)
        {
            float cx = Mathf.Clamp(gx, 0f, _grid.CellsX);
            float cz = Mathf.Clamp(gz, 0f, _grid.CellsZ);
            return new Vector3(cx * _grid.CellSize, _grid.SampleMetres(cx, cz) + Lift, cz * _grid.CellSize);
        }

        /// <summary>
        /// Outline the PRIMAL cells spanned by a vertex rect: lines through the vertices
        /// themselves, so the squares are the quads between them. This is what flatten
        /// addresses.
        /// </summary>
        public void AddVertexGrid(int vx0, int vz0, int w, int h)
        {
            if (_grid == null || w < 2 || h < 2) return;

            for (int i = 0; i < w; i++) DualLine(vx0 + i, vz0, vx0 + i, vz0 + h - 1);
            for (int j = 0; j < h; j++) DualLine(vx0, vz0 + j, vx0 + w - 1, vz0 + j);
        }

        /// <summary>Straight segment between two world positions, e.g. a ramp axis.</summary>
        public void AddSegment(Vector3 worldA, Vector3 worldB)
        {
            Push(worldA - transform.position + Vector3.up * Lift);
            Push(worldB - transform.position + Vector3.up * Lift);
        }

        public void End()
        {
            if (_mesh == null) return;

            _mesh.Clear();

            if (_verts.Count == 0)
            {
                if (_renderer != null) _renderer.enabled = false;
                return;
            }

            _mesh.SetVertices(_verts);
            _mesh.SetIndices(_indices, MeshTopology.Lines, 0);
            _mesh.RecalculateBounds();
            _renderer.enabled = true;
        }



        void Push(Vector3 v)
        {
            _indices.Add(_verts.Count);
            _verts.Add(v);
        }
    }
}
