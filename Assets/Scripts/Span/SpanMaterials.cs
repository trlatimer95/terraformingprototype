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
        public const byte Ore = 3;

        public const int Count = 4;

        public static readonly string[] Names = { "topsoil", "subsoil", "rock", "ore" };

        public static readonly Color[] Colours =
        {
            new Color(0.38f, 0.46f, 0.26f),   // topsoil, grassed
            new Color(0.42f, 0.33f, 0.22f),   // subsoil
            new Color(0.45f, 0.46f, 0.48f),   // rock
            new Color(0.55f, 0.31f, 0.20f),   // ore-bearing rock
        };
    }
}
