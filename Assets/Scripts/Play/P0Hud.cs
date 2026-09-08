using UnityEngine;
using Terraform.Core;
using Terraform.View;

namespace Terraform.Play
{
    /// <summary>
    /// IMGUI readout. Ugly on purpose: a spike needs numbers on screen in one file,
    /// not a UI stack. The volume figure is the one that matters -- it is the check
    /// that the fixed-point maths and the cut/fill accounting actually agree.
    /// </summary>
    public sealed class P0Hud : MonoBehaviour
    {
        public ChunkView Chunk;
        public TerraformTool Tool;
        public P0Bootstrap Bootstrap;

        static readonly Color Idle = new Color(1f, 1f, 1f, 0.4f);
        static readonly Color Ready = new Color(1f, 0.78f, 0.15f, 0.95f);
        static readonly Color Raising = new Color(0.45f, 0.9f, 0.45f, 0.95f);
        static readonly Color Lowering = new Color(0.95f, 0.45f, 0.35f, 0.95f);
        static readonly Color LabelPlain = new Color(1f, 1f, 0.94f, 0.95f);

        /// <summary>Corners the current tool would move. Matches the cell outline's colour.</summary>
        static readonly Color LabelFootprint = new Color(0.35f, 0.88f, 1f, 1f);

        /// <summary>Matched to the cell model so neither reads as the clearer of the two.</summary>
        public float LabelRange = 14f;

