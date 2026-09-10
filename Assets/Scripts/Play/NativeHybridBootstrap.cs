using System.Collections.Generic;
using UnityEngine;
using Terraform.Core;
using Terraform.Span;
using Terraform.View;

namespace Terraform.Play
{
    /// <summary>
    /// The hybrid again, with Unity's own Terrain drawing the surface.
    ///
    /// Everything below the surface is shared with the custom-mesh scene: the same span
    /// store, the same ceding rule, the same seam, the same mesher. Only the surface output
    /// changes. That is deliberate -- if the two modes shared no code, the difference between
    /// them would include every incidental difference in how they were written.
    ///
    /// What native Terrain brings is the reason to try it: splatmap texturing, detail and
    /// tree rendering, terrain LOD, and a native collider. Those systems are most of what
    /// separates a prototype hillside from a finished-looking one.
    ///
    /// What it costs is the vertical face. A heightfield stores one height per vertex, so two
    /// flat areas at different heights cannot sit directly against each other -- the vertices
    /// along their shared edge would have to hold two values at once. Here that is the
    /// requirement rather than a limitation: transitions slope, and the rule is structural
    /// instead of enforced.
    ///
    /// Openings use TerrainData.SetHoles. A hole sample covers exactly one cell, and the
    /// terrain stops on cell boundaries -- straight lines between heightmap samples, which
    /// the span mesher samples too. So the seam is exact at full resolution by construction.
    /// The risk is LOD: raise the pixel error and the terrain moves its vertices while the
    /// patch does not. That is the thing to watch, and it only shows from a distance.
    /// </summary>
    public sealed class NativeHybridBootstrap : MonoBehaviour, IWorldBootstrap
    {
        [Header("World")]
        public float WorldMetres = 64f;

        /// <summary>
        /// Heightmap spacing, and the scale a click works at, in one number because on a
        /// heightfield the VERTEX is the unit of work and there is nothing to decouple.
        ///
        /// Finer sampling was tried and abandoned. It makes a vertex too small to aim at, so
        /// the tool has to address a cell instead -- and a cell does not own its vertices.
        /// Every attempt to make that work added machinery: a block write, then weights to
        /// stop the shared boundary being written twice, then the discovery that a quarter
        /// weight on a two-unit step rounds to zero and corrugates every cell edge. All of it
        /// was scaffolding around using the wrong unit. Addressing the vertex needs none of
        /// it: one click, one point, the full step, and a volume that is exactly the vertex
        /// area times the movement.
        ///
        /// Finer sampling is still the right call underground, where nobody aims at a column.
        /// </summary>
        public float CellSize = 1f;

        public float StartHeight = 4f;
        public float FloorMetres = 0f;
        public float TerrainHeightRange = 64f;

        /// <summary>
        /// Underground storage spacing, cycled with C.
        ///
        /// Static, not a serialized field: Unity bakes public field values into a scene when
        /// the component is added, so a default changed in code leaves an existing scene on
        /// the old one. That has already cost this project two debugging rounds.
        ///
        /// 1 m is the true-cube look -- one column per square metre, which is what the
        /// reference tunnels are. 0.25 m is sixteen per square metre: finer control and a
        /// smaller step underfoot, at the cost of reading as smaller blocks.
        /// </summary>
        public static readonly float[] ColumnSizes = { 1f, 0.5f, 0.25f };

        /// <summary>
        /// Every runtime toggle below is NonSerialized on purpose.
        ///
        /// Unity writes a public field into the scene the moment a component is added, so a
        /// default changed in code leaves an existing scene running whatever it was first
        /// given. That has cost this project three separate debugging rounds -- a missing
        /// column size, a stale sampling spacing, a grid drawn five times too wide -- every
        /// one of them presenting as odd behaviour rather than as an error. These are cycled
        /// by keypress during play and there is nothing worth persisting in them, so the code
        /// default is simply always the truth.
        /// </summary>
        [System.NonSerialized] public int ColumnSizeIndex = 0;
        public float ColumnSize { get { return ColumnSizes[Mathf.Clamp(ColumnSizeIndex, 0, ColumnSizes.Length - 1)]; } }

        public float BrickMetres = 4f;

        /// <summary>
        /// Cut a demonstration adit and drift on load.
        ///
        /// Worth having while the only ground was a generated hill and there was nothing to
        /// look at otherwise. On real imported terrain it is in the way -- the world should
        /// start as authored, and anything underground should be there because somebody dug
        /// it.
        /// </summary>
        public bool CarveTestMine;

        /// <summary>
        /// Stands in for the seed a server would roll once when a world is created and keep
        /// to itself. Fixed here so a run is reproducible; zero means pick a fresh one, which
        /// is what shows whether the distribution holds up across worlds rather than only on
        /// the one it was tuned against.
        /// </summary>
        public uint OreSeed = 20260909u;

        

        [Header("Ground source")]
        public Vector2 SampleOrigin = Vector2.zero;
        public float SampleStride = 1f;
        public float SampleFloorMetres = 4f;
        public int DiffuseResolution = 1024;

        /// <summary>
        /// Words to look for in the source terrain textures, one per span material.
        ///
        /// Matched by NAME rather than by index, because layer order is whatever the
        /// generator happened to choose and an index that is right for one terrain is
        /// arbitrary for the next. Names survive regeneration; positions do not.
        /// </summary>
        public string TopsoilMatch = "grass";
        public string SubsoilMatch = "sand";
        public string RockMatch = "rock";

        /// <summary>Metres per texture tile on dug faces.</summary>
        public float DugTextureScale = 2f;
        public bool Imported { get; private set; }

        [Header("Tools")]
        public float Reach = 5f;
        public float EyeHeight = 1.62f;

        public static readonly float[] BiteSizes = { 1f, 0.5f, 0.25f };

        [System.NonSerialized] public int BiteSizeIndex = 0;

        /// <summary>
        /// Take everything in the bite rather than only the material aimed at.
        ///
        /// Off by default because a full cell of mixed ground is what a swing actually
        /// removes; picking one material out of a face is the special case, not the norm.
        /// </summary>
        [System.NonSerialized] public bool Selective;

        /// <summary>
        /// Take the whole cell across, however deep the bite is.
        ///
        /// A terrain hole is one heightmap cell and cannot be smaller. Dig a quarter-metre
        /// shaft and the terrain still has to drop a full square metre, leaving three
        /// quarters of it standing but drawn by something else -- which is why the ground
        /// changed appearance in a ring around every opening. Cutting the whole cell makes
        /// what is removed and what stops being drawn the same thing.
        ///
        /// Depth stays as fine as the bite: a swing is a layer off a cell, not a cube.
        /// </summary>
        [System.NonSerialized] public bool SquareToCells = true;

        public enum Job { Terraform, Mine }
        public Job Doing { get; private set; }

        public enum SurfaceTool { Sculpt, Flatten }
        public SurfaceTool Tool { get; private set; }

        /// <summary>
        /// The readout panel. Off is the interesting state: every judgement about how the
        /// ground LOOKS has been made with a wall of numbers covering a third of the screen,
        /// and that is not the view anyone will play in.
        /// </summary>
        [System.NonSerialized] public bool ShowPanel = true;

        [System.NonSerialized] public bool ShowHeights = true;

