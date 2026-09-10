using UnityEngine;
using Terraform.Span;

namespace Terraform.View
{
    /// <summary>
    /// Ore textures built from the terrain's own rock.
    ///
    /// Painting them by hand, or shipping four more image files, would give four surfaces
    /// with no relationship to the ground they are cut into -- and mismatched rock reads as
    /// a bug even when every individual texture is good. Starting from the rock the terrain
    /// already uses means the grain, the lighting response and the colour temperature are
    /// right before a single mineral is added.
    ///
    /// The minerals themselves are inclusions scattered over that base: dense dull blobs for
    /// coal, streaky rust for iron, oxidised patches for copper, sparse bright flecks for
    /// silver. Each is a deterministic function of a seed, so they are identical every run
    /// and cost nothing to store.
    ///
    /// Inclusions wrap at the edges, because these tile across a wall. A blob clipped at the
    /// border would put a visible seam on every metre of every tunnel.
    /// </summary>
    public static class OreTextures
    {
        /// <summary>
        /// One ore texture. Returns null if the base rock could not be read, which leaves the
        /// caller on its flat colour rather than producing something worse.
        /// </summary>
        public static Texture2D Build(Texture2D rock, byte material, int size = 512)
        {
            if (rock == null) return null;

            Color32[] basePixels = Blit(rock, size);
            if (basePixels == null) return null;

            Profile p = For(material);
            var rng = new Random(p.Seed);

            for (int i = 0; i < p.Blobs; i++)
            {
                float cx = rng.Unit() * size;
                float cy = rng.Unit() * size;

                float radius = Mathf.Lerp(p.MinRadius, p.MaxRadius, rng.Unit()) * (size / 512f);
                float stretch = Mathf.Lerp(1f, p.Stretch, rng.Unit());
                float angle = rng.Unit() * Mathf.PI;

                Splat(basePixels, size, cx, cy, radius, stretch, angle, p, rng);
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            tex.name = "Ore " + SpanMaterials.Names[material];
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.SetPixels32(basePixels);
            tex.Apply(true, false);

            return tex;
        }

        struct Profile
        {
            public int Seed;
            public int Blobs;
            public float MinRadius;
            public float MaxRadius;
            public float Stretch;      // 1 = round, higher = drawn out into a streak
            public Color Tint;
            public float Strength;     // how far toward the tint an inclusion pulls the rock
            public float Sparkle;      // extra brightness at the centre, for metals
        }

        static Profile For(byte material)
        {
            switch (material)
            {
                case SpanMaterials.Coal:
                    // Dense, dark and matte. Coal should read as a bed, not as flecks.
                    return new Profile
                    {
                        Seed = 11, Blobs = 260, MinRadius = 8f, MaxRadius = 34f,
                        Stretch = 2.2f, Tint = new Color(0.05f, 0.05f, 0.06f),
                        Strength = 0.92f, Sparkle = 0f
                    };

                case SpanMaterials.Iron:
                    // Rust bleeds along bedding, so long and streaky rather than round.
                    return new Profile
                    {
                        Seed = 23, Blobs = 200, MinRadius = 6f, MaxRadius = 26f,
                        Stretch = 3.4f, Tint = new Color(0.42f, 0.20f, 0.10f),
                        Strength = 0.72f, Sparkle = 0.06f
                    };

                case SpanMaterials.Copper:
                    // Oxidised green over a warm core, in patches rather than veins.
                    return new Profile
                    {
                        Seed = 37, Blobs = 150, MinRadius = 5f, MaxRadius = 20f,
                        Stretch = 1.7f, Tint = new Color(0.16f, 0.55f, 0.44f),
                        Strength = 0.70f, Sparkle = 0.10f
                    };

                default:
                    // Sparse and small. Silver is worth something because you have to look.
                    return new Profile
                    {
                        Seed = 53, Blobs = 90, MinRadius = 2f, MaxRadius = 7f,
                        Stretch = 1.4f, Tint = new Color(0.86f, 0.88f, 0.92f),
                        Strength = 0.85f, Sparkle = 0.30f
                    };
            }
        }

        static void Splat(Color32[] pixels, int size,
                          float cx, float cy, float radius, float stretch, float angle,
                          Profile p, Random rng)
        {
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);

            int reach = Mathf.CeilToInt(radius * Mathf.Max(1f, stretch)) + 1;

            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    // Rotated and stretched, so a streak has a direction.
                    float rx = (dx * cos + dy * sin) / stretch;
                    float ry = -dx * sin + dy * cos;

                    float d = Mathf.Sqrt(rx * rx + ry * ry) / radius;
                    if (d >= 1f) continue;

                    // Soft edge with a ragged bite taken out, so inclusions do not read as
                    // circles stamped on rock.
                    float falloff = 1f - d * d;
                    falloff *= 0.65f + 0.35f * rng.Unit();

                    // Wrapped: these tile, and a clipped blob is a seam on every wall.
                    int x = Wrap(Mathf.RoundToInt(cx) + dx, size);
                    int y = Wrap(Mathf.RoundToInt(cy) + dy, size);

                    int index = y * size + x;
                    Color32 c = pixels[index];

                    float amount = Mathf.Clamp01(falloff * p.Strength);
                    float lift = p.Sparkle * Mathf.Clamp01(falloff * falloff);

                    pixels[index] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(c.r, p.Tint.r * 255f, amount) + lift * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(c.g, p.Tint.g * 255f, amount) + lift * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(c.b, p.Tint.b * 255f, amount) + lift * 255f), 0, 255),
                        255);
                }
            }
        }

        static int Wrap(int v, int size)
        {
            v %= size;
            return v < 0 ? v + size : v;
        }

        /// <summary>
        /// Read a texture that is very likely compressed and not marked readable, via a
        /// render target. Same reason as TerrainImport.CopyPixels: imported terrain textures
        /// are not readable and GetPixels on them throws.
        /// </summary>
        static Color32[] Blit(Texture2D source, int size)
        {
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
        /// Small deterministic generator. Not UnityEngine.Random: that is global state, so
        /// generating these would perturb every other random draw in the frame and the
        /// textures would depend on what else happened to run first.
        /// </summary>
        sealed class Random
        {
            uint _state;

            public Random(int seed) { _state = (uint)(seed * 747796405 + 2891336453); }

            public float Unit()
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return (_state & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }
}
