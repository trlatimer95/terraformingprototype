using System.Collections.Generic;
using UnityEngine;
using Terraform.Core;
using Terraform.Span;
using Terraform.View;

namespace Terraform.Play
{
    /// <summary>
    /// The original terraforming laid over the tunnelling, on one piece of ground.
    ///
    /// Surface is the cell model, unchanged: the same grid, the same commands, the same
    /// derived corners and edge midpoints, the same terracing release. Underground is the
    /// span store. Neither has been bent to fit the other -- the only new thing is
    /// CellSurface, which lets a span column ask the surface what height it is instead of
    /// deciding for itself.
    ///
    /// What to look at, in order:
    ///
    ///   1. The mouth of the adit. The ground either side of it is drawn by two different
    ///      meshers at a four-metre boundary. If the seam idea works you cannot tell where.
    ///   2. Terraforming over the tunnel roof. Raise and lower ground standing on top of
    ///      it; the roof thins and thickens under you and the edit is refused before it
    ///      breaks through.
    ///   3. Digging down from open ground. This is where the two genuinely disagree and
    ///      the demo does not hide it -- see the HUD note.
    /// </summary>
    public sealed class HybridBootstrap : MonoBehaviour, IWorldBootstrap
    {
        [Header("World")]
        public int CellsX = 64;
        public int CellsZ = 64;
        public float CellSize = 1f;
        public float StartHeight = 4f;
        public float FloorMetres = 0f;

        /// <summary>Storage resolution underground. The gate landed on a quarter metre.</summary>
        public float ColumnSize = 0.25f;

        /// <summary>Cells per brick. The brick is also the unit of ownership -- see HybridWorld.</summary>
        public int CellsPerBrick = 4;

        [Header("Ground source")]
        /// <summary>
        /// Any Terrain already in the scene is imported and hidden. Drop a Gaia build in
        /// beside this object and it becomes the ground; with nothing there the demo hill is
        /// used instead. No switch to set.
        /// </summary>
        public Vector2 SampleOrigin = Vector2.zero;

        /// <summary>Metres of source terrain per metre of ours. Above 1 samples a wider area.</summary>
        public float SampleStride = 1f;

        /// <summary>Where the lowest imported point lands, so there is room to dig.</summary>
        public float SampleFloorMetres = 4f;

        public int DiffuseResolution = 1024;

        public bool Imported { get; private set; }

        [Header("Tools")]
        public float Reach = 5f;
        public float EyeHeight = 1.62f;

        public static readonly float[] BiteSizes = { 1f, 0.5f, 0.25f };
        public int BiteSizeIndex = 1;
        public bool Selective = true;

        public enum Job { Terraform, Mine }
        public Job Doing { get; private set; }

        public enum SurfaceTool { Sculpt, Flatten }
        public SurfaceTool Surface { get; private set; }

        /// <summary>
        /// Height numbers over the ground. On by default while terraforming, because the
        /// tool moves cells by a tenth of a metre and there is no judging that by eye.
        /// </summary>
        public bool ShowHeights = true;

        /// <summary>How far the numbers are drawn, in metres. Beyond this they are noise.</summary>
        public float LabelRange = 14f;

        public static readonly int[] StepChoices = { 1, 2, 5, 10, 20 };
        public int StepIndex = 1;
        public int MaxStepUnits = 60;

        // ---- readouts ----
        public float BuildMs { get; private set; }
        public float CookMs { get; private set; }
        public float SurfaceMs { get; private set; }
        public int EditBricks { get; private set; }
        public string Note { get; private set; }
        public int Triangles { get; private set; }

        public HybridWorld World { get { return _world; } }
        public Camera Cam { get { return _cam; } }
        public CellGrid Grid { get { return _cells; } }
        public float BiteSize { get { return BiteSizes[Mathf.Clamp(BiteSizeIndex, 0, BiteSizes.Length - 1)]; } }
        public int StepUnits { get { return StepChoices[Mathf.Clamp(StepIndex, 0, StepChoices.Length - 1)]; } }
        public string ViewName { get { return _views == null ? "-" : _views[_view].Name; } }
        public bool HasTarget { get; private set; }
        public int TargetCx { get; private set; }
        public int TargetCz { get; private set; }
        public float SeamError { get; private set; }

        struct ViewPoint
        {
            public string Name;
            public Vector3 At;
            public float Yaw;

            public ViewPoint(string name, float x, float y, float z, float yaw)
            {
                Name = name; At = new Vector3(x, y, z); Yaw = yaw;
            }
        }

        /// <summary>
        /// Built after the adit is sited, because on imported ground its position is not
        /// known until the heightfield has been read.
        /// </summary>
        ViewPoint[] _views;

