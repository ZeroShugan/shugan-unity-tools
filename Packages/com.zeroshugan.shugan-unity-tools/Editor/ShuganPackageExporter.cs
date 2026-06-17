// ╔══════════════════════════════════════════════════════════════════════╗
// ║          SHUGAN'S PACKAGE EXPORTER                                    ║
// ║          Preset-driven .unitypackage export with dependency picking  ║
// ║          and semver auto-versioning. — by Shugan                     ║
// ╚══════════════════════════════════════════════════════════════════════╝

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    // NOTE: PackageExportPreset (the ScriptableObject) lives in its own runtime file
    // PackageExportPreset.cs — a ScriptableObject must be in a file whose name matches
    // the class name, or Unity can't create its MonoScript and the assets won't be found.

    public class ShuganPackageExporter : EditorWindow
    {
        const string TOOL_NAME    = "Package Exporter";
        const string TOOL_VERSION = "1.0";
        const string WIKI_URL     = "https://www.notion.so/shugan/Package-Exporter";

        const string PRESETS_FOLDER = "Assets/! Shugan/!_Lab/Script/Presets";

        PackageExportPreset _preset;
        string _newPresetName = "NewPackagePreset";

        // Discovered dependency paths (excludes roots), recomputed on Refresh.
        List<string> _currentDeps = new List<string>();
        HashSet<string> _addedSinceRefresh   = new HashSet<string>();
        HashSet<string> _removedSinceRefresh = new HashSet<string>();
        HashSet<string> _removedPaths        = new HashSet<string>();  // asset paths of removed deps
        bool _hasRefreshed;

        // Tree filter: show everything, or only the new / only the removed dependencies.
        enum DepFilter { All, NewOnly, RemovedOnly }
        DepFilter _depFilter = DepFilter.All;

        // Folder-tree view of the dependencies (like Unity's native Export Package window).
        DepNode _depTree;
        readonly HashSet<string> _collapsedFolders = new HashSet<string>();

        // One node in the dependency folder tree (a folder, or a leaf file).
        class DepNode
        {
            public string name;
            public string fullPath;   // "Assets/Foo" (folder) or "Assets/Foo/bar.png" (file)
            public bool   isFolder;
            public string filePath;   // asset path, files only
            public readonly SortedDictionary<string, DepNode> children =
                new SortedDictionary<string, DepNode>(System.StringComparer.OrdinalIgnoreCase);
        }

        Vector2 _scroll, _depScroll;
        string _status = "";
        MessageType _statusType = MessageType.None;

        [MenuItem("Tools/Shugan/Package Exporter", false, 1926)]
        static void Open()
        {
            var win = GetWindow<ShuganPackageExporter>("Package Exporter");
            win.minSize = new Vector2(480, 540);
        }

        void OnEnable()
        {
            // Auto-select the first preset if one exists.
            var presets = FindAllPresets();
            if (_preset == null && presets.Count > 0) SelectPreset(presets[0]);
        }

        // ─── GUI ───────────────────────────────────────────────────────────────

        void OnGUI()
        {
            ShuganToolUI.DrawHeader("Package Exporter");
            ShuganToolUI.DrawSocialLinks(WIKI_URL);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawPresetBar();
            Separator();

            if (_preset == null)
            {
                EditorGUILayout.HelpBox(
                    "Create or select a preset to begin.\n" +
                    "A preset captures which assets to export, which dependencies to keep, " +
                    "the naming scheme, and the version.",
                    MessageType.Info);
                EditorGUILayout.EndScrollView();
                ShuganToolUI.DrawCredits(TOOL_NAME, TOOL_VERSION);
                return;
            }

            DrawNamingSection();
            Separator();
            DrawRootsSection();
            Separator();
            DrawDependencySection();

            EditorGUILayout.EndScrollView();

            DrawExportBar();

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, _statusType);

            ShuganToolUI.DrawCredits(TOOL_NAME, TOOL_VERSION);
        }

        void DrawPresetBar()
        {
            GUILayout.Label("Preset", EditorStyles.boldLabel);

            var presets = FindAllPresets();
            EditorGUILayout.BeginHorizontal();

            string[] names = presets.Select(p => p.name).ToArray();
            int current = _preset != null ? presets.IndexOf(_preset) : -1;
            int picked = EditorGUILayout.Popup(current, names);
            if (picked != current && picked >= 0 && picked < presets.Count)
                SelectPreset(presets[picked]);

            if (_preset != null && GUILayout.Button("Delete", GUILayout.Width(60)))
                DeleteCurrentPreset();
            EditorGUILayout.EndHorizontal();

            // Direct reference to the selected preset: drag a preset here to select it, or click the
            // value to ping/highlight it in the Project window.
            EditorGUI.BeginChangeCheck();
            var dropped = (PackageExportPreset)EditorGUILayout.ObjectField(
                new GUIContent("Selected Preset",
                    "Drag a preset here to select it, or click it to highlight the asset in the Project."),
                _preset, typeof(PackageExportPreset), false);
            if (EditorGUI.EndChangeCheck() && dropped != null && dropped != _preset)
                SelectPreset(dropped);

            EditorGUILayout.BeginHorizontal();
            _newPresetName = EditorGUILayout.TextField(_newPresetName);
            if (GUILayout.Button("+ New Preset", GUILayout.Width(110)))
                CreatePreset(_newPresetName);
            EditorGUILayout.EndHorizontal();
        }

        void DrawNamingSection()
        {
            GUILayout.Label("Naming & Version", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            _preset.packagePrefix = EditorGUILayout.TextField(
                new GUIContent("Prefix", "Prepended to the file name, e.g. \"Shugan_\""), _preset.packagePrefix);
            _preset.baseName = EditorGUILayout.TextField(
                new GUIContent("Base Name", "The package name, e.g. \"FeetRig\""), _preset.baseName);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent("Version", "Semver. Patch auto-bumps on export if the file already exists."), GUILayout.Width(146));
            _preset.versionLabel = EditorGUILayout.TextField(_preset.versionLabel, GUILayout.Width(28));
            _preset.major = Mathf.Max(0, EditorGUILayout.IntField(_preset.major, GUILayout.Width(40)));
            EditorGUILayout.LabelField(".", GUILayout.Width(8));
            _preset.minor = Mathf.Max(0, EditorGUILayout.IntField(_preset.minor, GUILayout.Width(40)));
            EditorGUILayout.LabelField(".", GUILayout.Width(8));
            _preset.patch = Mathf.Max(0, EditorGUILayout.IntField(_preset.patch, GUILayout.Width(40)));
            EditorGUILayout.EndHorizontal();

            // Export folder
            EditorGUILayout.BeginHorizontal();
            if (string.IsNullOrEmpty(_preset.exportFolder)) _preset.exportFolder = DefaultExportFolder();
            _preset.exportFolder = EditorGUILayout.TextField(
                new GUIContent("Export Folder", "Where .unitypackage files are written."), _preset.exportFolder);
            if (GUILayout.Button("Browse", GUILayout.Width(64)))
            {
                string picked = EditorUtility.OpenFolderPanel("Select Export Folder",
                    Directory.Exists(_preset.exportFolder) ? _preset.exportFolder : DefaultExportFolder(), "");
                if (!string.IsNullOrEmpty(picked)) _preset.exportFolder = picked.Replace("/", "\\");
            }
            EditorGUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck()) MarkPresetDirty();

            // Live filename preview
            EditorGUILayout.LabelField("Next file:", BuildFileName(_preset.major, _preset.minor, _preset.patch),
                EditorStyles.miniLabel);
        }

        void DrawRootsSection()
        {
            GUILayout.Label("Assets to Export (roots)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Drag prefabs, folders, scenes, or any asset here.", EditorStyles.miniLabel);

            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < _preset.rootAssets.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _preset.rootAssets[i] = EditorGUILayout.ObjectField(_preset.rootAssets[i], typeof(Object), false);
                if (GUILayout.Button("×", GUILayout.Width(22))) { _preset.rootAssets.RemoveAt(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ Add Asset Slot", GUILayout.Height(22)))
                _preset.rootAssets.Add(null);
            if (EditorGUI.EndChangeCheck()) MarkPresetDirty();
        }

        void DrawDependencySection()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Dependencies", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("↻ Refresh Dependencies", GUILayout.Width(180)))
                RefreshDependencies();
            EditorGUILayout.EndHorizontal();

            if (!_hasRefreshed)
            {
                EditorGUILayout.HelpBox("Click Refresh to scan the selected assets for dependencies.", MessageType.None);
                return;
            }

            bool hasDiff = _addedSinceRefresh.Count > 0 || _removedPaths.Count > 0;

            // Diff summary since last refresh
            if (hasDiff)
            {
                Color c = GUI.color;
                if (_addedSinceRefresh.Count > 0)
                {
                    GUI.color = new Color(0.5f, 1f, 0.5f);
                    EditorGUILayout.LabelField($"+ {_addedSinceRefresh.Count} new dependency(ies) since last refresh", EditorStyles.miniLabel);
                }
                if (_removedPaths.Count > 0)
                {
                    GUI.color = new Color(1f, 0.55f, 0.55f);
                    EditorGUILayout.LabelField($"− {_removedPaths.Count} dependency(ies) removed (shown in red, not exported)", EditorStyles.miniLabel);
                }
                GUI.color = c;
            }

            int included = _currentDeps.Count(p => !IsExcluded(p));
            EditorGUILayout.LabelField($"{included} / {_currentDeps.Count} dependencies included", EditorStyles.miniLabel);

            // Filter row — only meaningful when there's a diff to look at.
            if (hasDiff)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Show:", GUILayout.Width(40));
                DrawFilterToggle("All", DepFilter.All);
                DrawFilterToggle($"New (+{_addedSinceRefresh.Count})", DepFilter.NewOnly);
                DrawFilterToggle($"Removed (−{_removedPaths.Count})", DepFilter.RemovedOnly);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            else _depFilter = DepFilter.All;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Include All", EditorStyles.miniButton, GUILayout.Width(82)))
            { _preset.excludedDependencyGuids.Clear(); MarkPresetDirty(); }
            if (GUILayout.Button("Exclude All", EditorStyles.miniButton, GUILayout.Width(82)))
            { _preset.excludedDependencyGuids = _currentDeps.Select(AssetDatabase.AssetPathToGUID).ToList(); MarkPresetDirty(); }
            GUILayout.Space(8);
            if (GUILayout.Button("Expand All", EditorStyles.miniButton, GUILayout.Width(82)))
                _collapsedFolders.Clear();
            if (GUILayout.Button("Collapse All", EditorStyles.miniButton, GUILayout.Width(86)))
                CollapseAll(_depTree);
            EditorGUILayout.EndHorizontal();

            _depScroll = EditorGUILayout.BeginScrollView(_depScroll, GUILayout.MinHeight(140), GUILayout.MaxHeight(320));
            if (_depTree != null) DrawDepNode(_depTree, 0);
            EditorGUILayout.EndScrollView();
        }

        // Recursive folder-tree renderer (mirrors Unity's native Export Package window):
        // folders get a fold arrow + a select-whole-folder tri-state toggle; files get a checkbox.
        void DrawDepNode(DepNode node, int indent)
        {
            foreach (var child in node.children.Values
                         .OrderByDescending(c => c.isFolder)
                         .ThenBy(c => c.name, System.StringComparer.OrdinalIgnoreCase))
            {
                // Apply the New/Removed filter: skip files and empty folders that don't match.
                if (child.isFolder) { if (!FolderHasVisibleLeaf(child)) continue; }
                else if (!LeafMatchesFilter(child)) continue;

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(indent * 14f);

                if (child.isFolder)
                {
                    bool collapsed = _collapsedFolders.Contains(child.fullPath);
                    if (GUILayout.Button(collapsed ? "▶" : "▼", EditorStyles.label, GUILayout.Width(14)))
                    {
                        if (collapsed) _collapsedFolders.Remove(child.fullPath);
                        else _collapsedFolders.Add(child.fullPath);
                    }

                    var (all, any) = FolderState(child);
                    EditorGUI.showMixedValue = any && !all;
                    bool newState = EditorGUILayout.Toggle(all, GUILayout.Width(16));
                    EditorGUI.showMixedValue = false;
                    if (newState != all) { SetFolderInclusion(child, newState); MarkPresetDirty(); }

                    var folderIcon = EditorGUIUtility.IconContent("Folder Icon");
                    if (folderIcon != null && folderIcon.image != null)
                        GUILayout.Label(folderIcon.image, GUILayout.Width(16), GUILayout.Height(16));
                    GUILayout.Label(child.name, EditorStyles.boldLabel);
                    EditorGUILayout.EndHorizontal();

                    if (!collapsed) DrawDepNode(child, indent + 1);
                }
                else if (_removedPaths.Contains(child.filePath))
                {
                    // Removed dependency: red, NOT checkable, not exported. Click pings the asset.
                    GUILayout.Space(16); // placeholder where the checkbox would be
                    var icon = AssetDatabase.GetCachedIcon(child.filePath);
                    if (icon != null) GUILayout.Label(icon, GUILayout.Width(16), GUILayout.Height(16));

                    Color c = GUI.color;
                    GUI.color = new Color(1f, 0.5f, 0.5f);
                    if (GUILayout.Button(new GUIContent("− " + child.name + "   (removed)", child.filePath), EditorStyles.label))
                    {
                        var obj = AssetDatabase.LoadMainAssetAtPath(child.filePath);
                        if (obj != null) EditorGUIUtility.PingObject(obj);
                    }
                    GUI.color = c;
                    EditorGUILayout.EndHorizontal();
                }
                else
                {
                    GUILayout.Space(14); // align file checkbox under the folder fold arrow
                    string guid    = AssetDatabase.AssetPathToGUID(child.filePath);
                    bool included  = !_preset.excludedDependencyGuids.Contains(guid);
                    bool isAdded   = _addedSinceRefresh.Contains(guid);

                    bool newInc = EditorGUILayout.Toggle(included, GUILayout.Width(16));
                    if (newInc != included)
                    {
                        if (newInc) _preset.excludedDependencyGuids.Remove(guid);
                        else if (!_preset.excludedDependencyGuids.Contains(guid)) _preset.excludedDependencyGuids.Add(guid);
                        MarkPresetDirty();
                    }

                    var icon = AssetDatabase.GetCachedIcon(child.filePath);
                    if (icon != null) GUILayout.Label(icon, GUILayout.Width(16), GUILayout.Height(16));

                    Color c = GUI.color;
                    if (isAdded) GUI.color = new Color(0.6f, 1f, 0.6f);
                    else if (!included) GUI.color = new Color(0.6f, 0.6f, 0.6f);
                    if (GUILayout.Button(new GUIContent((isAdded ? "● " : "") + child.name, child.filePath), EditorStyles.label))
                        EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(child.filePath));
                    GUI.color = c;

                    EditorGUILayout.EndHorizontal();
                }
            }
        }

        void DrawFilterToggle(string label, DepFilter f)
        {
            bool on = _depFilter == f;
            Color prev = GUI.backgroundColor;
            if (on) GUI.backgroundColor = new Color(0.5f, 0.7f, 1f);
            if (GUILayout.Button(label, EditorStyles.miniButton))
                _depFilter = f;
            GUI.backgroundColor = prev;
        }

        // Does this leaf pass the current New/Removed filter?
        bool LeafMatchesFilter(DepNode leaf)
        {
            if (_depFilter == DepFilter.All) return true;
            if (_depFilter == DepFilter.RemovedOnly) return _removedPaths.Contains(leaf.filePath);
            // NewOnly
            return _addedSinceRefresh.Contains(AssetDatabase.AssetPathToGUID(leaf.filePath));
        }

        // Does this folder contain at least one leaf visible under the current filter?
        bool FolderHasVisibleLeaf(DepNode folder)
        {
            if (_depFilter == DepFilter.All) return true;
            foreach (var leaf in Leaves(folder))
                if (LeafMatchesFilter(leaf)) return true;
            return false;
        }

        static DepNode BuildDepTree(List<string> paths)
        {
            var root = new DepNode { name = "Assets", fullPath = "Assets", isFolder = true };
            foreach (string p in paths)
            {
                string[] segs = p.Split('/');
                var node = root;
                string accum = segs[0];
                for (int i = 1; i < segs.Length; i++)
                {
                    accum += "/" + segs[i];
                    bool isLast = i == segs.Length - 1;
                    if (!node.children.TryGetValue(segs[i], out var child))
                    {
                        child = new DepNode { name = segs[i], fullPath = accum, isFolder = !isLast };
                        node.children[segs[i]] = child;
                    }
                    if (isLast) { child.isFolder = false; child.filePath = p; }
                    node = child;
                }
            }
            return root;
        }

        static IEnumerable<DepNode> Leaves(DepNode node)
        {
            foreach (var child in node.children.Values)
            {
                if (child.isFolder) { foreach (var l in Leaves(child)) yield return l; }
                else yield return child;
            }
        }

        (bool all, bool any) FolderState(DepNode folder)
        {
            int total = 0, inc = 0;
            foreach (var leaf in Leaves(folder))
            {
                if (_removedPaths.Contains(leaf.filePath)) continue; // removed deps aren't selectable
                total++;
                if (!_preset.excludedDependencyGuids.Contains(AssetDatabase.AssetPathToGUID(leaf.filePath))) inc++;
            }
            return (total > 0 && inc == total, inc > 0);
        }

        void SetFolderInclusion(DepNode folder, bool include)
        {
            foreach (var leaf in Leaves(folder))
            {
                if (_removedPaths.Contains(leaf.filePath)) continue; // can't toggle removed deps
                string guid = AssetDatabase.AssetPathToGUID(leaf.filePath);
                if (include) _preset.excludedDependencyGuids.Remove(guid);
                else if (!_preset.excludedDependencyGuids.Contains(guid)) _preset.excludedDependencyGuids.Add(guid);
            }
        }

        void CollapseAll(DepNode node)
        {
            if (node == null) return;
            foreach (var child in node.children.Values)
            {
                if (!child.isFolder) continue;
                _collapsedFolders.Add(child.fullPath);
                CollapseAll(child);
            }
        }

        void DrawExportBar()
        {
            EditorGUILayout.Space(4);
            int rootCount = _preset.rootAssets.Count(a => a != null);
            int depCount  = _hasRefreshed ? _currentDeps.Count(p => !IsExcluded(p)) : 0;

            EditorGUI.BeginDisabledGroup(rootCount == 0);
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = rootCount > 0 ? new Color(0.4f, 0.8f, 0.4f) : Color.white;
            string label = _hasRefreshed
                ? $"▶  Export Package  ({rootCount} roots + {depCount} deps)"
                : $"▶  Export Package  ({rootCount} roots — refresh deps first)";
            if (GUILayout.Button(label, GUILayout.Height(34)))
                ExportPackage();
            GUI.backgroundColor = prev;
            EditorGUI.EndDisabledGroup();
        }

        // ─── Actions ─────────────────────────────────────────────────────────────

        void RefreshDependencies()
        {
            var rootPaths = GetRootPaths();
            _currentDeps = ComputeDependencies(rootPaths);

            var currentGuids = new HashSet<string>(_currentDeps.Select(AssetDatabase.AssetPathToGUID));
            var last = new HashSet<string>(_preset.lastKnownDependencyGuids);
            _addedSinceRefresh   = new HashSet<string>(currentGuids.Where(g => !last.Contains(g)));
            _removedSinceRefresh = new HashSet<string>(last.Where(g => !currentGuids.Contains(g)));

            // Resolve removed-dependency GUIDs back to asset paths (only those still on disk), so they
            // can be shown (red, not exported) in the tree alongside the current dependencies.
            _removedPaths = new HashSet<string>(_removedSinceRefresh
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p) && p.StartsWith("Assets/")));

            _depTree = BuildDepTree(_currentDeps.Concat(_removedPaths).Distinct().ToList());
            _depFilter = DepFilter.All;

            // Drop exclusions that no longer correspond to a real dependency.
            _preset.excludedDependencyGuids.RemoveAll(g => !currentGuids.Contains(g));
            _preset.lastKnownDependencyGuids = currentGuids.ToList();
            _hasRefreshed = true;
            MarkPresetDirty();

            SetStatus($"Found {_currentDeps.Count} dependencies " +
                      $"(+{_addedSinceRefresh.Count} new, −{_removedPaths.Count} removed).", MessageType.Info);
        }

        void ExportPackage()
        {
            var rootPaths = GetRootPaths();
            if (rootPaths.Count == 0) { SetStatus("Add at least one valid asset to export.", MessageType.Error); return; }

            var deps = _hasRefreshed ? _currentDeps : ComputeDependencies(rootPaths);
            var includedDeps = deps.Where(p => !IsExcluded(p)).ToList();
            var exportPaths = rootPaths.Concat(includedDeps).Distinct().ToArray();

            string folder = string.IsNullOrEmpty(_preset.exportFolder) ? DefaultExportFolder() : _preset.exportFolder;
            try { Directory.CreateDirectory(folder); }
            catch (System.Exception e) { SetStatus("Cannot create export folder: " + e.Message, MessageType.Error); return; }

            // Auto-version: bump patch until the filename is free, so old exports are never overwritten.
            int patch = _preset.patch;
            string file;
            while (true)
            {
                file = Path.Combine(folder, BuildFileName(_preset.major, _preset.minor, patch));
                if (!File.Exists(file)) break;
                patch++;
            }

            // Confirm before writing — show exactly what will happen.
            string bumpNote = patch != _preset.patch
                ? $"\n\nNote: patch auto-bumped to {patch} (a file with version {_preset.major}.{_preset.minor}.{_preset.patch} already exists — the old export is preserved)."
                : "";
            bool confirmed = EditorUtility.DisplayDialog(
                "Export Package?",
                $"Export {exportPaths.Length} asset(s) — {rootPaths.Count} root(s) + {includedDeps.Count} dependency(ies) — as:\n\n" +
                $"{Path.GetFileName(file)}\n\n" +
                $"To folder:\n{folder}{bumpNote}",
                "Export", "Cancel");
            if (!confirmed) { SetStatus("Export cancelled.", MessageType.None); return; }

            AssetDatabase.ExportPackage(exportPaths, file, ExportPackageOptions.Default);

            _preset.patch = patch; // remember what we shipped; next export bumps again
            MarkPresetDirty();
            AssetDatabase.SaveAssets();

            SetStatus($"✓ Exported {Path.GetFileName(file)}  ({exportPaths.Length} assets)", MessageType.Info);
            if (File.Exists(file)) EditorUtility.RevealInFinder(file);
        }

        // ─── Preset management ─────────────────────────────────────────────────

        static List<PackageExportPreset> FindAllPresets()
        {
            return AssetDatabase.FindAssets("t:PackageExportPreset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<PackageExportPreset>)
                .Where(p => p != null)
                .OrderBy(p => p.name)
                .ToList();
        }

        void SelectPreset(PackageExportPreset p)
        {
            _preset = p;
            _hasRefreshed = false;
            _addedSinceRefresh.Clear();
            _removedSinceRefresh.Clear();
            _removedPaths.Clear();
            _depFilter = DepFilter.All;
            _currentDeps.Clear();
            _status = "";
        }

        void CreatePreset(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName)) { SetStatus("Enter a preset name first.", MessageType.Warning); return; }
            EnsureFolder(PRESETS_FOLDER);

            var p = CreateInstance<PackageExportPreset>();
            p.exportFolder = DefaultExportFolder();
            p.baseName = presetName;

            string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{PRESETS_FOLDER}/{presetName}.asset");
            AssetDatabase.CreateAsset(p, assetPath);
            AssetDatabase.SaveAssets();
            SelectPreset(p);
            SetStatus($"Created preset: {Path.GetFileName(assetPath)}", MessageType.Info);
        }

        void DeleteCurrentPreset()
        {
            if (_preset == null) return;
            string path = AssetDatabase.GetAssetPath(_preset);
            if (!EditorUtility.DisplayDialog("Delete Preset?",
                $"Delete '{_preset.name}'?\nThis only removes the preset, not your assets.", "Delete", "Cancel")) return;
            AssetDatabase.DeleteAsset(path);
            _preset = null;
            var rest = FindAllPresets();
            if (rest.Count > 0) SelectPreset(rest[0]);
        }

        void MarkPresetDirty()
        {
            if (_preset != null) EditorUtility.SetDirty(_preset);
        }

        // ─── Helpers ─────────────────────────────────────────────────────────────

        List<string> GetRootPaths()
        {
            return _preset.rootAssets
                .Where(a => a != null)
                .Select(AssetDatabase.GetAssetPath)
                .Where(p => !string.IsNullOrEmpty(p) && p.StartsWith("Assets/"))
                .Distinct()
                .ToList();
        }

        static List<string> ComputeDependencies(List<string> rootPaths)
        {
            if (rootPaths.Count == 0) return new List<string>();
            var all = AssetDatabase.GetDependencies(rootPaths.ToArray(), true);
            var roots = new HashSet<string>(rootPaths);
            return all
                .Where(p => p.StartsWith("Assets/") && !roots.Contains(p))
                .Distinct()
                .OrderBy(p => p)
                .ToList();
        }

        bool IsExcluded(string path)
            => _preset.excludedDependencyGuids.Contains(AssetDatabase.AssetPathToGUID(path));

        string BuildFileName(int major, int minor, int patch)
            => $"{_preset.packagePrefix}{_preset.baseName}_{_preset.versionLabel}{major}.{minor}.{patch}.unitypackage";

        static string DefaultExportFolder()
        {
            // <project-parent>/PACKAGE  → for the MAIN project this resolves to ...\!MAIN\PACKAGE
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "PACKAGE"));
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace("\\", "/");
            string leaf   = Path.GetFileName(folder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        void SetStatus(string msg, MessageType type) { _status = msg; _statusType = type; }

        void Separator()
        {
            EditorGUILayout.Space(6);
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 1), new Color(0.5f, 0.5f, 0.5f, 0.3f));
            EditorGUILayout.Space(6);
        }
    }
}
