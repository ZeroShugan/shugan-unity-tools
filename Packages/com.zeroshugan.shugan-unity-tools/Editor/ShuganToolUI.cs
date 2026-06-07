using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// Shared branding UI for all Shugan Unity Tools editor windows.
    /// Single source of truth for store/social links and credit footers — change a URL
    /// here and every tool picks it up. Lifted from the FBXSwapper layout that the user approved.
    /// </summary>
    public static class ShuganToolUI
    {
        // ─── URL constants (single source of truth) ────────────────────────────

        public const string UrlDiscord       = "https://discord.com/invite/6FZmzkb";
        public const string UrlBooth         = "https://shugan.booth.pm/";
        public const string UrlGumroad       = "https://gumroad.com/shugan";
        public const string UrlBlenderMarket = "https://blendermarket.com/creators/shugan";
        public const string UrlWikiRoot      = "https://www.notion.so/shugan/Shugan-Unity-Tools";

        // Convenience: store landing page for the paid AutoRig Feet bundle
        public const string UrlStoreFeetRig  = "https://shugan.booth.pm/";

        // ─── Brand colors ──────────────────────────────────────────────────────

        static readonly Color ColorBlenderMarket = new Color(1f, 0.5f, 0f);

        // ─── Header ────────────────────────────────────────────────────────────

        /// <summary>Large title with a 2px gray separator rule underneath.</summary>
        public static void DrawHeader(string title)
        {
            GUILayout.Label(title, EditorStyles.largeLabel);
            EditorGUILayout.Space(5);
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 2), Color.gray);
        }

        // ─── Social / store links ──────────────────────────────────────────────

        /// <summary>
        /// Horizontal row of branded buttons: Discord · Booth · Gumroad · Blender Market · Wiki.
        /// Pass a tool-specific wiki URL or null to fall back to <see cref="UrlWikiRoot"/>.
        /// </summary>
        public static void DrawSocialLinks(string wikiUrl = null)
        {
            EditorGUILayout.BeginHorizontal();
            Color orig = GUI.color;

            GUI.color = Color.cyan;
            if (GUILayout.Button("Discord", EditorStyles.miniButton, GUILayout.Width(60)))
                Application.OpenURL(UrlDiscord);

            GUI.color = Color.red;
            if (GUILayout.Button("Booth", EditorStyles.miniButton, GUILayout.Width(50)))
                Application.OpenURL(UrlBooth);

            GUI.color = Color.magenta;
            if (GUILayout.Button("Gumroad", EditorStyles.miniButton, GUILayout.Width(70)))
                Application.OpenURL(UrlGumroad);

            GUI.color = ColorBlenderMarket;
            if (GUILayout.Button("Blender Market", EditorStyles.miniButton, GUILayout.Width(100)))
                Application.OpenURL(UrlBlenderMarket);

            GUI.color = Color.white;
            if (GUILayout.Button("Wiki", EditorStyles.miniButton, GUILayout.Width(50)))
                Application.OpenURL(string.IsNullOrEmpty(wikiUrl) ? UrlWikiRoot : wikiUrl);

            GUI.color = orig;
            EditorGUILayout.EndHorizontal();
        }

        // ─── Credits footer ────────────────────────────────────────────────────

        /// <summary>
        /// Centered "Tool Name v1.2.3 — by Shugan" footer label.
        /// Pass <paramref name="version"/> = null to omit the version segment.
        /// Call this as the last thing in a tool's OnGUI().
        /// </summary>
        public static void DrawCredits(string toolName, string version = null)
        {
            EditorGUILayout.Space(4);
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 1), new Color(0.5f, 0.5f, 0.5f, 0.3f));
            EditorGUILayout.Space(2);

            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
            };
            style.normal.textColor = Color.gray;

            string label = string.IsNullOrEmpty(version)
                ? $"{toolName} — by Shugan"
                : $"{toolName} v{version} — by Shugan";

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(label, style);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
    }
}
