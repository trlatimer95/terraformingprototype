using System.Collections.Generic;
using UnityEngine;
using Terraform.Core;

namespace Terraform.View
{
    /// <summary>
    /// Scene-side view of one CellGrid: mesh, collider, cell outlines, and the marquee.
    /// All authority stays in the grid and the command log.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    [RequireComponent(typeof(MeshCollider))]
    public sealed class CellChunkView : MonoBehaviour
    {
        public CellGrid Grid { get; private set; }
        public CellCommandLog Log { get; private set; }

        public float LastMeshMs { get; private set; }
        public float LastColliderMs { get; private set; }
        public int TriangleCount { get { return _mesh == null ? 0 : _mesh.vertexCount / 3; } }

        public event System.Action MeshChanged;

        Mesh _mesh;
        MeshCollider _collider;
        bool _dirty;

        public void Initialise(CellGrid grid)
        {
            Grid = grid;
            Log = new CellCommandLog();

            _collider = GetComponent<MeshCollider>();

            _mesh = new Mesh();
            _mesh.name = "CellChunk";
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            transform.position = grid.Origin;
            Rebuild();
        }

        public bool Execute(ICellCommand cmd)
        {
            if (Grid == null || !Log.Execute(cmd, Grid)) return false;
            _dirty = true;
            return true;
        }

        public bool Undo() { if (Grid != null && Log.Undo(Grid)) { _dirty = true; return true; } return false; }
        public bool Redo() { if (Grid != null && Log.Redo(Grid)) { _dirty = true; return true; } return false; }

        void LateUpdate()
        {
            if (!_dirty) return;
            _dirty = false;
            Rebuild();
        }

        public void Rebuild()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CellMeshBuilder.Build(Grid, _mesh);
            LastMeshMs = (float)sw.Elapsed.TotalMilliseconds;

            sw.Reset();
            sw.Start();
            _collider.sharedMesh = null;
            _collider.sharedMesh = _mesh;
            LastColliderMs = (float)sw.Elapsed.TotalMilliseconds;

            if (MeshChanged != null) MeshChanged();
        }
    }

    /// <summary>
    /// Outlines every cell along its own corner heights. Because a flattened cell keeps
    /// its own corners, the outline steps at a wall instead of running through it -- the
    /// grid you see is the ground you have.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class CellGridOverlay : MonoBehaviour
    {
        const float Lift = 0.015f;

        CellChunkView _chunk;
        Mesh _mesh;

        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<int> _indices = new List<int>();

        public void Initialise(CellChunkView chunk)
        {
            if (_chunk != null) _chunk.MeshChanged -= Rebuild;
            _chunk = chunk;

            _mesh = new Mesh();
            _mesh.name = "CellGridOverlay";
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            transform.position = chunk.Grid.Origin;
            chunk.MeshChanged += Rebuild;
            Rebuild();
        }

        void OnDestroy() { if (_chunk != null) _chunk.MeshChanged -= Rebuild; }

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

            CellGrid g = _chunk.Grid;

            _verts.Clear();
            _indices.Clear();

            for (int cz = 0; cz < g.CellsZ; cz++)
                for (int cx = 0; cx < g.CellsX; cx++)
                    Outline(g, cx, cz);

            _mesh.Clear();
            _mesh.indexFormat = _verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            _mesh.SetVertices(_verts);
            _mesh.SetIndices(_indices, MeshTopology.Lines, 0);
            _mesh.RecalculateBounds();
        }

        void Outline(CellGrid g, int cx, int cz)
        {
            // Through the edge midpoints as well as the corners, so the outline follows
            // a ridge crossing the edge instead of cutting under it.
            Vector3 previous = Ring(g, cx, cz, 7);
            for (int i = 0; i < 8; i++)
            {
                Vector3 next = Ring(g, cx, cz, i);
                Push(previous); Push(next);
                previous = next;
            }
        }

        internal static Vector3 Ring(CellGrid g, int cx, int cz, int i, float lift)
        {
            float s = g.CellSize;
            switch (i)
            {
                case 0: return new Vector3(cx * s, g.CornerMetresForCell(cx, cz, cx, cz) + lift, cz * s);
                case 1: return new Vector3((cx + 0.5f) * s, g.EdgeMetresForCell(cx, cz, cx, cz - 1) + lift, cz * s);
                case 2: return new Vector3((cx + 1) * s, g.CornerMetresForCell(cx, cz, cx + 1, cz) + lift, cz * s);
                case 3: return new Vector3((cx + 1) * s, g.EdgeMetresForCell(cx, cz, cx + 1, cz) + lift, (cz + 0.5f) * s);
                case 4: return new Vector3((cx + 1) * s, g.CornerMetresForCell(cx, cz, cx + 1, cz + 1) + lift, (cz + 1) * s);
                case 5: return new Vector3((cx + 0.5f) * s, g.EdgeMetresForCell(cx, cz, cx, cz + 1) + lift, (cz + 1) * s);
                case 6: return new Vector3(cx * s, g.CornerMetresForCell(cx, cz, cx, cz + 1) + lift, (cz + 1) * s);
                default: return new Vector3(cx * s, g.EdgeMetresForCell(cx, cz, cx - 1, cz) + lift, (cz + 0.5f) * s);
            }
        }

        Vector3 Ring(CellGrid g, int cx, int cz, int i)
        {
            return Ring(g, cx, cz, i, Lift);
        }

        void Push(Vector3 v)
        {
            _indices.Add(_verts.Count);
            _verts.Add(v);
        }
    }

    /// <summary>Highlights the targeted cell: its outline plus a cross to the centre point.</summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class CellMarquee : MonoBehaviour
    {
        const float Lift = 0.04f;

        CellGrid _grid;
        Mesh _mesh;
        MeshRenderer _renderer;

        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<int> _indices = new List<int>();

        public void Initialise(CellGrid grid)
        {
            _grid = grid;

            _mesh = new Mesh();
            _mesh.name = "CellMarquee";
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            _renderer = GetComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            transform.position = grid.Origin;
            Hide();
        }

        public void Hide() { if (_renderer != null) _renderer.enabled = false; }

        public void Show(int cx, int cz)
        {
            if (_grid == null || !_grid.InBounds(cx, cz)) { Hide(); return; }

            _verts.Clear();
            _indices.Clear();

            Vector3 mid = new Vector3(
                (cx + 0.5f) * _grid.CellSize,
                _grid.GetMetres(cx, cz) + Lift,
                (cz + 0.5f) * _grid.CellSize);

            Vector3 previous = CellGridOverlay.Ring(_grid, cx, cz, 7, Lift);
            for (int i = 0; i < 8; i++)
            {
                Vector3 next = CellGridOverlay.Ring(_grid, cx, cz, i, Lift);
                Push(previous); Push(next);

                // Spokes to the centre make the stored point visible -- that is the value
                // the number refers to, and the thing sculpt actually moves.
                Push(next); Push(mid);

                previous = next;
            }

            _mesh.Clear();
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
