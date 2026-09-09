using UnityEngine;
using Terraform.Span;
using Terraform.View;

namespace Terraform.Play
{
    /// <summary>
    /// The appearance gate, plus enough mining to feel it.
    ///
    /// Builds a span world from the same height function the main prototype uses, meshes it
    /// with the exact or smoothed mesher, and lets you walk and dig it. Nothing here accounts
    /// or networks -- the questions are whether the geometry is acceptable, and what one edit
    /// actually costs when only the affected bricks rebuild.
    ///
    /// That second number is the one the earlier projection was guessing at. A whole-world
    /// bake says nothing about a per-edit cost; rebuilding the touched bricks does.
    /// </summary>
    public sealed class SpanGateBootstrap : MonoBehaviour, IWorldBootstrap
    {
        [Header("World")]
        public float AreaMetres = 32f;

        /// <summary>Mesh/collider grouping. Not an ownership region -- see the design note.</summary>
        public float BrickMetres = 4f;

        public float FloorMetres = 0f;
        public float BaseMetres = 4f;

        /// <summary>
        /// Deliberately NOT a serialized field. Unity bakes public field values into the scene
        /// when a component is first added, so changing this default in code left an
        /// already-saved scene holding the old array -- which is how the 1 m option went
        /// missing. Static, so the code is the only source of truth.
        /// </summary>
        public static readonly float[] ColumnSizes = { 1f, 0.5f, 0.25f, 0.125f };

        /// <summary>
        /// What one swing takes out. Separate from the column size on purpose: the bite is a
        /// player-scale decision and the column is a fidelity one, and collapsing them is what
        /// makes people think finer geometry has to mean more clicking.
        /// </summary>
        public static readonly float[] BiteSizes = { 1f, 0.5f, 0.25f };

        [Header("Tools")]
        public int ColumnSizeIndex;
        public int BiteSizeIndex;

        /// <summary>Take only the material aimed at, leaving everything else in the face.</summary>
        public bool Selective = true;

        /// <summary>Snap tunnel floors to whole steps, the cubed behaviour. Off gives ramps.</summary>
        public bool QuantiseFloor;

        [Header("Player")]
        public float EyeHeight = 1.62f;
        public float Reach = 4.5f;

        struct ViewPoint
        {
            public string Name;
            public Vector3 At;      // y is a hint: the open surface nearest it wins
            public float Yaw;

            public ViewPoint(string name, float x, float y, float z, float yaw)
            {
                Name = name; At = new Vector3(x, y, z); Yaw = yaw;
            }
        }

        static readonly ViewPoint[] Views =
        {
            new ViewPoint("pad step", -8f, 6f, 5f, 90f),
            new ViewPoint("adit mouth", 7.7f, 4f, -14.5f, 0f),
            new ViewPoint("inside the adit", 7.7f, 2.6f, -11f, 0f),
            new ViewPoint("the drift corner", 7.7f, 2.6f, -10f, 90f),
            new ViewPoint("summit", 7.7f, 15f, -7.7f, 200f),
        };

        // ---- results, read by the HUD ----
        public float FillMs { get; private set; }
        public float BuildMs { get; private set; }
        public float CookMs { get; private set; }
        public int Triangles { get; private set; }
        public int BrickCount { get; private set; }
        public int ColumnCount { get; private set; }
        public int SpanCount { get; private set; }

        public int EditBricks { get; private set; }
        public float EditBuildMs { get; private set; }
        public float EditCookMs { get; private set; }
        public string EditNote { get; private set; }

        public float ColumnSize { get { return ColumnSizes[Mathf.Clamp(ColumnSizeIndex, 0, ColumnSizes.Length - 1)]; } }
        public float BiteSize { get { return BiteSizes[Mathf.Clamp(BiteSizeIndex, 0, BiteSizes.Length - 1)]; } }
        public string ViewName { get { return Views[_view].Name; } }

        sealed class Brick
        {
            public GameObject Go;
            public Mesh Mesh;
            public MeshCollider Collider;
            public MeshRenderer Renderer;
            public int X0, Z0, W, H;
            public int Triangles;
        }

        SpanGrid _grid;
        GameObject _root;
        Brick[,] _bricks;
        int _bricksPerSide;
        int _columnsPerBrick;
        Material[] _materials;

        FirstPersonController _player;
        Transform _head;
        Camera _cam;
        SkyController _sky;
        PauseMenu _menu;
        Light _sun;
        Light _lamp;
        int _view;

