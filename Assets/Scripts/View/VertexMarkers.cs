using System.Collections.Generic;
using UnityEngine;
using Terraform.Core;

namespace Terraform.View
{
    /// <summary>
    /// The cell grid drawn onto the ground itself.
    ///
    /// Height numbers say what the ground is doing; they do not show it. Reading whether a
    /// patch is level from a scatter of decimals means comparing them pair by pair, and on
    /// textured ground the surface gives almost nothing away -- a tenth of a metre across a
    /// metre is invisible against grass.
    ///
    /// Ribbons along the cell edges turn that into a shape question. Level ground puts the
    /// squares in a plane and the eye finds a plane instantly, while a single vertex left
    /// behind bends four squares at once.
    ///
    /// They lie ON the surface rather than floating over it: a cell edge runs vertex to
    /// vertex, which is an edge of the heightfield's own triangulation, so the ribbon
    /// follows the ground exactly instead of cutting through hills and hovering over dips.
    /// Only a small vertical lift keeps it out of a z-fight.
    ///
    /// One combined mesh rather than instanced draws, because instancing needs the shader
    /// to support it and this has to work with whatever material the library resolves --
    /// the same constraint that turns a player build magenta when it is ignored.
    /// </summary>
    public sealed class VertexMarkers : MonoBehaviour
    {
        public bool Show = true;

        /// <summary>Ribbon width in metres. Thin enough to read as a line, not a path.</summary>
        public float Size = 0.03f;

        /// <summary>How far the grid is drawn. Beyond this it is clutter.</summary>
        public float Range = 14f;

        /// <summary>Lift off the surface. Just enough to win the depth test.</summary>
        public float Lift = 0.02f;

        HeightGrid _grid;
        Transform _eye;

        Mesh _mesh;
        MeshRenderer _renderer;

        int _version = -1;
        int _centreVx = int.MinValue;
        int _centreVz = int.MinValue;
        int _targetVx = int.MinValue;
        int _targetVz = int.MinValue;
        bool _dirty = true;

        static readonly List<Vector3> Verts = new List<Vector3>();
        static readonly List<int> Plain = new List<int>();
        static readonly List<int> Target = new List<int>();

        public void Initialise(HeightGrid grid, Transform eye, Material plain, Material target)
        {
            _grid = grid;
            _eye = eye;

            _mesh = new Mesh { name = "Surface Grid" };
            _mesh.MarkDynamic();
            _mesh.subMeshCount = 2;

            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;

            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.sharedMaterials = new[] { plain, target };
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            _dirty = true;
        }

        /// <summary>
        /// The vertex under the crosshair. The four cells around it are highlighted, because
        /// those four are exactly what a sculpt click moves -- the highlight is the footprint
        /// of the edit, not a decoration.
        /// </summary>
        public void SetTarget(int vx, int vz)
        {
            if (vx == _targetVx && vz == _targetVz) return;

            _targetVx = vx;
            _targetVz = vz;
            _dirty = true;
        }

        void LateUpdate()
        {
            if (_grid == null || _eye == null) return;

            _renderer.enabled = Show;
            if (!Show) return;

            int vx, vz;
            _grid.WorldToNearestVertex(_eye.position, out vx, out vz);

            if (_grid.Version != _version || vx != _centreVx || vz != _centreVz) _dirty = true;
            if (!_dirty) return;

            _version = _grid.Version;
            _centreVx = vx;
            _centreVz = vz;
            _dirty = false;

            Rebuild(vx, vz);
        }

        void Rebuild(int centreVx, int centreVz)
        {
            Verts.Clear();
            Plain.Clear();
            Target.Clear();

            int range = Mathf.CeilToInt(Range / _grid.CellSize);
            float rangeSq = Range * Range;

            Vector3 eye = _eye.position;

            for (int vz = centreVz - range; vz <= centreVz + range; vz++)
            {
                for (int vx = centreVx - range; vx <= centreVx + range; vx++)
                {
                    if (!_grid.InBounds(vx, vz)) continue;

                    Vector3 at = _grid.VertexWorld(vx, vz);
                    if ((at - eye).sqrMagnitude > rangeSq) continue;

                    // Each vertex contributes the two edges leaving it, so every edge is
                    // emitted once rather than once per cell that touches it.
                    Edge(vx, vz, vx + 1, vz);
                    Edge(vx, vz, vx, vz + 1);
                }
            }

            _mesh.Clear();
            _mesh.indexFormat = Verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            _mesh.SetVertices(Verts);
            _mesh.subMeshCount = 2;
            _mesh.SetTriangles(Plain, 0);
            _mesh.SetTriangles(Target, 1);
            _mesh.RecalculateBounds();
        }

        void Edge(int ax, int az, int bx, int bz)
        {
            if (!_grid.InBounds(ax, az) || !_grid.InBounds(bx, bz)) return;

            Vector3 a = _grid.VertexWorld(ax, az) - transform.position;
            Vector3 b = _grid.VertexWorld(bx, bz) - transform.position;

            a.y += Lift;
            b.y += Lift;

            // Across the edge, flat on the ground.
            Vector3 along = (b - a);
            along.y = 0f;

            Vector3 across = Vector3.Cross(Vector3.up, along.normalized) * (Size * 0.5f);

            List<int> tris = Highlighted(ax, az, bx, bz) ? Target : Plain;

            int i = Verts.Count;

            Verts.Add(a - across);
            Verts.Add(a + across);
            Verts.Add(b + across);
            Verts.Add(b - across);

            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);

            // Both windings: the ribbon is a flat sliver with no inside, and on a slope you
            // can easily end up looking at the face that would have been culled.
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 1);
            tris.Add(i); tris.Add(i + 3); tris.Add(i + 2);
        }

        /// <summary>An edge bounding one of the four cells that share the targeted vertex.</summary>
        bool Highlighted(int ax, int az, int bx, int bz)
        {
            if (_targetVx == int.MinValue) return false;

            return Mathf.Abs(ax - _targetVx) <= 1 && Mathf.Abs(az - _targetVz) <= 1
                && Mathf.Abs(bx - _targetVx) <= 1 && Mathf.Abs(bz - _targetVz) <= 1;
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
