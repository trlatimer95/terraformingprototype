using UnityEngine;
using Terraform.Core;
using Terraform.View;

namespace Terraform.Play
{
    /// <summary>
    /// Builds the whole demo in code so there is no serialized setup to break.
    /// Auto-spawns on play if not already in the scene, so any scene works.
    ///
    /// BOTH terrain models are built at startup and live side by side in the same space,
    /// with only one visible and collidable at a time. Swapping with M is instant and
    /// each model keeps whatever you sculpted in it, so you can flip back and forth
    /// comparing the same piece of ground shaped two different ways.
    /// </summary>
    public sealed class P0Bootstrap : MonoBehaviour
    {
        public enum GroundSource { DemoMap, SceneTerrain }

        [Header("Model")]
        public bool StartWithCellModel = true;

        [Header("Grid")]
        // 64 keeps VertsX at 65 -- 2^n+1 -- which is what Unity Terrain requires, so the
        // T comparison in the vertex model stays available.
        public int CellsX = 64;
        public int CellsZ = 64;
        public float CellSize = 1f;
        public float StartHeight = 4f;      // headroom to dig down as well as up

        /// <summary>Flat sandbox in the middle, features around it. Off gives a bare pad.</summary>
        public bool GenerateTerrain = true;

        [Header("Ground source")]
        /// <summary>
        /// Where the starting ground comes from. SceneTerrain samples a Unity Terrain that
        /// is already in the scene -- whatever generator put it there -- so the tools can
        /// be tried on generated ground. Falls back to the demo map, loudly, if the scene
        /// has no terrain in it.
        /// </summary>
        public GroundSource Source = GroundSource.DemoMap;

        /// <summary>World XZ on the source terrain that the near corner of the window sits on.</summary>
        public Vector2 SampleOrigin = Vector2.zero;

        /// <summary>
        /// Source metres per grid metre. 1 is the real thing. Above 1 the window covers
        /// more ground as a true-scale miniature -- heights shrink to match -- which is
        /// for finding a spot worth looking at, not for judging how the tools feel.
        /// </summary>
        public float SampleStride = 1f;

        /// <summary>Where the lowest point of the window lands, leaving room to dig below it.</summary>
        public float SampleFloorMetres = 4f;

        /// <summary>The source sits where our world does; leaving it visible collides and z-fights.</summary>
        public bool HideSourceTerrain = true;

        /// <summary>Bake the source's painted layers onto the models instead of the flat colour.</summary>
        public bool ImportTextures = true;

        /// <summary>
        /// Baked surface size. 1024 over a 64 m window is 16 px per metre. Higher is
        /// sharper and slower, and the bake runs again on every window move.
        /// </summary>
        public int TextureResolution = 1024;

        [Header("Player")]
        public float EyeHeight = 1.62f;
        public float Reach = 5f;

        [Header("Comparison")]
        /// <summary>
        /// Unity Terrain size.y for the vertex model's comparison view. Heights are stored
        /// normalised against this, so precision is heightRange/65535.
        /// </summary>
        public float TerrainHeightRange = 64f;

        [Header("Look")]
        public Color GroundColour = new Color(0.42f, 0.40f, 0.33f);
        public Color GridColour = new Color(0.10f, 0.10f, 0.11f);   // opaque: Unlit/Color ignores alpha
        public Color CellColour = new Color(0.25f, 0.85f, 1f);
        public Color MarkerColour = new Color(1f, 0.78f, 0.15f);

        public bool UsingCellModel { get; private set; }
        public bool UsingUnityTerrain { get; private set; }
        public UnityTerrainView TerrainView { get { return _terrainView; } }

        // ---- cell model ----
        GameObject _cellRoot;
        GameObject _cellOverlay;
        MeshRenderer _cellRenderer;
        CellChunkView _cellChunk;
        MeshCollider _cellCollider;
        CellTerraformTool _cellTool;
        CellHud _cellHud;

        // ---- vertex model ----
        GameObject _vertexRoot;
        GameObject _vertexOverlay;
        ChunkView _chunk;
        CellHighlight _highlight;
        UnityTerrainView _terrainView;
        MeshRenderer _vertexRenderer;
        Transform _vertexMarker;
        MeshCollider _vertexCollider;
        TerraformTool _tool;
        P0Hud _hud;

        // ---- ground source ----
        TerrainImport.Source _source;
        float _sampleOffset;
        int _windowIndex;
        Texture2D _surface;

