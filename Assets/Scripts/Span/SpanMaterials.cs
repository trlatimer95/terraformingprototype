using UnityEngine;

namespace Terraform.Span
{
    /// <summary>
    /// The appearance gate's palette. Four materials is enough to judge whether exposed
    /// layers read at a glance, which is half of what this test is for -- a beautiful cave
    /// is not a success if you cannot tell which surface holds ore.
    ///
    /// Flat colours on purpose. Textures would be judging the art, and normal mapping is
    /// exactly the thing that must not be allowed to disguise stepped geometry.
    /// </summary>
    public static class SpanMaterials
    {
        public const byte Topsoil = 0;
        public const byte Subsoil = 1;
        public const byte Rock = 2;
        public const byte Coal = 3;
        public const byte Iron = 4;
        public const byte Copper = 5;
        public const byte Silver = 6;

        /// <summary>First and last ore, so callers can loop without naming each one.</summary>
        public const byte FirstOre = Coal;
        public const byte LastOre = Silver;

        public static bool IsOre(byte material)
        {
            return material >= FirstOre && material <= LastOre;
        }

        /// <summary>
        /// Intact ground, drawn by the spans only because something else had to stop.
        ///
        /// A terrain hole is a whole heightmap cell, so digging a small opening forces the
        /// surface renderer to drop a square metre of ground that is mostly still there. The
        /// spans have to draw that remainder, and it is NOT dug earth -- it is the same lawn
        /// as its neighbours and has to look like it. Giving it its own slot is what lets it
        /// be skinned to match the surface while cut faces are skinned as soil and rock.
        /// </summary>
        public const byte SurfaceSkin = 7;

        public const int Count = 8;

        public static readonly string[] Names =
        {
            "topsoil", "subsoil", "rock", "coal", "iron", "copper", "silver", "surface"
        };

        public static readonly Color[] Colours =
        {
            new Color(0.38f, 0.46f, 0.26f),   // topsoil, grassed
            new Color(0.42f, 0.33f, 0.22f),   // subsoil
            new Color(0.45f, 0.46f, 0.48f),   // rock
            new Color(0.10f, 0.10f, 0.11f),   // coal, near-black and dull
            new Color(0.48f, 0.29f, 0.19f),   // iron, rusted ochre
            new Color(0.24f, 0.52f, 0.44f),   // copper, oxidised green over a warm base
            new Color(0.78f, 0.80f, 0.84f),   // silver, bright and cold
            new Color(0.38f, 0.46f, 0.26f),   // intact ground, skinned to match the surface
        };
    }
}