        /// <summary>
        /// Cubes on the vertices. On by default while terraforming: numbers say what the
        /// ground is doing, cubes show it, and on textured ground a tenth of a metre is
        /// invisible without them.
        /// </summary>
        [System.NonSerialized] public bool ShowMarkers = true;

        public float LabelRange = 14f;

        public static readonly int[] StepChoices = { 1, 2, 5, 10, 20 };

        [System.NonSerialized] public int StepIndex = 1;

        /// <summary>
        /// Steepest ground allowed, as metres of rise per metre of run. 3.0 is about 72
        /// degrees: near-vertical, which is as close to a wall as this design wants.
        ///
        /// A SLOPE, not a height. Storing the limit as a fixed step between neighbouring
        /// vertices is the same trap the smoothing threshold fell into: 3 m between samples
        /// a metre apart is 72 degrees, and between samples a quarter-metre apart it is 85 --
        /// so refining the heightmap silently let the ground get four times steeper, and
        /// repeated clicks built towers and canyons instead of mounds.
        /// </summary>
        public float MaxSlope = 3f;

        /// <summary>
        /// 0 leaves a sharp point where you clicked. Above that, the ring around it relaxes
        /// toward its own neighbours, rounding the apex. Off by default: the point of
        /// addressing a vertex is that the vertex you clicked is the one that moves.
        /// </summary>
        public float Rounding;

        /// <summary>The slope above, converted to a step between two adjacent samples.</summary>
        public int MaxStepUnits
        {
            get { return Mathf.Max(1, Mathf.RoundToInt(MaxSlope * CellSize * HeightGrid.UnitsPerMetre)); }
        }

        // ---- readouts ----
        public float BuildMs { get; private set; }
        public float CookMs { get; private set; }
        public float SurfaceMs { get; private set; }
        public int EditBricks { get; private set; }
        public string Note { get; private set; }
        public int Triangles { get; private set; }
        public float CutFill { get; private set; }

        /// <summary>
        /// What the last click actually moved. Worth showing, because it is not what anyone
        /// expects: a shared vertex lifts the ground on both sides of it, so raising one cell
        /// by a tenth of a metre costs more than a tenth of a cubic metre. How much more is
        /// set by CellSize, and this is the number that proves it.
        /// </summary>
        public float LastVolume { get; private set; }

        public HybridWorld World { get { return _world; } }
        public HeightGrid Grid { get { return _grid; } }
        public UnityTerrainView TerrainView { get { return _terrain; } }
        public Camera Cam { get { return _cam; } }
        public float BiteSize { get { return BiteSizes[Mathf.Clamp(BiteSizeIndex, 0, BiteSizes.Length - 1)]; } }
        public int StepUnits { get { return StepChoices[Mathf.Clamp(StepIndex, 0, StepChoices.Length - 1)]; } }
        public bool HasTarget { get; private set; }
        public int TargetCx { get; private set; }
        public int TargetCz { get; private set; }

        /// <summary>The vertex sculpt moves. Flatten works on the cell instead.</summary>
        public int TargetVx { get; private set; }
        public int TargetVz { get; private set; }
        public float TargetVertexHeight
        {
            get { return _grid == null ? 0f : _grid.GetMetres(TargetVx, TargetVz); }
        }
        public float SeamError { get; private set; }

        /// <summary>Surface layer the last soil profile was derived from. Diagnostic.</summary>
        public string LastProfileName { get; private set; }

        /// <summary>What the terrain is painted with under the crosshair, and what that implies.</summary>
        public string GroundReport()
        {
            if (_drawnAlpha == null) return "no splatmap - strata are uniform";
            if (!HasTarget) return null;

            float wx = _grid.Origin.x + (TargetCx + 0.5f) * CellSize;
            float wz = _grid.Origin.z + (TargetCz + 0.5f) * CellSize;

            string dominant = TerrainImport.DominantOn(_terrain.Terrain, _drawnAlpha, wx, wz);
            if (string.IsNullOrEmpty(dominant)) return "surface unpainted here";

            int colX = Mathf.Clamp(TargetCx * _world.ColumnsPerCell, 0, _world.Spans.ColumnsX - 1);
            int colZ = Mathf.Clamp(TargetCz * _world.ColumnsPerCell, 0, _world.Spans.ColumnsZ - 1);

            Vector2 profile = SoilProfileAt(colX, colZ);

            return string.Format("{0} -> topsoil {1:0.##} m, subsoil {2:0.##} m",
                                 Shorten(dominant), profile.x, profile.y);
        }

        static string Shorten(string name)
        {
            if (string.IsNullOrEmpty(name)) return "-";

            if (name.Contains("rock")) return "rock";
            if (name.Contains("grass")) return "grass";
            if (name.Contains("sand")) return "sand";

            return name;
        }
        public string ViewName { get { return _views == null ? "-" : _views[_view].Name; } }

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

        ViewPoint[] _views;

        sealed class Brick
        {
            public GameObject Go;
            public Mesh Mesh;
            public MeshCollider Collider;
            public MeshRenderer Renderer;
            public int Triangles;
        }

        HeightGrid _grid;
        HybridWorld _world;
        UnityTerrainView _terrain;
        CommandLog _log;

        GameObject _spanRoot;
        readonly Dictionary<int, Brick> _bricks = new Dictionary<int, Brick>();
        readonly HashSet<int> _holed = new HashSet<int>();
        Material[] _spanMaterials;
        Material _groundMaterial;

        FirstPersonController _player;
        Transform _head;
        Camera _cam;
        SkyController _sky;
        PauseMenu _menu;
        Light _sun;
        Light _lamp;
        TerrainImport.Source _source;
        TerrainImport.Splat _splat;

        /// <summary>
        /// Our own terrain's splatmap, read once after the layers are applied.
        ///
        /// The strata are derived from THIS rather than from the source terrain, and the
        /// difference matters more than it looks. Reading the source means mapping our world
        /// back into its coordinates -- a second copy of the window arithmetic that already
        /// exists in the resample. Two copies agreed on paper and disagreed in practice, and
        /// the ground ended up made of something other than what it was painted with.
        ///
        /// What is drawn is what the player sees, so what is drawn is what the ground should
        /// be made of. Asking the drawn map directly needs no mapping at all and cannot drift.
        /// </summary>
        float[,,] _drawnAlpha;
        Texture2D _surfaceTexture;
        readonly List<Texture2D> _oreTextures = new List<Texture2D>();
        VertexMarkers _markers;

        int _view;
        float PortalX, PortalZ, PortalFloor;

        static readonly Color GroundColour = new Color(0.36f, 0.44f, 0.28f);

