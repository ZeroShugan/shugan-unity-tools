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
    public partial class AutoRigFeetDistributor : EditorWindow
    {
        enum ExportMode { Duplicate, Replace }
        // Order is load-bearing: the choice is persisted as an INT in EditorPrefs
        // (PrefSwapMethod), so reordering these would silently flip existing users' setting.
        // "Experimental" was renamed to "Standard" in place — it is the mode actually relied on —
        // and keeping its index means nobody's stored preference changed meaning.
        enum SwapMethod { Legacy, Standard }
        enum State      { Idle, BlenderRunning, OutfitsRunning, FBXSwapping, AddingPrefabs, Restoring, OutfitsRestoring, Done, Error }
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
        const string PrefRigOnBestShape       = "ShuganTools_ARF_RigOnBestShape";
        const string PrefZeroFeetShapeKeys    = "ShuganTools_ARF_ZeroFeetShapeKeys";
        const string PrefCleanForeignToes     = "ShuganTools_ARF_CleanForeignToes";
        const string PrefUnlinkedPrefabSceneOnly = "ShuganTools_ARF_UnlinkedPrefabSceneOnly";
        const string PrefFootHelper           = "ShuganTools_ARF_FootHelper";
        const string PrefMakeShrinkKeys       = "ShuganTools_ARF_MakeShrinkKeys";

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
        // Serialized so a script recompile does not empty the Target Avatar field. Everything DERIVED
        // from it (rig state, the mesh caches, the foot-bone lists) is not serialized and is rebuilt by
        // the OnEnable re-check below.
        [SerializeField] GameObject _avatarObject;

        // The FBX as it stood before this run, so the re-sync afterwards can tell what the FILE changed
        // from what the USER changed. Not serialized: a run does not survive a domain reload anyway.
        ArmatureSync.Snapshot _armatureBefore;
        HumanDescription      _humanBefore;
        bool                  _hasHumanBefore;
        // Where new bones go when the avatar is an instance of a prefab that has no link to the FBX.
        // Default is to fix the .prefab itself, because its other instances are broken too — measured.
        bool                  _unlinkedPrefabSceneOnly;
        // A helper bone under each foot that carries the foot's weights, so feet can be animated without
        // taking over VRChat's IK. On by default; needs the paid script 3.16.0 or newer.
        bool                  _footHelper = true;
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

        // ─── Foot shape ───────────────────────────────────────────────────────
        // Rig on the foot shape that fits the bones (FootShapeFit), keep the scene avatar's shape-key
        // values, and add a VRCFury toggle that sets the fitting shape. Off = the 1.2.4 behaviour.
        bool _rigOnBestShape = true;
        // Off by default: the foot shape keys list keeps the avatar's values and animates them instead.
        bool _zeroFeetShapeKeys = false;

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
            _exportMode    = (ExportMode)ArfPrefs.GetInt(PrefExportMode, (int)ExportMode.Duplicate);
            _swapMethod     = (SwapMethod)ArfPrefs.GetInt(PrefSwapMethod, (int)SwapMethod.Legacy);
            _autoSwapMethod = ArfPrefs.GetBool(PrefAutoSwapMethod, true);
            _exportSuffix  = ArfPrefs.GetString(PrefSuffix, "Rig_Feet");
            _exportFolder  = ArfPrefs.GetString(PrefExportFolder, "");
            _advancedFoldout = ArfPrefs.GetBool(PrefAdvanced, false);

            string fbxPath = ArfPrefs.GetString(PrefFbxPath, "");
            if (!string.IsNullOrEmpty(fbxPath))
            {
                _sourceFbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                if (_sourceFbxAsset != null) RefreshMeshNames();
            }
            _selectedMeshIndex = Mathf.Clamp(
                ArfPrefs.GetInt(PrefMeshIndex, 0), 0, Mathf.Max(0, _meshNames.Length - 1));

            string garments = ArfPrefs.GetString(PrefGarments, "");
            _garmentMeshNames = string.IsNullOrEmpty(garments)
                ? new List<string>()
                : garments.Split('|').Where(s => !string.IsNullOrEmpty(s)).ToList();

            _backupEnabled = ArfPrefs.GetBool(PrefBackupEnabled, true);
            _autoMapFeet   = ArfPrefs.GetBool(PrefAutoMapFeet, true);
            _rigOnBestShape = ArfPrefs.GetBool(PrefRigOnBestShape, true);
            _zeroFeetShapeKeys = ArfPrefs.GetBool(PrefZeroFeetShapeKeys, false);
            _cleanForeignToes = ArfPrefs.GetBool(PrefCleanForeignToes, true);
            _unlinkedPrefabSceneOnly = ArfPrefs.GetBool(PrefUnlinkedPrefabSceneOnly, false);
            _footHelper = ArfPrefs.GetBool(PrefFootHelper, true);
            _makeShrinkKeys = ArfPrefs.GetBool(PrefMakeShrinkKeys, true);
            _timeoutMin    = ArfPrefs.GetInt(PrefTimeoutMin, 10);
            _tab           = (Tab)ArfPrefs.GetInt(PrefTab, (int)Tab.Setup);
            // The contact field persists (so a repeat reporter doesn't retype it) but the message
            // and the consent tick never do — consent is per-send, exactly as in the addon.
            _bugContact    = ArfPrefs.GetString(PrefBugContact, "");
            _runsCacheDirty = true;

            if (_prefabsToAdd.Count == 0)
                foreach (string p in DefaultPrefabPaths)
                    _prefabsToAdd.Add(AssetDatabase.LoadAssetAtPath<GameObject>(p));

            // Auto-detect Blender if not already set
            if (!File.Exists(ArfPrefs.GetString(BlenderBridge.PrefBlenderPath, "")))
                TryAutoDetectBlender(silent: true);

            // The avatar may already be selected in the Hierarchy before this window is opened;
            // adopt it now so the Target Avatar field is filled straight away. Only when the field
            // is empty, so a remembered avatar is never clobbered by an unrelated selection.
            //
            // A remembered one is re-checked instead. OnEnable also runs after every script recompile,
            // which wipes every non-serialized field: the avatar is still in the field but the rig
            // state, the mesh caches and the foot-bone candidate lists are all gone, and nothing used
            // to rebuild them until the user clicked another avatar and back.
            if (_avatarObject == null) TryAdoptSelectedAvatar();
            else OnAvatarChanged();

            // Bring back the last run report for this FBX (survives restarts / domain reloads).
            LoadLastReport();

            RegisterInterruptionHandlers();

            // Once-per-session check whether a newer paid AutoRig .py is published (GitHub
            // version file; stores can't push updates through VCC).
            StartPyVersionCheck();
        }

        // Closing the window mid-run must not leave the log stream open — the file would keep the
        // handle and the folder would look like a run that never ended.
        void OnDisable()
        {
            if (Busy) InterruptRun("the AutoRig Feet window was closed or scripts were reloaded");
            UnregisterInterruptionHandlers();
            if (_runLogger != null)
            {
                _runLogger.Line("[log] tool window closed or scripts reloaded — log ended here");
                CloseRunLog();
            }
        }

        void OnSelectionChange() => TryAdoptSelectedAvatar();

        // Coming back to the window is the moment to retry whatever could not be read before — after a
        // script recompile that is usually the foot-bone lists.
        void OnFocus() => RearmFootCandidates();

        // Pull the currently selected scene object into the Target Avatar field.
        // Called on selection change AND when the window opens: OnSelectionChange only fires on a
        // CHANGE, so selecting the avatar first and opening the tool afterwards used to leave the
        // field empty until you clicked something else.
        void TryAdoptSelectedAvatar()
        {
            if (_state != State.Idle && _state != State.Done && _state != State.Error) return;
            var go = Selection.activeGameObject;
            if (go == null || !go.scene.IsValid()) return;
            go = ResolveAvatarRoot(go);
            if (go == _avatarObject) return;
            _avatarObject = go;
            OnAvatarChanged();
            Repaint();
        }

        void Update()
        {
            // The foot-shape fit is measured on a worker thread; show its answer as soon as it lands.
            if (_fitTask != null && _fitTask.IsCompleted) Repaint();
            // Foot-bone candidates that couldn't be read yet are retried here, not left empty.
            if (FootCandidatesRetryDue)
            {
                RefreshFootCandidates();
                Repaint();
            }

            SyncRecordStage();
            SyncReloadLock();
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
                        _state = AfterBodyRun();   // outfits from other FBXs first, when any are ticked
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
                    if (EvaluateBlenderResult(exitCode, isRestore: true) && CommitRestoreExport())
                    {
                        // A SUCCESSFUL restore goes straight to Done — there is no prefab step to
                        // finalize it — so without this its report was never written and its log
                        // was left open. (Failures were already covered by EvaluateBlenderResult.)
                        // A restore rewrites the FBX just as a run does, so the avatar needs the same
                        // treatment — measured, it came back with 376 bones against 350 bind poses and every
                        // mesh stopped rendering, with the old toe bones still hanging in the armature.
                        string restoredFbx = AssetDatabase.GetAssetPath(_sourceFbxAsset);
                        ResyncArmatureAfterRun(_avatarObject, restoredFbx);
                        RepairHumanoidAfterSkeletonChange(restoredFbx);
                        foreach (var other in OtherOpenSceneUsers(restoredFbx, _avatarObject))
                            ReportArmatureResult(ArmatureSync.Sync(other, restoredFbx, _armatureBefore, UnlinkedPrefabMode));
                        if (_restoreRemoveObjects)
                        {
                            int removedObjects = RemoveArfObjects(_avatarObject, ArfRecord.Load(restoredFbx)?.runs ?? new List<ArfRecord.RunEntry>());
                            if (removedObjects > 0)
                                _runReport.AddIssue("U_ARF_OBJECTS_REMOVED", "info",
                                    removedObjects + " object(s) AutoRig Feet had added were removed from the avatar.");
                        }

                        // Outfits from other FBXs that carry the rig are restored too, one Blender run each.
                        if (BeginOutfitRestores())
                        {
                            SaveRunReport();
                            _state = State.OutfitsRestoring;
                            Repaint();
                            return;
                        }

                        FinishRunReport();
                        _displayProgress  = 1f;
                        _state            = State.Done;
                    SyncRecordStage();
                        _currentStepLabel = "Restore done!";
                        SetStatus("Restore complete — the FBX was reverted (feet rig removed).",
                            MessageType.Info);
                    }
                }
                Repaint();
            }

            if (_state == State.OutfitsRunning)
            {
                _displayProgress = Mathf.Max(_displayProgress, _exportMode == ExportMode.Duplicate ? 0.70f : 0.88f);
                try
                {
                    if (TickOutfits()) _state = State.FBXSwapping;
                }
                catch (Exception ex)
                {
                    // An outfit never stops the body: note it and carry on with the swap.
                    _runReport.AddIssue("U_OUTFIT_FAILED", "warning", "The outfits from other FBXs stopped: " + ex.Message);
                    _state = State.FBXSwapping;
                }
                Repaint();
            }

            if (_state == State.OutfitsRestoring)
            {
                _displayProgress = Mathf.Max(_displayProgress, 0.9f);
                bool finished;
                try { finished = TickOutfits(); }
                catch (Exception ex)
                {
                    _runReport.AddIssue("U_OUTFIT_FAILED", "warning", "The outfit restore stopped: " + ex.Message);
                    finished = true;
                }
                if (finished)
                {
                    FinishOutfitRestores();
                    CleanOutfitTemp(keep: false);
                    FinishRunReport();
                    _displayProgress  = 1f;
                    _state            = State.Done;
                    SyncRecordStage();
                    _currentStepLabel = "Restore done!";
                    SetStatus("Restore complete — the FBX and its outfits were reverted (feet rig removed).",
                        MessageType.Info);
                }
                Repaint();
            }

            if (_state == State.FBXSwapping)
            {
                if (ArfAutomation.Active && ArfAutomation.HaltAt == "swap") { Repaint(); return; }
                try
                {
                    _currentStepLabel = "Swapping FBX into prefab…";
                    _displayProgress  = _exportMode == ExportMode.Duplicate ? 0.75f : 0.90f;
                    SyncRecordStage();
                    Repaint();
                    if (_avatarObject == null)
                        throw new Exception("the avatar was deleted, or its scene closed, while Blender was running. "
                                            + (_exportMode == ExportMode.Replace
                                                ? "The rigged FBX is in place: select the avatar again and use \"Finish it\", "
                                                  + "or \"Restore to original\" in the Backups tab."
                                                : "Your original FBX was not changed."));
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
                    SyncRecordStage();
                    Repaint();
                    if (_resultInstance == null)
                        throw new Exception("the avatar was deleted, or its scene closed, during the run.");
                    // The other rig's toe bones go first: the swap carries their GameObjects over from the
                    // avatar even though Blender removed them from the skeleton.
                    RemoveCleanedToeBoneObjects();
                    // Replace hands the user's own avatar back, and Unity only carries the new bones into it
                    // while it is still a prefab instance of that FBX. Anything else — unpacked, or saved as
                    // its own prefab — keeps the old skeleton and its meshes stop rendering. Do this before
                    // the prefabs are added, so they attach to a complete armature.
                    // Duplicate/Standard grafts missing bones by name but never re-parents one, so a toe the
                    // FBX moved under the foot helper stayed under the foot and would ignore the helper. The
                    // copy points at its own private FBX, which is what it is synced against.
                    string syncFbx = null;
                    if (EffectiveSwapMethod() == SwapMethod.Standard)
                    {
                        if (_resultInstance != null &&
                            HumanoidRigMapping.TryResolveRigFbx(_resultInstance, out string duplicateFbx))
                        {
                            syncFbx = duplicateFbx;
                            if (_recRun != null) { _recRun.privateFbx = duplicateFbx; _rec?.Save(); }
                        }
                    }
                    else if (_exportMode == ExportMode.Replace)
                        syncFbx = ToProjectRelative(_exportPath);
                    if (syncFbx != null)
                        ResyncArmatureAfterRun(_resultInstance, syncFbx);
                    // Outfits from other FBXs: Replace brings each rewritten outfit in step, Duplicate gives the copy
                    // its own copy of each outfit.
                    ApplyExternalOutfits();
                    RunAddPrefabs();
                    // Give the finished avatar a name that describes what it IS, not how it was made.
                    RenameResultInstance();
                    if (_rigOnBestShape)
                    {
                        // The result keeps every shape-key value the avatar had; the foot-shape toggle
                        // is what switches the feet into the rigged shape while the feet system is on.
                        // The toggle comes last, so it lands below the prefabs just added.
                        RestoreSceneBlendShapeWeights();
                        CreateFootShapeToggleAfterRun();
                    }
                    else if (_zeroFeetShapeKeys)
                    {
                        // Zero the ticked feet shape keys on the generated copy (Duplicate mode only —
                        // in Replace mode the user fixes them on the avatar itself via the warning).
                        // Only when the opt-in section asked for it: nothing changes a shape-key value
                        // behind the user's back.
                        ApplyDuplicateShapeKeyFixes();
                    }
                    // End of pipeline: ensure the FINAL scene avatar's FBX has humanoid foot/toes mapped.
                    if (_autoMapFeet) AutoMapHumanoidFeet();
                    // The auto-map reimports the FBX. That should leave the meshes alone — say so rather
                    // than assume it.
                    if (_exportMode == ExportMode.Replace && _resultInstance != null)
                    {
                        var after = ArmatureSync.ValidateOnly(_resultInstance, ToProjectRelative(_exportPath));
                        if (after.unbound.Count > 0) ReportArmatureResult(after);
                    }
                    CheckPrefabLinks();
                    CleanOutfitTemp(keep: false);
                    FinishRunReport();   // re-save: the auto-map may have added warnings
                    // The avatar now HAS a rig (and a fresh version marker). Without this the
                    // "already rigged" notice stayed hidden in Replace mode, where the result is
                    // the same object so avatar-change adoption never fires.
                    RefreshRigState();
                    _backupScanDirty  = true;   // the run just wrote scene and/or FBX backups
                    _displayProgress  = 1f;
                    _state            = State.Done;
                    SyncRecordStage();
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
            string storedBlender = ArfPrefs.GetString(BlenderBridge.PrefBlenderPath, "");
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
                ArfPrefs.SetInt(PrefTab, (int)_tab);
                _runsCacheDirty = true;
                GUI.FocusControl(null);
            }
            EditorGUILayout.Space(6);

            switch (_tab)
            {
                case Tab.Setup:
                    // No separator above Setup when nothing is missing: the block drew nothing.
                    if (DrawDependencyStatus()) Separator();
                    DrawInterruptedRunPanel();
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
            // The run is held back, and the button says why, until the avatar checks have finished:
            // starting before them would rig with a guessed foot bone or without the fitting shape.
            string checking = ready && !busy ? PendingCheckLabel() : null;

            EditorGUI.BeginDisabledGroup(!ready || busy || checking != null);
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = ready && !busy && checking == null ? new Color(0.4f, 0.8f, 0.4f) : Color.white;
            if (GUILayout.Button(busy ? GetBusyLabel() : checking ?? "▶  AutoRig Feet", GUILayout.Height(34)))
                Execute();
            GUI.backgroundColor = prev;
            EditorGUI.EndDisabledGroup();
        }

        // Replace rigs the ORIGINAL avatar. Before anything is written, say what that changes, point at
        // the safe way to try it, and make clear there is a way back.
        bool ConfirmReplaceMode()
        {
            var changes = new StringBuilder();
            changes.Append("• Your FBX is overwritten with the rigged version:\n     ")
                   .Append(AssetDatabase.GetAssetPath(_sourceFbxAsset)).Append('\n');
            changes.Append("• The avatar in the scene gets the new toe bones, and its foot and toe weights are repainted");
            if (TickedOutfits().Any(g => !string.IsNullOrEmpty(g))) changes.Append(" (on the outfits too)");
            changes.Append(".\n");
            var externalOutfits = ScriptSupportsExternalOutfits() ? TickedExternalOutfits().Keys.ToList() : new List<string>();
            if (externalOutfits.Count > 0)
                changes.Append($"• Experimental: {externalOutfits.Count} outfit FBX(s) from other files get the toe bones and "
                               + "are overwritten too (" + string.Join(", ", externalOutfits.Select(Path.GetFileName)) + "), "
                               + "each backed up first.\n");
            int prefabs = _prefabsToAdd.Count(p => p != null);
            if (prefabs > 0)
                changes.Append($"• {prefabs} feet prefab(s) are added under the avatar.\n");
            if (_rigOnBestShape && MakeShrinkKeysNow)
                changes.Append($"• Three shrink shape keys ({FootShapeFit.OwnShrinkPrefix}Toes, Toe Base, Foot) are added to "
                               + "the body mesh, replacing any from an earlier run.\n");
            if (_rigOnBestShape && FootShapeToggle.VRCFuryAvailable)
                changes.Append($"• The \"{FootShapeToggle.ObjectName}\" toggle ({FootShapeToggle.MenuPath}) and a Foot Shrink "
                               + "toggle for each shrink key are added under the avatar, replacing any earlier ones.\n");
            if (_autoMapFeet)
                changes.Append("• Empty humanoid Foot/Toes slots of the FBX are filled in.\n");
            var cleanToes = ForeignToeBonesToClean();
            if (cleanToes.Count > 0)
                changes.Append($"• {cleanToes.Count} toe bone(s) from another rig are removed ({FormatBoneList(cleanToes)}); "
                               + "their weights go onto the foot or toe bone they hang from.\n");

            return ArfAutomation.Confirm(
                ArfAutomation.AskReplaceMode,
                "Replace your original avatar?",
                "Export Mode is set to Replace, so AutoRig Feet will modify your ORIGINAL avatar and its FBX:\n\n"
                + changes
                + "\nNot sure? Set Export Mode to Duplicate to try it safely: it rigs a copy placed next to your "
                + "avatar and leaves the original untouched.\n\n"
                + "Both modes make backups before changing anything: the scene (once it has been saved)"
                + (_backupEnabled ? ", and a rig backup you can restore from the Backups tab" : "")
                + ". Replace also keeps a timestamped copy of the original FBX in the _Backups folder next to it, "
                + "so you can always go back to your original.",
                "Replace my avatar", "Cancel");
        }

        // What the run is still waiting for, as the button label, or null when the checks are done.
        // Also starts the foot-shape check when it hasn't run yet (another tab may be open).
        string PendingCheckLabel()
        {
            if (_avatarObject == null) return null;
            if (FootCandidatesPending) return "⏳  Checking foot bones…";
            if (_rigOnBestShape && _meshNames.Length > 0)
            {
                var smr = FindAvatarSkinnedMesh(_meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)]);
                if (smr != null && smr.sharedMesh != null && smr.sharedMesh.blendShapeCount > 0)
                {
                    EnsureFootShapeFit(smr, out bool measuring);
                    if (measuring) return "⏳  Checking foot shape keys…";
                }
            }
            return null;
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

        // Only what is MISSING is drawn, and when nothing is missing the whole block disappears: a
        // column of green ticks is noise on every repaint of a project that is already set up. The paid
        // bundle's update notice is the one thing that still shows through on a complete install.
        // Returns true when it drew something, so the caller can skip the separator under it.
        bool DrawDependencyStatus()
        {
            bool anyMissing = !_depBlender || !_depVRCFury || !_depAutoRigScript;
            if (!anyMissing)
            {
                if (!PyUpdateAvailable(out _, out _)) return false;
                DrawPaidScriptVersionRow();
                return true;
            }

            GUILayout.Label("Requirements", EditorStyles.boldLabel);

            // Blender
            if (!_depBlender) DrawMissingDep("Blender 4.0+",
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
            if (!_depVRCFury) DrawMissingDep("VRCFury",
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
            if (!_depAutoRigScript) DrawMissingDep("AutoRig Feet Script (paid)",
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
            return true;
        }

        void DrawMissingDep(string label, bool required, Action notFoundExtra)
        {
            EditorGUILayout.BeginHorizontal();
            Color c = GUI.color;
            GUI.color = required ? Color.red : Color.yellow;
            GUILayout.Label($"✗  {label}", EditorStyles.miniLabel, GUILayout.Width(200));
            GUI.color = c;
            EditorGUILayout.EndHorizontal();
            notFoundExtra?.Invoke();
        }

        // ─── Main section ──────────────────────────────────────────────────────

        void DrawMainSection()
        {
            GUILayout.Label("Setup", EditorStyles.boldLabel);

            // Target Avatar
            EditorGUI.BeginChangeCheck();
            _avatarObject = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Target Avatar",
                    "The avatar to rig. Clicking the avatar or anything inside it in the Hierarchy fills this in."),
                _avatarObject, typeof(GameObject), allowSceneObjects: true);
            if (EditorGUI.EndChangeCheck())
            {
                _avatarObject = ResolveAvatarRoot(_avatarObject);
                OnAvatarChanged();
            }

            // Root check
            if (_avatarObject != null && !IsRootObject(_avatarObject))
            {
                Color c = GUI.color; GUI.color = Color.red;
                EditorGUILayout.HelpBox(
                    "⚠️  This avatar sits inside another object. Drag it to the top level of the Hierarchy first.",
                    MessageType.Error);
                GUI.color = c;
            }

            // Out of step with its own FBX: an earlier Replace run on an unpacked avatar, or a file someone
            // swapped in Explorer. Unity reports neither, so this is the only place the user finds out.
            string armatureProblem = ArmatureProblem();
            if (armatureProblem != null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox("This avatar no longer matches its FBX. " + armatureProblem,
                                        MessageType.Warning);
                if (GUILayout.Button(
                        new GUIContent("Re-sync\narmature",
                            "Add the bones the FBX has, re-parent the ones it moved, and re-bind the meshes "
                            + "to the new skeleton."),
                        GUILayout.Width(92), GUILayout.Height(40)))
                    ResyncArmatureManual();
                EditorGUILayout.EndHorizontal();
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
                    ArfPrefs.SetInt(PrefMeshIndex, _selectedMeshIndex);

                string curMeshName = _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)];
                var curSmr = FindAvatarSkinnedMesh(curMeshName);
                var pickedSmr = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                    curSmr, typeof(SkinnedMeshRenderer), true, GUILayout.Width(RefFieldWidth));
                if (pickedSmr != curSmr && pickedSmr != null && pickedSmr.sharedMesh != null)
                {
                    int idx = Array.FindIndex(_meshNames, m => string.Equals(
                        m, pickedSmr.sharedMesh.name, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        _selectedMeshIndex = idx;
                        ArfPrefs.SetInt(PrefMeshIndex, _selectedMeshIndex);
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

            // Export Mode
            EditorGUI.BeginChangeCheck();
            _exportMode = (ExportMode)EditorGUILayout.EnumPopup(
                new GUIContent("Export Mode",
                    "Duplicate: new FBX + new prefab alongside the original.\n" +
                    "Replace: overwrites original FBX in place (auto-backup created)."),
                _exportMode);
            if (EditorGUI.EndChangeCheck())
                ArfPrefs.SetInt(PrefExportMode, (int)_exportMode);

            // Garments right under the export settings.
            DrawGarmentSection();

            // The foot shape keys, the toe bones from another rig and the old set-to-0 list are all
            // decided for you, and all three read as walls of checkboxes to someone rigging their first
            // avatar — so they live in Advanced Settings, and Setup stays the four things that must be
            // right before a run.

            // Only the FBX that could NOT be found is worth a line. The green "auto-detected: <path>"
            // badge confirmed something the user never doubted, on every repaint.
            if (_avatarObject != null && IsRootObject(_avatarObject))
            {
                EditorGUILayout.Space(2);
                if (_sourceFbxAsset == null)
                {
                    Color c = GUI.color; GUI.color = Color.yellow;
                    EditorGUILayout.HelpBox(
                        "FBX not detected — set it manually in Advanced Settings.",
                        MessageType.None);
                    GUI.color = c;
                }
            }

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
        // Returns true when it drew something, so the section around it can say that a mesh is clean.
        bool DrawFeetShapeKeyWarning(string meshName, string label)
        {
            if (_avatarObject == null || string.IsNullOrEmpty(meshName)) return false;

            var smr = FindAvatarSkinnedMesh(meshName);
            if (smr == null || smr.sharedMesh == null) return false;
            var mesh = smr.sharedMesh;
            if (mesh.blendShapeCount == 0) return false;

            // Cheap pass first: only enabled shape keys can cause the problem.
            bool anyActive = false;
            for (int i = 0; i < mesh.blendShapeCount && !anyActive; i++)
                if (Mathf.Abs(smr.GetBlendShapeWeight(i)) > 0.01f) anyActive = true;
            if (!anyActive) return false;

            var feetVerts = GetFeetVerts(smr, out string note);
            if (feetVerts == null || feetVerts.Count == 0)
            {
                // Only worth saying for the body mesh; garments legitimately have no foot weights.
                if (string.IsNullOrEmpty(note) || label != BodyMeshLabel) return false;
                EditorGUILayout.HelpBox(
                    "Couldn't check the active shape keys against the feet: " + note + ".",
                    MessageType.Info);
                return true;
            }

            var offenders = GetFeetShapeOffenders(smr);
            if (offenders.Count == 0) return false;

            // With the foot-shape option on, the new avatar keeps every shape-key value, so there is no
            // "set to 0 on the duplicate" any more: only the explicit Fix-to-0 buttons remain.
            bool duplicating = _exportMode == ExportMode.Duplicate && !_rigOnBestShape;

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
                _rigOnBestShape
                    ? "These shape keys are switched on and they move the feet. The rig is built with "
                      + "every shape key at 0 and the avatar keeps these values, so while they are on the "
                      + "toe bones sit away from the feet you see. Set them to 0 below if you don't need "
                      + "them (this changes the avatar in the scene)."
                : duplicating
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
                    + "each one). Fix those too — if the body is set to 0 but an outfit is not, the "
                    + "outfit stops lining up with the rigged body the moment it is shown.",
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
            return true;
        }

        // ─── Set feet shape keys to 0 (opt-in) ─────────────────────────────────
        // What AutoRig Feet used to push on its own: the switched-on shape keys that move the feet,
        // with a button to zero them, because the rig is built at Basis. The foot shape keys list above
        // does that job without changing the avatar — it keeps the values and animates them with the
        // feet — so this is off by default and lives here, for someone who would rather the shape keys
        // simply went away.
        void DrawShapeKeyZeroSection()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUI.BeginChangeCheck();
            _zeroFeetShapeKeys = EditorGUILayout.ToggleLeft(
                new GUIContent("Set feet shape keys to 0",
                    "Lists the shape keys that are switched on and move the feet, so you can set them "
                    + "to 0 before rigging.\n\nUsually unnecessary: with Foot shape keys on, the rig is "
                    + "built with every shape key at 0, the avatar keeps its own values, and the toggle "
                    + "sets them while the feet are animated."),
                _zeroFeetShapeKeys, EditorStyles.boldLabel);
            if (EditorGUI.EndChangeCheck())
                ArfPrefs.SetBool(PrefZeroFeetShapeKeys, _zeroFeetShapeKeys);

            if (!_zeroFeetShapeKeys)
                EditorGUILayout.LabelField(
                    _rigOnBestShape
                        ? "Not needed while Foot shape keys is on: its toggle sets those keys for you."
                        : "Shape keys that move the feet are left exactly as they are.",
                    EditorStyles.wordWrappedMiniLabel);
            else
            {
                bool drew = false;
                if (_meshNames.Length > 0)
                    drew = DrawFeetShapeKeyWarning(
                        _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)], BodyMeshLabel);
                // Garments have their own shape keys, and one that moves the feet breaks the
                // transferred toe weights the same way it breaks the body.
                foreach (var g in TickedOutfits())
                    if (!string.IsNullOrEmpty(g)) drew |= DrawFeetShapeKeyWarning(g, g);
                if (!drew)
                    EditorGUILayout.LabelField("No shape key that moves the feet is switched on.",
                        EditorStyles.miniLabel);
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
            foreach (var g in TickedOutfits())
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

        // ─── Foot shape: rig on the shape that fits the bones ──────────────────
        // FootShapeFit measures which of the body's shape keys, each at 0 or 100, put the avatar's own
        // foot and toe joints inside the mesh. When that is not the Basis (Maya's Basis is a high heel),
        // Blender builds the rig on the fitting shape and a VRCFury toggle sets it while the feet system
        // is on. Keys switched on in the scene that push the feet off their bones get a 0 row in that
        // toggle, and the avatar itself keeps every value it had.

        FootShapeFit.Input  _fitInput;      // mesh, bones and shape-key deltas; rebuilt when those change
        string              _fitInputKey;
        FootShapeFit.Result _fit;           // last answer, valid for _fitKey
        string              _fitKey;
        System.Threading.Tasks.Task<FootShapeFit.Result> _fitTask;
        string              _fitTaskKey;
        // Rows whose tick was changed from its default, by shape-key name.
        readonly HashSet<string> _fitRowFlipped = new HashSet<string>();
        // Foot Shrink rows whose tick was changed from its default (ticked when outfits are ticked).
        readonly HashSet<string> _shrinkRowFlipped = new HashSet<string>();
        // On: the run builds its own shrink shape keys on the body mesh, so the feet can be flattened under
        // an outfit whatever the creator did or didn't ship.
        bool _makeShrinkKeys = true;
        Vector2 _footShrinkScroll;
        Vector2 _footShapeScroll;
        const float FootShapeListMaxHeight = 150f;   // about seven rows, then it scrolls

        // ─── Toe bones from another rig (ToeRigDetect) ─────────────────────────
        bool _cleanForeignToes = true;
        readonly HashSet<Transform> _foreignToeUnticked = new HashSet<Transform>();
        readonly List<Transform>    _foreignToeAdded    = new List<Transform>();   // null = empty slot
        Vector2 _foreignToeScroll;
        string  _cleanToeBonesJson;   // chosen in Execute; null = cleaning switched off
        List<string> _cleanToeBoneNames = new List<string>();   // the same bones, for the scene-side pass
        // Chosen in Execute, used once the run is back.
        string _rigShapeJson;
        List<FootShapeFit.ToggleRow> _plannedToggleRows;
        List<string> _plannedShrinkKeys;
        BlendShapeWeightSnapshot.Snapshot _blendShapeSnapshot;

        void ResolveFootTransforms(out Transform footL, out Transform footR, out Transform toeL, out Transform toeR)
        {
            footL = footR = toeL = toeR = null;
            if (_avatarObject == null) return;
            var animator = _avatarObject.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.isHuman)
            {
                footL = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                footR = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                toeL  = animator.GetBoneTransform(HumanBodyBones.LeftToes);
                toeR  = animator.GetBoneTransform(HumanBodyBones.RightToes);
            }
            // A manual foot pick wins, as it does in Blender. The humanoid toe is only kept when it
            // actually hangs under the picked foot.
            if (!string.IsNullOrEmpty(_footOverrideL))
            {
                var t = FindAvatarBone(_footOverrideL);
                if (t != null) { footL = t; if (toeL != null && !toeL.IsChildOf(t)) toeL = null; }
            }
            if (!string.IsNullOrEmpty(_footOverrideR))
            {
                var t = FindAvatarBone(_footOverrideR);
                if (t != null) { footR = t; if (toeR != null && !toeR.IsChildOf(t)) toeR = null; }
            }

            // The humanoid Toes slot is optional, and plenty of bases ship with it empty even though
            // the armature has a perfectly good toe bone — that is exactly the avatar AutoRig Feet is
            // for. Blender finds the toe by NAME (its TOE_PRIMARY keywords), so Unity has to as well:
            // without it the foot-shape check has no joint below the ankle to measure against and
            // skips a base it could have answered for.
            if (toeL == null) toeL = FindToeUnder(footL);
            if (toeR == null) toeR = FindToeUnder(footR);
        }

        // The shallowest toe-named bone under a foot. Tip markers (*_end) and bones carrying a renderer
        // are not joints. Shallowest wins, so a toe rig someone added underneath (Toes_1_a1_L…) can
        // never beat the base's own toe bone.
        static Transform FindToeUnder(Transform foot)
        {
            if (foot == null) return null;
            Transform best = null;
            int bestDepth = int.MaxValue;

            void Walk(Transform node, int depth)
            {
                for (int i = 0; i < node.childCount; i++)
                {
                    var c = node.GetChild(i);
                    string n = c.name.ToLowerInvariant();
                    bool isEnd = n.EndsWith("_end") || n.EndsWith(".end") || n.EndsWith(" end");
                    if (!isEnd && depth < bestDepth && c.GetComponent<Renderer>() == null &&
                        HumanoidRigMapping.NameLooksLikeToe(c.name))
                    {
                        best = c;
                        bestDepth = depth;
                    }
                    Walk(c, depth + 1);
                }
            }

            Walk(foot, 0);
            return best;
        }

        // Which renderer, mesh and bones: what the expensive capture depends on.
        static string FootShapeGeometryKey(SkinnedMeshRenderer smr, Transform footL, Transform footR,
                                           Transform toeL, Transform toeR)
        {
            int Id(UnityEngine.Object o) => o != null ? o.GetInstanceID() : 0;
            return $"{Id(smr)}|{Id(smr.sharedMesh)}|{Id(footL)}|{Id(footR)}|{Id(toeL)}|{Id(toeR)}";
        }

        // The toggle rows depend on which shape keys are switched on, so the values are part of the question.
        static string FootShapeWeightsKey(SkinnedMeshRenderer smr)
        {
            var sb = new StringBuilder();
            var mesh = smr.sharedMesh;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                float w = smr.GetBlendShapeWeight(i);
                if (Mathf.Abs(w) > 0.01f)
                    sb.Append(i).Append('=')
                      .Append(w.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            }
            return sb.ToString();
        }

        // Main-thread capture, reusing the mesh read when only shape-key values changed.
        FootShapeFit.Input PrepareFootShapeInput(SkinnedMeshRenderer smr, Transform footL, Transform footR,
                                                 Transform toeL, Transform toeR, string geometryKey)
        {
            if (_fitInput == null || _fitInputKey != geometryKey)
            {
                _fitInput    = FootShapeFit.Capture(smr, footL, footR, toeL, toeR, _avatarObject.transform);
                _fitInputKey = geometryKey;
            }
            else
            {
                foreach (var k in _fitInput.keys) k.sceneWeight = smr.GetBlendShapeWeight(k.index);
            }
            return _fitInput;
        }

        // The answer for this body, or null while it is still being measured in the background.
        FootShapeFit.Result EnsureFootShapeFit(SkinnedMeshRenderer smr, out bool measuring)
        {
            measuring = false;
            ResolveFootTransforms(out var footL, out var footR, out var toeL, out var toeR);
            string geometryKey = FootShapeGeometryKey(smr, footL, footR, toeL, toeR);
            string key = geometryKey + "#" + FootShapeWeightsKey(smr);
            if (_fit != null && _fitKey == key) return _fit;

            if (_fitTask != null)
            {
                if (!_fitTask.IsCompleted) { measuring = true; return null; }
                if (_fitTaskKey == key)
                {
                    _fit = _fitTask.IsFaulted
                        ? new FootShapeFit.Result
                          { error = "the check failed (" + _fitTask.Exception?.GetBaseException().Message + ")" }
                        : _fitTask.Result;
                    _fitKey = key;
                }
                _fitTask = null;
                if (_fitKey == key) return _fit;
            }

            // The input is only touched here, and never while a measurement is still running on it.
            var input = PrepareFootShapeInput(smr, footL, footR, toeL, toeR, geometryKey);
            _fitTaskKey = key;
            _fitTask    = System.Threading.Tasks.Task.Run(() => FootShapeFit.Run(input));
            measuring   = true;
            return null;
        }

        // The same answer, right now: Execute can't wait for the background check to come round.
        FootShapeFit.Result FootShapeFitNow(string bodyMeshName)
        {
            var smr = FindAvatarSkinnedMesh(bodyMeshName);
            if (smr == null || smr.sharedMesh == null || _avatarObject == null) return null;
            ResolveFootTransforms(out var footL, out var footR, out var toeL, out var toeR);
            string geometryKey = FootShapeGeometryKey(smr, footL, footR, toeL, toeR);
            string key = geometryKey + "#" + FootShapeWeightsKey(smr);
            if (_fit != null && _fitKey == key) return _fit;
            if (_fitTask != null)
            {
                try { _fitTask.Wait(); } catch { }   // never two measurements over the same input
                if (_fitTaskKey == key && !_fitTask.IsFaulted)
                {
                    _fit = _fitTask.Result;
                    _fitKey = key;
                    _fitTask = null;
                    return _fit;
                }
                _fitTask = null;
            }
            try
            {
                _fit = FootShapeFit.Run(PrepareFootShapeInput(smr, footL, footR, toeL, toeR, geometryKey));
            }
            catch (Exception e)
            {
                _fit = new FootShapeFit.Result { error = "the check failed (" + e.Message + ")" };
            }
            _fitKey = key;
            return _fit;
        }

        bool FootShapeRowIncluded(FootShapeFit.ToggleRow row) =>
            row.includedByDefault != _fitRowFlipped.Contains(row.key);

        List<FootShapeFit.ToggleRow> IncludedFootShapeRows(FootShapeFit.Result fit)
        {
            var rows = new List<FootShapeFit.ToggleRow>();
            if (fit?.rows != null)
                foreach (var r in fit.rows)
                    if (!r.IsFootShrink && FootShapeRowIncluded(r)) rows.Add(r);
            return rows;
        }

        // Foot shrink keys flatten the skin under shoes and socks. They only make sense with an outfit on the feet,
        // so they are ticked by default exactly when an outfit is ticked for the run.
        /// <summary>The switch, and a script new enough to act on it (3.17.0). With an older script the switch
        /// would only hide the creator's keys and add a warning to every run.</summary>
        bool MakeShrinkKeysNow => _makeShrinkKeys && ScriptSupportsShrinkKeys();

        bool ShrinkRowsTickedByDefault()
        {
            // With our own keys on there is no reason to reach for the creator's: they are listed to pick by
            // hand, not ticked. Off, they are the only shrink there is, and an outfit is what makes them useful.
            if (MakeShrinkKeysNow) return false;
            try { return TickedOutfits().Count > 0 || TickedExternalOutfits().Count > 0; }
            catch { return false; }
        }

        bool FootShrinkRowIncluded(FootShapeFit.ToggleRow row, bool byDefault) =>
            byDefault != _shrinkRowFlipped.Contains(row.key);

        List<string> IncludedShrinkKeys(FootShapeFit.Result fit)
        {
            var keys = new List<string>();
            if (fit?.rows == null) return keys;
            bool byDefault = ShrinkRowsTickedByDefault();
            foreach (var r in fit.rows)
                if (r.IsFootShrink && FootShrinkRowIncluded(r, byDefault)) keys.Add(r.key);
            return keys;
        }

        // {"High heeled": 100} for the rows set to 100. The 0 rows are not sent: Blender builds the rig
        // with every other shape key at 0 anyway.
        static string RigShapeJson(List<FootShapeFit.ToggleRow> rows)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var r in rows)
            {
                if (r.value <= 0f) continue;
                if (!first) sb.Append(", ");
                first = false;
                sb.Append('"').Append(JsonEscape(r.key)).Append("\": ")
                  .Append(r.value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.Append('}').ToString();
        }

        static string JsonEscape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // The foot shape list: every shape key of the body that moves the feet, ticked when the ARF Foot
        // Shape toggle will set it (to the value shown).
        bool DrawFootShapeSection(string bodyMeshName)
        {
            if (_avatarObject == null) return false;
            var smr = FindAvatarSkinnedMesh(bodyMeshName);
            if (smr == null || smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0) return false;

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            // The feature's on/off switch is the list's own title, so it sits where its effect is seen.
            EditorGUI.BeginChangeCheck();
            _rigOnBestShape = EditorGUILayout.ToggleLeft(
                new GUIContent("Foot shape keys",
                    "On: finds the shape keys that put the avatar's own foot bones inside the feet, rigs the toes "
                    + "on that shape, keeps the avatar's shape-key values, and adds a VRCFury toggle ("
                    + FootShapeToggle.MenuPath + ") that sets the ticked keys to the value shown.\n\n"
                    + "Off: rigs on the Basis and adds no toggle. The shape keys that move the feet are then "
                    + "only listed under \"Set feet shape keys to 0\", for you to zero yourself."),
                _rigOnBestShape, EditorStyles.boldLabel);
            if (EditorGUI.EndChangeCheck())
                ArfPrefs.SetBool(PrefRigOnBestShape, _rigOnBestShape);
            EditorGUILayout.LabelField("Toggles the ticked blendshapes when you animate the feet.",
                EditorStyles.miniLabel);

            if (!_rigOnBestShape)
            {
                EditorGUILayout.EndVertical();
                return false;   // off: the old shape-key warning speaks for the body
            }

            var fit = EnsureFootShapeFit(smr, out bool measuring);
            if (measuring || fit == null || fit.error != null)
            {
                EditorGUILayout.LabelField(
                    measuring || fit == null ? "Checking the shape keys…" : "Check skipped: " + fit.error + ".",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
                return measuring;
            }

            bool hasToggle = FootShapeToggle.FindAll(_avatarObject).Count > 0;

            var shapeRows  = fit.rows.Where(r => !r.IsFootShrink).ToList();
            var shrinkRows = fit.rows.Where(r => r.IsFootShrink).ToList();
            if (shapeRows.Count == 0)
            {
                EditorGUILayout.LabelField("No shape key moves the feet.", EditorStyles.miniLabel);
            }
            else
            {
                // Fixed height, scrolling: some bases have fifteen keys touching the feet, and the list
                // must not push the rest of the settings off the window.
                float rowHeight = EditorGUIUtility.singleLineHeight + 2f;
                _footShapeScroll = EditorGUILayout.BeginScrollView(_footShapeScroll,
                    GUILayout.Height(Mathf.Min(shapeRows.Count * rowHeight + 6f, FootShapeListMaxHeight)));
                foreach (var row in shapeRows)
                {
                    EditorGUILayout.BeginHorizontal();
                    bool on = FootShapeRowIncluded(row);
                    string nowValue = Mathf.Abs(row.sceneWeight - row.value) > 0.01f
                        ? $"   (now {row.sceneWeight:0.#})" : "";
                    bool ticked = EditorGUILayout.ToggleLeft(
                        new GUIContent($"{row.key}  →  {row.value:0}{nowValue}", FootShapeRowTooltip(row)), on);
                    if (ticked != on && !_fitRowFlipped.Remove(row.key)) _fitRowFlipped.Add(row.key);
                    GUILayout.Label(FootShapeRowLabel(row), EditorStyles.miniLabel, GUILayout.Width(110));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(new GUIContent("Foot Shrink (one menu toggle each)",
                    "Flattening the skin of the feet under shoes and socks, so it can't poke through. Each one gets "
                    + "its own VRCFury toggle at " + FootShapeToggle.ShrinkMenuPrefix + "<name>."),
                EditorStyles.miniBoldLabel);
            DrawMakeShrinkKeysOption();

            if (shrinkRows.Count > 0)
            {
                EditorGUILayout.LabelField(new GUIContent(
                        MakeShrinkKeysNow ? "The creator's own shrink keys (tick to add one as well)"
                                        : "The creator's own shrink keys",
                        "Shrink keys the creator made to flatten the feet or toes under shoes and socks. Each ticked "
                        + "key gets its own VRCFury toggle that sets it to 100, at "
                        + FootShapeToggle.ShrinkMenuPrefix + "<key>."),
                    EditorStyles.miniLabel);
                bool byDefault = ShrinkRowsTickedByDefault();
                float shrinkRowHeight = EditorGUIUtility.singleLineHeight + 2f;
                _footShrinkScroll = EditorGUILayout.BeginScrollView(_footShrinkScroll,
                    GUILayout.Height(Mathf.Min(shrinkRows.Count * shrinkRowHeight + 6f, FootShapeListMaxHeight)));
                foreach (var row in shrinkRows)
                {
                    EditorGUILayout.BeginHorizontal();
                    bool on = FootShrinkRowIncluded(row, byDefault);
                    bool ticked = EditorGUILayout.ToggleLeft(new GUIContent(row.key + "  →  100",
                        $"Collapses {row.collapsedShare * 100f:0}% of the skin it moves, {row.footShare * 100f:0}% of it "
                        + "around the feet and toes. Menu: " + FootShapeToggle.ShrinkMenuPath(row.key)), on);
                    if (ticked != on && !_shrinkRowFlipped.Remove(row.key)) _shrinkRowFlipped.Add(row.key);
                    GUILayout.Label("foot shrink", EditorStyles.miniLabel, GUILayout.Width(110));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
                if (!byDefault && !MakeShrinkKeysNow)
                    EditorGUILayout.LabelField("Not ticked by default: no outfit is ticked for the run.",
                        EditorStyles.wordWrappedMiniLabel);
            }
            else if (!MakeShrinkKeysNow)
                EditorGUILayout.LabelField("This body has no shrink shape key of its own.",
                    EditorStyles.wordWrappedMiniLabel);

            if (fit.coarse)
                EditorGUILayout.LabelField(
                    "There is no toe bone under the feet yet, so the fit is judged from the ankle joints "
                    + "alone — the list of keys above is complete either way.",
                    EditorStyles.wordWrappedMiniLabel);

            if (fit.NeedsShape)
            {
                var lacking = new List<string>();
                foreach (var g in TickedOutfits())
                {
                    if (string.IsNullOrEmpty(g)) continue;
                    var gs = FindAvatarSkinnedMesh(g);
                    if (gs == null || gs.sharedMesh == null) continue;
                    var missing = fit.combo.Where(k => gs.sharedMesh.GetBlendShapeIndex(k) < 0).ToList();
                    if (missing.Count > 0) lacking.Add($"{g} (no {string.Join(", ", missing)})");
                }
                if (lacking.Count > 0)
                    EditorGUILayout.LabelField(
                        "⚠ No " + string.Join(", ", fit.combo) + " on: " + string.Join("; ", lacking)
                        + " — won't follow the feet when the toggle is on.",
                        EditorStyles.wordWrappedMiniLabel);
            }

            if (!FootShapeToggle.VRCFuryAvailable)
                EditorGUILayout.LabelField(
                    "VRCFury is not installed, so no toggle can be added: animate the ticked keys yourself.",
                    EditorStyles.wordWrappedMiniLabel);
            else if (hasToggle)
                DrawFootShapeToggleStatus();

            EditorGUILayout.EndVertical();
            return true;
        }

        void DrawFootShapeToggleStatus()
        {
            if (FootShapeToggle.IsLastChild(_avatarObject))
            {
                EditorGUILayout.LabelField("✓ The AutoRig Feet toggles are the last objects under the avatar.",
                    EditorStyles.miniLabel);
                return;
            }
            EditorGUILayout.HelpBox(
                "The AutoRig Feet toggles (Foot Shape, Foot Shrink) are no longer the last objects under the avatar. "
                + "A VRCFury toggle below them that sets the same shape keys would override them.",
                MessageType.Warning);
            if (GUILayout.Button("Move to bottom", GUILayout.Height(20)))
                EditorApplication.delayCall += () =>
                {
                    if (_avatarObject != null) FootShapeToggle.MoveToBottom(_avatarObject);
                    Repaint();
                };
        }

        // ─── Clean existing toe bones ──────────────────────────────────────────
        // Toe bones another rig added under the feet (ToeRigDetect), listed so they can be removed before
        // rigging: their weights are added onto the foot or toe bone they hang from, in Blender.

        List<Transform> DetectForeignToeBones()
        {
            if (_avatarObject == null) return new List<Transform>();
            ResolveFootTransforms(out var footL, out var footR, out var toeL, out var toeR);
            try { return ToeRigDetect.Find(_avatarObject.transform, footL, footR, toeL, toeR); }
            catch { return new List<Transform>(); }
        }

        // Why a bone can't be added by hand, or null when it can.
        string ForeignToeAddProblem(Transform t)
        {
            if (t == null) return null;
            if (_avatarObject == null || !t.IsChildOf(_avatarObject.transform)) return "it is not part of this avatar";
            ResolveFootTransforms(out var footL, out var footR, out var toeL, out var toeR);
            if (t == footL || t == footR || t == toeL || t == toeR)
                return "it is the avatar's own foot or toe bone, which the rig needs";
            if (ToeRigDetect.IsAutoRigBone(t.name)) return "it is an AutoRig Feet bone, which the rig replaces anyway";
            bool underFoot = (footL != null && t.IsChildOf(footL)) || (footR != null && t.IsChildOf(footR));
            return underFoot ? null : "it is not under a foot bone";
        }

        // The bones a run removes: the ticked detected ones plus the valid ones added by hand.
        List<Transform> ForeignToeBonesToClean()
        {
            var list = new List<Transform>();
            if (!_cleanForeignToes || _avatarObject == null) return list;
            foreach (var t in DetectForeignToeBones())
                if (!_foreignToeUnticked.Contains(t)) list.Add(t);
            foreach (var t in _foreignToeAdded)
                if (t != null && !list.Contains(t) && ForeignToeAddProblem(t) == null) list.Add(t);
            return list;
        }

        static string FormatBoneList(List<Transform> bones) =>
            string.Join(", ", bones.Take(6).Select(b => b.name)) + (bones.Count > 6 ? $" and {bones.Count - 6} more" : "");

        bool ConfirmForeignToeCleaning(List<Transform> bones) =>
            ArfAutomation.Confirm(
                ArfAutomation.AskForeignToes,
                "Clean existing toe bones?",
                $"{bones.Count} toe bone(s) from another rig will be removed from the rigged copy:\n\n"
                + FormatBoneList(bones)
                + "\n\nTheir weights are added onto the foot or toe bone they hang from, so the mesh keeps its "
                + "skinning. Your original avatar is not changed.\n\n"
                + "To keep some of them, untick them in the \"Clean existing toe bones\" list.",
                "Clean and rig", "Cancel");

        void DrawForeignToeSection()
        {
            if (_avatarObject == null) return;
            var detected = DetectForeignToeBones();

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.BeginChangeCheck();
            _cleanForeignToes = EditorGUILayout.ToggleLeft(
                new GUIContent("Clean existing toe bones",
                    "Toe bones another rig added under the feet. The ticked ones are removed before rigging, and "
                    + "their weights are added onto the foot or toe bone they hang from, so no vertex loses its "
                    + "skinning. The avatar's own foot and toe bones are never listed.\n\n"
                    + "Off: nothing is removed (the run report warns if such bones are found)."),
                _cleanForeignToes, EditorStyles.boldLabel);
            if (EditorGUI.EndChangeCheck())
                ArfPrefs.SetBool(PrefCleanForeignToes, _cleanForeignToes);
            EditorGUILayout.LabelField("Removes toe bones from another rig; their weights go to the foot.",
                EditorStyles.miniLabel);

            if (_cleanForeignToes)
            {
                int rows = detected.Count + _foreignToeAdded.Count;
                if (rows == 0)
                {
                    EditorGUILayout.LabelField("No toe bones from another rig found.", EditorStyles.miniLabel);
                }
                else
                {
                    float rowHeight = EditorGUIUtility.singleLineHeight + 2f;
                    _foreignToeScroll = EditorGUILayout.BeginScrollView(_foreignToeScroll,
                        GUILayout.Height(Mathf.Min(rows * rowHeight + 6f, FootShapeListMaxHeight)));
                    foreach (var t in detected)
                    {
                        EditorGUILayout.BeginHorizontal();
                        bool on  = !_foreignToeUnticked.Contains(t);
                        bool now = EditorGUILayout.Toggle(on, GUILayout.Width(18));
                        if (now != on)
                        {
                            if (now) _foreignToeUnticked.Remove(t);
                            else     _foreignToeUnticked.Add(t);
                        }
                        // Reference only (click it to highlight the bone); a different bone dropped here is ignored.
                        EditorGUILayout.ObjectField(t, typeof(Transform), true);
                        EditorGUILayout.EndHorizontal();
                    }
                    for (int i = 0; i < _foreignToeAdded.Count; i++)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(22);
                        var picked = (Transform)EditorGUILayout.ObjectField(_foreignToeAdded[i], typeof(Transform), true);
                        if (picked != _foreignToeAdded[i])
                        {
                            string problem = ForeignToeAddProblem(picked);
                            if (problem == null) _foreignToeAdded[i] = picked;
                            else SetStatus($"\"{picked.name}\" can't be cleaned: {problem}.", MessageType.Warning);
                        }
                        bool removeRow = GUILayout.Button("×", GUILayout.Width(22));
                        EditorGUILayout.EndHorizontal();
                        if (removeRow)
                        {
                            _foreignToeAdded.RemoveAt(i);
                            i--;
                        }
                    }
                    EditorGUILayout.EndScrollView();
                }
                if (GUILayout.Button(new GUIContent("+ Add bone", "Add a toe bone the check missed. Drop it from the Hierarchy into the new row."),
                        GUILayout.Height(18)))
                    _foreignToeAdded.Add(null);
            }
            EditorGUILayout.EndVertical();
        }

        static string FootShapeRowLabel(FootShapeFit.ToggleRow row)
        {
            switch (row.reason)
            {
                case FootShapeFit.RowReason.BestFit:        return "best fit";
                case FootShapeFit.RowReason.FitsWorse:      return "fits worse";
                case FootShapeFit.RowReason.OnAndFitsWorse: return "on, fits worse";
                case FootShapeFit.RowReason.HidesGeometry:  return "hides geometry";
                case FootShapeFit.RowReason.ReshapesBody:   return "reshapes body";
                case FootShapeFit.RowReason.ShrinksSkin:    return "shrink key";
                default:                                    return "minor";
            }
        }

        static string FootShapeRowTooltip(FootShapeFit.ToggleRow row)
        {
            string cost = $"Switched on from the best shape, it costs {row.samplesDrop * 100f:0} points of the "
                        + $"foot inside the mesh and {row.depthDrop:0.00} foot lengths of depth.";
            switch (row.reason)
            {
                case FootShapeFit.RowReason.BestFit:
                    return "The toe rig is built with this shape key at 100, and the toggle sets it to 100.";
                case FootShapeFit.RowReason.FitsWorse:
                    return "Moves the feet off their bones. " + cost
                         + " The toggle holds it at 0, so nothing else can switch it on while the feet system is on.";
                case FootShapeFit.RowReason.OnAndFitsWorse:
                    return "Switched on in the scene, and the feet fit their bones better without it. The toggle sets it to 0.";
                case FootShapeFit.RowReason.HidesGeometry:
                    return "Hides part of the mesh. Left out of the toggle unless you tick it.";
                case FootShapeFit.RowReason.ReshapesBody:
                    return "Also changes the legs or body. Left out of the toggle unless you tick it.";
                case FootShapeFit.RowReason.ShrinksSkin:
                    return $"Collapses {row.collapsedShare * 100f:0}% of the skin it moves: a shrink key the creator made "
                         + "to hide the body under an outfit. Never used for the rig and left out of the toggle "
                         + "unless you tick it.";
                default:
                    return "Barely changes how the feet fit their bones. " + cost
                         + " Left out of the toggle unless you tick it.";
            }
        }

        // Blender takes the other rig's toe bones out of the FBX skeleton. The scene copy, though, is built
        // from the avatar that was selected, so those GameObjects come across with it and are left
        // dangling: nothing skins to them any more, but they still sit under the feet, still carry whatever
        // the other rig put on them, and still read as bones in the Hierarchy. This removes them from the
        // finished avatar, in both export modes.
        //
        // Two guards, because these are objects the user never named:
        //   • a bone still used by any SkinnedMeshRenderer (bones array or rootBone) is KEPT, so nothing
        //     can lose its skinning, whatever happened in Blender;
        //   • a child that is not itself being removed (a prop or constraint parented under an old toe)
        //     moves up to the bone's parent instead of being destroyed along with it.
        void RemoveCleanedToeBoneObjects()
        {
            if (_resultInstance == null || _cleanToeBoneNames == null || _cleanToeBoneNames.Count == 0) return;
            var names = new HashSet<string>(_cleanToeBoneNames, StringComparer.Ordinal);

            var inUse = new HashSet<Transform>();
            foreach (var smr in _resultInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.rootBone != null) inUse.Add(smr.rootBone);
                var bones = smr.bones;
                if (bones == null) continue;
                foreach (var b in bones) if (b != null) inUse.Add(b);
            }

            // Deepest first, so a chain comes apart from its tip and each bone is childless by its turn.
            var targets = _resultInstance.GetComponentsInChildren<Transform>(true)
                .Where(t => t != null && t != _resultInstance.transform && names.Contains(t.name))
                .OrderByDescending(TransformDepth)
                .ToList();

            int removed = 0, moved = 0;
            var kept = new List<string>();
            foreach (var t in targets)
            {
                if (t == null) continue;
                if (inUse.Contains(t)) { kept.Add(t.name + " (still skinned)"); continue; }
                var parent = t.parent;
                for (int i = t.childCount - 1; i >= 0; i--)
                {
                    Undo.SetTransformParent(t.GetChild(i), parent, "AutoRig Feet — Clean Toe Bones");
                    moved++;
                }
                try
                {
                    Undo.DestroyObjectImmediate(t.gameObject);
                    removed++;
                }
                catch (Exception e)
                {
                    kept.Add(t.name + " (" + e.Message + ")");
                }
            }

            if (removed > 0)
                _runReport.AddIssue("U_TOE_BONES_CLEANED", "info",
                    $"{removed} toe bone object(s) from another rig were removed from the avatar in the scene"
                    + (moved > 0 ? $", and {moved} object(s) parented under them were moved onto the foot" : "")
                    + ". Blender had already taken them out of the FBX skeleton.");
            if (kept.Count > 0)
                _runReport.AddIssue("U_TOE_BONES_KEPT", "warning",
                    $"{kept.Count} toe bone object(s) could not be removed from the avatar in the scene: "
                    + string.Join("; ", kept.Take(6)) + (kept.Count > 6 ? "…" : ""),
                    "They are no longer part of the rig. Delete them by hand if you don't want them, or "
                    + "use Duplicate mode, which builds a fresh copy.");
        }

        static int TransformDepth(Transform t)
        {
            int d = 0;
            while (t.parent != null) { d++; t = t.parent; }
            return d;
        }

        // After the swap: every shape-key value the original avatar had, by renderer path and name.
        void RestoreSceneBlendShapeWeights()
        {
            if (_resultInstance == null || _blendShapeSnapshot == null) return;
            var missing = new List<string>();
            var (matched, changed) = BlendShapeWeightSnapshot.Apply(_resultInstance, _blendShapeSnapshot, missing);
            _runReport.AddIssue("U_BLENDSHAPES_RESTORED", "info",
                $"The avatar keeps the shape-key values it had ({matched} checked, {changed} put back after the swap).");
            if (missing.Count > 0)
                _runReport.AddIssue("U_BLENDSHAPES_MISSING", "warning",
                    $"{missing.Count} shape-key value(s) could not be put back, because the shape key or its "
                    + "mesh no longer exists: " + string.Join("; ", missing.Take(6)) + (missing.Count > 6 ? "…" : ""),
                    "Check those meshes on the avatar. The original values are in blendshape_weights.json in "
                    + "the run folder.");
        }

        // The foot-shape toggle. The 100 rows are what Blender actually built the rig on (from the final
        // report), so the toggle always matches the rig; the 0 rows are the ticked rows from the box.
        void CreateFootShapeToggleAfterRun()
        {
            if (_resultInstance == null) return;
            // Every run starts clean: the toggle object from an earlier run goes first, whatever it held,
            // and a new one is built from this run's list.
            bool hadToggle = FootShapeToggle.Remove(_resultInstance);
            CreateFootShrinkTogglesAfterRun();
            var rows = new List<(string key, float value)>();
            var rigged = ParseRigShapeStats(_runReport.finalRawJson);
            if (rigged != null)
                foreach (var kv in rigged)
                    if (kv.Value > 0f) rows.Add((kv.Key, kv.Value));
            else if (_plannedToggleRows != null)
                foreach (var r in _plannedToggleRows)
                    if (r.value > 0f) rows.Add((r.key, r.value));
            if (_plannedToggleRows != null)
                foreach (var r in _plannedToggleRows)
                    if (r.value <= 0f && !rows.Exists(x => x.key == r.key)) rows.Add((r.key, 0f));
            if (rows.Count == 0)
            {
                if (hadToggle)
                    _runReport.AddIssue("U_FOOT_SHAPE_TOGGLE_REMOVED", "info",
                        $"Removed \"{FootShapeToggle.ObjectName}\" from the earlier run: no shape key is ticked "
                        + "in the foot shape list now.");
                return;
            }

            string list = string.Join(", ", rows.Select(r => $"{r.key} = {r.value:0}"));
            if (!FootShapeToggle.VRCFuryAvailable)
            {
                _runReport.AddIssue("U_FOOT_SHAPE_NO_VRCFURY", "warning",
                    $"The feet need these shape keys while the feet system is on: {list}. VRCFury is not "
                    + "installed, so no toggle was added.",
                    "Install VRCFury and run again, or animate those shape keys yourself.");
                return;
            }
            var go = FootShapeToggle.CreateOrUpdate(_resultInstance, rows, out string error);
            if (go == null)
            {
                _runReport.AddIssue("U_FOOT_SHAPE_TOGGLE_FAILED", "warning",
                    "The foot-shape toggle could not be added: " + error,
                    $"Animate these shape keys yourself while the feet system is on: {list}.");
                return;
            }
            _runReport.AddIssue("U_FOOT_SHAPE_TOGGLE", "info",
                (hadToggle ? "Replaced" : "Added") + $" \"{FootShapeToggle.ObjectName}\" as the last object under "
                + $"the avatar, with the menu toggle {FootShapeToggle.MenuPath}: {list}.",
                "Turn it on while you use the feet system. Keep the object last under the avatar, so no other "
                + "toggle overrides it.");
        }

        static GUIStyle _shrinkOptionTitle;

        // The tool's own shrink shape keys. Drawn in colour because it is the one thing in this box that changes
        // the body mesh, and because it is what makes flattening the feet possible on a body whose creator
        // shipped no shrink key at all — which is most of them.
        void DrawMakeShrinkKeysOption()
        {
            if (_shrinkOptionTitle == null)
                _shrinkOptionTitle = new GUIStyle(EditorStyles.boldLabel)
                {
                    normal = { textColor = new Color(0.10f, 0.45f, 0.70f) },
                    onNormal = { textColor = new Color(0.10f, 0.45f, 0.70f) },
                };
            if (EditorGUIUtility.isProSkin)
            {
                _shrinkOptionTitle.normal.textColor = new Color(0.45f, 0.80f, 1.00f);
                _shrinkOptionTitle.onNormal.textColor = new Color(0.45f, 0.80f, 1.00f);
            }

            var bg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.55f, 0.80f, 1.00f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = bg;

            bool scriptOk = ScriptSupportsShrinkKeys();
            EditorGUI.BeginDisabledGroup(!scriptOk);
            EditorGUI.BeginChangeCheck();
            _makeShrinkKeys = EditorGUILayout.ToggleLeft(
                new GUIContent("Make the shrink shape keys (recommended)",
                    "On: the run adds \"" + FootShapeFit.OwnShrinkPrefix + "Toes\", \"… Toe Base\" and \"… Foot\" to "
                    + "the body mesh, built from the rig's own weights, each with its own menu toggle. They flatten "
                    + "the toes, the ball of the foot and the foot, so an outfit can't be poked through.\n\n"
                    + "Off: nothing is added to the mesh, and only the creator's own shrink keys (if any) can be "
                    + "toggled."),
                _makeShrinkKeys && scriptOk, _shrinkOptionTitle);
            if (EditorGUI.EndChangeCheck()) ArfPrefs.SetBool(PrefMakeShrinkKeys, _makeShrinkKeys);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.LabelField(
                !scriptOk
                    ? "Needs the AutoRig Feet script 3.17.0 or newer. Until then the creator's own shrink keys are "
                      + "used, as below."
                    : MakeShrinkKeysNow
                    ? "Three shape keys are added to the body mesh and rebuilt on every run — the toes, the ball of "
                      + "the foot and the foot itself — each with its own toggle under "
                      + FootShapeToggle.ShrinkMenuPrefix + "."
                    : "Off: the body mesh is left as it is, and only the creator's own shrink keys can be toggled.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        /// <summary>The shrink keys the run actually put on the body, read from the mesh that came back rather
        /// than from what was asked for — so a run finished after an interruption finds them too.</summary>
        List<string> OwnShrinkKeysOnResult()
        {
            var found = new List<string>();
            if (_resultInstance == null) return found;
            foreach (var smr in _resultInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    if (FootShapeFit.IsOwnShrinkKey(name) && !found.Contains(name)) found.Add(name);
                }
            }
            return found;
        }

        // One VRCFury toggle per foot shrink key, each at Animation/Feet/Fix/Foot Shrink <name>. Built before the
        // Foot Shape toggle, so Foot Shape stays the very last object under the avatar.
        void CreateFootShrinkTogglesAfterRun()
        {
            int removed = FootShapeToggle.RemoveAllShrink(_resultInstance);
            // Ours first (in the order the script wrote them), then any of the creator's the user ticked.
            var keys = MakeShrinkKeysNow ? OwnShrinkKeysOnResult() : new List<string>();
            foreach (string planned in _plannedShrinkKeys ?? new List<string>())
                if (!keys.Contains(planned)) keys.Add(planned);
            if (_recRun != null) { _recRun.shrinkKeys = new List<string>(keys); _rec?.Save(); }
            if (MakeShrinkKeysNow && keys.Count == 0)
                _runReport.AddIssue("U_FOOT_SHRINK_KEYS_MISSING", "warning",
                    "The shrink shape keys were asked for, but the rigged body has none.",
                    "The AutoRig Feet script may be older than 3.17.0, or the run's log says why it could not "
                    + "build them. The rig itself is finished.");
            if (keys.Count == 0)
            {
                if (removed > 0)
                    _runReport.AddIssue("U_FOOT_SHRINK_TOGGLES_REMOVED", "info",
                        $"Removed {removed} Foot Shrink toggle(s) from the earlier run: no foot shrink key is ticked now.");
                return;
            }
            if (!FootShapeToggle.VRCFuryAvailable)
            {
                _runReport.AddIssue("U_FOOT_SHAPE_NO_VRCFURY", "warning",
                    "Foot shrink keys are ticked (" + string.Join(", ", keys) + "), but VRCFury is not installed, so no "
                    + "toggle was added.", "Install VRCFury and run again, or animate those shape keys yourself.");
                return;
            }
            var made = new List<string>();
            foreach (var (key, label) in FootShapeToggle.ShrinkLabels(keys))
            {
                var go = FootShapeToggle.CreateOrUpdate(_resultInstance, FootShapeToggle.ShrinkObjectNameFor(label),
                    FootShapeToggle.ShrinkMenuPathFor(label), new List<(string, float)> { (key, 100f) }, out string error);
                if (go == null)
                    _runReport.AddIssue("U_FOOT_SHAPE_TOGGLE_FAILED", "warning",
                        $"The Foot Shrink toggle for {key} could not be added: " + error);
                else
                    made.Add(FootShapeToggle.ShrinkMenuPathFor(label));
            }
            if (made.Count > 0)
                _runReport.AddIssue("U_FOOT_SHRINK_TOGGLES", "info",
                    $"Added {made.Count} Foot Shrink toggle(s), each setting its shrink key to 100: " + string.Join(", ", made) + ".",
                    "Turn one on while an outfit covers the feet, so the feet don't poke through it.");
        }

        // stats.rig_shape from the final report: {"High heeled": 100.0}. JsonUtility can't read a
        // dictionary, and the keys are the creator's shape-key names, which can hold any character.
        static Dictionary<string, float> ParseRigShapeStats(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int stats = json.IndexOf("\"stats\"", StringComparison.Ordinal);
            int at = stats < 0 ? -1 : json.IndexOf("\"rig_shape\"", stats, StringComparison.Ordinal);
            int i = at < 0 ? -1 : json.IndexOf('{', at);
            if (i < 0) return null;
            var map = new Dictionary<string, float>();
            i++;
            while (i < json.Length)
            {
                while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
                if (i >= json.Length) return null;
                if (json[i] == '}') return map;
                string key = ReadJsonString(json, ref i);
                if (key == null) return null;
                while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ':')) i++;
                int start = i;
                while (i < json.Length && "+-.0123456789eE".IndexOf(json[i]) >= 0) i++;
                if (!float.TryParse(json.Substring(start, i - start), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float value)) return null;
                map[key] = value;
            }
            return null;
        }

        static string ReadJsonString(string json, ref int i)
        {
            if (i >= json.Length || json[i] != '"') return null;
            var sb = new StringBuilder();
            for (i++; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= json.Length) return null;
                switch (json[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 >= json.Length) return null;
                        sb.Append((char)Convert.ToInt32(json.Substring(i + 1, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(json[i]); break;   // \" \\ \/
                }
            }
            return null;
        }

        // ─── Restore original rig (from JSON backup) ───────────────────────────

        // True when the SOURCE FBX asset itself carries AutoRig Feet bones — i.e. some previous run
        // rigged it IN PLACE. Cached on path + last-write-time; OnGUI runs many times a second and
        // this walks a few hundred transforms.
        string _srcRiggedKey;
        bool   _srcRiggedValue;
        string _srcBackupKey;
        bool   _srcBackupValue;

        // Script 3.16.0+ stores the original weights inside the FBX, as a custom property on each mesh
        // (Blender writes it as a plain FBX user property). Unity never loads those, so the file is
        // searched for the property name. Cached on path + last-write-time like SourceFbxIsRigged.
        static readonly byte[] WeightBackupMarker = System.Text.Encoding.ASCII.GetBytes("ARF_WeightBackup");

        bool SourceFbxHasWeightBackup()
        {
            if (_sourceFbxAsset == null) return false;
            string path = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            if (string.IsNullOrEmpty(path)) return false;
            string abs = ToAbsPath(path);
            string key = path;
            try { if (File.Exists(abs)) key = path + "|" + File.GetLastWriteTimeUtc(abs).Ticks; }
            catch { }
            if (key == _srcBackupKey) return _srcBackupValue;

            _srcBackupKey = key;
            _srcBackupValue = false;
            try { _srcBackupValue = FileContains(abs, WeightBackupMarker); }
            catch { }
            return _srcBackupValue;
        }

        static bool FileContains(string absPath, byte[] needle)
        {
            if (!File.Exists(absPath)) return false;
            byte[] data = File.ReadAllBytes(absPath);
            int last = data.Length - needle.Length;
            for (int i = 0; i <= last; i++)
            {
                if (data[i] != needle[0]) continue;
                int j = 1;
                while (j < needle.Length && data[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        /// <summary>"&lt;fbx folder&gt;/_Backups/&lt;fbx name&gt;_rigbackup_": the script reads every JSON backup
        /// of this FBX from it, for the meshes the FBX itself holds no backup for.</summary>
        string BackupSearchPrefix()
        {
            if (_sourceFbxAsset == null) return null;
            string fbxName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(_sourceFbxAsset));
            return Path.Combine(SourceFbxAbsDir(), "_Backups", fbxName + "_rigbackup_");
        }

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
                GUILayout.Label("Clean AutoRig Feet from the FBX", EditorStyles.boldLabel);
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
            bool inFbx = SourceFbxHasWeightBackup();
            if (jsons.Length == 0 && !inFbx) return;  // nothing to restore

            EditorGUILayout.Space(6);
            GUILayout.Label("Clean AutoRig Feet from the FBX (keep later FBX edits)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Removes the toe rig from the current FBX in Blender and puts the original weights back. The file is "
                + "not the original one: use this only if you changed the FBX after rigging and want to keep those "
                + "changes. Otherwise use Restore to original above.", EditorStyles.wordWrappedMiniLabel);

            bool busy = _state != State.Idle && _state != State.Done && _state != State.Error;
            EditorGUILayout.BeginHorizontal();
            if (inFbx)
            {
                // The backup inside the FBX covers the body and every outfit and wins over any file; the JSON
                // files are only read for a mesh it has nothing for, so there is nothing to choose here.
                EditorGUILayout.LabelField(
                    new GUIContent("Backup stored inside the FBX",
                        "Original weights of the body, the outfits and the nails, saved in the FBX by the rig. "
                        + (jsons.Length > 0 ? "The JSON files in _Backups are used for any mesh it doesn't cover." : "")),
                    EditorStyles.miniLabel);
            }
            else
            {
                _restoreIndex = Mathf.Clamp(_restoreIndex, 0, jsons.Length - 1);
                _restoreIndex = EditorGUILayout.Popup(_restoreIndex, jsons.Select(Path.GetFileName).ToArray());
            }
            EditorGUI.BeginDisabledGroup(busy || !_depAutoRigScript || !_depBlender);
            if (GUILayout.Button("Clean", GUILayout.Width(90)))
                RunRestore(inFbx ? null : jsons[_restoreIndex]);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        bool _restoreRemoveObjects;

        void RunRestore(string jsonPath, bool removeObjectsAfter = false)
        {
            string blenderPath = ArfPrefs.GetString(BlenderBridge.PrefBlenderPath, "");
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

            bool ok = removeObjectsAfter || ArfAutomation.Confirm(
                ArfAutomation.AskRestoreRig,
                "Clean AutoRig Feet from the FBX?",
                "This imports the current FBX into Blender, removes the AutoRig Feet rig using:\n" +
                (string.IsNullOrEmpty(jsonPath) ? "the backup stored inside the FBX" : Path.GetFileName(jsonPath)) +
                "\n\nand OVERWRITES the FBX:\n" + ToProjectRelative(sourceFbxAbs) +
                "\n\nMesh edits made after rigging are kept; only the feet rig is removed. The FBX is backed up first. "
                + "The result is NOT byte-identical to the original file: to get that back (for an FBX patch such as "
                + "face tracking), use \"Restore to original\" instead." + UsersWarning(ToProjectRelative(sourceFbxAbs), _avatarObject) +
                (ScriptSupportsExternalOutfits() && RiggedExternalOutfits().Count > 0
                    ? "\n\nThese outfit FBXs carry the rig too and are restored the same way (each backed up first):\n"
                      + string.Join("\n", RiggedExternalOutfits().Select(o => Path.GetFileName(o.fbx)))
                    : ""),
                "Restore", "Cancel");
            if (!ok) return;

            CaptureArmatureBefore();
            string notWritable = FbxNotWritable(sourceFbxAbs);
            if (notWritable != null)
            {
                SetStatus("The FBX can't be overwritten: " + notWritable + ". Nothing was changed.", MessageType.Error);
                return;
            }
            _restoreRemoveObjects = true;       // cleaning the rig out of the FBX takes the scene objects out too
            _restoreTempExport = NewTempExport("clean");

            _displayProgress  = 0f;
            _currentStepLabel = "Restoring rig in Blender…";
            lock (_outputLock) _outputQueue.Clear();
            BeginRunLog("restore");
            _runReport        = new ShuganTools.RunReport();
            _runStartTicksUtc = DateTime.UtcNow.Ticks;

            string pythonCode = BlenderBridge.BuildRestoreFeetScript(
                sourceFbxAbs, targetMesh, _restoreTempExport, scriptPath, jsonPath,
                headless: true, stepDelay: 0f, backupSearchPrefix: BackupSearchPrefix());
            RecordBeginRun("clean");

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
            ArfPrefs.SetInt(PrefTab, (int)_tab);
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

        // ─── Backups tab ───────────────────────────────────────────────────────
        // Everything this tool has ever backed up, project-wide, grouped by the asset it restores.
        //
        // Project-wide rather than "the current avatar's backups" because of when you actually need
        // this: something broke, and the thing that broke may be the very avatar you can no longer
        // select. Entries matching the current selection are highlighted so they are still the
        // easiest to reach.

        List<ShuganBackups.AssetGroup> _backupScan;
        bool _backupScanDirty = true;
        readonly HashSet<string> _backupExpanded = new HashSet<string>();

        List<ShuganBackups.AssetGroup> BackupScan()
        {
            // Walks every _Backups folder under Assets/, so never from a plain repaint.
            if (_backupScan == null || _backupScanDirty)
            {
                _backupScan      = ShuganBackups.Scan();
                _backupScanDirty = false;
            }
            return _backupScan;
        }

        void DrawBackupsTab()
        {
            EditorGUILayout.LabelField("Backups & Restore", EditorStyles.boldLabel);

            DrawOriginalPanel();
            EditorGUILayout.Space(6);

            var groups = BackupScan();
            var originals = OriginalBackupFiles();
            long total = 0;
            int  files = 0;
            foreach (var g in groups) { total += g.totalBytes; files += g.entries.Count; }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    $"{groups.Count} asset(s), {files} backup(s), {ShuganBackups.FormatBytes(total)}",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Refresh", GUILayout.Width(70))) _backupScanDirty = true;
            }

            EditorGUILayout.HelpBox(
                "Backups live in a _Backups folder next to the asset they restore, so they travel "
                + "with it. This tool never deletes a backup — not on a schedule, not by age, not "
                + "ever. A backup is the only copy of a state you might need back, so removing one "
                + "is your call, in Explorer.\n\n"
                + "Restoring makes a backup of the current file first, so picking the wrong one is "
                + "recoverable.", MessageType.None);

            // What the user has selected right now — highlighted below, not filtered to.
            string selFbx   = _sourceFbxAsset != null ? AssetDatabase.GetAssetPath(_sourceFbxAsset) : null;
            string selScene = null;
            if (_avatarObject != null && _avatarObject.scene.IsValid())
                selScene = _avatarObject.scene.path;

            if (groups.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No backups yet. Running AutoRig Feet backs up the scene, and the source FBX "
                    + "whenever a run would overwrite it.", MessageType.Info);
            }

            ShuganBackups.Kind? lastKind = null;
            foreach (var g in groups)
            {
                if (lastKind != g.kind)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(
                        g.kind == ShuganBackups.Kind.Fbx   ? "FBX"
                      : g.kind == ShuganBackups.Kind.Scene ? "Scenes"
                      : "Other", EditorStyles.boldLabel);
                    lastKind = g.kind;
                }

                bool isSelected =
                    (selFbx   != null && string.Equals(g.assetPath, selFbx,   StringComparison.OrdinalIgnoreCase)) ||
                    (selScene != null && string.Equals(g.assetPath, selScene, StringComparison.OrdinalIgnoreCase));

                Color prevBg = GUI.backgroundColor;
                if (isSelected) GUI.backgroundColor = new Color(0.55f, 0.85f, 1f);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUI.backgroundColor = prevBg;

                // The selected avatar's own backups open by themselves, once.
                if (isSelected && _backupAutoExpanded.Add(g.assetPath)) _backupExpanded.Add(g.assetPath);
                bool expanded = _backupExpanded.Contains(g.assetPath);
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool now = EditorGUILayout.Foldout(expanded,
                        new GUIContent(g.assetName + (isSelected ? "   ← selected" : ""),
                                       g.assetPath), true);
                    if (now != expanded)
                    {
                        if (now) _backupExpanded.Add(g.assetPath);
                        else     _backupExpanded.Remove(g.assetPath);
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"{g.entries.Count} × {ShuganBackups.FormatBytes(g.totalBytes)}",
                                    EditorStyles.miniLabel, GUILayout.Width(120));
                }

                if (!g.assetExists)
                    EditorGUILayout.LabelField(
                        "   The original is gone — restoring writes the file back to " + g.assetPath,
                        EditorStyles.miniLabel);

                if (expanded)
                {
                    foreach (var e in g.entries)
                        DrawBackupEntry(g, e, originals.Contains(e.filePath));
                }
                EditorGUILayout.EndVertical();
            }

        }

        readonly HashSet<string> _backupAutoExpanded = new HashSet<string>();

        void DrawBackupEntry(ShuganBackups.AssetGroup g, ShuganBackups.Entry e, bool isOriginal = false)
        {
            Color prevColor = GUI.color;
            if (isOriginal) GUI.color = new Color(0.65f, 1f, 0.7f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.color = prevColor;
                GUILayout.Space(14);
                string label = (isOriginal ? "★ original, before AutoRig Feet — " : "")
                             + e.when.ToString("yyyy-MM-dd HH:mm")
                             + (e.isPreSave ? "  (before unsaved changes)" : "");
                EditorGUILayout.LabelField(new GUIContent(label, e.fileName),
                                           EditorStyles.miniLabel, GUILayout.MinWidth(150));
                GUILayout.Label(ShuganBackups.FormatBytes(e.sizeBytes),
                                EditorStyles.miniLabel, GUILayout.Width(62));

                if (e.kind == ShuganBackups.Kind.RigJson)
                {
                    // Not a file swap — this one is applied through the Blender restore path below.
                    GUILayout.Label("rig-only — see Clean AutoRig Feet",
                                    EditorStyles.miniLabel, GUILayout.Width(178));
                }
                else if (GUILayout.Button(
                             new GUIContent("Restore",
                                 "Put this backup back over " + g.assetName
                                 + ". The current file is backed up first."),
                             EditorStyles.miniButton, GUILayout.Width(62)))
                {
                    RestoreBackup(g, e);
                }

                if (GUILayout.Button("Show", EditorStyles.miniButton, GUILayout.Width(46)))
                    EditorUtility.RevealInFinder(e.filePath);
            }
        }

        void RestoreBackup(ShuganBackups.AssetGroup g, ShuganBackups.Entry e)
        {
            bool isOpenScene = e.kind == ShuganBackups.Kind.Scene && IsSceneOpen(g.assetPath);

            string msg = "Replace\n    " + g.assetPath
                       + "\nwith the backup from " + e.when.ToString("yyyy-MM-dd HH:mm")
                       + (e.isPreSave ? "  (the copy taken before your unsaved changes)" : "")
                       + "?\n\nThe current file is backed up first, so this is reversible."
                       + (e.kind == ShuganBackups.Kind.Fbx ? UsersWarning(g.assetPath, null) : "");
            if (isOpenScene)
                msg += "\n\nThat scene is open. It will be reloaded from the restored file, and any "
                     + "unsaved changes in it are lost.";

            if (!ArfAutomation.Confirm(ArfAutomation.AskRestoreBackup, "Restore backup?", msg, "Restore", "Cancel")) return;

            // Unity holds the open scene in memory and would write it back over the file we just
            // restored, so close it to an empty scene first and reopen afterwards.
            if (isOpenScene)
            {
                if (!UnityEditor.SceneManagement.EditorSceneManager
                        .SaveCurrentModifiedScenesIfUserWantsTo()) return;
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Single);
            }

            if (!ShuganBackups.Restore(e, g.assetPath, out string safety, out string error))
            {
                ArfAutomation.Notify("restore-failed", "Restore failed", error ?? "Unknown error.");
                return;
            }

            if (isOpenScene)
            {
                try
                {
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                        g.assetPath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning(
                        "[AutoRig Feet Distributor] Restored, but could not reopen the scene: " + ex.Message);
                }
            }

            // The restored file has a different skeleton from the one the avatars in the scene were built
            // against, so put them back in step before the reference to the avatar is dropped.
            ResyncSceneAvatarsUsing(g.assetPath);

            _backupScanDirty = true;
            _avatarObject    = null;      // the object it pointed at may no longer exist
            RefreshRigState();

            UnityEngine.Debug.Log("[AutoRig Feet Distributor] Restored " + g.assetPath
                + " from " + e.fileName
                + (safety != null ? "  (previous file kept as " + Path.GetFileName(safety) + ")" : ""));
            SetStatus("Restored " + g.assetName + " from the "
                      + e.when.ToString("yyyy-MM-dd HH:mm") + " backup."
                      + (safety != null ? " The file it replaced was backed up first." : ""),
                      MessageType.Info);
        }

        static bool IsSceneOpen(string scenePath)
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (s.IsValid() && string.Equals(s.path, scenePath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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
            if (EditorGUI.EndChangeCheck()) ArfPrefs.SetString(PrefBugContact, _bugContact ?? "");

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
        // One table for every outfit mesh of the avatar, wherever it lives. The body FBX's own outfits are
        // found and ticked for you, because rigging them is the normal case and costs nothing extra. The ones
        // in a separate FBX are found and listed but never ticked: rigging one is experimental and rewrites a
        // file that other avatars may share. A column says which is which, and so does the add menu.

        struct GarmentRow
        {
            public string mesh, key, fbxPath, source, why;
            public bool   external, detected, ticked;
            public SkinnedMeshRenderer smr;
        }

        struct GarmentChoice
        {
            public string mesh, key, menu;
            public bool   external;
        }

        /// <summary>Every outfit mesh the table shows, the body FBX's first.</summary>
        List<GarmentRow> GarmentRows(string body)
        {
            var rows = new List<GarmentRow>();
            string bodyFbx = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            LoadOutfitChoices(out var unticked, out var added);

            foreach (var c in OutfitDetect.Find(bodyFbx, body, new[] { _footOverrideL, _footOverrideR }))
                rows.Add(new GarmentRow { mesh = c.meshName, key = c.meshName, fbxPath = bodyFbx,
                                          source = "body FBX", why = c.Explain(), detected = true,
                                          ticked = !unticked.Contains(c.meshName),
                                          smr = FindAvatarSkinnedMesh(c.meshName) });
            foreach (string m in added)
            {
                if (string.Equals(m, body, StringComparison.OrdinalIgnoreCase)) continue;
                if (rows.Any(r => string.Equals(r.mesh, m, StringComparison.OrdinalIgnoreCase))) continue;
                rows.Add(new GarmentRow { mesh = m, key = m, fbxPath = bodyFbx, source = "body FBX",
                                          why = "added by you", ticked = true, smr = FindAvatarSkinnedMesh(m) });
            }

            LoadExternalChoices(out var tickedExternal);
            foreach (var r in ExternalOutfitRows())
                rows.Add(new GarmentRow { mesh = r.mesh, key = r.key, fbxPath = r.fbxPath,
                                          source = "outfit FBX: " + Path.GetFileName(r.fbxPath), why = r.why,
                                          external = true, detected = r.detected,
                                          ticked = tickedExternal.Contains(r.key), smr = r.smr });
            return rows;
        }

        /// <summary>What "+ Add Outfit Mesh" offers: every mesh of the avatar that is not in the table yet, from
        /// the body's FBX or from any other, each under a heading that says where it comes from. The mesh chosen
        /// as the body in the setup is never offered — that one IS the body.</summary>
        List<GarmentChoice> GarmentChoices(string body, List<GarmentRow> rows)
        {
            var list = new List<GarmentChoice>();
            foreach (string m in _meshNames)
            {
                if (string.Equals(m, body, StringComparison.OrdinalIgnoreCase)) continue;
                if (rows.Any(r => !r.external && string.Equals(r.mesh, m, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new GarmentChoice { mesh = m, key = m, menu = "From the body FBX/" + MenuSafe(m) });
            }
            foreach (var x in ExternalMeshes())
            {
                if (rows.Any(r => r.external && r.key == x.key)) continue;
                list.Add(new GarmentChoice { mesh = x.mesh, key = x.key, external = true,
                                             menu = "From another FBX/" + MenuSafe(Path.GetFileName(x.fbxPath))
                                                    + "/" + MenuSafe(x.mesh) });
            }
            return list;
        }

        /// <summary>A menu item's text can't hold a slash: GenericMenu reads it as a submenu.</summary>
        static string MenuSafe(string name) => (name ?? "").Replace("/", "\u2215");

        void DrawGarmentSection()
        {
            EditorGUILayout.Space(6);
            GUILayout.Label("Transfer Toe Weights To", EditorStyles.boldLabel);

            bool hasFbx = _sourceFbxAsset != null && IsValidFbx(_sourceFbxAsset);
            if (!hasFbx || _meshNames.Length == 0)
            {
                EditorGUILayout.HelpBox("Select an avatar first to see its outfit meshes.", MessageType.Info);
                return;
            }

            string body = _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)];
            var rows = GarmentRows(body);
            bool scriptOk = ScriptSupportsExternalOutfits();
            bool swapSkips = ExternalOutfitsSkippedBySwap;
            bool externalUsable = scriptOk && !swapSkips;
            bool changedBody = false, changedExternal = false;
            LoadOutfitChoices(out var unticked, out var added);
            LoadExternalChoices(out var tickedExternal);

            if (rows.Count == 0)
                EditorGUILayout.LabelField("No other mesh of this avatar sits on the feet.", EditorStyles.miniLabel);
            else
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Mesh", EditorStyles.miniBoldLabel, GUILayout.MinWidth(120));
                GUILayout.Label("Comes from", EditorStyles.miniBoldLabel, GUILayout.Width(150));
                GUILayout.Label("Why it is listed", EditorStyles.miniBoldLabel, GUILayout.Width(110));
                GUILayout.Label(" ", EditorStyles.miniLabel, GUILayout.Width(GarmentRefWidth));
                EditorGUILayout.EndHorizontal();
            }

            foreach (var row in rows)
            {
                bool blocked = row.external && !externalUsable;
                EditorGUI.BeginDisabledGroup(blocked);
                EditorGUILayout.BeginHorizontal();
                bool now = EditorGUILayout.ToggleLeft(
                    new GUIContent(row.mesh, row.fbxPath), row.ticked && !blocked, GUILayout.MinWidth(120));
                if (!blocked && now != row.ticked)
                {
                    if (row.external)
                    {
                        if (now) tickedExternal.Add(row.key); else tickedExternal.Remove(row.key);
                        changedExternal = true;
                    }
                    else if (row.detected)
                    {
                        // Off the "added by hand" list either way: it overrides the unticked one, so a mesh
                        // that is on both would keep running while the box shows it unticked.
                        added.RemoveAll(m => string.Equals(m, row.mesh, StringComparison.OrdinalIgnoreCase));
                        if (now) unticked.Remove(row.mesh); else unticked.Add(row.mesh);
                        changedBody = true;
                    }
                    else if (!now) { added.Remove(row.mesh); changedBody = true; }   // added by hand: untick removes it
                }
                GUILayout.Label(new GUIContent(row.source, row.external
                        ? row.fbxPath + "\n\nExperimental: this file is rewritten, "
                          + OutfitFormLabel(row.smr, row.fbxPath) + "."
                        : row.fbxPath),
                    EditorStyles.miniLabel, GUILayout.Width(150));
                GUILayout.Label(row.why, EditorStyles.miniLabel, GUILayout.Width(110));
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField(row.smr, typeof(SkinnedMeshRenderer), true, GUILayout.Width(GarmentRefWidth));
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.EndHorizontal();
                EditorGUI.EndDisabledGroup();

                // The warning belongs to the garment that will actually be changed, so it is drawn per ticked row.
                if (row.external && now && !blocked)
                    EditorGUILayout.HelpBox(
                        "Experimental: the toe bones are added to " + row.mesh + "'s own armature and its foot "
                        + "weights are "
                        + (_exportMode == ExportMode.Replace
                            ? "rewritten in " + Path.GetFileName(row.fbxPath) + " (a backup is kept in _Backups). "
                            : "written to a copy of " + Path.GetFileName(row.fbxPath) + " for the duplicated avatar. ")
                        + "Any other avatar using that file gets the toe bones too. Restore to original puts it back.",
                        MessageType.Warning);
            }

            if (rows.Any(r => r.external) && !scriptOk)
                EditorGUILayout.HelpBox("The meshes from another FBX need a newer AutoRig Feet script (one that "
                                        + "can rig outfits from other FBXs).", MessageType.Info);
            else if (rows.Any(r => r.external) && swapSkips)
                EditorGUILayout.HelpBox("Meshes from another FBX are skipped with the Legacy swap in Duplicate "
                                        + "mode. Use the Standard swap (the default) to include them.", MessageType.Info);

            var choices = GarmentChoices(body, rows);
            EditorGUI.BeginDisabledGroup(choices.Count == 0);
            if (GUILayout.Button(new GUIContent("+ Add Outfit Mesh",
                    "Every mesh of this avatar that is not in the list yet, grouped by the FBX it comes from."),
                GUILayout.Height(22)))
            {
                var menu = new GenericMenu();
                foreach (var choice in choices)
                {
                    var c = choice;   // the menu calls back later, after this list is gone
                    menu.AddItem(new GUIContent(c.menu), false, () =>
                    {
                        if (c.external)
                        {
                            LoadExternalChoices(out var set);
                            set.Add(c.key);
                            SaveExternalChoices(set);
                        }
                        else
                        {
                            LoadOutfitChoices(out var off, out var extra);
                            off.Remove(c.mesh);
                            if (!extra.Contains(c.mesh)) extra.Add(c.mesh);
                            SaveOutfitChoices(off, extra);
                        }
                        Repaint();
                    });
                }
                menu.ShowAsContext();
            }
            EditorGUI.EndDisabledGroup();

            if (changedBody) SaveOutfitChoices(unticked, added);
            if (changedExternal) SaveExternalChoices(tickedExternal);
        }

        void SaveGarments()
            => ArfPrefs.SetString(PrefGarments, string.Join("|", _garmentMeshNames));

        // ─── Outfits: what a run actually transfers to ─────────────────────────
        // The list is filled in for you. OutfitDetect reads the FBX and says which meshes sit on the feet,
        // and those are ticked; the stored setting holds only the DIFFERENCES from that answer, so a mesh
        // added to the FBX later is picked up on its own instead of waiting for someone to notice.
        //
        // Stored per FBX (avatars have different outfits), and seeded once from the single global list the
        // older versions kept, so nobody loses a selection they made by hand.

        string OutfitPrefKey()
        {
            string path = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            string guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid) ? null : "ShuganTools_ARF_Outfits_" + guid;
        }

        /// <summary>Meshes the user unticked, and meshes they added by hand.</summary>
        void LoadOutfitChoices(out HashSet<string> unticked, out List<string> added)
        {
            unticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            added    = new List<string>();

            string key = OutfitPrefKey();
            if (key == null) return;

            string stored = ArfPrefs.GetString(key, null);
            if (stored == null)
            {
                // First time for this FBX: carry over anything the old global list named, if this FBX has
                // those meshes at all.
                foreach (string legacy in ArfPrefs.GetString(PrefGarments, "").Split('|'))
                    if (!string.IsNullOrEmpty(legacy) && _meshNames.Contains(legacy, StringComparer.OrdinalIgnoreCase))
                        added.Add(legacy);
                return;
            }

            foreach (string entry in stored.Split('|'))
            {
                if (entry.Length < 2) continue;
                if (entry[0] == '-') unticked.Add(entry.Substring(1));
                else if (entry[0] == '+') added.Add(entry.Substring(1));
            }
        }

        void SaveOutfitChoices(HashSet<string> unticked, List<string> added)
        {
            string key = OutfitPrefKey();
            if (key == null) return;
            var parts = unticked.Select(n => "-" + n).Concat(added.Select(n => "+" + n));
            ArfPrefs.SetString(key, string.Join("|", parts));
        }

        /// <summary>The meshes this run transfers the toe and foot weights onto: everything detected on the
        /// feet and still ticked, plus anything added by hand. The body mesh is never one of them.</summary>
        List<string> TickedOutfits()
        {
            var list = new List<string>();
            if (_sourceFbxAsset == null || _meshNames.Length == 0) return list;

            string body = _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)];
            LoadOutfitChoices(out var unticked, out var added);

            foreach (var c in OutfitDetect.Find(AssetDatabase.GetAssetPath(_sourceFbxAsset), body, new[] { _footOverrideL, _footOverrideR }))
                if (!unticked.Contains(c.meshName) && !string.Equals(c.meshName, body, StringComparison.OrdinalIgnoreCase))
                    list.Add(c.meshName);

            foreach (string name in added)
                if (!string.IsNullOrEmpty(name) && !list.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                    !string.Equals(name, body, StringComparison.OrdinalIgnoreCase))
                    list.Add(name);

            return list;
        }

        // ─── Reset to defaults ─────────────────────────────────────────────────
        // Every option in Advanced Settings, with the value it ships with. Settings live in EditorPrefs,
        // which are shared by every Unity project on the machine, so a setting changed once for one avatar
        // quietly follows the user everywhere — and a panel with no way back is a panel people stop
        // trusting. Paths and picks are deliberately absent: see ResetAdvancedSettings.
        static readonly (string key, object value)[] AdvancedDefaults =
        {
            (PrefSuffix,                  "Rig_Feet"),
            (PrefExportFolder,            ""),
            (PrefSwapMethod,              (int)SwapMethod.Legacy),
            (PrefAutoSwapMethod,          true),
            (PrefBackupEnabled,           true),
            (PrefTimeoutMin,              10),
            (PrefAutoMapFeet,             true),
            (PrefRigOnBestShape,          true),
            (PrefZeroFeetShapeKeys,       false),
            (PrefCleanForeignToes,        true),
            (PrefUnlinkedPrefabSceneOnly, false),
            (PrefFootHelper,              true),
            (PrefMakeShrinkKeys,          true),
        };

        void DrawResetToDefaults()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                "Put every option below back to the value it ships with.",
                EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button(new GUIContent("Reset to defaults",
                    "Blender and script paths, the source FBX, the target mesh, the foot bones, your outfit "
                    + "list and your bug-report contact are all left alone."),
                    GUILayout.Width(130)))
                ResetAdvancedSettings();
            EditorGUILayout.EndHorizontal();
        }

        void ResetAdvancedSettings()
        {
            bool go = ArfAutomation.Confirm(
                "reset-defaults",
                "Reset AutoRig Feet settings?",
                "These go back to their defaults:\n\n"
                + "  • Export suffix and folder\n"
                + "  • FBX swap method (and choosing it automatically)\n"
                + "  • Rig JSON backup, Blender timeout\n"
                + "  • Humanoid auto-map\n"
                + "  • Foot shape keys, set feet shape keys to 0\n"
                + "  • Clean existing toe bones\n"
                + "  • Where bones go for avatars not linked to their FBX\n"
                + "  • Foot helper bone\n"
                + "  • The list of prefabs a run adds\n\n"
                + "These are KEPT:\n\n"
                + "  • Blender path and paid script path\n"
                + "  • Source FBX, target mesh and foot bone picks\n"
                + "  • Export mode, your outfit list, your bug-report contact",
                "Reset", "Cancel");
            if (!go) return;

            foreach (var (key, _) in AdvancedDefaults) ArfPrefs.DeleteKey(key);

            _exportSuffix            = "Rig_Feet";
            _exportFolder            = "";
            _swapMethod              = SwapMethod.Legacy;
            _autoSwapMethod          = true;
            _backupEnabled           = true;
            _timeoutMin              = 10;
            _autoMapFeet             = true;
            _rigOnBestShape          = true;
            _zeroFeetShapeKeys       = false;
            _cleanForeignToes        = true;
            _unlinkedPrefabSceneOnly = false;
            _footHelper              = true;
            _makeShrinkKeys          = true;

            // Per-row ticks are choices about one avatar, not settings, and they live only in memory.
            _foreignToeUnticked.Clear();
            _foreignToeAdded.Clear();
            _fitRowFlipped.Clear();
            _shrinkRowFlipped.Clear();

            _prefabsToAdd.Clear();
            foreach (string p in DefaultPrefabPaths)
                _prefabsToAdd.Add(AssetDatabase.LoadAssetAtPath<GameObject>(p));

            // The avatar is re-checked because several of these decide what the panel shows about it.
            OutfitDetect.Forget();
            if (_avatarObject != null) OnAvatarChanged();

            SetStatus("Settings are back to their defaults. Blender/script paths, the FBX, the foot bones "
                      + "and your outfit list were left as they are.", MessageType.Info);
            Repaint();
        }

        // ─── Advanced section ──────────────────────────────────────────────────

        void DrawAdvancedSection()
        {
            EditorGUI.BeginChangeCheck();
            _advancedFoldout = EditorGUILayout.Foldout(_advancedFoldout, "Advanced Settings", true);
            if (EditorGUI.EndChangeCheck())
                ArfPrefs.SetBool(PrefAdvanced, _advancedFoldout);

            if (!_advancedFoldout) return;

            EditorGUI.indentLevel++;

            // First thing inside the foldout: the way back. These settings are machine-wide, so one changed
            // for a single avatar otherwise follows the user into every project they open.
            DrawResetToDefaults();
            Separator();

            // ── What the run does to this avatar ─────────────────────────────
            // Both lists are filled in and ticked for you, so a run is correct without ever opening
            // them; they are here for the person who wants to see or change what was decided.
            if (_meshNames.Length > 0)
            {
                DrawFootShapeSection(_meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)]);
                DrawShapeKeyZeroSection();
                DrawForeignToeSection();
                Separator();
            }

            // ── Blender path ─────────────────────────────────────────────────
            GUILayout.Label("Blender", EditorStyles.boldLabel);

            string blenderPath = ArfPrefs.GetString(BlenderBridge.PrefBlenderPath, "");
            bool   blenderOk   = File.Exists(blenderPath);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            string newPath = EditorGUILayout.TextField(
                new GUIContent("blender.exe", "Full path to the Blender executable."),
                blenderPath);
            if (EditorGUI.EndChangeCheck())
                ArfPrefs.SetString(BlenderBridge.PrefBlenderPath, newPath);

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

            string overridePath = ArfPrefs.GetString(PrefAutoRigScriptPath, "");

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
                        ArfPrefs.SetString(PrefAutoRigScriptPath, isDefault ? "" : abs);
                    }
                    else
                    {
                        ArfPrefs.SetString(PrefAutoRigScriptPath, "");
                    }
                }
                else
                {
                    ArfPrefs.SetString(PrefAutoRigScriptPath, "");
                }
                _autoRigScriptResolvedPath = ResolveAutoRigScriptPath();   // reflect it immediately
            }
            if (GUILayout.Button("Browse…", GUILayout.Width(70)))
                BrowseForAutoRigScript();
            if (!string.IsNullOrEmpty(overridePath) && GUILayout.Button("×", GUILayout.Width(22)))
                ArfPrefs.SetString(PrefAutoRigScriptPath, "");
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
                ArfPrefs.SetBool(PrefBackupEnabled, _backupEnabled);

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
                ArfPrefs.SetInt(PrefTimeoutMin, _timeoutMin);
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
                ArfPrefs.SetBool(PrefAutoMapFeet, _autoMapFeet);

            Separator();

            // ── Foot helper bone ─────────────────────────────────────────────
            GUILayout.Label("Foot helper bone", EditorStyles.boldLabel);
            string helperPyVer = LocalPyVersion();
            bool helperSupported = string.IsNullOrEmpty(helperPyVer) || !IsOlder(helperPyVer, "3.16.0");
            EditorGUI.BeginDisabledGroup(!helperSupported);
            EditorGUI.BeginChangeCheck();
            _footHelper = EditorGUILayout.ToggleLeft(
                new GUIContent("Add a helper bone under each foot  (ARF Helper_L / _R)",
                    "A copy of the foot bone that takes over the foot's weights and becomes the parent of the "
                    + "toes. VRChat's IK moves the foot; the helper follows it and can be animated on top — "
                    + "rotate it to flex the whole foot without fighting the IK. At rest nothing looks "
                    + "different."),
                _footHelper);
            if (EditorGUI.EndChangeCheck())
                ArfPrefs.SetBool(PrefFootHelper, _footHelper);
            EditorGUI.EndDisabledGroup();
            if (!helperSupported)
                EditorGUILayout.LabelField("Needs AutoRig Feet script 3.16.0 or newer (installed: " + helperPyVer + ").",
                    EditorStyles.wordWrappedMiniLabel);

            Separator();

            // ── Avatars that are not linked to their FBX ─────────────────────
            GUILayout.Label("Avatars not linked to their FBX", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "An avatar you unpacked — or unpacked and saved as your own prefab — has no link to its FBX, "
                + "so Unity never carries new bones into it. AutoRig Feet adds them itself.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUI.BeginChangeCheck();
            int unlinkedChoice = EditorGUILayout.Popup(
                new GUIContent("Add the bones to",
                    "The prefab itself: every instance of it, in every scene, gets the new bones — its other "
                    + "instances are broken in exactly the same way.\n\n"
                    + "This avatar only: the prefab keeps the old skeleton, and each of its other instances "
                    + "stays broken until you select it and press Re-sync armature."),
                _unlinkedPrefabSceneOnly ? 1 : 0,
                new[]
                {
                    new GUIContent("The prefab itself  (recommended)"),
                    new GUIContent("This avatar only"),
                });
            if (EditorGUI.EndChangeCheck())
            {
                _unlinkedPrefabSceneOnly = unlinkedChoice == 1;
                ArfPrefs.SetBool(PrefUnlinkedPrefabSceneOnly, _unlinkedPrefabSceneOnly);
            }

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
                ArfPrefs.SetBool(PrefAutoSwapMethod, _autoSwapMethod);

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
                    ArfPrefs.SetInt(PrefSwapMethod, (int)_swapMethod);

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
            else if (_exportMode == ExportMode.Duplicate)
                // Legacy rebuilds the avatar on the new FBX object by object, so anything it has to
                // recreate comes out as a plain GameObject. Prefabs from other creators on the avatar
                // therefore arrive in the copy unpacked. Standard keeps them, so say so here rather
                // than let it be discovered on a finished avatar.
                EditorGUILayout.HelpBox(
                    "Legacy rebuilds the avatar object by object, so prefabs on it (hand controllers, "
                    + "gesture setups, props) lose their prefab link in the copy. Standard keeps them "
                    + "linked — use it unless Legacy is fixing something else for you.",
                    MessageType.Info);

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
                ArfPrefs.SetString(PrefFbxPath,
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
                    ArfPrefs.SetString(PrefSuffix, _exportSuffix);

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
                    ArfPrefs.SetString(PrefExportFolder, "");
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

            Separator();

            DrawFootShapeToggleTest();

            // The same button as at the top: after scrolling through every option, the way back is right here.
            Separator();
            DrawResetToDefaults();

            EditorGUI.indentLevel--;
        }

        // ── VRCFury foot-shape toggle (test) ───────────────────────────────────
        // Builds the same object and toggle a rig run will add, so the VRCFury side can be checked
        // in the scene on its own. The rows are the shape keys that are switched on and move the
        // feet (set to 0), plus anything typed in Extra keys.
        string _footShapeTestSpec = "";

        void DrawFootShapeToggleTest()
        {
            GUILayout.Label("VRCFury Foot-Shape Toggle (test)", EditorStyles.boldLabel);
            if (!FootShapeToggle.VRCFuryAvailable)
            {
                EditorGUILayout.HelpBox(
                    "VRCFury is not installed in this project, so the toggle can't be created.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(
                $"Adds \"{FootShapeToggle.ObjectName}\" under the avatar, with the menu toggle "
                + FootShapeToggle.MenuPath + ".",
                EditorStyles.wordWrappedMiniLabel);

            _footShapeTestSpec = EditorGUILayout.TextField(
                new GUIContent("Extra keys",
                    "Optional. Shape keys to put in the toggle, as name=value separated by ';' "
                    + "(value 0-100; a name on its own means 100). For example:\n"
                    + "High heeled=100; Foot_hill=0\n\n"
                    + "The ticked keys of the foot shape list (under Export Mode) are added automatically."),
                _footShapeTestSpec);

            bool hasAvatar = _avatarObject != null;
            bool exists    = hasAvatar && FootShapeToggle.Find(_avatarObject) != null;

            // The scene changes are deferred to after this GUI pass: creating or removing the object
            // mid-pass changes which controls are drawn, which Unity reports as a layout error.
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = hasAvatar;
            if (GUILayout.Button(exists ? "Update toggle on avatar" : "Create toggle on avatar",
                    GUILayout.Height(22)))
                EditorApplication.delayCall += CreateFootShapeToggleTest;
            GUI.enabled = exists;
            if (GUILayout.Button("Remove", GUILayout.Width(70), GUILayout.Height(22)))
                EditorApplication.delayCall += RemoveFootShapeToggleTest;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (!exists) return;
            if (FootShapeToggle.IsLastChild(_avatarObject))
            {
                EditorGUILayout.LabelField("✓ Present, and the last object under the avatar.",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "The toggle is no longer the last object under the avatar. A VRCFury toggle below it "
                    + "that sets the same shape keys would override it.",
                    MessageType.Warning);
                if (GUILayout.Button("Move to bottom", GUILayout.Height(20)))
                    EditorApplication.delayCall += () =>
                    {
                        if (_avatarObject != null) FootShapeToggle.MoveToBottom(_avatarObject);
                        Repaint();
                    };
            }
        }

        void CreateFootShapeToggleTest()
        {
            if (_avatarObject == null) return;
            var rows = new List<(string key, float value)>();
            var seen = new HashSet<string>();

            // Typed rows first, so a key can be forced to any value.
            foreach (string part in (_footShapeTestSpec ?? "").Split(';'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int eq = p.LastIndexOf('=');
                string key = (eq > 0 ? p.Substring(0, eq) : p).Trim();
                float value = 100f;
                if (eq > 0 && !float.TryParse(p.Substring(eq + 1).Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out value))
                {
                    SetStatus($"Couldn't read the value in \"{p}\". Use name=value, e.g. High heeled=100.",
                        MessageType.Warning);
                    Repaint();
                    return;
                }
                if (key.Length > 0 && seen.Add(key)) rows.Add((key, Mathf.Clamp(value, 0f, 100f)));
            }

            if (_meshNames.Length > 0)
            {
                string bodyName = _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)];
                // The ticked rows of the foot shape list: exactly what a run puts in the toggle. Without
                // that list (option off, or the check can't run), the switched-on feet shape keys at 0.
                var fit = _rigOnBestShape ? FootShapeFitNow(bodyName) : null;
                if (fit != null && fit.error == null)
                {
                    foreach (var r in IncludedFootShapeRows(fit))
                        if (seen.Add(r.key)) rows.Add((r.key, r.value));
                }
                else
                {
                    foreach (var o in GetFeetShapeOffenders(FindAvatarSkinnedMesh(bodyName)))
                        if (seen.Add(o.name)) rows.Add((o.name, 0f));
                }
            }

            var go = FootShapeToggle.CreateOrUpdate(_avatarObject, rows, out string error);
            if (go == null)
            {
                SetStatus("Foot-shape toggle not created: " + error
                    + (rows.Count == 0 ? " Type a shape key in Extra keys to test anyway." : ""),
                    MessageType.Warning);
            }
            else
            {
                // Pinged, not selected: selecting it would make this window adopt it as the avatar.
                EditorGUIUtility.PingObject(go);
                SetStatus($"Foot-shape toggle added under {_avatarObject.name} with {rows.Count} shape key(s): "
                    + string.Join(", ", rows.Select(r => $"{r.key} = {r.value:0.##}")),
                    MessageType.Info);
            }
            Repaint();
        }

        void RemoveFootShapeToggleTest()
        {
            if (_avatarObject != null && FootShapeToggle.Remove(_avatarObject))
                SetStatus("Foot-shape toggle removed.", MessageType.Info);
            Repaint();
        }

        // ─── Progress bar ──────────────────────────────────────────────────────

        void DrawProgressBarIfActive()
        {
            bool show = _state == State.BlenderRunning || _state == State.FBXSwapping ||
                        _state == State.AddingPrefabs  || _state == State.Restoring ||
                        _state == State.OutfitsRunning || _state == State.OutfitsRestoring ||
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
            // The body is already rigged by then: skipping the outfits stops them, and the body run finishes.
            if ((_state == State.OutfitsRunning || _state == State.OutfitsRestoring) &&
                GUILayout.Button(new GUIContent("Skip outfits", "Stop rigging the outfits from other FBXs; the body finishes."),
                                 GUILayout.Width(90), GUILayout.Height(20)))
                SkipRemainingOutfits();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        // ─── Automation (TestLab) ──────────────────────────────────────────────
        // The test harness drives this window the way a person does: pick an avatar, wait for the
        // checks, press Run, then tick until the state machine finishes. Every one of those steps is
        // private, and Update() only runs while a window is on screen, so these thin wrappers are the
        // whole seam. They change nothing by themselves — the dialogs are gated by ArfAutomation.Active
        // and the settings by ArfPrefs.Sandbox, both of which a harness sets before opening the window.

        internal void AutomationSelect(GameObject avatar)
        {
            _avatarObject = avatar;
            OnAvatarChanged();
            // OnGUI refreshes these every frame; headless there is no OnGUI, and Execute() refuses to
            // run without _depAutoRigScript.
            _depBlender                = File.Exists(ArfPrefs.GetString(BlenderBridge.PrefBlenderPath, ""));
            _depVRCFury                = HasVRCFury();
            _autoRigScriptResolvedPath = ResolveAutoRigScriptPath();
            _depAutoRigScript          = !string.IsNullOrEmpty(_autoRigScriptResolvedPath);
        }

        /// <summary>null when a run can start, otherwise what it is still waiting for.</summary>
        internal string AutomationNotReady()
        {
            if (_avatarObject == null) return "no avatar selected";
            if (!IsRootObject(_avatarObject)) return "the avatar is not a root object";
            if (_sourceFbxAsset == null || !IsValidFbx(_sourceFbxAsset)) return "no source FBX";
            if (_meshNames.Length == 0) return "the FBX has no meshes";
            if (!File.Exists(ArfPrefs.GetString(BlenderBridge.PrefBlenderPath, ""))) return "Blender not found";
            if (!_depAutoRigScript) return "the paid AutoRig script was not found";
            // Running before the foot-bone lists are read would measure a half-loaded window, which is
            // the very bug the 1.3.2 retry loop exists for.
            if (FootCandidatesPending) return "the foot-bone lists are still being read";
            return null;
        }

        /// <summary>Settings a scenario flips after OnEnable has already read the prefs.</summary>
        internal void AutomationSetOption(string option, bool value)
        {
            switch (option)
            {
                case "replaceMode":       _exportMode        = value ? ExportMode.Replace : ExportMode.Duplicate; break;
                case "autoSwapMethod":    _autoSwapMethod    = value; break;
                case "legacySwap":        _swapMethod        = value ? SwapMethod.Legacy : SwapMethod.Standard; break;
                case "backupEnabled":     _backupEnabled     = value; break;
                case "autoMapFeet":       _autoMapFeet       = value; break;
                case "rigOnBestShape":    _rigOnBestShape    = value; break;
                case "zeroFeetShapeKeys": _zeroFeetShapeKeys = value; break;
                case "cleanForeignToes":  _cleanForeignToes  = value; break;
                case "footHelper":        _footHelper        = value; break;
                case "makeShrinkKeys":    _makeShrinkKeys    = value; break;
                default: throw new ArgumentException("unknown option: " + option);
            }
        }

        internal void AutomationRun() => Execute();
        internal void AutomationResyncArmature() => ResyncArmatureManual();
        internal List<string> AutomationTickedOutfits() => TickedOutfits();
        internal string AutomationSourceFbx => AssetDatabase.GetAssetPath(_sourceFbxAsset);
        internal string AutomationBodyMesh =>
            _meshNames.Length > 0 ? _meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)] : "";
        internal void AutomationRestoreRig(string rigBackupJson) => RunRestore(rigBackupJson);
        internal void AutomationRestoreBackup(ShuganBackups.AssetGroup group, ShuganBackups.Entry entry)
            => RestoreBackup(group, entry);
        internal void AutomationTick() => Update();
        internal void AutomationResetDefaults() => ResetAdvancedSettings();

        internal string AutomationState => _state.ToString();
        internal bool   AutomationBusy  => _state != State.Idle && _state != State.Done && _state != State.Error;
        internal bool   AutomationFailed => _state == State.Error;
        internal ShuganTools.RunReport AutomationReport => _runReport;
        internal GameObject AutomationResult => _resultInstance;
        internal GameObject AutomationAvatar => _avatarObject;

        /// <summary>What clicking an object in the Hierarchy does: the panel takes the avatar it belongs to.</summary>
        internal void AutomationClick(GameObject go)
        {
            Selection.activeGameObject = go;
            TryAdoptSelectedAvatar();
        }

        // ─── Armature re-sync ──────────────────────────────────────────────────
        // Unity carries new bones into an avatar only while that avatar is still a prefab instance of the
        // FBX. An unpacked avatar — or one unpacked and saved as a new prefab — keeps the old skeleton and
        // its renderers stop matching the reimported mesh, at which point Unity quietly stops skinning them
        // and the meshes disappear. Measured on the Moe base: 276 bones against 376 bind poses, with
        // nothing at all in the console.

        ArmatureSync.UnlinkedPrefab UnlinkedPrefabMode =>
            _unlinkedPrefabSceneOnly ? ArmatureSync.UnlinkedPrefab.SceneOnly
                                     : ArmatureSync.UnlinkedPrefab.PrefabAsset;

        // Asked while the panel draws, so it is throttled and never throws: an avatar out of step with its
        // FBX is invisible in Unity (no error, no warning — the meshes simply stop being drawn), which is
        // precisely why the panel has to say it out loud.
        double _armatureCheckedAt;
        string _armatureProblem;

        string ArmatureProblem()
        {
            if (_avatarObject == null || _sourceFbxAsset == null) return null;

            double now = EditorApplication.timeSinceStartup;
            if (now - _armatureCheckedAt < 0.5) return _armatureProblem;
            _armatureCheckedAt = now;
            _armatureProblem   = null;

            try
            {
                string fbx = AssetDatabase.GetAssetPath(_sourceFbxAsset);
                if (string.IsNullOrEmpty(fbx)) return null;

                var check = ArmatureSync.ValidateOnly(_avatarObject, fbx);
                if (check.unbound.Count > 0)
                {
                    _armatureProblem = check.unbound.Count + " mesh(es) no longer match the skeleton and will "
                                     + "not render: " + string.Join("; ", check.unbound.Take(2))
                                     + (check.unbound.Count > 2 ? "…" : "");
                }
                else
                {
                    int missing = ArmatureSync.CountMissingBones(_avatarObject, fbx);
                    if (missing > 0)
                        _armatureProblem = missing + " bone(s) the FBX has are missing from the avatar.";
                }
            }
            catch { _armatureProblem = null; }

            return _armatureProblem;
        }

        void CaptureArmatureBefore()
        {
            _armatureBefore = null;
            _hasHumanBefore = false;
            string fbx = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            if (string.IsNullOrEmpty(fbx)) return;

            _armatureBefore = ArmatureSync.Capture(fbx);
            if (AssetImporter.GetAtPath(fbx) is ModelImporter importer)
            {
                _humanBefore    = importer.humanDescription;
                _hasHumanBefore = true;
            }
        }

        /// <summary>Bring the scene avatar back in step with the FBX the run just rewrote.</summary>
        void ResyncArmatureAfterRun(GameObject avatar, string fbxPath)
        {
            if (avatar == null || string.IsNullOrEmpty(fbxPath)) return;
            try
            {
                var result = ArmatureSync.Sync(avatar, fbxPath, _armatureBefore, UnlinkedPrefabMode);
                ReportArmatureResult(result);
            }
            catch (Exception e)
            {
                _runReport?.AddIssue("U_ARMATURE_RESYNC_FAILED", "warning",
                    "The avatar in the scene could not be brought in step with the FBX: " + e.Message,
                    "Duplicate mode builds a fresh copy instead of changing your avatar.");
            }
        }

        void ReportArmatureResult(ArmatureSync.Result r)
        {
            if (_runReport == null || r == null) return;

            if (r.error != null)
                _runReport.AddIssue("U_ARMATURE_RESYNC_FAILED", "warning",
                    "The avatar in the scene could not be checked against the FBX: " + r.error);
            else if (r.Changed)
                _runReport.AddIssue("U_ARMATURE_RESYNCED", "info",
                    "The avatar in the scene was brought in step with the FBX: " + r.Summary() +
                    (r.prefabAssetUpdated != null
                        ? ". The prefab " + r.prefabAssetUpdated + " was updated too, so its other instances match."
                        : "."),
                    r.linked ? null : "Your avatar is not a prefab instance of its FBX, so the new bones were "
                                    + "added to it directly.");

            if (r.prefabAssetStale != null)
                _runReport.AddIssue("U_ARMATURE_PREFAB_STALE", "warning",
                    "The prefab " + r.prefabAssetStale + " still has the old skeleton, so its other instances "
                    + "will not show the new bones.",
                    "Select each of them and press Re-sync armature, or switch \"Unlinked prefab\" in "
                    + "Advanced Settings back to updating the prefab itself.");

            if (r.keptPoses.Count > 0)
                _runReport.AddIssue("U_ARMATURE_POSE_KEPT", "info",
                    r.keptPoses.Count + " bone(s) sit where your avatar had them rather than where the new "
                    + "FBX puts them: " + string.Join(", ", r.keptPoses.Take(6))
                    + (r.keptPoses.Count > 6 ? "…" : "") + ".",
                    "Their rest pose in the avatar no longer matches the FBX's, so the run left them alone "
                    + "instead of overwriting something you may have set on purpose.");

            if (r.unbound.Count > 0)
                _runReport.AddIssue("U_ARMATURE_BIND_MISMATCH", "fatal",
                    "These meshes still do not match their skeleton, so they will not render: "
                    + string.Join("; ", r.unbound.Take(6)) + (r.unbound.Count > 6 ? "…" : "") + ".",
                    "Restore the FBX from the Backups tab, then rig with Duplicate mode instead.");
        }

        /// <summary>The panel button: fix an avatar whose FBX changed at some point in the past — an older
        /// run, or a file someone replaced by hand.</summary>
        internal void ResyncArmatureManual()
        {
            if (_avatarObject == null) { SetStatus("Select the avatar first.", MessageType.Warning); return; }
            string fbx = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            if (string.IsNullOrEmpty(fbx)) { SetStatus("No source FBX for this avatar.", MessageType.Warning); return; }

            bool fresh = _runReport == null;
            if (fresh) _runReport = new ShuganTools.RunReport();

            // No snapshot: nothing here knows what the FBX used to look like, so the sync only adds what the
            // meshes need and only removes bones this tool owns.
            var result = ArmatureSync.Sync(_avatarObject, fbx, null, UnlinkedPrefabMode);
            ReportArmatureResult(result);
            RepairHumanoidAfterSkeletonChange(fbx);

            SetStatus(result.Ok
                    ? "Armature re-synced with the FBX: " + result.Summary() + "."
                    : "Re-sync could not fix every mesh: " + string.Join("; ", result.unbound.Take(3)),
                result.Ok ? MessageType.Info : MessageType.Error);

            RefreshRigState();
            Repaint();
        }

        /// <summary>After a file was put back from the Backups tab: every avatar in the open scene that uses
        /// it is now measuring itself against a different mesh, whether or not it was the selected one.</summary>
        void ResyncSceneAvatarsUsing(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) ||
                !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return;

            int fixedUp = 0;
            var before = _armatureBefore != null &&
                         string.Equals(_armatureBefore.fbxPath, assetPath, StringComparison.OrdinalIgnoreCase)
                ? _armatureBefore : null;

            foreach (var root in OtherOpenSceneUsers(assetPath, null))
            {
                var result = ArmatureSync.Sync(root, assetPath, before, UnlinkedPrefabMode);
                ReportArmatureResult(result);
                if (result.Changed) fixedUp++;
            }
            if (fixedUp > 0)
                UnityEngine.Debug.Log($"[AutoRig Feet Distributor] Re-synced {fixedUp} avatar(s) in the scene "
                                      + "with the restored FBX.");
        }

        void RepairHumanoidAfterSkeletonChange(string fbxPath)
        {
            try
            {
                string note = HumanoidRigMapping.RepairAfterSkeletonChange(
                    fbxPath, _hasHumanBefore, _humanBefore);
                if (!string.IsNullOrEmpty(note))
                    _runReport?.AddIssue("U_HUMANOID_REPAIRED", "info", note);
            }
            catch (Exception e)
            {
                _runReport?.AddIssue("U_HUMANOID_MAP", "warning",
                    "The humanoid mapping could not be checked after the skeleton changed: " + e.Message);
            }
        }

        // ─── Execute ───────────────────────────────────────────────────────────

        void Execute()
        {
            string blenderPath = ArfPrefs.GetString(BlenderBridge.PrefBlenderPath, "");
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
            // Replace: Blender writes a temp file, which only goes over the source once it is complete and checked.
            _blenderExportPath = _exportMode == ExportMode.Replace ? NewTempExport("body") : _exportPath;
            if (_exportMode == ExportMode.Replace)
            {
                string notWritable = FbxNotWritable(_exportPath);
                if (notWritable != null)
                {
                    SetStatus("The FBX can't be overwritten: " + notWritable + ". Nothing was changed.", MessageType.Error);
                    return;
                }
            }
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

            if (_exportMode == ExportMode.Replace && !ConfirmReplaceMode()) return;

            // Toe bones from another rig: the ticked ones are removed in Blender. Replace already lists them
            // in its dialog; a Duplicate run asks here, because it is still a change the user may not expect.
            var cleanToes = ForeignToeBonesToClean();
            if (_exportMode == ExportMode.Duplicate && cleanToes.Count > 0 && !ConfirmForeignToeCleaning(cleanToes))
                return;
            _cleanToeBonesJson = _cleanForeignToes
                ? "[" + string.Join(", ", cleanToes.Select(t => "\"" + JsonEscape(t.name) + "\"")) + "]"
                : null;
            // Blender takes them out of the FBX skeleton; the scene copy is built from the avatar, so the
            // GameObjects survive there and have to be removed after the swap (RemoveCleanedToeBoneObjects).
            _cleanToeBoneNames = cleanToes.Select(t => t.name).ToList();

            // RE-RIG: the source FBX already carries a rig, so this run will replace it. Ask how to
            // treat the existing foot weights before anything is written.
            if (!ConfirmReRigStrategy(out _rerigRestoreJson, out _rerigSkipFootReduction, out _rerigProtect)) return;

            // FOOT SHAPE: which shape the rig is built on, what the toggle sets afterwards, and the
            // shape-key values the result must get back. Measured here if the background check hasn't
            // finished yet (a second or two at most).
            _rigShapeJson       = null;
            _plannedToggleRows  = null;
            _plannedShrinkKeys  = null;
            _blendShapeSnapshot = null;
            if (_rigOnBestShape)
            {
                var fit = FootShapeFitNow(targetMesh);
                if (fit != null && fit.error == null)
                {
                    _plannedToggleRows = IncludedFootShapeRows(fit);
                    _rigShapeJson      = RigShapeJson(_plannedToggleRows);
                    _plannedShrinkKeys = IncludedShrinkKeys(fit);
                }
                _blendShapeSnapshot = BlendShapeWeightSnapshot.Capture(_avatarObject);
            }

            // What the FBX looks like BEFORE Blender rewrites it. The re-sync afterwards needs this to tell
            // a bone the FILE moved from one the USER moved, and to know which bones the run removed.
            CaptureArmatureBefore();

            BackupScene();
            BackupOriginalFbx();

            _blenderMilestone  = 0f;
            _displayProgress   = 0f;
            _currentStepLabel  = "Starting Blender…";
            _resultInstance    = null;
            _createdPrefabPath = null;
            lock (_outputLock) _outputQueue.Clear();

            // Garment meshes to also select in Blender (body's toe + foot weights get transferred to
            // them). Drop blanks, the body itself, and duplicates.
            string[] garmentNames = TickedOutfits()
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

            // The shape-key values the result gets back, kept next to the run log too, so they can be
            // re-applied by hand if anything goes wrong after the swap.
            if (_blendShapeSnapshot != null && _runLogger != null && !string.IsNullOrEmpty(_runLogger.FolderAbs))
            {
                try
                {
                    BlendShapeWeightSnapshot.Save(_blendShapeSnapshot,
                        Path.Combine(_runLogger.FolderAbs, "blendshape_weights.json"));
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning("[AutoRig Feet] Could not save blendshape_weights.json: " + e.Message);
                }
            }

            // Fresh run report; the launch timestamp anchors the "was a NEW FBX actually
            // exported?" check (a stale file on disk must not count as success).
            _runReport         = new ShuganTools.RunReport();
            _runStartTicksUtc  = DateTime.UtcNow.Ticks;

            // Outfits from other FBXs (experimental): one Blender run each after the body. Null when none is ticked,
            // and the body run is then exactly what it was.
            RecordBeginRun("rig");
            if (_recRun != null)
            {
                _recRun.toggleRows = (_plannedToggleRows ?? new List<FootShapeFit.ToggleRow>())
                    .Select(r => r.key + "=" + r.value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
                _recRun.shrinkKeys = new List<string>(_plannedShrinkKeys ?? new List<string>());
                _rec?.Save();
            }
            string outfitBlendPath = PrepareExternalOutfits(DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            string sourceFbxAbs = ToAbsPath(AssetDatabase.GetAssetPath(_sourceFbxAsset));
            string pythonCode   = BlenderBridge.BuildAutoRigFeetScript(
                sourceFbxAbs, targetMesh, _blenderExportPath, scriptPath,
                headless: true, stepDelay: 0f, garmentNames: garmentNames,
                backupJsonPath: backupJsonPath,
                footBoneL: _footOverrideL, footBoneR: _footOverrideR,
                rerigRestoreJsonPath: _rerigRestoreJson,
                skipFootReduction: _rerigSkipFootReduction,
                rigShapeJson: _rigShapeJson,
                cleanToeBonesJson: _cleanToeBonesJson,
                footHelper: _footHelper,
                backupSearchPrefix: BackupSearchPrefix(),
                rerigProtect: _rerigProtect,
                outfitBlendPath: outfitBlendPath,
                shrinkKeys: MakeShrinkKeysNow);

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
                if (_recRun != null) { _recRun.createdPrefab = _createdPrefabPath; _rec?.Save(); }
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

                // The avatar may already carry this prefab, and the copy can be in one of two states.
                // A LIVE instance (it came across the swap still linked to its asset) is left exactly
                // as it is, overrides included. A LOOSE copy — same name, no link, typically flattened
                // by an older run — is replaced by a real instance, so the finished avatar follows the
                // prefab instead of holding a frozen snapshot of it.
                var existing = FindDirectChild(_resultInstance.transform, prefabRef.name);
                if (existing != null)
                {
                    if (IsInstanceOfPrefabAsset(existing.gameObject, prefabRef))
                    {
                        UnityEngine.Debug.Log(
                            $"[AutoRig Feet Distributor] '{existing.name}' is already on '{_resultInstance.name}' "
                            + "as a prefab instance — kept as it is.");
                        continue;
                    }
                    UnityEngine.Debug.LogWarning(
                        $"[AutoRig Feet Distributor] '{existing.name}' under '{_resultInstance.name}' had no prefab "
                        + $"link — replaced with the '{prefabRef.name}' prefab.");
                    _runReport?.AddIssue("U_PREFAB_RELINKED", "info",
                        $"\"{existing.name}\" was on the avatar without a prefab link, so it was replaced with the "
                        + $"\"{prefabRef.name}\" prefab.",
                        "Any change you had made to that copy is gone — it now follows the prefab again.");
                    Undo.DestroyObjectImmediate(existing.gameObject);
                }
                var child = (GameObject)PrefabUtility.InstantiatePrefab(
                    prefabRef, _resultInstance.transform);
                Undo.RegisterCreatedObjectUndo(child, "AutoRig Feet — Add Prefab");
                RecordAdded(child);
            }
            Selection.activeGameObject = _resultInstance;
        }

        // The child named `name`, or one Unity numbered ("Name (1)") / a suffixed variant, or null.
        // Same matching as ChildNameExists, which decided whether to skip a prefab.
        static Transform FindDirectChild(Transform parent, string name)
        {
            foreach (Transform t in parent)
                if (t.name == name || t.name.StartsWith(name + " ")) return t;
            return null;
        }

        // True when `go` is a live instance of `prefabAsset` (nested instances included).
        static bool IsInstanceOfPrefabAsset(GameObject go, GameObject prefabAsset)
        {
            if (go == null || prefabAsset == null) return false;
            if (!PrefabUtility.IsPartOfPrefabInstance(go)) return false;
            string want = AssetDatabase.GetAssetPath(prefabAsset);
            if (string.IsNullOrEmpty(want)) return false;
            try
            {
                string have = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) return true;
                var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
                return src != null && string.Equals(
                    AssetDatabase.GetAssetPath(src), want, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
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
            SyncRecordStage();
            SyncReloadLock();
            CleanOutfitTemp(keep: false);
            SetStatus("Cancelled — nothing was changed (the FBX is only written once Blender has finished "
                      + "and the file is complete).", MessageType.Info);
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
                ? (_restoreTempExport ?? ToAbsPath(AssetDatabase.GetAssetPath(_sourceFbxAsset)))
                : (_blenderExportPath ?? _exportPath);
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
                        ArfPrefs.SetString(PrefLastReportPrefix + guid, path);
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
                string path = ArfPrefs.GetString(PrefLastReportPrefix + guid, "");
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
                            ArfPrefs.DeleteKey(PrefLastReportPrefix + guid);
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
                string blenderPath = ArfPrefs.GetString(BlenderBridge.PrefBlenderPath, "");
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
                    new KeyValuePair<string, string>("garments",       string.Join(", ", TickedOutfits().ToArray())),
                    new KeyValuePair<string, string>("alreadyRigged",  _alreadyRigged.ToString()),
                    new KeyValuePair<string, string>("riggedWithScript",
                        string.IsNullOrEmpty(_riggedVersion) ? "(unknown / pre-3.9.0)" : _riggedVersion),
                    // Which re-rig strategy the user picked, so a bug report explains why the foot
                    // weights look the way they do.
                    new KeyValuePair<string, string>("rerigStrategy",
                        _rerigProtect == true && !_rerigSkipFootReduction ? "restore original weights first"
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
                    garmentMeshNames = new List<string>(TickedOutfits()),
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
            meshes.AddRange(TickedOutfits());

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
            // Ticks and added rows in the lists belong to the avatar they were made on.
            _fitRowFlipped.Clear();
            _shrinkRowFlipped.Clear();
            _originalPlan = null;
            _foreignToeUnticked.Clear();
            _foreignToeAdded.Clear();
            RefreshRigState();
            // Same moment as the shape-key and bone checks: if the foot-bone lists are empty, ask the
            // FBX again. A domain reload leaves them empty while the AssetDatabase is still busy.
            RearmFootCandidates();
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
            // The feet caches are keyed by mesh, and a run swaps the meshes out: rebuild them too.
            _feetVertsByMesh.Clear();
            _shapeDeltaByMesh.Clear();
            _scanNoteByMesh.Clear();
            _fit         = null;
            _fitKey      = null;
            _fitInput    = null;
            _fitInputKey = null;
            if (_avatarObject == null) return;
            _alreadyRigged  = HasFeetRigBones(_avatarObject);
            if (_alreadyRigged) _riggedVersion = GetRiggedScriptVersion(_avatarObject);
        }

        // Chosen in ConfirmReRigStrategy, consumed when building the Blender script.
        string _rerigRestoreJson;
        bool   _rerigSkipFootReduction;
        bool?  _rerigProtect;

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
        bool ConfirmReRigStrategy(out string restoreJson, out bool skipFootReduction, out bool? protect)
        {
            restoreJson       = null;
            skipFootReduction = false;
            protect           = null;

            if (!SourceFbxIsRigged()) return true;   // normal first rig — nothing to ask

            // The backup inside the FBX (script 3.16.0+) comes first; a JSON file in _Backups is the fallback.
            string backup    = OldestRigBackup();
            bool   inFbx     = SourceFbxHasWeightBackup();
            bool   hasBackup = inFbx || !string.IsNullOrEmpty(backup);
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
                ? "PROTECT: restores the original weights of the body and the outfits from your backup "
                  + "first, then rigs.\n"
                  + "    " + (inFbx ? "the backup stored inside the FBX" : Path.GetFileName(backup)) + "\n"
                  + "    Result: the new rig is built from the un-rigged mesh, exactly like the "
                  + "first time.\n\n"
                : "KEEP: no backup was found for this FBX, so the original weights can't be "
                  + "restored.\n    Instead the foot reduction is skipped, so nothing degrades "
                  + "further. The toe bones and their weights are still fully rebuilt; only the "
                  + "foot/toe blend keeps the earlier run's shaping.\n\n";

            int choice = ArfAutomation.Choose(
                ArfAutomation.AskRerigStrategy,
                "Re-apply AutoRig Feet?",
                head + protectHow
                     + "RE-APPLY AS-IS: rigs without protecting anything. The foot weights are "
                     + "reduced again.",
                protectLabel,            // 0
                "Cancel",                // 1  (middle button = the safe default on Esc)
                "Re-apply as-is");       // 2

            if (choice == 1) return false;

            protect = choice == 0;
            if (choice == 0)
            {
                if (hasBackup) restoreJson = backup;   // null when only the FBX holds one
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
                ArfPrefs.SetString(PrefFbxPath, path);
                RefreshMeshNames();
                _selectedMeshIndex = GuessBodyMeshIndex();
                ArfPrefs.SetInt(PrefMeshIndex, _selectedMeshIndex);
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
        // The FBX the lists and the manual picks belong to. Serialized so a script reload doesn't look
        // like a new FBX: that used to wipe the user's manual foot pick on every recompile.
        [SerializeField] string _footCandFbxPath = "";
        bool   _footCandLoaded;      // lists computed for _footCandFbxPath (recomputed after a reload)
        int    _footCandAttempts;
        double _footCandNextTry;
        const int    FootCandMaxAttempts  = 20;
        const double FootCandRetrySeconds = 0.5;
        // After the fast burst the answer is accepted, so the run button stops waiting on it. An EMPTY
        // answer is not accepted as the truth though: it keeps being retried, slowly, because the way
        // it usually happens is a script recompile (updating the package from VCC, say) reloading the
        // domain while the AssetDatabase is still busy. Every fast attempt then comes back empty, and
        // the empty lists used to stay until the user clicked another avatar and back.
        const double FootCandSlowSeconds  = 2.0;
        const int    FootCandSlowAttempts = 90;

        // True while the foot-bone candidates are still waiting for a readable FBX.
        bool FootCandidatesPending => !_footCandLoaded && !string.IsNullOrEmpty(_footCandFbxPath);

        // An FBX we have no bones for. Either it has none to rank, or it could not be read yet.
        bool FootCandidatesEmpty =>
            !string.IsNullOrEmpty(_footCandFbxPath) && (_footCandL.Count == 0 || _footCandR.Count == 0);

        bool FootCandidatesRetryDue =>
            FootCandidatesEmpty &&
            _footCandAttempts < FootCandMaxAttempts + FootCandSlowAttempts &&
            EditorApplication.timeSinceStartup >= _footCandNextTry;

        // Ask again for lists we never got, without touching the manual picks: the FBX has not changed,
        // only our answer about it was worthless. Called whenever the avatar is re-checked and when the
        // window is focused, so a recompile never leaves the two foot rows empty.
        void RearmFootCandidates()
        {
            if (!FootCandidatesEmpty) return;
            _footCandLoaded   = false;
            _footCandAttempts = 0;
            _footCandNextTry  = 0;
        }

        void RefreshFootCandidates()
        {
            string path = _sourceFbxAsset != null ? AssetDatabase.GetAssetPath(_sourceFbxAsset) : "";
            if (path != _footCandFbxPath)
            {
                _footCandFbxPath  = path;
                _footOverrideL    = "";
                _footOverrideR    = "";
                _footCandLoaded   = false;
                _footCandAttempts = 0;
            }
            if (_footCandLoaded && !FootCandidatesEmpty) return;
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
            // An FBX that can't be read at this moment (Unity still importing it, or reloading scripts)
            // gives no candidates at all. That answer used to be cached for the path, which left both foot
            // rows on "Auto / None" until another avatar was clicked. It is retried from Update instead,
            // and the run button waits for it; after FootCandMaxAttempts it is accepted as it is.
            _footCandAttempts++;
            bool empty = !string.IsNullOrEmpty(path) && (_footCandL.Count == 0 || _footCandR.Count == 0);
            _footCandLoaded  = !empty || _footCandAttempts >= FootCandMaxAttempts;
            _footCandNextTry = EditorApplication.timeSinceStartup
                             + (_footCandAttempts < FootCandMaxAttempts ? FootCandRetrySeconds
                                                                        : FootCandSlowSeconds);
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
                curBone, typeof(Transform), true, GUILayout.Width(RefFieldWidth));
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

            return ArfAutomation.Confirm(
                ArfAutomation.AskFootBonesUncertain,
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

        // Width of the fields that show a scene object next to a dropdown, wide enough for long mesh and
        // bone names.
        const float RefFieldWidth = 210f;
        const float GarmentRefWidth = 182f;

        /// <summary>
        /// The avatar a clicked object belongs to, so selecting a mesh or a bone deep in the hierarchy picks
        /// the whole avatar. The outermost object carrying an avatar descriptor wins (found by type name:
        /// this tool doesn't need the VRChat SDK); without one, the top of the hierarchy.
        /// </summary>
        static GameObject ResolveAvatarRoot(GameObject go)
        {
            if (go == null || !go.scene.IsValid()) return go;
            GameObject withDescriptor = null;
            for (var t = go.transform; t != null; t = t.parent)
                foreach (var c in t.GetComponents<Component>())
                    if (c != null && c.GetType().Name == "VRCAvatarDescriptor") { withDescriptor = t.gameObject; break; }
            return withDescriptor != null ? withDescriptor : go.transform.root.gameObject;
        }

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
                ArfPrefs.SetString(BlenderBridge.PrefBlenderPath, found);
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
            ArfPrefs.SetString(BlenderBridge.PrefBlenderPath, picked.Replace("/", "\\"));
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
            string overridePath = ArfPrefs.GetString(PrefAutoRigScriptPath, "");
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
            ArfPrefs.SetString(PrefAutoRigScriptPath, picked.Replace("/", "\\"));
            Repaint();
        }

        // ─── Backup ────────────────────────────────────────────────────────────

        /// <summary>
        /// Back up the scene the avatar lives in, before the run changes it.
        ///
        /// Two backups when the scene has unsaved changes, and the order is the point:
        ///
        ///   1. the scene file AS IT SITS ON DISK — without the user's pending edits. Someone who
        ///      was mid-experiment and did not want those edits kept has this to come back to.
        ///   2. then the scene is saved, and backed up again — now including those edits, which is
        ///      the state the run is actually about to modify.
        ///
        /// A clean scene needs only one, since disk and memory already agree.
        ///
        /// Saving is not optional here: without it the second backup would capture nothing the first
        /// did not, and the user's in-progress work would sit outside every restore point. Backup 1
        /// is what makes saving safe rather than presumptuous.
        /// </summary>
        void BackupScene()
        {
            try
            {
                if (_avatarObject == null) return;
                var scene = _avatarObject.scene;
                if (!scene.IsValid()) return;

                if (string.IsNullOrEmpty(scene.path))
                {
                    // Never saved — there is no file to copy, and saving would need a Save As dialog.
                    _runReport.AddIssue("U_SCENE_UNSAVED", "warning",
                        "This scene has never been saved, so it could not be backed up.",
                        "Save the scene once (Ctrl+S) and run again if you want a restore point for it. "
                        + "Your FBX is still backed up as usual.");
                    UnityEngine.Debug.LogWarning(
                        "[AutoRig Feet Distributor] Scene has no path yet — scene backup skipped.");
                    return;
                }

                string stamp = ShuganBackups.Stamp();
                bool   dirty = scene.isDirty;

                // 1. The on-disk state, before any pending edits are written out.
                if (dirty)
                {
                    string pre = ShuganBackups.BackupAsset(scene.path, "_presave", stamp);
                    if (pre != null)
                        UnityEngine.Debug.Log(
                            "[AutoRig Feet Distributor] Scene backup (before your unsaved changes): " + pre);

                    // 2. Save, so the next backup captures the state the run will actually modify.
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                }

                string post = ShuganBackups.BackupAsset(scene.path, "", stamp);
                if (post != null)
                    UnityEngine.Debug.Log("[AutoRig Feet Distributor] Scene backup: " + post);

                _runReport.AddIssue("U_SCENE_BACKED_UP", "info",
                    dirty
                        ? "Scene backed up twice: once as it was on disk before your unsaved changes, "
                          + "then again after saving them."
                        : "Scene backed up (it had no unsaved changes).",
                    "Both are in the _Backups folder next to the scene, and listed on the Backups tab.");
            }
            catch (Exception ex)
            {
                // A scene backup is a safety net, not a precondition — never block the rig over it.
                _runReport.AddIssue("U_SCENE_BACKUP_FAILED", "warning",
                    "The scene could not be backed up: " + ex.Message,
                    "The run continues, and your FBX backup is unaffected.");
                UnityEngine.Debug.LogWarning(
                    "[AutoRig Feet Distributor] Scene backup failed: " + ex.Message);
            }
        }

        void BackupOriginalFbx()
        {
            _lastFbxBackup = null;
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

            // Shared with the scene backup: same naming, same _Backups location, and the same
            // fresh-guid .meta copy (a verbatim .meta duplicates the original's guid and Unity then
            // logs a GUID conflict on every import, forever).
            string dest = ShuganBackups.BackupAsset(AssetDatabase.GetAssetPath(_sourceFbxAsset));
            _lastFbxBackup = dest;
            if (dest != null)
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
            if (!File.Exists(ArfPrefs.GetString(BlenderBridge.PrefBlenderPath, ""))) return false;
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
                case State.OutfitsRunning:   return "Rigging outfits…";
                case State.OutfitsRestoring: return "Restoring outfits…";
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
            else if (!File.Exists(ArfPrefs.GetString(BlenderBridge.PrefBlenderPath, "")))
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
            ArfPrefs.SetString(PrefExportFolder, _exportFolder);
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
            SyncRecordStage();
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
