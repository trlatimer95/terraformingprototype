using UnityEngine;

namespace Terraform.Play
{
    /// <summary>
    /// Height numbers drawn over the world, shared by both models.
    ///
    /// Deliberately shared rather than duplicated: the playtest asks which terrain model
    /// people prefer, and if one model's numbers were easier to read than the other's,
    /// that difference would come back in the answers dressed up as a difference between
    /// the models.
    /// </summary>
    public static class HudLabels
    {
        /// <summary>
        /// Behind every number. Labels sit over ground whose brightness changes with the
        /// slope and the time of day, so no single text colour stays readable on its own.
        /// A dark backing does that work instead.
        /// </summary>
        static readonly Color Shadow = new Color(0f, 0f, 0f, 0.85f);

        public static GUIStyle MakeStyle()
        {
            var style = new GUIStyle(GUI.skin.label);
            style.fontSize = 13;
            style.fontStyle = FontStyle.Bold;
            style.alignment = TextAnchor.MiddleCenter;
            return style;
        }

        /// <summary>Number with a one-pixel drop shadow, so it reads over any ground.</summary>
        public static void Draw(Rect rect, string text, Color tint, GUIStyle style)
        {
            GUI.color = new Color(Shadow.r, Shadow.g, Shadow.b, Shadow.a * tint.a);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);

            GUI.color = tint;
            GUI.Label(rect, text, style);
        }
    }
}