        GUIStyle _style;
        GUIStyle _label;
        float _fps;

        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.1f);
        }

        void OnGUI()
        {
            if (Chunk == null || Chunk.Grid == null || Tool == null) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.fontSize = 13;
                _style.richText = true;

                _label = HudLabels.MakeStyle();
            }

            DrawVertexLabels();
            DrawCrosshair();
            DrawPanel();
        }

        /// <summary>
        /// A height on every nearby vertex.
        ///
        /// On the VERTEX grid, not cell centres: this model stores its heights at the
        /// corners, and putting the numbers anywhere else would teach the wrong thing
        /// about what a click moves. It also makes flatten legible -- the four corner
        /// numbers are the ones being averaged, so the result is readable before the
        /// click rather than only afterwards.
        /// </summary>
        void DrawVertexLabels()
        {
            HeightGrid g = Chunk.Grid;
            Camera cam = Tool.Cam;
            if (cam == null) return;

            Vector3 eye = cam.transform.position;
            int range = Mathf.CeilToInt(LabelRange / g.CellSize);

            Vector2 here = g.WorldToGridPoint(eye);
            int px = Mathf.Clamp(Mathf.RoundToInt(here.x), 0, g.CellsX);
            int pz = Mathf.Clamp(Mathf.RoundToInt(here.y), 0, g.CellsZ);

            int fx0 = 0, fz0 = 0, fw = 0, fh = 0;
            bool footprint = Tool.Tool != TerraformTool.Mode.Sculpt
                             && Tool.TryFootprint(out fx0, out fz0, out fw, out fh);

            Color previous = GUI.color;

            for (int vz = pz - range; vz <= pz + range; vz++)
            {
                for (int vx = px - range; vx <= px + range; vx++)
                {
                    if (!g.InBounds(vx, vz)) continue;

                    Vector3 world = g.VertexWorld(vx, vz);
                    if ((world - eye).sqrMagnitude > LabelRange * LabelRange) continue;

                    Vector3 screen = cam.WorldToScreenPoint(world);
                    if (screen.z <= 0.5f) continue;

                    bool marked = Tool.HasTarget && vx == Tool.TargetVx && vz == Tool.TargetVz;
                    bool inside = footprint
                                  && vx >= fx0 && vx < fx0 + fw
                                  && vz >= fz0 && vz < fz0 + fh;

                    Color tint = marked ? Ready : (inside ? LabelFootprint : LabelPlain);

                    HudLabels.Draw(new Rect(screen.x - 26f, Screen.height - screen.y - 9f, 52f, 18f),
                        g.GetMetres(vx, vz).ToString("0.##"), tint, _label);
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

        /// <summary>Spreadsheet-style cell name, so the HUD reads the way you talk about it.</summary>
        static string CellName(int cx, int cz)
        {
            string col = "";
            int n = cx;
            do { col = (char)('A' + n % 26) + col; n = n / 26 - 1; } while (n >= 0);
            return col + (cz + 1);
        }

        void DrawPanel()
        {
            HeightGrid g = Chunk.Grid;
            bool terrain = Bootstrap != null && Bootstrap.UsingUnityTerrain;
            UnityTerrainView tv = Bootstrap != null ? Bootstrap.TerrainView : null;

            // Only shown when the ground was imported, so the panel grows to fit it.
            string source = Bootstrap != null ? Bootstrap.SourceLine : null;
            float extra = (source != null ? 16f : 0f) + 16f;

            GUI.Box(new Rect(10, 10, 420, 344 + extra), GUIContent.none);
            GUILayout.BeginArea(new Rect(20, 18, 402, 332 + extra));

            GUILayout.Label(string.Format("<b>Vertex terrain</b>   {0:0} fps", _fps), _style);
            GUILayout.Label("Model       <b>vertex</b> - raise a corner, flatten the cells between   [M]", _style);
            if (source != null) GUILayout.Label(source, _style);
            if (Bootstrap != null)
                GUILayout.Label(string.Format("Light       <b>{0}</b>   [L]", Bootstrap.SkyName), _style);
            GUILayout.Label(string.Format("Surface     <b>{0}</b>   [T to swap]",
                terrain ? "Unity Terrain" : "generated mesh"), _style);
            GUILayout.Space(4);

            GUILayout.Label(string.Format("Tool        <b>{0}</b>   [1 sculpt  2 flatten  3 ramp  4 cell]", Tool.Tool), _style);

            if (Tool.HasTarget)
            {
                GUILayout.Label(string.Format("Cell        <b>{0}</b>   ({1}, {2})   height {3:0.###} m",
                    CellName(Tool.TargetVx, Tool.TargetVz), Tool.TargetVx, Tool.TargetVz,
                    g.GetMetres(Tool.TargetVx, Tool.TargetVz)), _style);
            }
            else
            {
                GUILayout.Label(string.Format("Cell        <i>nothing within {0:0.#} m reach</i>", Tool.MaxReach), _style);
            }

            switch (Tool.Tool)
            {
                case TerraformTool.Mode.Sculpt:
                    GUILayout.Label(string.Format("Step        {0:0.####} m per click   [scroll]", Tool.StepMetres), _style);
                    GUILayout.Label(Tool.MaxStepUnits > 0
                        ? string.Format("Max step    {0:0.##} m above a neighbour   [- / =]",
                            HeightGrid.ToMetres(Tool.MaxStepUnits))
                        : "Max step    <i>unlimited</i>   [- / =]", _style);
                    GUILayout.Label(Tool.Rounding > 0f
                        ? string.Format("Rounding    {0:0.##} - <b>also moves neighbouring corners</b>   [, / .]", Tool.Rounding)
                        : "Rounding    <i>off - corners move only when you click them</i>   [, / .]", _style);
                    GUILayout.Label(Tool.LastEditRejected
                        ? "            <b>blocked - would exceed max step</b>"
                        : "            <b>LMB</b> raise   <b>RMB</b> lower", _style);
                    break;

                case TerraformTool.Mode.Flatten:
                    GUILayout.Label("Footprint   <b>the single cell</b> under the crosshair", _style);
                    GUILayout.Label(string.Format("Levels to   <b>{0:0.###} m</b>   <i>average of this cell's corners</i>",
                        Tool.FlattenTargetMetres), _style);
                    GUILayout.Label(Tool.LastEditRejected
                        ? "            <b>blocked - would exceed max step</b>"
                        : "            <b>LMB</b> level this cell", _style);
                    break;

                case TerraformTool.Mode.Cell:
                    GUILayout.Label(string.Format("Step        {0:0.####} m per click   [scroll]", Tool.StepMetres), _style);
                    GUILayout.Label(Tool.MaxStepUnits > 0
                        ? string.Format("Max step    {0:0.##} m above a neighbour   [- / =]",
                            HeightGrid.ToMetres(Tool.MaxStepUnits))
                        : "Max step    <i>unlimited</i>   [- / =]", _style);
                    GUILayout.Label(Tool.LastEditRejected
                        ? "            <b>blocked - would exceed max step</b>"
                        : "            <b>LMB</b> raise all four corners   <b>RMB</b> lower", _style);
                    break;

                case TerraformTool.Mode.Ramp:
                    GUILayout.Label(string.Format("Width       {0} cells   [- / =]", Tool.RampWidth), _style);
                    GUILayout.Label(Tool.HasAnchor
                        ? "Ramp        <b>anchor set</b> - LMB at the far end   [RMB cancel]"
                        : "Ramp        <i>LMB to place the start anchor</i>", _style);
                    break;
            }

            GUILayout.Label(string.Format("Cut / fill  {0:+0.###;-0.###;0} m^3 net   ({1} ops, {2} redo)",
                Chunk.Log.NetVolume, Chunk.Log.DoneCount, Chunk.Log.UndoneCount), _style);

            GUILayout.Label(string.Format("Mesh cost   build {0:0.00} ms + collider {1:0.00} ms   ({2} tris)",
                Chunk.LastMeshMs, Chunk.LastColliderMs, Chunk.TriangleCount), _style);

            if (tv != null && tv.Valid)
            {
                GUILayout.Label(string.Format("Terrain     SetHeights {0:0.00} ms   pixel error {1:0.#}  [ / ]   holes {2}",
                    tv.LastSyncMs, tv.PixelError, tv.HoleCount), _style);
            }

            GUILayout.Label(string.Format("Last        {0}", Chunk.Log.LastDescription ?? "-"), _style);

            GUILayout.Space(6);
            GUILayout.Label("<b>WASD</b> walk   <b>Space</b> jump   <b>H</b> hole   <b>Z/Y</b> undo", _style);
            GUILayout.Label("<b>Tab</b> grid   <b>R</b> reset   <b>Esc</b> cursor", _style);

            GUILayout.EndArea();
        }
    }
}
