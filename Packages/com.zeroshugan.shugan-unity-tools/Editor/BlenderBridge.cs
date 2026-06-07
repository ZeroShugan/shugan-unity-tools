using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    public class BlenderBridge : EditorWindow
    {
        enum ExportMode { Replace, AddNew }

        public const string PrefBlenderPath  = "ShuganTools_BlenderPath";
        const string PrefHeadless     = "ShuganTools_BB_Headless";
        const string PrefSourceFbx    = "ShuganTools_BB_SourceFbx";
        const string PrefMeshIndex    = "ShuganTools_BB_MeshIndex";
        const string PrefExportMode   = "ShuganTools_BB_ExportMode";
        const string PrefAddNewFolder = "ShuganTools_BB_AddNewFolder";
        const string PrefAddNewName   = "ShuganTools_BB_AddNewName";
        const string PrefScriptPath   = "ShuganTools_BB_ScriptPath";

        // Blender path
        string _blenderPath = "";

        // Launch settings
        bool  _factoryStartup = true;
        bool  _headless       = false;
        float _stepDelay      = 1.5f;

        // Script to run on the imported FBX
        UnityEngine.Object _scriptAsset;        // .py file imported as DefaultAsset (when in-project)
        string             _scriptAbsPath = ""; // absolute path; populated from asset OR Browse

        // Source FBX + target mesh
        GameObject _sourceFbx;
        string[]   _meshNames         = new string[0];
        int        _selectedMeshIndex = 0;
        ExportMode _exportMode        = ExportMode.AddNew;
        string     _addNewFolder      = "";   // empty = same folder as source FBX
        string     _addNewName        = "";   // empty = source FBX name (auto-suffixed)

        // Progress tracking
        const float EstimatedDurationSec = 120f;

        static readonly (string marker, float progress)[] ProgressMarkers =
        {
            ("[BlenderBridge] Default scene objects removed", 0.03f),
            ("[BlenderBridge] Importing:",                   0.06f),
            ("[BlenderBridge] Selected:",                    0.10f),
            ("[BlenderBridge] Running script:",              0.13f),
            ("DETECTING FOOT AND TOE BONES",                 0.15f),
            ("USING IMPROVED TIP/END DETECTION:",            0.22f),
            ("CALCULATED HEAD COORDINATES",                  0.27f),
            ("BUILDING BVH TREE",                            0.30f),
            ("BVH tree built",                               0.32f),
            ("STARTING FILTERING SYSTEM",                    0.35f),
            ("CREATING ARMATURE AND BONES",                  0.42f),
            ("Creating bones for POSITIVE X",                0.44f),
            ("Creating bones for NEGATIVE X",                0.50f),
            ("PHASE 2: APPLYING WEIGHT PAINTING",            0.56f),
            ("WEIGHT PAINTING WITH FILTERING COMPLETE",      0.80f),
            ("Debug empties skipped",                        0.82f),
            ("Join toe bones",                               0.84f),
            ("REPOSITIONING BIGTOE BONES",                   0.88f),
            ("BIGTOE REPOSITIONING COMPLETE",                0.93f),
            ("ALL OPERATIONS COMPLETED",                     0.95f),
            ("[BlenderBridge] Exporting",                    0.96f),
            ("[BlenderBridge] Export done",                  0.99f),
        };

        // Exposed for external orchestrators (e.g. AutoRigFeetDistributor)
        public static System.Collections.ObjectModel.ReadOnlyCollection<(string marker, float progress)>
            AutoRigProgressMarkers => System.Array.AsReadOnly(ProgressMarkers);

        Queue<string> _outputQueue      = new Queue<string>();
        readonly object _outputLock     = new object();
        float         _milestoneProgress = 0f;
        float         _displayProgress   = 0f;
        double        _processStartTime  = 0;
        double        _lastUpdateTime    = 0;
        string        _lastMilestone     = "";

        // State
        Vector2     _scroll;
        string      _statusMsg  = "";
        MessageType _statusType = MessageType.None;
        Process     _blenderProcess;

        const string WikiUrl     = "https://www.notion.so/shugan/Shugan-Unity-Tools";
        const string ToolVersion = "1.0";

        // ─── Menu ──────────────────────────────────────────────────────────────

        [MenuItem("Tools/Shugan/Blender Bridge", false, 1922)]
        static void Open()
        {
            var win = GetWindow<BlenderBridge>("Blender Bridge");
            win.minSize = new Vector2(460, 360);
        }

        // ─── Lifecycle ─────────────────────────────────────────────────────────

        void OnEnable()
        {
            _blenderPath  = EditorPrefs.GetString(PrefBlenderPath, "");
            _headless     = EditorPrefs.GetBool(PrefHeadless, false);
            _exportMode   = (ExportMode)EditorPrefs.GetInt(PrefExportMode, (int)ExportMode.AddNew);
            _addNewFolder = EditorPrefs.GetString(PrefAddNewFolder, "");
            _addNewName   = EditorPrefs.GetString(PrefAddNewName, "");

            string savedFbx = EditorPrefs.GetString(PrefSourceFbx, "");
            if (!string.IsNullOrEmpty(savedFbx))
            {
                _sourceFbx = AssetDatabase.LoadAssetAtPath<GameObject>(savedFbx);
                if (_sourceFbx != null) RefreshMeshNames();
            }
            _selectedMeshIndex = Mathf.Clamp(EditorPrefs.GetInt(PrefMeshIndex, 0), 0, Mathf.Max(0, _meshNames.Length - 1));

            // Restore script path
            _scriptAbsPath = EditorPrefs.GetString(PrefScriptPath, "");
            if (!string.IsNullOrEmpty(_scriptAbsPath))
            {
                string rel = ToProjectRelative(_scriptAbsPath);
                if (!string.IsNullOrEmpty(rel) && File.Exists(_scriptAbsPath))
                    _scriptAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(rel);
            }

            if (string.IsNullOrEmpty(_blenderPath))
                TryAutoDetect(silent: true);
        }

        // Polls process, drains stdout queue, and drives the progress bar.
        void Update()
        {
            if (_blenderProcess == null) return;

            double now = EditorApplication.timeSinceStartup;
            float  dt  = (float)(now - _lastUpdateTime);
            _lastUpdateTime = now;

            // Drain output on main thread
            lock (_outputLock)
            {
                while (_outputQueue.Count > 0)
                {
                    string line = _outputQueue.Dequeue();
                    foreach (var (marker, progress) in ProgressMarkers)
                    {
                        if (line.Contains(marker) && progress > _milestoneProgress)
                        {
                            _milestoneProgress = progress;
                            _lastMilestone = line.Trim();
                            break;
                        }
                    }
                }
            }

            if (!_blenderProcess.HasExited)
            {
                // Time-based easing: cubic ease-out reaching ~88% at EstimatedDurationSec
                float elapsed = (float)(now - _processStartTime);
                float t       = Mathf.Clamp01(elapsed / EstimatedDurationSec);
                float timePct = (1f - Mathf.Pow(1f - t, 3f)) * 0.88f;

                // Display = whichever is further (milestone jumps, time fills gaps)
                float target = Mathf.Max(_milestoneProgress, timePct);

                // Smooth interpolation toward target (snappy catch-up, gentle drift)
                _displayProgress = Mathf.Lerp(_displayProgress, target, 1f - Mathf.Pow(0.05f, dt));

                Repaint();
            }
            else
            {
                _displayProgress = 1f;
                _blenderProcess.Dispose();
                _blenderProcess = null;
                AssetDatabase.Refresh();
                SetStatus("Blender finished. AssetDatabase refreshed.", MessageType.Info);
                Repaint();
            }
        }

        // ─── GUI ───────────────────────────────────────────────────────────────

        void OnGUI()
        {
            ShuganToolUI.DrawHeader("Blender Bridge");
            ShuganToolUI.DrawSocialLinks(WikiUrl);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawScriptSection();
            Separator();
            DrawBlenderPathSection();
            Separator();
            DrawLaunchSettings();
            Separator();
            DrawRunScriptSection();
            EditorGUILayout.EndScrollView();

            if (_blenderProcess != null || _displayProgress > 0f && _displayProgress < 1f)
                DrawProgressBar();

            if (!string.IsNullOrEmpty(_statusMsg))
                EditorGUILayout.HelpBox(_statusMsg, _statusType);

            ShuganToolUI.DrawCredits("Blender Bridge", ToolVersion);
        }

        void DrawBlenderPathSection()
        {
            GUILayout.Label("Blender Executable", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _blenderPath = EditorGUILayout.TextField(_blenderPath);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetString(PrefBlenderPath, _blenderPath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
                BrowseForBlender();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Auto-detect (Steam)"))
                TryAutoDetect(silent: false);
            bool valid = File.Exists(_blenderPath);
            GUI.color = valid ? Color.green : (_blenderPath.Length > 0 ? Color.red : Color.gray);
            GUILayout.Label(valid ? "✓ Found" : (_blenderPath.Length > 0 ? "✗ Not found" : "—"), GUILayout.Width(80));
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();
        }

        void DrawLaunchSettings()
        {
            GUILayout.Label("Launch Settings", EditorStyles.boldLabel);

            _factoryStartup = EditorGUILayout.Toggle(
                new GUIContent("Factory Startup", "Launch without user addons or preferences — clean Blender"),
                _factoryStartup);

            EditorGUI.BeginChangeCheck();
            _headless = EditorGUILayout.Toggle(
                new GUIContent("Headless", "Run in background — no Blender window, silent execution"),
                _headless);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetBool(PrefHeadless, _headless);

            EditorGUI.BeginDisabledGroup(_headless);
            _stepDelay = EditorGUILayout.Slider(
                new GUIContent("Step Delay (s)", "Pause between operations so you can observe (windowed only)"),
                _stepDelay, 0f, 5f);
            EditorGUI.EndDisabledGroup();
        }

        void DrawScriptSection()
        {
            GUILayout.Label("Script", EditorStyles.boldLabel);

            // ObjectField — drag a .py from the project (imported as DefaultAsset)
            EditorGUI.BeginChangeCheck();
            _scriptAsset = EditorGUILayout.ObjectField(
                new GUIContent("Script .py",
                    "Blender Python script to run on the imported FBX.\n" +
                    "Drag a .py file from your project, or use Browse to pick one from disk."),
                _scriptAsset, typeof(UnityEngine.Object), false);
            if (EditorGUI.EndChangeCheck())
            {
                if (_scriptAsset != null)
                {
                    string rel = AssetDatabase.GetAssetPath(_scriptAsset);
                    if (!string.IsNullOrEmpty(rel) && rel.EndsWith(".py", System.StringComparison.OrdinalIgnoreCase))
                        _scriptAbsPath = ToAbsPath(rel);
                    else
                        _scriptAbsPath = "";
                }
                else
                {
                    _scriptAbsPath = "";
                }
                EditorPrefs.SetString(PrefScriptPath, _scriptAbsPath);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Browse…", GUILayout.Width(80)))
                BrowseForScript();
            if (!string.IsNullOrEmpty(_scriptAbsPath) && GUILayout.Button("×", GUILayout.Width(22)))
            {
                _scriptAsset   = null;
                _scriptAbsPath = "";
                EditorPrefs.SetString(PrefScriptPath, "");
            }

            bool scriptValid = !string.IsNullOrEmpty(_scriptAbsPath) && File.Exists(_scriptAbsPath);
            GUI.color = scriptValid ? Color.green : (string.IsNullOrEmpty(_scriptAbsPath) ? Color.gray : Color.red);
            GUILayout.Label(
                scriptValid ? "✓ Found"
                : (string.IsNullOrEmpty(_scriptAbsPath) ? "— no script selected —" : "✗ Not found"),
                GUILayout.Width(180));
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_scriptAbsPath) && _scriptAsset == null)
            {
                // External script outside the Unity project
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField(new GUIContent("Path", "External script path (outside the Unity project)"),
                    _scriptAbsPath);
                EditorGUI.EndDisabledGroup();
            }
        }

        void BrowseForScript()
        {
            string start = !string.IsNullOrEmpty(_scriptAbsPath) && File.Exists(_scriptAbsPath)
                ? Path.GetDirectoryName(_scriptAbsPath)
                : Application.dataPath;
            string picked = EditorUtility.OpenFilePanel("Select Blender Python Script", start, "py");
            if (string.IsNullOrEmpty(picked)) return;
            _scriptAbsPath = picked.Replace("/", "\\");
            EditorPrefs.SetString(PrefScriptPath, _scriptAbsPath);

            // If the picked path is inside the project, also bind to the ObjectField
            string rel = ToProjectRelative(_scriptAbsPath);
            _scriptAsset = (rel != _scriptAbsPath && !string.IsNullOrEmpty(rel))
                ? AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(rel)
                : null;
            Repaint();
        }

        void DrawRunScriptSection()
        {
            GUILayout.Label("Run Script on FBX", EditorStyles.boldLabel);

            bool scriptValid = !string.IsNullOrEmpty(_scriptAbsPath) && File.Exists(_scriptAbsPath);
            if (!scriptValid)
            {
                EditorGUILayout.HelpBox(
                    "Select a Blender Python script (.py) at the top of this window before running.",
                    MessageType.Info);
                return;
            }

            // ── Source FBX ──────────────────────────────────────────────────
            EditorGUI.BeginChangeCheck();
            _sourceFbx = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Source FBX", "FBX file to import into Blender"),
                _sourceFbx, typeof(GameObject), false);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(PrefSourceFbx, _sourceFbx != null ? AssetDatabase.GetAssetPath(_sourceFbx) : "");
                RefreshMeshNames();
                _selectedMeshIndex = 0;
                EditorPrefs.SetInt(PrefMeshIndex, 0);
            }

            bool hasFbx = _sourceFbx != null && IsValidFbx(_sourceFbx);
            if (_sourceFbx != null && !hasFbx)
                EditorGUILayout.HelpBox("Selected asset is not an FBX file.", MessageType.Warning);

            // ── Target mesh object ──────────────────────────────────────────
            EditorGUI.BeginDisabledGroup(!hasFbx);
            EditorGUI.BeginChangeCheck();
            if (_meshNames.Length > 0)
            {
                _selectedMeshIndex = EditorGUILayout.Popup(
                    new GUIContent("Target Object", "Mesh to run AutoRig Feet on — must have an Armature modifier"),
                    Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1),
                    _meshNames);
            }
            else
            {
                EditorGUILayout.LabelField("Target Object", hasFbx ? "No meshes found in FBX" : "— select an FBX first —");
            }
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetInt(PrefMeshIndex, _selectedMeshIndex);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(6);
            GUILayout.Label("Export", EditorStyles.boldLabel);

            // ── Export mode ─────────────────────────────────────────────────
            EditorGUI.BeginChangeCheck();
            _exportMode = (ExportMode)EditorGUILayout.EnumPopup(
                new GUIContent("Mode",
                    "Replace: overwrite the original source FBX (shows warning first)\n" +
                    "Add New: save as a separate FBX alongside the original"),
                _exportMode);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetInt(PrefExportMode, (int)_exportMode);

            if (_exportMode == ExportMode.AddNew)
            {
                // Folder
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("Folder", "Empty = same folder as source FBX"), GUILayout.Width(46));
                EditorGUI.BeginDisabledGroup(true);
                string folderDisplay = string.IsNullOrEmpty(_addNewFolder)
                    ? (hasFbx ? ToProjectRelative(SourceFbxAbsDir()) + "  (source folder)" : "— select FBX first —")
                    : ToProjectRelative(_addNewFolder);
                EditorGUILayout.TextField(folderDisplay);
                EditorGUI.EndDisabledGroup();
                if (GUILayout.Button("Browse", GUILayout.Width(60)))
                    BrowseAddNewFolder();
                if (!string.IsNullOrEmpty(_addNewFolder) && GUILayout.Button("×", GUILayout.Width(22)))
                {
                    _addNewFolder = "";
                    EditorPrefs.SetString(PrefAddNewFolder, "");
                }
                EditorGUILayout.EndHorizontal();

                // Name
                EditorGUI.BeginChangeCheck();
                _addNewName = EditorGUILayout.TextField(
                    new GUIContent("Name", "FBX filename without extension. Empty = use source name (auto-suffixed)"),
                    _addNewName);
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetString(PrefAddNewName, _addNewName);

                // Resolved output preview
                EditorGUI.BeginDisabledGroup(true);
                string preview = hasFbx ? Path.GetFileName(GetExportPath()) : "—";
                EditorGUILayout.TextField(new GUIContent("Output", "Actual filename that will be written"), preview);
                EditorGUI.EndDisabledGroup();
            }

            EditorGUILayout.Space(8);

            bool blenderOk = File.Exists(_blenderPath);
            bool ready     = hasFbx && _meshNames.Length > 0 && blenderOk;
            bool busy      = _blenderProcess != null;

            EditorGUI.BeginDisabledGroup(!ready || busy);
            if (GUILayout.Button(busy ? "Blender Running…" : "Run Script", GUILayout.Height(32)))
                RunSelectedScript();
            EditorGUI.EndDisabledGroup();

            if (!blenderOk)
                EditorGUILayout.HelpBox("Set a valid Blender path above.", MessageType.Warning);
        }

        // ─── Run Script logic ──────────────────────────────────────────────────

        void RunSelectedScript()
        {
            string sourceFbxAbs = ToAbsPath(AssetDatabase.GetAssetPath(_sourceFbx));
            string exportPath   = GetExportPath();
            string scriptPath   = _scriptAbsPath;
            string targetName   = _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)];

            if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
            {
                SetStatus("Select a Blender Python script (.py) at the top of the window first.",
                    MessageType.Warning);
                return;
            }

            if (_exportMode == ExportMode.Replace && File.Exists(exportPath))
            {
                bool ok = EditorUtility.DisplayDialog(
                    "Replace Source FBX?",
                    "This will overwrite:\n" + ToProjectRelative(exportPath) +
                    "\n\nThe original FBX will be replaced with the AutoRig result.",
                    "Replace", "Cancel");
                if (!ok) return;
            }

            _milestoneProgress = 0f;
            _displayProgress   = 0f;
            _lastMilestone     = "";
            lock (_outputLock) _outputQueue.Clear();

            _blenderProcess = LaunchBlenderWithScript(
                BuildAutoRigScript(sourceFbxAbs, targetName, exportPath, scriptPath));

            if (_blenderProcess != null)
            {
                _processStartTime = EditorApplication.timeSinceStartup;
                _lastUpdateTime   = _processStartTime;
                SetStatus("Blender running… Unity will refresh when it finishes.", MessageType.Info);
            }
        }

        void DrawProgressBar()
        {
            EditorGUILayout.Space(4);

            bool done = _blenderProcess == null && _displayProgress >= 1f;
            string label = done
                ? "Done"
                : _displayProgress < 0.01f
                    ? "Starting Blender…"
                    : string.IsNullOrEmpty(_lastMilestone)
                        ? $"{_displayProgress * 100f:0}%"
                        : $"{_displayProgress * 100f:0}%  —  {TruncateLabel(_lastMilestone, 55)}";

            Rect barRect = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(barRect, _displayProgress, label);

            EditorGUILayout.Space(2);
        }

        static string TruncateLabel(string s, int max)
            => s.Length <= max ? s : "…" + s.Substring(s.Length - (max - 1));

        string GetExportPath()
        {
            if (_sourceFbx == null) return "";

            string sourceDir  = SourceFbxAbsDir();
            string sourceName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(_sourceFbx));

            if (_exportMode == ExportMode.Replace)
                return Path.Combine(sourceDir, sourceName + ".fbx");

            // AddNew
            string folder = string.IsNullOrEmpty(_addNewFolder) ? sourceDir : _addNewFolder;
            string name   = string.IsNullOrEmpty(_addNewName)   ? sourceName : _addNewName.Trim();
            string path   = Path.Combine(folder, name + ".fbx");
            if (File.Exists(path))
            {
                int i = 1;
                while (File.Exists(Path.Combine(folder, name + "_" + i.ToString("D3") + ".fbx"))) i++;
                path = Path.Combine(folder, name + "_" + i.ToString("D3") + ".fbx");
            }
            return path;
        }

        string BuildAutoRigScript(string sourceFbxPath, string targetName, string exportPath, string autoRigScriptPath)
            => BuildAutoRigFeetScript(sourceFbxPath, targetName, exportPath, autoRigScriptPath, _headless, _stepDelay);

        public static string BuildAutoRigFeetScript(
            string sourceFbxPath, string targetName, string exportPath,
            string autoRigScriptPath, bool headless, float stepDelay)
        {
            string d        = stepDelay.ToString("F1", CultureInfo.InvariantCulture);
            string pySrc    = sourceFbxPath.Replace("\\", "/");
            string pyExport = exportPath.Replace("\\", "/");
            string pyScript = autoRigScriptPath.Replace("\\", "/");
            string delay    = headless ? "" : $"time.sleep({d})\n";

            return
$@"import bpy, sys, time, os, importlib.util

def load_script(path):
    spec = importlib.util.spec_from_file_location('blender_script', path)
    mod  = importlib.util.module_from_spec(spec)
    mod.__file__ = path
    spec.loader.exec_module(mod)
    if hasattr(mod, 'main'):
        mod.main()

# ── Clean default scene objects ────────────────────────────────────────────────
for _name in ['Cube', 'Light', 'Camera']:
    _obj = bpy.data.objects.get(_name)
    if _obj:
        bpy.data.objects.remove(_obj, do_unlink=True)
print('[BlenderBridge] Default scene objects removed.')

# ── Import FBX ────────────────────────────────────────────────────────────────
print('[BlenderBridge] Importing: {pySrc}')
bpy.ops.import_scene.fbx(filepath='{pySrc}')
{delay}
# ── Select target object ───────────────────────────────────────────────────────
target_name = '{targetName}'
obj = bpy.data.objects.get(target_name)
if obj is None:
    for o in bpy.data.objects:
        if target_name.lower() in o.name.lower() and o.type == 'MESH':
            obj = o
            break
if obj is None:
    print('[BlenderBridge] ERROR: object not found: ' + target_name)
    sys.exit(1)

bpy.ops.object.select_all(action='DESELECT')
bpy.context.view_layer.objects.active = obj
obj.select_set(True)
print('[BlenderBridge] Selected: ' + obj.name)
{delay}
# ── Run user script ────────────────────────────────────────────────────────────
_script_basename = os.path.basename('{pyScript}')
print('[BlenderBridge] Running script: ' + _script_basename)
load_script('{pyScript}')
print('[BlenderBridge] Script complete: ' + _script_basename)
{delay}
# ── Export as FBX  (preset: fbx_to_Unity__no_modifier) ────────────────────────
print('[BlenderBridge] Exporting to: {pyExport}')
bpy.ops.export_scene.fbx(
    filepath='{pyExport}',
    # objects
    use_selection=False,
    use_visible=False,
    use_active_collection=False,
    object_types={{'MESH', 'ARMATURE'}},
    # scale / transform
    global_scale=1.0,
    apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_UNITS',
    use_space_transform=True,
    bake_space_transform=False,
    # mesh
    use_mesh_modifiers=False,
    use_mesh_modifiers_render=True,
    mesh_smooth_type='OFF',
    colors_type='SRGB',
    prioritize_active_color=False,
    use_subsurf=False,
    use_mesh_edges=False,
    use_tspace=False,
    use_triangles=False,
    use_custom_props=False,
    # armature
    add_leaf_bones=False,
    primary_bone_axis='Y',
    secondary_bone_axis='X',
    use_armature_deform_only=False,
    armature_nodetype='NULL',
    # animation
    bake_anim=False,
    # paths / axes
    path_mode='AUTO',
    embed_textures=False,
    batch_mode='OFF',
    axis_forward='-Z',
    axis_up='Y',
)
print('[BlenderBridge] Export done.')
{delay}sys.exit(0)
";
        }

        // ─── FBX helpers ──────────────────────────────────────────────────────

        void RefreshMeshNames()
        {
            if (_sourceFbx == null || !IsValidFbx(_sourceFbx))
            {
                _meshNames = new string[0];
                return;
            }
            _meshNames = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(_sourceFbx))
                .OfType<Mesh>()
                .Select(m => m.name)
                .ToArray();
        }

        bool IsValidFbx(GameObject obj)
        {
            string p = AssetDatabase.GetAssetPath(obj);
            return !string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);
        }

        string SourceFbxAbsDir()
        {
            if (_sourceFbx == null) return Application.dataPath;
            return Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", Path.GetDirectoryName(AssetDatabase.GetAssetPath(_sourceFbx))));
        }

        string ToAbsPath(string assetPath)
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        string ToProjectRelative(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return "";
            string root = Directory.GetParent(Application.dataPath).FullName;
            return absPath.StartsWith(root) ? absPath.Substring(root.Length).TrimStart('\\', '/') : absPath;
        }

        // ─── Blender process ──────────────────────────────────────────────────

        Process LaunchBlenderWithScript(string pythonCode)
        {
            if (!File.Exists(_blenderPath))
            {
                SetStatus("Blender executable not found.", MessageType.Error);
                return null;
            }
            return LaunchBlenderProcess(
                _blenderPath, pythonCode, _headless, _factoryStartup,
                line => { lock (_outputLock) _outputQueue.Enqueue(line); });
        }

        public static Process LaunchBlenderProcess(
            string blenderPath, string pythonCode, bool headless, bool factoryStartup,
            Action<string> onOutputLine)
        {
            if (!File.Exists(blenderPath)) return null;

            string script = Path.Combine(Application.temporaryCachePath, "shugan_blender_bridge.py");
            File.WriteAllText(script, pythonCode, Encoding.UTF8);

            var args = new StringBuilder();
            if (factoryStartup) args.Append("--factory-startup ");
            if (headless)       args.Append("--background ");
            args.Append("--python \"").Append(script).Append('"');

            var psi = new ProcessStartInfo
            {
                FileName               = blenderPath,
                Arguments              = args.ToString(),
                UseShellExecute        = false,
                CreateNoWindow         = headless,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

            var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) onOutputLine?.Invoke(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { /* discard stderr */ };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return proc;
        }

        // ─── Steam auto-detect ─────────────────────────────────────────────────

        void TryAutoDetect(bool silent)
        {
            string found = FindBlenderViaSteam();
            if (found != null)
            {
                _blenderPath = found;
                EditorPrefs.SetString(PrefBlenderPath, _blenderPath);
                SetStatus("Auto-detected: " + _blenderPath, MessageType.Info);
                Repaint();
            }
            else if (!silent)
                SetStatus("Blender not found via Steam. Set the path manually.", MessageType.Warning);
        }

        string FindBlenderViaSteam()
        {
#if UNITY_EDITOR_WIN
            string[] regKeys = {
                @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
            };
            foreach (string key in regKeys)
            {
                string steam = Microsoft.Win32.Registry.GetValue(key, "SteamPath",   null) as string
                            ?? Microsoft.Win32.Registry.GetValue(key, "InstallPath", null) as string;
                if (steam == null) continue;
                string hit = ScanSteamRoot(steam);
                if (hit != null) return hit;
            }
            foreach (string steam in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
            {
                string hit = ScanSteamRoot(steam);
                if (hit != null) return hit;
            }
#endif
            return null;
        }

        string ScanSteamRoot(string steamRoot)
        {
            string exe = Path.Combine(steamRoot, "steamapps", "common", "Blender", "blender.exe");
            if (File.Exists(exe)) return exe;
            string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) return null;
            foreach (string line in File.ReadAllLines(vdf))
            {
                if (!line.Contains("\"path\"")) continue;
                string[] parts = line.Trim().Split('"');
                if (parts.Length < 4) continue;
                string lib = parts[3].Replace("\\\\", "\\");
                exe = Path.Combine(lib, "steamapps", "common", "Blender", "blender.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        // ─── Path browsers ─────────────────────────────────────────────────────

        void BrowseForBlender()
        {
            string picked = EditorUtility.OpenFilePanel("Select Blender Executable", "", "exe");
            if (string.IsNullOrEmpty(picked)) return;
            _blenderPath = picked.Replace("/", "\\");
            EditorPrefs.SetString(PrefBlenderPath, _blenderPath);
            Repaint();
        }

        void BrowseAddNewFolder()
        {
            string start = !string.IsNullOrEmpty(_addNewFolder) && Directory.Exists(_addNewFolder)
                ? _addNewFolder : (_sourceFbx != null ? SourceFbxAbsDir() : Application.dataPath);
            string picked = EditorUtility.OpenFolderPanel("Select Export Folder", start, "");
            if (string.IsNullOrEmpty(picked)) return;
            _addNewFolder = picked.Replace("/", "\\");
            EditorPrefs.SetString(PrefAddNewFolder, _addNewFolder);
            Repaint();
        }

        // ─── UI helpers ────────────────────────────────────────────────────────

        void Separator()
        {
            EditorGUILayout.Space(6);
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 1), new Color(0.5f, 0.5f, 0.5f, 0.3f));
            EditorGUILayout.Space(6);
        }

        void SetStatus(string msg, MessageType type) { _statusMsg = msg; _statusType = type; }
    }
}
