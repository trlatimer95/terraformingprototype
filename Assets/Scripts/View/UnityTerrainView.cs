using UnityEngine;
using Terraform.Core;

namespace Terraform.View
{
    /// <summary>
    /// Second view onto the same HeightGrid, rendered through Unity's Terrain instead of
    /// a generated mesh. Exists purely so the two can be compared under identical edits:
    /// same data, same commands, different renderer and different collider.
    ///
    /// The TerrainData is constructed at runtime and never saved. Mutating a TerrainData
    /// that came from a project asset writes through to disk and permanently alters the
    /// source world, which is the classic way to lose an afternoon.
    /// </summary>
    public sealed class UnityTerrainView : MonoBehaviour
    {
        public Terrain Terrain { get; private set; }
        public TerrainCollider TerrainCollider { get; private set; }

        public bool Valid { get; private set; }
        public float LastSyncMs { get; private set; }
        public int HoleCount { get; private set; }
        public int HolesResolution { get { return _holesRes; } }

        /// <summary>Last failure from a hole write, surfaced in the HUD. Null when fine.</summary>
        public string LastHoleError { get; private set; }

        /// <summary>False when the terrain material cannot clip holes, so they only affect collision.</summary>
        public bool HolesRenderable { get; private set; }

        public string ShaderName { get; private set; }

        public float PixelError
        {
            get { return Terrain == null ? 0f : Terrain.heightmapPixelError; }
            set { if (Terrain != null) Terrain.heightmapPixelError = Mathf.Clamp(value, 0f, 200f); }
        }

        HeightGrid _grid;
        TerrainData _data;
        TerrainLayer _layer;
        Texture2D _texture;
        Material _material;
        float[,] _heights;
        bool[,] _solid;
        int _holesRes;
        float _heightRange;
        bool _logged;

        /// <summary>
        /// Unity Terrain heightmaps must be square and 2^n+1 (33, 65, 129, ...). Our grid
        /// is vertex-based too, so a 32-cell chunk maps exactly onto a 33 heightmap.
        /// </summary>
        public static bool SupportsGrid(HeightGrid g)
        {
            if (g.VertsX != g.VertsZ) return false;
            int n = g.VertsX - 1;
            return n >= 32 && (n & (n - 1)) == 0;
        }

        public bool Initialise(HeightGrid grid, float heightRange, Color surfaceColour)
        {
            _grid = grid;
            _heightRange = heightRange;

            if (!SupportsGrid(grid))
            {
                Debug.LogError(string.Format(
                    "UnityTerrainView needs a square 2^n+1 vertex grid; got {0}x{1}. " +
                    "Set CellsX = CellsZ = 32, 64, 128, ... to enable the comparison view.",
                    grid.VertsX, grid.VertsZ));
                Valid = false;
                return false;
            }

            _data = new TerrainData();
            _data.name = "P0 TerrainData (runtime, unsaved)";

            // Order matters: assigning heightmapResolution resets size back to default.
            _data.heightmapResolution = grid.VertsX;
            _data.size = new Vector3(grid.CellsX * grid.CellSize, heightRange, grid.CellsZ * grid.CellSize);

            // A terrain with no layers falls back to a render path that ignores the holes
            // texture, so holes silently do nothing. One flat layer puts it on the real
            // terrain shader and, incidentally, matches the mesh view's colour.
            BuildSurfaceLayer(surfaceColour);

            Terrain = gameObject.AddComponent<Terrain>();
            Terrain.terrainData = _data;
            Terrain.allowAutoConnect = false;
            Terrain.heightmapPixelError = 5f;   // Unity's default; the thing worth tuning

            BuildTerrainMaterial();

            TerrainCollider = gameObject.AddComponent<TerrainCollider>();
            TerrainCollider.terrainData = _data;

            transform.position = grid.Origin;

            _heights = new float[grid.VertsZ, grid.VertsX];

            // Size the holes map from Unity rather than assuming it matches the cell grid.
            // It is normally heightmapResolution-1, but assuming that and being wrong makes
            // SetHoles throw, which looks identical to "holes do nothing".
            _holesRes = _data.holesResolution;
            _solid = new bool[_holesRes, _holesRes];
            for (int z = 0; z < _holesRes; z++)
                for (int x = 0; x < _holesRes; x++) _solid[z, x] = true;

            Valid = true;
            Sync();

            Debug.Log(string.Format(
                "[UnityTerrainView] ready\n" +
                "  cells {0}x{1}   heightmapResolution {2}   holesResolution {3}\n" +
                "  shader = {4}   holes renderable = {5}",
                grid.CellsX, grid.CellsZ, _data.heightmapResolution, _holesRes,
                ShaderName ?? "null", HolesRenderable));

            return true;
        }

