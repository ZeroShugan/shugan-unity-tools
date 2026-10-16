// Copyright (c) 2026 Shugan. All rights reserved.
// Part of Shugan Unity Tools. Use is subject to LICENSE.md in the package root.
// No redistribution or resale, modified or not. Not to be used to train AI models, nor given
// to an AI system to reproduce, rewrite or port it.

using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    // The "something is missing" notices of the Setup tab, each with the button that fixes it right there:
    // pick blender.exe, the paid script or a feet prefab from a file dialog, or open the store. Plus a
    // developer preview (the window's ⋮ menu) that shows every notice on a project where nothing is missing.
    public partial class AutoRigFeetDistributor : IHasCustomMenu
    {
        [Flags] enum NoticePreview { None = 0, Blender = 1, Script = 2, Prefabs = 4, Update = 8 }

        // Per editor session: a preview left on must not greet the developer again next week.
        const string SessionNoticePreview = "ShuganTools_ARF_NoticePreview";
        const string PreviewLatestVersion = "9.9.9";

        static NoticePreview Previews
        {
            get => (NoticePreview)SessionState.GetInt(SessionNoticePreview, 0);
            set => SessionState.SetInt(SessionNoticePreview, (int)value);
        }

        static bool Previewing(NoticePreview p) => (Previews & p) != 0;

        public void AddItemsToMenu(GenericMenu menu)
        {
            void Item(string label, NoticePreview flag) =>
                menu.AddItem(new GUIContent("Developer: preview notices/" + label), Previewing(flag), () =>
                {
                    Previews ^= flag;
                    Repaint();
                });
            Item("Blender not found", NoticePreview.Blender);
            Item("Paid script not found", NoticePreview.Script);
            Item("Feet prefabs not found", NoticePreview.Prefabs);
            Item("Bundle update available", NoticePreview.Update);
            menu.AddSeparator("Developer: preview notices/");
            menu.AddItem(new GUIContent("Developer: preview notices/Show all"), false, () =>
            {
                Previews = NoticePreview.Blender | NoticePreview.Script | NoticePreview.Prefabs | NoticePreview.Update;
                Repaint();
            });
            menu.AddItem(new GUIContent("Developer: preview notices/Hide all"), false, () =>
            {
                Previews = NoticePreview.None;
                Repaint();
            });
        }

        // What the notices show: the real state, or missing when previewed.
        bool ShowBlenderMissing => !_depBlender || Previewing(NoticePreview.Blender);
        bool ShowScriptMissing  => !_depAutoRigScript || Previewing(NoticePreview.Script);

        /// <summary>The prefab slots whose prefab is not in the project. In the preview with nothing missing,
        /// every default slot, so the rows look as a customer would see them.</summary>
        PrefabSlot[] MissingPrefabSlots()
        {
            var missing = _prefabSlots.Where(s => s.asset == null && s.path.Length > 0).ToArray();
            if (missing.Length > 0 || !Previewing(NoticePreview.Prefabs)) return missing;
            return _prefabSlots.Where(s => s.path.Length > 0).ToArray();
        }

        struct NoticeButton
        {
            public string label, tip;
            public Action act;
            public Color? tint;
        }

        static NoticeButton Act(string label, string tip, Action act) =>
            new NoticeButton { label = label, tip = tip, act = act };

        static NoticeButton Link(string label, string url, Color tint) =>
            new NoticeButton { label = label, tip = url, act = () => Application.OpenURL(url), tint = tint };

        /// <summary>A little space between the fixes and the store links.</summary>
        static readonly NoticeButton Gap = default;

        /// <summary>A notice, then its buttons in one row under it, each as wide as its label.</summary>
        static void Notice(string title, string message, MessageType type, params NoticeButton[] buttons)
        {
            EditorGUILayout.HelpBox(string.IsNullOrEmpty(title) ? message : title + "\n" + message, type);
            if (buttons.Length > 0)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(4);
                foreach (var b in buttons)
                {
                    if (b.label == null) { GUILayout.Space(12); continue; }
                    Color prev = GUI.color;
                    if (b.tint.HasValue) GUI.color = b.tint.Value;
                    if (GUILayout.Button(new GUIContent(b.label, b.tip), EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                    {
                        var act = b.act;
                        // A file dialog opened in the middle of a layout pass breaks it; run after this frame.
                        EditorApplication.delayCall += () => act();
                    }
                    GUI.color = prev;
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.Space(4);
        }

        // ─── The fixes ─────────────────────────────────────────────────────────

        void ChooseBlender()
        {
            string start = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string picked = EditorUtility.OpenFilePanel("Where is blender.exe?", start, "exe");
            if (string.IsNullOrEmpty(picked)) return;
            if (!string.Equals(Path.GetFileName(picked), "blender.exe", StringComparison.OrdinalIgnoreCase)
                && !EditorUtility.DisplayDialog("AutoRig Feet",
                       Path.GetFileName(picked) + " is not blender.exe. Use it anyway?", "Use it", "Cancel"))
                return;
            ArfPrefs.SetString(BlenderBridge.PrefBlenderPath, picked.Replace("/", "\\"));
            SetStatus("Blender set: " + picked.Replace("/", "\\"), MessageType.Info);
            Repaint();
        }

        void DetectBlenderFromNotice()
        {
            TryAutoDetectBlender(silent: false);
            Repaint();
        }

        void ChooseScript()
        {
            BrowseForAutoRigScript();
            _autoRigScriptResolvedPath = ResolveAutoRigScriptPath();
            if (!string.IsNullOrEmpty(_autoRigScriptResolvedPath))
                SetStatus("AutoRig Feet script set: " + _autoRigScriptResolvedPath, MessageType.Info);
            Repaint();
        }

        /// <summary>Pick the prefab file for a slot; it has to be inside this project to be added to an avatar.</summary>
        void ChoosePrefab(PrefabSlot slot)
        {
            string file = Path.GetFileName(slot.path);
            string picked = EditorUtility.OpenFilePanel(
                string.IsNullOrEmpty(file) ? "Select a prefab" : "Where is " + file + "?", Application.dataPath, "prefab");
            if (string.IsNullOrEmpty(picked)) return;
            string rel = ToProjectRelative(picked);
            var asset = LoadPrefabAsset(rel);
            if (asset == null)
            {
                SetStatus(rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(rel) + " is not a prefab."
                    : "The prefab has to be inside this Unity project (under Assets). Import the bundle's "
                      + ".unitypackage first, then pick it from there.", MessageType.Warning);
                Repaint();
                return;
            }
            if (!_prefabSlots.Contains(slot)) _prefabSlots.Add(slot);
            slot.path = rel;
            slot.asset = asset;
            slot.searched = false;
            SavePrefabSlots();
            SetStatus("Prefab set: " + rel, MessageType.Info);
            Repaint();
        }

        void SearchPrefabsAgain()
        {
            foreach (var s in _prefabSlots) s.searched = false;
            ResolvePrefabSlots(searchByName: true);
            int still = _prefabSlots.Count(s => s.asset == null && s.path.Length > 0);
            SetStatus(still == 0 ? "All feet prefabs found." : still + " prefab(s) still not found in this project.",
                      still == 0 ? MessageType.Info : MessageType.Warning);
            Repaint();
        }

        // ─── The notices ───────────────────────────────────────────────────────

        /// <summary>The feet prefabs notice of Requirements, then one line per missing prefab with its own "Choose…".</summary>
        void DrawMissingPrefabs(PrefabSlot[] missing)
        {
            Notice("FX feet prefabs not found (optional).",
                "They come with the AutoRig Feet bundle and are added under the avatar after rigging. The rig works "
                + "without them; the gesture and hand controls are just not added.",
                MessageType.Info,
                Act("Search project", "Look for each prefab by its file name anywhere in the project.", SearchPrefabsAgain),
                Gap,
                Link("Get the bundle", StoreBoothUrl, new Color(1f, 0.45f, 0.45f)));
            foreach (var slot in missing)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(8);
                var s = slot;
                if (GUILayout.Button(new GUIContent("Choose…", "Pick this prefab from a file dialog."),
                        EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                    EditorApplication.delayCall += () => ChoosePrefab(s);
                GUILayout.Label(new GUIContent(Path.GetFileName(slot.path), "Expected at " + slot.path), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.Space(4);
        }
    }
}