        // ---- shared ----
        FirstPersonController _player;
        Transform _head;
        Camera _cam;
        bool _wantsRelock;
        PauseMenu _menu;
        bool _paused;
        SkyController _sky;
        Light _sun;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoSpawn()
        {
            if (FindAnyObjectByType<P0Bootstrap>() != null) return;

            // The span gate is its own scene with its own bootstrap. Without this, both
            // worlds would spawn on top of each other.
            if (FindAnyObjectByType<SpanGateBootstrap>() != null) return;

            var go = new GameObject("P0 Bootstrap (auto)");
            go.AddComponent<P0Bootstrap>();
        }

        void Awake()
        {
            EnsureLight();

            Vector3 origin = new Vector3(-CellsX * CellSize * 0.5f, 0f, -CellsZ * CellSize * 0.5f);

            // Before anything of ours exists, or the scan would find the vertex model's
            // own comparison terrain and sample the prototype into itself.
            CaptureSource();

            BuildCellWorld(origin);
            BuildVertexWorld(origin);

            RefreshSurface();

            if (Source == GroundSource.SceneTerrain && HideSourceTerrain) TerrainImport.Hide(_source);

            _player = BuildPlayer(new Vector3(0f, StartHeight + 1.5f, -6f));
            _cam = AttachCameraToHead();

            BuildTools();
            SetModel(StartWithCellModel);
            CaptureCursor();

            ReportSource();
        }

        // ---- ground source ----------------------------------------------------

        void CaptureSource()
        {
            if (Source != GroundSource.SceneTerrain) return;

            _source = TerrainImport.Capture();

            if (!_source.Valid)
            {
                Debug.LogWarning("[P0] Ground source is SceneTerrain but the scene has no Terrain in it. " +
                                 "Falling back to the demo map.");
                Source = GroundSource.DemoMap;
                return;
            }

            SampleStride = Mathf.Max(0.01f, SampleStride);
            RefreshOffset();
        }

        /// <summary>
        /// One vertical shift, shared by both models. If they rebased separately they would
        /// stop being the same piece of ground and the comparison would mean nothing.
        /// </summary>
        void RefreshOffset()
        {
            _sampleOffset = TerrainImport.FitOffset(_source, SampleOrigin, SampleStride,
                                                    CellsX, CellsZ, CellSize, SampleFloorMetres);
        }

        bool ImportingScene
        {
            get { return Source == GroundSource.SceneTerrain && _source != null && _source.Valid; }
        }

        void ApplySource(CellGrid grid)
        {
            if (ImportingScene)
            {
                TerrainImport.Apply(grid, _source, SampleOrigin, SampleStride, _sampleOffset);
                return;
            }

            if (GenerateTerrain) DemoTerrain.Apply(grid, StartHeight);
        }

        void ApplySource(HeightGrid grid)
        {
            if (ImportingScene)
            {
                TerrainImport.Apply(grid, _source, SampleOrigin, SampleStride, _sampleOffset);
                return;
            }

            if (GenerateTerrain) DemoTerrain.Apply(grid, StartHeight);
        }

        void ReportSource()
        {
            if (!ImportingScene) return;

            float steepest = TerrainImport.SteepestStepMetres(_cellChunk.Grid);
            float guard = _cellTool != null ? _cellTool.MaxStepUnits * CellGrid.MetresPerUnit : 0f;

            Debug.LogFormat(
                "[P0] imported scene terrain{0}" +
                "  tiles      {1}{0}" +
                "  world      {2:0} x {3:0} m{0}" +
                "  window     ({4:0}, {5:0}) covering {6:0} m at 1:{7:0.##}{0}" +
                "  rebased    {8:+0.0;-0.0} m so the low point sits at {9:0.#} m{0}" +
                "  steepest   {10:0.##} m between neighbouring cells",
                "\n",
                _source.Tiles.Length, _source.Size.x, _source.Size.z,
                SampleOrigin.x, SampleOrigin.y, CellsX * CellSize * SampleStride, SampleStride,
                _sampleOffset, SampleFloorMetres, steepest);

            if (guard > 0f && steepest > guard)
                Debug.LogFormat("[P0] parts of this window are steeper than the {0:0.#} m step guard. " +
                                "Edits there go through only while they do not deepen the step, so a " +
                                "slope has to be worked from the top down.", guard);
        }

