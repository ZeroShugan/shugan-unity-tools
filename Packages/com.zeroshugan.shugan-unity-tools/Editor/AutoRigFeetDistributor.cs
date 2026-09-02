using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
        const string PrefAutoMapFeet          = "ShuganTools_ARF_AutoMapFeet";
        const string PrefTimeoutMin           = "ShuganTools_ARF_TimeoutMin";

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

        // ─── Blender watchdog ─────────────────────────────────────────────────
        // A hung Blender used to leave the UI stuck forever (no exit event, no cancel).
        int _timeoutMin = 10;   // kill the Blender process after N minutes (Advanced; 0 = never)

        // ─── Humanoid auto-map ────────────────────────────────────────────────
        // After the rigged FBX is back in Unity, ensure its humanoid foot/toe bones are mapped
        // (calls HumanoidRigMapping; fills only missing slots, never replaces existing mappings).
        bool _autoMapFeet = true;

        // ─── Run log (full Blender stdout+stderr → file, for debugging) ───────
        StringBuilder _runLog;
        string        _runLogPath;

        // ─── Run report (typed issues parsed from the Blender stdout sentinels) ───
        // Filled live by DrainOutputQueue (RunReport.TryParseLine), evaluated when the process
        // exits (exit code + fresh-FBX check), shown in the report panel. Serialized so the
        // last report survives domain reloads.
        [SerializeField] ShuganTools.RunReport _runReport = new ShuganTools.RunReport();
        [SerializeField] long _runStartTicksUtc;   // UTC ticks at launch — fresh-FBX mtime check

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
            _autoMapFeet   = EditorPrefs.GetBool(PrefAutoMapFeet, true);
            _timeoutMin    = EditorPrefs.GetInt(PrefTimeoutMin, 10);

            if (_prefabsToAdd.Count == 0)
                foreach (string p in DefaultPrefabPaths)
                    _prefabsToAdd.Add(AssetDatabase.LoadAssetAtPath<GameObject>(p));

            // Auto-detect Blender if not already set
            if (!File.Exists(EditorPrefs.GetString(BlenderBridge.PrefBlenderPath, "")))
                TryAutoDetectBlender(silent: true);

            // The avatar may already be selected in the Hierarchy before this window is opened;
            // adopt it now so the Target Avatar field is filled straight away. Only when the field
            // is empty, so a remembered avatar is never clobbered by an unrelated selection.
            if (_avatarObject == null) TryAdoptSelectedAvatar();

            // Bring back the last run report for this FBX (survives restarts / domain reloads).
            LoadLastReport();

            // Once-per-session check whether a newer paid AutoRig .py is published (GitHub
            // version file; stores can't push updates through VCC).
            StartPyVersionCheck();
        }

        void OnSelectionChange() => TryAdoptSelectedAvatar();

        // Pull the currently selected scene object into the Target Avatar field.
        // Called on selection change AND when the window opens: OnSelectionChange only fires on a
        // CHANGE, so selecting the avatar first and opening the tool afterwards used to leave the
        // field empty until you clicked something else.
        void TryAdoptSelectedAvatar()
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
                if (CheckBlenderTimeout()) { Repaint(); return; }

                float elapsed    = (float)(now - _processStartTime);
                float t          = Mathf.Clamp01(elapsed / EstimatedBlenderSec);
                float timePct    = (1f - Mathf.Pow(1f - t, 3f)) * 0.88f;
                float blTarget   = Mathf.Max(_blenderMilestone, timePct);
                float overallMax = _exportMode == ExportMode.Duplicate ? 0.70f : 0.88f;
                _displayProgress = Mathf.Lerp(_displayProgress,
                    blTarget * overallMax, 1f - Mathf.Pow(0.05f, dt));

                if (_blenderProcess != null && _blenderProcess.HasExited)
                {
                    int exitCode = SafeExitCode(_blenderProcess);
                    _blenderProcess.Dispose();
                    _blenderProcess = null;
                    DrainOutputQueue();   // catch lines that arrived after the top-of-frame drain
                    WriteRunLog();
                    AssetDatabase.Refresh();
                    if (EvaluateBlenderResult(exitCode, isRestore: false))
                        _state = State.FBXSwapping;
                }
                Repaint();
            }

            if (_state == State.Restoring)
            {
                DrainOutputQueue();
                if (CheckBlenderTimeout()) { Repaint(); return; }
                float elapsed = (float)(now - _processStartTime);
                float t       = Mathf.Clamp01(elapsed / EstimatedBlenderSec);
                _displayProgress = (1f - Mathf.Pow(1f - t, 3f)) * 0.95f;

                if (_blenderProcess != null && _blenderProcess.HasExited)
                {
                    int exitCode = SafeExitCode(_blenderProcess);
                    _blenderProcess.Dispose();
                    _blenderProcess = null;
                    DrainOutputQueue();
                    WriteRunLog();
                    AssetDatabase.Refresh();
                    if (EvaluateBlenderResult(exitCode, isRestore: true))
                    {
                        _displayProgress  = 1f;
                        _state            = State.Done;
                        _currentStepLabel = "Restore done!";
                        SetStatus("Restore complete — the FBX was reverted (feet rig removed).",
                            MessageType.Info);
                    }
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
                    // Zero the ticked feet shape keys on the generated copy (Duplicate mode only —
                    // in Replace mode the user fixes them on the avatar itself via the warning).
                    ApplyDuplicateShapeKeyFixes();
                    // End of pipeline: ensure the FINAL scene avatar's FBX has humanoid foot/toes mapped.
                    if (_autoMapFeet) AutoMapHumanoidFeet();
                    FinishRunReport();   // re-save: the auto-map may have added warnings
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
            EditorGUILayout.Space(4);
            DrawDependencyStatus();
            Separator();
            DrawMainSection();
            Separator();
            DrawAdvancedSection();

            EditorGUILayout.EndScrollView();

            DrawReadinessHints(IsReady());

            if (_alreadyRigged && _state == State.Idle)
            {
                EditorGUILayout.HelpBox(
                    "This avatar already has AutoRig Feet bones (z_CB / Toes_a1 found). " +
                    "Running again is safe: the previous feet rig is removed automatically and " +
                    "re-created cleanly — use this to redo the rig or apply a newer script version.",
                    MessageType.Info);
            }

            if (!string.IsNullOrEmpty(_statusMsg))
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.HelpBox(_statusMsg, _statusType);
            }

            DrawRunReportPanel();

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

            // Run button + progress bar at the BOTTOM of the window (always visible, below the
            // scroll view, so it stays reachable regardless of how long the settings get).
            EditorGUILayout.Space(4);
            DrawRunButton();
            DrawProgressBarIfActive();

            ShuganToolUI.DrawCredits("AutoRig Feet (Distributor)", ToolVersion);
        }

        // The green "AutoRig Feet" run button — drawn once, at the bottom of the window.
        void DrawRunButton()
        {
            bool busy  = _state != State.Idle && _state != State.Done && _state != State.Error;
            bool ready = IsReady();

            EditorGUI.BeginDisabledGroup(!ready || busy);
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = ready && !busy ? new Color(0.4f, 0.8f, 0.4f) : Color.white;
            if (GUILayout.Button(busy ? GetBusyLabel() : "▶  AutoRig Feet", GUILayout.Height(34)))
                Execute();
            GUI.backgroundColor = prev;
            EditorGUI.EndDisabledGroup();
        }

        // ─── Paid bundle update check ──────────────────────────────────────────
        // The paid AutoRig Feet BUNDLE (the .py script + the FX feet prefabs, shipped together in
        // one store .unitypackage) is distributed via stores, not VCC, so updates aren't visible in
        // the Creator Companion. One version covers the whole bundle: the script's SCRIPT_VERSION.
        // A tiny public version file on GitHub lists the latest published version (same pattern as
        // the Blender addons' version.json — metadata only, never source). Unity reads the installed
        // script's version, fetches the published one once per session, and shows an update warning
        // + store button when behind. This version is INDEPENDENT of the Unity Tools package version.

        const string PaidVersionsUrl =
            "https://raw.githubusercontent.com/ZeroShugan/shugan-unity-tools/main/paid-versions.json";
        const string SessionPyChecked = "ShuganTools_ARF_PyVerChecked";
        const string SessionPyLatest  = "ShuganTools_ARF_PyVerLatest";
        const string SessionPyStore   = "ShuganTools_ARF_PyVerStore";

        // JSON shape: { "autorig_feet_bundle": { "version", "store", "changes":[] } }
        [Serializable] class PaidVersionsDto  { public PaidVersionEntry autorig_feet_bundle; }
        [Serializable] class PaidVersionEntry { public string version; public string store; public string[] changes; }

        string _localPyVersion;          // parsed from the installed .py (cached per path)
        string _localPyVersionPath;
        string _latestPyVersion;         // from GitHub (null until fetched / on failure)
        string _latestPyStoreUrl;
        UnityEngine.Networking.UnityWebRequest _pyVersionRequest;

        string LocalPyVersion()
        {
            string path = _autoRigScriptResolvedPath;
            if (string.IsNullOrEmpty(path)) return null;
            if (path == _localPyVersionPath) return _localPyVersion;
            _localPyVersionPath = path;
            _localPyVersion = null;
            try
            {
                // Only the head of the file is needed (SCRIPT_VERSION sits near the top; the
                // docstring "v3.8.7" is the fallback for older paid-script versions).
                string abs = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
                using (var reader = new StreamReader(abs))
                {
                    var buf = new char[6000];
                    int n = reader.Read(buf, 0, buf.Length);
                    string head = new string(buf, 0, Mathf.Max(0, n));
                    var m = System.Text.RegularExpressions.Regex.Match(
                        head, "SCRIPT_VERSION\\s*=\\s*['\"]([0-9][0-9.]*)");
                    if (!m.Success)
                        m = System.Text.RegularExpressions.Regex.Match(head, @"v(\d+\.\d+(?:\.\d+)?)");
                    if (m.Success) _localPyVersion = m.Groups[1].Value;
                }
            }
            catch { }
            return _localPyVersion;
        }

        void StartPyVersionCheck(bool force = false)
        {
            if (!force && SessionState.GetBool(SessionPyChecked, false))
            {
                string v = SessionState.GetString(SessionPyLatest, "");
                string s = SessionState.GetString(SessionPyStore, "");
                _latestPyVersion  = v == "" ? null : v;
                _latestPyStoreUrl = s == "" ? null : s;
                return;
            }
            if (_pyVersionRequest != null) return;
            try
            {
                _pyVersionRequest = UnityEngine.Networking.UnityWebRequest.Get(PaidVersionsUrl);
                _pyVersionRequest.timeout = 10;
                _pyVersionRequest.SendWebRequest();
                EditorApplication.update += PollPyVersionRequest;
            }
            catch { _pyVersionRequest = null; }
        }

        void PollPyVersionRequest()
        {
            if (_pyVersionRequest == null) { EditorApplication.update -= PollPyVersionRequest; return; }
            if (!_pyVersionRequest.isDone) return;
            EditorApplication.update -= PollPyVersionRequest;
            try
            {
                if (_pyVersionRequest.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    var dto = JsonUtility.FromJson<PaidVersionsDto>(_pyVersionRequest.downloadHandler.text);
                    if (dto != null && dto.autorig_feet_bundle != null &&
                        !string.IsNullOrEmpty(dto.autorig_feet_bundle.version))
                    {
                        _latestPyVersion  = dto.autorig_feet_bundle.version;
                        _latestPyStoreUrl = dto.autorig_feet_bundle.store;
                    }
                }
            }
            catch { /* offline / malformed — silently skip, never bother the user */ }
            finally
            {
                SessionState.SetBool(SessionPyChecked, true);
                SessionState.SetString(SessionPyLatest, _latestPyVersion ?? "");
                SessionState.SetString(SessionPyStore,  _latestPyStoreUrl ?? "");
                _pyVersionRequest.Dispose();
                _pyVersionRequest = null;
                Repaint();
            }
        }

        bool PyUpdateAvailable(out string local, out string latest)
        {
            local  = LocalPyVersion();
            latest = _latestPyVersion;
            if (local == null || latest == null) return false;
            return Version.TryParse(local.TrimStart('v', 'V'), out var lv) &&
                   Version.TryParse(latest.TrimStart('v', 'V'), out var rv) &&
                   rv > lv;
        }

        // Version line under the paid-script requirement row: shows the installed version and,
        // when the published list says there's a newer one, an update warning + store button.
        void DrawPaidScriptVersionRow()
        {
            string local = LocalPyVersion();
            if (PyUpdateAvailable(out _, out string latest))
            {
                EditorGUILayout.HelpBox(
                    $"AutoRig Feet bundle update available: v{latest} (you have v{local}). " +
                    "This covers the Blender script and the FX feet prefabs. Download the latest " +
                    "version from the store where you purchased it, then re-import the .unitypackage.",
                    MessageType.Warning);
                EditorGUILayout.BeginHorizontal();
                Color c = GUI.color;
                GUI.color = new Color(0.5f, 0.9f, 0.5f);
                if (GUILayout.Button("Get the update", EditorStyles.miniButton, GUILayout.Width(110)))
                    Application.OpenURL(string.IsNullOrEmpty(_latestPyStoreUrl)
                        ? StoreBoothUrl : _latestPyStoreUrl);
                GUI.color = c;
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            else if (local != null)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(18);
                GUILayout.Label(
                    _latestPyVersion != null
                        ? $"v{local}  (latest)"
                        : $"v{local}",
                    EditorStyles.miniLabel);
                if (GUILayout.Button(new GUIContent("↻", "Check for updates"),
                        EditorStyles.miniButton, GUILayout.Width(22)))
                    StartPyVersionCheck(force: true);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
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

            // Installed paid-script version + store-update notice (VCC can't update this one).
            if (_depAutoRigScript) DrawPaidScriptVersionRow();
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

            // Target Mesh — dropdown + linked scene-object field. The field always mirrors the
            // dropdown (click it to ping/highlight the mesh in the scene); dropping a scene mesh
            // into it selects that mesh in the dropdown.
            bool hasFbx = _sourceFbxAsset != null && IsValidFbx(_sourceFbxAsset);
            EditorGUI.BeginDisabledGroup(!hasFbx);
            if (_meshNames.Length > 0)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                _selectedMeshIndex = EditorGUILayout.Popup(
                    new GUIContent("Target Mesh",
                        "Body mesh to rig. Auto-selected by counting how many humanoid bones are weighted to each mesh."),
                    Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1),
                    _meshNames);
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetInt(PrefMeshIndex, _selectedMeshIndex);

                string curMeshName = _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)];
                var curSmr = FindAvatarSkinnedMesh(curMeshName);
                var pickedSmr = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                    curSmr, typeof(SkinnedMeshRenderer), true, GUILayout.Width(150));
                if (pickedSmr != curSmr && pickedSmr != null && pickedSmr.sharedMesh != null)
                {
                    int idx = Array.FindIndex(_meshNames, m => string.Equals(
                        m, pickedSmr.sharedMesh.name, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        _selectedMeshIndex = idx;
                        EditorPrefs.SetInt(PrefMeshIndex, _selectedMeshIndex);
                    }
                    else
                        SetStatus($"'{pickedSmr.sharedMesh.name}' is not a mesh of the source FBX.",
                            MessageType.Warning);
                }
                EditorGUILayout.EndHorizontal();
            }
            else
                EditorGUILayout.LabelField("Target Mesh",
                    _avatarObject == null ? "— select an avatar first —"
                    : hasFbx ? "No meshes in FBX"
                    : "— detecting FBX…");

            // Foot bones: Auto (script detects) or a manual pick — dropdown + linked bone field
            // (click to highlight the bone in the hierarchy, or drop a bone to select it).
            if (_meshNames.Length > 0)
            {
                _footOverrideL = DrawFootBoneRow("Left foot bone",  _footCandL, _footOverrideL);
                _footOverrideR = DrawFootBoneRow("Right foot bone", _footCandR, _footOverrideR);
            }
            EditorGUI.EndDisabledGroup();

            // Enabled shape keys that deform the feet would be baked out by the Blender rig.
            if (_meshNames.Length > 0)
                DrawFeetShapeKeyWarning(
                    _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)], BodyMeshLabel);

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


        // ─── Feet shape-key conflict detection ────────────────────────────────
        // Blender rigs a mesh at its BASIS shape (all shape keys 0). If a mesh ships with a shape
        // key enabled that moves the feet, the new toe bones land where the basis feet are, not
        // where the feet visibly are, so the toes animate with a permanent offset. Detected here in
        // Unity, before rigging, for the body mesh AND every garment.
        //
        // Caches are keyed BY MESH: the body and each garment are checked in the same repaint, so a
        // single-slot cache would thrash and re-scan every mesh every frame.
        readonly Dictionary<Mesh, HashSet<int>> _feetVertsByMesh = new Dictionary<Mesh, HashSet<int>>();
        readonly Dictionary<Mesh, Dictionary<int, float>> _shapeDeltaByMesh = new Dictionary<Mesh, Dictionary<int, float>>();
        readonly Dictionary<Mesh, string> _scanNoteByMesh = new Dictionary<Mesh, string>();

        const string BodyMeshLabel = "Body mesh";

        // A shape key counts as touching the feet only if it moves a foot vertex at least this far
        // (metres) — filters out float noise in exported deltas.
        const float FeetShapeDeltaThreshold = 0.0002f;

        // Vertices of this mesh skinned to the foot/toe bones (or anything parented under them,
        // which covers toe bones and any rig previously added). Computed once per mesh.
        HashSet<int> GetFeetVerts(SkinnedMeshRenderer smr, out string note)
        {
            note = "";
            if (smr == null || smr.sharedMesh == null) return null;
            var mesh = smr.sharedMesh;
            if (_feetVertsByMesh.TryGetValue(mesh, out var cachedSet))
            {
                _scanNoteByMesh.TryGetValue(mesh, out note);
                return cachedSet;
            }

            HashSet<int> set = null;
            try
            {
                var bones = smr.bones;
                if (bones == null || bones.Length == 0) note = "this mesh has no bones";
                else
                {
                    // Roots of the feet region: humanoid Foot/Toes when mapped, else foot-like bone
                    // names (same multi-language keywords the rig uses), else the manual picks.
                    var roots = new List<Transform>();
                    var animator = _avatarObject != null ? _avatarObject.GetComponentInChildren<Animator>(true) : null;
                    if (animator != null && animator.isHuman)
                        foreach (var hb in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
                                                   HumanBodyBones.LeftToes, HumanBodyBones.RightToes })
                        {
                            var t = animator.GetBoneTransform(hb);
                            if (t != null) roots.Add(t);
                        }
                    if (roots.Count == 0)
                    {
                        foreach (var bn in bones)
                            if (bn != null && HumanoidRigMapping.NameLooksLikeFoot(bn.name)) roots.Add(bn);
                        foreach (var n in new[] { _footOverrideL, _footOverrideR })
                        {
                            if (string.IsNullOrEmpty(n)) continue;
                            var t = FindAvatarBone(n);
                            if (t != null && !roots.Contains(t)) roots.Add(t);
                        }
                    }

                    if (roots.Count == 0)
                        note = "no foot/toe bones could be identified (map the avatar to Humanoid, or pick the foot bones above)";
                    else
                    {
                        var feetBoneIdx = new HashSet<int>();
                        for (int i = 0; i < bones.Length; i++)
                        {
                            var bn = bones[i];
                            if (bn == null) continue;
                            for (var t = bn; t != null; t = t.parent)
                                if (roots.Contains(t)) { feetBoneIdx.Add(i); break; }
                        }

                        var weights = mesh.boneWeights;
                        if (feetBoneIdx.Count == 0 || weights == null || weights.Length == 0)
                            note = "nothing on this mesh is skinned to the foot bones";
                        else
                        {
                            set = new HashSet<int>();
                            for (int v = 0; v < weights.Length; v++)
                            {
                                var w = weights[v];
                                if ((w.weight0 > 0.01f && feetBoneIdx.Contains(w.boneIndex0)) ||
                                    (w.weight1 > 0.01f && feetBoneIdx.Contains(w.boneIndex1)) ||
                                    (w.weight2 > 0.01f && feetBoneIdx.Contains(w.boneIndex2)) ||
                                    (w.weight3 > 0.01f && feetBoneIdx.Contains(w.boneIndex3)))
                                    set.Add(v);
                            }
                            if (set.Count == 0) { set = null; note = "nothing on this mesh is skinned to the foot bones"; }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                set = null;
                note = "the mesh data could not be read (" + e.Message + ")";
            }

            _feetVertsByMesh[mesh] = set;
            _scanNoteByMesh[mesh] = note;
            return set;
        }

        // Largest movement this shape key applies to any foot vertex, in metres. Cached per mesh +
        // shape: reading blend-shape frames allocates vertex-count arrays, so it must never run per
        // repaint. Only called for shape keys that are currently non-zero.
        float FeetDeltaForShape(Mesh mesh, int shapeIndex, HashSet<int> feetVerts)
        {
            if (!_shapeDeltaByMesh.TryGetValue(mesh, out var perShape))
                _shapeDeltaByMesh[mesh] = perShape = new Dictionary<int, float>();
            if (perShape.TryGetValue(shapeIndex, out float cached)) return cached;

            float maxDelta = 0f;
            try
            {
                int frames = mesh.GetBlendShapeFrameCount(shapeIndex);
                if (frames > 0 && feetVerts != null)
                {
                    var dv = new Vector3[mesh.vertexCount];
                    mesh.GetBlendShapeFrameVertices(shapeIndex, frames - 1, dv, null, null);
                    foreach (int v in feetVerts)
                        if (v < dv.Length)
                        {
                            float m = dv[v].magnitude;
                            if (m > maxDelta) maxDelta = m;
                        }
                }
            }
            catch { maxDelta = 0f; }

            perShape[shapeIndex] = maxDelta;
            return maxDelta;
        }

        // Enabled shape keys on this mesh that move the feet. Shared by the warning UI and by the
        // duplicate-export fix-up, so both always agree on what counts as a problem.
        List<(int idx, string name, float weight, float delta)> GetFeetShapeOffenders(SkinnedMeshRenderer smr)
        {
            var result = new List<(int, string, float, float)>();
            if (smr == null || smr.sharedMesh == null) return result;
            var mesh = smr.sharedMesh;
            if (mesh.blendShapeCount == 0) return result;

            var feetVerts = GetFeetVerts(smr, out _);
            if (feetVerts == null || feetVerts.Count == 0) return result;

            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                float w = smr.GetBlendShapeWeight(i);
                if (Mathf.Abs(w) <= 0.01f) continue;                       // cheap check first
                float d = FeetDeltaForShape(mesh, i, feetVerts);
                if (d >= FeetShapeDeltaThreshold)
                    result.Add((i, mesh.GetBlendShapeName(i), w, d));
            }
            return result;
        }

        // Per-shape opt-out for the duplicate fix-up, keyed by mesh + shape key NAME (indices can
        // differ between the source mesh and the exported one). Absent = enabled, so the checkbox
        // defaults to on without having to pre-populate anything.
        readonly HashSet<string> _shapeFixOptOut = new HashSet<string>();
        static string ShapeFixKey(string meshName, string shapeName) => meshName + "|" + shapeName;

        // Red warning listing every ENABLED shape key that deforms the feet of one mesh.
        // Drawn for the body mesh and for each garment.
        //
        // The offered action depends on Export Mode, because the consequence differs:
        //   Replace   → the rig lands on THIS avatar, so the shape key must actually be zeroed here.
        //   Duplicate → the original is left alone; the fix is applied to the generated copy instead,
        //               so the user keeps their shape key on the avatar they are still working on.
        void DrawFeetShapeKeyWarning(string meshName, string label)
        {
            if (_avatarObject == null || string.IsNullOrEmpty(meshName)) return;

            var smr = FindAvatarSkinnedMesh(meshName);
            if (smr == null || smr.sharedMesh == null) return;
            var mesh = smr.sharedMesh;
            if (mesh.blendShapeCount == 0) return;

            // Cheap pass first: only enabled shape keys can cause the problem.
            bool anyActive = false;
            for (int i = 0; i < mesh.blendShapeCount && !anyActive; i++)
                if (Mathf.Abs(smr.GetBlendShapeWeight(i)) > 0.01f) anyActive = true;
            if (!anyActive) return;

            var feetVerts = GetFeetVerts(smr, out string note);
            if (feetVerts == null || feetVerts.Count == 0)
            {
                // Only worth saying for the body mesh; garments legitimately have no foot weights.
                if (!string.IsNullOrEmpty(note) && label == BodyMeshLabel)
                    EditorGUILayout.HelpBox(
                        "Couldn't check the active shape keys against the feet: " + note + ".",
                        MessageType.Info);
                return;
            }

            var offenders = GetFeetShapeOffenders(smr);
            if (offenders.Count == 0) return;

            bool duplicating = _exportMode == ExportMode.Duplicate;

            EditorGUILayout.Space(3);
            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.45f, 0.45f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            Color prev = GUI.color;
            GUI.color = new Color(1f, 0.5f, 0.5f);
            EditorGUILayout.LabelField($"⚠  {label}: {offenders.Count} shape key(s) move the feet",
                                       EditorStyles.boldLabel);
            GUI.color = prev;

            EditorGUILayout.LabelField(
                duplicating
                    ? "These shape keys are switched on and they move the feet. "
                      + "The rig is built with every shape key at 0, so on the new avatar the toe bones "
                      + "would sit away from the feet you see and the toes would animate offset. "
                      + "Leave the boxes ticked to have them set to 0 on the duplicate only — your "
                      + "current avatar is not touched — and check no animation switches them on in-game."
                    : "These shape keys are switched on and they move the feet. "
                      + "The rig is built with every shape key at 0, so the toe bones would sit away from "
                      + "the feet you actually see and the toes would animate offset. "
                      + "Fix them to 0 below (or in the mesh Inspector) before rigging and before "
                      + "uploading, and check no animation switches them on in-game.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(2);
            foreach (var o in offenders)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    new GUIContent(o.name, $"Moves the feet by up to {o.delta * 100f:0.##} cm at full weight"),
                    GUILayout.MinWidth(110));
                GUILayout.Label($"{o.weight:0.#}", EditorStyles.miniLabel, GUILayout.Width(34));
                GUILayout.Label($"{o.delta * 100f:0.##} cm", EditorStyles.miniLabel, GUILayout.Width(54));

                if (duplicating)
                {
                    string key = ShapeFixKey(meshName, o.name);
                    bool on = !_shapeFixOptOut.Contains(key);
                    bool now = GUILayout.Toggle(on,
                        new GUIContent(" Fix: set to 0 on duplicate",
                            "Set this shape key to 0 on the generated avatar. The original is left as it is."),
                        GUILayout.Width(170));
                    if (now != on)
                    {
                        if (now) _shapeFixOptOut.Remove(key);
                        else     _shapeFixOptOut.Add(key);
                    }
                }
                else if (GUILayout.Button(new GUIContent("Fix to 0", "Set this shape key to 0 on this avatar"),
                             EditorStyles.miniButton, GUILayout.Width(62)))
                {
                    Undo.RecordObject(smr, "AutoRig Feet: zero feet shape key");
                    smr.SetBlendShapeWeight(o.idx, 0f);
                    EditorUtility.SetDirty(smr);
                }
                EditorGUILayout.EndHorizontal();
            }

            if (!duplicating && offenders.Count > 1)
            {
                EditorGUILayout.Space(2);
                if (GUILayout.Button("Fix all to 0", GUILayout.Height(18)))
                {
                    Undo.RecordObject(smr, "AutoRig Feet: zero feet shape keys");
                    foreach (var o in offenders) smr.SetBlendShapeWeight(o.idx, 0f);
                    EditorUtility.SetDirty(smr);
                }
            }

            EditorGUILayout.EndVertical();
        }

        // After a duplicate avatar is produced, zero the ticked feet shape keys ON THE COPY.
        // Matching is by shape-key NAME because the exported mesh may order them differently.
        void ApplyDuplicateShapeKeyFixes()
        {
            if (_resultInstance == null || _resultInstance == _avatarObject) return;  // Replace mode: nothing to do

            var meshNames = new List<string>();
            if (_meshNames.Length > 0)
                meshNames.Add(_meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)]);
            foreach (var g in _garmentMeshNames)
                if (!string.IsNullOrEmpty(g) && !meshNames.Contains(g)) meshNames.Add(g);

            int fixedCount = 0;
            foreach (string mn in meshNames)
            {
                var srcSmr = FindAvatarSkinnedMesh(mn);       // decisions come from the source avatar
                if (srcSmr == null) continue;
                var offenders = GetFeetShapeOffenders(srcSmr);
                if (offenders.Count == 0) continue;

                var dstSmr = FindSkinnedMeshIn(_resultInstance, mn);
                if (dstSmr == null || dstSmr.sharedMesh == null) continue;

                foreach (var o in offenders)
                {
                    if (_shapeFixOptOut.Contains(ShapeFixKey(mn, o.name))) continue;   // user unticked it
                    int idx = dstSmr.sharedMesh.GetBlendShapeIndex(o.name);
                    if (idx < 0) continue;
                    dstSmr.SetBlendShapeWeight(idx, 0f);
                    fixedCount++;
                }
                EditorUtility.SetDirty(dstSmr);
            }

            if (fixedCount > 0)
            {
                _runReport.AddIssue("U_SHAPEKEY_FIXED", "info",
                    $"Set {fixedCount} feet-affecting shape key(s) to 0 on the new avatar "
                    + "(the original was left unchanged).");
                UnityEngine.Debug.Log($"[AutoRig Feet] Zeroed {fixedCount} feet shape key(s) on the duplicate.");
            }
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
            BeginRunLog("restore");
            _runReport        = new ShuganTools.RunReport();
            _runStartTicksUtc = DateTime.UtcNow.Ticks;

            string pythonCode = BlenderBridge.BuildRestoreFeetScript(
                sourceFbxAbs, targetMesh, sourceFbxAbs, scriptPath, jsonPath,
                headless: true, stepDelay: 0f);

            _blenderProcess = BlenderBridge.LaunchBlenderProcess(
                blenderPath, pythonCode, headless: true, factoryStartup: true,
                onOutputLine: EnqueueLine);

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

                // Linked scene field, same idea as Target Mesh: it mirrors the dropdown (click to
                // ping the garment in the Hierarchy) and dropping a mesh in picks it in the dropdown.
                var curGarmentSmr = FindAvatarSkinnedMesh(_garmentMeshNames[i]);
                var pickedSmr = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                    curGarmentSmr, typeof(SkinnedMeshRenderer), true, GUILayout.Width(140));
                if (pickedSmr != curGarmentSmr && pickedSmr != null && pickedSmr.sharedMesh != null)
                {
                    string dropped = pickedSmr.sharedMesh.name;
                    if (choices.Contains(dropped, StringComparer.OrdinalIgnoreCase))
                    {
                        _garmentMeshNames[i] = dropped;
                        SaveGarments();
                    }
                    else if (string.Equals(dropped, bodyMesh, StringComparison.OrdinalIgnoreCase))
                        SetStatus($"'{dropped}' is the body mesh — pick a different mesh as a garment.",
                            MessageType.Warning);
                    else
                        SetStatus($"'{dropped}' is not a mesh of the source FBX.", MessageType.Warning);
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

                // Garments have their own shape keys, and one that moves the feet breaks the
                // transferred toe weights the same way it breaks the body.
                DrawFeetShapeKeyWarning(_garmentMeshNames[i], _garmentMeshNames[i]);
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

            EditorGUI.BeginChangeCheck();
            _timeoutMin = EditorGUILayout.IntField(
                new GUIContent("Blender timeout (minutes)",
                    "If Blender hasn't finished after this many minutes it is stopped and the run "
                    + "fails safely (the FBX is only written at the very end, so nothing is "
                    + "modified). 0 = never time out. Raise this for very heavy avatars."),
                _timeoutMin);
            if (EditorGUI.EndChangeCheck())
            {
                _timeoutMin = Mathf.Clamp(_timeoutMin, 0, 240);
                EditorPrefs.SetInt(PrefTimeoutMin, _timeoutMin);
            }

            Separator();

            // ── Humanoid auto-map ────────────────────────────────────────────
            GUILayout.Label("Humanoid", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _autoMapFeet = EditorGUILayout.ToggleLeft(
                new GUIContent("Auto-map feet/toes to Humanoid (after rig)",
                    "After the rigged FBX returns to Unity, ensure its humanoid Foot/Toes bones are "
                    + "mapped (sets the FBX to Humanoid if needed). Only fills slots Unity left empty — "
                    + "never replaces bones you've already mapped. Uses the Humanoid Rig Mapping tool."),
                _autoMapFeet);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetBool(PrefAutoMapFeet, _autoMapFeet);

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

            // Cancel is only offered during the Blender step: killing Blender there is always
            // safe (the FBX is written at the very end of the script), while the later Unity
            // steps (swap / prefabs) are quick and shouldn't be interrupted midway.
            bool cancellable = _state == State.BlenderRunning || _state == State.Restoring;
            EditorGUILayout.BeginHorizontal();
            Rect r = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(r, Mathf.Clamp01(_displayProgress), label);
            if (cancellable && GUILayout.Button("Cancel", GUILayout.Width(64), GUILayout.Height(20)))
                CancelRun();
            EditorGUILayout.EndHorizontal();
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

            // ── Unity-side pre-flight (cheap; the Python pre-flight remains source of truth) ──
            // The target mesh must be SKINNED — an unskinned mesh has no Armature modifier in
            // Blender and the run would only fail minutes later.
            var targetSmr = FindFbxSkinnedMesh(targetMesh);
            if (targetSmr == null || targetSmr.bones == null || targetSmr.bones.Length == 0)
            {
                SetStatus(
                    $"'{targetMesh}' is not a skinned mesh (it has no bones), so it can't be "
                    + "rigged. Pick the avatar's body mesh — the one that deforms with the "
                    + "skeleton.", MessageType.Error);
                return;
            }

            // Foot-bone sanity: when Auto is on and even the best C# guess has no foot/ankle
            // keyword, ask before spending a Blender run on a likely misdetection.
            if (!ConfirmFootBonesIfUncertain()) return;

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

            // Capture the full Blender console (stdout+stderr) to a log file for debugging.
            BeginRunLog("autorig");

            // Fresh run report; the launch timestamp anchors the "was a NEW FBX actually
            // exported?" check (a stale file on disk must not count as success).
            _runReport         = new ShuganTools.RunReport();
            _runStartTicksUtc  = DateTime.UtcNow.Ticks;

            string sourceFbxAbs = ToAbsPath(AssetDatabase.GetAssetPath(_sourceFbxAsset));
            string pythonCode   = BlenderBridge.BuildAutoRigFeetScript(
                sourceFbxAbs, targetMesh, _exportPath, scriptPath,
                headless: true, stepDelay: 0f, garmentNames: garmentNames,
                backupJsonPath: backupJsonPath,
                footBoneL: _footOverrideL, footBoneR: _footOverrideR);

            _blenderProcess = BlenderBridge.LaunchBlenderProcess(
                blenderPath, pythonCode, headless: true, factoryStartup: true,
                onOutputLine: EnqueueLine);

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
                    _runReport.TryParseLine(line);   // collect [SHUGAN_ISSUE]/[SHUGAN_REPORT] sentinels
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

        // ─── Blender watchdog / cancel ─────────────────────────────────────────

        // Kill + clean up the Blender process (used by Cancel and the timeout watchdog).
        // Safe at any point during the Blender step: the FBX is only written by the export at
        // the very END of the Blender script, so killing mid-run never leaves a half-written FBX.
        void KillBlender()
        {
            try { if (_blenderProcess != null && !_blenderProcess.HasExited) _blenderProcess.Kill(); }
            catch { }
            try { _blenderProcess?.Dispose(); } catch { }
            _blenderProcess = null;
            DrainOutputQueue();
            WriteRunLog();
        }

        // Returns true when the run was killed because it exceeded the timeout.
        bool CheckBlenderTimeout()
        {
            if (_timeoutMin <= 0 || _blenderProcess == null) return false;
            float elapsed = (float)(EditorApplication.timeSinceStartup - _processStartTime);
            if (elapsed < _timeoutMin * 60f) return false;

            KillBlender();
            _runReport.AddIssue("U_TIMEOUT", "fatal",
                $"Blender didn't finish within {_timeoutMin} minutes and was stopped.",
                "Your FBX was not modified (it is only written at the very end). Try again — "
                + "if it keeps hanging, open the run log and check the last line reached. The "
                + "timeout can be raised in Advanced Settings.");
            _runReport.exitCode       = -1;
            _runReport.logPath        = _runLogPath ?? "";
            _runReport.timestampTicks = DateTime.UtcNow.Ticks;
            FinishRunReport();
            SetError(_runReport.FirstFatal.message);
            return true;
        }

        void CancelRun()
        {
            KillBlender();
            _runReport.AddIssue("U_CANCELLED", "info", "Run cancelled by the user.");
            _state            = State.Idle;
            _displayProgress  = 0f;
            _currentStepLabel = "";
            SetStatus("Cancelled — nothing was changed (the FBX is only written at the very end "
                      + "of the Blender step).", MessageType.Info);
        }

        // ─── Blender result evaluation (exit code + typed report + fresh-FBX check) ──

        static int SafeExitCode(Process p)
        {
            try { return p.ExitCode; } catch { return -1; }
        }

        // Decides whether the Blender step actually SUCCEEDED. Previously success was inferred
        // purely from an exported FBX existing on disk — a stale file from an earlier run (or a
        // crashed Blender) could fake success. Now: exit code 0 AND no fatal typed issue AND a
        // FRESHLY-written FBX are all required. Returns true to continue the pipeline; on false
        // the state is set to Error with the most relevant message.
        bool EvaluateBlenderResult(int exitCode, bool isRestore)
        {
            _runReport.exitCode       = exitCode;
            _runReport.logPath        = _runLogPath ?? "";
            _runReport.timestampTicks = DateTime.UtcNow.Ticks;

            if (_runReport.HasFatal || exitCode != 0)
            {
                if (!_runReport.HasFatal)
                    _runReport.AddIssue("U_EXIT_NONZERO", "fatal",
                        $"Blender reported a failure (exit code {exitCode}).",
                        "Open the run log for details. Your original FBX is backed up in the "
                        + "_Backups folder next to it.");
                var fatal = _runReport.FirstFatal;
                FinishRunReport();
                SetError(fatal != null ? fatal.message : "The Blender step failed.");
                return false;
            }

            // Fresh-output check: the FBX Blender was supposed to write must be newer than the
            // launch time (small tolerance for filesystem timestamp granularity). The restore
            // overwrites the source FBX; the rig run writes _exportPath.
            string expected = isRestore
                ? ToAbsPath(AssetDatabase.GetAssetPath(_sourceFbxAsset))
                : _exportPath;
            try
            {
                bool fresh = File.Exists(expected) &&
                             File.GetLastWriteTimeUtc(expected).Ticks >=
                             _runStartTicksUtc - TimeSpan.TicksPerSecond * 5;
                if (!fresh)
                {
                    _runReport.AddIssue("U_STALE_FBX", "fatal",
                        "Blender exited but no new FBX was exported"
                        + (File.Exists(expected) ? " (the file on disk is from an earlier run)."
                                                 : " (the file is missing)."),
                        "Open the run log to see what went wrong. Your original FBX is untouched.");
                    FinishRunReport();
                    SetError(_runReport.FirstFatal.message);
                    return false;
                }
            }
            catch { /* filesystem hiccup — fall through, the swap step re-checks existence */ }

            if (!_runReport.receivedFinal)
                _runReport.AddIssue("U_NO_REPORT", "info",
                    "The Blender script didn't send a detailed report (older paid-script "
                    + "version?) — only the basic success checks were done.");

            if (string.IsNullOrEmpty(_runReport.status))
                _runReport.status = _runReport.HasWarnings ? "warnings" : "ok";
            FinishRunReport();
            return true;
        }

        // ─── Run report persistence + panel ────────────────────────────────────

        const string PrefLastReportPrefix = "ShuganTools_ARF_LastReport_"; // + source FBX GUID

        // Diagnostic-only codes that stay in the log/JSON but are noise in the UI panel.
        static readonly string[] HiddenIssueCodes = { "BONE_CANDIDATES", "INFO_BLENDER_VERSION" };

        [SerializeField] bool _reportFoldout = true;

        // Persist the evaluated report next to the run logs and remember it per-FBX, so the
        // panel survives domain reloads and Unity restarts.
        void FinishRunReport()
        {
            try
            {
                if (_sourceFbxAsset == null) return;
                string fbxPath = AssetDatabase.GetAssetPath(_sourceFbxAsset);
                string dir     = Path.Combine(SourceFbxAbsDir(), "_Backups");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir,
                    Path.GetFileNameWithoutExtension(fbxPath) + "_lastreport.json");
                File.WriteAllText(path, JsonUtility.ToJson(_runReport, true));
                string guid = AssetDatabase.AssetPathToGUID(fbxPath);
                if (!string.IsNullOrEmpty(guid))
                    EditorPrefs.SetString(PrefLastReportPrefix + guid, path);
            }
            catch { /* persistence is best-effort — the in-memory report still shows */ }
        }

        // Reload the last saved report for the current FBX (only when nothing is in memory).
        void LoadLastReport()
        {
            try
            {
                if (_sourceFbxAsset == null) return;
                if (_runReport != null && _runReport.issues.Count > 0) return;
                string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_sourceFbxAsset));
                if (string.IsNullOrEmpty(guid)) return;
                string path = EditorPrefs.GetString(PrefLastReportPrefix + guid, "");
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                var rep = JsonUtility.FromJson<ShuganTools.RunReport>(File.ReadAllText(path));
                if (rep != null && rep.issues != null) _runReport = rep;
            }
            catch { }
        }

        // "Last Run Report" panel: per-issue HelpBoxes (severity-colored) with hints, an Open-log
        // button, and — on fatal — a reassurance that the original FBX is backed up/restorable.
        void DrawRunReportPanel()
        {
            if (_runReport == null || string.IsNullOrEmpty(_runReport.status)) return;
            var visible = _runReport.issues
                .Where(i => Array.IndexOf(HiddenIssueCodes, i.code) < 0).ToList();

            EditorGUILayout.Space(4);
            string when = _runReport.timestampTicks > 0
                ? new DateTime(_runReport.timestampTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "";
            string headline;
            Color  headColor;
            switch (_runReport.status)
            {
                case "fatal":
                    headline  = "Last run FAILED";
                    headColor = new Color(1f, 0.45f, 0.45f);
                    break;
                case "warnings":
                    headline  = "Last run finished with warnings";
                    headColor = new Color(1f, 0.85f, 0.4f);
                    break;
                default:
                    headline  = "Last run OK";
                    headColor = new Color(0.5f, 0.9f, 0.5f);
                    break;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            Color prev = GUI.color; GUI.color = headColor;
            _reportFoldout = EditorGUILayout.Foldout(
                _reportFoldout,
                $"Run Report — {headline}" + (when == "" ? "" : $"   ({when})"),
                true, EditorStyles.foldoutHeader);
            GUI.color = prev;

            if (_reportFoldout)
            {
                if (visible.Count == 0)
                    EditorGUILayout.HelpBox("No issues — everything looks good.", MessageType.Info);

                foreach (var issue in visible)
                {
                    var type = issue.IsFatal ? MessageType.Error
                             : issue.IsWarning ? MessageType.Warning : MessageType.Info;
                    string text = issue.message;
                    if (!string.IsNullOrEmpty(issue.hint)) text += "\n→ " + issue.hint;
                    EditorGUILayout.HelpBox(text, type);
                }

                if (_runReport.HasFatal)
                    EditorGUILayout.HelpBox(
                        "Your original FBX is safe: a timestamped copy is in the _Backups folder "
                        + "next to it, and (if a rig was applied) the Restore section can revert it.",
                        MessageType.None);

                EditorGUILayout.BeginHorizontal();
                if (!string.IsNullOrEmpty(_runReport.logPath) && File.Exists(_runReport.logPath))
                {
                    if (GUILayout.Button("Open Log", GUILayout.Width(90)))
                        EditorUtility.OpenWithDefaultApp(_runReport.logPath);
                    if (GUILayout.Button("Show in Explorer", GUILayout.Width(120)))
                        EditorUtility.RevealInFinder(_runReport.logPath);
                }
                if (GUILayout.Button("Clear Report", GUILayout.Width(100)))
                {
                    _runReport = new ShuganTools.RunReport();
                    try
                    {
                        if (_sourceFbxAsset != null)
                        {
                            string guid = AssetDatabase.AssetPathToGUID(
                                AssetDatabase.GetAssetPath(_sourceFbxAsset));
                            EditorPrefs.DeleteKey(PrefLastReportPrefix + guid);
                        }
                    }
                    catch { }
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        // End of pipeline: ensure the FINAL scene avatar's FBX has its humanoid Foot/Toes mapped.
        // Resolves the FBX from the result instance (the avatar that ends up in the scene), fills only
        // missing slots (never replaces existing mappings), sets Humanoid if needed. Non-fatal.
        void AutoMapHumanoidFeet()
        {
            try
            {
                if (_resultInstance == null)
                {
                    UnityEngine.Debug.LogWarning("[AutoRig Feet] Humanoid auto-map skipped: no result avatar.");
                    return;
                }
                if (!HumanoidRigMapping.TryResolveRigFbx(_resultInstance, out string fbxPath))
                {
                    _runReport.AddIssue("U_HUMANOID_MAP", "warning",
                        "The new toe bones were rigged, but the Humanoid auto-map was skipped "
                        + "(couldn't resolve the final avatar's FBX).",
                        "Open Tools > Shugan > Humanoid Rig Mapping to map the Foot/Toes slots "
                        + "manually if animations need them.");
                    UnityEngine.Debug.LogWarning("[AutoRig Feet] Humanoid auto-map skipped: couldn't resolve the avatar's FBX.");
                    return;
                }
                var res = HumanoidRigMapping.EnsureFeetAndToesMapped(
                    fbxPath, replaceLowConfidence: false, removeJaw: false, logSource: "autorig");
                UnityEngine.Debug.Log($"[AutoRig Feet] Humanoid auto-map ({System.IO.Path.GetFileName(fbxPath)}): {res.message}");
                if (!res.avatarValid || !res.feetToesComplete)
                    _runReport.AddIssue("U_HUMANOID_MAP", "warning",
                        "The new toe bones were rigged, but they couldn't be fully auto-mapped "
                        + "to the Humanoid avatar: " + res.message,
                        "Open the FBX's Rig settings (Configure) or Tools > Shugan > Humanoid "
                        + "Rig Mapping to finish the Foot/Toes mapping.");
            }
            catch (Exception e)
            {
                _runReport.AddIssue("U_HUMANOID_MAP", "warning",
                    "The Humanoid auto-map step failed: " + e.Message,
                    "Open Tools > Shugan > Humanoid Rig Mapping to map the Foot/Toes slots manually.");
                UnityEngine.Debug.LogWarning("[AutoRig Feet] Humanoid auto-map skipped: " + e.Message);
            }
        }

        // ─── Run log capture (full Blender console → file) ─────────────────────

        // Called on the Blender process's output thread for every stdout/stderr line.
        void EnqueueLine(string line)
        {
            lock (_outputLock)
            {
                _outputQueue.Enqueue(line);
                if (_runLog != null) _runLog.AppendLine(line);
            }
        }

        void BeginRunLog(string kind)
        {
            _runLog = new StringBuilder();
            try
            {
                string dir = Path.Combine(SourceFbxAbsDir(), "_Backups");
                Directory.CreateDirectory(dir);
                string fbxName = _sourceFbxAsset != null
                    ? Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(_sourceFbxAsset))
                    : "unknown";
                _runLogPath = Path.Combine(dir,
                    $"{fbxName}_{kind}_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            }
            catch { _runLogPath = null; }
        }

        void WriteRunLog()
        {
            if (_runLog == null || string.IsNullOrEmpty(_runLogPath)) { _runLog = null; return; }
            try
            {
                string text;
                lock (_outputLock) text = _runLog.ToString();
                File.WriteAllText(_runLogPath, text);
                UnityEngine.Debug.Log("[AutoRig Feet] Blender console log saved:\n" + _runLogPath);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[AutoRig Feet] Could not write Blender log: " + ex.Message);
            }
            _runLog = null;
        }

        // ─── Avatar / FBX detection ────────────────────────────────────────────

        void OnAvatarChanged()
        {
            _alreadyRigged = false;
            if (_avatarObject == null) return;
            _alreadyRigged = HasFeetRigBones(_avatarObject);
            AutoDetectFbx();
            LoadLastReport();   // show the saved report for the newly selected FBX (if any)
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

        SkinnedMeshRenderer FindFbxSkinnedMesh(string meshName)
        {
            if (_sourceFbxAsset == null) return null;
            return _sourceFbxAsset.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .FirstOrDefault(s => s.sharedMesh != null &&
                    string.Equals(s.sharedMesh.name, meshName, StringComparison.OrdinalIgnoreCase));
        }

        static float Max(float[] a) { float m = 0f; foreach (float v in a) if (v > m) m = v; return m; }
        static float Div(float a, float max) => max > 0f ? a / max : 0f;

        void RefreshMeshNames()
        {
            if (_sourceFbxAsset == null || !IsValidFbx(_sourceFbxAsset))
            {
                _meshNames = new string[0];
                RefreshFootCandidates();
                return;
            }
            _meshNames = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(_sourceFbxAsset))
                .OfType<Mesh>().Select(m => m.name).ToArray();
            RefreshFootCandidates();
        }

        // ─── Foot-bone picker (manual override of the Blender-side auto-detection) ──

        // Ranked candidate lists for the current FBX (best first, via HumanoidRigMapping's
        // C# scorer — no Blender launch needed). Overrides are stored by NAME and reset to
        // Auto whenever the FBX changes (they are avatar-specific).
        List<(string name, float score)> _footCandL = new List<(string, float)>();
        List<(string name, float score)> _footCandR = new List<(string, float)>();
        [SerializeField] string _footOverrideL = "";   // "" = Auto
        [SerializeField] string _footOverrideR = "";
        string _footCandFbxPath = "";                  // cache key

        void RefreshFootCandidates()
        {
            string path = _sourceFbxAsset != null ? AssetDatabase.GetAssetPath(_sourceFbxAsset) : "";
            if (path == _footCandFbxPath) return;
            _footCandFbxPath = path;
            _footOverrideL = "";
            _footOverrideR = "";
            try
            {
                _footCandL = HumanoidRigMapping.RankFootCandidates(path, "L");
                _footCandR = HumanoidRigMapping.RankFootCandidates(path, "R");
            }
            catch
            {
                _footCandL = new List<(string, float)>();
                _footCandR = new List<(string, float)>();
            }
        }

        // Draw one "Left/Right foot bone" row: ranked popup + linked scene-bone object field.
        // The field mirrors the popup (click = ping/highlight the bone in the Hierarchy); dropping
        // a scene bone into it updates the popup selection. Returns the override name ("" = Auto).
        string DrawFootBoneRow(string label, List<(string name, float score)> cands, string current)
        {
            // Options: Auto (showing the top guess) + ranked candidates. A current override that
            // isn't in the ranked list (e.g. a dropped bone) is appended so it stays selected.
            string top = cands.Count > 0 ? cands[0].name : null;
            bool extraCurrent = !string.IsNullOrEmpty(current) &&
                                cands.FindIndex(c => c.name == current) < 0;
            var options = new string[cands.Count + 1 + (extraCurrent ? 1 : 0)];
            options[0] = top == null ? "Auto" : $"Auto  (detected: {top})";
            for (int i = 0; i < cands.Count; i++) options[i + 1] = cands[i].name;
            if (extraCurrent) options[options.Length - 1] = current;

            int cur = 0;
            if (!string.IsNullOrEmpty(current))
            {
                int idx = cands.FindIndex(c => c.name == current);
                cur = idx >= 0 ? idx + 1 : (extraCurrent ? options.Length - 1 : 0);
            }

            EditorGUILayout.BeginHorizontal();
            int picked = EditorGUILayout.Popup(
                new GUIContent(label,
                    "Which bone is this foot/ankle. Auto = let the script detect it (works for "
                    + "most avatars). Pick manually when the bones use unusual names — the list "
                    + "is sorted most-likely first. You can also drop a bone from the Hierarchy "
                    + "into the field on the right."),
                cur, options);
            string result =
                picked <= 0 ? ""
                : picked <= cands.Count ? cands[picked - 1].name
                : current;

            // Linked scene field: shows the currently effective bone (override, or Auto's guess).
            string shownName = string.IsNullOrEmpty(result) ? top : result;
            var curBone = FindAvatarBone(shownName);
            var pickedBone = (Transform)EditorGUILayout.ObjectField(
                curBone, typeof(Transform), true, GUILayout.Width(150));
            if (pickedBone != curBone && pickedBone != null)
                result = pickedBone.name;
            EditorGUILayout.EndHorizontal();
            return result;
        }

        // ─── Scene lookups for the linked object fields ───────────────────────

        Transform FindAvatarBone(string boneName)
        {
            if (_avatarObject == null || string.IsNullOrEmpty(boneName)) return null;
            foreach (var t in _avatarObject.GetComponentsInChildren<Transform>(true))
                if (t.name == boneName) return t;
            return null;
        }

        // Same lookup as FindAvatarSkinnedMesh but on an arbitrary root — used for the generated
        // duplicate, which isn't _avatarObject.
        static SkinnedMeshRenderer FindSkinnedMeshIn(GameObject root, string meshName)
        {
            if (root == null || string.IsNullOrEmpty(meshName)) return null;
            foreach (var s in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (s.sharedMesh != null &&
                    string.Equals(s.sharedMesh.name, meshName, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }

        SkinnedMeshRenderer FindAvatarSkinnedMesh(string meshName)
        {
            if (_avatarObject == null || string.IsNullOrEmpty(meshName)) return null;
            foreach (var s in _avatarObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (s.sharedMesh != null &&
                    string.Equals(s.sharedMesh.name, meshName, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }

        // Warn before running when Auto is on and the best guess doesn't even contain a foot/ankle
        // keyword — on such rigs the Blender-side detection is likely to grab a shin/leg bone.
        // Returns true to continue, false to cancel the run.
        bool ConfirmFootBonesIfUncertain()
        {
            var uncertain = new List<string>();
            if (string.IsNullOrEmpty(_footOverrideL))
            {
                string top = _footCandL.Count > 0 ? _footCandL[0].name : null;
                if (top == null || !HumanoidRigMapping.NameLooksLikeFoot(top))
                    uncertain.Add("left: " + (top ?? "nothing found"));
            }
            if (string.IsNullOrEmpty(_footOverrideR))
            {
                string top = _footCandR.Count > 0 ? _footCandR[0].name : null;
                if (top == null || !HumanoidRigMapping.NameLooksLikeFoot(top))
                    uncertain.Add("right: " + (top ?? "nothing found"));
            }
            if (uncertain.Count == 0) return true;

            return EditorUtility.DisplayDialog(
                "Foot bones uncertain",
                "I'm not sure these are the foot bones (their names don't contain 'foot'/'ankle'):\n\n  "
                + string.Join("\n  ", uncertain) +
                "\n\nYou can pick the correct bones in the 'Left/Right foot bone' dropdowns "
                + "(sorted most-likely first), or continue and let the script guess.",
                "Continue anyway", "Cancel");
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
