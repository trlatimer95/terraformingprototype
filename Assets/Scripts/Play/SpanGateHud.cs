using UnityEngine;
using Terraform.Span;

namespace Terraform.Play
{
    /// <summary>
    /// Readout for the appearance gate.
    ///
    /// The number that matters most is the per-edit one. A whole-world bake says nothing
    /// about what a player swinging a pick costs; rebuilding only the bricks that changed
    /// does, and that is the figure the 64-player projection was guessing at.
    /// </summary>
    public sealed class SpanGateHud : MonoBehaviour
    {
        public SpanGateBootstrap Gate;

        GUIStyle _panel;
        float _fps;

        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.1f);
        }

        void OnGUI()
        {
            if (Gate == null) return;

            if (_panel == null)
            {
                _panel = new GUIStyle(GUI.skin.label);
                _panel.fontSize = 13;
                _panel.richText = true;
            }

            GUI.Box(new Rect(10, 10, 452, 286), GUIContent.none);
            GUILayout.BeginArea(new Rect(20, 18, 434, 274));

            GUILayout.Label(string.Format("<b>Span gate</b>   {0:0} fps", _fps), _panel);

            GUILayout.Label(string.Format("Column      <b>{0:0.###} m</b>   [C]", Gate.ColumnSize), _panel);

            GUILayout.Label(SpanMeshBuilder.Smooth
                ? "Caps        <b>smoothed</b> - floors and ceilings ramp, walls square   [V]"
                : "Caps        <b>exact</b> - flat per column, matches span volume   [V]", _panel);

            GUILayout.Label(Gate.QuantiseFloor
                ? string.Format("Tunnel floor <b>stepped</b> at {0:0.##} m   [Tab]", SpanContent.FloorStep)
                : "Tunnel floor <b>ramped</b> - continuous   [Tab]", _panel);

            GUILayout.Space(4);

            GUILayout.Label(string.Format("Bite        <b>{0:0.###} m</b> cube   [- / =]", Gate.BiteSize), _panel);

            GUILayout.Label(Gate.Selective
                ? "Mining      <b>selective</b> - takes only the material aimed at   [T]"
                : "Mining      <b>everything</b> in the bite   [T]", _panel);

            GUILayout.Label("            <b>LMB</b> to mine", _panel);

            GUILayout.Space(4);

            GUILayout.Label(string.Format("World       {0:0} m   {1:n0} columns   {2:n0} spans   {3:n0} tris",
                Gate.AreaMetres, Gate.ColumnCount, Gate.SpanCount, Gate.Triangles), _panel);

            GUILayout.Label(string.Format("Whole world {0} bricks at {1:0.#} m   build {2:0.0} + cook {3:0.0} ms   (one-off)",
                Gate.BrickCount, Gate.BrickMetres, Gate.BuildMs, Gate.CookMs), _panel);

            GUILayout.Space(4);

            if (Gate.EditBricks > 0)
            {
                GUILayout.Label(string.Format("<b>Last edit</b>  {0} brick{1}   build <b>{2:0.00} ms</b> + cook <b>{3:0.00} ms</b>",
                    Gate.EditBricks, Gate.EditBricks == 1 ? "" : "s", Gate.EditBuildMs, Gate.EditCookMs), _panel);

                GUILayout.Label(string.Format("            total <b>{0:0.00} ms</b>   took {1}",
                    Gate.EditBuildMs + Gate.EditCookMs, Gate.EditNote ?? "-"), _panel);
            }
            else
            {
                GUILayout.Label(string.Format("<b>Last edit</b>  <i>{0}</i>", Gate.EditNote ?? "none yet"), _panel);
                GUILayout.Label(" ", _panel);
            }

            GUILayout.Space(4);
            GUILayout.Label(string.Format("View        <b>{0}</b>   [1 pads  2 mouth  3 adit  4 corner  H summit]",
                Gate.ViewName), _panel);

            GUILayout.EndArea();

            DrawLegend();
            DrawCrosshair();
        }

        void DrawCrosshair()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            Color previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.7f);

            GUI.DrawTexture(new Rect(cx - 7f, cy - 1f, 5f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx + 2f, cy - 1f, 5f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1f, cy - 7f, 2f, 5f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1f, cy + 2f, 2f, 5f), Texture2D.whiteTexture);

            GUI.color = previous;
        }

        void DrawLegend()
        {
            const float w = 132f;
            float h = 22f + SpanMaterials.Count * 18f;

            var box = new Rect(Screen.width - w - 10f, 10f, w, h);
            GUI.Box(box, GUIContent.none);

            Color previous = GUI.color;

            for (int i = 0; i < SpanMaterials.Count; i++)
            {
                float y = box.y + 12f + i * 18f;

                GUI.color = SpanMaterials.Colours[i];
                GUI.DrawTexture(new Rect(box.x + 12f, y + 3f, 11f, 11f), Texture2D.whiteTexture);

                GUI.color = previous;
                GUI.Label(new Rect(box.x + 30f, y, w - 40f, 18f), SpanMaterials.Names[i], _panel);
            }

            GUI.color = previous;
        }
    }
}