        /// <summary>
        /// Slide the window across the source in half-window steps and re-import both
        /// models. 64 m out of a generated world is a small keyhole; this is how to look
        /// through it somewhere else without leaving play mode.
        /// </summary>
        public void StepWindow(int direction)
        {
            if (!ImportingScene) return;

            float span = CellsX * CellSize * SampleStride;
            float step = Mathf.Max(1f, span * 0.5f);

            int cols = Mathf.Max(1, Mathf.FloorToInt((_source.Size.x - span) / step) + 1);
            int rows = Mathf.Max(1, Mathf.FloorToInt((_source.Size.z - span) / step) + 1);
            int count = cols * rows;

            _windowIndex = ((_windowIndex + direction) % count + count) % count;

            SampleOrigin = new Vector2(_source.Min.x + (_windowIndex % cols) * step,
                                       _source.Min.z + (_windowIndex / cols) * step);

            RefreshOffset();
            RefreshSurface();

            // Both models, always. They have to stay the same piece of ground.
            _cellChunk.Grid.Fill(StartHeight);
            ApplySource(_cellChunk.Grid);
            _cellChunk.Log.Clear();
            _cellChunk.Rebuild();

            _chunk.Grid.Fill(StartHeight);
            ApplySource(_chunk.Grid);
            _chunk.Log.Clear();
            _chunk.Rebuild();
            if (_terrainView != null) _terrainView.ClearHoles();

            PlacePlayer();

            Debug.LogFormat("[P0] window {0}/{1} at ({2:0}, {3:0}), rebased {4:+0.0;-0.0} m",
                            _windowIndex + 1, count, SampleOrigin.x, SampleOrigin.y, _sampleOffset);
        }

        /// <summary>Drop the player onto the new ground; the old spot is probably inside a hill.</summary>
        void PlacePlayer()
        {
            if (_player == null) return;

            Vector3 at = _player.transform.position;
            float ground = StartHeight;

            if (UsingCellModel)
            {
                int cx, cz;
                if (_cellChunk.Grid.WorldToCell(at, out cx, out cz)) ground = _cellChunk.Grid.GetMetres(cx, cz);
            }
            else
            {
                Vector2 g = _chunk.Grid.WorldToGridPoint(at);
                ground = _chunk.Grid.SampleMetres(g.x, g.y);
            }

            var cc = _player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            _player.transform.position = new Vector3(at.x, ground + 1.5f, at.z);
            if (cc != null) cc.enabled = true;
        }

        /// <summary>
        /// Bake the source's painted layers into one map and put it on both models.
        ///
        /// One texture on the standard material rather than a splatting shader: a custom
        /// shader is one more thing that has to be dragged into a player build, and that
        /// is exactly how the world turned magenta the first time.
        /// </summary>
        void RefreshSurface()
        {
            if (_surface != null)
            {
                Destroy(_surface);
                _surface = null;
            }

            if (ImportingScene && ImportTextures)
            {
                _surface = TerrainImport.BakeDiffuse(_source, SampleOrigin, SampleStride,
                                                     CellsX * CellSize, CellsZ * CellSize,
                                                     TextureResolution, GroundColour);

                if (_surface == null)
                    Debug.Log("[P0] the source has no painted terrain layers, so the flat colour stays. " +
                              "Run a texture spawner on it if you want the surface as well as the shape.");
            }

            PaintSurface(_cellRenderer);
            PaintSurface(_vertexRenderer);

            if (_terrainView != null && _terrainView.Valid)
                _terrainView.SetDiffuse(_surface, CellsX * CellSize, CellsZ * CellSize);
        }

        void PaintSurface(MeshRenderer target)
        {
            if (target == null || target.sharedMaterial == null) return;

            Material m = target.sharedMaterial;

            // Both mesh builders already write world-planar UVs in metres, so the only
            // thing needed is a scale that maps the window onto 0..1 of the baked map.
            Vector2 scale = _surface != null
                ? new Vector2(1f / (CellsX * CellSize), 1f / (CellsZ * CellSize))
                : Vector2.one;

            if (m.HasProperty("_MainTex"))
            {
                m.SetTexture("_MainTex", _surface);
                m.SetTextureScale("_MainTex", scale);
            }

            if (m.HasProperty("_BaseMap"))
            {
                m.SetTexture("_BaseMap", _surface);
                m.SetTextureScale("_BaseMap", scale);
            }

            // The tint multiplies the map, so it has to go white or every imported colour
            // comes through wearing the clay colour.
            Color tint = _surface != null ? Color.white : GroundColour;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
            if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
        }

        void OnDestroy()
        {
            if (_surface != null) Destroy(_surface);
        }

