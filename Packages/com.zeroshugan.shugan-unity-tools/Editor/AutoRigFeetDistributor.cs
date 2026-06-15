using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    public class AutoRigFeetDistributor : EditorWindow
    {
        enum ExportMode { Duplicate, Replace }
        enum SwapMethod { Legacy, Experimental }
        enum State      { Idle, BlenderRunning, FBXSwapping, AddingPrefabs, Restoring, Done, Error }

        // ─── EditorPrefs ───────────────────────────────────────────────────────
        const string PrefFbxPath              = "ShuganTools_ARF_FbxPath";
        const string PrefMeshIndex            = "ShuganTools_ARF_MeshIndex";
        const string PrefExportMode           = "ShuganTools_ARF_ExportMode";
        const string PrefSuffix               = "ShuganTools_ARF_Suffix";
        const string PrefExportFolder         = "ShuganTools_ARF_ExportFolder";
        const string PrefAdvanced             = "ShuganTools_ARF_Advanced";
        const string PrefAutoRigScriptPath    = "ShuganTools_ARF_AutoRigScriptPath";
        const string PrefSwapMethod           = "ShuganTools_ARF_SwapMethod";
        const string PrefGarments             = "ShuganTools_ARF_Garments";
        const string PrefBackupEnabled        = "ShuganTools_ARF_BackupEnabled";

        // ─── Paid-content paths (installed via Shugan store bundle) ───────────
        const string DefaultAutoRigScriptPath = "Assets/! Shugan/!_Lab/Script/shugan_autorig_feet.py";

        // Store links for missing paid content
        const string StoreBoothUrl         = "https://shugan.booth.pm/";
        const string StoreGumroadUrl       = "https://gumroad.com/shugan";
        const string StoreBlenderMarketUrl = "https://blendermarket.com/creators/shugan";

        // ─── Default prefabs ───────────────────────────────────────────────────
        static readonly string[] DefaultPrefabPaths =
        {
            "Assets/! Shugan/!_Prefabs/FX Hand_Controller (HaC).prefab",
            "Assets/! Shugan/!_Prefabs/FX Gesture_Feet.prefab",
        };

        // ─── Feet rig bone markers (unique to AutoRig Feet output) ────────────
        static readonly string[] AutoRigFeetBoneKeywords = { "z_CB ", "Toes_a1" };

        // ─── Avatar / FBX ──────────────────────────────────────────────────────
        GameObject   _avatarObject;
        GameObject   _sourceFbxAsset;
        string[]     _meshNames         = new string[0];
        int          _selectedMeshIndex = 0;
        bool         _fbxAutoDetected;
        bool         _alreadyRigged;

        // ─── Garment meshes (toe-weight transfer targets) ─────────────────────
        // Extra meshes from the source FBX (socks / thigh-highs / shoes) that the body's toe + foot
        // weights are transferred onto in Blender. Stored as mesh names so they survive FBX reloads.
        // Default empty (0 slots) — the user adds slots with the "+" button.
        List<string> _garmentMeshNames = new List<string>();

        // ─── Rig backup (JSON) ────────────────────────────────────────────────
        bool _backupEnabled = true;   // capture a rig-only JSON backup before each run (Advanced)
        int  _restoreIndex;            // selected backup in the Restore dropdown

        // ─── Export ────────────────────────────────────────────────────────────
        ExportMode _exportMode   = ExportMode.Duplicate;
        SwapMethod _swapMethod   = SwapMethod.Legacy;
        string     _exportSuffix = "Rig_Feet";
        string     _exportFolder = "";

        // ─── Prefabs ───────────────────────────────────────────────────────────
        List<GameObject> _prefabsToAdd = new List<GameObject>();

        // ─── Progress ──────────────────────────────────────────────────────────
        const float EstimatedBlenderSec = 120f;
        Queue<string>   _outputQueue      = new Queue<string>();
        readonly object _outputLock       = new object();
        float           _blenderMilestone = 0f;
        float           _displayProgress  = 0f;
        double          _processStartTime = 0;
        double          _lastUpdateTime   = 0;
        string          _currentStepLabel = "";

        // ─── Runtime ───────────────────────────────────────────────────────────
        State      _state          = State.Idle;
        Process    _blenderProcess;
        string     _exportPath;
        string     _createdPrefabPath;
        GameObject _resultInstance;

        // ─── UI ────────────────────────────────────────────────────────────────
        Vector2 _scroll;
        string      _statusMsg  = "";
        MessageType _statusType = MessageType.None;
        bool        _advancedFoldout;

        // ─── Dependency cache (refreshed each OnGUI pass) ─────────────────────
        bool   _depBlender;
        bool   _depVRCFury;
        bool   _depAutoRigScript;
        string _autoRigScriptResolvedPath; // the path that resolved (default OR override)
        string _blenderFoundPath;          // path actually found; may differ from EditorPrefs if pref changed

        // ─── Menu ──────────────────────────────────────────────────────────────

        const string WikiUrl = "https://www.notion.so/shugan/AutoRig-Feet-Distributor";
        const string ToolVersion = "1.0";

        [MenuItem("Tools/Shugan/AutoRig Feet (Distributor)", false, 1900)]
        static void Open()
        {
            var win = GetWindow<AutoRigFeetDistributor>("AutoRig Feet (Distributor)");
            win.minSize = new Vector2(460, 420);
        }

        // ─── Lifecycle ─────────────────────────────────────────────────────────

        void OnEnable()
        {
            _exportMode    = (ExportMode)EditorPrefs.GetInt(PrefExportMode, (int)ExportMode.Duplicate);
            _swapMethod    = (SwapMethod)EditorPrefs.GetInt(PrefSwapMethod, (int)SwapMethod.Legacy);
            _exportSuffix  = EditorPrefs.GetString(PrefSuffix, "Rig_Feet");
            _exportFolder  = EditorPrefs.GetString(PrefExportFolder, "");
            _advancedFoldout = EditorPrefs.GetBool(PrefAdvanced, false);

            string fbxPath = EditorPrefs.GetString(PrefFbxPath, "");
            if (!string.IsNullOrEmpty(fbxPath))
            {
                _sourceFbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                if (_sourceFbxAsset != null) RefreshMeshNames();
            }
            _selectedMeshIndex = Mathf.Clamp(
                EditorPrefs.GetInt(PrefMeshIndex, 0), 0, Mathf.Max(0, _meshNames.Length - 1));

            string garments = EditorPrefs.GetString(PrefGarments, "");
            _garmentMeshNames = string.IsNullOrEmpty(garments)
                ? new List<string>()
                : garments.Split('|').Where(s => !string.IsNullOrEmpty(s)).ToList();

            _backupEnabled = EditorPrefs.GetBool(PrefBackupEnabled, true);

            if (_prefabsToAdd.Count == 0)
                foreach (string p in DefaultPrefabPaths)
                    _prefabsToAdd.Add(AssetDatabase.LoadAssetAtPath<GameObject>(p));

            // Auto-detect Blender if not already set
            if (!File.Exists(EditorPrefs.GetString(BlenderBridge.PrefBlenderPath, "")))
                TryAutoDetectBlender(silent: true);
        }

        void OnSelectionChange()
        {
            if (_state != State.Idle && _state != State.Done && _state != State.Error) return;
            var go = Selection.activeGameObject;
            if (go == null || !go.scene.IsValid()) return;
            if (go == _avatarObject) return;
            _avatarObject = go;
            OnAvatarChanged();
            Repaint();
        }

        void Update()
        {
            if (_state == State.Idle || _state == State.Done || _state == State.Error) return;

            double now = EditorApplication.timeSinceStartup;
            float  dt  = (float)(now - _lastUpdateTime);
            _lastUpdateTime = now;

            if (_state == State.BlenderRunning)
            {
                DrainOutputQueue();

                float elapsed    = (float)(now - _processStartTime);
                float t          = Mathf.Clamp01(elapsed / EstimatedBlenderSec);
                float timePct    = (1f - Mathf.Pow(1f - t, 3f)) * 0.88f;
                float blTarget   = Mathf.Max(_blenderMilestone, timePct);
                float overallMax = _exportMode == ExportMode.Duplicate ? 0.70f : 0.88f;
                _displayProgress = Mathf.Lerp(_displayProgress,
                    blTarget * overallMax, 1f - Mathf.Pow(0.05f, dt));

                if (_blenderProcess != null && _blenderProcess.HasExited)
                {
                    _blenderProcess.Dispose();
                    _blenderProcess = null;
                    AssetDatabase.Refresh();
                    _state = State.FBXSwapping;
                }
                Repaint();
            }

            if (_state == State.Restoring)
            {
                DrainOutputQueue();
                float elapsed = (float)(now - _processStartTime);
                float t       = Mathf.Clamp01(elapsed / EstimatedBlenderSec);
                _displayProgress = (1f - Mathf.Pow(1f - t, 3f)) * 0.95f;

                if (_blenderProcess != null && _blenderProcess.HasExited)
                {
                    _blenderProcess.Dispose();
                    _blenderProcess = null;
                    AssetDatabase.Refresh();
                    _displayProgress  = 1f;
                    _state            = State.Done;
                    _currentStepLabel = "Restore done!";
                    SetStatus("Restore complete — the FBX was reverted (feet rig removed).",
                        MessageType.Info);
                }
                Repaint();
            }

            if (_state == State.FBXSwapping)
            {
                try
                {
                    _currentStepLabel = "Swapping FBX into prefab…";
                    _displayProgress  = _exportMode == ExportMode.Duplicate ? 0.75f : 0.90f;
                    Repaint();
                    RunFBXSwap();
                    if (_state != State.Error) _state = State.AddingPrefabs;
                }
                catch (Exception ex)
                {
                    SetError("FBX Swap failed: " + ex.Message);
                }
                Repaint();
            }

            if (_state == State.AddingPrefabs)
            {
                try
                {
                    _currentStepLabel = "Adding prefabs…";
                    _displayProgress  = _exportMode == ExportMode.Duplicate ? 0.92f : 0.95f;
                    Repaint();
                    RunAddPrefabs();
                    _displayProgress  = 1f;
                    _state            = State.Done;
                    _currentStepLabel = "Done!";
                    SetStatus("AutoRig Feet complete! New prefab added to scene.", MessageType.Info);
                }
                catch (Exception ex)
                {
                    SetError("Add Prefabs failed: " + ex.Message);
                }
                Repaint();
            }
        }

        // ─── GUI ───────────────────────────────────────────────────────────────

        void OnGUI()
        {
            // Refresh dependency state every frame (cheap checks)
            string storedBlender = EditorPrefs.GetString(BlenderBridge.PrefBlenderPath, "");
            _depBlender = File.Exists(storedBlender);
            _depVRCFury = HasVRCFury();
            _autoRigScriptResolvedPath = ResolveAutoRigScriptPath();
            _depAutoRigScript = !string.IsNullOrEmpty(_autoRigScriptResolvedPath);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            ShuganToolUI.DrawHeader("AutoRig Feet  —  Distributor");
            ShuganToolUI.DrawSocialLinks(WikiUrl);
            Separator();
            DrawDependencyStatus();
            Separator();
            DrawMainSection();
            Separator();
            DrawAdvancedSection();

            EditorGUILayout.EndScrollView();

            DrawProgressBarIfActive();

            bool busy  = _state != State.Idle && _state != State.Done && _state != State.Error;
            bool ready = IsReady();

            EditorGUILayout.Space(4);
            EditorGUI.BeginDisabledGroup(!ready || busy);
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = ready && !busy ? new Color(0.4f, 0.8f, 0.4f) : Color.white;
            if (GUILayout.Button(busy ? GetBusyLabel() : "▶  AutoRig Feet", GUILayout.Height(34)))
                Execute();
            GUI.backgroundColor = prev;
            EditorGUI.EndDisabledGroup();

            DrawReadinessHints(ready);

            if (_alreadyRigged && _state == State.Idle)
            {
                Color c = GUI.color; GUI.color = Color.yellow;
                EditorGUILayout.HelpBox(
                    "⚠️  This avatar already has AutoRig Feet bones (z_CB / Toes_a1 found). " +
                    "Running again will add duplicate bones.",
                    MessageType.Warning);
                GUI.color = c;
            }

            if (!string.IsNullOrEmpty(_statusMsg))
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.HelpBox(_statusMsg, _statusType);
            }

            if (_state == State.Done || _state == State.Error)
            {
                if (GUILayout.Button("Reset"))
                {
                    _state            = State.Idle;
                    _displayProgress  = 0f;
                    _currentStepLabel = "";
                    _statusMsg        = "";
                    Repaint();
                }
            }

            ShuganToolUI.DrawCredits("AutoRig Feet (Distributor)", ToolVersion);
        }

        // ─── Dependency status ─────────────────────────────────────────────────

        void DrawDependencyStatus()
        {
            GUILayout.Label("Requirements", EditorStyles.boldLabel);

            // Blender
            DrawDepRow("Blender 4.0+", _depBlender,
                required: true,
                notFoundExtra: () =>
                {
                    EditorGUILayout.BeginHorizontal();
                    Color c = GUI.color;

                    GUI.color = new Color(0.6f, 0.8f, 1f);
                    if (GUILayout.Button("Steam", EditorStyles.miniButton, GUILayout.Width(52)))
                        Application.OpenURL("https://store.steampowered.com/app/365670/Blender/");

                    GUI.color = new Color(1f, 0.6f, 0.2f);
                    if (GUILayout.Button("blender.org", EditorStyles.miniButton, GUILayout.Width(80)))
                        Application.OpenURL("https://www.blender.org/download/");

                    GUI.color = new Color(0.6f, 1f, 0.6f);
                    if (GUILayout.Button("Portable ↓", EditorStyles.miniButton, GUILayout.Width(72)))
                        Application.OpenURL("https://www.blender.org/download/lts/");

                    GUI.color = c;
                    GUILayout.Label("(set path in Advanced)", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                });

            // VRCFury
            DrawDepRow("VRCFury", _depVRCFury,
                required: false,
                notFoundExtra: () =>
                {
                    EditorGUILayout.BeginHorizontal();
                    Color c = GUI.color;
                    GUI.color = new Color(0.8f, 0.6f, 1f);
                    if (GUILayout.Button("Get VRCFury", EditorStyles.miniButton, GUILayout.Width(90)))
                        Application.OpenURL("https://vrcfury.com/");
                    GUI.color = c;
                    GUILayout.Label("(optional — needed for FX prefabs)", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                });

            // AutoRig Feet Script (paid bundle)
            DrawDepRow("AutoRig Feet Script (paid)", _depAutoRigScript,
                required: true,
                notFoundExtra: () =>
                {
                    EditorGUILayout.HelpBox(
                        "This Blender script is sold separately as part of the Shugan AutoRig Feet bundle. " +
                        "Get it from one of the stores below, then import the .unitypackage into your project.",
                        MessageType.Info);

                    EditorGUILayout.BeginHorizontal();
                    Color c = GUI.color;

                    GUI.color = Color.red;
                    if (GUILayout.Button("Get on Booth", EditorStyles.miniButton, GUILayout.Width(90)))
                        Application.OpenURL(StoreBoothUrl);

                    GUI.color = Color.magenta;
                    if (GUILayout.Button("Get on Gumroad", EditorStyles.miniButton, GUILayout.Width(110)))
                        Application.OpenURL(StoreGumroadUrl);

                    GUI.color = new Color(1f, 0.5f, 0f);
                    if (GUILayout.Button("Get on Blender Market", EditorStyles.miniButton, GUILayout.Width(140)))
                        Application.OpenURL(StoreBlenderMarketUrl);

                    GUI.color = c;
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.LabelField(
                        $"Expected: {DefaultAutoRigScriptPath}", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(
                        "(override path in Advanced Settings → Paid Blender Scripts)",
                        EditorStyles.miniLabel);
                });
        }

        void DrawDepRow(string label, bool found, bool required, Action notFoundExtra)
        {
            EditorGUILayout.BeginHorizontal();
            Color c = GUI.color;
            GUI.color = found ? Color.green : (required ? Color.red : Color.yellow);
            GUILayout.Label(found ? $"✓  {label}" : $"✗  {label}",
                EditorStyles.miniLabel, GUILayout.Width(200));
            GUI.color = c;
            EditorGUILayout.EndHorizontal();
            if (!found) notFoundExtra?.Invoke();
        }

        // ─── Main section ──────────────────────────────────────────────────────

        void DrawMainSection()
        {
            GUILayout.Label("Setup", EditorStyles.boldLabel);

            // Target Avatar
            EditorGUI.BeginChangeCheck();
            _avatarObject = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Target Avatar",
                    "Root scene object of the avatar. Clicking any scene object auto-fills this."),
                _avatarObject, typeof(GameObject), allowSceneObjects: true);
            if (EditorGUI.EndChangeCheck())
                OnAvatarChanged();

            // Root check
            if (_avatarObject != null && !IsRootObject(_avatarObject))
            {
                Color c = GUI.color; GUI.color = Color.red;
                EditorGUILayout.HelpBox(
                    "⚠️  The selected object is not a root — select the top-level avatar GameObject.",
                    MessageType.Error);
                GUI.color = c;
            }

            // Target Mesh
            bool hasFbx = _sourceFbxAsset != null && IsValidFbx(_sourceFbxAsset);
            EditorGUI.BeginDisabledGroup(!hasFbx);
            EditorGUI.BeginChangeCheck();
            if (_meshNames.Length > 0)
                _selectedMeshIndex = EditorGUILayout.Popup(
                    new GUIContent("Target Mesh",
                        "Body mesh to rig. Auto-selected by counting how many humanoid bones are weighted to each mesh."),
                    Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1),
                    _meshNames);
            else
                EditorGUILayout.LabelField("Target Mesh",
                    _avatarObject == null ? "— select an avatar first —"
                    : hasFbx ? "No meshes in FBX"
                    : "— detecting FBX…");
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetInt(PrefMeshIndex, _selectedMeshIndex);
            EditorGUI.EndDisabledGroup();

            // Export Mode
            EditorGUI.BeginChangeCheck();
            _exportMode = (ExportMode)EditorGUILayout.EnumPopup(
                new GUIContent("Export Mode",
                    "Duplicate: new FBX + new prefab alongside the original.\n" +
                    "Replace: overwrites original FBX in place (auto-backup created)."),
                _exportMode);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetInt(PrefExportMode, (int)_exportMode);

            // FBX badge
            if (_avatarObject != null && IsRootObject(_avatarObject))
            {
                EditorGUILayout.Space(2);
                if (_sourceFbxAsset != null && _fbxAutoDetected)
                {
                    Color c = GUI.color; GUI.color = Color.green;
                    EditorGUILayout.HelpBox(
                        $"✓ FBX auto-detected: {AssetDatabase.GetAssetPath(_sourceFbxAsset)}",
                        MessageType.None);
                    GUI.color = c;
                }
                else if (_sourceFbxAsset == null)
                {
                    Color c = GUI.color; GUI.color = Color.yellow;
                    EditorGUILayout.HelpBox(
                        "FBX not detected — set it manually in Advanced Settings.",
                        MessageType.None);
                    GUI.color = c;
                }
            }

            DrawGarmentSection();
            DrawRestoreSection();
        }

        // ─── Restore original rig (from JSON backup) ───────────────────────────

        void DrawRestoreSection()
        {
            bool hasFbx = _sourceFbxAsset != null && IsValidFbx(_sourceFbxAsset);
            if (!hasFbx) return;

            string backupDir = Path.Combine(SourceFbxAbsDir(), "_Backups");
            string fbxName   = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(_sourceFbxAsset));
            string[] jsons = Directory.Exists(backupDir)
                ? Directory.GetFiles(backupDir, fbxName + "_rigbackup_*.json")
                    .OrderByDescending(f => f).ToArray()
                : new string[0];
            if (jsons.Length == 0) return;  // nothing to restore

            EditorGUILayout.Space(6);
            GUILayout.Label("Restore Original Rig", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Re-import the FBX, remove the feet rig using a backup, and overwrite the FBX "
                + "(keeps post-rig mesh edits).", EditorStyles.miniLabel);

            _restoreIndex = Mathf.Clamp(_restoreIndex, 0, jsons.Length - 1);
            string[] labels = jsons.Select(Path.GetFileName).ToArray();

            EditorGUILayout.BeginHorizontal();
            _restoreIndex = EditorGUILayout.Popup(_restoreIndex, labels);
            bool busy = _state != State.Idle && _state != State.Done && _state != State.Error;
            EditorGUI.BeginDisabledGroup(busy || !_depAutoRigScript || !_depBlender);
            if (GUILayout.Button("Restore", GUILayout.Width(90)))
                RunRestore(jsons[_restoreIndex]);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        void RunRestore(string jsonPath)
        {
            string blenderPath = EditorPrefs.GetString(BlenderBridge.PrefBlenderPath, "");
            if (!File.Exists(blenderPath))
            {
                SetStatus("Blender not found — set its path in Advanced Settings.", MessageType.Error);
                return;
            }
            string scriptPath = ResolveAutoRigScriptPath();
            if (string.IsNullOrEmpty(scriptPath))
            {
                SetStatus("AutoRig_Feet.py not found — needed for the restore logic.", MessageType.Error);
                return;
            }
            if (_meshNames.Length == 0)
            {
                SetStatus("No meshes in the FBX to restore.", MessageType.Error);
                return;
            }

            string sourceFbxAbs = ToAbsPath(AssetDatabase.GetAssetPath(_sourceFbxAsset));
            string targetMesh   = _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)];

            bool ok = EditorUtility.DisplayDialog(
                "Restore original rig?",
                "This imports the current FBX into Blender, removes the AutoRig Feet rig using:\n" +
                Path.GetFileName(jsonPath) +
                "\n\nand OVERWRITES the FBX:\n" + ToProjectRelative(sourceFbxAbs) +
                "\n\nMesh edits made after rigging are kept; only the feet rig is removed.",
                "Restore", "Cancel");
            if (!ok) return;

            _displayProgress  = 0f;
            _currentStepLabel = "Restoring rig in Blender…";
            lock (_outputLock) _outputQueue.Clear();

            string pythonCode = BlenderBridge.BuildRestoreFeetScript(
                sourceFbxAbs, targetMesh, sourceFbxAbs, scriptPath, jsonPath,
                headless: true, stepDelay: 0f);

            _blenderProcess = BlenderBridge.LaunchBlenderProcess(
                blenderPath, pythonCode, headless: true, factoryStartup: true,
                onOutputLine: line => { lock (_outputLock) _outputQueue.Enqueue(line); });

            if (_blenderProcess == null)
            {
                SetError("Failed to launch Blender for restore.");
                return;
            }
            _processStartTime = EditorApplication.timeSinceStartup;
            _lastUpdateTime   = _processStartTime;
            _state            = State.Restoring;
            SetStatus("Restoring rig in Blender… Unity will refresh when it finishes.", MessageType.Info);
        }

        // ─── Garment meshes (toe-weight transfer targets) ──────────────────────

        void DrawGarmentSection()
        {
            EditorGUILayout.Space(6);
            GUILayout.Label("Transfer Toe Weights To (optional)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Socks / thigh-highs / shoes from the same FBX that should follow the new toe bones.",
                EditorStyles.miniLabel);

            bool hasFbx = _sourceFbxAsset != null && IsValidFbx(_sourceFbxAsset);
            string bodyMesh = _meshNames.Length > 0
                ? _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)] : null;

            // Garment choices = all FBX meshes except the body mesh.
            string[] choices = hasFbx
                ? _meshNames.Where(m => !string.Equals(m, bodyMesh, StringComparison.OrdinalIgnoreCase)).ToArray()
                : new string[0];

            EditorGUI.BeginDisabledGroup(!hasFbx);

            for (int i = 0; i < _garmentMeshNames.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();

                int cur = Array.IndexOf(choices, _garmentMeshNames[i]);
                EditorGUI.BeginChangeCheck();
                int picked = EditorGUILayout.Popup(
                    cur < 0 ? 0 : cur,
                    choices.Length > 0 ? choices : new[] { "— no other meshes —" });
                if (EditorGUI.EndChangeCheck() && choices.Length > 0)
                {
                    _garmentMeshNames[i] = choices[Mathf.Clamp(picked, 0, choices.Length - 1)];
                    SaveGarments();
                }

                // Flag a stale name (mesh no longer in this FBX) or an accidental duplicate.
                if (!string.IsNullOrEmpty(_garmentMeshNames[i]) && cur < 0)
                {
                    Color c = GUI.color; GUI.color = Color.yellow;
                    GUILayout.Label("not in FBX", EditorStyles.miniLabel, GUILayout.Width(70));
                    GUI.color = c;
                }
                else if (_garmentMeshNames.Take(i).Contains(_garmentMeshNames[i]))
                {
                    Color c = GUI.color; GUI.color = Color.yellow;
                    GUILayout.Label("duplicate", EditorStyles.miniLabel, GUILayout.Width(70));
                    GUI.color = c;
                }

                if (GUILayout.Button("×", GUILayout.Width(22)))
                {
                    _garmentMeshNames.RemoveAt(i);
                    SaveGarments();
                    i--;
                    EditorGUILayout.EndHorizontal();
                    continue;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("+ Add Garment Mesh", GUILayout.Height(22)))
            {
                // Default the new slot to the first not-yet-chosen garment mesh, else the first.
                string next = choices.FirstOrDefault(m => !_garmentMeshNames.Contains(m))
                              ?? (choices.Length > 0 ? choices[0] : "");
                _garmentMeshNames.Add(next);
                SaveGarments();
            }

            EditorGUI.EndDisabledGroup();

            if (!hasFbx && _garmentMeshNames.Count > 0)
                EditorGUILayout.HelpBox("Select an avatar / FBX first to pick garment meshes.",
                    MessageType.Info);
        }

        void SaveGarments()
            => EditorPrefs.SetString(PrefGarments, string.Join("|", _garmentMeshNames));

        // ─── Advanced section ──────────────────────────────────────────────────

        void DrawAdvancedSection()
        {
            EditorGUI.BeginChangeCheck();
            _advancedFoldout = EditorGUILayout.Foldout(_advancedFoldout, "Advanced Settings", true);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetBool(PrefAdvanced, _advancedFoldout);

            if (!_advancedFoldout) return;

            EditorGUI.indentLevel++;

            // ── Blender path ─────────────────────────────────────────────────
            GUILayout.Label("Blender", EditorStyles.boldLabel);

            string blenderPath = EditorPrefs.GetString(BlenderBridge.PrefBlenderPath, "");
            bool   blenderOk   = File.Exists(blenderPath);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            string newPath = EditorGUILayout.TextField(
                new GUIContent("blender.exe", "Full path to the Blender executable."),
                blenderPath);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetString(BlenderBridge.PrefBlenderPath, newPath);

            Color co = GUI.color;
            GUI.color = blenderOk ? Color.green : (blenderPath.Length > 0 ? Color.red : Color.gray);
            GUILayout.Label(blenderOk ? "✓" : (blenderPath.Length > 0 ? "✗" : "—"),
                GUILayout.Width(20));
            GUI.color = co;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Auto-detect", GUILayout.Width(90)))
                TryAutoDetectBlender(silent: false);
            if (GUILayout.Button("Browse…", GUILayout.Width(70)))
                BrowseForBlender();
            EditorGUILayout.EndHorizontal();

            if (blenderOk)
            {
                string versionHint = GuessBlenderVersion(blenderPath);
                if (!string.IsNullOrEmpty(versionHint))
                {
                    Color c = GUI.color;
                    bool goodVer = versionHint.StartsWith("4.") || versionHint.StartsWith("5.");
                    GUI.color = goodVer ? Color.green : Color.yellow;
                    EditorGUILayout.LabelField("Version (path hint)", versionHint, EditorStyles.miniLabel);
                    GUI.color = c;
                    if (!goodVer)
                        EditorGUILayout.HelpBox("Blender 4.0 or 5.0+ is required.", MessageType.Warning);
                }
            }

            Separator();

            // ── Paid Blender Scripts (override path) ─────────────────────────
            GUILayout.Label("Paid Blender Scripts", EditorStyles.boldLabel);

            string overridePath = EditorPrefs.GetString(PrefAutoRigScriptPath, "");
            string overrideRel  = !string.IsNullOrEmpty(overridePath)
                ? ToProjectRelative(overridePath) : "";
            UnityEngine.Object overrideAsset =
                !string.IsNullOrEmpty(overrideRel) && File.Exists(overridePath)
                    ? AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(overrideRel)
                    : null;

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            var pickedAsset = EditorGUILayout.ObjectField(
                new GUIContent("AutoRig Feet .py",
                    "Custom path to AutoRig_Feet.py.\nEmpty = use default: " + DefaultAutoRigScriptPath),
                overrideAsset, typeof(UnityEngine.Object), false);
            if (EditorGUI.EndChangeCheck())
            {
                if (pickedAsset != null)
                {
                    string rel = AssetDatabase.GetAssetPath(pickedAsset);
                    if (!string.IsNullOrEmpty(rel) && rel.EndsWith(".py",
                            StringComparison.OrdinalIgnoreCase))
                        EditorPrefs.SetString(PrefAutoRigScriptPath, ToAbsPath(rel));
                    else
                        EditorPrefs.SetString(PrefAutoRigScriptPath, "");
                }
                else
                {
                    EditorPrefs.SetString(PrefAutoRigScriptPath, "");
                }
            }
            if (GUILayout.Button("Browse…", GUILayout.Width(70)))
                BrowseForAutoRigScript();
            if (!string.IsNullOrEmpty(overridePath) && GUILayout.Button("×", GUILayout.Width(22)))
                EditorPrefs.SetString(PrefAutoRigScriptPath, "");
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(overridePath) && overrideAsset == null)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField(new GUIContent("Path",
                    "External script path (outside the Unity project)"), overridePath);
                EditorGUI.EndDisabledGroup();
            }

            // Status line
            Color cs = GUI.color;
            GUI.color = _depAutoRigScript ? Color.green : Color.red;
            EditorGUILayout.LabelField(
                _depAutoRigScript
                    ? $"✓ Using: {ToProjectRelative(_autoRigScriptResolvedPath)}"
                    : "✗ Not found — install paid bundle or set a custom path",
                EditorStyles.miniLabel);
            GUI.color = cs;

            Separator();

            // ── Backups ──────────────────────────────────────────────────────
            GUILayout.Label("Backups", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _backupEnabled = EditorGUILayout.ToggleLeft(
                new GUIContent("Save rig backup (JSON)",
                    "Before rigging, capture a rig-only JSON backup (bones/groups/weights the rig "
                    + "changes) to the FBX's _Backups folder. Lets you later 'Restore original rig' "
                    + "while keeping mesh edits made after rigging. Separate from the full FBX backup."),
                _backupEnabled);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetBool(PrefBackupEnabled, _backupEnabled);

            Separator();

            // ── FBX Swap method ──────────────────────────────────────────────
            GUILayout.Label("FBX Swap", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _swapMethod = (SwapMethod)EditorGUILayout.EnumPopup(
                new GUIContent("Method",
                    "Legacy: rebuild the avatar on the new FBX (current behaviour).\n" +
                    "Experimental: duplicate the avatar and give it a private copy of the FBX " +
                    "— the original is never touched. Always produces a duplicate."),
                _swapMethod);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetInt(PrefSwapMethod, (int)_swapMethod);
            if (_swapMethod == SwapMethod.Experimental)
                EditorGUILayout.HelpBox(
                    "Experimental: always duplicates (Export Mode is treated as Duplicate). " +
                    "Writes a debug log to Assets/! Shugan/!_Lab/Script/FBXSwapper_Logs/.",
                    MessageType.None);

            Separator();

            // ── Source FBX ───────────────────────────────────────────────────
            GUILayout.Label("Source", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _sourceFbxAsset = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent(_fbxAutoDetected ? "Source FBX (auto)" : "Source FBX",
                    "FBX file the avatar's body mesh comes from."),
                _sourceFbxAsset, typeof(GameObject), allowSceneObjects: false);
            if (EditorGUI.EndChangeCheck())
            {
                _fbxAutoDetected = false;
                EditorPrefs.SetString(PrefFbxPath,
                    _sourceFbxAsset != null ? AssetDatabase.GetAssetPath(_sourceFbxAsset) : "");
                RefreshMeshNames();
                _selectedMeshIndex = 0;
            }
            GUI.enabled = _avatarObject != null;
            if (GUILayout.Button(new GUIContent("↺", "Re-detect FBX from avatar"), GUILayout.Width(26)))
                AutoDetectFbx();
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (_sourceFbxAsset != null && !IsValidFbx(_sourceFbxAsset))
                EditorGUILayout.HelpBox("Selected asset is not an FBX file.", MessageType.Warning);

            Separator();

            // ── Export options ───────────────────────────────────────────────
            GUILayout.Label("Export", EditorStyles.boldLabel);

            if (_exportMode == ExportMode.Duplicate)
            {
                EditorGUI.BeginChangeCheck();
                _exportSuffix = EditorGUILayout.TextField(
                    new GUIContent("Suffix", "Appended to the source FBX filename."),
                    _exportSuffix);
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetString(PrefSuffix, _exportSuffix);

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("Folder",
                    "Where to save the new FBX. Empty = same folder as source."), GUILayout.Width(50));
                EditorGUI.BeginDisabledGroup(true);
                bool hasFbx = _sourceFbxAsset != null && IsValidFbx(_sourceFbxAsset);
                EditorGUILayout.TextField(string.IsNullOrEmpty(_exportFolder)
                    ? (hasFbx ? ToProjectRelative(SourceFbxAbsDir()) + "  (source)" : "—")
                    : ToProjectRelative(_exportFolder));
                EditorGUI.EndDisabledGroup();
                if (GUILayout.Button("Browse", GUILayout.Width(56))) BrowseExportFolder();
                if (!string.IsNullOrEmpty(_exportFolder) && GUILayout.Button("×", GUILayout.Width(22)))
                {
                    _exportFolder = "";
                    EditorPrefs.SetString(PrefExportFolder, "");
                }
                EditorGUILayout.EndHorizontal();

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField(
                    new GUIContent("Output FBX"),
                    _sourceFbxAsset != null && IsValidFbx(_sourceFbxAsset)
                        ? Path.GetFileName(ComputeExportPath()) : "—");
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Original FBX will be overwritten. A timestamped backup is saved to a _Backups subfolder first.",
                    MessageType.Info);
            }

            Separator();

            // ── Prefabs ──────────────────────────────────────────────────────
            GUILayout.Label("Prefabs to Add as Children", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Instantiated under the avatar root after rigging.",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(2);

            for (int i = 0; i < _prefabsToAdd.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _prefabsToAdd[i] = (GameObject)EditorGUILayout.ObjectField(
                    _prefabsToAdd[i], typeof(GameObject), false);
                if (_prefabsToAdd[i] != null && _resultInstance != null &&
                    ChildNameExists(_resultInstance, _prefabsToAdd[i].name))
                {
                    Color c = GUI.color; GUI.color = Color.yellow;
                    GUILayout.Label("exists", EditorStyles.miniLabel, GUILayout.Width(36));
                    GUI.color = c;
                }
                if (GUILayout.Button("×", GUILayout.Width(22))) { _prefabsToAdd.RemoveAt(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ Add Prefab Slot", GUILayout.Height(22)))
                _prefabsToAdd.Add(null);

            EditorGUI.indentLevel--;
        }

        // ─── Progress bar ──────────────────────────────────────────────────────

        void DrawProgressBarIfActive()
        {
            bool show = _state == State.BlenderRunning || _state == State.FBXSwapping ||
                        _state == State.AddingPrefabs  || _state == State.Restoring ||
                        (_displayProgress > 0f && _displayProgress < 1.01f &&
                         (_state == State.Done || _state == State.Error));
            if (!show) return;
            EditorGUILayout.Space(4);
            string label = _displayProgress >= 1f ? "✓ Done"
                : string.IsNullOrEmpty(_currentStepLabel) ? $"{_displayProgress * 100f:0}%"
                : $"{_displayProgress * 100f:0}%  —  {TruncateLabel(_currentStepLabel, 50)}";
            Rect r = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(r, Mathf.Clamp01(_displayProgress), label);
            EditorGUILayout.Space(2);
        }

        // ─── Execute ───────────────────────────────────────────────────────────

        void Execute()
        {
            string blenderPath = EditorPrefs.GetString(BlenderBridge.PrefBlenderPath, "");
            if (!File.Exists(blenderPath))
            {
                SetStatus("Blender not found — set its path in Advanced Settings.", MessageType.Error);
                return;
            }

            string scriptPath = ResolveAutoRigScriptPath();
            if (string.IsNullOrEmpty(scriptPath))
            {
                SetStatus(
                    $"AutoRig_Feet.py not found.\n" +
                    $"Default: {DefaultAutoRigScriptPath}\n" +
                    $"Install the paid bundle, or set a custom path in Advanced Settings → Paid Blender Scripts.",
                    MessageType.Error);
                return;
            }

            _exportPath = ComputeExportPath();
            string targetMesh = _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)];

            if (_exportMode == ExportMode.Replace)
            {
                bool ok = EditorUtility.DisplayDialog(
                    "Replace Source FBX?",
                    "The original FBX will be overwritten:\n" +
                    AssetDatabase.GetAssetPath(_sourceFbxAsset) +
                    "\n\nA timestamped backup will be created in a _Backups folder.",
                    "Replace", "Cancel");
                if (!ok) return;
            }

            BackupOriginalFbx();

            _blenderMilestone  = 0f;
            _displayProgress   = 0f;
            _currentStepLabel  = "Starting Blender…";
            _resultInstance    = null;
            _createdPrefabPath = null;
            lock (_outputLock) _outputQueue.Clear();

            // Garment meshes to also select in Blender (body's toe + foot weights get transferred to
            // them). Drop blanks, the body itself, and duplicates.
            string[] garmentNames = _garmentMeshNames
                .Where(g => !string.IsNullOrEmpty(g) &&
                            !string.Equals(g, targetMesh, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Rig-only JSON backup (captured in Blender BEFORE the rig runs) — pairs with the FBX
            // backup but lets the user later strip just the rig while keeping post-rig mesh edits.
            string backupJsonPath = null;
            if (_backupEnabled)
            {
                string backupDir = Path.Combine(SourceFbxAbsDir(), "_Backups");
                Directory.CreateDirectory(backupDir);
                string fbxName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(_sourceFbxAsset));
                string stamp   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                backupJsonPath = Path.Combine(backupDir, $"{fbxName}_rigbackup_{stamp}.json");
            }

            string sourceFbxAbs = ToAbsPath(AssetDatabase.GetAssetPath(_sourceFbxAsset));
            string pythonCode   = BlenderBridge.BuildAutoRigFeetScript(
                sourceFbxAbs, targetMesh, _exportPath, scriptPath,
                headless: true, stepDelay: 0f, garmentNames: garmentNames,
                backupJsonPath: backupJsonPath);

            _blenderProcess = BlenderBridge.LaunchBlenderProcess(
                blenderPath, pythonCode, headless: true, factoryStartup: true,
                onOutputLine: line => { lock (_outputLock) _outputQueue.Enqueue(line); });

            if (_blenderProcess == null)
            {
                SetStatus("Failed to launch Blender.", MessageType.Error);
                _state = State.Error;
                return;
            }

            _processStartTime = EditorApplication.timeSinceStartup;
            _lastUpdateTime   = _processStartTime;
            _state            = State.BlenderRunning;
            SetStatus("Blender running headless… this takes ~2 minutes.", MessageType.Info);
        }

        // ─── Run steps ─────────────────────────────────────────────────────────

        void RunFBXSwap()
        {
            // Experimental method: duplicate-and-relink. Always produces a duplicate, so it
            // ignores Export Mode (Replace). The "new FBX" is the Blender export; the "old FBX"
            // is the source body FBX whose duplicate gets the new content written into it.
            if (_swapMethod == SwapMethod.Experimental)
            {
                string relExp   = ToProjectRelative(_exportPath);
                var newFbxExp   = AssetDatabase.LoadAssetAtPath<GameObject>(relExp);
                if (newFbxExp == null) { SetError("New FBX not found after Blender step: " + relExp); return; }

                _resultInstance = FBXSwapperTest.ExecuteSwap(_avatarObject, newFbxExp, _sourceFbxAsset);
                if (_resultInstance == null)
                    SetError("Experimental FBX swap failed — see the Console and the FBXSwapper log.");
                return;
            }

            if (_exportMode == ExportMode.Replace)
            {
                _resultInstance = _avatarObject;
                return;
            }

            string relExport = ToProjectRelative(_exportPath);
            var newFbxAsset  = AssetDatabase.LoadAssetAtPath<GameObject>(relExport);
            if (newFbxAsset == null)
            {
                SetError("New FBX not found after Blender step: " + relExport);
                return;
            }

            // Resolve a .prefab asset to use as the template.
            // If the avatar is only an FBX instance (no prefab), we save a temp prefab first.
            GameObject targetPrefab = GetAvatarPrefabAsset();
            string tempPrefabPath   = null;
            if (targetPrefab == null)
            {
                string fbxDir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(_sourceFbxAsset));
                tempPrefabPath = fbxDir + "/_temp_arf_" + _avatarObject.name + ".prefab";
                targetPrefab = PrefabUtility.SaveAsPrefabAsset(_avatarObject, tempPrefabPath);
                AssetDatabase.Refresh();
                if (targetPrefab == null)
                {
                    SetError("Could not create a temporary prefab from the scene avatar.");
                    return;
                }
            }

            string outFolder = string.IsNullOrEmpty(_exportFolder)
                ? Path.GetDirectoryName(AssetDatabase.GetAssetPath(_sourceFbxAsset))
                : ToProjectRelative(_exportFolder);

            try
            {
                _createdPrefabPath = ShuganTools.FBXSwapper.ExecuteSwap(
                    targetPrefab:       targetPrefab,
                    newFbxModel:        newFbxAsset,
                    oldFbxToReplace:    _sourceFbxAsset,
                    outputFolder:       outFolder,
                    instantiateInScene: false);
            }
            finally
            {
                if (tempPrefabPath != null) AssetDatabase.DeleteAsset(tempPrefabPath);
            }

            if (string.IsNullOrEmpty(_createdPrefabPath))
            {
                SetError("FBX Swapper returned no output. Check the Unity console for details.");
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(_createdPrefabPath);
            if (prefab == null)
            {
                SetError("Could not load created prefab: " + _createdPrefabPath);
                return;
            }

            _resultInstance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            _resultInstance.transform.position = _avatarObject.transform.position + Vector3.right * 1f;
            _resultInstance.transform.rotation = _avatarObject.transform.rotation;
        }

        // Returns the .prefab asset the scene avatar is an instance of, or null if none.
        GameObject GetAvatarPrefabAsset()
        {
            if (_avatarObject == null) return null;

            // Nearest prefab instance root handles nested prefabs correctly
            var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(_avatarObject);
            if (prefabRoot != null)
            {
                var src = PrefabUtility.GetCorrespondingObjectFromSource(prefabRoot);
                if (src != null)
                {
                    string p = AssetDatabase.GetAssetPath(src);
                    if (p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return src;
                }
            }

            // Direct original source (for non-nested prefabs)
            var direct = PrefabUtility.GetCorrespondingObjectFromOriginalSource(_avatarObject);
            if (direct != null)
            {
                string p = AssetDatabase.GetAssetPath(direct);
                if (p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return direct;
            }

            return null;
        }

        void RunAddPrefabs()
        {
            if (_resultInstance == null) return;
            foreach (var prefabRef in _prefabsToAdd)
            {
                if (prefabRef == null) continue;
                if (ChildNameExists(_resultInstance, prefabRef.name))
                {
                    UnityEngine.Debug.LogWarning(
                        $"[AutoRig Feet Distributor] Skipped '{prefabRef.name}' — already exists under '{_resultInstance.name}'.");
                    continue;
                }
                var child = (GameObject)PrefabUtility.InstantiatePrefab(
                    prefabRef, _resultInstance.transform);
                Undo.RegisterCreatedObjectUndo(child, "AutoRig Feet — Add Prefab");
            }
            Selection.activeGameObject = _resultInstance;
        }

        void DrainOutputQueue()
        {
            lock (_outputLock)
            {
                while (_outputQueue.Count > 0)
                {
                    string line = _outputQueue.Dequeue();
                    foreach (var (marker, progress) in BlenderBridge.AutoRigProgressMarkers)
                    {
                        if (line.Contains(marker) && progress > _blenderMilestone)
                        {
                            _blenderMilestone = progress;
                            _currentStepLabel = line.Trim();
                            break;
                        }
                    }
                }
            }
        }

        // ─── Avatar / FBX detection ────────────────────────────────────────────

        void OnAvatarChanged()
        {
            _alreadyRigged = false;
            if (_avatarObject == null) return;
            _alreadyRigged = HasFeetRigBones(_avatarObject);
            AutoDetectFbx();
        }

        void AutoDetectFbx()
        {
            if (_avatarObject == null) return;
            string path = DetectFbxPath(_avatarObject);
            if (path != null)
            {
                _sourceFbxAsset  = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                _fbxAutoDetected = true;
                EditorPrefs.SetString(PrefFbxPath, path);
                RefreshMeshNames();
                _selectedMeshIndex = GuessBodyMeshIndex();
                EditorPrefs.SetInt(PrefMeshIndex, _selectedMeshIndex);
            }
            else
            {
                _fbxAutoDetected = false;
                _sourceFbxAsset  = null;
            }
        }

        static string DetectFbxPath(GameObject avatar)
        {
            foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                string path = AssetDatabase.GetAssetPath(smr.sharedMesh);
                if (!string.IsNullOrEmpty(path) &&
                    path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            return null;
        }

        // Count humanoid bone slots weighted to each mesh; pick the mesh with the highest count.
        // Smart body-mesh detection: a weighted blend of several signals, each normalized across
        // the candidate meshes so no single one dominates. The body is the mesh that is actually
        // skinned to the whole humanoid skeleton (incl. extremities), is tall + wide (T/A-pose arm
        // span), often has a material named "body", and is reasonably dense.
        const float WBodyHumanoid   = 0.40f; // fraction of humanoid bones it is REALLY weighted to
        const float WBodyExtremity  = 0.20f; // weighted to head + both hands + both feet
        const float WBodyHeight     = 0.12f; // tallest bounds (full-height)
        const float WBodyWidth      = 0.12f; // widest bounds (T-pose hand-to-hand span)
        const float WBodyMaterial   = 0.10f; // a material named "...body..."
        const float WBodyVerts      = 0.06f; // vertex count (minor tiebreak)

        int GuessBodyMeshIndex()
        {
            int n = _meshNames.Length;
            if (n <= 1) return 0;

            var smrs = _avatarObject != null
                ? _avatarObject.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                : new SkinnedMeshRenderer[0];

            // Humanoid bone names, plus the key extremity bones (head / hands / feet).
            var humanoidNames  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extremityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var animator = _avatarObject != null ? _avatarObject.GetComponent<Animator>() : null;
            if (animator != null && animator.isHuman)
            {
                foreach (HumanBodyBones hb in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (hb == HumanBodyBones.LastBone) continue;
                    var t = animator.GetBoneTransform(hb);
                    if (t != null) humanoidNames.Add(t.name);
                }
                foreach (var hb in new[] { HumanBodyBones.Head, HumanBodyBones.LeftHand,
                    HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
                {
                    var t = animator.GetBoneTransform(hb);
                    if (t != null) extremityNames.Add(t.name);
                }
            }

            var humanoid  = new float[n];
            var extremity = new float[n];
            var heights   = new float[n];
            var widths    = new float[n];
            var bodyMat   = new float[n];
            var verts     = new float[n];

            for (int i = 0; i < n; i++)
            {
                var smr  = smrs.FirstOrDefault(s => s.sharedMesh != null &&
                    string.Equals(s.sharedMesh.name, _meshNames[i], StringComparison.OrdinalIgnoreCase));
                Mesh mesh = smr != null ? smr.sharedMesh : FindFbxMesh(_meshNames[i]);
                if (mesh == null) continue;

                verts[i]   = mesh.vertexCount;
                var size   = mesh.bounds.size;
                heights[i] = size.y;
                widths[i]  = size.x;

                if (smr != null)
                {
                    foreach (var m in smr.sharedMaterials)
                        if (m != null && m.name.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0)
                        { bodyMat[i] = 1f; break; }

                    if (humanoidNames.Count > 0)
                    {
                        var used = UsedBoneNames(smr);
                        humanoid[i]  = used.Count(humanoidNames.Contains);
                        extremity[i] = extremityNames.Count(used.Contains);
                    }
                }
            }

            float hMax = Max(humanoid), eMax = Max(extremity), tMax = Max(heights),
                  wMax = Max(widths),   vMax = Max(verts);

            int best = 0; float bestScore = -1f;
            for (int i = 0; i < n; i++)
            {
                float score =
                    WBodyHumanoid  * Div(humanoid[i],  hMax) +
                    WBodyExtremity * Div(extremity[i], eMax) +
                    WBodyHeight    * Div(heights[i],   tMax) +
                    WBodyWidth     * Div(widths[i],    wMax) +
                    WBodyMaterial  * bodyMat[i] +
                    WBodyVerts     * Div(verts[i],     vMax);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            return best;
        }

        // Distinct bone names a mesh is ACTUALLY weighted to (weight > ~0), not just listed in bones[].
        static HashSet<string> UsedBoneNames(SkinnedMeshRenderer smr)
        {
            var set = new HashSet<string>();
            var bones = smr.bones;
            var mesh  = smr.sharedMesh;
            if (bones == null || mesh == null) return set;
            try
            {
                var bw = mesh.boneWeights;
                if (bw == null || bw.Length == 0)
                {
                    foreach (var b in bones) if (b != null) set.Add(b.name); // no weight data → fall back
                    return set;
                }
                var used = new HashSet<int>();
                foreach (var w in bw)
                {
                    if (w.weight0 > 0.0001f) used.Add(w.boneIndex0);
                    if (w.weight1 > 0.0001f) used.Add(w.boneIndex1);
                    if (w.weight2 > 0.0001f) used.Add(w.boneIndex2);
                    if (w.weight3 > 0.0001f) used.Add(w.boneIndex3);
                }
                foreach (int idx in used)
                    if (idx >= 0 && idx < bones.Length && bones[idx] != null) set.Add(bones[idx].name);
            }
            catch
            {
                foreach (var b in bones) if (b != null) set.Add(b.name); // mesh not readable → fall back
            }
            return set;
        }

        Mesh FindFbxMesh(string meshName)
        {
            if (_sourceFbxAsset == null) return null;
            return AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(_sourceFbxAsset))
                .OfType<Mesh>()
                .FirstOrDefault(m => string.Equals(m.name, meshName, StringComparison.OrdinalIgnoreCase));
        }

        static float Max(float[] a) { float m = 0f; foreach (float v in a) if (v > m) m = v; return m; }
        static float Div(float a, float max) => max > 0f ? a / max : 0f;

        void RefreshMeshNames()
        {
            if (_sourceFbxAsset == null || !IsValidFbx(_sourceFbxAsset))
            {
                _meshNames = new string[0];
                return;
            }
            _meshNames = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(_sourceFbxAsset))
                .OfType<Mesh>().Select(m => m.name).ToArray();
        }

        static bool HasFeetRigBones(GameObject avatar)
        {
            foreach (Transform t in avatar.GetComponentsInChildren<Transform>(true))
                foreach (string kw in AutoRigFeetBoneKeywords)
                    if (t.name.Contains(kw)) return true;
            return false;
        }

        static bool IsRootObject(GameObject go)
            => go != null && go.transform.parent == null;

        static bool ChildNameExists(GameObject parent, string childName)
        {
            foreach (Transform t in parent.transform)
                if (t.name == childName || t.name.StartsWith(childName + " "))
                    return true;
            return false;
        }

        // ─── Blender detection ─────────────────────────────────────────────────

        void TryAutoDetectBlender(bool silent)
        {
            string found = FindBlenderExe();
            if (found != null)
            {
                EditorPrefs.SetString(BlenderBridge.PrefBlenderPath, found);
                if (!silent) SetStatus("Blender auto-detected: " + found, MessageType.Info);
                Repaint();
            }
            else if (!silent)
                SetStatus("Blender not found automatically — set the path manually.", MessageType.Warning);
        }

        static string FindBlenderExe()
        {
            // 1. Steam (registry + fallback locations)
#if UNITY_EDITOR_WIN
            string[] regKeys = {
                @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
            };
            foreach (string key in regKeys)
            {
                string steam = Microsoft.Win32.Registry.GetValue(key, "SteamPath", null) as string
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

            // 2. Standard Blender Foundation installs (4.x / 5.x, newest first)
            string[] programRoots = {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs"),
            };
            string[] versions = { "5.1", "5.0", "4.4", "4.3", "4.2", "4.1", "4.0" };
            foreach (string root in programRoots)
            {
                string bfDir = Path.Combine(root, "Blender Foundation");
                if (!Directory.Exists(bfDir)) continue;
                foreach (string ver in versions)
                {
                    string exe = Path.Combine(bfDir, $"Blender {ver}", "blender.exe");
                    if (File.Exists(exe)) return exe;
                }
                // Also scan whatever subdirs exist
                foreach (string sub in Directory.GetDirectories(bfDir).OrderByDescending(d => d))
                {
                    string exe = Path.Combine(sub, "blender.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
#endif
            return null;
        }

#if UNITY_EDITOR_WIN
        static string ScanSteamRoot(string steamRoot)
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
#endif

        // Best-effort version guess from the executable's parent folder name.
        static string GuessBlenderVersion(string blenderExePath)
        {
            // e.g. "Blender 4.3" → "4.3"
            string dir = Path.GetFileName(Path.GetDirectoryName(blenderExePath));
            if (string.IsNullOrEmpty(dir)) return null;
            string lower = dir.ToLowerInvariant();
            int idx = lower.IndexOf("blender", StringComparison.Ordinal);
            if (idx < 0) return null;
            string after = dir.Substring(idx + 7).Trim(' ', '-', '_', 'v', 'V');
            int spaceIdx = after.IndexOf(' ');
            string ver = spaceIdx > 0 ? after.Substring(0, spaceIdx) : after;
            return ver.Length > 0 && char.IsDigit(ver[0]) ? ver : null;
        }

        void BrowseForBlender()
        {
            string picked = EditorUtility.OpenFilePanel("Select blender.exe", "", "exe");
            if (string.IsNullOrEmpty(picked)) return;
            EditorPrefs.SetString(BlenderBridge.PrefBlenderPath, picked.Replace("/", "\\"));
            Repaint();
        }

        // ─── Dependency checks ─────────────────────────────────────────────────

        static bool HasVRCFury()
        {
            if (Directory.Exists("Packages/com.vrcfury.vrcfury")) return true;
            if (Directory.Exists(Path.Combine(Application.dataPath, "VRCFury"))) return true;
            return AssetDatabase.FindAssets("VRCFury t:MonoScript").Length > 0;
        }

        // ─── AutoRig Feet script resolution ────────────────────────────────────

        // Returns the absolute path to AutoRig_Feet.py:
        //   1. User override (Advanced Settings → Paid Blender Scripts)
        //   2. Default paid-bundle location (Assets/! Shugan/!_Lab/Script/AutoRig_Feet.py)
        //   3. null if neither exists
        string ResolveAutoRigScriptPath()
        {
            string overridePath = EditorPrefs.GetString(PrefAutoRigScriptPath, "");
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
                return overridePath;

            string defaultAbs = ToAbsPath(DefaultAutoRigScriptPath);
            if (File.Exists(defaultAbs)) return defaultAbs;

            return null;
        }

        void BrowseForAutoRigScript()
        {
            string start = !string.IsNullOrEmpty(_autoRigScriptResolvedPath) &&
                           File.Exists(_autoRigScriptResolvedPath)
                ? Path.GetDirectoryName(_autoRigScriptResolvedPath)
                : Application.dataPath;
            string picked = EditorUtility.OpenFilePanel("Select AutoRig_Feet.py", start, "py");
            if (string.IsNullOrEmpty(picked)) return;
            EditorPrefs.SetString(PrefAutoRigScriptPath, picked.Replace("/", "\\"));
            Repaint();
        }

        // ─── Backup ────────────────────────────────────────────────────────────

        void BackupOriginalFbx()
        {
            string srcAbs    = ToAbsPath(AssetDatabase.GetAssetPath(_sourceFbxAsset));
            string backupDir = Path.Combine(Path.GetDirectoryName(srcAbs), "_Backups");
            Directory.CreateDirectory(backupDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string name  = Path.GetFileNameWithoutExtension(srcAbs);
            string dest  = Path.Combine(backupDir, $"{name}_backup_{stamp}.fbx");
            File.Copy(srcAbs, dest, overwrite: false);
            string metaSrc = srcAbs + ".meta";
            if (File.Exists(metaSrc)) File.Copy(metaSrc, dest + ".meta", overwrite: false);
            UnityEngine.Debug.Log($"[AutoRig Feet Distributor] Backup: {dest}");
        }

        // ─── Path helpers ──────────────────────────────────────────────────────

        string ComputeExportPath()
        {
            if (_sourceFbxAsset == null) return "";
            string srcDir  = SourceFbxAbsDir();
            string srcName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(_sourceFbxAsset));
            if (_exportMode == ExportMode.Replace)
                return Path.Combine(srcDir, srcName + ".fbx");
            string folder = string.IsNullOrEmpty(_exportFolder) ? srcDir : _exportFolder;
            string suffix = string.IsNullOrEmpty(_exportSuffix) ? "Rig_Feet" : _exportSuffix.Trim();
            string name   = srcName + "_" + suffix;
            string path   = Path.Combine(folder, name + ".fbx");
            if (!File.Exists(path)) return path;
            int n = 1;
            while (File.Exists(Path.Combine(folder, $"{name}_{n:D3}.fbx"))) n++;
            return Path.Combine(folder, $"{name}_{n:D3}.fbx");
        }

        string SourceFbxAbsDir()
        {
            if (_sourceFbxAsset == null) return Application.dataPath;
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..",
                Path.GetDirectoryName(AssetDatabase.GetAssetPath(_sourceFbxAsset))));
        }

        string ToAbsPath(string assetPath)
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        string ToProjectRelative(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return "";
            string root = Directory.GetParent(Application.dataPath).FullName;
            return absPath.StartsWith(root)
                ? absPath.Substring(root.Length).TrimStart('\\', '/') : absPath;
        }

        bool IsValidFbx(GameObject obj)
        {
            string p = AssetDatabase.GetAssetPath(obj);
            return !string.IsNullOrEmpty(p) && p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        }

        // ─── UI helpers ────────────────────────────────────────────────────────

        bool IsReady()
        {
            if (_avatarObject == null) return false;
            if (!IsRootObject(_avatarObject)) return false;
            if (_sourceFbxAsset == null || !IsValidFbx(_sourceFbxAsset)) return false;
            if (_meshNames.Length == 0) return false;
            if (!File.Exists(EditorPrefs.GetString(BlenderBridge.PrefBlenderPath, ""))) return false;
            if (!_depAutoRigScript) return false;
            return true;
        }

        string GetBusyLabel()
        {
            switch (_state)
            {
                case State.BlenderRunning: return "Blender Running…";
                case State.FBXSwapping:   return "Swapping FBX…";
                case State.AddingPrefabs: return "Adding Prefabs…";
                case State.Restoring:     return "Restoring…";
                default:                  return "Working…";
            }
        }

        void DrawReadinessHints(bool ready)
        {
            if (ready) return;
            if (_avatarObject == null)
                EditorGUILayout.HelpBox(
                    "Click an avatar root in the scene, or drag it into Target Avatar.", MessageType.Info);
            else if (!IsRootObject(_avatarObject))
                EditorGUILayout.HelpBox(
                    "Select the top-level root object of the avatar, not a child.", MessageType.Error);
            else if (_sourceFbxAsset == null || !IsValidFbx(_sourceFbxAsset))
                EditorGUILayout.HelpBox(
                    "FBX not detected. Expand Advanced Settings → Source FBX.", MessageType.Warning);
            else if (_meshNames.Length == 0)
                EditorGUILayout.HelpBox("No meshes found in the FBX.", MessageType.Warning);
            else if (!File.Exists(EditorPrefs.GetString(BlenderBridge.PrefBlenderPath, "")))
                EditorGUILayout.HelpBox(
                    "Blender not found — expand Advanced Settings to configure or auto-detect it.",
                    MessageType.Warning);
            else if (!_depAutoRigScript)
                EditorGUILayout.HelpBox(
                    "AutoRig_Feet.py not installed — see the 'AutoRig Feet Script (paid)' row above for store links, " +
                    "or set a custom path in Advanced Settings → Paid Blender Scripts.",
                    MessageType.Warning);
        }

        void BrowseExportFolder()
        {
            string start = !string.IsNullOrEmpty(_exportFolder) && Directory.Exists(_exportFolder)
                ? _exportFolder : SourceFbxAbsDir();
            string picked = EditorUtility.OpenFolderPanel("Select Export Folder", start, "");
            if (string.IsNullOrEmpty(picked)) return;
            _exportFolder = picked.Replace("/", "\\");
            EditorPrefs.SetString(PrefExportFolder, _exportFolder);
            Repaint();
        }

        void Separator()
        {
            EditorGUILayout.Space(6);
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 1), new Color(0.5f, 0.5f, 0.5f, 0.3f));
            EditorGUILayout.Space(6);
        }

        void SetStatus(string msg, MessageType type) { _statusMsg = msg; _statusType = type; }

        void SetError(string msg)
        {
            SetStatus(msg, MessageType.Error);
            _state = State.Error;
            UnityEngine.Debug.LogError("[AutoRig Feet Distributor] " + msg);
        }

        static string TruncateLabel(string s, int max)
            => s.Length <= max ? s : "…" + s.Substring(s.Length - (max - 1));
    }
}
