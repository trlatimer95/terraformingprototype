using UnityEngine;
using Terraform.Core;

namespace Terraform.View
{
    /// <summary>
    /// Samples a Unity Terrain already in the scene into either model, so the tools can be
    /// tried on generated ground instead of the hand-written demo map.
    ///
    /// It reads a Terrain, not any particular generator. Gaia, the built-in terrain tools,
    /// MapMagic and a plain heightmap import all leave the same thing behind, so any of
    /// them work as a source and none of them are a dependency of this project.
    ///
    /// The grid is far smaller than a generated world -- 64 m against a kilometre or more
    /// -- so this takes a WINDOW out of the source rather than the whole thing. Stride
    /// widens what that window covers, scaling heights to match so the result is a true
    /// miniature; at anything but 1 a cell no longer measures a metre, which makes it
    /// useful for choosing where to look and useless for judging how the tools feel.
    ///
    /// Deliberately outside Core. Core stays free of engine types beyond arithmetic, and
    /// that is the whole reason it would port cheaply to another engine.
    /// </summary>
    public static class TerrainImport
    {
        /// <summary>
        /// A snapshot of the terrains that were in the scene before we built our own.
        ///
        /// Taken once, up front, and passed around afterwards. Scanning Terrain.activeTerrains
        /// later would also find the vertex model's own comparison terrain and sample the
        /// prototype into itself.
        /// </summary>
        public sealed class Source
        {
            public Terrain[] Tiles;
            public Vector3 Min;
            public Vector3 Max;

            public bool Valid { get { return Tiles != null && Tiles.Length > 0; } }
            public Vector3 Size { get { return Max - Min; } }
        }

        /// <summary>
        /// Snapshot every active terrain now. Call before building anything of our own.
        /// Generated worlds are often tiled, so this keeps the whole set and picks the
        /// right tile per sample rather than assuming one.
        /// </summary>
        public static Source Capture()
        {
            Terrain[] active = Terrain.activeTerrains;

            int kept = 0;
            for (int i = 0; i < active.Length; i++)
                if (active[i] != null && active[i].terrainData != null) kept++;

            var src = new Source();
            src.Tiles = new Terrain[kept];

            int w = 0;
            for (int i = 0; i < active.Length; i++)
            {
                Terrain t = active[i];
                if (t == null || t.terrainData == null) continue;

                Vector3 origin = t.transform.position;
                Vector3 size = t.terrainData.size;

                if (w == 0)
                {
                    src.Min = origin;
                    src.Max = origin + size;
                }
                else
                {
                    src.Min = Vector3.Min(src.Min, origin);
                    src.Max = Vector3.Max(src.Max, origin + size);
                }

                src.Tiles[w++] = t;
            }

            return src;
        }

        /// <summary>Absolute world height of the source at a world XZ, in metres.</summary>
        public static float SampleMetres(Source src, float worldX, float worldZ)
        {
            Terrain t = TileAt(src, worldX, worldZ);
            if (t == null) return 0f;

            // SampleHeight is measured from the terrain's own transform, and is bilinear
            // between heightmap samples, so it does not care that our grid and the source
            // resolution disagree.
            return t.SampleHeight(new Vector3(worldX, 0f, worldZ)) + t.transform.position.y;
        }

        /// <summary>
        /// The tile covering this point. Falls back to the first tile so sampling just off
        /// the edge of a world clamps to its border rather than dropping to zero and
        /// cutting a cliff around the window.
        /// </summary>
        static Terrain TileAt(Source src, float x, float z)
        {
            if (!src.Valid) return null;

            for (int i = 0; i < src.Tiles.Length; i++)
            {
                Terrain t = src.Tiles[i];
                Vector3 o = t.transform.position;
                Vector3 s = t.terrainData.size;

                if (x >= o.x && x <= o.x + s.x && z >= o.z && z <= o.z + s.z) return t;
            }

            return src.Tiles[0];
        }

