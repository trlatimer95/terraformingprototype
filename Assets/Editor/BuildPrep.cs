using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using Terraform.Play;
using Terraform.View;

namespace Terraform.EditorTools
{
    /// <summary>
    /// Makes the project buildable, and keeps it that way.
    ///
    /// The demo builds its materials in code, which a player build cannot see -- nothing
    /// references those shaders, so Shader.Find returns null and the world renders magenta.
    ///
    /// The first fix here was to force the shaders into Always Included Shaders. That is
    /// wrong for the large ones: with no material to inform stripping, Unity compiles EVERY
    /// variant, and for Nature/Terrain/Standard the pass matrix is large enough that the
    /// shader stripper crashes the editor inside BuildExtraResourcesBundleForPlayer.
    ///
    /// Materials under Resources/ give the same guarantee for a fraction of the compile, so
    /// that is what ships now -- and the banned entries are actively removed from projects
    /// that still carry them from the earlier approach.
    /// </summary>
    public sealed class BuildPrep : IPreprocessBuildWithReport
    {
        /// <summary>
        /// Nothing is added to Always Included Shaders any more. Every material this demo
        /// needs reaches the player through a Resources asset instead, which is both safer
        /// and cheaper -- see the class comment. This list exists to take entries OUT.
        ///
        /// Two separate failures came from putting shaders here: oversized variant matrices
        /// (Standard, Nature/Terrain/Standard) and editor-internal shaders that must never
        /// be in a player build at all (Hidden/Internal-Colored).
        /// </summary>
        static readonly string[] BannedFromAlwaysIncluded =
        {
            "Standard",
            "Nature/Terrain/Standard",
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Terrain/Lit",
            "Hidden/Internal-Colored",
        };

        const string ResourceDir = "Assets/Resources/P0";

        public int callbackOrder { get { return 0; } }

        /// <summary>
        /// Deliberately creates nothing and never calls AssetDatabase.SaveAssets. Writing
        /// assets from inside a build callback is its own source of instability, so this
        /// only removes the entries that would crash the build, and warns about the rest.
        /// </summary>
        public void OnPreprocessBuild(BuildReport report)
        {
            int removed = PruneAlwaysIncluded();
            if (removed > 0)
                Debug.LogWarning("[BuildPrep] Removed " + removed + " shader(s) from Always Included " +
                                 "Shaders that would have crashed the shader stripper.");

            foreach (string name in MaterialNames)
            {
                if (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath(name)) != null) continue;

                Debug.LogWarning("[BuildPrep] Missing " + MaterialPath(name) +
                                 ". Run Tools > Terraform > Prepare Build before building, " +
                                 "or this build may render magenta.");
            }
        }

        [MenuItem("Tools/Terraform/Prepare Build")]
        public static void PrepareBuild()
        {
            int removed = PruneAlwaysIncluded();
            int created = CreateMaterials();
            EnsureSceneInBuild();

            AssetDatabase.SaveAssets();

            Debug.Log("[BuildPrep] ready to build.\n" +
                      "  materials created  " + created + " (existing ones left alone)\n" +
                      "  shaders removed    " + removed + " from Always Included");
        }

        // ---- always included shaders -------------------------------------------

        static SerializedProperty AlwaysIncluded(out SerializedObject settings)
        {
            settings = null;

            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("[BuildPrep] Could not open GraphicsSettings.asset.");
                return null;
            }

            settings = new SerializedObject(assets[0]);

            SerializedProperty list = settings.FindProperty("m_AlwaysIncludedShaders");
            if (list == null) Debug.LogWarning("[BuildPrep] m_AlwaysIncludedShaders not found.");