        void Awake()
        {
            EnsureLight();
            BuildMaterials();
            Rebuild();

            _player = BuildPlayer();
            _cam = AttachCamera();

            _sky = gameObject.AddComponent<SkyController>();
            _sky.Initialise(_sun, _cam);

            gameObject.AddComponent<SpanGateHud>().Gate = this;
            _menu = gameObject.AddComponent<PauseMenu>();

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        void Update()
        {
            if (InputCompat.EscapePressed && _menu != null) _menu.Toggle();

            if (_menu != null && _menu.IsOpen)
            {
                if (_player != null) _player.enabled = false;
                return;
            }

            if (_player != null) _player.enabled = true;

            if (InputCompat.SpanResolutionPressed)
            {
                ColumnSizeIndex = (ColumnSizeIndex + 1) % ColumnSizes.Length;
                Rebuild();
                GoTo(_view);
            }

            if (InputCompat.SmoothPressed)
            {
                SpanMeshBuilder.Smooth = !SpanMeshBuilder.Smooth;
                Rebuild();
            }

            if (InputCompat.ToggleGridPressed)
            {
                QuantiseFloor = !QuantiseFloor;
                Rebuild();
                GoTo(_view);
            }

            if (InputCompat.BrushUpPressed) BiteSizeIndex = Mathf.Min(BiteSizeIndex + 1, BiteSizes.Length - 1);
            if (InputCompat.BrushDownPressed) BiteSizeIndex = Mathf.Max(BiteSizeIndex - 1, 0);
            if (InputCompat.ToggleSurfacePressed) Selective = !Selective;

            if (InputCompat.LeftPressed) Mine();

            if (InputCompat.Tool1Pressed) GoTo(0);
            if (InputCompat.Tool2Pressed) GoTo(1);
            if (InputCompat.Tool3Pressed) GoTo(2);
            if (InputCompat.Tool4Pressed) GoTo(3);
            if (InputCompat.HolePressed) GoTo(4);

            if (_player != null) _player.TerrainCollider = BrickUnder(_player.transform.position);

            UpdateLamp();
        }

        // ---- mining ------------------------------------------------------------

        /// <summary>
        /// Take one bite out of whatever is under the crosshair, then rebuild only the bricks
        /// that changed. The rebuild set includes a one-brick ring, because a face is culled
        /// against its neighbour's spans and removing material can expose a wall that belongs
        /// to the brick next door.
        /// </summary>
        void Mine()
        {
            EditNote = null;

            if (_cam == null || _grid == null) return;

            RaycastHit hit;
            var ray = new Ray(_cam.transform.position, _cam.transform.forward);

            if (!Physics.Raycast(ray, out hit, Reach))
            {
                EditNote = "nothing within reach";
                return;
            }

            // A little past the surface, so the sample lands inside the material rather than
            // exactly on the boundary between it and the air.
            Vector3 at = hit.point + ray.direction * (BiteSize * 0.35f);
            float half = BiteSize * 0.5f;

            int x0, z0, x1, z1;
            ColumnAt(at.x - half, at.z - half, out x0, out z0);
            ColumnAt(at.x + half, at.z + half, out x1, out z1);

            int bottomMm = SpanGrid.ToMm(at.y - half);
            int topMm = SpanGrid.ToMm(at.y + half);

            int mx, mz;
            ColumnAt(at.x, at.z, out mx, out mz);
            byte target = _grid.MaterialAt(mx, mz, SpanGrid.ToMm(at.y));

            if (Selective && target == 255)
            {
                EditNote = "aimed at air";
                return;
            }

            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!_grid.InBounds(cx, cz)) continue;

                    if (Selective) _grid.SubtractMaterial(cx, cz, bottomMm, topMm, target);
                    else _grid.Subtract(cx, cz, bottomMm, topMm);
                }

            SpanCount = _grid.SpanCount;
            EditNote = Selective
                ? SpanMaterials.Names[target < SpanMaterials.Count ? target : 0]
                : "everything";

            RebuildRegion(x0, z0, x1, z1);
        }

        /// <summary>
        /// Rebuild every brick whose columns, or whose one-column halo, changed.
        ///
        /// The halo matters because a face is culled against its neighbour's spans, so
        /// removing material can expose a wall belonging to the brick next door. But it only
        /// matters at a boundary: an edit in the middle of a brick needs no neighbour rebuilt
        /// at all. Rebuilding a blanket 3x3 did nine bricks' work for one brick's change,
        /// which at fine columns is most of the cost of an edit.
        /// </summary>
        void RebuildRegion(int x0, int z0, int x1, int z1)
        {
            int hx0 = x0 - 1, hx1 = x1 + 1;
            int hz0 = z0 - 1, hz1 = z1 + 1;

            int bx0 = Mathf.Max(0, hx0 / _columnsPerBrick);
            int bz0 = Mathf.Max(0, hz0 / _columnsPerBrick);
            int bx1 = Mathf.Min(_bricksPerSide - 1, hx1 / _columnsPerBrick);
            int bz1 = Mathf.Min(_bricksPerSide - 1, hz1 / _columnsPerBrick);

            EditBricks = 0;
            EditBuildMs = 0f;
            EditCookMs = 0f;

            for (int bz = bz0; bz <= bz1; bz++)
                for (int bx = bx0; bx <= bx1; bx++)
                {
                    // Does this brick, or its halo, actually overlap what changed?
                    int cx0 = bx * _columnsPerBrick;
                    int cz0 = bz * _columnsPerBrick;
                    int cx1 = cx0 + _columnsPerBrick - 1;
                    int cz1 = cz0 + _columnsPerBrick - 1;

                    if (cx1 < hx0 || cx0 > hx1 || cz1 < hz0 || cz0 > hz1) continue;

                    float build, cook;
                    if (!BuildBrick(bx, bz, out build, out cook)) continue;

                    EditBricks++;
                    EditBuildMs += build;
                    EditCookMs += cook;
                }

            Triangles = 0;
            for (int bz = 0; bz < _bricksPerSide; bz++)
                for (int bx = 0; bx < _bricksPerSide; bx++)
                    if (_bricks[bx, bz] != null) Triangles += _bricks[bx, bz].Triangles;
        }

