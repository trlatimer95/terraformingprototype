using UnityEngine;

namespace Terraform.View
{
    /// <summary>
    /// Where the demo's materials come from, and why they come from there.
    ///
    /// The obvious way to make a code-built material survive a player build is to force its
    /// shader into Always Included Shaders. That works for small shaders and is a trap for
    /// large ones: with no material to inform stripping, Unity has to compile EVERY variant
    /// of the shader. For Standard that is tens of thousands; for Nature/Terrain/Standard
    /// the pass matrix is large enough that the stripper itself crashes the build inside
    /// BuildExtraResourcesBundleForPlayer.
    ///
    /// A material asset under Resources/ has the opposite property. It is a real reference,
    /// so the shader ships -- but only the variants that material actually uses. Same
    /// guarantee, a fraction of the compile, and no build crash.
    ///
    /// Shader.Find stays as a fallback so the project still runs in the editor if the
    /// assets have not been generated yet. It is NOT a substitute in a build: Shader.Find
    /// returns null for anything nothing references, which is how the world renders magenta.
    /// </summary>
    public static class MaterialLibrary
    {
        /// <summary>Folder under Resources/ holding the generated materials.</summary>
        public const string Root = "P0/";

        public const string Lit = "Lit";
        public const string Unlit = "Unlit";
        public const string TerrainSurface = "Terrain";
        public const string Sky = "Sky";

        /// <summary>
        /// A fresh material for the given role. Prefers the Resources asset, falls back to
        /// the first shader name that resolves.
        /// </summary>
        public static Material Build(string label, string assetName, params string[] shaderNames)
        {
            Material asset = Resources.Load<Material>(Root + assetName);

            if (asset != null && asset.shader != null)
            {
                // A copy, always. Writing to the loaded asset would edit the project file
                // itself the moment this runs in the editor.
                var instance = new Material(asset);
                instance.name = "P0 " + assetName;

                Debug.Log("[Materials] " + label + " <- Resources/" + Root + assetName +
                          " (" + asset.shader.name + ")");
                return instance;
            }

            for (int i = 0; i < shaderNames.Length; i++)
            {
                Shader s = Shader.Find(shaderNames[i]);
                if (s == null) continue;

                Debug.LogWarning("[Materials] " + label + " <- Shader.Find(\"" + s.name + "\"). " +
                                 "No Resources material, so this will NOT survive a player build. " +
                                 "Run Tools > Terraform > Prepare Build.");
                return new Material(s);
            }

            Debug.LogError("[Materials] nothing resolved for " + label +
                           " (tried Resources/" + Root + assetName +
                           " then: " + string.Join(", ", shaderNames) + "). Will render magenta.");
            return null;
        }

        /// <summary>Shader candidates per role, shared by the runtime and the asset generator.</summary>
        public static string[] LitShaders
        {
            get { return new[] { "Universal Render Pipeline/Lit", "Standard", "Legacy Shaders/Diffuse", "Diffuse" }; }
        }

        // Hidden/Internal-Colored is deliberately absent. It lives in "unity default
        // resources" -- the EDITOR's bundle, not unity_builtin_extra -- so referencing it
        // from anything a player build touches crashes the build while stripping it.
        public static string[] UnlitShaders
        {
            get { return new[] { "Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default" }; }
        }

        public static string[] TerrainShaders
        {
            get { return new[] { "Nature/Terrain/Standard", "Universal Render Pipeline/Terrain/Lit", "Nature/Terrain/Diffuse" }; }
        }

        // Procedural first: it needs no textures, so the build ships no sky art at all.
        // The cubemap variants are only here so a hand-authored Sky.mat still resolves.
        public static string[] SkyShaders
        {
            get { return new[] { "Skybox/Procedural", "Skybox/Cubemap", "Skybox/6 Sided" }; }
        }
    }
}