        /// <summary>
        /// With materialTemplate left null, Unity renders through an internal default that
        /// does not sample the holes texture: a punched cell loses collision but keeps
        /// drawing. The built-in terrain shaders do clip, so assign one explicitly.
        /// </summary>
        void BuildTerrainMaterial()
        {
            // Through the library, not Shader.Find: the terrain shader is exactly the one
            // that cannot be forced into Always Included Shaders without crashing the build.
            _material = MaterialLibrary.Build("terrain", MaterialLibrary.TerrainSurface,
                                              MaterialLibrary.TerrainShaders);

            if (_material == null)
            {
                ShaderName = "none found";
                HolesRenderable = false;
                Debug.LogWarning("[UnityTerrainView] No terrain shader resolved. Holes will " +
                                 "remove collision but stay visible.");
                return;
            }

            _material.name = "P0 Terrain Material";
            Terrain.materialTemplate = _material;

            ShaderName = _material.shader.name;
            HolesRenderable = _material.HasProperty("_TerrainHolesTexture");

            if (!HolesRenderable)
                Debug.LogWarning("[UnityTerrainView] Shader '" + _material.shader.name +
                                 "' has no _TerrainHolesTexture; holes will not render.");
        }

        void BuildSurfaceLayer(Color colour)
        {
            _texture = new Texture2D(8, 8);
            _texture.name = "P0 Terrain Surface";
            var pixels = new Color[8 * 8];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = colour;
            _texture.SetPixels(pixels);
            _texture.Apply();

            _layer = new TerrainLayer();
            _layer.name = "P0 Surface Layer";
            _layer.diffuseTexture = _texture;
            _layer.tileSize = new Vector2(4f, 4f);

            _data.terrainLayers = new[] { _layer };

            // Assigning layers leaves the alphamap zeroed, which renders as unpainted.
            // Weight the single layer fully so the surface is actually visible.
            _data.alphamapResolution = 32;
            var weights = new float[_data.alphamapResolution, _data.alphamapResolution, 1];
            for (int y = 0; y < _data.alphamapResolution; y++)
                for (int x = 0; x < _data.alphamapResolution; x++)
                    weights[y, x, 0] = 1f;

            _data.SetAlphamaps(0, 0, weights);
        }

