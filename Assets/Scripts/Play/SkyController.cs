using UnityEngine;
using UnityEngine.Rendering;
using Terraform.View;

namespace Terraform.Play
{
    /// <summary>
    /// Procedural sky, plus a few times of day to cycle through.
    ///
    /// More than decoration for this demo. Terrain relief is read almost entirely from
    /// shading, and a high midday sun flattens exactly the small steps and cut edges the
    /// playtest is asking people to judge. Raking light at golden hour makes a 0.1 m
    /// terrace obvious; flat overcast light hides it. Letting a tester swap between them
    /// is the cheapest way to find out whether an edge reads because of its shape or only
    /// because of where the sun happened to be.
    ///
    /// Skybox/Procedural needs no textures at all, which keeps the build free of art and
    /// avoids shipping cubemaps for a prototype.
    /// </summary>
    public sealed class SkyController : MonoBehaviour
    {
        public struct Preset
        {
            public string Name;
            public Vector2 SunAngles;      // x elevation, y azimuth
            public Color SunColour;
            public float SunIntensity;
            public LightShadows Shadows;
            public float ShadowStrength;
            public Color SkyTint;
            public Color GroundColour;
            public float Atmosphere;
            public float Exposure;
            public float Ambient;
        }

        static readonly Preset[] Presets =
        {
            new Preset {
                Name = "Midday",
                SunAngles = new Vector2(52f, 35f),
                SunColour = new Color(1f, 0.98f, 0.92f),
                SunIntensity = 1.05f,
                Shadows = LightShadows.Soft,
                ShadowStrength = 0.72f,
                SkyTint = new Color(0.52f, 0.60f, 0.72f),
                GroundColour = new Color(0.32f, 0.30f, 0.26f),
                Atmosphere = 1.0f,
                Exposure = 1.30f,
                Ambient = 1.0f,
            },
            new Preset {
                Name = "Low sun",
                SunAngles = new Vector2(13f, 118f),
                SunColour = new Color(1f, 0.80f, 0.58f),
                SunIntensity = 1.15f,
                Shadows = LightShadows.Soft,
                ShadowStrength = 0.85f,
                SkyTint = new Color(0.58f, 0.50f, 0.46f),
                GroundColour = new Color(0.28f, 0.24f, 0.20f),
                Atmosphere = 1.75f,
                Exposure = 1.15f,
                Ambient = 0.85f,
            },
            new Preset {
                Name = "Overcast",
                SunAngles = new Vector2(62f, 200f),
                SunColour = new Color(0.86f, 0.88f, 0.90f),
                SunIntensity = 0.55f,
                Shadows = LightShadows.None,
                ShadowStrength = 0f,
                SkyTint = new Color(0.62f, 0.64f, 0.66f),
                GroundColour = new Color(0.34f, 0.34f, 0.33f),
                Atmosphere = 0.45f,
                Exposure = 0.95f,
                Ambient = 1.25f,
            },
        };

        public Light Sun;
        public Camera Cam;

        /// <summary>Fallback when the sky material will not resolve, so nothing goes magenta.</summary>
        public Color FallbackColour = new Color(0.16f, 0.18f, 0.21f);

        Material _sky;
        int _index;

        public string CurrentName
        {
            get { return _sky == null ? "flat colour" : Presets[_index].Name; }
        }

        public void Initialise(Light sun, Camera cam)
        {
            Sun = sun;
            Cam = cam;

            _sky = MaterialLibrary.Build("sky", MaterialLibrary.Sky, MaterialLibrary.SkyShaders);

            if (_sky == null)
            {
                if (Cam != null)
                {
                    Cam.clearFlags = CameraClearFlags.SolidColor;
                    Cam.backgroundColor = FallbackColour;
                }
                return;
            }

            RenderSettings.skybox = _sky;
            RenderSettings.sun = Sun;
            RenderSettings.ambientMode = AmbientMode.Skybox;

            if (Cam != null) Cam.clearFlags = CameraClearFlags.Skybox;

            Apply(0);
        }

        public void Next()
        {
            if (_sky == null) return;
            Apply((_index + 1) % Presets.Length);
        }

        void Apply(int index)
        {
            _index = index;
            Preset p = Presets[index];

            if (_sky.HasProperty("_SkyTint")) _sky.SetColor("_SkyTint", p.SkyTint);
            if (_sky.HasProperty("_GroundColor")) _sky.SetColor("_GroundColor", p.GroundColour);
            if (_sky.HasProperty("_AtmosphereThickness")) _sky.SetFloat("_AtmosphereThickness", p.Atmosphere);
            if (_sky.HasProperty("_Exposure")) _sky.SetFloat("_Exposure", p.Exposure);
            if (_sky.HasProperty("_SunSize")) _sky.SetFloat("_SunSize", 0.035f);
            if (_sky.HasProperty("_SunDisk")) _sky.SetFloat("_SunDisk", 2f);   // high quality

            if (Sun != null)
            {
                Sun.transform.rotation = Quaternion.Euler(p.SunAngles.x, p.SunAngles.y, 0f);
                Sun.color = p.SunColour;
                Sun.intensity = p.SunIntensity;
                Sun.shadows = p.Shadows;
                if (p.Shadows != LightShadows.None) Sun.shadowStrength = p.ShadowStrength;
            }

            RenderSettings.ambientIntensity = p.Ambient;

            // Ambient light is derived from the sky, so it has to be recomputed or the
            // world keeps the previous preset's bounce while the sky itself changes.
            DynamicGI.UpdateEnvironment();
        }
    }
}