        /// <summary>Current sky preset, for the HUD.</summary>
        public string SkyName { get { return _sky != null ? _sky.CurrentName : "none"; } }

        /// <summary>One HUD line saying where the ground came from. Null on the demo map.</summary>
        public string SourceLine
        {
            get
            {
                if (!ImportingScene) return null;

                return string.Format(
                    "Source      <b>scene terrain</b>  window ({0:0}, {1:0}) at 1:{2:0.##}   [N / Shift+N]",
                    SampleOrigin.x, SampleOrigin.y, SampleStride);
            }
        }

        // ---- construction -----------------------------------------------------

        void BuildCellWorld(Vector3 origin)
        {
            _cellRoot = new GameObject("CellWorld");
            var grid = new CellGrid(CellsX, CellsZ, CellSize, origin, StartHeight);
            ApplySource(grid);

            var chunkGo = Child(_cellRoot, "CellChunk");
            chunkGo.AddComponent<MeshFilter>();
            _cellRenderer = chunkGo.AddComponent<MeshRenderer>();
            _cellRenderer.sharedMaterial = MakeLitMaterial(GroundColour);
            _cellCollider = chunkGo.AddComponent<MeshCollider>();

            _cellChunk = chunkGo.AddComponent<CellChunkView>();
            _cellChunk.Initialise(grid);

            _cellOverlay = Child(_cellRoot, "CellGridOverlay");
            _cellOverlay.AddComponent<MeshFilter>();
            Unlit(_cellOverlay.AddComponent<MeshRenderer>(), GridColour);
            _cellOverlay.AddComponent<CellGridOverlay>().Initialise(_cellChunk);

            var marqueeGo = Child(_cellRoot, "CellMarquee");
            marqueeGo.AddComponent<MeshFilter>();
            Unlit(marqueeGo.AddComponent<MeshRenderer>(), CellColour);
            marqueeGo.AddComponent<CellMarquee>().Initialise(grid);
        }

        void BuildVertexWorld(Vector3 origin)
        {
            _vertexRoot = new GameObject("VertexWorld");
            var grid = new HeightGrid(CellsX, CellsZ, CellSize, origin, StartHeight);
            ApplySource(grid);

            var chunkGo = Child(_vertexRoot, "TerrainChunk");
            chunkGo.AddComponent<MeshFilter>();
            _vertexRenderer = chunkGo.AddComponent<MeshRenderer>();
            _vertexRenderer.sharedMaterial = MakeLitMaterial(GroundColour);
            _vertexCollider = chunkGo.AddComponent<MeshCollider>();

            _chunk = chunkGo.AddComponent<ChunkView>();
            _chunk.Initialise(grid);

            _vertexOverlay = Child(_vertexRoot, "GridOverlay");
            _vertexOverlay.AddComponent<MeshFilter>();
            Unlit(_vertexOverlay.AddComponent<MeshRenderer>(), GridColour);
            _vertexOverlay.AddComponent<GridOverlay>().Initialise(_chunk);

            var highlightGo = Child(_vertexRoot, "CellHighlight");
            highlightGo.AddComponent<MeshFilter>();
            Unlit(highlightGo.AddComponent<MeshRenderer>(), CellColour);
            _highlight = highlightGo.AddComponent<CellHighlight>();
            _highlight.Initialise(grid);

            var terrainGo = Child(_vertexRoot, "UnityTerrain");
            _terrainView = terrainGo.AddComponent<UnityTerrainView>();
            _terrainView.Initialise(grid, TerrainHeightRange, GroundColour);

            // A dot on the vertex sculpt will move. Its collider would sit exactly where
            // we aim and shadow the terrain, so it goes.
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "VertexMarker";
            marker.transform.SetParent(_vertexRoot.transform, false);
            marker.transform.localScale = Vector3.one * 0.22f;
            Destroy(marker.GetComponent<Collider>());
            Unlit(marker.GetComponent<MeshRenderer>(), MarkerColour);
            _vertexMarker = marker.transform;

            _chunk.MeshChanged += SyncTerrainView;
        }