        /// <summary>Height fed into the models: the source, scaled to the window, then shifted.</summary>
        static float WindowMetres(Source src, Vector2 origin, float stride, float dx, float dz, float offset)
        {
            float raw = SampleMetres(src, origin.x + dx * stride, origin.y + dz * stride);
            return raw / stride + offset;
        }

        /// <summary>
        /// The vertical shift that lands the lowest point of the window on floorMetres.
        ///
        /// Computed once and handed to BOTH models. A generated world can sit hundreds of
        /// metres up, which would spawn the player underground and leave nothing to dig
        /// into; more importantly, if the two models rebased separately they would no
        /// longer be the same ground and the comparison would be worthless.
        /// </summary>
        public static float FitOffset(Source src, Vector2 origin, float stride,
                                      int cellsX, int cellsZ, float cellSize, float floorMetres)
        {
            if (!src.Valid) return 0f;

            float lowest = float.MaxValue;

            for (int vz = 0; vz <= cellsZ; vz++)
            {
                for (int vx = 0; vx <= cellsX; vx++)
                {
                    float h = SampleMetres(src, origin.x + vx * cellSize * stride,
                                                origin.y + vz * cellSize * stride) / stride;
                    if (h < lowest) lowest = h;
                }
            }

            return floorMetres - lowest;
        }

        /// <summary>Cell model: one sample per cell, taken at its centre.</summary>
        public static void Apply(CellGrid grid, Source src, Vector2 origin, float stride, float offset)
        {
            if (!src.Valid) return;

            for (int cz = 0; cz < grid.CellsZ; cz++)
            {
                for (int cx = 0; cx < grid.CellsX; cx++)
                {
                    float m = WindowMetres(src, origin, stride,
                                           (cx + 0.5f) * grid.CellSize,
                                           (cz + 0.5f) * grid.CellSize, offset);

                    grid.SetRaw(cx, cz, CellGrid.SnapRaw(m, CellGrid.GenerationStepUnits));
                    grid.SetFlat(cx, cz, false);
                }
            }
        }

        /// <summary>Vertex model: one sample per vertex, from the same window.</summary>
        public static void Apply(HeightGrid grid, Source src, Vector2 origin, float stride, float offset)
        {
            if (!src.Valid) return;

            for (int vz = 0; vz < grid.VertsZ; vz++)
                for (int vx = 0; vx < grid.VertsX; vx++)
                {
                    float m = WindowMetres(src, origin, stride,
                                           vx * grid.CellSize, vz * grid.CellSize, offset);

                    grid.SetRaw(vx, vz, HeightGrid.SnapRaw(m, HeightGrid.GenerationStepUnits));
                }
        }

        /// <summary>
        /// Steepest step between neighbouring cells in the window, in metres.
        ///
        /// Reported at import because generated ground routinely arrives steeper than the
        /// angle-of-repose guard allows. That is not a fault, but it does change what the
        /// tools will let you do, and it is better seen in the log than discovered as
        /// "the tools do not work on that hill".
        /// </summary>
        public static float SteepestStepMetres(CellGrid grid)
        {
            int worst = 0;

            for (int cz = 0; cz < grid.CellsZ; cz++)
                for (int cx = 0; cx < grid.CellsX; cx++)
                {
                    int here = grid.GetRaw(cx, cz);

                    if (cx + 1 < grid.CellsX)
                    {
                        int d = Mathf.Abs(here - grid.GetRaw(cx + 1, cz));
                        if (d > worst) worst = d;
                    }

                    if (cz + 1 < grid.CellsZ)
                    {
                        int d = Mathf.Abs(here - grid.GetRaw(cx, cz + 1));
                        if (d > worst) worst = d;
                    }
                }

            return worst * CellGrid.MetresPerUnit;
        }


        // ---- surface texture ---------------------------------------------------