        void BuildViews()
        {
            _views = new[]
            {
                new ViewPoint("the bench", PortalX - 2.5f, PortalFloor + 1f, PortalZ, 90f),
                new ViewPoint("inside the adit", PortalX + 3.5f, PortalFloor + 0.6f, PortalZ, 90f),
                new ViewPoint("the drift", PortalX + 5.5f, PortalFloor - 0.6f, PortalZ + 5f, 0f),
                new ViewPoint("on the roof", PortalX + 3.5f, PortalFloor + 12f, PortalZ, 90f),
                new ViewPoint("open ground", _cells.Origin.x + _cells.CellsX * 0.5f, PortalFloor + 8f,
                                             _cells.Origin.z + _cells.CellsZ * 0.5f, 135f),
            };
        }

        sealed class Brick
        {
            public GameObject Go;
            public Mesh Mesh;
            public MeshCollider Collider;
            public MeshRenderer Renderer;
            public int Triangles;
        }

        CellGrid _cells;
        HybridWorld _world;
        CellChunkView _chunk;
        MeshCollider _surfaceCollider;

        GameObject _spanRoot;
        readonly Dictionary<int, Brick> _bricks = new Dictionary<int, Brick>();
        Material[] _spanMaterials;
        Material _groundMaterial;

        FirstPersonController _player;
        Transform _head;
        Camera _cam;
        SkyController _sky;
        PauseMenu _menu;
        Light _sun;
        Light _lamp;
        CellMarquee _marquee;
        TerrainImport.Source _source;
        Texture2D _surfaceTexture;
        int _view;
        int _lastEditCx;
        int _lastEditCz;

        static readonly Color GroundColour = new Color(0.36f, 0.44f, 0.28f);