        void Awake()
        {
            SpanMeshBuilder.Surface = null;
            SpanMeshBuilder.Smooth = true;

            EnsureLight();
            BuildMaterials();
            BuildSurface();

            if (_terrain == null || !_terrain.Valid)
            {
                Debug.LogError("[NativeHybrid] Unity Terrain would not initialise for this grid. " +
                               "Nothing else in this scene can work without it.");
                enabled = false;
                return;
            }

            BuildWorld();

            _player = BuildPlayer();
            _cam = AttachCamera();

            _sky = gameObject.AddComponent<SkyController>();
            _sky.Initialise(_sun, _cam);

            var markerGo = new GameObject("VertexMarkers");
            markerGo.transform.SetParent(transform, false);

            _markers = markerGo.AddComponent<VertexMarkers>();
            // Width deliberately NOT exposed here. It was a serialized field, so the value
            // baked into the scene when it still meant cube size kept overriding every later
            // default -- the third time a public field has quietly outlived its own meaning
            // in this project. VertexMarkers is added at runtime, so its own default always
            // applies.
            _markers.Range = LabelRange;
            // Muted. The grid is there to be read past, not looked at -- bright white lines
            // over a photographed hillside draw the eye to the overlay instead of the ground
            // it is supposed to be describing.
            _markers.Initialise(_grid, _head,
                                Unlit(new Color(0.62f, 0.66f, 0.70f, 1f)),
                                Unlit(new Color(0.40f, 0.85f, 0.50f, 1f)));

            gameObject.AddComponent<NativeHybridHud>().Scene = this;
            _menu = gameObject.AddComponent<PauseMenu>();

            GoTo(0);

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        /// <summary>
        /// Build, or rebuild, everything below the surface. Called again when the column size
        /// changes, which is the only way to compare the two looks on the same ground --
        /// spacing is fixed when the span grid is allocated, so it cannot be swapped in place.
        /// Anything already mined is lost, which is the honest cost of the comparison.
        /// </summary>
        void BuildWorld()
        {
            if (_spanRoot != null) Destroy(_spanRoot);

            _bricks.Clear();
            _holed.Clear();
            if (_terrain != null) _terrain.ClearHoles();

            _world = new HybridWorld(new VertexGround(_grid), ColumnSize,
                                     Mathf.Max(1, Mathf.RoundToInt(BrickMetres / CellSize)),
                                     FloorMetres);

            // The terrain keeps drawing every cell it has not been holed for, so the spans
            // must not draw ground on top of it.
            // What the surface is painted with decides what lies under it. Without this the
            // strata are the same everywhere, so a rock face reads as rock from above and
            // turns to grass and sand as soon as anyone cuts into it.
            _world.SoilProfile = SoilProfileAt;

            _world.SurfaceCoveredElsewhere = (colX, colZ) =>
            {
                int per = _world.ColumnsPerCell;
                return !CellDugOpen(colX / per, colZ / per);
            };

            SpanMeshBuilder.Surface = _world;

            _spanRoot = new GameObject("SpanWorld");
            _spanRoot.transform.SetParent(transform, false);

            // Sited either way: the viewpoints need somewhere to stand, and the ore body is
            // placed relative to it whether or not a demonstration mine is cut.
            ChooseAditSite();
            PlaceOreBody();
            BuildOreField();
            FindDeposits();

            if (CarveTestMine) CarveMine();

            BuildViews();
            CheckSeam();
            FlushDirty();
            SyncHoles();
        }

        void RebuildBricks()
        {
            // Snapshotted: BuildBrick inserts when a brick is new, and adding to a
            // dictionary while enumerating it throws.
            var keys = new List<int>(_bricks.Keys);

            foreach (int index in keys)
            {
                float build, cook;
                BuildBrick(index % _world.BricksX, index / _world.BricksX, out build, out cook);
            }

            Triangles = 0;
            foreach (Brick brick in _bricks.Values) Triangles += brick.Triangles;
        }

        void OnDestroy()
        {
            SpanMeshBuilder.Surface = null;

            // Runtime-created, so nothing else will free them.
            for (int i = 0; i < _oreTextures.Count; i++)
                if (_oreTextures[i] != null) Destroy(_oreTextures[i]);

            _oreTextures.Clear();
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

            if (InputCompat.Tool1Pressed) Tool = SurfaceTool.Sculpt;
            if (InputCompat.Tool2Pressed) Tool = SurfaceTool.Flatten;

            if (InputCompat.BrushUpPressed) BiteSizeIndex = Mathf.Min(BiteSizeIndex + 1, BiteSizes.Length - 1);
            if (InputCompat.BrushDownPressed) BiteSizeIndex = Mathf.Max(BiteSizeIndex - 1, 0);

            if (InputCompat.SpanResolutionPressed)
            {
                ColumnSizeIndex = (ColumnSizeIndex + 1) % ColumnSizes.Length;
                BuildWorld();
                GoTo(_view);
            }

            if (InputCompat.SmoothPressed)
            {
                SpanMeshBuilder.Smooth = !SpanMeshBuilder.Smooth;
                RebuildBricks();
            }

            if (InputCompat.MarkersPressed) ShowMarkers = !ShowMarkers;
            if (InputCompat.SquarePressed) SquareToCells = !SquareToCells;
            if (InputCompat.DepositPressed) GoToDeposit();
            if (InputCompat.PanelPressed) ShowPanel = !ShowPanel;

            if (_markers != null)
            {
                _markers.Show = ShowMarkers && Doing == Job.Terraform;
                if (HasTarget) _markers.SetTarget(TargetVx, TargetVz);
            }

            if (InputCompat.SkyPressed && _sky != null) _sky.Next();
            if (InputCompat.HolePressed) GoTo(_view + 1);

            // The LOD test. Raising pixel error simplifies the terrain but not the ceded
            // patches, so if the seam is going to open it opens here -- and only at a
            // distance, which is exactly where a close-up screenshot would miss it.
            if (InputCompat.PixelErrorUpPressed) _terrain.PixelError = Mathf.Min(_terrain.PixelError + 5f, 200f);
            if (InputCompat.PixelErrorDownPressed) _terrain.PixelError = Mathf.Max(_terrain.PixelError - 5f, 1f);

            if (InputCompat.UndoPressed && _log.Undo(_grid)) AfterSurfaceEdit();

            AimTarget();

            if (Doing == Job.Terraform)
            {
                if (InputCompat.LeftPressed) Terraform(+1);
                if (InputCompat.RightPressed) Terraform(-1);
            }
            else if (InputCompat.LeftPressed) Mine();

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
            if (!Physics.Raycast(ray, out hit, Reach)) return;

            int cx, cz;
            if (!InteractionCellAt(hit.point, out cx, out cz)) return;

            int vx, vz;
            if (!_grid.WorldToNearestVertex(hit.point, out vx, out vz)) return;

            HasTarget = true;
            TargetCx = cx;
            TargetCz = cz;
            TargetVx = vx;
            TargetVz = vz;
        }

        /// <summary>
        /// Interaction cell under a world point -- the scale a click targets, coarser than
        /// the heightmap beneath it.
        /// </summary>
        public bool InteractionCellAt(Vector3 world, out int cx, out int cz)
        {
            cx = Mathf.FloorToInt((world.x - _grid.Origin.x) / CellSize);
            cz = Mathf.FloorToInt((world.z - _grid.Origin.z) / CellSize);

            int span = InteractionCells;
            return cx >= 0 && cz >= 0 && cx < span && cz < span;
        }

        public int InteractionCells { get { return Mathf.RoundToInt(WorldMetres / CellSize); } }

        /// <summary>Height at the middle of an interaction cell.</summary>
        public float InteractionHeight(int cx, int cz)
        {
            return VertexSurface.MetresAt(_grid, cx + 0.5f, cz + 0.5f);
        }

        /// <summary>Whether the spans own the ground under an interaction cell.</summary>
        public bool InteractionCeded(int cx, int cz)
        {
            return _world.CellCeded(cx, cz);
        }

        // ---- surface -----------------------------------------------------------

        /// <summary>
        /// One cell of ground, moved or levelled.
        ///
        /// A cell is four shared vertices, so an edit here always reaches into the cells
        /// beside it. That is the honest cost of a heightfield and the reason two flat areas
        /// at different heights cannot abut: the shared vertices can only hold one value, so
        /// levelling one cell necessarily tilts its neighbours into a slope.
        ///
        /// Volume is measured over every cell whose corners moved, from the same diagonal the
        /// mesh and the terrain both use, so the ledger matches the ground rather than
        /// approximating it.
        /// </summary>
        void Terraform(int direction)
        {
            Note = null;

            if (!HasTarget) { Note = "nothing within reach"; return; }

            // Sculpt moves ONE vertex. That is the whole operation: no block, no weights,
            // no shared boundary to reconcile, and a volume that is exactly the vertex area
            // times the movement. Flatten is the only thing that works on a cell, because
            // levelling is inherently about an area rather than a point.
            int vx = TargetVx, vz = TargetVz;
            int rx0, rz0, rx1, rz1;

            ITerrainCommand command;

            if (Tool == SurfaceTool.Sculpt)
            {
                // The rect is the neighbourhood the optional rounding may relax, not the
                // thing being moved -- that is the single vertex (vx, vz). Clamped, because
                // a vertex on the boundary has no ring around it.
                int ox = Mathf.Max(0, vx - 1);
                int oz = Mathf.Max(0, vz - 1);
                int ex = Mathf.Min(_grid.CellsX, vx + 1);
                int ez = Mathf.Min(_grid.CellsZ, vz + 1);

                command = new VertexAdjustCommand(ox, oz, ex - ox + 1, ez - oz + 1,
                                                  vx, vz, direction * StepUnits,
                                                  MaxStepUnits, Rounding);

                // A vertex belongs to the four cells around it, and its own neighbours can be
                // pulled by the repose check, so the resync ring reaches one further.
                rx0 = vx - 2; rz0 = vz - 2; rx1 = vx + 1; rz1 = vz + 1;
            }
            else
            {
                float target = InteractionHeight(TargetCx, TargetCz);
                command = new FlattenAreaCommand(TargetCx, TargetCz, 2, 2, HeightGrid.ToRaw(target));

                rx0 = TargetCx - 2; rz0 = TargetCz - 2; rx1 = TargetCx + 2; rz1 = TargetCz + 2;
            }

            if (!_log.Execute(command, _grid))
            {
                float step = MaxSlope * CellSize;

                Note = string.Format(
                    "step limit - at most {0:0.##} m to a neighbour, {1:0.##} m diagonally. " +
                    "Raise the ring first.",
                    step, step * 1.41421356f);
                return;
            }

            if (!_world.ResyncCells(rx0, rz0, rx1, rz1))
            {
                _log.Undo(_grid);
                _world.ResyncCells(rx0, rz0, rx1, rz1);
                _world.ClearDirty();

                Note = string.Format("would leave {0:0.00} m of roof over a tunnel, minimum {1:0.00} m",
                                     _world.LastRoofMetres, HybridWorld.MinRoof);
                return;
            }

            // The command already measured what it moved, over the same triangulation the
            // mesh and the terrain use. Recomputing it here would be a second opinion that
            // could disagree.
            LastVolume = ((VertexRectCommand)command).SignedVolume;
            CutFill += LastVolume;

            AfterSurfaceEdit();
        }

        void AfterSurfaceEdit()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _terrain.Sync();
            SurfaceMs = (float)watch.Elapsed.TotalMilliseconds;

            FlushDirty();
        }

