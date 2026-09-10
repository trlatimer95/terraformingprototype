using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Terraform.Play;

namespace Terraform.EditorTools
{
    /// <summary>
    /// Creates and opens the hybrid scene: the original terraforming on the surface, spans
    /// only where something has been dug. Kept out of build settings like the gate scene.
    /// </summary>
    public static class HybridSetup
    {
        const string ScenePath = "Assets/Scenes/Hybrid.unity";

        [MenuItem("Tools/Terraform/Open Hybrid Scene")]
        public static void Open()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (File.Exists(ScenePath))
            {
                EditorSceneManager.OpenScene(ScenePath);
                return;
            }

            Directory.CreateDirectory("Assets/Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            new GameObject("Hybrid").AddComponent<HybridBootstrap>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[Hybrid] created " + ScenePath +
                      ". Press Play. You start at the adit mouth; H cycles viewpoints, " +
                      "M swaps between terraforming and mining.");
        }

        /// <summary>
        /// Throw the scene away and build it again from nothing.
        ///
        /// For when the scene has picked up another bootstrap. Two of them build two worlds
        /// on the same ground, and the one that is not being looked at still draws an intact
        /// surface over the other's tunnels -- so holes stop appearing and the two HUDs
        /// print over each other. Neither is a fault in the geometry, which is exactly why
        /// it is worth being able to rule out in one click.
        /// </summary>
        [MenuItem("Tools/Terraform/Rebuild Hybrid Scene")]
        public static void Rebuild()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (File.Exists(ScenePath))
            {
                AssetDatabase.DeleteAsset(ScenePath);
                AssetDatabase.Refresh();
            }

            Open();
        }

        const string NativePath = "Assets/Scenes/NativeHybrid.unity";

        /// <summary>
        /// The same hybrid with Unity Terrain drawing the surface. A separate scene rather
        /// than a mode switch: everything below the surface is shared code, so the comparison
        /// stays honest, and the working scene stays untouched while this one is judged.
        /// </summary>
        [MenuItem("Tools/Terraform/Open Native Hybrid Scene")]
        public static void OpenNative()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (File.Exists(NativePath))
            {
                EditorSceneManager.OpenScene(NativePath);
                return;
            }

            Directory.CreateDirectory("Assets/Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            new GameObject("Native Hybrid").AddComponent<NativeHybridBootstrap>();

            EditorSceneManager.SaveScene(scene, NativePath);
            AssetDatabase.Refresh();

            Debug.Log("[NativeHybrid] created " + NativePath +
                      ". Press Play. H cycles viewpoints -- the last one is the distance view " +
                      "for the LOD test; [ and ] change terrain pixel error there.");
        }

        /// <summary>
        /// Rebuild the native scene from nothing.
        ///
        /// Unity writes a component's public field values into the scene the moment it is
        /// added, so changing a default in code leaves an existing scene running the old
        /// value -- and the symptom is not an error, it is the tool quietly behaving as it
        /// did before. Renaming a field sheds the stale value; this sheds all of them.
        /// </summary>
        [MenuItem("Tools/Terraform/Rebuild Native Hybrid Scene")]
        public static void RebuildNative()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (File.Exists(NativePath))
            {
                AssetDatabase.DeleteAsset(NativePath);
                AssetDatabase.Refresh();
            }

            OpenNative();
        }
    }
}
