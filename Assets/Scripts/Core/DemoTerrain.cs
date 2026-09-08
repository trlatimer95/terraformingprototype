using UnityEngine;

namespace Terraform.Core
{
    /// <summary>
    /// The playtest world: a flat sandbox in the middle, ringed by ground worth trying
    /// tools on. Testers shouldn't have to spend ten minutes building a hill before they
    /// can find out how it feels to cut into one.
    ///
    /// One height function, sampled by BOTH models -- the cell model reads it at cell
    /// centres, the vertex model at vertices -- so the two are compared on identical
    /// ground rather than on whatever each tester happened to sculpt.
    ///
    /// This is also the shape a real world generator plugs into later: swap this for a
    /// Gaia heightmap or a noise field and nothing above it changes.
    /// </summary>
    public static class DemoTerrain
    {
        /// <summary>Radius of the flat middle, as a fraction of the world.</summary>
        const float ClearInner = 0.15f;
        const float ClearOuter = 0.29f;

        /// <summary>
        /// Height in metres at continuous grid coordinates. gx runs 0..cellsX.
        /// Slopes are kept under the 3 m max step so every tool still works on them.
        /// </summary>
        public static float SampleMetres(float gx, float gz, int cellsX, int cellsZ, float baseMetres)
        {
            float u = gx / cellsX;
            float v = gz / cellsZ;

            // Nothing encroaches on the sandbox in the middle.
            float dc = Distance(u, v, 0.5f, 0.5f);
            float clear = Smooth(Mathf.InverseLerp(ClearInner, ClearOuter, dc));
            if (clear <= 0f) return baseMetres;

            float h = 0f;

            h += Dome(u, v, 0.26f, 0.74f, 0.20f, 5.5f);          // NW: one clean hill
            h += Rolling(u, v, 0.74f, 0.74f);                     // NE: rolling ground
            h += Mountain(u, v, 0.74f, 0.26f);                    // SE: a small mountain
            h += Basin(u, v, 0.26f, 0.26f);                       // SW: a shallow bowl

            return baseMetres + h * clear;
        }

        /// <summary>A single smooth hill: the simplest thing to cut a pad into.</summary>
        static float Dome(float u, float v, float cu, float cv, float radius, float height)
        {
            float t = 1f - Mathf.Clamp01(Distance(u, v, cu, cv) / radius);
            return Smooth(t) * height;
        }

        /// <summary>Low broken ground -- no single right answer about where a pad goes.</summary>
        static float Rolling(float u, float v, float cu, float cv)
        {
            float mask = Smooth(1f - Mathf.Clamp01(Distance(u, v, cu, cv) / 0.26f));
            if (mask <= 0f) return 0f;

            float n = Mathf.PerlinNoise(u * 11f + 3.1f, v * 11f + 7.4f) - 0.5f;
            n += (Mathf.PerlinNoise(u * 23f + 11.7f, v * 23f + 2.9f) - 0.5f) * 0.45f;

            return n * 3.4f * mask;
        }

        /// <summary>
        /// Steeper and taller, with a little ridge structure so it is not just a cone.
        /// Still under the step limit per cell, so it can be dug into rather than only
        /// looked at.
        /// </summary>
        static float Mountain(float u, float v, float cu, float cv)
        {
            float d = Distance(u, v, cu, cv);
            float t = 1f - Mathf.Clamp01(d / 0.22f);
            if (t <= 0f) return 0f;

            float peak = Mathf.Pow(Smooth(t), 1.4f) * 11f;
            float ridge = (Mathf.PerlinNoise(u * 17f + 21.3f, v * 17f + 5.6f) - 0.5f) * 2.2f * t;

            return peak + ridge;
        }

        /// <summary>A dip, so lowering and backfilling get tested as well as piling.</summary>
        static float Basin(float u, float v, float cu, float cv)
        {
            float t = 1f - Mathf.Clamp01(Distance(u, v, cu, cv) / 0.20f);
            return -Smooth(t) * 2.6f;
        }

        static float Distance(float ax, float az, float bx, float bz)
        {
            float dx = ax - bx;
            float dz = az - bz;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        // ---- application ------------------------------------------------------

        /// <summary>Cell model: one sample per cell, taken at its centre.</summary>
        public static void Apply(CellGrid grid, float baseMetres)
        {
            for (int cz = 0; cz < grid.CellsZ; cz++)
                for (int cx = 0; cx < grid.CellsX; cx++)
                {
                    float m = SampleMetres(cx + 0.5f, cz + 0.5f, grid.CellsX, grid.CellsZ, baseMetres);
                    grid.SetRaw(cx, cz, CellGrid.SnapRaw(m, CellGrid.GenerationStepUnits));
                }
        }

        /// <summary>Vertex model: one sample per vertex, from the same function.</summary>
        public static void Apply(HeightGrid grid, float baseMetres)
        {
            for (int vz = 0; vz < grid.VertsZ; vz++)
                for (int vx = 0; vx < grid.VertsX; vx++)
                {
                    float m = SampleMetres(vx, vz, grid.CellsX, grid.CellsZ, baseMetres);
                    grid.SetRaw(vx, vz, HeightGrid.SnapRaw(m, HeightGrid.GenerationStepUnits));
                }
        }
    }
}