        /// <summary>
        /// Volume over every cell an edit at (cx,cz) can move, which is wider than the cell
        /// itself: moving a shared vertex changes the ground on both sides of it.
        /// </summary>
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

            float footprint = bite;

            if (SquareToCells)
            {
                int scx, scz;
                if (InteractionCellAt(at, out scx, out scz))
                {
                    at.x = _grid.Origin.x + (scx + 0.5f) * CellSize;
                    at.z = _grid.Origin.z + (scz + 0.5f) * CellSize;
                    footprint = CellSize;
                }
            }

            byte target = SpanMaterials.Rock;

            if (Selective)
            {
                int mx, mz;
                ColumnAt(at.x, at.z, out mx, out mz);

                _world.Cede(_world.BrickOfColumn(Mathf.Max(0, mx)), _world.BrickOfColumn(Mathf.Max(0, mz)));

                target = _world.Spans.MaterialAt(mx, mz, SpanGrid.ToMm(at.y));
                if (target == 255) { Note = "aimed at air"; return; }
            }

            // Digging on open ground follows the surface; digging into a face does not. The
            // ray says which: a swing that hit the terrain came from above, one that hit a
            // brick is already underground.
            bool fromSurface = hit.collider == (Collider)_terrain.TerrainCollider;

            float removed = _world.Mine(at, footprint, bite, target, Selective, fromSurface);

            Note = string.Format("{0} {1:0.000} m^3",
                Selective ? SpanMaterials.Names[target < SpanMaterials.Count ? target : 0] : "everything",
                removed);

            FlushDirty();

            // Always, not only when the ceded count moved. Breaking through to the sky inside
            // a brick the spans already owned changes no count, so gating on that left the
            // terrain covering the new opening -- a hole you had visibly dug and could not
            // see through. SyncHoles only uploads when something actually changed.
            SyncHoles();
        }

        // ---- holes -------------------------------------------------------------

        /// <summary>
        /// Punch the terrain wherever the spans have taken ownership.
        ///
        /// A hole sample covers exactly one cell, and ceding works in whole bricks, so the
        /// mask is always cell-aligned and the terrain always stops on a straight line
        /// between heightmap samples. The span mesher samples those same vertices, which is
        /// what makes the seam exact without either side knowing about the other.
        /// </summary>
        void SyncHoles()
        {
            bool changed = false;

            int per = _world.ColumnsPerCell;

            for (int bz = 0; bz < _world.BricksZ; bz++)
            {
                for (int bx = 0; bx < _world.BricksX; bx++)
                {
                    if (!_world.BrickCeded(bx, bz)) continue;

                    int c0 = bx * _world.CellsPerBrick;
                    int r0 = bz * _world.CellsPerBrick;

                    for (int j = 0; j < _world.CellsPerBrick; j++)
                    {
                        for (int i = 0; i < _world.CellsPerBrick; i++)
                        {
                            int cx = c0 + i;
                            int cz = r0 + j;

                            if (!CellDugOpen(cx, cz)) continue;

                            int key = cz * _world.Ground.CellsX + cx;
                            if (!_holed.Add(key)) continue;

                            _terrain.SetHole(cx, cz, false);
                            changed = true;
                        }
                    }
                }
            }

            if (changed) _terrain.CommitHoles();
        }

        /// <summary>
        /// Is any part of this cell actually dug through to the sky?
        ///
        /// Holes used to follow brick ownership, which punched sixteen cells the moment
        /// anyone opened a tunnel anywhere inside a brick -- including under ground nobody
        /// had touched. That is what made the terrain revert to a flatter, blurrier version
        /// of itself around every mine: the spans took over drawing ground that was still
        /// perfectly intact, using one baked texture instead of the terrain layers.
        ///
        /// A tunnel under an intact roof needs no hole at all.
        /// </summary>
        bool CellDugOpen(int cx, int cz)
        {
            int per = _world.ColumnsPerCell;

            // Strictly inside the cell. Columns are cells, not vertices, so index per is the
            // FIRST column of the next cell along -- including it made every dig report its
            // neighbour as open too, holing a square metre of untouched ground and handing it
            // to the blurry fallback skin. The patches always appeared exactly one cell over.
            for (int j = 0; j < per; j++)
                for (int i = 0; i < per; i++)
                    if (_world.ColumnOpen(cx * per + i, cz * per + j)) return true;

            return false;
        }