        // ---- world -------------------------------------------------------------

        public void Rebuild()
        {
            if (_root != null) Destroy(_root);

            float column = ColumnSize;
            int columns = Mathf.RoundToInt(AreaMetres / column);
            var origin = new Vector3(-AreaMetres * 0.5f, 0f, -AreaMetres * 0.5f);

            _grid = new SpanGrid(columns, columns, column, origin, SpanGrid.ToMm(FloorMetres));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            SpanContent.Fill(_grid, AreaMetres, FloorMetres, BaseMetres, QuantiseFloor);
            FillMs = (float)watch.Elapsed.TotalMilliseconds;

            ColumnCount = columns * columns;
            SpanCount = _grid.SpanCount;

            _root = new GameObject("SpanWorld");
            _root.transform.SetParent(transform, false);

            _columnsPerBrick = Mathf.Max(1, Mathf.RoundToInt(BrickMetres / column));
            _bricksPerSide = Mathf.CeilToInt(columns / (float)_columnsPerBrick);
            _bricks = new Brick[_bricksPerSide, _bricksPerSide];

            BuildMs = CookMs = 0f;
            Triangles = 0;
            BrickCount = 0;

            for (int bz = 0; bz < _bricksPerSide; bz++)
                for (int bx = 0; bx < _bricksPerSide; bx++)
                {
                    float build, cook;
                    if (!BuildBrick(bx, bz, out build, out cook)) continue;

                    BuildMs += build;
                    CookMs += cook;
                    BrickCount++;
                    Triangles += _bricks[bx, bz].Triangles;
                }

            EditBricks = 0;
            EditBuildMs = EditCookMs = 0f;
            EditNote = null;
        }

        bool BuildBrick(int bx, int bz, out float buildMs, out float cookMs)
        {
            buildMs = cookMs = 0f;

            int x0 = bx * _columnsPerBrick;
            int z0 = bz * _columnsPerBrick;
            int w = Mathf.Min(_columnsPerBrick, _grid.ColumnsX - x0);
            int h = Mathf.Min(_columnsPerBrick, _grid.ColumnsZ - z0);
            if (w <= 0 || h <= 0) return false;

            Brick brick = _bricks[bx, bz];

            if (brick == null)
            {
                var local = new Vector3(_grid.WorldX(x0), 0f, _grid.WorldZ(z0));

                brick = new Brick { X0 = x0, Z0 = z0, W = w, H = h };
                brick.Go = new GameObject("Brick " + bx + "," + bz);
                brick.Go.transform.SetParent(_root.transform, false);
                brick.Go.transform.position = local;

                brick.Mesh = new Mesh();
                brick.Mesh.name = brick.Go.name;

                brick.Go.AddComponent<MeshFilter>().sharedMesh = brick.Mesh;
                brick.Renderer = brick.Go.AddComponent<MeshRenderer>();
                brick.Renderer.sharedMaterials = _materials;
                brick.Collider = brick.Go.AddComponent<MeshCollider>();

                _bricks[bx, bz] = brick;
            }

            Vector3 origin = brick.Go.transform.position;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            SpanMeshBuilder.Build(_grid, brick.X0, brick.Z0, brick.W, brick.H, brick.Mesh, origin);
            buildMs = (float)watch.Elapsed.TotalMilliseconds;

            brick.Triangles = brick.Mesh.vertexCount / 3;
            bool solid = brick.Mesh.vertexCount > 0;

            // Same boundary as the main prototype's HUD: null then assign forces a synchronous
            // cook, which is the worst case and the one worth recording.
            watch.Reset();
            watch.Start();
            brick.Collider.sharedMesh = null;
            if (solid) brick.Collider.sharedMesh = brick.Mesh;
            cookMs = (float)watch.Elapsed.TotalMilliseconds;

            brick.Renderer.enabled = solid;
            return true;
        }