        void Awake()
        {
            // Cleared up front as well as in OnDestroy. These are static hooks on shared
            // meshers, and a scene that inherits one from a previous run draws holes where
            // nothing has been dug.
            CellMeshBuilder.Ceded = null;
            SpanMeshBuilder.Surface = null;

            // Noted now, evicted at the end of Awake. Another bootstrap in the scene builds
            // a second world on the same ground: two HUDs at the same screen position, its
            // keys firing alongside these ones, and -- worst -- its intact surface mesh
            // sitting coplanar over these bricks, hiding every hole and z-fighting with the
            // ceded caps. Eviction waits until the camera has been taken over, because the
            // camera is usually parented under the other bootstrap's player.
            List<GameObject> foreign = FindForeignBootstraps();

            EnsureLight();
            BuildMaterials();
            BuildSurface();

            _world = new HybridWorld(new CellGround(_cells), ColumnSize, CellsPerBrick, FloorMetres);
            SpanMeshBuilder.Surface = _world;
            SpanMeshBuilder.Smooth = true;
            CellMeshBuilder.Ceded = _world.CellCeded;

            ApplyDiffuse();

            _spanRoot = new GameObject("SpanWorld");
            _spanRoot.transform.SetParent(transform, false);

            CarveMine();
            BuildViews();
            CheckSeam();
            FlushDirty();
            RebuildSurface();

            _player = BuildPlayer();
            _cam = AttachCamera();

            _sky = gameObject.AddComponent<SkyController>();
            _sky.Initialise(_sun, _cam);

            gameObject.AddComponent<HybridHud>().Scene = this;
            _menu = gameObject.AddComponent<PauseMenu>();

            // The bench: the mouth is dead ahead and the ground either side of the ceded
            // boundary is the first thing worth judging.
            GoTo(0);

            Evict(foreign);

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        /// <summary>Other bootstraps sharing this scene. Empty is the normal case.</summary>
        List<GameObject> FindForeignBootstraps()
        {
            var found = new List<GameObject>();

            foreach (P0Bootstrap other in
                     FindObjectsByType<P0Bootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (other.gameObject != gameObject) found.Add(other.gameObject);

            foreach (SpanGateBootstrap other in
                     FindObjectsByType<SpanGateBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (other.gameObject != gameObject) found.Add(other.gameObject);

            return found;
        }

        /// <summary>
        /// Remove another bootstrap and the world it built.
        ///
        /// Its roots are created unparented in its own Awake, which may already have run, so
        /// destroying the component alone leaves the scenery behind. Anything of this scene's
        /// own is parented under this object, which is how the two are told apart.
        /// </summary>
        void Evict(List<GameObject> foreign)
        {
            if (foreign.Count == 0) return;

            foreach (GameObject go in foreign)
            {
                Debug.LogWarningFormat(
                    "[Hybrid] '{0}' is another bootstrap in this scene and has been removed. " +
                    "Two worlds on the same ground hide each other's geometry. " +
                    "Use Tools > Terraform > Rebuild Hybrid Scene for a clean one.", go.name);

                Destroy(go);
            }

            foreach (GameObject root in
                     UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root == gameObject) continue;
                if (root.transform.IsChildOf(transform)) continue;

                // This scene's own player is unparented and carries the same name, so it has
                // to be excluded by identity rather than by name.
                if (_player != null && root == _player.gameObject) continue;

                switch (root.name)
                {
                    case "CellWorld":
                    case "VertexWorld":
                    case "SpanWorld":
                    case "Player":
                        Destroy(root);
                        break;
                }
            }
        }

        void OnDestroy()
        {
            // Static hooks, so they outlive the scene unless they are cleared. Leaving them
            // set is how the plain demo would start drawing holes where this scene's bricks
            // used to be.
            SpanMeshBuilder.Surface = null;
            CellMeshBuilder.Ceded = null;
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

            if (InputCompat.ToggleModelPressed) Doing = Doing == Job.Terraform ? Job.Mine : Job.Terraform;
            if (InputCompat.ToggleSurfacePressed) Selective = !Selective;
            if (InputCompat.ToggleGridPressed) ShowHeights = !ShowHeights;

            if (InputCompat.Tool1Pressed) Surface = SurfaceTool.Sculpt;
            if (InputCompat.Tool2Pressed) Surface = SurfaceTool.Flatten;

            if (InputCompat.BrushUpPressed) BiteSizeIndex = Mathf.Min(BiteSizeIndex + 1, BiteSizes.Length - 1);
            if (InputCompat.BrushDownPressed) BiteSizeIndex = Mathf.Max(BiteSizeIndex - 1, 0);

            if (InputCompat.SkyPressed && _sky != null) _sky.Next();
            if (InputCompat.HolePressed) GoTo(_view + 1);

            // Resynced around the cell the edit was made at, not the one currently under the
            // crosshair -- undo puts the ground back where the command was, which may be
            // nowhere near where you are now looking.
            if (InputCompat.UndoPressed && _chunk.Undo()) AfterSurfaceEdit(_lastEditCx, _lastEditCz);

            AimTarget();

            if (Doing == Job.Terraform)
            {
                if (InputCompat.LeftPressed) Terraform(+1);
                if (InputCompat.RightPressed) Terraform(-1);
            }
            else
            {
                if (InputCompat.LeftPressed) Mine();
            }

            BindColliders();
            UpdateLamp();
        }

        // ---- aiming ------------------------------------------------------------

        void AimTarget()
        {
            HasTarget = false;
            if (_cam == null) return;

            RaycastHit hit;
            var ray = new Ray(_cam.transform.position, _cam.transform.forward);
            if (!Physics.Raycast(ray, out hit, Reach)) { if (_marquee != null) _marquee.Hide(); return; }

            int cx, cz;
            if (!_cells.WorldToCell(hit.point, out cx, out cz)) { if (_marquee != null) _marquee.Hide(); return; }

            HasTarget = true;
            TargetCx = cx;
            TargetCz = cz;

            if (_marquee != null)
            {
                if (Doing == Job.Terraform) _marquee.Show(cx, cz);
                else _marquee.Hide();
            }
        }

        // ---- surface edits -----------------------------------------------------

        /// <summary>
        /// The original terraforming, unchanged, with one extra step afterwards.
        ///
        /// The command runs first and the consequences are checked second, then undone if
        /// the ground would have come through a tunnel roof. Checking first would mean
        /// predicting derived corners two cells out without applying the edit, which is the
        /// kind of duplicate rule that drifts out of step with the thing it duplicates.
        /// </summary>
        void Terraform(int direction)
        {
            Note = null;
            if (!HasTarget) { Note = "nothing within reach"; return; }

            bool ok = Surface == SurfaceTool.Sculpt
                ? _chunk.Execute(new CellSculptCommand(TargetCx, TargetCz, direction * StepUnits, MaxStepUnits))
                : _chunk.Execute(new CellFlattenCommand(TargetCx, TargetCz, MaxStepUnits));

            if (!ok) { Note = "step limit"; return; }

            _lastEditCx = TargetCx;
            _lastEditCz = TargetCz;

            AfterSurfaceEdit(TargetCx, TargetCz);
        }

        void AfterSurfaceEdit(int cx, int cz)
        {
            int r = HybridWorld.SurfaceInfluenceCells;

            if (!_world.ResyncCells(cx - r, cz - r, cx + r, cz + r))
            {
                _chunk.Undo();
                _world.ResyncCells(cx - r, cz - r, cx + r, cz + r);
                _world.ClearDirty();
                Note = string.Format("would leave {0:0.00} m of roof over a tunnel, minimum {1:0.00} m",
                                     _world.LastRoofMetres, HybridWorld.MinRoof);
                return;
            }

            FlushDirty();
            RebuildSurface();
        }

        // ---- mining ------------------------------------------------------------

        void Mine()
        {
            Note = null;
            if (_cam == null) return;

            RaycastHit hit;
            var ray = new Ray(_cam.transform.position, _cam.transform.forward);
            if (!Physics.Raycast(ray, out hit, Reach)) { Note = "nothing within reach"; return; }

            float bite = BiteSize;
            Vector3 at = hit.point + ray.direction * (bite * 0.35f);

            // Read BEFORE anything can cede, including the sampling below. Taken after it,
            // a selective swing that ceded its own brick while looking up the material would
            // see no change in the count, skip the surface rebuild, and leave the intact
            // surface mesh covering the hole it had just dug. Taking everything skipped that
            // sampling, so only selective mining was affected -- which is exactly how it
            // presented.
            int cededBefore = _world.CededBricks;

            byte target = SpanMaterials.Rock;

            if (Selective)
            {
                int mx, mz;
                ColumnAt(at.x, at.z, out mx, out mz);

                // The column may never have been touched, in which case there is nothing to
                // sample yet. Ceding first is what gives it spans.
                _world.Cede(_world.BrickOfColumn(Mathf.Max(0, mx)), _world.BrickOfColumn(Mathf.Max(0, mz)));

                target = _world.Spans.MaterialAt(mx, mz, SpanGrid.ToMm(at.y));
                if (target == 255) { Note = "aimed at air"; return; }
            }

            float removed = _world.Mine(at, bite, target, Selective);

            Note = string.Format("{0} {1:0.000} m^3",
                Selective ? SpanMaterials.Names[target < SpanMaterials.Count ? target : 0] : "everything",
                removed);

            FlushDirty();

            // Only when the ceded set actually grew. Mining inside a brick that spans
            // already own changes nothing about which cells the surface mesher skips, and
            // rebuilding the whole chunk per swing would both cost more than the edit and
            // bury the number this scene exists to report.
            if (_world.CededBricks != cededBefore) RebuildSurface();
        }

        // ---- meshing -----------------------------------------------------------

        /// <summary>Rebuild every brick the last operation marked, and time it.</summary>
        void FlushDirty()
        {
            BuildMs = CookMs = 0f;
            EditBricks = 0;

            foreach (int index in _world.Dirty)
            {
                int bx = index % _world.BricksX;
                int bz = index / _world.BricksX;
                if (!_world.BrickCeded(bx, bz)) continue;

                float build, cook;
                BuildBrick(bx, bz, out build, out cook);

                BuildMs += build;
                CookMs += cook;
                EditBricks++;
            }

            _world.ClearDirty();

            Triangles = 0;
            foreach (Brick brick in _bricks.Values) Triangles += brick.Triangles;
        }

        void BuildBrick(int bx, int bz, out float buildMs, out float cookMs)
        {
            int index = _world.BrickIndex(bx, bz);

            int x0 = bx * _world.ColumnsPerBrick;
            int z0 = bz * _world.ColumnsPerBrick;
            int w = Mathf.Min(_world.ColumnsPerBrick, _world.Spans.ColumnsX - x0);
            int h = Mathf.Min(_world.ColumnsPerBrick, _world.Spans.ColumnsZ - z0);

            Brick brick;
            if (!_bricks.TryGetValue(index, out brick))
            {
                brick = new Brick();
                brick.Go = new GameObject("Brick " + bx + "," + bz);
                brick.Go.transform.SetParent(_spanRoot.transform, false);
                brick.Go.transform.position =
                    new Vector3(_world.Spans.WorldX(x0), 0f, _world.Spans.WorldZ(z0));

                brick.Mesh = new Mesh();
                brick.Mesh.name = brick.Go.name;

                brick.Go.AddComponent<MeshFilter>().sharedMesh = brick.Mesh;
                brick.Renderer = brick.Go.AddComponent<MeshRenderer>();
                brick.Renderer.sharedMaterials = _spanMaterials;
                brick.Collider = brick.Go.AddComponent<MeshCollider>();

                _bricks[index] = brick;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            SpanMeshBuilder.Build(_world.Spans, x0, z0, w, h, brick.Mesh, brick.Go.transform.position);
            buildMs = (float)watch.Elapsed.TotalMilliseconds;

            brick.Triangles = brick.Mesh.vertexCount / 3;
            bool solid = brick.Mesh.vertexCount > 0;

            watch.Reset();
            watch.Start();
            brick.Collider.sharedMesh = null;
            if (solid) brick.Collider.sharedMesh = brick.Mesh;
            cookMs = (float)watch.Elapsed.TotalMilliseconds;

            brick.Renderer.enabled = solid;
        }

        void RebuildSurface()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _chunk.Rebuild();
            SurfaceMs = (float)watch.Elapsed.TotalMilliseconds;
        }

        // ---- construction ------------------------------------------------------

        void BuildSurface()
        {
            var origin = new Vector3(-CellsX * CellSize * 0.5f, 0f, -CellsZ * CellSize * 0.5f);

            _cells = new CellGrid(CellsX, CellsZ, CellSize, origin, StartHeight);
            ApplySource();

            var root = new GameObject("CellWorld");
            root.transform.SetParent(transform, false);

            var chunkGo = new GameObject("CellChunk");
            chunkGo.transform.SetParent(root.transform, false);
            chunkGo.AddComponent<MeshFilter>();
            chunkGo.AddComponent<MeshRenderer>().sharedMaterial = _groundMaterial;
            _surfaceCollider = chunkGo.AddComponent<MeshCollider>();

            _chunk = chunkGo.AddComponent<CellChunkView>();
            _chunk.Initialise(_cells);

            var marqueeGo = new GameObject("CellMarquee");
            marqueeGo.transform.SetParent(root.transform, false);
            marqueeGo.AddComponent<MeshFilter>();

            Material unlit = MaterialLibrary.Build("marquee", MaterialLibrary.Unlit, MaterialLibrary.UnlitShaders);
            if (unlit != null)
            {
                Color c = new Color(1f, 0.86f, 0.35f);
                if (unlit.HasProperty("_BaseColor")) unlit.SetColor("_BaseColor", c);
                if (unlit.HasProperty("_Color")) unlit.SetColor("_Color", c);
            }
            marqueeGo.AddComponent<MeshRenderer>().sharedMaterial = unlit;

            _marquee = marqueeGo.AddComponent<CellMarquee>();
            _marquee.Initialise(_cells);
        }

        /// <summary>
        /// One adit driven into the mountain's west flank, then a drift turning north and
        /// descending through the ore body.
        ///
        /// Carved only where there is real rock overhead, so the mouth is a hole in a
        /// hillside rather than a trench with the roof cut off. The first column with less
        /// than half a metre of cover is where the tunnel stops being a tunnel.
        /// </summary>
        /// <summary>
        /// Take the ground from a Terrain already in the scene -- a Gaia build, or anything
        /// else that leaves a Unity Terrain behind -- instead of the demo hill.
        ///
        /// Worth having for judging the look rather than the mechanism. The demo hill is a
        /// smooth analytic dome and flatters nothing; real generated ground has the breaks
        /// and shoulders that show whether a representation holds up.
        ///
        /// It changes nothing structural. An imported terrain still lands as one height per
        /// cell, and the span store, the ceding rule and the seam are all indifferent to
        /// where those heights came from.
        /// </summary>
        void ApplySource()
        {
            _source = TerrainImport.Capture();

            if (_source == null || !_source.Valid)
            {
                DemoTerrain.Apply(_cells, StartHeight);
                return;
            }

            SampleStride = Mathf.Max(0.01f, SampleStride);

            float offset = TerrainImport.FitOffset(_source, SampleOrigin, SampleStride,
                                                   CellsX, CellsZ, CellSize, SampleFloorMetres);

            TerrainImport.Apply(_cells, _source, SampleOrigin, SampleStride, offset);

            // The source terrain has to go, or it renders straight through everything built
            // from it and every tunnel is hidden behind the ground it was cut into.
            TerrainImport.Hide(_source);

            Imported = true;

            Debug.LogFormat("[Hybrid] imported {0} scene terrain tile(s) at 1:{1:0.##}. " +
                            "Press T with no tool active, or set Source back, to compare.",
                            _source.Tiles.Length, SampleStride);
        }

        /// <summary>
        /// The terrain's own colouring, baked flat and mapped in world XZ.
        ///
        /// Both meshers write world-XZ UVs, so one planar texture lands identically on the
        /// surface mesh and on a ceded brick's caps. Without it the ground is a single flat
        /// colour, and a flat colour is what makes every facet of the lighting visible --
        /// half of what reads as blocky is the absence of anything else to look at.
        /// </summary>
        void ApplyDiffuse()
        {
            if (!Imported || _groundMaterial == null) return;

            _surfaceTexture = TerrainImport.BakeDiffuse(
                _source, SampleOrigin, SampleStride,
                CellsX * CellSize, CellsZ * CellSize, DiffuseResolution, GroundColour);

            if (_surfaceTexture == null) return;

            float spanX = CellsX * CellSize;
            float spanZ = CellsZ * CellSize;

            // World XZ into [0,1] across the world, matching the UVs both meshers emit.
            var scale = new Vector2(1f / spanX, 1f / spanZ);
            var offset = new Vector2(-_cells.Origin.x / spanX, -_cells.Origin.z / spanZ);

            ApplyTexture(_groundMaterial, scale, offset);
        }

        void ApplyTexture(Material m, Vector2 scale, Vector2 offset)
        {
            if (m == null) return;

            if (m.HasProperty("_BaseMap"))
            {
                m.SetTexture("_BaseMap", _surfaceTexture);
                m.SetTextureScale("_BaseMap", scale);
                m.SetTextureOffset("_BaseMap", offset);
            }

            if (m.HasProperty("_MainTex"))
            {
                m.SetTexture("_MainTex", _surfaceTexture);
                m.SetTextureScale("_MainTex", scale);
                m.SetTextureOffset("_MainTex", offset);
            }

            Color white = Color.white;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", white);
            if (m.HasProperty("_Color")) m.SetColor("_Color", white);
        }

        /// <summary>
        /// Where to drive the adit.
        ///
        /// On the demo hill this is hand-placed and checked: the mountain sits at a known
        /// spot and the mouth lands where it should. On imported ground nothing can be
        /// assumed, so the site is read out of the heightfield -- the flat-ish shelf with the
        /// steepest rise in front of it, which is what a portal wants and is roughly how
        /// anyone would choose it by eye.
        ///
        /// The adit always runs east, so the search only looks that way.
        /// </summary>
        void ChooseAditSite(out float x, out float z, out float floor)
        {
            if (!Imported)
            {
                x = 8f;
                z = -15.5f;
                floor = 5f;
                return;
            }

            const int Run = 8;        // how far in the tunnel drives
            const int Width = 2;      // cells either side that must share the shelf

            float bestScore = 0f;
            int bestCx = -1, bestCz = -1;

            for (int cz = Width; cz < _cells.CellsZ - Width; cz++)
            {
                for (int cx = Width; cx < _cells.CellsX - Run - 1; cx++)
                {
                    float baseHeight = _cells.GetMetres(cx, cz);

                    // A bench has to be cut somewhere the ground is not already a cliff.
                    float flatness = 0f;
                    for (int d = -Width; d <= Width; d++)
                        flatness = Mathf.Max(flatness,
                            Mathf.Abs(_cells.GetMetres(cx, cz + d) - baseHeight));

                    if (flatness > 1.5f) continue;

                    // And the rise has to be monotonic, or the tunnel surfaces halfway in.
                    float rise = 0f;
                    bool climbs = true;

                    for (int d = 1; d <= Run; d++)
                    {
                        float h = _cells.GetMetres(cx + d, cz);
                        if (h < baseHeight + rise - 0.5f) { climbs = false; break; }
                        rise = Mathf.Max(rise, h - baseHeight);
                    }

                    if (!climbs || rise < 4f) continue;

                    if (rise > bestScore) { bestScore = rise; bestCx = cx; bestCz = cz; }
                }
            }

            if (bestCx < 0)
            {
                Debug.LogWarning("[Hybrid] no slope on the imported terrain was steep enough " +
                                 "for an adit, so none was cut. Mining still works -- dig into " +
                                 "any hillside by hand.");

                x = _cells.Origin.x + _cells.CellsX * 0.5f;
                z = _cells.Origin.z + _cells.CellsZ * 0.5f;
                floor = CellSurface.MetresAtWorld(_cells, x, z) + 0.2f;
                return;
            }

            x = _cells.Origin.x + bestCx + 0.5f;
            z = _cells.Origin.z + bestCz + 0.5f;
            floor = _cells.GetMetres(bestCx, bestCz) + 0.2f;

            Debug.LogFormat("[Hybrid] adit sited at ({0:0.#}, {1:0.#}), floor {2:0.##} m, " +
                            "with {3:0.#} m of rise ahead of it", x, z, floor, bestScore);
        }

        void CarveMine()
        {
            ChooseAditSite(out PortalX, out PortalZ, out PortalFloor);

            // The ore sits around the far end of the drift, so following it is the reason to
            // keep digging rather than stopping at the first rock.
            // Kept as the fallback shape for when the field is switched off; the field
            // itself needs no placing, since it already covers everywhere.
            _world.OreFootprint = new Rect(PortalX + 3.5f, PortalZ - 2f, 5f, 8f);
            _world.OreTopMetres = PortalFloor + 0.4f;
            _world.OreBottomMetres = PortalFloor - 2.6f;

            CutPortal();

            // Floor level with the bench outside, so you walk in rather than drop in.
            Corridor(new Vector3(PortalX, PortalFloor, PortalZ),
                     new Vector3(PortalX + 7f, PortalFloor, PortalZ), 2.2f);

            // A drift turning north and descending through the ore, ending blind.
            Corridor(new Vector3(PortalX + 5.5f, PortalFloor, PortalZ),
                     new Vector3(PortalX + 5.5f, PortalFloor - 1.8f, PortalZ + 8f), 2.2f);
        }

        float PortalX;
        float PortalZ;
        float PortalFloor;

        /// <summary>
        /// The bench the adit starts from, cut with the surface model rather than the span
        /// one.
        ///
        /// A level adit driven straight into a rising slope has no usable mouth: the roof
        /// only gets thick enough well inside the hill, so the entrance ends up buried and
        /// you drop into it. Real mines answer this by cutting a bench first and starting
        /// the tunnel from its face, and that is the division of labour the whole hybrid is
        /// built on -- the shovel shapes open ground, the pick goes underground.
        ///
        /// The cells are flattened, so the face is a genuine vertical wall drawn by the
        /// surface mesher. The mouth is a hole punched in that wall by the span mesher. If
        /// the seam is going to fail anywhere it is here.
        /// </summary>
        void CutPortal()
        {
            for (int cz = 0; cz < _cells.CellsZ; cz++)
            {
                for (int cx = 0; cx < _cells.CellsX; cx++)
                {
                    float wx = _cells.Origin.x + cx + 0.5f;
                    float wz = _cells.Origin.z + cz + 0.5f;

                    if (wx < PortalX - 7f || wx > PortalX) continue;
                    if (wz < PortalZ - 2f || wz > PortalZ + 2f) continue;

                    // Only ever cut down. Where the ground is already below the bench it is
                    // left as it is, so the approach stays a natural slope up to the face.
                    if (_cells.GetMetres(cx, cz) <= PortalFloor) continue;

                    _cells.SetRaw(cx, cz, CellGrid.ToRaw(PortalFloor));
                    _cells.SetFlat(cx, cz, true);
                }
            }
        }

        void Corridor(Vector3 from, Vector3 to, float size)
        {
            float length = Vector3.Distance(from, to);
            int steps = Mathf.CeilToInt(length / (ColumnSize * 0.5f));

            for (int i = 0; i <= steps; i++)
            {
                Vector3 at = Vector3.Lerp(from, to, i / (float)steps);

                int cx, cz;
                ColumnAt(at.x, at.z, out cx, out cz);
                if (!_world.Spans.InBounds(cx, cz)) continue;

                // Half a metre of cover, or this is the mouth and the tunnel stops.
                float ceiling = at.y + size * 0.5f;
                if (SpanGrid.ToMetres(_world.SurfaceMmAt(cx, cz)) < ceiling + 0.5f) continue;

                _world.Mine(new Vector3(at.x, at.y + size * 0.5f - 0.001f, at.z), size, 0, false);
            }
        }

        /// <summary>
        /// How far the ground a ceded brick DRAWS departs from the ground the surface mesher
        /// would have drawn for the same cells.
        ///
        /// Measured, not asserted, and measured inside the columns rather than only at their
        /// corners -- the corners agree by construction, so checking them proves nothing. The
        /// interesting places are where a cell's fan creases: at four columns per cell those
        /// creases run along column diagonals, and a cap folded the wrong way flattens one.
        ///
        /// The claim is that this reads zero. If it ever does not, the fold rule in
        /// HybridWorld.SwapCapDiagonal is the first thing to look at.
        /// </summary>
        void CheckSeam()
        {
            float worst = 0f;
            int sampled = 0;

            for (int bz = 0; bz < _world.BricksZ; bz++)
            {
                for (int bx = 0; bx < _world.BricksX; bx++)
                {
                    if (!_world.BrickCeded(bx, bz)) continue;

                    int x0 = bx * _world.ColumnsPerBrick;
                    int z0 = bz * _world.ColumnsPerBrick;

                    for (int j = 0; j < _world.ColumnsPerBrick; j++)
                    {
                        for (int i = 0; i < _world.ColumnsPerBrick; i++)
                        {
                            worst = Mathf.Max(worst, CapDeparture(x0 + i, z0 + j));
                            sampled++;
                        }
                    }
                }
            }

            SeamError = worst;
            Debug.LogFormat(
                "[Hybrid] ceded surface departs from the original by at most {0:0.000000} m " +
                "over {1} columns in {2} bricks",
                worst, sampled, _world.CededBricks);
        }

        static readonly Vector2[] Samples =
        {
            new Vector2(0.5f, 0.5f),
            new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.25f),
            new Vector2(0.25f, 0.75f), new Vector2(0.75f, 0.75f),
            new Vector2(0.5f, 0.2f),   new Vector2(0.2f, 0.5f),
            new Vector2(0.8f, 0.5f),   new Vector2(0.5f, 0.8f),
        };

        float CapDeparture(int colX, int colZ)
        {
            if (!_world.Spans.InBounds(colX, colZ)) return 0f;

            float c00 = _world.CornerMetres(colX, colZ, 0, 0);
            float c10 = _world.CornerMetres(colX, colZ, 1, 0);
            float c11 = _world.CornerMetres(colX, colZ, 1, 1);
            float c01 = _world.CornerMetres(colX, colZ, 0, 1);

            bool swap = _world.SwapCapDiagonal(colX, colZ);

            float k = 1f / _world.ColumnsPerCell;
            float u0 = colX / (float)_world.ColumnsPerCell;
            float v0 = colZ / (float)_world.ColumnsPerCell;

            float worst = 0f;

            for (int i = 0; i < Samples.Length; i++)
            {
                float u = Samples[i].x;
                float v = Samples[i].y;

                float drawn = Interpolate(c00, c10, c11, c01, u, v, swap);
                float truth = CellSurface.MetresAt(_cells, u0 + u * k, v0 + v * k);

                worst = Mathf.Max(worst, Mathf.Abs(drawn - truth));
            }

            return worst;
        }

        /// <summary>The cap as the mesher actually triangulates it, folded either way.</summary>
        static float Interpolate(float c00, float c10, float c11, float c01,
                                 float u, float v, bool swap)
        {
            if (!swap)
            {
                return v <= u
                    ? c00 * (1f - u) + c10 * (u - v) + c11 * v
                    : c00 * (1f - v) + c11 * u + c01 * (v - u);
            }

            return u + v <= 1f
                ? c00 * (1f - u - v) + c10 * u + c01 * v
                : c11 * (u + v - 1f) + c10 * (1f - v) + c01 * (1f - u);
        }

        // ---- scene furniture ---------------------------------------------------

        void BuildMaterials()
        {
            _groundMaterial = Lit(GroundColour);

            _spanMaterials = new Material[SpanMaterials.Count];
            for (int i = 0; i < _spanMaterials.Length; i++)
            {
                // Topsoil is the material a ceded patch of open ground draws with, so it has
                // to be the SAME material the surface uses. Solving the seam geometrically
                // and then handing it back as a colour change would be no solution at all.
                _spanMaterials[i] = i == SpanMaterials.Topsoil
                    ? _groundMaterial
                    : Lit(SpanMaterials.Colours[i]);
            }
        }

        static Material Lit(Color colour)
        {
            Material m = MaterialLibrary.Build("hybrid", MaterialLibrary.Lit, MaterialLibrary.LitShaders);
            if (m == null) return null;

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
            if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.03f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);

            return m;
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

            return player;
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

            cam.transform.SetParent(_head, false);
            cam.transform.localPosition = Vector3.zero;
            cam.transform.localRotation = Quaternion.identity;
            cam.nearClipPlane = 0.05f;

            return cam;
        }