        // ---- meshing -----------------------------------------------------------

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

        // ---- construction ------------------------------------------------------

        void BuildSurface()
        {
            int cells = Mathf.RoundToInt(WorldMetres / CellSize);
            var origin = new Vector3(-WorldMetres * 0.5f, 0f, -WorldMetres * 0.5f);

            _grid = new HeightGrid(cells, cells, CellSize, origin, StartHeight);

            if (!UnityTerrainView.SupportsGrid(_grid))
            {
                Debug.LogErrorFormat(
                    "[NativeHybrid] {0} samples across gives a heightmap resolution of {1}, " +
                    "which Unity will not accept -- it must be a power of two plus one. " +
                    "Adjust WorldMetres or CellSize.", cells, cells + 1);
            }

            // Printed every run on purpose. Public fields are baked into a scene when the
            // component is first added, so changing a default in code does nothing to a scene
            // that already exists -- and the symptom is not an error, it is the tool quietly
            // behaving as it did last week. Saying the values out loud makes that visible in
            // one line instead of being inferred from the shape of the ground.
            Debug.LogFormat("[NativeHybrid] {0} m across at {1} m spacing, {2} vertices; " +
                            "max slope {3}:1 = {4} units between neighbours",
                            WorldMetres, CellSize, cells + 1, MaxSlope, MaxStepUnits);
            _log = new CommandLog();

            ApplySource();

            var root = new GameObject("NativeTerrain");
            root.transform.SetParent(transform, false);

            _terrain = root.AddComponent<UnityTerrainView>();
            _terrain.Initialise(_grid, TerrainHeightRange, GroundColour);

            ApplyDiffuse();
        }

        void ApplySource()
        {
            _source = TerrainImport.Capture();

            if (_source == null || !_source.Valid)
            {
                DemoTerrain.Apply(_grid, StartHeight);
                return;
            }

            SampleStride = Mathf.Max(0.01f, SampleStride);

            float offset = TerrainImport.FitOffset(_source, SampleOrigin, SampleStride,
                                                   _grid.CellsX, _grid.CellsZ, CellSize,
                                                   SampleFloorMetres);

            TerrainImport.Apply(_grid, _source, SampleOrigin, SampleStride, offset);

            // Built before the source is hidden, though it would work either way -- the data
            // lives on the component, not on whether it renders.
            _splat = TerrainImport.Splat.Build(_source);

            if (_splat != null)
            {
                var names = new System.Collections.Generic.List<string>();
                for (int i = 0; i < _splat.Layers.Length; i++)
                {
                    Texture2D t = _splat.Layers[i].diffuseTexture;
                    names.Add(t == null ? "(no texture)" : t.name);
                }

                Debug.Log("[NativeHybrid] surface layers: " + string.Join(", ", names.ToArray()));
            }
            else
            {
                Debug.LogWarning("[NativeHybrid] no splatmap read, so the strata under every " +
                                 "column will be identical regardless of what the surface shows.");
            }

            TerrainImport.Hide(_source);

            Imported = true;
        }

        void ApplyDiffuse()
        {
            if (!Imported || _terrain == null) return;

            // The surface is a Unity Terrain, so it can blend the source layers itself at
            // their own tiling, with their normal maps, and no custom shader. Baking is only
            // necessary where one mesh means one material -- which is the span bricks, below.
            bool splat = TerrainImport.ApplyLayers(_terrain.Terrain, _source, SampleOrigin, SampleStride);

            _surfaceTexture = TerrainImport.BakeDiffuse(
                _source, SampleOrigin, SampleStride,
                WorldMetres, WorldMetres, DiffuseResolution, GroundColour);

            if (_surfaceTexture == null) return;

            // Only as a fallback. Assigning the bake over live layers would throw away the
            // detail we just gained.
            if (!splat) _terrain.SetDiffuse(_surfaceTexture, WorldMetres, WorldMetres);

            // Deliberately NOT applied to the span materials here.
            //
            // Those materials end up on cave walls, and the UVs are planar world XZ -- which
            // is meaningful on ground and meaningless on anything vertical, where a whole
            // wall collapses onto a single texel and renders as one flat smear. That is the
            // "missing texture" wall: not missing, just sampling one pixel of grass.
            //
            // The custom-mesh scene still needs the bake, because there the spans draw the
            // ground itself. Here the terrain does.

            TextureDugGround();
            CacheDrawnSplat();
        }

        /// <summary>Read back what actually ended up painted on our terrain.</summary>
        void CacheDrawnSplat()
        {
            if (_terrain == null || _terrain.Terrain == null) return;

            TerrainData d = _terrain.Terrain.terrainData;
            if (d == null || d.terrainLayers == null || d.terrainLayers.Length == 0) return;

            _drawnAlpha = d.GetAlphamaps(0, 0, d.alphamapWidth, d.alphamapHeight);
        }

        /// <summary>
        /// Dress the span materials in the terrain's own layer textures.
        ///
        /// Tiled by world position rather than stretched across the world, and the mesher now
        /// writes UVs per face direction, so these sit correctly on a wall as well as a
        /// floor. Both were needed: a tiling texture with planar-XZ UVs would still smear
        /// down every vertical face.
        /// </summary>
        void TextureDugGround()
        {
            Texture2D[] textures = TerrainImport.LayerTextures(_source);
            if (textures == null || _spanMaterials == null) return;

            // Only the three that come from the terrain. The ores are generated below, from
            // the rock this picks, so that they share its grain and lighting.
            string[] wanted = { TopsoilMatch, SubsoilMatch, RockMatch };
            var scale = new Vector2(1f / Mathf.Max(0.01f, DugTextureScale),
                                    1f / Mathf.Max(0.01f, DugTextureScale));

            for (int i = 0; i < _spanMaterials.Length && i < wanted.Length; i++)
            {
                Material m = _spanMaterials[i];
                if (m == null) continue;

                Texture2D tex = PickTexture(textures, wanted[i], i);

                if (m.HasProperty("_BaseMap"))
                {
                    m.SetTexture("_BaseMap", tex);
                    m.SetTextureScale("_BaseMap", scale);
                    m.SetTextureOffset("_BaseMap", Vector2.zero);
                }

                if (m.HasProperty("_MainTex"))
                {
                    m.SetTexture("_MainTex", tex);
                    m.SetTextureScale("_MainTex", scale);
                    m.SetTextureOffset("_MainTex", Vector2.zero);
                }

                // Tinted rather than white, so the layers still read as different materials
                // when two of them borrow the same image.
                Color tint = Color.Lerp(Color.white, SpanMaterials.Colours[i], 0.45f);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
                if (m.HasProperty("_Color")) m.SetColor("_Color", tint);

                Debug.LogFormat("[NativeHybrid] {0} <- '{1}'", SpanMaterials.Names[i], tex.name);
            }

            BuildOreTextures(PickTexture(textures, RockMatch, 2));
            SkinIntactGround();
        }