        Collider BrickUnder(Vector3 world)
        {
            if (_bricks == null || _grid == null) return null;

            int cx, cz;
            ColumnAt(world.x, world.z, out cx, out cz);

            int bx = Mathf.Clamp(cx / _columnsPerBrick, 0, _bricksPerSide - 1);
            int bz = Mathf.Clamp(cz / _columnsPerBrick, 0, _bricksPerSide - 1);

            Brick brick = _bricks[bx, bz];
            return brick == null ? null : brick.Collider;
        }

        // ---- scene furniture ---------------------------------------------------

        void BuildMaterials()
        {
            _materials = new Material[SpanMaterials.Count];

            for (int i = 0; i < _materials.Length; i++)
            {
                Material m = MaterialLibrary.Build("span " + SpanMaterials.Names[i],
                    MaterialLibrary.Lit, MaterialLibrary.LitShaders);

                if (m == null) continue;

                Color colour = SpanMaterials.Colours[i];
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
                if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.03f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);

                _materials[i] = m;
            }
        }

        void EnsureLight()
        {
            foreach (Light existing in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (existing.type != LightType.Directional) continue;
                _sun = existing;
                return;
            }

            var go = new GameObject("Sun");
            _sun = go.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.shadows = LightShadows.Soft;
        }

        FirstPersonController BuildPlayer()
        {
            var go = new GameObject("Player");

            var cc = go.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.3f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.slopeLimit = 55f;
            cc.stepOffset = 0.45f;
            cc.skinWidth = 0.02f;

            _head = new GameObject("Head").transform;
            _head.SetParent(go.transform, false);
            _head.localPosition = new Vector3(0f, EyeHeight, 0f);

            var player = go.AddComponent<FirstPersonController>();
            player.Head = _head;

            var lampGo = new GameObject("Headlamp");
            lampGo.transform.SetParent(_head, false);
            _lamp = lampGo.AddComponent<Light>();
            _lamp.type = LightType.Point;
            _lamp.range = 16f;
            _lamp.intensity = 1.6f;
            _lamp.color = new Color(1f, 0.93f, 0.82f);
            _lamp.shadows = LightShadows.None;
            _lamp.enabled = false;

            _player = player;
            _view = 2;                 // start inside the adit; that is the thing to judge
            GoTo(_view);

            return player;
        }

        /// <summary>
        /// Drop the player at a viewpoint. The height in the viewpoint is a hint, not a
        /// position: the open surface nearest it wins, so "inside the adit" lands on the
        /// tunnel floor rather than on the mountain ten metres overhead.
        /// </summary>
        void GoTo(int index)
        {
            if (_player == null || _grid == null) return;

            _view = Mathf.Clamp(index, 0, Views.Length - 1);
            ViewPoint view = Views[_view];

            int cx, cz;
            ColumnAt(view.At.x, view.At.z, out cx, out cz);

            int open = _grid.OpenTopNear(cx, cz, SpanGrid.ToMm(view.At.y));
            float ground = open == int.MinValue ? view.At.y : SpanGrid.ToMetres(open);

            var cc = _player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            // Disabled and re-enabled so the controller re-reads its yaw from the transform.
            _player.enabled = false;
            _player.transform.position = new Vector3(view.At.x, ground + 0.4f, view.At.z);
            _player.transform.rotation = Quaternion.Euler(0f, view.Yaw, 0f);
            _player.enabled = true;

            if (cc != null) cc.enabled = true;
        }

        void ColumnAt(float x, float z, out int cx, out int cz)
        {
            cx = Mathf.Clamp(Mathf.FloorToInt((x - _grid.Origin.x) / _grid.ColumnSize), 0, _grid.ColumnsX - 1);
            cz = Mathf.Clamp(Mathf.FloorToInt((z - _grid.Origin.z) / _grid.ColumnSize), 0, _grid.ColumnsZ - 1);
        }

        /// <summary>
        /// The lamp comes on under rock and goes off in daylight. Judging a cave wall in the
        /// dark tells you nothing, and a lamp burning outdoors would flatten exactly the
        /// shading the surface has to be judged on.
        /// </summary>
        void UpdateLamp()
        {
            if (_lamp == null || _grid == null || _player == null) return;

            Vector3 at = _player.transform.position;

            int cx, cz;
            ColumnAt(at.x, at.z, out cx, out cz);

            _lamp.enabled = _grid.SolidAbove(cx, cz, SpanGrid.ToMm(at.y + EyeHeight));
        }

        Camera AttachCamera()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
            }

            cam.farClipPlane = 500f;
            cam.nearClipPlane = 0.05f;

            cam.transform.SetParent(_head, false);
            cam.transform.localPosition = Vector3.zero;
            cam.transform.localRotation = Quaternion.identity;

            return cam;
        }
    }
}
