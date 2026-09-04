using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZeroShugan.ShuganUnityTools
{
    public class AutoRigFeetDistributor : EditorWindow
    {
        enum ExportMode { Duplicate, Replace }
        // Order is load-bearing: the choice is persisted as an INT in EditorPrefs
        // (PrefSwapMethod), so reordering these would silently flip existing users' setting.
        // "Experimental" was renamed to "Standard" in place — it is the mode actually relied on —
        // and keeping its index means nobody's stored preference changed meaning.
        enum SwapMethod { Legacy, Standard }
        enum State      { Idle, BlenderRunning, FBXSwapping, AddingPrefabs, Restoring, Done, Error }
        enum Tab        { Setup, Logs, Backups, Report }

        // One concern per tab. These three used to share a single "Logs & Support" page, where the
        // run history, the backups and the bug-report form ran together as one long scroll.
        static readonly string[] TabLabels = { "Setup", "Run Logs", "Backups", "Report a Bug" };

        // ─── EditorPrefs ───────────────────────────────────────────────────────
        const string PrefFbxPath              = "ShuganTools_ARF_FbxPath";
        const string PrefMeshIndex            = "ShuganTools_ARF_MeshIndex";
        const string PrefExportMode           = "ShuganTools_ARF_ExportMode";
        const string PrefSuffix               = "ShuganTools_ARF_Suffix";
        const string PrefExportFolder         = "ShuganTools_ARF_ExportFolder";
        const string PrefAdvanced             = "ShuganTools_ARF_Advanced";
        const string PrefAutoRigScriptPath    = "ShuganTools_ARF_AutoRigScriptPath";
        const string PrefSwapMethod           = "ShuganTools_ARF_SwapMethod";
        const string PrefAutoSwapMethod       = "ShuganTools_ARF_AutoSwapMethod";
        const string PrefGarments             = "ShuganTools_ARF_Garments";
        const string PrefBackupEnabled        = "ShuganTools_ARF_BackupEnabled";
        const string PrefAutoMapFeet          = "ShuganTools_ARF_AutoMapFeet";
        const string PrefTimeoutMin           = "ShuganTools_ARF_TimeoutMin";
        const string PrefTab                  = "ShuganTools_ARF_Tab";
        const string PrefBugContact           = "ShuganTools_ARF_BugContact";

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
        string       _riggedVersion;   // script version that built the existing rig; null = unknown

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

        // ─── Run log (unified per-run diagnostics folder) ─────────────────────
        // Everything one run produces — Blender console, Unity Console, the typed report, the
        // environment and the avatar snapshot — lands in ONE folder the customer can send us.
        // See ShuganRunLog. Previously this was a StringBuilder flushed once when Blender exited,
        // which lost the whole log on a crash or watchdog kill.
        const string LogToolName = "AutoRigFeet";
        ShuganRunLog _runLogger;
        string       _runLogPath;

        // ─── Run report (typed issues parsed from the Blender stdout sentinels) ───
        // Filled live by DrainOutputQueue (RunReport.TryParseLine), evaluated when the process
        // exits (exit code + fresh-FBX check), shown in the report panel. Serialized so the
        // last report survives domain reloads.
        [SerializeField] ShuganTools.RunReport _runReport = new ShuganTools.RunReport();
        [SerializeField] long _runStartTicksUtc;   // UTC ticks at launch — fresh-FBX mtime check

        // ─── Export ────────────────────────────────────────────────────────────
        ExportMode _exportMode   = ExportMode.Duplicate;
        SwapMethod _swapMethod   = SwapMethod.Legacy;   // manual choice, only used when !_autoSwapMethod

        // Pick the swap method from the Export Mode instead of asking. Only one method honours each
        // mode, so the question was never really the user's to answer:
        //
        //   Replace   → Legacy   — the only method that writes the source FBX in place.
        //   Duplicate → Standard — duplicate-and-relink, the proven path.
        //
        // The combination that made this worth automating is Standard + Replace: Standard always
        // duplicates, so it silently ignored Replace and left the source untouched while the user
        // believed it had been replaced. That combination is now unreachable by default.
        //
        // The manual override survives as an escape hatch: Legacy + Duplicate is still a valid path
        // (prefab rebuild), and if the duplicate-and-relink swap ever chokes on someone's avatar it
        // is the only workaround they have before a fix ships.
        bool _autoSwapMethod = true;

        SwapMethod EffectiveSwapMethod()
        {
            if (!_autoSwapMethod) return _swapMethod;
            return _exportMode == ExportMode.Replace ? SwapMethod.Legacy : SwapMethod.Standard;
        }
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
        Tab         _tab = Tab.Setup;

        // ─── Logs & Support tab ────────────────────────────────────────────────
        List<ShuganRunLog.RunFolder> _runsCache;
        bool    _runsCacheDirty = true;
        int     _selectedRun;
        string  _bugMessage = "";
        string  _bugContact = "";
        bool    _bugConsent;                 // per-send, reset after each report (never persisted)
        string  _bugStatus  = "";
        bool    _bugStatusOk;
        bool    _bugLogsFoldout;

        // ─── Dependency cache (refreshed each OnGUI pass) ─────────────────────
        bool   _depBlender;
        bool   _depVRCFury;
        bool   _depAutoRigScript;
        string _autoRigScriptResolvedPath; // the path that resolved (default OR override)
        string _blenderFoundPath;          // path actually found; may differ from EditorPrefs if pref changed

        // ─── Menu ──────────────────────────────────────────────────────────────

        const string WikiUrl = "https://www.notion.so/shugan/AutoRig-Feet-Distributor";

        [MenuItem("Tools/Shugan/AutoRig Feet (Distributor)", false, 1900)]
        static void Open()
        {
            // Tab title stays short — the full "v1.2.2 · script 3.9.0" line is in the header.
            var win = GetWindow<AutoRigFeetDistributor>("AutoRig Feet " + PackageVersion());
            win.minSize = new Vector2(460, 420);
        }

        // ─── Lifecycle ─────────────────────────────────────────────────────────

        void OnEnable()
        {
            _exportMode    = (ExportMode)EditorPrefs.GetInt(PrefExportMode, (int)ExportMode.Duplicate);
            _swapMethod     = (SwapMethod)EditorPrefs.GetInt(PrefSwapMethod, (int)SwapMethod.Legacy);
            _autoSwapMethod = EditorPrefs.GetBool(PrefAutoSwapMethod, true);
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
            _tab           = (Tab)EditorPrefs.GetInt(PrefTab, (int)Tab.Setup);
            // The contact field persists (so a repeat reporter doesn't retype it) but the message
            // and the consent tick never do — consent is per-send, exactly as in the addon.
            _bugContact    = EditorPrefs.GetString(PrefBugContact, "");
            _runsCacheDirty = true;

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

        // Closing the window mid-run must not leave the log stream open — the file would keep the
        // handle and the folder would look like a run that never ended.
        void OnDisable()
        {
            if (_runLogger != null)
            {
                _runLogger.Line("[log] tool window closed or scripts reloaded — log ended here");
                CloseRunLog();
            }
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
                        // A SUCCESSFUL restore goes straight to Done — there is no prefab step to
                        // finalize it — so without this its report was never written and its log
                        // was left open. (Failures were already covered by EvaluateBlenderResult.)
                        FinishRunReport();
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
                    // Give the finished avatar a name that describes what it IS, not how it was made.
                    RenameResultInstance();
                    // Zero the ticked feet shape keys on the generated copy (Duplicate mode only —
                    // in Replace mode the user fixes them on the avatar itself via the warning).
                    ApplyDuplicateShapeKeyFixes();
                    // End of pipeline: ensure the FINAL scene avatar's FBX has humanoid foot/toes mapped.
                    if (_autoMapFeet) AutoMapHumanoidFeet();
                    FinishRunReport();   // re-save: the auto-map may have added warnings
                    // The avatar now HAS a rig (and a fresh version marker). Without this the
                    // "already rigged" notice stayed hidden in Replace mode, where the result is
                    // the same object so avatar-change adoption never fires.
                    RefreshRigState();
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

            // Both versions in the header: the package version identifies the Unity tool, the
            // script version is the paid bundle — and the script version is the one that decides
            // whether re-applying a rig gains the user anything, so it belongs where it is visible
            // rather than only in Advanced Settings.
            string pyVer = LocalPyVersion();
            ShuganToolUI.DrawHeader("AutoRig Feet  —  Distributor    v" + PackageVersion()
                + (string.IsNullOrEmpty(pyVer) ? "" : "  ·  script " + pyVer));
            ShuganToolUI.DrawSocialLinks(WikiUrl);
            EditorGUILayout.Space(4);

            // Only the scroll-view BODY switches. Everything below EndScrollView (status, report
            // panel, run button, progress) is chrome that stays put, so the run button remains
            // reachable from either tab exactly as before.
            EditorGUI.BeginChangeCheck();
            _tab = (Tab)GUILayout.Toolbar((int)_tab, TabLabels, GUILayout.Height(22));
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt(PrefTab, (int)_tab);
                _runsCacheDirty = true;
                GUI.FocusControl(null);
            }
            EditorGUILayout.Space(6);

            switch (_tab)
            {
                case Tab.Setup:
                    DrawDependencyStatus();
                    Separator();
                    DrawMainSection();
                    Separator();
                    DrawAdvancedSection();
                    break;
                case Tab.Logs:    DrawRunLogsTab();   break;
                case Tab.Backups: DrawBackupsTab();   break;
                case Tab.Report:  DrawBugReportTab(); break;
            }

            EditorGUILayout.EndScrollView();

            if (_tab == Tab.Setup) DrawReadinessHints(IsReady());

            // Deliberately NOT gated on State.Idle any more: after a run the state is Done, and
            // hiding the notice exactly then meant the avatar that had just been rigged was the one
            // avatar that never showed it.
            if (_tab == Tab.Setup && _alreadyRigged && _state != State.BlenderRunning
                && _state != State.Restoring)
            {
                EditorGUILayout.HelpBox(BuildAlreadyRiggedMessage(), MessageType.Info);
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

            // Package version, not the old hardcoded ToolVersion: a third version string that never
            // moved told the user nothing and disagreed with everything else on screen.
            ShuganToolUI.DrawCredits("AutoRig Feet (Distributor)", PackageVersion());
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

            // Cache key is path + LAST WRITE TIME, not path alone.
            //
            // Keying on the path only meant the version was read once and then held forever: after
            // the paid script was updated in place, the tool kept reporting the OLD version until
            // the next domain reload. Observed for real — a run wrote "3.8.7" into environment.json
            // and into a bug report while the file on disk said 3.8.8.
            //
            // That is precisely the case this whole version check exists for: a customer buys an
            // update and drops the new .py in while Unity is open. Reporting a stale version there
            // is worse than not checking at all, because the update banner stays hidden AND the
            // wrong version is attached to any bug report they send.
            string stamp = path;
            try
            {
                string absStamp = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
                if (File.Exists(absStamp))
                    stamp = path + "|" + File.GetLastWriteTimeUtc(absStamp).Ticks;
            }
            catch { }

            if (stamp == _localPyVersionPath) return _localPyVersion;
            _localPyVersionPath = stamp;
            _localPyVersion = null;
            try
            {
                // Only the head of the file is needed (SCRIPT_VERSION sits near the top; the
                // docstring "v3.8.7" is the fallback for older paid-script versions).
                //
                // The window was 6000 chars and that turned out to be too tight: the script's
                // docstring grows with every release, and by 3.8.8 SCRIPT_VERSION had drifted to
                // offset ~7200, out of range. Detection then silently fell through to the docstring
                // regex — which happens to work, but is the fallback meant for OLD scripts, so a
                // version mismatch would have been reported with no sign anything was wrong.
                // 64 KB leaves generous headroom and still reads only the head of a 300 KB file.
                string abs = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
                using (var reader = new StreamReader(abs))
                {
                    var buf = new char[65536];
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
            // Restore Original Rig now lives on the "Logs & Support" tab, next to the backups it
            // reads and the run logs that explain why you might want it.
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

        // ─── The same shape key on OTHER meshes ────────────────────────────────
        //
        // Zeroing a feet-moving shape key on the body but leaving the SAME key at another value on
        // a garment desyncs them. The rig is built at basis, so once the body is fixed to 0 and the
        // garment still sits at 100, the moment the user toggles that garment on its feet are
        // somewhere the body's are not. Both meshes have to agree, and it does not matter whether
        // the garment came from the body's FBX or an entirely separate one dropped under the avatar.
        //
        // Cache: the name → (mesh, index) map only changes when the AVATAR changes, so it is built
        // once and weights are read live off it. GetBlendShapeIndex is a name lookup and OnGUI runs
        // many times a second — doing it per frame across every mesh on a 583-blendshape avatar is
        // not free.
        GameObject _companionCacheFor;
        readonly Dictionary<string, List<(SkinnedMeshRenderer smr, int idx)>> _companionCache =
            new Dictionary<string, List<(SkinnedMeshRenderer smr, int idx)>>();

        List<(SkinnedMeshRenderer smr, int idx)> ShapeKeyLocations(string shapeName)
        {
            if (_companionCacheFor != _avatarObject)
            {
                _companionCache.Clear();
                _companionCacheFor = _avatarObject;
            }
            if (_companionCache.TryGetValue(shapeName, out var cached)) return cached;

            var list = new List<(SkinnedMeshRenderer smr, int idx)>();
            if (_avatarObject != null && !string.IsNullOrEmpty(shapeName))
            {
                foreach (var s in _avatarObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (s == null || s.sharedMesh == null) continue;
                    int idx = s.sharedMesh.GetBlendShapeIndex(shapeName);
                    if (idx >= 0) list.Add((s, idx));
                }
            }
            _companionCache[shapeName] = list;
            return list;
        }

        /// <summary>
        /// Every OTHER mesh under the avatar carrying a shape key of this name at a non-zero weight.
        /// These are the ones that would fall out of step if only the rigged mesh were fixed.
        /// </summary>
        List<(SkinnedMeshRenderer smr, int idx, float weight, string meshName)> CompanionShapeKeys(
            string shapeName, SkinnedMeshRenderer exclude)
        {
            var outp = new List<(SkinnedMeshRenderer smr, int idx, float weight, string meshName)>();
            foreach (var loc in ShapeKeyLocations(shapeName))
            {
                if (loc.smr == null || loc.smr == exclude || loc.smr.sharedMesh == null) continue;
                float w = loc.smr.GetBlendShapeWeight(loc.idx);
                if (Mathf.Abs(w) > 0.01f)
                    outp.Add((loc.smr, loc.idx, w, loc.smr.sharedMesh.name));
            }
            return outp;
        }

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

            // Any "↳ also on" rows below need explaining, or fixing them looks like busywork.
            bool anyCompanion = false;
            foreach (var o in offenders)
                if (CompanionShapeKeys(o.name, smr).Count > 0) { anyCompanion = true; break; }
            if (anyCompanion)
                EditorGUILayout.LabelField(
                    "Other meshes on this avatar have the same shape key switched on (listed under "
                    + "each one). Fix those too — if the body is set to 0 but a garment is not, the "
                    + "garment stops lining up with the rigged body the moment it is shown.",
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

                // The same key on other meshes under this avatar. Fixing it here but leaving a
                // garment at another value is worse than leaving both alone: the garment then
                // disagrees with the rigged body the moment it is toggled on.
                foreach (var c in CompanionShapeKeys(o.name, smr))
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(14);
                    EditorGUILayout.LabelField(
                        new GUIContent("↳ also on  " + c.meshName,
                            "This mesh has the same shape key switched on. Fix it too, or it will "
                            + "not line up with the rigged body when it is shown."),
                        EditorStyles.miniLabel, GUILayout.MinWidth(96));
                    GUILayout.Label($"{c.weight:0.#}", EditorStyles.miniLabel, GUILayout.Width(34));
                    GUILayout.Label(" ", EditorStyles.miniLabel, GUILayout.Width(54));

                    if (duplicating)
                    {
                        string ckey = ShapeFixKey(c.meshName, o.name);
                        bool con = !_shapeFixOptOut.Contains(ckey);
                        bool cnow = GUILayout.Toggle(con,
                            new GUIContent(" Fix: set to 0 on duplicate",
                                "Set this shape key to 0 on this mesh in the generated avatar too."),
                            GUILayout.Width(170));
                        if (cnow != con)
                        {
                            if (cnow) _shapeFixOptOut.Remove(ckey);
                            else      _shapeFixOptOut.Add(ckey);
                        }
                    }
                    else if (GUILayout.Button(
                                 new GUIContent("Fix to 0", "Set this shape key to 0 on this mesh"),
                                 EditorStyles.miniButton, GUILayout.Width(62)))
                    {
                        Undo.RecordObject(c.smr, "AutoRig Feet: zero feet shape key");
                        c.smr.SetBlendShapeWeight(c.idx, 0f);
                        EditorUtility.SetDirty(c.smr);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            if (!duplicating && (offenders.Count > 1 || anyCompanion))
            {
                EditorGUILayout.Space(2);
                // Includes the other meshes: fixing only this one is what causes the mismatch.
                if (GUILayout.Button(anyCompanion ? "Fix all to 0 (this mesh + the others listed)"
                                                  : "Fix all to 0", GUILayout.Height(18)))
                {
                    Undo.RecordObject(smr, "AutoRig Feet: zero feet shape keys");
                    foreach (var o in offenders) smr.SetBlendShapeWeight(o.idx, 0f);
                    EditorUtility.SetDirty(smr);

                    foreach (var o in offenders)
                        foreach (var c in CompanionShapeKeys(o.name, smr))
                        {
                            Undo.RecordObject(c.smr, "AutoRig Feet: zero feet shape keys");
                            c.smr.SetBlendShapeWeight(c.idx, 0f);
                            EditorUtility.SetDirty(c.smr);
                        }
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

            // Build the plan first, deduped: the SAME (mesh, shape) pair can be reached twice — once
            // as a rigged mesh's own offender and once as another mesh's companion.
            var plan = new List<(string meshName, string shapeName)>();
            var seen = new HashSet<string>();

            void Queue(string mn2, string shape)
            {
                string key = ShapeFixKey(mn2, shape);
                if (_shapeFixOptOut.Contains(key)) return;   // user unticked it
                if (!seen.Add(key)) return;
                plan.Add((mn2, shape));
            }

            foreach (string mn in meshNames)
            {
                var srcSmr = FindAvatarSkinnedMesh(mn);       // decisions come from the source avatar
                if (srcSmr == null) continue;
                foreach (var o in GetFeetShapeOffenders(srcSmr))
                {
                    Queue(mn, o.name);
                    // Every OTHER mesh holding the same key at a non-zero value. Without this the
                    // body would be corrected while a garment kept its old value, and the two would
                    // disagree as soon as the garment was toggled on.
                    foreach (var c in CompanionShapeKeys(o.name, srcSmr))
                        Queue(c.meshName, o.name);
                }
            }

            int fixedCount = 0, companionCount = 0;
            foreach (var p in plan)
            {
                var dstSmr = FindSkinnedMeshIn(_resultInstance, p.meshName);
                if (dstSmr == null || dstSmr.sharedMesh == null) continue;
                int idx = dstSmr.sharedMesh.GetBlendShapeIndex(p.shapeName);
                if (idx < 0) continue;
                dstSmr.SetBlendShapeWeight(idx, 0f);
                EditorUtility.SetDirty(dstSmr);
                fixedCount++;
                // Case-insensitive to match how meshes are looked up everywhere else.
                if (meshNames.FindIndex(m => string.Equals(m, p.meshName,
                        StringComparison.OrdinalIgnoreCase)) < 0) companionCount++;
            }

            if (fixedCount > 0)
            {
                string extra = companionCount > 0
                    ? $" ({companionCount} of them on other meshes that share the same shape key, so "
                      + "everything stays in step when those meshes are shown)"
                    : "";
                _runReport.AddIssue("U_SHAPEKEY_FIXED", "info",
                    $"Set {fixedCount} feet-affecting shape key(s) to 0 on the new avatar{extra} "
                    + "(the original was left unchanged).");
                UnityEngine.Debug.Log($"[AutoRig Feet] Zeroed {fixedCount} feet shape key(s) on the duplicate.");
            }
        }

        // ─── Restore original rig (from JSON backup) ───────────────────────────

        // True when the SOURCE FBX asset itself carries AutoRig Feet bones — i.e. some previous run
        // rigged it IN PLACE. Cached on path + last-write-time; OnGUI runs many times a second and
        // this walks a few hundred transforms.
        string _srcRiggedKey;
        bool   _srcRiggedValue;

        bool SourceFbxIsRigged()
        {
            if (_sourceFbxAsset == null) return false;
            string path = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            if (string.IsNullOrEmpty(path)) return false;

            string key = path;
            try
            {
                string abs = ToAbsPath(path);
                if (File.Exists(abs)) key = path + "|" + File.GetLastWriteTimeUtc(abs).Ticks;
            }
            catch { }
            if (key == _srcRiggedKey) return _srcRiggedValue;

            _srcRiggedKey = key;
            _srcRiggedValue = false;
            try
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset != null) _srcRiggedValue = HasFeetRigBones(asset);
            }
            catch { }
            return _srcRiggedValue;
        }

        void DrawRestoreSection()
        {
            bool hasFbx = _sourceFbxAsset != null && IsValidFbx(_sourceFbxAsset);
            if (!hasFbx) return;

            // Restore reads AND writes the SOURCE fbx (see RunRestore: BuildRestoreFeetScript gets
            // sourceFbxAbs as both input and output). That only makes sense when the source is the
            // file that got rigged — i.e. Replace mode. In Duplicate mode the rig goes to a separate
            // "_Rig_Feet.fbx" and the source is never modified, so restoring it would push an
            // untouched file through a pointless Blender round-trip.
            //
            // Gated on whether the source ACTUALLY carries rig bones, not on the current Export Mode
            // dropdown: someone who rigged in Replace mode and later switched the dropdown to
            // Duplicate still has a rigged source and still needs this.
            if (!SourceFbxIsRigged())
            {
                EditorGUILayout.Space(6);
                GUILayout.Label("Restore Original Rig", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Not needed for this FBX — it has no AutoRig Feet bones, so nothing has been "
                    + "rigged into it.\n\n"
                    + "In Duplicate mode the rig is written to a separate FBX and your source file "
                    + "is never modified, so there is nothing to restore. This appears automatically "
                    + "if you rig this FBX in Replace mode.",
                    MessageType.None);
                return;
            }

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

            ArchiveWrapperScript(pythonCode);

            _blenderProcess = BlenderBridge.LaunchBlenderProcess(
                blenderPath, pythonCode, headless: true, factoryStartup: true,
                onOutputLine: EnqueueLine);

            if (_blenderProcess == null)
            {
                _runReport.AddIssue("U_LAUNCH_FAILED", "fatal",
                    "Blender could not be started for the restore.",
                    "Check the Blender path in Advanced Settings.");
                _runReport.exitCode       = -1;
                _runReport.logPath        = _runLogPath ?? "";
                _runReport.timestampTicks = DateTime.UtcNow.Ticks;
                FinishRunReport();
                SetError("Failed to launch Blender for restore.");
                return;
            }
            _processStartTime = EditorApplication.timeSinceStartup;
            _lastUpdateTime   = _processStartTime;
            _state            = State.Restoring;
            SetStatus("Restoring rig in Blender… Unity will refresh when it finishes.", MessageType.Info);
        }

        // ─── Run Logs tab ──────────────────────────────────────────────────────
        // What each run produced. The selection made here is what the Report a Bug tab attaches.

        void DrawRunLogsTab()
        {
            EditorGUILayout.LabelField("Run History", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Every run writes its full log, the typed report, your setup and an anonymized "
                + "description of the avatar to:\n" + ShuganRunLog.ToolRootAssetPath(LogToolName)
                + "\nThe last " + ShuganRunLog.DefaultKeepRuns
                + " runs are kept; older ones are deleted automatically.",
                MessageType.None);

            DrawRunHistory();

            var sel = SelectedRun();
            if (sel != null)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(
                    "The selected run is the one the \"Report a Bug\" tab will attach.",
                    MessageType.None);
                if (GUILayout.Button("Report this run to the developer…", GUILayout.Height(22)))
                    GoToTab(Tab.Report);
            }
        }

        void GoToTab(Tab tab)
        {
            _tab = tab;
            EditorPrefs.SetInt(PrefTab, (int)_tab);
            _runsCacheDirty = true;
            GUI.FocusControl(null);
            Repaint();
        }

        List<ShuganRunLog.RunFolder> Runs()
        {
            if (_runsCache == null || _runsCacheDirty)
            {
                _runsCache      = ShuganRunLog.ListRuns(LogToolName);
                _runsCacheDirty = false;
                _selectedRun    = Mathf.Clamp(_selectedRun, 0, Mathf.Max(0, _runsCache.Count - 1));
            }
            return _runsCache;
        }

        ShuganRunLog.RunFolder SelectedRun()
        {
            var runs = Runs();
            if (runs.Count == 0) return null;
            return runs[Mathf.Clamp(_selectedRun, 0, runs.Count - 1)];
        }

        void DrawRunHistory()
        {
            var runs = Runs();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Refresh", GUILayout.Width(70))) _runsCacheDirty = true;
                using (new EditorGUI.DisabledScope(runs.Count == 0))
                {
                    if (GUILayout.Button("Open Folder", GUILayout.Width(95)))
                    {
                        string root = ShuganSanitize.ToAbsolute(ShuganRunLog.ToolRootAssetPath(LogToolName));
                        if (!string.IsNullOrEmpty(root)) EditorUtility.RevealInFinder(root);
                    }
                }
            }

            if (runs.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No runs recorded yet. The next time you run AutoRig Feet — successful or not — "
                    + "its diagnostics will appear here.", MessageType.Info);
                return;
            }

            for (int i = 0; i < runs.Count; i++)
            {
                var r = runs[i];
                bool selected = i == _selectedRun;

                using (new EditorGUILayout.VerticalScope(selected ? EditorStyles.helpBox : GUIStyle.none))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Toggle(selected, GUIContent.none, GUILayout.Width(16)) && !selected)
                            _selectedRun = i;

                        Color prev = GUI.color;
                        GUI.color = StatusColor(r.status);
                        GUILayout.Label(StatusGlyph(r.status), GUILayout.Width(18));
                        GUI.color = prev;

                        GUILayout.Label(r.when != default(DateTime)
                                ? r.when.ToString("yyyy-MM-dd HH:mm") : "(unknown date)",
                            GUILayout.Width(115));
                        GUILayout.Label(string.IsNullOrEmpty(r.label) ? "—" : r.label,
                            EditorStyles.miniLabel);
                        GUILayout.FlexibleSpace();
                        GUILayout.Label(FormatBytes(r.sizeBytes), EditorStyles.miniLabel,
                            GUILayout.Width(60));
                    }

                    if (selected)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(18);
                            if (GUILayout.Button("Open Log", GUILayout.Width(80)))
                                OpenRunFile(r, "run.log");
                            using (new EditorGUI.DisabledScope(!r.hasReport))
                                if (GUILayout.Button("Report", GUILayout.Width(70)))
                                    OpenRunFile(r, "report.json");
                            using (new EditorGUI.DisabledScope(!r.hasAvatar))
                                if (GUILayout.Button("Avatar", GUILayout.Width(70)))
                                    OpenRunFile(r, "avatar.json");
                            if (GUILayout.Button("Show in Explorer", GUILayout.Width(120)))
                                EditorUtility.RevealInFinder(r.folderAbs);
                            GUILayout.FlexibleSpace();
                        }
                    }
                }
            }
        }

        static void OpenRunFile(ShuganRunLog.RunFolder r, string fileName)
        {
            try
            {
                string p = Path.Combine(r.folderAbs, fileName);
                if (File.Exists(p)) EditorUtility.OpenWithDefaultApp(p);
                else EditorUtility.DisplayDialog("Not found",
                    fileName + " is not in this run's folder.\n\n"
                    + "That usually means the run ended before it got that far.", "OK");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[AutoRig Feet] Could not open " + fileName + ": " + ex.Message);
            }
        }

        static string StatusGlyph(string status)
        {
            switch (status)
            {
                case "fatal":    return "✖";
                case "warnings": return "▲";
                case "ok":       return "✔";
                default:         return "•";
            }
        }

        static Color StatusColor(string status)
        {
            switch (status)
            {
                case "fatal":    return new Color(1f, 0.45f, 0.45f);
                case "warnings": return new Color(1f, 0.85f, 0.4f);
                case "ok":       return new Color(0.5f, 0.9f, 0.5f);
                default:         return Color.gray;
            }
        }

        static string FormatBytes(long b)
        {
            if (b <= 0) return "—";
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024f).ToString("0.#") + " KB";
            return (b / (1024f * 1024f)).ToString("0.#") + " MB";
        }

        // ─── Backups + restore (moved here from the Setup tab) ─────────────────

        void DrawBackupsTab()
        {
            EditorGUILayout.LabelField("Backups & Restore", EditorStyles.boldLabel);

            if (_sourceFbxAsset == null)
            {
                EditorGUILayout.HelpBox(
                    "Select a Target Avatar on the Setup tab to see its backups.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField("Source FBX",
                Path.GetFileName(AssetDatabase.GetAssetPath(_sourceFbxAsset)));

            EditorGUILayout.HelpBox(
                "Backups stay next to your FBX in its _Backups folder, not in the logs folder — "
                + "they are recovery files, and they are never rotated away.\n\n"
                + "A full FBX copy is only made when a run would overwrite your source file "
                + "(Replace mode). Duplicate mode writes a separate FBX and leaves the source "
                + "untouched, so it does not need one.", MessageType.None);

            try
            {
                string dir = Path.Combine(SourceFbxAbsDir(), "_Backups");
                string fbx = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(_sourceFbxAsset));
                if (Directory.Exists(dir))
                {
                    var fbxBackups = Directory.GetFiles(dir, fbx + "_backup_*.fbx");
                    Array.Sort(fbxBackups, StringComparer.Ordinal);
                    Array.Reverse(fbxBackups);

                    EditorGUILayout.LabelField(
                        "Full FBX backups: " + fbxBackups.Length, EditorStyles.miniLabel);
                    int show = Mathf.Min(fbxBackups.Length, 5);
                    for (int i = 0; i < show; i++)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label("  " + Path.GetFileName(fbxBackups[i]), EditorStyles.miniLabel);
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("Show", GUILayout.Width(50)))
                                EditorUtility.RevealInFinder(fbxBackups[i]);
                        }
                    }
                    if (fbxBackups.Length > show)
                        EditorGUILayout.LabelField("  … and " + (fbxBackups.Length - show) + " older",
                            EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.LabelField("No _Backups folder yet.", EditorStyles.miniLabel);
                }
            }
            catch (Exception ex)
            {
                EditorGUILayout.HelpBox("Could not list backups: " + ex.Message, MessageType.Warning);
            }

            EditorGUILayout.Space(4);
            DrawRestoreSection();
        }

        // ─── Report a Bug ──────────────────────────────────────────────────────
        // [ARF-BUGREPORT] Consent-first, mirroring the Data Transfer++ addon: Send stays disabled
        // until there is a message AND a ticked consent box, View shows the exact payload first,
        // and consent resets after every send.

        const string BugProductSlug = "shugan_autorig_feet";

        // Triage order: what the setup was, what the tool concluded, what the avatar looked like,
        // then the full trace.
        static readonly string[] ReportFileNames =
            { "environment.json", "report.json", "avatar.json", "humanoid_mapping.txt", "run.log" };

        // CooldownRemaining() reads a small JSON file; OnGUI runs many times a second, so it is
        // sampled at most once a second rather than per repaint.
        double _cooldownCheckedAt = -999;
        int    _cooldownCache;

        int CachedCooldown()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _cooldownCheckedAt > 1.0)
            {
                _cooldownCheckedAt = now;
                _cooldownCache     = ShuganBugReport.CooldownRemaining();
            }
            return _cooldownCache;
        }

        void DrawBugReportTab()
        {
            EditorGUILayout.LabelField("Report a Bug", EditorStyles.boldLabel);

            var runs = Runs();
            if (runs.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Run AutoRig Feet once first — a report is built from a run's diagnostics.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                "Sends: versions + the selected run's logs + your message.\n"
                + "Logs include names used in Unity (objects, bones, meshes, shape keys).\n"
                + "No mesh geometry, textures, materials or files. Material and texture names are "
                + "replaced with anonymous ids. Anonymous unless you fill in a contact.",
                MessageType.None);

            // Its own picker: the run list lives on another tab now, so a user who lands here
            // directly still has to be able to choose which run they are reporting.
            var labels = new string[runs.Count];
            for (int i = 0; i < runs.Count; i++)
            {
                var r = runs[i];
                labels[i] = StatusGlyph(r.status) + "  "
                          + (r.when != default(DateTime) ? r.when.ToString("yyyy-MM-dd HH:mm") : "?")
                          + "  " + (string.IsNullOrEmpty(r.label) ? "—" : r.label)
                          + (string.IsNullOrEmpty(r.status) ? "" : "  (" + r.status + ")");
            }
            _selectedRun = Mathf.Clamp(_selectedRun, 0, runs.Count - 1);
            _selectedRun = EditorGUILayout.Popup("Run to report", _selectedRun, labels);

            var run = runs[_selectedRun];
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth);
                if (GUILayout.Button("Open Log", GUILayout.Width(80)))
                    OpenRunFile(run, "run.log");
                if (GUILayout.Button("Show in Explorer", GUILayout.Width(120)))
                    EditorUtility.RevealInFinder(run.folderAbs);
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("What happened?");
            _bugMessage = EditorGUILayout.TextArea(_bugMessage, GUILayout.Height(56));

            EditorGUI.BeginChangeCheck();
            _bugContact = EditorGUILayout.TextField(
                new GUIContent("Contact (optional)",
                    "Email or username if you want a reply — leave empty to stay fully anonymous."),
                _bugContact);
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(PrefBugContact, _bugContact ?? "");

            _bugLogsFoldout = EditorGUILayout.Foldout(_bugLogsFoldout, "What gets attached", true);
            if (_bugLogsFoldout)
            {
                EditorGUI.indentLevel++;
                // Sizes come from FileInfo, not from assembling the bundle: this runs on every
                // repaint, and reading a 400 KB log per frame to show a number would be absurd.
                foreach (string f in ReportFileNames)
                {
                    string p = Path.Combine(run.folderAbs, f);
                    bool exists = File.Exists(p);
                    long len = 0;
                    if (exists) { try { len = new FileInfo(p).Length; } catch { } }
                    EditorGUILayout.LabelField("• " + f, exists ? FormatBytes(len) : "(not produced)");
                }
                EditorGUILayout.LabelField(" ",
                    "Use View Report to read the exact text before sending.", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            _bugConsent = EditorGUILayout.ToggleLeft(
                new GUIContent("I agree to share this data anonymously",
                    "Required before sending. Consent resets after each report."),
                _bugConsent);

            int cooldown = CachedCooldown();
            bool canSend = _bugConsent
                           && !string.IsNullOrEmpty((_bugMessage ?? "").Trim())
                           && cooldown == 0
                           && !ShuganBugReport.IsSending;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("View Report", GUILayout.Height(24)))
                {
                    string p = ShuganBugReport.OpenPreview(ShuganBugReport.Build(BuildBugRequest()));
                    _bugStatusOk = p != null;
                    _bugStatus   = p != null
                        ? "Report opened in your text editor — nothing was sent."
                        : "Could not open the preview file.";
                }

                using (new EditorGUI.DisabledScope(!canSend))
                {
                    if (GUILayout.Button(ShuganBugReport.IsSending ? "Sending…" : "Send Report",
                            GUILayout.Height(24)))
                        ConfirmAndSendBugReport();
                }
            }

            if (cooldown > 0)
                EditorGUILayout.LabelField(" ",
                    "You can send another report in " + cooldown + "s.", EditorStyles.miniLabel);
            else if (!_bugConsent || string.IsNullOrEmpty((_bugMessage ?? "").Trim()))
                EditorGUILayout.LabelField(" ",
                    "Describe the issue and tick the consent box to enable Send.",
                    EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(_bugStatus))
                EditorGUILayout.HelpBox(_bugStatus,
                    _bugStatusOk ? MessageType.Info : MessageType.Warning);
        }

        void ConfirmAndSendBugReport()
        {
            var payload = ShuganBugReport.Build(BuildBugRequest());

            bool go = EditorUtility.DisplayDialog(
                "Send anonymous bug report?",
                "Will be sent to the developer (Shugan):\n"
                + "  • the selected run's logs + your message\n"
                + "  • tool / Unity / Blender / OS versions\n"
                + "  • an anonymized description of your avatar\n\n"
                + "Logs include names used in Unity (objects, bones, meshes, shape keys).\n"
                + "No mesh geometry, textures or files.\n"
                + (string.IsNullOrEmpty(payload.contact)
                    ? "The report is anonymous."
                    : "You will be identifiable by the contact you entered: " + payload.contact),
                "Send Report", "Cancel");
            if (!go) return;

            ShuganBugReport.Send(payload, _bugConsent, (ok, msg) =>
            {
                _bugStatusOk = ok;
                _bugStatus   = msg;
                if (ok)
                {
                    // Message and consent clear on success; the contact deliberately stays, so a
                    // repeat reporter does not have to retype it.
                    _bugMessage = "";
                    _bugConsent = false;
                }
                Repaint();
            });
            Repaint();
        }

        ShuganBugReport.Request BuildBugRequest()
        {
            return new ShuganBugReport.Request
            {
                product        = BugProductSlug,
                productVersion = PackageVersion() + "+py" + (LocalPyVersion() ?? "unknown"),
                runtime        = "Unity " + Application.unityVersion + " / " + BlenderVersionForReport(),
                message        = _bugMessage ?? "",
                contact        = _bugContact ?? "",
                logParts       = BuildReportLogParts(),
            };
        }

        // Assembles the actual bundle. Only called when the user clicks View Report or Send —
        // never from OnGUI, because it reads every artifact off disk.
        List<ShuganBugReport.LogPart> BuildReportLogParts()
        {
            var parts = new List<ShuganBugReport.LogPart>();
            var run   = SelectedRun();
            if (run == null) return parts;

            foreach (string f in ReportFileNames)
            {
                string p = Path.Combine(run.folderAbs, f);
                if (!File.Exists(p)) continue;

                // Only run.log is trimmable. The structured documents are small, dense and the
                // most useful part of a report, so they are kept whole and the log takes the cut.
                bool isConsoleLog = f.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
                parts.Add(new ShuganBugReport.LogPart(
                    run.folderName + "/" + f,
                    ReadTextCapped(p, isConsoleLog ? 400000 : 120000),
                    trimmable: isConsoleLog));
            }
            return parts;
        }

        // Bounded read: a pathological run log must not be pulled into memory whole just to be
        // trimmed a moment later by the bundler.
        static string ReadTextCapped(string path, int maxChars)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length <= maxChars) return File.ReadAllText(path);

                using (var sr = new StreamReader(path))
                {
                    int head = maxChars / 4;
                    var buf  = new char[head];
                    int n    = sr.Read(buf, 0, head);
                    string headText = new string(buf, 0, Math.Max(0, n));

                    int tail = maxChars - head;
                    sr.BaseStream.Seek(Math.Max(0, fi.Length - tail), SeekOrigin.Begin);
                    sr.DiscardBufferedData();
                    string tailText = sr.ReadToEnd();

                    return headText + "\n\n[... trimmed while reading ...]\n\n" + tailText;
                }
            }
            catch (Exception ex) { return "(could not read " + Path.GetFileName(path) + ": " + ex.Message + ")"; }
        }

        string BlenderVersionForReport()
        {
            try
            {
                if (_runReport != null && _runReport.issues != null)
                    foreach (var i in _runReport.issues)
                        if (i != null && i.code == "INFO_BLENDER_VERSION" && !string.IsNullOrEmpty(i.message))
                            return i.message;
            }
            catch { }
            return "Blender (version in logs)";
        }

        // The published package version — the single Unity-side version this tool reports anywhere
        // (window title, header, credits, run log, environment.json, bug reports). It replaced a
        // hardcoded "1.0" constant that never moved and disagreed with everything else on screen.
        static string _packageVersion;
        static string PackageVersion()
        {
            if (!string.IsNullOrEmpty(_packageVersion)) return _packageVersion;
            _packageVersion = "unknown";
            try
            {
                string p = ShuganSanitize.ToAbsolute(
                    "Packages/com.zeroshugan.shugan-unity-tools/package.json");
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        File.ReadAllText(p), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) _packageVersion = m.Groups[1].Value;
                }
            }
            catch { }
            return _packageVersion;
        }

        // ─── Garment meshes (toe-weight transfer targets) ──────────────────────

        // Transient feedback for a rejected garment drag-and-drop. Self-clearing, and scoped to the
        // garment section instead of the window-wide status box.
        string _garmentDropWarning = "";
        double _garmentDropWarningAt;
        const double GarmentDropWarningSeconds = 6.0;

        void SetGarmentDropWarning(string msg)
        {
            _garmentDropWarning   = msg;
            _garmentDropWarningAt = EditorApplication.timeSinceStartup;
        }

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
                    // Rejections are transient feedback about one drag, NOT run status: they used to
                    // go through SetStatus, which owns the persistent status box at the bottom and
                    // never clears — so the message stuck around long after the garment was removed.
                    else if (string.Equals(dropped, bodyMesh, StringComparison.OrdinalIgnoreCase))
                        SetGarmentDropWarning($"'{dropped}' is the body mesh — pick a different mesh as a garment.");
                    else
                        SetGarmentDropWarning($"'{dropped}' is not a mesh of the source FBX.");
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

            // Expires on its own, and disappears immediately once there are no garment slots left.
            if (!string.IsNullOrEmpty(_garmentDropWarning))
            {
                if (_garmentMeshNames.Count == 0 ||
                    EditorApplication.timeSinceStartup - _garmentDropWarningAt > GarmentDropWarningSeconds)
                    _garmentDropWarning = "";
                else
                {
                    EditorGUILayout.HelpBox(_garmentDropWarning, MessageType.Warning);
                    Repaint();   // keep ticking so it clears without needing a mouse move
                }
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

            // The field shows the script ACTUALLY IN USE, not just a manual override.
            //
            // It used to be bound to the override alone, so on a normal install — where the script
            // sits at the default path and no override is set — the field read "None" while the
            // green line underneath said the script was found and in use. Two bits of UI directly
            // contradicting each other, and no way to click through to the asset.
            //
            // Now it auto-fills from ResolveAutoRigScriptPath(), so it always mirrors the status
            // line and you can ping/open the real file from it.
            bool usingOverride = !string.IsNullOrEmpty(overridePath) && File.Exists(overridePath);
            string shownPath   = _autoRigScriptResolvedPath;      // override, else default, else null
            string shownRel    = !string.IsNullOrEmpty(shownPath) ? ToProjectRelative(shownPath) : "";
            UnityEngine.Object shownAsset =
                !string.IsNullOrEmpty(shownRel) && shownRel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(shownRel)
                    : null;

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            var pickedAsset = EditorGUILayout.ObjectField(
                new GUIContent("AutoRig Feet .py",
                    "The AutoRig Feet Python script this tool will run.\n\n"
                    + "Auto-filled with the script it found. Drop a different .py here to override "
                    + "it; clear the field (or press ×) to go back to the default:\n"
                    + DefaultAutoRigScriptPath),
                shownAsset, typeof(UnityEngine.Object), false);
            if (EditorGUI.EndChangeCheck())
            {
                if (pickedAsset != null)
                {
                    string rel = AssetDatabase.GetAssetPath(pickedAsset);
                    if (!string.IsNullOrEmpty(rel) && rel.EndsWith(".py",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // Picking the default script is not an override — leave the pref empty so
                        // the tool keeps tracking the default if it ever moves.
                        string abs = ToAbsPath(rel);
                        bool isDefault = false;
                        // Guarded: this runs inside OnGUI, and an exception here would break the
                        // layout for the rest of the frame.
                        try
                        {
                            isDefault = string.Equals(
                                Path.GetFullPath(abs),
                                Path.GetFullPath(ToAbsPath(DefaultAutoRigScriptPath)),
                                StringComparison.OrdinalIgnoreCase);
                        }
                        catch { }
                        EditorPrefs.SetString(PrefAutoRigScriptPath, isDefault ? "" : abs);
                    }
                    else
                    {
                        EditorPrefs.SetString(PrefAutoRigScriptPath, "");
                    }
                }
                else
                {
                    EditorPrefs.SetString(PrefAutoRigScriptPath, "");
                }
                _autoRigScriptResolvedPath = ResolveAutoRigScriptPath();   // reflect it immediately
            }
            if (GUILayout.Button("Browse…", GUILayout.Width(70)))
                BrowseForAutoRigScript();
            if (!string.IsNullOrEmpty(overridePath) && GUILayout.Button("×", GUILayout.Width(22)))
                EditorPrefs.SetString(PrefAutoRigScriptPath, "");
            EditorGUILayout.EndHorizontal();

            // A script outside the Unity project can't be shown as an object reference, so fall
            // back to a read-only path field — otherwise the row would look empty for those users.
            if (!string.IsNullOrEmpty(shownPath) && shownAsset == null)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField(new GUIContent("Path",
                    "External script path (outside the Unity project)"), shownPath);
                EditorGUI.EndDisabledGroup();
            }

            // Status line — says WHERE it came from, so an auto-filled field is not mistaken for a
            // manual override the user does not remember setting.
            Color cs = GUI.color;
            GUI.color = _depAutoRigScript ? Color.green : Color.red;
            EditorGUILayout.LabelField(
                _depAutoRigScript
                    ? $"✓ Using {(usingOverride ? "custom override" : "default location")}: " +
                      ToProjectRelative(_autoRigScriptResolvedPath)
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
            _autoSwapMethod = EditorGUILayout.ToggleLeft(
                new GUIContent("Choose method automatically  (recommended)",
                    "Replace → Legacy (the only method that writes your FBX in place).\n"
                    + "Duplicate → Standard (duplicate-and-relink).\n\n"
                    + "Untick only to force a method — e.g. to fall back to Legacy if the "
                    + "duplicate-and-relink swap fails on a particular avatar."),
                _autoSwapMethod);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetBool(PrefAutoSwapMethod, _autoSwapMethod);

            if (_autoSwapMethod)
            {
                EditorGUILayout.LabelField("Method",
                    EffectiveSwapMethod() + "   (from Export Mode: " + _exportMode + ")",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                _swapMethod = (SwapMethod)EditorGUILayout.EnumPopup(
                    new GUIContent("Method (forced)",
                        "Standard: duplicate the avatar and give it a private copy of the FBX "
                        + "— your original is never touched. Always produces a duplicate.\n"
                        + "Legacy: rebuild the avatar on the new FBX. The only method that honours "
                        + "Replace mode."),
                    _swapMethod);
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetInt(PrefSwapMethod, (int)_swapMethod);

                // Only reachable now that the choice is forced — automatic selection can't produce it.
                if (_swapMethod == SwapMethod.Standard && _exportMode == ExportMode.Replace)
                    EditorGUILayout.HelpBox(
                        "Standard always duplicates, so Export Mode = Replace will be IGNORED and "
                        + "your source FBX left untouched. Use Legacy to actually replace it, or "
                        + "re-enable automatic selection.",
                        MessageType.Warning);
            }

            if (EffectiveSwapMethod() == SwapMethod.Standard)
                EditorGUILayout.LabelField(" ",
                    "Writes a swap debug log to Assets/! Shugan/!_Lab/Script/FBXSwapper_Logs/.",
                    EditorStyles.miniLabel);

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

            // RE-RIG: the source FBX already carries a rig, so this run will replace it. Ask how to
            // treat the existing foot weights before anything is written.
            if (!ConfirmReRigStrategy(out _rerigRestoreJson, out _rerigSkipFootReduction)) return;

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
                footBoneL: _footOverrideL, footBoneR: _footOverrideR,
                rerigRestoreJsonPath: _rerigRestoreJson,
                skipFootReduction: _rerigSkipFootReduction);

            // Archive the wrapper that actually ran. It goes to a fixed temp filename and is
            // overwritten by the next run, so without this the exact python behind a failure is
            // unrecoverable. Saved as .txt, never .py, so it can never be mistaken for the paid
            // script by the script-path resolver or by the user browsing the folder.
            ArchiveWrapperScript(pythonCode);

            _blenderProcess = BlenderBridge.LaunchBlenderProcess(
                blenderPath, pythonCode, headless: true, factoryStartup: true,
                onOutputLine: EnqueueLine);

            if (_blenderProcess == null)
            {
                _runReport.AddIssue("U_LAUNCH_FAILED", "fatal",
                    "Blender could not be started.",
                    "Check the Blender path in Advanced Settings, and that Blender is not blocked "
                    + "by antivirus or already running an update.");
                _runReport.exitCode       = -1;
                _runReport.logPath        = _runLogPath ?? "";
                _runReport.timestampTicks = DateTime.UtcNow.Ticks;
                FinishRunReport();
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
            // Standard method: duplicate-and-relink. Always produces a duplicate, so it
            // ignores Export Mode (Replace). The "new FBX" is the Blender export; the "old FBX"
            // is the source body FBX whose duplicate gets the new content written into it.
            if (EffectiveSwapMethod() == SwapMethod.Standard)
            {
                string relExp   = ToProjectRelative(_exportPath);
                var newFbxExp   = AssetDatabase.LoadAssetAtPath<GameObject>(relExp);
                if (newFbxExp == null) { SetError("New FBX not found after Blender step: " + relExp); return; }

                _resultInstance = FBXSwapperTest.ExecuteSwap(_avatarObject, newFbxExp, _sourceFbxAsset);
                if (_resultInstance == null)
                    SetError("FBX swap failed — see the Console and the FBXSwapper log.");
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

            // Instantiate into the TARGET AVATAR'S OWN SCENE, not the active one. With several
            // scenes open in the Hierarchy, the active scene is often not the one the avatar lives
            // in, and the rigged copy would land in the wrong scene — far from the original, and
            // easy to save into a scene the user never meant to touch.
            var targetScene = _avatarObject != null ? _avatarObject.scene : default(Scene);
            _resultInstance = targetScene.IsValid()
                ? (GameObject)PrefabUtility.InstantiatePrefab(prefab, targetScene)
                : (GameObject)PrefabUtility.InstantiatePrefab(prefab);

            _resultInstance.transform.position = _avatarObject.transform.position + Vector3.right * 1f;
            _resultInstance.transform.rotation = _avatarObject.transform.rotation;

            // Keep it next to the original in the Hierarchy too: same parent, inserted right after.
            if (_avatarObject.transform.parent != null)
                _resultInstance.transform.SetParent(_avatarObject.transform.parent, worldPositionStays: true);
            _resultInstance.transform.SetSiblingIndex(_avatarObject.transform.GetSiblingIndex() + 1);
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

        // ─── Final pass: name the result after what it is ──────────────────────

        /// <summary>
        /// Rename the finished avatar to "&lt;original avatar&gt;_&lt;Export Suffix&gt;", e.g.
        /// `Poiyomi_Airi` → `Poiyomi_Airi_Rig_Feet`.
        ///
        /// Neither swap path produced a meaningful name on its own: the Standard swap calls its
        /// duplicate `<name>_swap`, which describes the mechanism rather than the result (and
        /// FBXSwapper is a general-purpose tool, so that name stays right for its standalone use),
        /// while Legacy Duplicate mode simply inherits the source prefab's name. Either way the
        /// scene object did not say it was the feet-rigged version, and did not match the naming the
        /// exported FBX already uses.
        ///
        /// NOT done in Replace mode: there `_resultInstance` IS the user's original scene object,
        /// and renaming it would rename the avatar they already had rather than a new copy.
        /// </summary>
        void RenameResultInstance()
        {
            if (_resultInstance == null || _avatarObject == null) return;
            if (_resultInstance == _avatarObject) return;   // Replace mode — same object, leave it

            string suffix = (_exportSuffix ?? "").Trim();
            if (suffix.Length == 0) suffix = "Rig_Feet";     // same fallback as ComputeExportPath

            string desired = _avatarObject.name + "_" + suffix;
            string final   = UniqueSiblingName(_resultInstance, desired);
            if (_resultInstance.name == final) return;

            try
            {
                string before = _resultInstance.name;
                Undo.RecordObject(_resultInstance, "AutoRig Feet — Rename Result");
                _resultInstance.name = final;
                UnityEngine.Debug.Log(
                    "[AutoRig Feet Distributor] Renamed result: " + before + " → " + final);
            }
            catch (Exception ex)
            {
                // Cosmetic only — never fail a completed rig over a name.
                UnityEngine.Debug.LogWarning(
                    "[AutoRig Feet Distributor] Could not rename the result: " + ex.Message);
            }
        }

        /// <summary>
        /// `desired`, or `desired_001`, `desired_002`… if a sibling already has that name. Mirrors
        /// how ComputeExportPath numbers a taken FBX filename, so a second run's scene object and
        /// its FBX stay parallel instead of silently producing two identically-named avatars.
        /// </summary>
        static string UniqueSiblingName(GameObject self, string desired)
        {
            if (!NameTakenAmongSiblings(self, desired)) return desired;
            for (int n = 1; n < 1000; n++)
            {
                string candidate = desired + "_" + n.ToString("D3");
                if (!NameTakenAmongSiblings(self, candidate)) return candidate;
            }
            return desired;
        }

        static bool NameTakenAmongSiblings(GameObject self, string name)
        {
            try
            {
                Transform parent = self.transform.parent;
                if (parent != null)
                {
                    foreach (Transform t in parent)
                        if (t != null && t.gameObject != self && t.name == name) return true;
                    return false;
                }
                // No parent: the siblings are the scene's root objects.
                var scene = self.scene;
                if (!scene.IsValid()) return false;
                foreach (var root in scene.GetRootGameObjects())
                    if (root != null && root != self && root.name == name) return true;
            }
            catch { }
            return false;
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
            _runReport.exitCode       = -1;
            _runReport.logPath        = _runLogPath ?? "";
            _runReport.timestampTicks = DateTime.UtcNow.Ticks;
            // A cancelled run used to skip this entirely, so its report was never written and its
            // log folder was left open. A user who cancels because something looks wrong is exactly
            // the user whose diagnostics we want to keep.
            FinishRunReport();
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
            // SAVE, do not FINISH: the run is not over. The FBX swap, prefab wiring and humanoid
            // auto-map still have to happen, and finishing here would close the run log right after
            // Blender — which is exactly what it used to do, truncating the log before the
            // Unity-side steps and sending the humanoid mapping log to the wrong folder.
            SaveRunReport();
            return true;
        }

        // ─── Run report persistence + panel ────────────────────────────────────

        const string PrefLastReportPrefix = "ShuganTools_ARF_LastReport_"; // + source FBX GUID

        // Diagnostic-only codes that stay in the log/JSON but are noise in the UI panel.
        static readonly string[] HiddenIssueCodes = { "BONE_CANDIDATES", "INFO_BLENDER_VERSION" };

        [SerializeField] bool _reportFoldout = true;

        // Persist the evaluated report into this run's diagnostics folder and remember it per-FBX,
        // so the panel survives domain reloads and Unity restarts. Also the single place the run
        // log is closed — every terminal path funnels through here, including cancellation.
        // Save the report WITHOUT ending the run. Used at intermediate checkpoints (the Blender step
        // succeeded, but the Unity-side pipeline is still going).
        void SaveRunReport()
        {
            try
            {
                if (_runReport != null)
                {
                    _runReport.toolVersion   = PackageVersion();
                    _runReport.scriptVersion = LocalPyVersion() ?? "";
                    _runReport.runFolder     = _runLogger != null ? (_runLogger.FolderAssetPath ?? "") : "";
                }

                string json = JsonUtility.ToJson(_runReport, true);

                // Primary home: the run folder, so report.json sits with the log, the environment
                // and the avatar snapshot as one sendable bundle.
                if (_runLogger != null) _runLogger.WriteText("report.json", json);

                // Secondary: the per-FBX "last report" pointer that drives the panel. Kept beside
                // the FBX as before so an existing project keeps working after this change.
                if (_sourceFbxAsset != null)
                {
                    string fbxPath = AssetDatabase.GetAssetPath(_sourceFbxAsset);
                    string dir     = Path.Combine(SourceFbxAbsDir(), "_Backups");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir,
                        Path.GetFileNameWithoutExtension(fbxPath) + "_lastreport.json");
                    File.WriteAllText(path, json);
                    string guid = AssetDatabase.AssetPathToGUID(fbxPath);
                    if (!string.IsNullOrEmpty(guid))
                        EditorPrefs.SetString(PrefLastReportPrefix + guid, path);
                }
            }
            catch { /* persistence is best-effort — the in-memory report still shows */ }
        }

        // The run is over: save the final report and close the log. Every terminal path calls this.
        void FinishRunReport()
        {
            SaveRunReport();
            CloseRunLog();
        }

        // Close the streaming log. Safe to call twice.
        void CloseRunLog()
        {
            if (_runLogger == null) return;
            try
            {
                _runLogger.Section("RUN FINISHED — status: " +
                    (_runReport != null && !string.IsNullOrEmpty(_runReport.status)
                        ? _runReport.status : "(none)"));
                _runLogger.End();
            }
            catch { }
            _runLogger = null;
            _runsCacheDirty = true;
            // No AssetDatabase.Refresh here: the log folder ends in `~`, so Unity deliberately does
            // not import it (see ShuganRunLog.ToolRootAssetPath). Refreshing was what dragged the
            // still-being-written run.log into the asset pipeline in the first place.
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

                // A failed run is exactly when a customer wants to reach us, so offer the route
                // here rather than making them find the tab. This only NAVIGATES — it never sends;
                // the consent gate over there is still the only thing that can.
                if (_runReport.HasFatal && _tab != Tab.Report)
                {
                    EditorGUILayout.Space(2);
                    if (GUILayout.Button("Report this to the developer…", GUILayout.Height(22)))
                    {
                        _selectedRun = 0;             // the run that just failed is the newest
                        GoToTab(Tab.Report);
                    }
                }
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
                // Redirect the mapping log into THIS run's folder. It used to land in
                // HumanoidRigMapping_Logs, so every AutoRig run scattered a second log somewhere the
                // customer would never think to send.
                var res = HumanoidRigMapping.EnsureFeetAndToesMapped(
                    fbxPath, replaceLowConfidence: false, removeJaw: false, logSource: "autorig",
                    logFolderOverride: _runLogger != null ? _runLogger.FolderAssetPath : null);
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

        // ─── Run log capture (Blender console + Unity console → run folder) ────

        // Called on the Blender process's output thread for every stdout/stderr line.
        void EnqueueLine(string line)
        {
            lock (_outputLock) _outputQueue.Enqueue(line);
            // Streamed straight to disk (ShuganRunLog does its own locking, so the two locks are
            // never nested). AutoFlush means the log survives a crash or a watchdog kill.
            if (_runLogger != null) _runLogger.Line(line);
        }

        // Opens this run's diagnostics folder and captures everything we know BEFORE Blender starts,
        // so a run that dies early still produces a usable bundle.
        void BeginRunLog(string kind)
        {
            try { if (_runLogger != null) _runLogger.End(); } catch { }

            string fbxName = _sourceFbxAsset != null
                ? Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(_sourceFbxAsset))
                : "unknown";

            _runLogger  = ShuganRunLog.Begin(LogToolName, fbxName);
            _runLogPath = _runLogger.LogFileAbs;
            _runLogger.BeginConsoleCapture();

            _runLogger.Line("AutoRig Feet — " + kind + " run");
            _runLogger.Line("tool " + PackageVersion() + " · script " + (LocalPyVersion() ?? "?")
                            + " · Unity " + Application.unityVersion);
            _runLogger.Line("folder: " + _runLogger.FolderAssetPath);

            WriteEnvironmentJson(kind);
            WriteAvatarSnapshotJson();
            _runLogger.Section("BLENDER CONSOLE");
        }

        // The Blender process ended. The log stays OPEN: the Unity-side steps that follow (FBX swap,
        // prefab wiring, humanoid auto-map) can fail too, and those failures used to be invisible.
        // FinishRunReport closes it.
        void WriteRunLog()
        {
            if (_runLogger == null) return;
            _runLogger.Section("BLENDER PROCESS ENDED");
            if (!string.IsNullOrEmpty(_runLogPath))
                UnityEngine.Debug.Log("[AutoRig Feet] Run log:\n" + _runLogPath);
        }

        void WriteEnvironmentJson(string kind)
        {
            try
            {
                string blenderPath = EditorPrefs.GetString(BlenderBridge.PrefBlenderPath, "");
                var e = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("runKind",        kind),
                    new KeyValuePair<string, string>("startedUtc",     DateTime.UtcNow.ToString("o")),
                    new KeyValuePair<string, string>("toolVersion",    PackageVersion()),
                    new KeyValuePair<string, string>("scriptVersion",  LocalPyVersion() ?? "(unknown)"),
                    new KeyValuePair<string, string>("scriptPath",     _autoRigScriptResolvedPath ?? "(unresolved)"),
                    new KeyValuePair<string, string>("unityVersion",   Application.unityVersion),
                    new KeyValuePair<string, string>("os",             SystemInfo.operatingSystem),
                    new KeyValuePair<string, string>("blenderPath",    blenderPath),
                    new KeyValuePair<string, string>("hasVRCFury",     _depVRCFury.ToString()),
                    new KeyValuePair<string, string>("exportMode",     _exportMode.ToString()),
                    new KeyValuePair<string, string>("swapMethod",     EffectiveSwapMethod().ToString()),
                    new KeyValuePair<string, string>("swapMethodAuto", _autoSwapMethod.ToString()),
                    new KeyValuePair<string, string>("exportSuffix",   _exportSuffix ?? ""),
                    new KeyValuePair<string, string>("exportFolder",   string.IsNullOrEmpty(_exportFolder) ? "(beside source)" : _exportFolder),
                    new KeyValuePair<string, string>("backupEnabled",  _backupEnabled.ToString()),
                    new KeyValuePair<string, string>("autoMapFeet",    _autoMapFeet.ToString()),
                    new KeyValuePair<string, string>("timeoutMinutes", _timeoutMin.ToString()),
                    new KeyValuePair<string, string>("footOverrideL",  string.IsNullOrEmpty(_footOverrideL) ? "(auto)" : _footOverrideL),
                    new KeyValuePair<string, string>("footOverrideR",  string.IsNullOrEmpty(_footOverrideR) ? "(auto)" : _footOverrideR),
                    new KeyValuePair<string, string>("targetMesh",     CurrentMeshName()),
                    new KeyValuePair<string, string>("garments",       string.Join(", ", _garmentMeshNames.ToArray())),
                    new KeyValuePair<string, string>("alreadyRigged",  _alreadyRigged.ToString()),
                    new KeyValuePair<string, string>("riggedWithScript",
                        string.IsNullOrEmpty(_riggedVersion) ? "(unknown / pre-3.9.0)" : _riggedVersion),
                    // Which re-rig strategy the user picked, so a bug report explains why the foot
                    // weights look the way they do.
                    new KeyValuePair<string, string>("rerigStrategy",
                        !string.IsNullOrEmpty(_rerigRestoreJson) ? "restore original weights first"
                        : _rerigSkipFootReduction               ? "keep current weights (no backup to restore)"
                        : _alreadyRigged                        ? "re-apply as-is (weights reduced again)"
                        : "n/a (first rig)"),
                };
                _runLogger.WriteText("environment.json", AutoRigAvatarSnapshot.CaptureEnvironmentJson(e));
            }
            catch (Exception ex) { _runLogger.Line("[log] environment capture failed: " + ex.Message); }
        }

        void WriteAvatarSnapshotJson()
        {
            try
            {
                var extras = new AutoRigAvatarSnapshot.Extras
                {
                    bodyMeshName     = CurrentMeshName(),
                    garmentMeshNames = new List<string>(_garmentMeshNames),
                };
                extras.shapeKeyFindings = CollectShapeKeyFindings();

                string fbxPath = _sourceFbxAsset != null
                    ? AssetDatabase.GetAssetPath(_sourceFbxAsset) : "";
                string json = AutoRigAvatarSnapshot.CaptureJson(_avatarObject, fbxPath, extras);
                _runLogger.WriteText("avatar.json", json);
                _runLogger.Line("[log] avatar snapshot written (" + json.Length + " chars)");
            }
            catch (Exception ex) { _runLogger.Line("[log] avatar snapshot failed: " + ex.Message); }
        }

        // Re-uses the tool's own feet-shape-key detection, so the bundle records exactly what the
        // red warning showed the user — including the case where nothing was detected and why.
        List<string> CollectShapeKeyFindings()
        {
            var findings = new List<string>();
            var meshes   = new List<string> { CurrentMeshName() };
            meshes.AddRange(_garmentMeshNames);

            foreach (string mn in meshes)
            {
                if (string.IsNullOrEmpty(mn)) continue;
                try
                {
                    var smr = FindAvatarSkinnedMesh(mn);
                    if (smr == null) { findings.Add(mn + ": not found in the scene avatar"); continue; }
                    GetFeetVerts(smr, out string note);
                    if (!string.IsNullOrEmpty(note)) findings.Add(mn + ": " + note);
                    foreach (var o in GetFeetShapeOffenders(smr))
                    {
                        findings.Add(mn + ": shape key '" + o.name + "' = " + o.weight.ToString("0.##")
                                     + " moves the feet by " + (o.delta * 100f).ToString("0.###") + " cm");
                        // Same key switched on elsewhere — a desync source, so it belongs in a
                        // bug report even though the mesh itself is not being rigged.
                        foreach (var c in CompanionShapeKeys(o.name, smr))
                            findings.Add("    also on '" + c.meshName + "' = "
                                         + c.weight.ToString("0.##"));
                    }
                }
                catch (Exception ex) { findings.Add(mn + ": shape key check failed — " + ex.Message); }
            }
            return findings;
        }

        string CurrentMeshName()
        {
            if (_meshNames == null || _meshNames.Length == 0) return "";
            return _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)];
        }

        // The generated wrapper is public-package code that importlib-loads the paid script by
        // path — it contains no paid source, so archiving it leaks nothing.
        void ArchiveWrapperScript(string pythonCode)
        {
            if (_runLogger == null || string.IsNullOrEmpty(pythonCode)) return;
            try { _runLogger.WriteText("blender_wrapper.txt", pythonCode); }
            catch { }
        }

        // ─── Avatar / FBX detection ────────────────────────────────────────────

        void OnAvatarChanged()
        {
            RefreshRigState();
            if (_avatarObject == null) return;
            AutoDetectFbx();
            LoadLastReport();   // show the saved report for the newly selected FBX (if any)
        }

        /// <summary>
        /// Recompute whether the scene avatar carries a feet rig, and which script version built it.
        ///
        /// Called on avatar change AND at the end of a run. The end-of-run refresh matters because
        /// in Replace mode the result IS the original object, so `TryAdoptSelectedAvatar` early-outs
        /// on `go == _avatarObject` and never re-ran this — the "already rigged" notice stayed
        /// hidden on the very avatar that had just been rigged.
        /// </summary>
        void RefreshRigState()
        {
            _alreadyRigged  = false;
            _riggedVersion  = null;
            // Meshes may have been added or swapped (a run replaces them outright), so the
            // shape-key location map has to be rebuilt rather than trusted.
            _companionCache.Clear();
            _companionCacheFor = _avatarObject;
            if (_avatarObject == null) return;
            _alreadyRigged  = HasFeetRigBones(_avatarObject);
            if (_alreadyRigged) _riggedVersion = GetRiggedScriptVersion(_avatarObject);
        }

        // Chosen in ConfirmReRigStrategy, consumed when building the Blender script.
        string _rerigRestoreJson;
        bool   _rerigSkipFootReduction;

        /// <summary>
        /// The OLDEST rig backup for the source FBX, or null when there is none.
        ///
        /// Oldest, not newest, on purpose: the first rig's backup is the only one captured from a
        /// genuinely un-rigged mesh. (Later ones can no longer be written — the script now refuses
        /// to overwrite a good backup on a re-rig — but older projects may still have some.)
        /// </summary>
        string OldestRigBackup()
        {
            try
            {
                if (_sourceFbxAsset == null) return null;
                string dir = Path.Combine(SourceFbxAbsDir(), "_Backups");
                if (!Directory.Exists(dir)) return null;
                string fbx = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(_sourceFbxAsset));
                var files = Directory.GetFiles(dir, fbx + "_rigbackup_*.json");
                if (files.Length == 0) return null;
                Array.Sort(files, StringComparer.Ordinal);   // timestamped names sort chronologically
                return files[0];
            }
            catch { return null; }
        }

        /// <summary>
        /// When the source FBX already has a rig, ask how to treat the existing foot weights.
        /// Returns false to abort the run. Outputs are the two Blender-side options.
        ///
        /// Why this exists: the foot-weight reduction is multiplicative from the CURRENT weight, so
        /// re-applying a rig on an already-rigged mesh reduces the foot influence again — and again
        /// on the next run. That is silent damage on precisely the workflow meant to IMPROVE an
        /// avatar (re-apply after a script update). The user gets to decide, with the consequence
        /// spelled out, rather than finding out three re-rigs later.
        /// </summary>
        bool ConfirmReRigStrategy(out string restoreJson, out bool skipFootReduction)
        {
            restoreJson       = null;
            skipFootReduction = false;

            if (!SourceFbxIsRigged()) return true;   // normal first rig — nothing to ask

            string backup    = OldestRigBackup();
            bool   hasBackup = !string.IsNullOrEmpty(backup);
            string riggedVer = GetRiggedScriptVersion(_avatarObject)
                               ?? GetRiggedScriptVersion(_sourceFbxAsset);

            string head = "This avatar already has an AutoRig Feet rig"
                        + (string.IsNullOrEmpty(riggedVer) ? "" : " (script " + riggedVer + ")")
                        + ". Running now rebuilds it.\n\n"
                        + "Re-applying reduces the foot weights again, on top of the reduction the "
                        + "previous run already made. Repeated re-rigs fade the foot influence "
                        + "toward zero.\n\n";

            string protectLabel = hasBackup ? "Protect my weights" : "Keep current weights";
            string protectHow = hasBackup
                ? "PROTECT: restores the original foot weights from your backup first, then rigs.\n"
                  + "    " + Path.GetFileName(backup) + "\n"
                  + "    Result: the new rig is built from the un-rigged mesh, exactly like the "
                  + "first time.\n\n"
                : "KEEP: no backup was found for this FBX, so the original weights can't be "
                  + "restored.\n    Instead the foot reduction is skipped, so nothing degrades "
                  + "further. The toe bones and their weights are still fully rebuilt; only the "
                  + "foot/toe blend keeps the earlier run's shaping.\n\n";

            int choice = EditorUtility.DisplayDialogComplex(
                "Re-apply AutoRig Feet?",
                head + protectHow
                     + "RE-APPLY AS-IS: rigs without protecting anything. The foot weights are "
                     + "reduced again.",
                protectLabel,            // 0
                "Cancel",                // 1  (middle button = the safe default on Esc)
                "Re-apply as-is");       // 2

            if (choice == 1) return false;

            if (choice == 0)
            {
                if (hasBackup) restoreJson = backup;
                else           skipFootReduction = true;
            }
            return true;
        }

        /// <summary>true when <paramref name="a"/> is an older version than <paramref name="b"/>.</summary>
        static bool IsOlder(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return Version.TryParse(a.TrimStart('v', 'V'), out var va) &&
                   Version.TryParse(b.TrimStart('v', 'V'), out var vb) && va < vb;
        }

        // The "this is already rigged" notice. Says which version built it and whether re-applying
        // would actually gain anything — the whole point of stamping the version in the first place.
        string BuildAlreadyRiggedMessage()
        {
            string installed = LocalPyVersion();

            string what = string.IsNullOrEmpty(_riggedVersion)
                ? "This avatar already has an AutoRig Feet rig (z_CB / Toes_a1 bones found). "
                  + "It was made before the script started recording its version, so it predates "
                  + (string.IsNullOrEmpty(installed) ? "the installed one." : installed + ".")
                : "This avatar already has an AutoRig Feet rig, built with script "
                  + _riggedVersion + ".";

            string advice;
            if (!string.IsNullOrEmpty(_riggedVersion) && !string.IsNullOrEmpty(installed)
                && IsOlder(_riggedVersion, installed))
                advice = "  You have " + installed + " — re-run to update the rig.";
            else if (!string.IsNullOrEmpty(_riggedVersion) && _riggedVersion == installed)
                advice = "  That is the version you have installed, so re-running would rebuild "
                       + "the same rig.";
            else
                advice = "  Re-running rebuilds the rig from scratch.";

            return what + advice
                 + "\n\nThe old rig is removed automatically first, so nothing stacks. When you run, "
                 + "you'll be asked whether to protect your existing foot weights — re-applying "
                 + "reduces them again unless the originals are restored first.";
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

        // The paid script stamps the version that built a rig into two leaf bones named
        // "z_CB ARFv3_9_0_L" / "_R" (dots become underscores — dots are Blender's duplicate-name
        // convention and travel badly through FBX). Bone names are the only thing that reliably
        // survives Blender → FBX → Unity, which is why the version lives in one.
        static readonly System.Text.RegularExpressions.Regex RigVersionMarker =
            new System.Text.RegularExpressions.Regex(
                @"ARFv(\d+)_(\d+)_(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// The script version that produced this avatar's feet rig, or null when unknown — either
        /// it has no rig, or it was rigged before 3.9.0, when nothing recorded the version at all.
        /// </summary>
        static string GetRiggedScriptVersion(GameObject avatar)
        {
            if (avatar == null) return null;
            foreach (Transform t in avatar.GetComponentsInChildren<Transform>(true))
            {
                var m = RigVersionMarker.Match(t.name);
                if (m.Success)
                    return m.Groups[1].Value + "." + m.Groups[2].Value + "." + m.Groups[3].Value;
            }
            return null;
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

            // Only back up a file we are actually about to overwrite.
            //
            // This used to run unconditionally, so Duplicate mode — where Blender exports to a
            // separate "_Rig_Feet.fbx" and the source is never written — left a full copy of an
            // untouched FBX behind on every run. Measured on a real project: three Duplicate-mode
            // runs produced three identical 17.4 MB copies, 51 MB of backups of a file nothing had
            // touched. These are deliberately never rotated, so it grows without bound.
            //
            // The test is the export path itself rather than the Export Mode enum, so it stays
            // correct if ComputeExportPath's rules ever change.
            bool overwritesSource = false;
            try
            {
                overwritesSource = !string.IsNullOrEmpty(_exportPath) &&
                    string.Equals(Path.GetFullPath(_exportPath), Path.GetFullPath(srcAbs),
                                  StringComparison.OrdinalIgnoreCase);
            }
            catch { overwritesSource = true; }   // can't tell — keep the safety net

            if (!overwritesSource)
            {
                UnityEngine.Debug.Log(
                    "[AutoRig Feet Distributor] Full FBX backup skipped: this run writes to "
                    + Path.GetFileName(_exportPath) + " and does not modify the source FBX.");
                return;
            }

            string backupDir = Path.Combine(Path.GetDirectoryName(srcAbs), "_Backups");
            Directory.CreateDirectory(backupDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string name  = Path.GetFileNameWithoutExtension(srcAbs);
            string dest  = Path.Combine(backupDir, $"{name}_backup_{stamp}.fbx");
            File.Copy(srcAbs, dest, overwrite: false);

            // Copy the .meta so the backup keeps the original's import settings (rig type, scale,
            // material mapping) — restoring an FBX without them is close to useless.
            //
            // But give it a FRESH guid. A straight copy duplicates the original's guid, and Unity
            // then logs "GUID [...] conflicts with '<original>.fbx' (current owner). Assigning a new
            // guid." on the next import, once per backup, forever. Caught by the run log's console
            // capture on the very first real run.
            string metaSrc = srcAbs + ".meta";
            if (File.Exists(metaSrc))
            {
                try
                {
                    string meta = File.ReadAllText(metaSrc);
                    meta = System.Text.RegularExpressions.Regex.Replace(
                        meta, @"(?m)^guid:\s*[0-9a-fA-F]{32}\s*$",
                        "guid: " + Guid.NewGuid().ToString("N"));
                    File.WriteAllText(dest + ".meta", meta);
                }
                catch (Exception ex)
                {
                    // Import settings are a nice-to-have; the FBX itself is the backup that matters.
                    UnityEngine.Debug.LogWarning(
                        "[AutoRig Feet Distributor] Backup .meta not written: " + ex.Message);
                }
            }
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

        /// <summary>
        /// Absolute path → project-relative AssetDatabase path ("Assets/…"), or the path unchanged
        /// when it lies outside the project.
        ///
        /// Returns FORWARD slashes for the relative case. It used to inherit whatever separators the
        /// input had, which on Windows meant `Assets\! Shugan\…`. AssetDatabase mostly tolerates
        /// that, so it went unnoticed — until a caller reasonably tested the result with
        /// `StartsWith("Assets/")` and it silently never matched.
        ///
        /// Both sides are normalised before comparison too: the inputs arrive from a mix of
        /// `Application.dataPath` (forward slashes), `Path.GetFullPath` (backslashes) and
        /// `EditorUtility.OpenFilePanel` (forward slashes), so a raw comparison depends on which
        /// helper happened to produce the path.
        /// </summary>
        string ToProjectRelative(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return "";
            string root = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
            string p    = absPath.Replace('\\', '/');
            if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return absPath;
            return p.Substring(root.Length).TrimStart('/');
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

            // A run that had started logging must not end with an open log and no report. This
            // catches the post-Blender steps — the FBX swap and prefab wiring throw into SetError,
            // and those failures are exactly why the log is kept open past the Blender process.
            //
            // Guarded on _runLogger, which is null both before a run starts (SetError is also used
            // for pre-flight validation, which must not overwrite the previous run's report) and
            // after FinishRunReport has already run — so the paths that finalize then call SetError
            // do not write twice.
            if (_runLogger == null || _runReport == null) return;

            if (!_runReport.HasFatal)
                _runReport.AddIssue("U_STEP_FAILED", "fatal", msg,
                    "This happened after Blender finished, so the exported FBX exists — check the "
                    + "run log. Your original FBX is backed up in the _Backups folder next to it.");
            _runReport.logPath        = _runLogPath ?? "";
            _runReport.timestampTicks = DateTime.UtcNow.Ticks;
            FinishRunReport();
        }

        static string TruncateLabel(string s, int max)
            => s.Length <= max ? s : "…" + s.Substring(s.Length - (max - 1));
    }
}