        /// <summary>
        /// Generate the four ore surfaces from the rock the terrain is already using.
        ///
        /// Built rather than shipped, and built FROM the terrain rather than beside it. Four
        /// hand-made images would be four surfaces with no relationship to the ground they
        /// are cut into, and mismatched rock reads as a fault even when each texture is good
        /// on its own. Starting from the rock in use means the grain and the lighting are
        /// right before any mineral is added.
        /// </summary>
        void BuildOreTextures(Texture2D rock)
        {
            if (rock == null || _spanMaterials == null) return;

            var scale = new Vector2(1f / Mathf.Max(0.01f, DugTextureScale),
                                    1f / Mathf.Max(0.01f, DugTextureScale));

            for (byte m = SpanMaterials.FirstOre; m <= SpanMaterials.LastOre; m++)
            {
                if (m >= _spanMaterials.Length) break;

                Material mat = _spanMaterials[m];
                if (mat == null) continue;

                Texture2D tex = OreTextures.Build(rock, m);
                if (tex == null) continue;

                _oreTextures.Add(tex);

                if (mat.HasProperty("_BaseMap"))
                {
                    mat.SetTexture("_BaseMap", tex);
                    mat.SetTextureScale("_BaseMap", scale);
                    mat.SetTextureOffset("_BaseMap", Vector2.zero);
                }

                if (mat.HasProperty("_MainTex"))
                {
                    mat.SetTexture("_MainTex", tex);
                    mat.SetTextureScale("_MainTex", scale);
                    mat.SetTextureOffset("_MainTex", Vector2.zero);
                }

                // White: the mineral is in the texture now, so tinting on top would only
                // wash out the thing that distinguishes one ore from another.
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            }

            Debug.LogFormat("[NativeHybrid] generated {0} ore textures from '{1}'",
                            _oreTextures.Count, rock.name);
        }

        /// <summary>First texture whose name contains the word, or the nth as a fallback.</summary>
        static Texture2D PickTexture(Texture2D[] textures, string wanted, int fallback)
        {
            if (!string.IsNullOrEmpty(wanted))
            {
                for (int i = 0; i < textures.Length; i++)
                {
                    if (textures[i] == null) continue;
                    if (textures[i].name.ToLowerInvariant().Contains(wanted.ToLowerInvariant()))
                        return textures[i];
                }
            }

            return textures[Mathf.Clamp(fallback, 0, textures.Length - 1)];
        }

        /// <summary>
        /// Skin the ground the spans only draw because the terrain had to stop.
        ///
        /// A hole is a whole heightmap cell, so opening a small shaft forces the terrain to
        /// drop a square metre that is mostly untouched. That remainder is not dug earth and
        /// must not look like it -- it gets the baked surface, which comes from the same
        /// splatmap as the terrain and therefore carries the same colours. Softer than the
        /// live layers beside it, but the same ground rather than a different one.
        /// </summary>
        void SkinIntactGround()
        {
            if (_surfaceTexture == null) return;
            if (_spanMaterials == null || _spanMaterials.Length <= SpanMaterials.SurfaceSkin) return;

            Material m = _spanMaterials[SpanMaterials.SurfaceSkin];
            if (m == null) return;

            var scale = new Vector2(1f / WorldMetres, 1f / WorldMetres);
            var offset = new Vector2(-_grid.Origin.x / WorldMetres, -_grid.Origin.z / WorldMetres);

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

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
        }

        /// <summary>
        /// Put the ore where the adit would have gone, whether or not one is cut. Otherwise
        /// digging into an imported hillside finds nothing but rock, and there is no reason
        /// to follow a vein rather than a straight line.
        /// </summary>
        void PlaceOreBody()
        {
            // Kept as the fallback shape for when the field is switched off; the field
            // itself needs no placing, since it already covers everywhere.
            _world.OreFootprint = new Rect(PortalX + 3.5f, PortalZ - 2f, 5f, 8f);
            _world.OreTopMetres = PortalFloor + 0.4f;
            _world.OreBottomMetres = PortalFloor - 2.6f;
        }

        void CarveMine()
        {
            CutPortal();

            Corridor(new Vector3(PortalX, PortalFloor, PortalZ),
                     new Vector3(PortalX + 7f, PortalFloor, PortalZ), 2.2f);

            Corridor(new Vector3(PortalX + 5.5f, PortalFloor, PortalZ),
                     new Vector3(PortalX + 5.5f, PortalFloor - 1.8f, PortalZ + 8f), 2.2f);
        }

        /// <summary>
        /// Topsoil and subsoil depth for one column, read from what the terrain is painted
        /// with there. Bare rock gets neither; sand gets no turf; anything else is ordinary
        /// ground.
        /// </summary>
        Vector2 SoilProfileAt(int colX, int colZ)
        {
            var normal = new Vector2(HybridWorld.TopsoilDepth, HybridWorld.SubsoilDepth);
            if (_drawnAlpha == null) return normal;

            // OUR world position, read against OUR terrain. No window mapping, so nothing to
            // get out of step with the resample that produced it.
            float wx = _world.Spans.WorldX(colX) + _world.Spans.ColumnSize * 0.5f;
            float wz = _world.Spans.WorldZ(colZ) + _world.Spans.ColumnSize * 0.5f;

            string dominant = TerrainImport.DominantOn(_terrain.Terrain, _drawnAlpha, wx, wz);
            if (string.IsNullOrEmpty(dominant)) return normal;

            LastProfileName = dominant;

            if (!string.IsNullOrEmpty(RockMatch) && dominant.Contains(RockMatch.ToLowerInvariant()))
                return Vector2.zero;                                   // rock all the way up

            if (!string.IsNullOrEmpty(SubsoilMatch) && dominant.Contains(SubsoilMatch.ToLowerInvariant()))
                return new Vector2(0f, HybridWorld.SubsoilDepth);      // no turf over sand

            return normal;
        }

        System.Collections.Generic.List<OreField.Found> _deposits;
        int _deposit = -1;

        /// <summary>
        /// Build the deposit field, giving it a way to ask what the ground is like.
        ///
        /// Elevation is normalised across this world rather than taken in metres, so the same
        /// distribution works on a terrain that sits at four metres and one that sits at four
        /// hundred. "High ground" has to mean high FOR HERE.
        /// </summary>
        void BuildOreField()
        {
            float lowest = float.MaxValue;
            float highest = float.MinValue;

            for (int vz = 0; vz <= _grid.CellsZ; vz++)
            {
                for (int vx = 0; vx <= _grid.CellsX; vx++)
                {
                    float m = _grid.GetMetres(vx, vz);
                    if (m < lowest) lowest = m;
                    if (m > highest) highest = m;
                }
            }

            float span = Mathf.Max(0.001f, highest - lowest);
            uint seed = OreSeed != 0u ? OreSeed : (uint)Random.Range(1, int.MaxValue);

            _world.Ore = new OreField(
                (float wx, float wz, out float elevation, out bool rocky) =>
                {
                    elevation = Mathf.Clamp01((VertexSurface.MetresAtWorld(_grid, wx, wz) - lowest) / span);
                    rocky = SoilThicknessAt(wx, wz) < 0.05f;
                },
                seed);

            Debug.LogFormat("[NativeHybrid] ore field seed {0}, terrain {1:0.0} m to {2:0.0} m",
                            seed, lowest, highest);
        }

