using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Terraform.Play;

namespace Terraform.EditorTools
{
    /// <summary>
    /// Optional convenience: creates and saves a P0 scene. Not required -- P0Bootstrap
    /// auto-spawns in any scene on play -- but useful once you want a scene to keep.
    /// </summary>
    public static class P0Setup
    {
        [MenuItem("Tools/Terraform/Create P0 Scene")]
        public static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var go = new GameObject("P0 Bootstrap");
            go.AddComponent<P0Bootstrap>();

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/P0.unity");
            AssetDatabase.Refresh();

            Debug.Log("P0 scene created at Assets/Scenes/P0.unity. Press Play.");
        }
    }
}