        /// <summary>
        /// Everything needed to colour one source tile, worked out once and kept.
        ///
        /// Layer textures are copied through a RenderTexture rather than read directly:
        /// terrain layer textures are usually NOT marked readable, and GetPixel on those
        /// throws. A blit goes through the GPU and does not care.
        /// </summary>
        sealed class Palette
        {
            public float[,,] Weights;      // [z, x, layer]
            public int AlphaW;
            public int AlphaH;
            public Color32[][] Pixels;     // per layer, PixelSize square
            public Vector2[] TileSize;
            public Vector2[] TileOffset;
            public int PixelSize;
        }

        /// <summary>Size of the readable copy taken of each layer texture.</summary>
        const int LayerCopySize = 256;

        /// <summary>Below this weight a layer contributes nothing worth sampling.</summary>
        const float WeightFloor = 0.004f;

        static Palette BuildPalette(Terrain t)
        {
            TerrainData data = t.terrainData;
            TerrainLayer[] layers = data.terrainLayers;
            if (layers == null || layers.Length == 0) return null;

            var pal = new Palette();
            pal.AlphaW = data.alphamapWidth;
            pal.AlphaH = data.alphamapHeight;
            pal.Weights = data.GetAlphamaps(0, 0, pal.AlphaW, pal.AlphaH);
            pal.PixelSize = LayerCopySize;
            pal.Pixels = new Color32[layers.Length][];
            pal.TileSize = new Vector2[layers.Length];
            pal.TileOffset = new Vector2[layers.Length];

            for (int i = 0; i < layers.Length; i++)
            {
                TerrainLayer layer = layers[i];
                if (layer == null) continue;

                pal.TileSize[i] = layer.tileSize.x > 0.01f && layer.tileSize.y > 0.01f
                    ? layer.tileSize
                    : new Vector2(15f, 15f);
                pal.TileOffset[i] = layer.tileOffset;
                pal.Pixels[i] = CopyPixels(layer.diffuseTexture, LayerCopySize);
            }

            // Said out loud, because the symptom is otherwise indistinguishable from having
            // imported no terrain at all: the ground just renders as flat colour. A terrain
            // moved between projects loses its layer textures whenever the export brought the
            // TerrainLayer assets without the images they point at -- the layers survive, the
            // references dangle, and nothing complains.
            int missing = 0;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i] != null && layers[i].diffuseTexture == null) missing++;

            if (missing > 0)
            {
                Debug.LogWarningFormat(
                    "[TerrainImport] {0} of {1} terrain layers on '{2}' have no diffuse texture. " +
                    "The baked surface will be flat colour. Re-export the TerrainData WITH " +
                    "dependencies and keep the texture files.",
                    missing, layers.Length, t.name);
            }