        /// <summary>
        /// The deposit nearest the player, as a bearing and a depth.
        ///
        /// A prospecting aid, and a development one: the distribution is meant to make
        /// elevation a clue, and the only way to tell whether that is working is to walk
        /// around watching which mineral is nearest as the ground rises.
        ///
        /// A direction and a distance rather than coordinates -- coordinates would be a map,
        /// and a map answers the question this is supposed to be asking.
        /// </summary>
        public string NearestOreReport()
        {
            if (_deposits == null || _deposits.Count == 0) return "none in this world";
            if (_player == null) return null;

            Vector3 p = _player.transform.position;

            int best = -1;
            float bestSq = float.MaxValue;

            for (int i = 0; i < _deposits.Count; i++)
            {
                float dx = _deposits[i].X - p.x;
                float dz = _deposits[i].Z - p.z;
                float sq = dx * dx + dz * dz;

                if (sq >= bestSq) continue;

                bestSq = sq;
                best = i;
            }

            if (best < 0) return null;

            OreField.Found d = _deposits[best];

            float distance = Mathf.Sqrt(bestSq);
            float dig = SoilThicknessAt(d.X, d.Z) + d.Depth;

            string where = distance <= d.Radius
                ? "underfoot"
                : string.Format("{0:0} m {1}", distance, Compass(d.X - p.x, d.Z - p.z));

            return string.Format("{0}  {1}, dig {2:0.0} m{3}",
                                 SpanMaterials.Names[d.Material], where, dig,
                                 d.Outcrops ? "  (outcrops)" : "");
        }

        static string Compass(float dx, float dz)
        {
            // Bearing from north, clockwise, into eight points. Eight rather than four
            // because four sends you off at up to 45 degrees and a deposit is only a few
            // metres across.
            float bearing = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            if (bearing < 0f) bearing += 360f;

            string[] points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return points[Mathf.RoundToInt(bearing / 45f) % 8];
        }

        /// <summary>Soil above the rock at a world point, in metres.</summary>
        float SoilThicknessAt(float worldX, float worldZ)
        {
            int colX = Mathf.Clamp(
                Mathf.FloorToInt((worldX - _world.Spans.Origin.x) / _world.Spans.ColumnSize),
                0, _world.Spans.ColumnsX - 1);

            int colZ = Mathf.Clamp(
                Mathf.FloorToInt((worldZ - _world.Spans.Origin.z) / _world.Spans.ColumnSize),
                0, _world.Spans.ColumnsZ - 1);

            Vector2 profile = SoilProfileAt(colX, colZ);
            return profile.x + profile.y;
        }

        /// <summary>Every deposit inside this world, nearest the middle first.</summary>
        void FindDeposits()
        {
            float half = WorldMetres * 0.5f;

            _deposits = _world.Ore == null
                ? new System.Collections.Generic.List<OreField.Found>()
                : _world.Ore.Enumerate(-half, -half, half, half, Vector2.zero);

            var counts = new int[SpanMaterials.Count];
            for (int i = 0; i < _deposits.Count; i++) counts[_deposits[i].Material]++;

            Debug.LogFormat("[NativeHybrid] {0} deposits in this world: {1} coal, {2} iron, " +
                            "{3} copper, {4} silver. Press O to walk to each in turn.",
                            _deposits.Count,
                            counts[SpanMaterials.Coal], counts[SpanMaterials.Iron],
                            counts[SpanMaterials.Copper], counts[SpanMaterials.Silver]);

            int listed = Mathf.Min(_deposits.Count, 12);

            for (int i = 0; i < listed; i++)
            {
                OreField.Found d = _deposits[i];

                Debug.LogFormat("    {0,-6} at ({1,7:0.0}, {2,7:0.0})  {3,4:0.0} m below ground{4},  " +
                                "radius {5:0.0} m, {6:0.0} m thick",
                                SpanMaterials.Names[d.Material], d.X, d.Z,
                                SoilThicknessAt(d.X, d.Z) + d.Depth,
                                d.Outcrops ? "  OUTCROP" : "", d.Radius, d.Thickness);
            }
        }

        /// <summary>
        /// Stand on the surface above the next deposit.
        ///
        /// Above rather than inside: dropping the player into solid rock would be a way to
        /// confirm the ore exists and no way at all to see whether digging down to it feels
        /// like anything.
        /// </summary>
        void GoToDeposit()
        {
            if (_deposits == null || _deposits.Count == 0)
            {
                Note = "no deposits in this world";
                return;
            }

            _deposit = (_deposit + 1) % _deposits.Count;
            OreField.Found d = _deposits[_deposit];

            float surface = VertexSurface.MetresAtWorld(_grid, d.X, d.Z);

            var cc = _player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            _player.enabled = false;
            _player.transform.position = new Vector3(d.X, surface + 1f, d.Z);
            _player.enabled = true;

            if (cc != null) cc.enabled = true;

            // Deposits are measured from the rock head, so the soil on top has to be added
            // to answer the only question worth asking: how far down from where I stand.
            float soil = SoilThicknessAt(d.X, d.Z);

            Note = string.Format("{0} {1} of {2}: dig {3:0.0} m ({4:0.0} m soil + {5:0.0} m rock), {6:0.0} m thick",
                                 SpanMaterials.Names[d.Material], _deposit + 1, _deposits.Count,
                                 soil + d.Depth, soil, d.Depth, d.Thickness);

            // Also to the console, so a key that appears to do nothing can be told apart from
            // one that never fired.
            Debug.LogFormat("[NativeHybrid] moved to {0} at ({1:0.0}, {2:0.0}), {3:0.0} m down",
                            SpanMaterials.Names[d.Material], d.X, d.Z, d.Depth);
        }

        /// <summary>The flat-ish shelf with the steepest monotonic rise east of it.</summary>
        void ChooseAditSite()
        {
            // In metres, then converted, so the search does not change shape when the
            // sample spacing does.
            int Run = Mathf.Max(1, Mathf.RoundToInt(8f / CellSize));
            int Width = Mathf.Max(1, Mathf.RoundToInt(2f / CellSize));
            int Stride = 1;

            float best = 0f;
            int bestCx = -1, bestCz = -1;

            for (int cz = Width; cz < _grid.CellsZ - Width; cz += Stride)
            {
                for (int cx = Width; cx < _grid.CellsX - Run - 1; cx += Stride)
                {
                    float baseHeight = _grid.GetMetres(cx, cz);

                    float flatness = 0f;
                    for (int d = -Width; d <= Width; d += Stride)
                        flatness = Mathf.Max(flatness, Mathf.Abs(_grid.GetMetres(cx, cz + d) - baseHeight));

                    if (flatness > 1.5f) continue;

                    float rise = 0f;
                    bool climbs = true;

                    for (int d = Stride; d <= Run; d += Stride)
                    {
                        float h = _grid.GetMetres(cx + d, cz);
                        if (h < baseHeight + rise - 0.5f) { climbs = false; break; }
                        rise = Mathf.Max(rise, h - baseHeight);
                    }

                    if (!climbs || rise < 4f) continue;
                    if (rise > best) { best = rise; bestCx = cx; bestCz = cz; }
                }
            }

            if (bestCx < 0)
            {
                Debug.LogWarning("[NativeHybrid] no slope steep enough for an adit; none was cut.");
                PortalX = 0f;
                PortalZ = 0f;
                PortalFloor = VertexSurface.MetresAtWorld(_grid, PortalX, PortalZ) + 0.2f;
                return;
            }

            PortalX = _grid.Origin.x + bestCx * CellSize;
            PortalZ = _grid.Origin.z + bestCz * CellSize;
            PortalFloor = _grid.GetMetres(bestCx, bestCz) + 0.2f;

            Debug.LogFormat("[NativeHybrid] adit sited at ({0:0.#}, {1:0.#}), floor {2:0.##} m, rise {3:0.#} m",
                            PortalX, PortalZ, PortalFloor, best);
        }