        /// <summary>Push the grid's heights into the terrain. Called after every edit.</summary>
        public void Sync()
        {
            if (!Valid) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Terrain stores normalised 0..1 heights scaled by size.y, so precision is
            // heightRange/65535 rather than a fixed step. Our own units survive the trip
            // only because heightRange is small; at 600 m the quantisation would be ~9 mm.
            float inv = 1f / _heightRange;

            for (int vz = 0; vz < _grid.VertsZ; vz++)
            {
                for (int vx = 0; vx < _grid.VertsX; vx++)
                {
                    // Terrain's array is indexed [z, x], not [x, z].
                    _heights[vz, vx] = Mathf.Clamp01(_grid.GetMetres(vx, vz) * inv);
                }
            }

            _data.SetHeights(0, 0, _heights);

            LastSyncMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Punch or restore one cell -- the quad between four vertices, which is exactly
        /// what a terrain hole sample covers. This is the basis of the hybrid posture:
        /// heightmap surface, hole at the shaft mouth, generated geometry below.
        /// </summary>
        /// <summary>
        /// Set one cell solid or holed without pushing the change. Pair with CommitHoles:
        /// an excavation opens many cells at once, and calling SetHoles per cell uploads the
        /// whole mask every time.
        /// </summary>
        public void SetHole(int cx, int cz, bool solid)
        {
            if (!Valid) return;
            if (cx < 0 || cz < 0 || cx >= _grid.CellsX || cz >= _grid.CellsZ) return;

            int hx = Mathf.Clamp(cx * _holesRes / _grid.CellsX, 0, _holesRes - 1);
            int hz = Mathf.Clamp(cz * _holesRes / _grid.CellsZ, 0, _holesRes - 1);

            _solid[hz, hx] = solid;
        }

        /// <summary>Upload the hole mask once. Returns false if Unity rejected it.</summary>
        public bool CommitHoles()
        {
            if (!Valid) return false;

            try
            {
                _data.SetHoles(0, 0, _solid);   // false means hole
                LastHoleError = null;
            }
            catch (System.Exception e)
            {
                LastHoleError = e.GetType().Name;
                Debug.LogError(string.Format(
                    "[UnityTerrainView] SetHoles failed. holesResolution={0}, array={1}x{2}. {3}",
                    _holesRes, _solid.GetLength(0), _solid.GetLength(1), e));
                return false;
            }

            RecountHoles();
            return true;
        }

        public bool ToggleHole(int cx, int cz)
        {
            if (!Valid) return false;
            if (cx < 0 || cz < 0 || cx >= _grid.CellsX || cz >= _grid.CellsZ) return false;

            // Identity when holesResolution == cell count, which is the expected case.
            int hx = Mathf.Clamp(cx * _holesRes / _grid.CellsX, 0, _holesRes - 1);
            int hz = Mathf.Clamp(cz * _holesRes / _grid.CellsZ, 0, _holesRes - 1);

            _solid[hz, hx] = !_solid[hz, hx];

            try
            {
                _data.SetHoles(0, 0, _solid);   // false means hole
                LastHoleError = null;
            }
            catch (System.Exception e)
            {
                _solid[hz, hx] = !_solid[hz, hx];   // roll back so state stays truthful
                LastHoleError = e.GetType().Name;
                Debug.LogError(string.Format(
                    "[UnityTerrainView] SetHoles failed. holesResolution={0}, array={1}x{2}. {3}",
                    _holesRes, _solid.GetLength(0), _solid.GetLength(1), e));
                return false;
            }

            if (!_logged) { _logged = true; LogDiagnostics(hx, hz); }

            RecountHoles();
            return true;
        }

        /// <summary>
        /// Printed once on the first hole. Distinguishes "the data never got set" from
        /// "the data is set but the material is not drawing it", which look the same.
        /// </summary>
        void LogDiagnostics(int hx, int hz)
        {
            Material m = Terrain.materialTemplate;

            Debug.Log(string.Format(
                "[UnityTerrainView] first hole punched\n" +
                "  cells {0}x{1}   heightmapResolution {2}   holesResolution {3}\n" +
                "  wrote sample [{4},{5}] -> IsHole readback = {6}\n" +
                "  materialTemplate = {7}   shader = {8}\n" +
                "  Terrain.enabled = {9}   TerrainCollider.enabled = {10}",
                _grid.CellsX, _grid.CellsZ, _data.heightmapResolution, _holesRes,
                hx, hz, _data.IsHole(hx, hz),
                m == null ? "null (Unity built-in default)" : m.name,
                m == null || m.shader == null ? "-" : m.shader.name,
                Terrain.enabled, TerrainCollider.enabled));
        }

        void RecountHoles()
        {
            HoleCount = 0;
            for (int z = 0; z < _holesRes; z++)
                for (int x = 0; x < _holesRes; x++)
                    if (!_solid[z, x]) HoleCount++;
        }

        public void ClearHoles()
        {
            if (!Valid || HoleCount == 0) return;

            for (int z = 0; z < _holesRes; z++)
                for (int x = 0; x < _holesRes; x++) _solid[z, x] = true;

            _data.SetHoles(0, 0, _solid);
            HoleCount = 0;
            LastHoleError = null;
        }


        /// <summary>
        /// Show an imported surface instead of the flat colour, stretched once across the
        /// whole terrain so it lines up with the mesh view. Null restores the flat colour,
        /// otherwise pressing T would look like the texture had been lost.
        /// </summary>
        public void SetDiffuse(Texture2D diffuse, float spanX, float spanZ)
        {
            if (_layer == null) return;

            bool imported = diffuse != null;

            _layer.diffuseTexture = imported ? diffuse : _texture;
            _layer.tileSize = imported ? new Vector2(spanX, spanZ) : new Vector2(4f, 4f);
            _layer.tileOffset = Vector2.zero;

            // Reassigning the array is what makes the terrain pick the change up.
            if (_data != null) _data.terrainLayers = new[] { _layer };
        }

        void OnDestroy()
        {
            // All runtime-created, so nothing owns them but us.
            if (_data != null) Destroy(_data);
            if (_layer != null) Destroy(_layer);
            if (_texture != null) Destroy(_texture);
            if (_material != null) Destroy(_material);
        }
    }
}
