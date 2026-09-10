using UnityEngine;
using Terraform.Core;
using Terraform.Span;

namespace Terraform.Play
{
    /// <summary>
    /// Readout for the native-surface hybrid.
    ///
    /// Two figures matter here that did not in the other scene. The seam number is measured
    /// against the heightfield rather than against a mesh of our own, and pixel error is on
    /// the panel because it is the one setting that can break the seam without any edit at
    /// all -- raising it simplifies the terrain and leaves the ceded patches alone.
    /// </summary>
    public sealed class NativeHybridHud : MonoBehaviour
    {
        public NativeHybridBootstrap Scene;

        GUIStyle _panel;
        GUIStyle _label;
        Texture2D _backing;
        float _fps;

        const float PanelWidth = 540f;
        const float PanelHeight = 488f;
        const float Pad = 12f;

        static readonly Color LabelPlain = new Color(1f, 1f, 0.94f, 0.95f);
        static readonly Color LabelCeded = new Color(0.62f, 0.84f, 1f, 0.95f);
        static readonly Color LabelTarget = new Color(0.55f, 1f, 0.6f, 1f);

        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.1f);
        }

        void OnGUI()
        {
            if (Scene == null || Scene.World == null || Scene.TerrainView == null) return;

            if (_panel == null)
            {
                _panel = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
                _panel.normal.textColor = new Color(0.94f, 0.95f, 0.97f);
            }

            if (_backing == null)
            {
                _backing = new Texture2D(1, 1);
                _backing.SetPixel(0, 0, new Color(0.07f, 0.09f, 0.12f, 0.96f));
                _backing.Apply();
                _backing.hideFlags = HideFlags.HideAndDontSave;
            }

            if (!Scene.ShowPanel)
            {
                // The overlays are separate toggles: hiding the readout should not also hide
                // the grid somebody is using to judge whether ground is level.
                DrawControls();
                DrawHeights();
                DrawCrosshair();
                return;
            }

            GUI.DrawTexture(new Rect(10, 10, PanelWidth, PanelHeight), _backing);
            GUILayout.BeginArea(new Rect(10 + Pad, 10 + Pad,
                                         PanelWidth - Pad * 2f, PanelHeight - Pad * 2f));

            GUILayout.Label(string.Format("<b>Native hybrid</b>   Unity Terrain + tunnels   {0:0} fps", _fps), _panel);

            GUILayout.Label(Scene.Doing == NativeHybridBootstrap.Job.Terraform
                ? "Doing       <b>terraforming</b> - vertex heightfield, no vertical faces   [M]"
                : "Doing       <b>mining</b> - spans underneath   [M]", _panel);

            GUILayout.Space(4);

            if (Scene.Doing == NativeHybridBootstrap.Job.Terraform)
            {
                GUILayout.Label(Scene.Tool == NativeHybridBootstrap.SurfaceTool.Sculpt
                    ? "Tool        <b>sculpt</b> - moves the one vertex you aim at   [1 sculpt  2 flatten]"
                    : "Tool        <b>flatten</b> - levels the cell you aim at   [1 sculpt  2 flatten]", _panel);

                GUILayout.Label(string.Format("Step        <b>{0:0.##} m</b> per click       <b>LMB</b> raise  <b>RMB</b> lower",
                    Scene.StepUnits * HeightGrid.MetresPerUnit), _panel);

                GUILayout.Label(string.Format(
                    "Moved       <b>{0:0.000} m^3</b> last click   <b>{1:0.000} m^3</b> net   max step {2:0.#} m",
                    Scene.LastVolume, Scene.CutFill,
                    Scene.MaxStepUnits * HeightGrid.MetresPerUnit), _panel);

                GUILayout.Label(string.Format(
                    "Aiming at   vertex <b>{0}, {1}</b> at <b>{2:0.###} m</b>   max slope {3:0.#} : 1",
                    Scene.TargetVx, Scene.TargetVz, Scene.TargetVertexHeight, Scene.MaxSlope), _panel);
            }
            else
            {
                GUILayout.Label(string.Format("Bite        <b>{0:0.###} m</b> cube   [- / =]        <b>LMB</b> to mine",
                    Scene.BiteSize), _panel);

                GUILayout.Label(Scene.Selective
                    ? "Mining      <b>selective</b> - only the material aimed at   [T]"
                    : "Mining      <b>everything</b> in the bite   [T]", _panel);

                GUILayout.Label(string.Format(
                    "Column      <b>{0:0.###} m</b>   [C]        rock {1}   [V]",
                    Scene.ColumnSize,
                    SpanMeshBuilder.Smooth ? "<b>smoothed</b>" : "<b>exact</b> - true cubes"), _panel);

                GUILayout.Label(string.Format(
                    "Ore         <b>{0}</b>   deposits every ~{1:0} m, coal shallow to silver deep",
                    Scene.World.UseOreField ? "scattered" : "single body",
                    Terraform.Core.OreField.CellMetres), _panel);

                string nearest = Scene.NearestOreReport();
                if (!string.IsNullOrEmpty(nearest))
                    GUILayout.Label("Nearest     <b>" + nearest + "</b>", _panel);

                string ground = Scene.GroundReport();
                if (!string.IsNullOrEmpty(ground))
                    GUILayout.Label("Ground      <b>" + ground + "</b>", _panel);

                GUILayout.Label(Scene.SquareToCells
                    ? string.Format("Footprint   <b>{0:0.#} m cell</b> across, {1:0.###} m deep   [K]",
                                    Scene.CellSize, Scene.BiteSize)
                    : string.Format("Footprint   <b>{0:0.###} m</b> cube - can leave part of a cell standing   [K]",
                                    Scene.BiteSize), _panel);
            }

            GUILayout.Label(Scene.HasTarget
                ? string.Format("Cell        <b>{0}, {1}</b> at {2:0.0} m{3}",
                    Scene.TargetCx, Scene.TargetCz,
                    Scene.InteractionHeight(Scene.TargetCx, Scene.TargetCz),
                    Scene.InteractionCeded(Scene.TargetCx, Scene.TargetCz) ? "   <b>ceded to spans</b>" : "")
                : "Cell        <i>nothing within reach</i>", _panel);

            GUILayout.Space(6);

            GUILayout.Label(string.Format(
                "Terrain     holes <b>{0}</b>   pixel error <b>{1:0}</b>   [ / ]   holes render {2}",
                Scene.TerrainView.HoleCount, Scene.TerrainView.PixelError,
                Scene.TerrainView.HolesRenderable ? "yes" : "<b>NO - collision only</b>"), _panel);

            GUILayout.Label(string.Format(
                "Ceded       <b>{0}</b> bricks    columns stored <b>{1:n0}</b> of {2:n0}",
                Scene.World.CededBricks, Scene.World.MaterialisedColumns,
                Scene.World.Spans.ColumnsX * Scene.World.Spans.ColumnsZ), _panel);

            GUILayout.Label(string.Format(
                "Seam        worst departure <b>{0:0.000000} m</b> from the heightfield",
                Scene.SeamError), _panel);

            GUILayout.Space(6);

            GUILayout.Label(string.Format(
                "Last edit   spans <b>{0:0.00} ms</b> + cook <b>{1:0.00} ms</b> over {2} brick{3}",
                Scene.BuildMs, Scene.CookMs, Scene.EditBricks, Scene.EditBricks == 1 ? "" : "s"), _panel);

            GUILayout.Label(string.Format(
                "            terrain sync <b>{0:0.00} ms</b>   {1:n0} tris underground",
                Scene.SurfaceMs, Scene.Triangles), _panel);

            if (!string.IsNullOrEmpty(Scene.Note))
                GUILayout.Label("            <b>" + Scene.Note + "</b>", _panel);

            GUILayout.Space(4);

            GUILayout.Label(string.Format("Heights     <b>{0}</b>   [Tab]        markers <b>{1}</b>   [G]",
                Scene.ShowHeights ? "shown" : "hidden",
                Scene.ShowMarkers ? "shown" : "hidden"), _panel);

            GUILayout.Label(string.Format("View        <b>{0}</b>   [H] next   [L] sky   [Z] undo",
                Scene.ViewName), _panel);

            GUILayout.EndArea();

            DrawControls();
            DrawHeights();
            DrawCrosshair();
        }

        GUIStyle _bar;

        /// <summary>
        /// One line of keys along the bottom.
        ///
        /// Shown in both states, and deliberately not a copy of the panel: it lists what can
        /// be PRESSED, where the panel reports what is true. Mixing the two is how a readout
        /// grows until nobody reads it.
        ///
        /// Context-sensitive, because half these keys do nothing in the other mode and a list
        /// of inapplicable controls is worse than a shorter one.
        /// </summary>
        void DrawControls()
        {
            if (_bar == null)
            {
                _bar = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    richText = true,
                    alignment = TextAnchor.MiddleCenter
                };

                _bar.normal.textColor = new Color(0.86f, 0.89f, 0.93f);
            }

            string keys = Scene.Doing == NativeHybridBootstrap.Job.Terraform
                ? "<b>LMB</b> raise   <b>RMB</b> lower   <b>1</b> sculpt   <b>2</b> flatten   " +
                  "<b>Z</b> undo   <b>Tab</b> heights   <b>G</b> grid   <b>M</b> mine"
                : "<b>LMB</b> dig   <b>T</b> selective   <b>-/=</b> bite   <b>C</b> column   " +
                  "<b>V</b> rock   <b>K</b> footprint   <b>O</b> ore   <b>M</b> terraform";

            keys += "   <b>H</b> view   <b>L</b> sky   <b>[ ]</b> LOD   <b>F1</b> panel";

            const float height = 26f;
            var strip = new Rect(0f, Screen.height - height, Screen.width, height);

            GUI.DrawTexture(strip, _backing);
            GUI.Label(strip, keys, _bar);
        }

        /// <summary>
        /// Heights on the vertices, which is where the work happens.
        ///
        /// No yellow: a flattened cell is not a distinct state in a heightfield -- shared
        /// vertices make level ground just ground that happens to be level -- so there is
        /// nothing to colour differently.
        /// </summary>
        void DrawHeights()
        {
            if (!Scene.ShowHeights) return;
            if (Scene.Doing != NativeHybridBootstrap.Job.Terraform) return;

            Camera cam = Scene.Cam;
            HeightGrid g = Scene.Grid;
            if (cam == null || g == null) return;

            if (_label == null) _label = HudLabels.MakeStyle();

            Vector3 eye = cam.transform.position;

            // On the vertices, because the vertices are what move. Labelling cell centres
            // would be reporting a number nothing can be aimed at.
            int px, pz;
            g.WorldToNearestVertex(eye, out px, out pz);

            int range = Mathf.CeilToInt(Scene.LabelRange / g.CellSize);
            float rangeSq = Scene.LabelRange * Scene.LabelRange;

            Color previous = GUI.color;

            for (int vz = pz - range; vz <= pz + range; vz++)
            {
                for (int vx = px - range; vx <= px + range; vx++)
                {
                    if (!g.InBounds(vx, vz)) continue;

                    Vector3 world = g.VertexWorld(vx, vz);
                    if ((world - eye).sqrMagnitude > rangeSq) continue;

                    Vector3 screen = cam.WorldToScreenPoint(world);
                    if (screen.z <= 0.5f) continue;

                    bool target = Scene.HasTarget && vx == Scene.TargetVx && vz == Scene.TargetVz;

                    Color tint = target ? LabelTarget
                               : Scene.InteractionCeded(Mathf.Min(vx, g.CellsX - 1),
                                                        Mathf.Min(vz, g.CellsZ - 1)) ? LabelCeded
                               : LabelPlain;

                    HudLabels.Draw(new Rect(screen.x - 26f, Screen.height - screen.y - 9f, 52f, 18f),
                                   g.GetMetres(vx, vz).ToString("0.#"), tint, _label);
                }
            }

            GUI.color = previous;
        }

        void OnDestroy()
        {
            if (_backing != null) DestroyImmediate(_backing);
        }

        static void DrawCrosshair()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            GUI.color = new Color(1f, 1f, 1f, 0.75f);
            GUI.DrawTexture(new Rect(cx - 6f, cy - 1f, 12f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1f, cy - 6f, 2f, 12f), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