            return list;
        }

        /// <summary>Strip the oversized shaders, including ones an earlier version of this added.</summary>
        static int PruneAlwaysIncluded()
        {
            SerializedObject settings;
            SerializedProperty list = AlwaysIncluded(out settings);
            if (list == null) return 0;

            var banned = new HashSet<Shader>();
            foreach (string name in BannedFromAlwaysIncluded)
            {
                Shader s = Shader.Find(name);
                if (s != null) banned.Add(s);
            }

            int removed = 0;

            // Backwards: deleting an element shifts everything after it.
            for (int i = list.arraySize - 1; i >= 0; i--)
            {
                var shader = list.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (shader == null) continue;
                if (!banned.Contains(shader) && !IsEditorInternal(shader)) continue;

                // A populated object reference clears before the element itself deletes.
                list.GetArrayElementAtIndex(i).objectReferenceValue = null;
                list.DeleteArrayElementAtIndex(i);
                removed++;
            }

            if (removed > 0) settings.ApplyModifiedProperties();
            return removed;
        }

        /// <summary>
        /// True for shaders that live in the editor's own resource bundle rather than the
        /// one shipped with players.
        ///
        /// This is the general form of the Hidden/Internal-Colored failure, and it is worth
        /// having as a rule rather than a name: the built-ins a player build may reference
        /// come from "unity_builtin_extra", and anything from "unity default resources" is
        /// editor-only. Unity says as much -- "You are probably referencing internal Unity
        /// data in your build" -- and then dies in the shader stripper.
        /// </summary>
        static bool IsEditorInternal(Shader shader)
        {
            string path = AssetDatabase.GetAssetPath(shader);
            return !string.IsNullOrEmpty(path) && path.Contains("unity default resources");
        }

        // ---- resources materials -----------------------------------------------

        static string[] MaterialNames
        {
            get { return new[] { MaterialLibrary.Lit, MaterialLibrary.Unlit, MaterialLibrary.TerrainSurface, MaterialLibrary.Sky }; }
        }

        static string MaterialPath(string name)
        {
            return ResourceDir + "/" + name + ".mat";
        }

        /// <summary>
        /// One material per role, created through AssetDatabase so Unity resolves the
        /// built-in shader references itself. These are what carry the shaders into the
        /// build, and they pull only the variants they use.
        /// </summary>
        static int CreateMaterials()
        {
            Directory.CreateDirectory(ResourceDir);
            AssetDatabase.Refresh();

            int created = 0;

            created += Create(MaterialLibrary.Lit, MaterialLibrary.LitShaders) ? 1 : 0;
            created += Create(MaterialLibrary.Unlit, MaterialLibrary.UnlitShaders) ? 1 : 0;
            created += Create(MaterialLibrary.TerrainSurface, MaterialLibrary.TerrainShaders) ? 1 : 0;
            created += Create(MaterialLibrary.Sky, MaterialLibrary.SkyShaders) ? 1 : 0;

            return created;
        }

        static bool Create(string name, string[] shaderNames)
        {
            string path = MaterialPath(name);

            // Never overwrite. The point is a stable asset the build can reference, and a
            // hand-tweaked one should survive a re-run of this.
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return false;

            Shader shader = null;
            foreach (string n in shaderNames)
            {
                shader = Shader.Find(n);
                if (shader != null) break;
            }

            if (shader == null)
            {
                Debug.LogWarning("[BuildPrep] No shader found for " + name +
                                 " (tried: " + string.Join(", ", shaderNames) + ").");
                return false;
            }

            var material = new Material(shader);
            material.name = name;
            AssetDatabase.CreateAsset(material, path);

            Debug.Log("[BuildPrep] created " + path + " using " + shader.name);
            return true;
        }

        // ---- scene --------------------------------------------------------------

        /// <summary>
        /// A player build needs a scene. If one is already enabled it is left alone --
        /// inserting ours at index 0 would silently change which scene the build launches.
        /// </summary>
        static void EnsureSceneInBuild()
        {
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
                if (s.enabled) return;

            const string path = "Assets/Scenes/P0.unity";

            if (!File.Exists(path))
            {
                Directory.CreateDirectory("Assets/Scenes");

                var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                new GameObject("P0 Bootstrap").AddComponent<P0Bootstrap>();
                EditorSceneManager.SaveScene(scene, path);
                AssetDatabase.Refresh();
            }

            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            Debug.Log("[BuildPrep] no scene was enabled in Build Settings; registered " + path);
        }
    }
}