        void BuildTools()
        {
            _cellTool = gameObject.AddComponent<CellTerraformTool>();
            _cellTool.Chunk = _cellChunk;
            _cellTool.Cam = _cam;
            _cellTool.Marquee = _cellRoot.GetComponentInChildren<CellMarquee>(true);
            _cellTool.PickCollider = _cellCollider;
            _cellTool.MaxReach = Reach;

            _cellHud = gameObject.AddComponent<CellHud>();
            _cellHud.Chunk = _cellChunk;
            _cellHud.Tool = _cellTool;
            _cellHud.Bootstrap = this;

            _tool = gameObject.AddComponent<TerraformTool>();
            _tool.Chunk = _chunk;
            _tool.Cam = _cam;
            _tool.PickCollider = _vertexCollider;
            _tool.Marker = _vertexMarker;
            _tool.MaxReach = Reach;

            _hud = gameObject.AddComponent<P0Hud>();
            _hud.Chunk = _chunk;
            _hud.Tool = _tool;
            _hud.Bootstrap = this;

            _menu = gameObject.AddComponent<PauseMenu>();
            _menu.Bootstrap = this;

            _sky = gameObject.AddComponent<SkyController>();
            _sky.FallbackColour = _cam != null ? _cam.backgroundColor : Color.grey;
            _sky.Initialise(_sun, _cam);
        }