        /// <summary>
        /// The bench the adit starts from, cut with the surface model.
        ///
        /// Levelling a block of vertices leaves the ring around it sloping up to meet it,
        /// which is exactly what should happen and is the difference from the other scene:
        /// there the pad ended in a wall, here it ends in a bank.
        /// </summary>
        void CutPortal()
        {
            int cx0, cz0;
            _grid.WorldToCell(new Vector3(PortalX - 6f, 0f, PortalZ - 2f), out cx0, out cz0);

            int cx1, cz1;
            _grid.WorldToCell(new Vector3(PortalX, 0f, PortalZ + 2f), out cx1, out cz1);

            cx0 = Mathf.Clamp(cx0, 0, _grid.CellsX - 1);
            cz0 = Mathf.Clamp(cz0, 0, _grid.CellsZ - 1);
            cx1 = Mathf.Clamp(cx1, cx0, _grid.CellsX - 1);
            cz1 = Mathf.Clamp(cz1, cz0, _grid.CellsZ - 1);

            ushort target = HeightGrid.ToRaw(PortalFloor);

            for (int vz = cz0; vz <= cz1 + 1; vz++)
            {
                for (int vx = cx0; vx <= cx1 + 1; vx++)
                {
                    if (!_grid.InBounds(vx, vz)) continue;
                    if (_grid.GetRaw(vx, vz) <= target) continue;   // only ever cut down

                    _grid.SetRaw(vx, vz, target);
                }
            }

            _terrain.Sync();
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

                float ceiling = at.y + size * 0.5f;
                if (SpanGrid.ToMetres(_world.SurfaceMmAt(cx, cz)) < ceiling + 0.5f) continue;

                _world.Mine(new Vector3(at.x, at.y + size * 0.5f - 0.001f, at.z), size, 0, false);
            }
        }

        /// <summary>
        /// How far a ceded patch departs from the surface the terrain would have drawn.
        /// Measured inside columns, because the corners agree by construction.
        /// </summary>
        void CheckSeam()
        {
            float worst = 0f;

            for (int bz = 0; bz < _world.BricksZ; bz++)
            {
                for (int bx = 0; bx < _world.BricksX; bx++)
                {
                    if (!_world.BrickCeded(bx, bz)) continue;

                    int x0 = bx * _world.ColumnsPerBrick;
                    int z0 = bz * _world.ColumnsPerBrick;

                    for (int j = 0; j < _world.ColumnsPerBrick; j++)
                        for (int i = 0; i < _world.ColumnsPerBrick; i++)
                            worst = Mathf.Max(worst, Departure(x0 + i, z0 + j));
                }
            }

            SeamError = worst;
            Debug.LogFormat("[NativeHybrid] ceded surface departs from the heightfield by at most {0:0.000000} m",
                            worst);
        }

        static readonly Vector2[] Samples =
        {
            new Vector2(0.5f, 0.5f),
            new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.25f),
            new Vector2(0.25f, 0.75f), new Vector2(0.75f, 0.75f),
        };

        float Departure(int colX, int colZ)
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
                float u = Samples[i].x, v = Samples[i].y;

                float drawn = swap
                    ? (u + v <= 1f ? c00 * (1f - u - v) + c10 * u + c01 * v
                                   : c11 * (u + v - 1f) + c10 * (1f - v) + c01 * (1f - u))
                    : (v <= u ? c00 * (1f - u) + c10 * (u - v) + c11 * v
                              : c00 * (1f - v) + c11 * u + c01 * (v - u));

                float truth = VertexSurface.MetresAt(_grid, u0 + u * k, v0 + v * k);
                worst = Mathf.Max(worst, Mathf.Abs(drawn - truth));
            }

            return worst;
        }

        // ---- scene furniture ---------------------------------------------------

        void BuildMaterials()
        {
            _groundMaterial = Lit(GroundColour);

            _spanMaterials = new Material[SpanMaterials.Count];
            for (int i = 0; i < _spanMaterials.Length; i++)
            {
                // Its own material, not the ground one. They used to be shared so a ceded
                // patch matched the surface, but the terrain draws intact ground now and this
                // one only ever appears on cut faces -- which want dug earth, not lawn.
                _spanMaterials[i] = Lit(SpanMaterials.Colours[i]);
            }
        }

        static Material Unlit(Color colour)
        {
            Material m = MaterialLibrary.Build("marker", MaterialLibrary.Unlit, MaterialLibrary.UnlitShaders);
            if (m == null) return null;

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
            if (m.HasProperty("_Color")) m.SetColor("_Color", colour);

            return m;
        }

        static Material Lit(Color colour)
        {
            Material m = MaterialLibrary.Build("native hybrid", MaterialLibrary.Lit, MaterialLibrary.LitShaders);
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

        void BuildViews()
        {
            _views = new[]
            {
                new ViewPoint("the bench", PortalX - 2.5f, PortalFloor + 1f, PortalZ, 90f),
                new ViewPoint("inside the adit", PortalX + 3.5f, PortalFloor + 0.6f, PortalZ, 90f),
                new ViewPoint("the drift", PortalX + 5.5f, PortalFloor - 0.6f, PortalZ + 5f, 0f),
                new ViewPoint("on the roof", PortalX + 3.5f, PortalFloor + 12f, PortalZ, 90f),
                new ViewPoint("far off - watch the seam", PortalX - 40f, PortalFloor + 14f, PortalZ, 90f),
            };
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
            cam.farClipPlane = 400f;

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
                ground = open != int.MinValue
                    ? SpanGrid.ToMetres(open)
                    : VertexSurface.MetresAtWorld(_grid, view.At.x, view.At.z);
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
        /// Native collider on ordinary ground, brick collider inside a ceded region. The
        /// terrain's own collider stops at a hole, so without the handover the player would
        /// fall through the opening.
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
                _player.TerrainCollider = _terrain.TerrainCollider;
        }

        void UpdateLamp()
        {
            if (_lamp == null || _player == null) return;

            Vector3 p = _player.transform.position;

            int cx, cz;
            ColumnAt(p.x, p.z, out cx, out cz);

            _lamp.enabled = _world.Spans.InBounds(cx, cz)
                && _world.Spans.SolidAbove(cx, cz, SpanGrid.ToMm(p.y + 0.2f));
        }

        void ColumnAt(float x, float z, out int cx, out int cz)
        {
            cx = Mathf.FloorToInt((x - _world.Spans.Origin.x) / _world.Spans.ColumnSize);
            cz = Mathf.FloorToInt((z - _world.Spans.Origin.z) / _world.Spans.ColumnSize);
        }
    }
}
