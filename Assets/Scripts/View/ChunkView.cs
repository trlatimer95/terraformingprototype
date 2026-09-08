using UnityEngine;
using Terraform.Core;

namespace Terraform.View
{
    /// <summary>
    /// Scene-side view of one HeightGrid: owns the mesh, the collider, and nothing else.
    /// All authority lives in the grid and the command log, so this class can be thrown
    /// away and replaced per engine without touching gameplay logic.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    [RequireComponent(typeof(MeshCollider))]
    public sealed class ChunkView : MonoBehaviour
    {
        public HeightGrid Grid { get; private set; }
        public CommandLog Log { get; private set; }

        public float LastMeshMs { get; private set; }
        public float LastColliderMs { get; private set; }
        public int TriangleCount { get { return _mesh == null ? 0 : _mesh.vertexCount / 3; } }

        public event System.Action MeshChanged;

        Mesh _mesh;
        MeshCollider _collider;
        bool _dirty;

        public void Initialise(HeightGrid grid)
        {
            Grid = grid;
            Log = new CommandLog();

            _collider = GetComponent<MeshCollider>();

            _mesh = new Mesh();
            _mesh.name = "TerrainChunk";
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            transform.position = grid.Origin;
            Rebuild();
        }

        public bool Execute(ITerrainCommand cmd)
        {
            if (Grid == null || !Log.Execute(cmd, Grid)) return false;
            _dirty = true;
            return true;
        }

        public bool Undo()
        {
            if (Grid == null || !Log.Undo(Grid)) return false;
            _dirty = true;
            return true;
        }

        public bool Redo()
        {
            if (Grid == null || !Log.Redo(Grid)) return false;
            _dirty = true;
            return true;
        }

        // Coalesce every edit made this frame into one rebuild. Held-button terraforming
        // fires many commands per frame and each is cheap; the rebuild is not.
        void LateUpdate()
        {
            if (!_dirty) return;
            _dirty = false;
            Rebuild();
        }

        public void Rebuild()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ChunkMeshBuilder.Build(Grid, _mesh);
            LastMeshMs = (float)sw.Elapsed.TotalMilliseconds;

            sw.Reset();
            sw.Start();
            // Reassigning forces PhysX to re-cook the collision mesh. This is the
            // expensive half and the first thing to partition per-region at P4.
            _collider.sharedMesh = null;
            _collider.sharedMesh = _mesh;
            LastColliderMs = (float)sw.Elapsed.TotalMilliseconds;

            if (MeshChanged != null) MeshChanged();
        }
    }
}
