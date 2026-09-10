using System.Collections.Generic;
using UnityEngine;
using Terraform.Span;

namespace Terraform.Core
{
    /// <summary>
    /// Where the ore is: a function of position, of what the ground is like there, and of a
    /// seed the server keeps to itself.
    ///
    /// The first version was position alone, which made it deterministic but arbitrary -- a
    /// deposit had no reason to be where it was, or to be one mineral rather than another.
    /// Reading the terrain gives the distribution a shape a player can reason about before
    /// digging: silver in the peaks, coal in the low ground. Prospecting becomes a judgement
    /// instead of a lottery.
    ///
    /// **The seed is the secret, not a table.** A field anyone can evaluate is a field anyone
    /// can read the answers out of, which turns prospecting into a lookup for whoever is
    /// willing to run the arithmetic offline. Generating a private seed once, when a world is
    /// created, and never sending it to a client closes that without storing a deposit list:
    /// the server regenerates any deposit on demand, clients learn where ore is only by
    /// exposing it, and persistence is one integer rather than a table that has to be
    /// replicated, versioned and migrated.
    ///
    /// It cannot stop a client adding ore either way -- the span store is server state and
    /// mining is a server-validated edit -- so what the seed protects is knowledge, not
    /// integrity. Worth being clear about which of the two is at stake.
    ///
    /// Deposits sit near the surface on purpose. Ore forty metres down is ore nobody finds,
    /// and the question this prototype has to answer is whether digging for it feels like
    /// anything, which needs it findable.
    /// </summary>
    public sealed class OreField
    {
        /// <summary>Spacing of the candidate lattice, in metres.</summary>
        public const float CellMetres = 22f;

        /// <summary>Fraction of lattice cells that hold a deposit.</summary>
        public const float Density = 0.55f;

        /// <summary>
        /// Deeper than any deposit reaches, so a column can stop sampling and emit one span
        /// for everything below. Small, because the deposits are shallow -- which is what
        /// keeps the per-column slice walk cheap.
        /// </summary>
        public const float MaxDepth = 8f;

        /// <summary>Vertical step when resolving a column into seams.</summary>
        public const float SliceMetres = 0.5f;

        /// <summary>
        /// What the ground is like at a point: how high it stands relative to the rest of the
        /// world, and whether rock reaches the surface there.
        /// </summary>
        public delegate void Probe(float worldX, float worldZ, out float elevation01, out bool rocky);

        /// <summary>
        /// Server-side world state. Rolled once when a world is created and persisted with
        /// it; never sent to a client. Two worlds with the same seed and the same terrain have
        /// the same ore, which is what makes a saved world reloadable without storing one.
        /// </summary>
        public readonly uint Seed;

        readonly Probe _probe;
        readonly Dictionary<long, Deposit> _cache = new Dictionary<long, Deposit>();

        public OreField(Probe probe, uint seed)
        {
            _probe = probe;
            Seed = seed == 0u ? 1u : seed;
        }

        struct Deposit
        {
            public bool Exists;
            public float X, Z;

            /// <summary>Metres below the ROCK HEAD -- not below the ground, not an altitude.</summary>
            public float Depth;

            public float Radius;
            public float Thickness;
            public byte Material;
            public bool Outcrops;
        }

        public struct Found
        {
            public float X, Z;
            public float Depth;
            public float Radius;
            public float Thickness;
            public byte Material;
            public bool Outcrops;
        }

        /// <summary>Mineral at a point and depth below the rock head, or Rock if none.</summary>
        public byte MaterialAt(float worldX, float depthBelowRockHead, float worldZ)
        {
            int cx = Mathf.FloorToInt(worldX / CellMetres);
            int cz = Mathf.FloorToInt(worldZ / CellMetres);

            // The neighbours too: a deposit near a cell edge reaches across it, and testing
            // only the containing cell would clip every one of them into a straight line.
            for (int j = -1; j <= 1; j++)
            {
                for (int i = -1; i <= 1; i++)
                {
                    Deposit d = At(cx + i, cz + j);
                    if (!d.Exists) continue;

                    float dx = worldX - d.X;
                    float dz = worldZ - d.Z;

                    if (dx * dx + dz * dz > d.Radius * d.Radius) continue;
                    if (Mathf.Abs(depthBelowRockHead - d.Depth) > d.Thickness * 0.5f) continue;

                    return d.Material;
                }
            }

            return SpanMaterials.Rock;
        }

