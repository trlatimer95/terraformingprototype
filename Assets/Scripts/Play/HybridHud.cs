using UnityEngine;
using Terraform.Core;
using Terraform.Span;

namespace Terraform.Play
{
    /// <summary>
    /// Readout for the hybrid.
    ///
    /// The seam figure is the one to watch. It is the worst disagreement, in metres,
    /// between where the span mesher puts ground and where the surface mesher puts the same
    /// ground -- measured along every boundary of every ceded brick. The claim being tested
    /// is that it is zero, not that it is small.
    /// </summary>
    public sealed class HybridHud : MonoBehaviour
    {
        public HybridBootstrap Scene;

        GUIStyle _panel;
        GUIStyle _label;
        Texture2D _backing;

        // Plain ground, a levelled pad, ground the spans own, and the cell under the
        // crosshair. The ceded colour earns its place here: it is the only way to see where
        // the ownership boundary actually runs, and the whole point is that the geometry
        // gives nothing away.
        static readonly Color LabelPlain = new Color(1f, 1f, 0.94f, 0.95f);
        static readonly Color LabelFlat = new Color(1f, 0.86f, 0.32f, 1f);
        static readonly Color LabelCeded = new Color(0.62f, 0.84f, 1f, 0.95f);
        static readonly Color LabelTarget = new Color(0.55f, 1f, 0.6f, 1f);
        float _fps;