        GameObject Child(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        void Unlit(MeshRenderer r, Color colour)
        {
            r.sharedMaterial = MakeUnlitMaterial(colour);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        // ---- model switching --------------------------------------------------

        public void SetModel(bool cell)
        {
            UsingCellModel = cell;

            _cellRoot.SetActive(cell);
            _vertexRoot.SetActive(!cell);

            _cellTool.enabled = cell;
            _cellHud.enabled = cell;
            _tool.enabled = !cell;
            _hud.enabled = !cell;

            if (cell) BindGround(_cellCollider);
            else SetSurface(UsingUnityTerrain);
        }

        void BindGround(Collider ground)
        {
            if (_player != null) _player.TerrainCollider = ground;
            if (UsingCellModel) _cellTool.PickCollider = ground;
            else _tool.PickCollider = ground;
        }

        public void SetSurface(bool unityTerrain)
        {
            if (unityTerrain && (_terrainView == null || !_terrainView.Valid)) return;

            UsingUnityTerrain = unityTerrain;

            _vertexRenderer.enabled = !unityTerrain;
            _vertexCollider.enabled = !unityTerrain;

            if (_terrainView != null && _terrainView.Valid)
            {
                _terrainView.Terrain.enabled = unityTerrain;
                _terrainView.TerrainCollider.enabled = unityTerrain;
            }

            if (UsingCellModel) return;
            BindGround(unityTerrain ? (Collider)_terrainView.TerrainCollider : _vertexCollider);
        }

        void SyncTerrainView()
        {
            if (_terrainView != null && _terrainView.Valid) _terrainView.Sync();
        }

        // ---- loop -------------------------------------------------------------

        /// <summary>
        /// Stop the tools and free the mouse while the menu is up.
        ///
        /// Not Time.timeScale: the tools read the mouse in Update regardless of time, so
        /// pausing the clock would still let a click on Resume dig through the panel.
        /// </summary>
        public void SetPaused(bool paused)
        {
            if (_paused == paused) return;
            _paused = paused;

            if (paused)
            {
                _cellTool.enabled = false;
                _tool.enabled = false;
                if (_player != null) _player.enabled = false;

                _wantsRelock = false;
                ReleaseCursor();
                return;
            }

            if (_player != null) _player.enabled = true;

            // Back through SetModel so the right tool and HUD come back, rather than
            // enabling both and leaving the inactive model live.
            SetModel(UsingCellModel);
            CaptureCursor();
        }

        void Update()
        {
            if (InputCompat.EscapePressed && _menu != null) _menu.Toggle();
            if (_paused) return;

            if (InputCompat.ToggleModelPressed) SetModel(!UsingCellModel);

            if (InputCompat.ToggleGridPressed)
            {
                GameObject overlay = UsingCellModel ? _cellOverlay : _vertexOverlay;
                if (overlay != null) overlay.SetActive(!overlay.activeSelf);
            }

            if (InputCompat.ResetPressed) ResetActiveModel();

            if (InputCompat.SampleWindowPressed) StepWindow(InputCompat.Shift ? -1 : 1);

            if (InputCompat.SkyPressed && _sky != null) _sky.Next();

            if (!UsingCellModel) HandleVertexOnlyKeys();

            // Escape now opens the menu, so the only way the cursor comes loose during
            // play is the window losing focus. Clicking back in takes it again.
            if (InputCompat.LeftPressed && Cursor.lockState != CursorLockMode.Locked) _wantsRelock = true;
        }

        void ResetActiveModel()
        {
            if (UsingCellModel)
            {
                _cellChunk.Grid.Fill(StartHeight);
                ApplySource(_cellChunk.Grid);
                _cellChunk.Log.Clear();
                _cellChunk.Rebuild();
                return;
            }

            _chunk.Grid.Fill(StartHeight);
            ApplySource(_chunk.Grid);
            _chunk.Log.Clear();
            _chunk.Rebuild();
            if (_terrainView != null) _terrainView.ClearHoles();
        }

        void HandleVertexOnlyKeys()
        {
            if (InputCompat.ToggleSurfacePressed) SetSurface(!UsingUnityTerrain);

            if (!UsingUnityTerrain || _terrainView == null || !_terrainView.Valid) return;

            // Terrain holes are primal cells, so map the targeted vertex onto one.
            if (InputCompat.HolePressed && _tool != null && _tool.HasTarget)
            {
                HeightGrid g = _chunk.Grid;
                _terrainView.ToggleHole(
                    Mathf.Clamp(_tool.TargetVx, 0, g.CellsX - 1),
                    Mathf.Clamp(_tool.TargetVz, 0, g.CellsZ - 1));
            }

            if (InputCompat.PixelErrorUpPressed) _terrainView.PixelError += 1f;
            if (InputCompat.PixelErrorDownPressed) _terrainView.PixelError -= 1f;
        }

        // Re-lock after the tools have run, so the click that recaptures the cursor is
        // not also read as a dig.
        void LateUpdate()
        {
            if (_paused) return;

            if (!UsingCellModel) UpdateVertexHighlight();

            if (!_wantsRelock) return;
            _wantsRelock = false;
            CaptureCursor();
        }

        void UpdateVertexHighlight()
        {
            if (_highlight == null || _tool == null) return;

            _highlight.Begin();

            // Sculpt is shown by the dot alone; flatten and ramp outline their cells.
            if (_tool.Tool != TerraformTool.Mode.Sculpt)
            {
                int vx0, vz0, w, h;
                if (_tool.TryFootprint(out vx0, out vz0, out w, out h))
                    _highlight.AddVertexGrid(vx0, vz0, w, h);
            }

            if (_tool.HasAnchor && _tool.HasTarget)
                _highlight.AddSegment(_tool.AnchorWorld, _tool.AimWorld);

            _highlight.End();
        }

        static void CaptureCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        static void ReleaseCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // ---- scene furniture --------------------------------------------------

        FirstPersonController BuildPlayer(Vector3 spawn)
        {
            var go = new GameObject("Player");
            go.transform.position = spawn;

            var cc = go.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.3f;
            cc.center = new Vector3(0f, 0.9f, 0f);   // origin sits at the feet
            cc.slopeLimit = 55f;
            cc.stepOffset = 0.45f;
            cc.skinWidth = 0.02f;

            _head = new GameObject("Head").transform;
            _head.SetParent(go.transform, false);
            _head.localPosition = new Vector3(0f, EyeHeight, 0f);

            return go.AddComponent<FirstPersonController>();
        }

        Camera AttachCameraToHead()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
            }

            cam.farClipPlane = 1000f;
            cam.nearClipPlane = 0.05f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.18f, 0.21f);

            cam.transform.SetParent(_head, false);
            cam.transform.localPosition = Vector3.zero;
            cam.transform.localRotation = Quaternion.identity;

            _player.Head = _head;
            return cam;
        }

        /// <summary>The directional light, reused if the scene already has one. The sky
        /// drives its angle and colour, so it has to be kept rather than just created.</summary>
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
            _sun.intensity = 1.05f;
            _sun.shadows = LightShadows.Soft;
            go.transform.rotation = Quaternion.Euler(48f, 35f, 0f);
        }

        // ---- materials --------------------------------------------------------

        /// <summary>
        /// Resolves the first shader that exists, and says which. A null shader renders
        /// magenta with no explanation, so name the fallback rather than guess later.
        /// </summary>
        static Material MakeLitMaterial(Color colour)
        {
            Material m = MaterialLibrary.Build("lit", MaterialLibrary.Lit, MaterialLibrary.LitShaders);
            if (m == null) return null;

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
            if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.04f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.04f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            return m;
        }

        static Material MakeUnlitMaterial(Color colour)
        {
            Material m = MaterialLibrary.Build("unlit", MaterialLibrary.Unlit, MaterialLibrary.UnlitShaders);
            if (m == null) return null;

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
            if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
            return m;
        }
    }
}
