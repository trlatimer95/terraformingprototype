using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Terraform.Play;

namespace Terraform.EditorTools
{
    /// <summary>
    /// Creates and opens the appearance-gate scene. Kept out of the playtest build settings
    /// on purpose -- the tester build should stay exactly what it is while this branch runs.
    /// </summary>
    public static class SpanGateSetup
    {
        const string ScenePath = "Assets/Scenes/SpanGate.unity";

        [MenuItem("Tools/Terraform/Open Span Gate Scene")]
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
            new GameObject("Span Gate").AddComponent<SpanGateBootstrap>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[SpanGate] created " + ScenePath +
                      ". Press Play, walk the pad step and the tunnel, and use C to swap resolution.");
        }
    }
}