            return pal;
        }

        /// <summary>A readable copy of a texture, whatever its import settings say.</summary>
        static Color32[] CopyPixels(Texture source, int size)
        {
            if (source == null) return null;

            RenderTexture rt = RenderTexture.GetTemporary(
                size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;

            var flat = new Texture2D(size, size, TextureFormat.RGBA32, false);
            flat.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
            flat.Apply(false, false);

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            Color32[] pixels = flat.GetPixels32();
            Object.Destroy(flat);
            return pixels;
        }

        /// <summary>
        /// Which layer the source paints most heavily at a point, cached per tile.
        ///
        /// The splatmap is the only record of what the ground is actually MADE of. Without
        /// it the material column underneath is invented -- a fixed depth of topsoil over a
        /// fixed depth of subsoil, everywhere -- so a bare rock face reads as rock from
        /// above and turns to grass and sand the moment anyone digs into it. The strata have
        /// to start from what the surface already says.
        /// </summary>
        public sealed class Splat
        {
            Source _src;
            readonly System.Collections.Generic.Dictionary<Terrain, float[,,]> _maps =
                new System.Collections.Generic.Dictionary<Terrain, float[,,]>();

            public TerrainLayer[] Layers { get; private set; }

            public static Splat Build(Source src)
            {
                if (!src.Valid) return null;

                var s = new Splat();
                s._src = src;

                var layers = new System.Collections.Generic.List<TerrainLayer>();

                for (int i = 0; i < src.Tiles.Length; i++)
                {
                    TerrainLayer[] tl = src.Tiles[i].terrainData.terrainLayers;
                    if (tl == null) continue;

                    for (int j = 0; j < tl.Length; j++)
                        if (tl[j] != null && !layers.Contains(tl[j])) layers.Add(tl[j]);
                }

                if (layers.Count == 0) return null;

                s.Layers = layers.ToArray();
                return s;
            }

            /// <summary>Name of the heaviest layer at a world position, lower-case. Empty if none.</summary>
            public string DominantName(float worldX, float worldZ)
            {
                Terrain tile = TileAt(_src, worldX, worldZ);
                if (tile == null) return string.Empty;

                float[,,] map;
                if (!_maps.TryGetValue(tile, out map))
                {
                    TerrainData td = tile.terrainData;
                    map = td.GetAlphamaps(0, 0, td.alphamapWidth, td.alphamapHeight);
                    _maps[tile] = map;
                }

                return DominantOn(tile, map, worldX, worldZ);
            }
        }

        /// <summary>
        /// Heaviest layer at a world position on ANY terrain, source or destination.
        ///
        /// Shared so the same question can be asked of both. When the ground looks like one
        /// material and the strata beneath it are built from another, exactly one of two
        /// things is wrong -- the reading, or the resample that produced what is drawn -- and
        /// they need opposite fixes. Two readers with their own indexing arithmetic could
        /// disagree without either being right; one reader cannot.
        /// </summary>
        public static string DominantOn(Terrain tile, float[,,] map, float worldX, float worldZ)
        {
            if (tile == null || map == null) return string.Empty;

            Vector3 o = tile.transform.position;
            Vector3 size = tile.terrainData.size;

            int w = map.GetLength(1);
            int h = map.GetLength(0);

            int sx = Mathf.Clamp(Mathf.FloorToInt((worldX - o.x) / size.x * w), 0, w - 1);
            int sz = Mathf.Clamp(Mathf.FloorToInt((worldZ - o.z) / size.z * h), 0, h - 1);

            TerrainLayer[] tl = tile.terrainData.terrainLayers;
            if (tl == null) return string.Empty;

            int count = Mathf.Min(tl.Length, map.GetLength(2));

            int best = -1;
            float bestWeight = 0f;

            for (int i = 0; i < count; i++)
            {
                if (map[sz, sx, i] <= bestWeight) continue;
                bestWeight = map[sz, sx, i];
                best = i;
            }

            if (best < 0 || tl[best] == null || tl[best].diffuseTexture == null) return string.Empty;
            return tl[best].diffuseTexture.name.ToLowerInvariant();
        }

        /// <summary>Where a source tile actually sits, for checking a window maps onto it.</summary>
        public static string TileBounds(Source src, float worldX, float worldZ)
        {
            Terrain t = TileAt(src, worldX, worldZ);
            if (t == null) return "no tile";

            Vector3 o = t.transform.position;
            Vector3 size = t.terrainData.size;

            bool inside = worldX >= o.x && worldX <= o.x + size.x
                       && worldZ >= o.z && worldZ <= o.z + size.z;

            return string.Format("tile {0:0}..{1:0} x {2:0}..{3:0}{4}",
                                 o.x, o.x + size.x, o.z, o.z + size.z,
                                 inside ? "" : "  OUTSIDE - clamped");
        }

        /// <summary>Heaviest layer actually painted on a terrain, read fresh.</summary>
        public static string DominantDrawn(Terrain t, float worldX, float worldZ)
        {
            if (t == null || t.terrainData == null) return string.Empty;

            TerrainData d = t.terrainData;
            float[,,] map = d.GetAlphamaps(0, 0, d.alphamapWidth, d.alphamapHeight);

            return DominantOn(t, map, worldX, worldZ);
        }

        /// <summary>
        /// The diffuse textures the source paints with, in its own layer order.
        ///
        /// Dug ground has to be made of something, and inventing a flat colour for it puts a
        /// painted patch in the middle of a photographed hillside -- which reads worse than
        /// no texture at all, because the eye has something to compare it against.
        /// Borrowing the terrain's own materials keeps the hole made of the same earth as
        /// the ground it is cut into.
        /// </summary>
        public static Texture2D[] LayerTextures(Source src)
        {
            if (!src.Valid) return null;

            var found = new System.Collections.Generic.List<Texture2D>();

            for (int i = 0; i < src.Tiles.Length; i++)
            {
                TerrainLayer[] layers = src.Tiles[i].terrainData.terrainLayers;
                if (layers == null) continue;

                for (int j = 0; j < layers.Length; j++)
                {
                    if (layers[j] == null || layers[j].diffuseTexture == null) continue;
                    if (!found.Contains(layers[j].diffuseTexture)) found.Add(layers[j].diffuseTexture);
                }
            }

            return found.Count == 0 ? null : found.ToArray();
        }

        /// <summary>
        /// Give a destination Unity Terrain the source's own terrain layers, and resample its
        /// splatmap into the window.
        ///
        /// This is the alternative to BakeDiffuse, and it is available for exactly one reason:
        /// a Unity Terrain blends multiple layers natively. The bake exists because the custom
        /// mesh is one material, so the layers have to be flattened into a single texture --
        /// and flattening throws away everything that made them worth having. A 2048 bake over
        /// 256 m is 8 pixels per metre, where the source tiles its textures at hundreds. The
        /// result is correct colour and no detail whatsoever.
        ///
        /// Here nothing needs flattening. The layers keep their own tiling, their normal maps,
        /// and their smoothness, and the standard terrain shader does the blending -- so no
        /// custom shader has to survive into a build either.
        ///
        /// The layer ASSETS are shared with the source, never modified: writing to them would
        /// permanently edit the imported files.
        ///
        /// Returns false when the source has no layers, leaving the caller to fall back.
        /// </summary>
        public static bool ApplyLayers(Terrain dest, Source src, Vector2 origin, float stride)
        {
            if (dest == null || dest.terrainData == null || !src.Valid) return false;

            // One combined layer list, so tiles that share a layer share a channel.
            var layers = new System.Collections.Generic.List<TerrainLayer>();
            var channel = new System.Collections.Generic.Dictionary<TerrainLayer, int>();

            for (int i = 0; i < src.Tiles.Length; i++)
            {
                TerrainLayer[] tileLayers = src.Tiles[i].terrainData.terrainLayers;
                if (tileLayers == null) continue;

                for (int j = 0; j < tileLayers.Length; j++)
                {
                    TerrainLayer layer = tileLayers[j];
                    if (layer == null || channel.ContainsKey(layer)) continue;

                    channel[layer] = layers.Count;
                    layers.Add(layer);
                }
            }

            if (layers.Count == 0) return false;

            // Copies, with smoothness and metallic taken out. The source layers are authored
            // for a different pipeline and arrive glossy enough to make a hillside look wet;
            // editing them in place would permanently alter the imported assets, which in the
            // editor means altering files on disk. Instantiate leaves the originals alone.
            for (int i = 0; i < layers.Count; i++)
            {
                TerrainLayer copy = Object.Instantiate(layers[i]);
                copy.name = layers[i].name + " (matte)";
                copy.smoothness = 0f;
                copy.metallic = 0f;
                layers[i] = copy;
            }

            TerrainData data = dest.terrainData;
            data.terrainLayers = layers.ToArray();

            int res = data.alphamapResolution;
            var weights = new float[res, res, layers.Count];

            float spanX = data.size.x;
            float spanZ = data.size.z;

            var cache = new System.Collections.Generic.Dictionary<Terrain, float[,,]>();

            for (int z = 0; z < res; z++)
            {
                float v = (z + 0.5f) / res;

                for (int x = 0; x < res; x++)
                {
                    float u = (x + 0.5f) / res;

                    float wx = origin.x + u * spanX * stride;
                    float wz = origin.y + v * spanZ * stride;

                    Terrain tile = TileAt(src, wx, wz);
                    if (tile == null) continue;

                    float[,,] source;
                    if (!cache.TryGetValue(tile, out source))
                    {
                        TerrainData td = tile.terrainData;
                        source = td.GetAlphamaps(0, 0, td.alphamapWidth, td.alphamapHeight);
                        cache[tile] = source;
                    }

                    TerrainLayer[] tileLayers = tile.terrainData.terrainLayers;
                    if (tileLayers == null || source == null) continue;

                    Vector3 tileOrigin = tile.transform.position;
                    Vector3 tileSize = tile.terrainData.size;

                    int aw = source.GetLength(1);
                    int ah = source.GetLength(0);

                    int sx = Mathf.Clamp(Mathf.FloorToInt((wx - tileOrigin.x) / tileSize.x * aw), 0, aw - 1);
                    int sz = Mathf.Clamp(Mathf.FloorToInt((wz - tileOrigin.z) / tileSize.z * ah), 0, ah - 1);

                    int count = Mathf.Min(tileLayers.Length, source.GetLength(2));

                    for (int j = 0; j < count; j++)
                    {
                        TerrainLayer layer = tileLayers[j];
                        if (layer == null) continue;

                        int c;
                        if (!channel.TryGetValue(layer, out c)) continue;

                        weights[z, x, c] = source[sz, sx, j];
                    }
                }
            }

            data.SetAlphamaps(0, 0, weights);

            Debug.LogFormat("[TerrainImport] {0} terrain layers applied live, splatmap resampled to {1}x{1}.",
                            layers.Count, res);
            return true;
        }

        /// <summary>
        /// Bake the painted surface of the window into a single texture.
        ///
        /// The models are one mesh with one material, so splatting the source's layers
        /// live would need a custom shader -- and a custom shader is another thing that
        /// has to survive into a player build. Flattening the layers into one map keeps
        /// the standard material, so nothing new can turn magenta in a build.
        ///
        /// Layer tiling is sampled at the SOURCE world position, so detail scales with
        /// stride the same way the heights do and a scouting view stays honest.
        ///
        /// Returns null when the source has no painted layers, which leaves the flat
        /// colour in place rather than producing a blank texture.
        /// </summary>
        public static Texture2D BakeDiffuse(Source src, Vector2 origin, float stride,
                                            float spanX, float spanZ, int resolution,
                                            Color fallback)
        {
            if (!src.Valid) return null;

            resolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(resolution), 64, 2048);

            var palettes = new System.Collections.Generic.Dictionary<Terrain, Palette>();
            var pixels = new Color32[resolution * resolution];
            bool painted = false;

            // Anything the source leaves unpainted keeps the flat colour rather than
            // coming out black, which is what an untouched Color32 would give.
            Color32 blank = fallback;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = blank;

            for (int py = 0; py < resolution; py++)
            {
                float v = (py + 0.5f) / resolution;

                for (int px = 0; px < resolution; px++)
                {
                    float u = (px + 0.5f) / resolution;

                    float wx = origin.x + u * spanX * stride;
                    float wz = origin.y + v * spanZ * stride;

                    Terrain tile = TileAt(src, wx, wz);
                    if (tile == null) continue;

                    Palette pal;
                    if (!palettes.TryGetValue(tile, out pal))
                    {
                        pal = BuildPalette(tile);
                        palettes[tile] = pal;
                    }

                    if (pal == null) continue;

                    Color c = SurfaceColour(tile, pal, wx, wz);
                    if (c.a <= 0f) continue;

                    painted = true;

                    // Alpha forced to zero. In these source sets the diffuse alpha carries
                    // SMOOTHNESS, not coverage -- the textures are named A_SM for exactly
                    // that -- and Unity terrain shaders read smoothness from the splat
                    // alpha, so passing it through varnished the whole landscape. Zero is
                    // fully rough, which is what soil and grass are. Coverage was already
                    // decided by the test above, so nothing is lost.
                    c.a = 0f;
                    pixels[py * resolution + px] = c;
                }
            }

            if (!painted) return null;

            var baked = new Texture2D(resolution, resolution, TextureFormat.RGBA32, true);
            baked.name = "ImportedSurface";
            baked.wrapMode = TextureWrapMode.Clamp;
            baked.SetPixels32(pixels);
            baked.Apply(true, false);

            return baked;
        }

        /// <summary>Blend of the layers painted at one world position.</summary>
        static Color SurfaceColour(Terrain tile, Palette pal, float wx, float wz)
        {
            Vector3 tileOrigin = tile.transform.position;
            Vector3 tileSize = tile.terrainData.size;

            // Alphamap sampled bilinearly: a generated world often paints at 1 sample per
            // metre while we bake many pixels per metre, and point sampling would tile the
            // ground with visible one-metre squares.
            float ax = Mathf.Clamp01((wx - tileOrigin.x) / tileSize.x) * (pal.AlphaW - 1);
            float az = Mathf.Clamp01((wz - tileOrigin.z) / tileSize.z) * (pal.AlphaH - 1);

            int x0 = Mathf.FloorToInt(ax); int x1 = Mathf.Min(x0 + 1, pal.AlphaW - 1);
            int z0 = Mathf.FloorToInt(az); int z1 = Mathf.Min(z0 + 1, pal.AlphaH - 1);
            float fx = ax - x0;
            float fz = az - z0;

            float r = 0f, g = 0f, b = 0f, total = 0f;
            int count = pal.Weights.GetLength(2);

            for (int i = 0; i < count; i++)
            {
                if (pal.Pixels[i] == null) continue;

                float w = Mathf.Lerp(
                    Mathf.Lerp(pal.Weights[z0, x0, i], pal.Weights[z0, x1, i], fx),
                    Mathf.Lerp(pal.Weights[z1, x0, i], pal.Weights[z1, x1, i], fx), fz);

                if (w <= WeightFloor) continue;

                Color32 sample = Tap(pal, i, wx, wz);
                r += sample.r * w;
                g += sample.g * w;
                b += sample.b * w;
                total += w;
            }

            if (total <= 0f) return new Color(0f, 0f, 0f, 0f);

            float inv = 1f / (total * 255f);
            return new Color(r * inv, g * inv, b * inv, 1f);
        }

        /// <summary>
        /// One layer texture at a world position, honouring its tiling. Point sampled --
        /// the copies are small and the bake is denser than they are, so filtering here
        /// buys nothing the alphamap blend has not already smoothed.
        /// </summary>
        static Color32 Tap(Palette pal, int layer, float wx, float wz)
        {
            Vector2 size = pal.TileSize[layer];
            Vector2 offset = pal.TileOffset[layer];

            float u = (wx - offset.x) / size.x;
            float v = (wz - offset.y) / size.y;

            int n = pal.PixelSize;
            int px = Mathf.FloorToInt((u - Mathf.Floor(u)) * n);
            int py = Mathf.FloorToInt((v - Mathf.Floor(v)) * n);

            px = Mathf.Clamp(px, 0, n - 1);
            py = Mathf.Clamp(py, 0, n - 1);

            return pal.Pixels[layer][py * n + px];
        }

        /// <summary>
        /// Take the source out of the scene once it has been read.
        ///
        /// Our world is built at the origin and would sit inside the source terrain,
        /// which would collide with the player and z-fight the model surface. The heights
        /// have already been copied, so nothing is lost by hiding it.
        /// </summary>
        public static void Hide(Source src)
        {
            if (!src.Valid) return;

            for (int i = 0; i < src.Tiles.Length; i++)
                if (src.Tiles[i] != null) src.Tiles[i].gameObject.SetActive(false);
        }
    }
}
