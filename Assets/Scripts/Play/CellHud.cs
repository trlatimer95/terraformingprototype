using UnityEngine;
using Terraform.Core;
using Terraform.View;

namespace Terraform.Play
{
    /// <summary>
    /// Readout for the per-cell model, including a height label drawn on every nearby
    /// cell.
    ///
    /// Those labels are not decoration. Being able to read the exact height of the ground
    /// you are standing on, before you touch it, is most of what makes this style of
    /// terraforming feel precise instead of fiddly -- it is the difference between
    /// "level this to 39.5" and "level this until it looks right".
    /// </summary>
    public sealed class CellHud : MonoBehaviour
    {
        public CellChunkView Chunk;
        public CellTerraformTool Tool;
        public P0Bootstrap Bootstrap;

        public float LabelRange = 14f;

        static readonly Color Idle = new Color(1f, 1f, 1f, 0.4f);
        static readonly Color Ready = new Color(1f, 0.78f, 0.15f, 0.95f);
        static readonly Color Raising = new Color(0.45f, 0.9f, 0.45f, 0.95f);
        static readonly Color Lowering = new Color(0.95f, 0.45f, 0.35f, 0.95f);
        static readonly Color LabelPlain = new Color(1f, 1f, 0.94f, 0.95f);
        static readonly Color LabelFlat = new Color(1f, 0.86f, 0.32f, 1f);