        const float PanelWidth = 520f;
        const float PanelHeight = 368f;
        const float Pad = 12f;

        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.1f);
        }

        void OnGUI()
        {
            if (Scene == null || Scene.World == null) return;

            if (_panel == null)
            {
                _panel = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
                _panel.normal.textColor = new Color(0.94f, 0.95f, 0.97f);
            }

            if (_backing == null)
            {
                // GUI.Box is translucent, and over a bright hillside the text stops being
                // readable. A flat opaque fill costs one texel and is legible everywhere.
                _backing = new Texture2D(1, 1);
                _backing.SetPixel(0, 0, new Color(0.07f, 0.09f, 0.12f, 0.96f));
                _backing.Apply();
                _backing.hideFlags = HideFlags.HideAndDontSave;
            }

            GUI.DrawTexture(new Rect(10, 10, PanelWidth, PanelHeight), _backing);
            GUILayout.BeginArea(new Rect(10 + Pad, 10 + Pad,
                                         PanelWidth - Pad * 2f, PanelHeight - Pad * 2f));

            GUILayout.Label(string.Format("<b>Hybrid</b>   surface + tunnels   {0:0} fps", _fps), _panel);

            GUILayout.Label(Scene.Doing == HybridBootstrap.Job.Terraform
                ? "Doing       <b>terraforming</b> - the cell model, unchanged   [M]"
                : "Doing       <b>mining</b> - spans underneath   [M]", _panel);

            GUILayout.Space(4);

            if (Scene.Doing == HybridBootstrap.Job.Terraform)
            {
                GUILayout.Label(string.Format("Tool        <b>{0}</b>   [1 sculpt  2 flatten]",
                    Scene.Surface == HybridBootstrap.SurfaceTool.Sculpt ? "sculpt" : "flatten"), _panel);

                GUILayout.Label(string.Format("Step        <b>{0:0.##} m</b> per click       <b>LMB</b> raise  <b>RMB</b> lower",
                    Scene.StepUnits * CellGrid.MetresPerUnit), _panel);
            }
            else
            {
                GUILayout.Label(string.Format("Bite        <b>{0:0.###} m</b> cube   [- / =]        <b>LMB</b> to mine",
                    Scene.BiteSize), _panel);

                GUILayout.Label(Scene.Selective
                    ? "Mining      <b>selective</b> - only the material aimed at   [T]"
                    : "Mining      <b>everything</b> in the bite   [T]", _panel);
            }

            GUILayout.Label(Scene.HasTarget
                ? string.Format("Cell        <b>{0}, {1}</b> at {2:0.0} m{3}",
                    Scene.TargetCx, Scene.TargetCz,
                    Scene.Grid.GetMetres(Scene.TargetCx, Scene.TargetCz),
                    Scene.World.CellCeded(Scene.TargetCx, Scene.TargetCz) ? "   <b>ceded to spans</b>" : "")
                : "Cell        <i>nothing within reach</i>", _panel);

            GUILayout.Space(6);

            GUILayout.Label(string.Format(
                "Ceded       <b>{0}</b> bricks    columns stored <b>{1:n0}</b> of {2:n0}",
                Scene.World.CededBricks,
                Scene.World.MaterialisedColumns,
                Scene.World.Spans.ColumnsX * Scene.World.Spans.ColumnsZ), _panel);

            GUILayout.Label(string.Format(
                "Seam        worst disagreement <b>{0:0.000000} m</b> along every ceded boundary",
                Scene.SeamError), _panel);

            GUILayout.Space(6);

            GUILayout.Label(string.Format(
                "Last edit   spans <b>{0:0.00} ms</b> + cook <b>{1:0.00} ms</b> over {2} brick{3}",
                Scene.BuildMs, Scene.CookMs, Scene.EditBricks, Scene.EditBricks == 1 ? "" : "s"), _panel);

            GUILayout.Label(string.Format(
                "            surface <b>{0:0.00} ms</b> (whole chunk, not incremental)   {1:n0} tris underground",
                Scene.SurfaceMs, Scene.Triangles), _panel);

            if (!string.IsNullOrEmpty(Scene.Note))
                GUILayout.Label("            <b>" + Scene.Note + "</b>", _panel);

            GUILayout.Space(4);
            GUILayout.Label(string.Format("Heights     <b>{0}</b>   [Tab]        white ground   yellow levelled   blue ceded",
                Scene.ShowHeights ? "shown" : "hidden"), _panel);

            GUILayout.Label(string.Format("View        <b>{0}</b>   [H] next   [L] sky   [Z] undo",
                Scene.ViewName), _panel);

            GUILayout.EndArea();

            DrawHeights();
            DrawCrosshair();
        }

        /// <summary>
        /// Height numbers over nearby cells, the way the plain demo draws them. HudLabels is
        /// shared rather than reimplemented so the numbers read the same in both.
        /// </summary>
        void DrawHeights()
        {
            if (!Scene.ShowHeights) return;
            if (Scene.Doing != HybridBootstrap.Job.Terraform) return;

            Camera cam = Scene.Cam;
            CellGrid g = Scene.Grid;
            if (cam == null || g == null) return;

            if (_label == null) _label = HudLabels.MakeStyle();

            Vector3 eye = cam.transform.position;

            int px, pz;
            g.WorldToCell(eye, out px, out pz);

            int range = Mathf.CeilToInt(Scene.LabelRange / g.CellSize);
            float rangeSq = Scene.LabelRange * Scene.LabelRange;

            Color previous = GUI.color;

            for (int cz = pz - range; cz <= pz + range; cz++)
            {
                for (int cx = px - range; cx <= px + range; cx++)
                {
                    if (!g.InBounds(cx, cz)) continue;

                    Vector3 world = g.CellCentreWorld(cx, cz);
                    if ((world - eye).sqrMagnitude > rangeSq) continue;

                    Vector3 screen = cam.WorldToScreenPoint(world);
                    if (screen.z <= 0.5f) continue;

                    bool target = Scene.HasTarget && cx == Scene.TargetCx && cz == Scene.TargetCz;

                    Color tint = target ? LabelTarget
                               : g.IsFlat(cx, cz) ? LabelFlat
                               : Scene.World.CellCeded(cx, cz) ? LabelCeded
                               : LabelPlain;

                    HudLabels.Draw(new Rect(screen.x - 26f, Screen.height - screen.y - 9f, 52f, 18f),
                                   g.GetMetres(cx, cz).ToString("0.#"), tint, _label);
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