        void GoTo(int index)
        {
            if (_player == null || _views == null) return;

            _view = ((index % _views.Length) + _views.Length) % _views.Length;
            ViewPoint view = _views[_view];

            int cx, cz;
            ColumnAt(view.At.x, view.At.z, out cx, out cz);

            float ground = view.At.y;

            if (_world.Spans.InBounds(cx, cz))
            {
                int open = _world.Spans.OpenTopNear(cx, cz, SpanGrid.ToMm(view.At.y));
                if (open != int.MinValue) ground = SpanGrid.ToMetres(open);
                else ground = CellSurface.MetresAtWorld(_cells, view.At.x, view.At.z);
            }

            var cc = _player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            _player.enabled = false;
            _player.transform.position = new Vector3(view.At.x, ground + 0.4f, view.At.z);
            _player.transform.rotation = Quaternion.Euler(0f, view.Yaw, 0f);
            _player.enabled = true;

            if (cc != null) cc.enabled = true;
        }

        /// <summary>
        /// Hand the controller whichever surface is actually underfoot. Two colliders own
        /// the ground between them and the player crosses from one to the other without
        /// anything happening, which is the whole point.
        /// </summary>
        void BindColliders()
        {
            if (_player == null) return;

            Vector3 p = _player.transform.position;

            int cx, cz;
            ColumnAt(p.x, p.z, out cx, out cz);

            int bx = _world.BrickOfColumn(Mathf.Clamp(cx, 0, _world.Spans.ColumnsX - 1));
            int bz = _world.BrickOfColumn(Mathf.Clamp(cz, 0, _world.Spans.ColumnsZ - 1));

            Brick brick;
            if (_world.BrickCeded(bx, bz) && _bricks.TryGetValue(_world.BrickIndex(bx, bz), out brick))
                _player.TerrainCollider = brick.Collider;
            else
                _player.TerrainCollider = _surfaceCollider;
        }

        void UpdateLamp()
        {
            if (_lamp == null || _player == null) return;

            Vector3 p = _player.transform.position;

            int cx, cz;
            ColumnAt(p.x, p.z, out cx, out cz);

            bool underground = _world.Spans.InBounds(cx, cz)
                && _world.Spans.SolidAbove(cx, cz, SpanGrid.ToMm(p.y + 0.2f));

            _lamp.enabled = underground;
        }

        void ColumnAt(float x, float z, out int cx, out int cz)
        {
            cx = Mathf.FloorToInt((x - _world.Spans.Origin.x) / _world.Spans.ColumnSize);
            cz = Mathf.FloorToInt((z - _world.Spans.Origin.z) / _world.Spans.ColumnSize);
        }
    }
}