        GUIStyle _panel;
        GUIStyle _label;
        float _fps;

        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.1f);
        }

        void OnGUI()
        {
            if (Chunk == null || Chunk.Grid == null || Tool == null || Tool.Cam == null) return;

            if (_panel == null)
            {
                _panel = new GUIStyle(GUI.skin.label);
                _panel.fontSize = 13;
                _panel.richText = true;

                _label = HudLabels.MakeStyle();
            }

            DrawCellLabels();
            DrawCrosshair();
            DrawPanel();
        }

        void DrawCellLabels()
        {
            CellGrid g = Chunk.Grid;
            Camera cam = Tool.Cam;
            Vector3 eye = cam.transform.position;

            int range = Mathf.CeilToInt(LabelRange / g.CellSize);

            int px, pz;
            if (!g.WorldToCell(eye, out px, out pz))
            {
                px = Mathf.Clamp(Mathf.FloorToInt((eye.x - g.Origin.x) / g.CellSize), 0, g.CellsX - 1);
                pz = Mathf.Clamp(Mathf.FloorToInt((eye.z - g.Origin.z) / g.CellSize), 0, g.CellsZ - 1);
            }

            Color previous = GUI.color;

            for (int cz = pz - range; cz <= pz + range; cz++)
            {
                for (int cx = px - range; cx <= px + range; cx++)
                {
                    if (!g.InBounds(cx, cz)) continue;

                    Vector3 world = g.CellCentreWorld(cx, cz);
                    if ((world - eye).sqrMagnitude > LabelRange * LabelRange) continue;

                    Vector3 screen = cam.WorldToScreenPoint(world);
                    if (screen.z <= 0.5f) continue;

                    bool target = Tool.HasTarget && cx == Tool.TargetCx && cz == Tool.TargetCz;
                    Color tint = target ? Ready : (g.IsFlat(cx, cz) ? LabelFlat : LabelPlain);

                    HudLabels.Draw(new Rect(screen.x - 26f, Screen.height - screen.y - 9f, 52f, 18f),
                        g.GetMetres(cx, cz).ToString("0.##"), tint, _label);
                }
            }

            GUI.color = previous;
        }

        void DrawCrosshair()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            Color colour = Idle;
            if (Tool.HasTarget)
            {
                if (Tool.ActiveDirection > 0) colour = Raising;
                else if (Tool.ActiveDirection < 0) colour = Lowering;
                else colour = Ready;
            }

            Color previous = GUI.color;
            GUI.color = colour;

            GUI.DrawTexture(new Rect(cx - 8f, cy - 1f, 6f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx + 2f, cy - 1f, 6f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1f, cy - 8f, 2f, 6f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1f, cy + 2f, 2f, 6f), Texture2D.whiteTexture);

            GUI.color = previous;
        }

        static string CellName(int cx, int cz)
        {
            string col = "";
            int n = cx;
            do { col = (char)('A' + n % 26) + col; n = n / 26 - 1; } while (n >= 0);
            return col + (cz + 1);
        }

        void DrawPanel()
        {
            CellGrid g = Chunk.Grid;

            // Only shown when the ground was imported, so the panel grows to fit it.
            string source = Bootstrap != null ? Bootstrap.SourceLine : null;
            float extra = (source != null ? 16f : 0f) + 16f
                        + (Tool.Tool == CellTerraformTool.Mode.Flatten ? 16f : 0f);

            GUI.Box(new Rect(10, 10, 420, 250 + extra), GUIContent.none);
            GUILayout.BeginArea(new Rect(20, 18, 402, 238 + extra));

            GUILayout.Label(string.Format("<b>Cell terrain</b>   {0:0} fps", _fps), _panel);
            GUILayout.Label("Model       <b>cell</b> - one height per cell, flatten pins its corners   [M]", _panel);
            if (source != null) GUILayout.Label(source, _panel);
            if (Bootstrap != null)
                GUILayout.Label(string.Format("Light       <b>{0}</b>   [L]", Bootstrap.SkyName), _panel);
            GUILayout.Label(string.Format("Tool        <b>{0}</b>   [1 sculpt  2 flatten]", Tool.Tool), _panel);
            GUILayout.Space(4);

            if (Tool.HasTarget)
            {
                GUILayout.Label(string.Format("Cell        <b>{0}</b>   ({1}, {2})   {3:0.###} m   {4}",
                    CellName(Tool.TargetCx, Tool.TargetCz), Tool.TargetCx, Tool.TargetCz,
                    g.GetMetres(Tool.TargetCx, Tool.TargetCz),
                    g.IsFlat(Tool.TargetCx, Tool.TargetCz) ? "<b>flat</b>" : "<i>natural</i>"), _panel);
            }
            else
            {
                GUILayout.Label(string.Format("Cell        <i>nothing within {0:0.#} m reach</i>", Tool.MaxReach), _panel);
            }

            GUILayout.Label(string.Format("Step        {0:0.####} m per click   [scroll]", Tool.StepMetres), _panel);

            GUILayout.Label(Tool.MaxStepUnits > 0
                ? string.Format("Max step    {0:0.##} m against a neighbour   [- / =]",
                    CellGrid.ToMetres(Tool.MaxStepUnits))
                : "Max step    <i>unlimited</i>   [- / =]", _panel);

            if (Tool.Tool == CellTerraformTool.Mode.Flatten)
            {
                GUILayout.Label(Tool.HasTarget
                    ? string.Format("Levels to   <b>{0:0.###} m</b>   <i>this cell's own height</i>",
                        Tool.FlattenTargetMetres)
                    : "Levels to   <i>nothing targeted</i>", _panel);

                GUILayout.Label(Tool.LastEditRejected
                    ? "            <b>already level, and nothing beside it to soften</b>"
                    : "            <b>LMB</b> level this cell", _panel);
            }
            else
            {
                GUILayout.Label(Tool.LastEditRejected
                    ? "            <b>blocked - would exceed max step</b>"
                    : "            <b>LMB</b> raise   <b>RMB</b> lower", _panel);
            }

            GUILayout.Label(string.Format("Cut / fill  {0:+0.###;-0.###;0} m^3 net   ({1} ops, {2} redo)",
                Chunk.Log.NetVolume, Chunk.Log.DoneCount, Chunk.Log.UndoneCount), _panel);

            GUILayout.Label(string.Format("Mesh cost   build {0:0.00} ms + collider {1:0.00} ms   ({2} tris)",
                Chunk.LastMeshMs, Chunk.LastColliderMs, Chunk.TriangleCount), _panel);

            GUILayout.Label(string.Format("Last        {0}", Chunk.Log.LastDescription ?? "-"), _panel);

            GUILayout.Space(4);
            GUILayout.Label("<b>WASD</b> walk   <b>Z/Y</b> undo   <b>Tab</b> grid   <b>R</b> reset   <b>Esc</b> cursor", _panel);

            GUILayout.EndArea();
        }
    }
}
