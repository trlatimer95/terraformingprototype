using UnityEngine;

namespace Terraform.Play
{
    /// <summary>
    /// Escape menu. Small on purpose: this is a playtest build, and the only thing a
    /// tester actually needs from it is a way out that is not Alt+F4.
    ///
    /// Opening it pauses the tools through the bootstrap rather than by zeroing
    /// Time.timeScale, so a click on Resume cannot also dig a hole in the ground behind
    /// the panel.
    /// </summary>
    public sealed class PauseMenu : MonoBehaviour
    {
        public P0Bootstrap Bootstrap;

        public bool IsOpen { get; private set; }

        GUIStyle _title;
        GUIStyle _hint;
        GUIStyle _button;

        const float PanelWidth = 300f;
        const float PanelHeight = 208f;

        public void Toggle() { SetOpen(!IsOpen); }

        public void SetOpen(bool open)
        {
            if (IsOpen == open) return;

            IsOpen = open;

            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;

            if (Bootstrap != null) Bootstrap.SetPaused(open);
        }

        void OnGUI()
        {
            if (!IsOpen) return;

            // IMGUI draws lower depths last, so this lands over both HUDs.
            GUI.depth = -100;

            EnsureStyles();
            DimBackground();

            var panel = new Rect((Screen.width - PanelWidth) * 0.5f,
                                 (Screen.height - PanelHeight) * 0.5f,
                                 PanelWidth, PanelHeight);

            GUI.Box(panel, GUIContent.none);
            GUILayout.BeginArea(new Rect(panel.x + 24f, panel.y + 22f, PanelWidth - 48f, PanelHeight - 44f));

            GUILayout.Label("Paused", _title);
            GUILayout.Space(14f);

            if (GUILayout.Button("Resume", _button, GUILayout.Height(36f))) SetOpen(false);

            GUILayout.Space(8f);

            if (GUILayout.Button("Quit", _button, GUILayout.Height(36f))) Quit();

            GUILayout.Space(12f);
            GUILayout.Label("Esc closes this too", _hint);

            GUILayout.EndArea();
        }

        static void DimBackground()
        {
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        void EnsureStyles()
        {
            if (_title != null) return;

            _title = new GUIStyle(GUI.skin.label);
            _title.fontSize = 20;
            _title.fontStyle = FontStyle.Bold;
            _title.alignment = TextAnchor.MiddleCenter;

            _hint = new GUIStyle(GUI.skin.label);
            _hint.fontSize = 11;
            _hint.alignment = TextAnchor.MiddleCenter;
            _hint.normal.textColor = new Color(0.75f, 0.75f, 0.7f, 0.75f);

            _button = new GUIStyle(GUI.skin.button);
            _button.fontSize = 14;
        }

        static void Quit()
        {
#if UNITY_EDITOR
            // Application.Quit is a no-op in play mode, and stopping is what "quit" means
            // while testing in the editor.
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