        /// <summary>
        /// Every deposit whose centre falls in a world rectangle, nearest first.
        ///
        /// A development and server-side tool. Handing this to a client would give away the
        /// thing the seed exists to protect.
        /// </summary>
        public List<Found> Enumerate(float minX, float minZ, float maxX, float maxZ, Vector2 near)
        {
            var found = new List<Found>();

            int cx0 = Mathf.FloorToInt(minX / CellMetres) - 1;
            int cz0 = Mathf.FloorToInt(minZ / CellMetres) - 1;
            int cx1 = Mathf.FloorToInt(maxX / CellMetres) + 1;
            int cz1 = Mathf.FloorToInt(maxZ / CellMetres) + 1;

            for (int cz = cz0; cz <= cz1; cz++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    Deposit d = At(cx, cz);
                    if (!d.Exists) continue;
                    if (d.X < minX || d.X > maxX || d.Z < minZ || d.Z > maxZ) continue;

                    found.Add(new Found
                    {
                        X = d.X, Z = d.Z,
                        Depth = d.Depth,
                        Radius = d.Radius,
                        Thickness = d.Thickness,
                        Material = d.Material,
                        Outcrops = d.Outcrops
                    });
                }
            }

            found.Sort((a, b) =>
            {
                float da = (a.X - near.x) * (a.X - near.x) + (a.Z - near.y) * (a.Z - near.y);
                float db = (b.X - near.x) * (b.X - near.x) + (b.Z - near.y) * (b.Z - near.y);
                return da.CompareTo(db);
            });

            return found;
        }

        Deposit At(int cx, int cz)
        {
            long key = ((long)cx << 32) ^ (uint)cz;

            Deposit cached;
            if (_cache.TryGetValue(key, out cached)) return cached;

            Deposit d = Build(cx, cz);
            _cache[key] = d;
            return d;
        }

        /// <summary>
        /// Preferred height of each mineral, and how far from it it will still appear.
        ///
        /// Overlapping on purpose. Hard bands would draw a visible contour across the map
        /// where one mineral stopped and the next began; overlapping ones let the mix shift
        /// as you climb, which is what makes elevation a clue rather than a rule.
        /// </summary>
        static readonly float[] Prefer = { 0.15f, 0.62f, 0.42f, 0.90f };   // coal, iron, copper, silver
        static readonly float[] Tolerate = { 0.42f, 0.34f, 0.34f, 0.30f };

        static readonly byte[] Minerals =
        {
            SpanMaterials.Coal, SpanMaterials.Iron, SpanMaterials.Copper, SpanMaterials.Silver
        };

        Deposit Build(int cx, int cz)
        {
            var d = new Deposit();

            uint h = Hash(Seed ^ (uint)(cx * 73856093) ^ (uint)(cz * 19349663));

            d.Exists = Unit(h) < Density;
            if (!d.Exists) return d;

            h = Hash(h);
            float jx = (Unit(h) - 0.5f) * CellMetres * 0.7f;

            h = Hash(h);
            float jz = (Unit(h) - 0.5f) * CellMetres * 0.7f;

            d.X = (cx + 0.5f) * CellMetres + jx;
            d.Z = (cz + 0.5f) * CellMetres + jz;

            float elevation = 0.5f;
            bool rocky = false;
            if (_probe != null) _probe(d.X, d.Z, out elevation, out rocky);

            // Weighted by how close this ground is to each mineral's preferred height, then
            // drawn from that weighting -- so a peak usually gives silver and sometimes gives
            // iron, rather than always giving silver.
            float total = 0f;
            var weight = new float[Minerals.Length];

            for (int i = 0; i < Minerals.Length; i++)
            {
                weight[i] = Mathf.Max(0f, 1f - Mathf.Abs(elevation - Prefer[i]) / Tolerate[i]);
                total += weight[i];
            }

            h = Hash(h);
            float pick = Unit(h) * (total > 0.0001f ? total : 1f);

            d.Material = Minerals[0];

            for (int i = 0; i < Minerals.Length; i++)
            {
                pick -= weight[i];
                if (pick > 0f) continue;

                d.Material = Minerals[i];
                break;
            }

            h = Hash(h);
            float spread = Unit(h);

            d.Radius = Mathf.Lerp(3f, 7f, spread);
            d.Thickness = Mathf.Lerp(1.5f, 3.5f, spread);

            // Where rock reaches the surface the rock head IS the surface, so a deposit
            // centred near zero breaks ground. Everywhere else it stays under a metre or two
            // of rock, which is still within a couple of swings.
            h = Hash(h);
            d.Outcrops = rocky && Unit(h) < 0.55f;

            h = Hash(h);
            d.Depth = d.Outcrops
                ? Mathf.Lerp(-0.5f, 1.5f, Unit(h))
                : Mathf.Lerp(1f, 5f, Unit(h));

            return d;
        }

        /// <summary>
        /// Integer avalanche. Adjacent lattice cells must not correlate, or the deposits line
        /// up into rows.
        /// </summary>
        static uint Hash(uint x)
        {
            x ^= 2747636419u;
            x *= 2654435769u;
            x ^= x >> 16;
            x *= 2654435769u;
            x ^= x >> 16;
            x *= 2654435769u;
            return x;
        }

        static float Unit(uint h) { return (h & 0xFFFFFF) / (float)0x1000000; }
    }
}
