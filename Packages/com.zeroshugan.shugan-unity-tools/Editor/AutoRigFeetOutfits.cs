using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    // ─── Outfits from other FBXs (experimental) ───────────────────────────────
    // Many outfits ship as their own FBX, with their own armature using the body's bone names, and are joined to
    // the avatar at build time (VRCFury Armature Link, Modular Avatar). They never got the toe rig, so shoes and
    // socks stayed stiff while the toes moved.
    //
    // The body is rigged exactly as before. When such an outfit is ticked, the body run also keeps the rigged
    // body as a .blend, and every outfit FBX then gets its own Blender run: the outfit is imported next to that
    // body, the bones the rig added or changed are copied into the outfit's armature at the same place in the
    // world, the foot/toe weights are transferred under the same shape-key rules, and the outfit alone is
    // exported. Replace writes it over the outfit FBX (a backup first); Duplicate gives the copied avatar its own
    // copy of the outfit. Assets are changed, never a prefab instance's structure, so every prefab under the avatar
    // stays linked; a check at the end says so, or names the one that is not.
    public partial class AutoRigFeetDistributor
    {
        const string PrefExternalOutfitsPrefix = "ShuganTools_ARF_ExternalOutfits_";   // + body FBX GUID

        class OutfitJob
        {
            public string   fbxPath;          // the outfit FBX in the project
            public string[] meshes;
            public string   exportAbs;        // where Blender writes the rigged outfit (temp)
            public string   backupJson;
            public string   footL, footR, toeL, toeR;
            public ArmatureSync.Snapshot before;
            public HumanDescription human;
            public bool     hasHuman;
            public bool     ok;
            public string   copyPath;         // Duplicate: the outfit copy the duplicated avatar uses
            public ShuganTools.RunReport report;
        }

        struct ExternalRow
        {
            public string fbxPath, mesh, key, why;
            public SkinnedMeshRenderer smr;
            public bool detected;
            public int hits;
        }

        struct PrefabLink
        {
            public string path, asset;
        }

        readonly List<OutfitJob> _outfitJobs = new List<OutfitJob>();
        int    _outfitJobIndex = -1;
        bool   _outfitRestoreMode;
        string _outfitTempDir;
        string _outfitBlendPath;
        List<PrefabLink> _prefabLinksBefore;
        readonly Dictionary<string, string> _prefabRepointed = new Dictionary<string, string>();

        // ─── Which outfits ─────────────────────────────────────────────────────

        string ExternalOutfitPrefKey()
        {
            string path = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            string guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid) ? null : PrefExternalOutfitsPrefix + guid;
        }

        // A mesh from another FBX is listed for you but never ticked for you — rigging one is experimental,
        // and it changes a file the avatar's creator may share with other outfits. So the setting is simply the
        // set of ticked ones; "+" is kept as the entry marker, which the older two-sided format also used.
        void LoadExternalChoices(out HashSet<string> ticked)
        {
            ticked = new HashSet<string>(StringComparer.Ordinal);
            string key = ExternalOutfitPrefKey();
            if (key == null) return;
            foreach (string entry in ArfPrefs.GetString(key, "").Split('|'))
                if (entry.Length > 1 && entry[0] == '+') ticked.Add(entry.Substring(1));
        }

        void SaveExternalChoices(HashSet<string> ticked)
        {
            string key = ExternalOutfitPrefKey();
            if (key == null) return;
            ArfPrefs.SetString(key, string.Join("|", ticked.Select(n => "+" + n)));
        }

        static string ExternalKey(string fbxPath, string mesh) => AssetDatabase.AssetPathToGUID(fbxPath) + ":" + mesh;

        /// <summary>The body's foot bones, as the outfit detection should look for them in an outfit's armature.</summary>
        List<string> BodyFootBoneNames()
        {
            var names = new List<string>();
            if (!string.IsNullOrEmpty(_footOverrideL)) names.Add(_footOverrideL);
            if (!string.IsNullOrEmpty(_footOverrideR)) names.Add(_footOverrideR);
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(_sourceFbxAsset)) as ModelImporter;
            if (importer != null && importer.humanDescription.human != null)
                foreach (var h in importer.humanDescription.human)
                    if ((h.humanName == "LeftFoot" || h.humanName == "RightFoot") && !names.Contains(h.boneName))
                        names.Add(h.boneName);
            return names;
        }

        /// <summary>Every mesh under the avatar that comes from an FBX other than the body's, and every FBX they
        /// come from. One entry per mesh per FBX.</summary>
        List<(string fbxPath, string mesh, string key, SkinnedMeshRenderer smr)> ExternalMeshes()
        {
            var all = new List<(string, string, string, SkinnedMeshRenderer)>();
            if (_avatarObject == null || _sourceFbxAsset == null) return all;
            string bodyFbx = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var smr in _avatarObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                string path = AssetDatabase.GetAssetPath(smr.sharedMesh);
                if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, bodyFbx, StringComparison.OrdinalIgnoreCase)) continue;
                string key = ExternalKey(path, smr.sharedMesh.name);
                if (seen.Add(key)) all.Add((path, smr.sharedMesh.name, key, smr));
            }
            return all;
        }

        /// <summary>The meshes from another FBX the garment table lists: the ones their own FBX says sit on the
        /// feet, plus any the user ticked by hand. Detected is not the same as ticked — see LoadExternalChoices.</summary>
        List<ExternalRow> ExternalOutfitRows()
        {
            var rows = new List<ExternalRow>();
            if (_avatarObject == null || _sourceFbxAsset == null) return rows;
            var feet = BodyFootBoneNames();
            LoadExternalChoices(out var ticked);

            foreach (var g in ExternalMeshes().GroupBy(x => x.fbxPath, StringComparer.OrdinalIgnoreCase))
            {
                var found = OutfitDetect.Find(g.Key, null, feet)
                    .GroupBy(c => c.meshName)
                    .ToDictionary(c => c.Key, c => c.OrderBy(x => (int)x.reason).First(), StringComparer.Ordinal);
                foreach (var x in g)
                {
                    bool det = found.TryGetValue(x.mesh, out var c);
                    if (!det && !ticked.Contains(x.key)) continue;
                    rows.Add(new ExternalRow { fbxPath = x.fbxPath, mesh = x.mesh, key = x.key, smr = x.smr,
                                               detected = det, hits = det ? c.hits : 0,
                                               why = det ? c.Explain() : "added by you" });
                }
            }
            return rows;
        }

        /// <summary>outfit FBX path → the ticked meshes of it.</summary>
        Dictionary<string, List<string>> TickedExternalOutfits()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            LoadExternalChoices(out var ticked);
            foreach (var r in ExternalOutfitRows())
            {
                if (!ticked.Contains(r.key)) continue;
                if (!result.TryGetValue(r.fbxPath, out var list)) result[r.fbxPath] = list = new List<string>();
                if (!list.Contains(r.mesh)) list.Add(r.mesh);
            }
            return result;
        }

        string _outfitScriptKey;
        bool   _outfitScriptSupported, _shrinkScriptSupported;

        bool ScriptSupportsExternalOutfits() { ReadScriptFeatures(); return _outfitScriptSupported; }

        /// <summary>The shrink shape keys arrived in script 3.17.0.</summary>
        bool ScriptSupportsShrinkKeys() { ReadScriptFeatures(); return _shrinkScriptSupported; }

        void ReadScriptFeatures()
        {
            string path = ResolveAutoRigScriptPath();
            if (string.IsNullOrEmpty(path)) { _outfitScriptKey = null; _outfitScriptSupported = _shrinkScriptSupported = false; return; }
            string abs = ToAbsPath(path);
            string key = abs;
            try { if (File.Exists(abs)) key += "|" + File.GetLastWriteTimeUtc(abs).Ticks; } catch { }
            if (key == _outfitScriptKey) return;
            _outfitScriptKey = key;
            try
            {
                string text = File.Exists(abs) ? File.ReadAllText(abs) : "";
                _outfitScriptSupported = text.Contains("def outfit_main(");
                _shrinkScriptSupported = text.Contains("def create_shrink_shape_keys(");
            }
            catch { _outfitScriptSupported = _shrinkScriptSupported = false; }
        }

        bool ExternalOutfitsSkippedBySwap =>
            _exportMode == ExportMode.Duplicate && EffectiveSwapMethod() == SwapMethod.Legacy;

        // ─── Panel ─────────────────────────────────────────────────────────────
        // The table itself is DrawGarmentSection, which lists these rows beside the body FBX's own outfits.

        /// <summary>How an outfit reaches the avatar: as the FBX itself, inside a prefab, or unpacked. Shown in the
        /// table so it is clear what a run will change.</summary>
        static string OutfitFormLabel(SkinnedMeshRenderer smr, string fbxPath)
        {
            if (smr == null) return "";
            if (!PrefabUtility.IsPartOfPrefabInstance(smr.gameObject) || PrefabUtility.IsAddedGameObjectOverride(smr.gameObject))
                return "unpacked";
            string asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(smr.gameObject);
            if (string.Equals(asset, fbxPath, StringComparison.OrdinalIgnoreCase)) return "FBX instance";
            var root = PrefabUtility.GetNearestPrefabInstanceRoot(smr.gameObject);
            if (root != null && ArmatureSync.IsLinkedTo(root, fbxPath)) return "prefab linked to the FBX";
            return "prefab (" + Path.GetFileName(asset) + ")";
        }

        // ─── Before the run ────────────────────────────────────────────────────

        /// <summary>Queue one Blender run per ticked outfit FBX. Called from Execute once the run report exists.
        /// Returns the .blend path the body run should keep, or null when there is nothing to do.</summary>
        string PrepareExternalOutfits(string blenderStamp)
        {
            _outfitJobs.Clear();
            _outfitJobIndex = -1;
            _outfitRestoreMode = false;
            _outfitBlendPath = null;
            _prefabRepointed.Clear();
            _prefabLinksBefore = CapturePrefabLinks(_avatarObject);

            var ticked = TickedExternalOutfits();
            if (ticked.Count == 0) return null;
            if (!ScriptSupportsExternalOutfits())
            {
                _runReport.AddIssue("U_OUTFIT_SKIPPED", "warning",
                    "Outfits from other FBXs were not rigged: the AutoRig Feet script is too old for them.",
                    "Update the paid script.");
                return null;
            }
            if (ExternalOutfitsSkippedBySwap)
            {
                _runReport.AddIssue("U_OUTFIT_SKIPPED_LEGACY", "info",
                    "Outfits from other FBXs (" + string.Join(", ", ticked.Keys.Select(Path.GetFileName)) + ") were not "
                    + "rigged: the Legacy swap in Duplicate mode doesn't support them.",
                    "Switch the swap method back to Automatic (Standard) to include them.");
                return null;
            }

            _outfitTempDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp", "ARF", blenderStamp));
            Directory.CreateDirectory(Path.Combine(_outfitTempDir, "outfits"));
            _outfitBlendPath = Path.Combine(_outfitTempDir, "body_rigged.blend");

            foreach (var kv in ticked)
            {
                string fbx = kv.Key;
                var job = new OutfitJob
                {
                    fbxPath   = fbx,
                    meshes    = kv.Value.ToArray(),
                    exportAbs = Path.Combine(_outfitTempDir, "outfits", _outfitJobs.Count + "_" + Path.GetFileName(fbx)),
                    before    = ArmatureSync.Capture(fbx),
                };
                if (AssetImporter.GetAtPath(fbx) is ModelImporter importer)
                {
                    job.human    = importer.humanDescription;
                    job.hasHuman = importer.animationType == ModelImporterAnimationType.Human;
                    foreach (var h in job.human.human ?? new HumanBone[0])
                    {
                        if (h.humanName == "LeftFoot")  job.footL = h.boneName;
                        if (h.humanName == "RightFoot") job.footR = h.boneName;
                        if (h.humanName == "LeftToes")  job.toeL  = h.boneName;
                        if (h.humanName == "RightToes") job.toeR  = h.boneName;
                    }
                }
                if (_backupEnabled)
                {
                    string dir = Path.Combine(Path.GetDirectoryName(ToAbsPath(fbx)), "_Backups");
                    Directory.CreateDirectory(dir);
                    job.backupJson = Path.Combine(dir, Path.GetFileNameWithoutExtension(fbx) + "_rigbackup_"
                                                       + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
                }
                // Replace writes over the outfit FBX, so it is backed up first, like the body.
                if (_exportMode == ExportMode.Replace)
                {
                    string dest = ShuganBackups.BackupAsset(fbx);
                    if (dest != null) UnityEngine.Debug.Log("[AutoRig Feet] Outfit backup: " + dest);
                    RecordOutfitOriginal(fbx, dest);
                }
                _outfitJobs.Add(job);
            }
            _runLogger?.Line("outfits from other FBXs: " + string.Join("; ", _outfitJobs.Select(j =>
                Path.GetFileName(j.fbxPath) + " (" + string.Join(", ", j.meshes) + ")")));
            return _outfitBlendPath;
        }

        static string OutfitBackupPrefix(string fbxPath, Func<string, string> toAbs) =>
            Path.Combine(Path.GetDirectoryName(toAbs(fbxPath)), "_Backups",
                         Path.GetFileNameWithoutExtension(fbxPath) + "_rigbackup_");

        // ─── Running them ──────────────────────────────────────────────────────

        /// <summary>The body run succeeded: start the first outfit, or go straight on to the swap.</summary>
        State AfterBodyRun()
        {
            // Replace: the finished export goes over the source FBX only now.
            if (!CommitBodyExport()) return State.Error;
            if (_outfitJobs.Count == 0) return State.FBXSwapping;
            if (!File.Exists(_outfitBlendPath))
            {
                _runReport.AddIssue("U_OUTFIT_FAILED", "warning",
                    "The outfits from other FBXs were not rigged: the rigged body was not kept for them.",
                    "Open the run log for details.");
                _outfitJobs.Clear();
                return State.FBXSwapping;
            }
            _outfitJobIndex = -1;
            return LaunchNextOutfit() ? State.OutfitsRunning : State.FBXSwapping;
        }

        bool LaunchNextOutfit()
        {
            string blenderPath = ArfPrefs.GetString(BlenderBridge.PrefBlenderPath, "");
            string scriptPath  = ResolveAutoRigScriptPath();
            while (++_outfitJobIndex < _outfitJobs.Count)
            {
                var job = _outfitJobs[_outfitJobIndex];
                job.report = new ShuganTools.RunReport();
                string code = _outfitRestoreMode
                    ? BlenderBridge.BuildOutfitRestoreScript(ToAbsPath(job.fbxPath), job.exportAbs, ToAbsPath(scriptPath),
                                                             job.meshes, OutfitBackupPrefix(job.fbxPath, ToAbsPath))
                    : BlenderBridge.BuildOutfitRigScript(_outfitBlendPath, ToAbsPath(job.fbxPath), job.exportAbs,
                                                         ToAbsPath(scriptPath), job.meshes, job.footL, job.footR, job.toeL, job.toeR,
                                                         job.backupJson, OutfitBackupPrefix(job.fbxPath, ToAbsPath), _rerigProtect);
                try { _runLogger?.WriteText("blender_wrapper_outfit_" + _outfitJobIndex + ".txt", code); } catch { }
                _runLogger?.Section("OUTFIT " + (_outfitJobIndex + 1) + "/" + _outfitJobs.Count + ": " + job.fbxPath);
                lock (_outputLock) _outputQueue.Clear();
                try { if (File.Exists(job.exportAbs)) File.Delete(job.exportAbs); } catch { }

                _blenderProcess = BlenderBridge.LaunchBlenderProcess(blenderPath, code, headless: true,
                                                                     factoryStartup: true, onOutputLine: EnqueueLine);
                if (_blenderProcess == null)
                {
                    OutfitFailed(job, "Blender could not be started.");
                    continue;
                }
                _processStartTime = EditorApplication.timeSinceStartup;
                _currentStepLabel = (_outfitRestoreMode ? "Restoring outfit " : "Rigging outfit ")
                                    + (_outfitJobIndex + 1) + "/" + _outfitJobs.Count + ": " + Path.GetFileName(job.fbxPath);
                return true;
            }
            return false;
        }

        /// <summary>Called every Update while an outfit runs. Returns true when every outfit is done.</summary>
        bool TickOutfits()
        {
            if (_outfitJobIndex < 0 || _outfitJobIndex >= _outfitJobs.Count) return true;
            var job = _outfitJobs[_outfitJobIndex];

            lock (_outputLock)
                while (_outputQueue.Count > 0) job.report.TryParseLine(_outputQueue.Dequeue());

            if (_blenderProcess != null && _timeoutMin > 0 &&
                EditorApplication.timeSinceStartup - _processStartTime > _timeoutMin * 60f)
            {
                try { if (!_blenderProcess.HasExited) _blenderProcess.Kill(); } catch { }
                try { _blenderProcess.Dispose(); } catch { }
                _blenderProcess = null;
                OutfitFailed(job, $"Blender didn't finish within {_timeoutMin} minutes.");
                return !LaunchNextOutfit();
            }

            if (_blenderProcess == null || !_blenderProcess.HasExited) return false;

            int exit = SafeExitCode(_blenderProcess);
            _blenderProcess.Dispose();
            _blenderProcess = null;
            lock (_outputLock)
                while (_outputQueue.Count > 0) job.report.TryParseLine(_outputQueue.Dequeue());

            bool fresh = File.Exists(job.exportAbs);
            string label = Path.GetFileName(job.fbxPath);
            foreach (var issue in job.report.issues)
            {
                if (issue.code == "INFO_BLENDER_VERSION" || issue.code == "BONE_CANDIDATES") continue;
                // One outfit never fails the run: its problems are warnings on the run, named after the outfit.
                string sev = issue.IsFatal ? "warning" : issue.severity;
                string msg = issue.message.StartsWith(label) ? issue.message : label + ": " + issue.message;
                _runReport.AddIssue(issue.code, sev, msg, issue.hint);
            }

            if (exit != 0 || job.report.HasFatal || !fresh)
                OutfitFailed(job, exit != 0 ? "Blender reported a failure (exit code " + exit + ")." : "No FBX was exported.");
            else
            {
                try { CommitOutfit(job); }
                catch (Exception e) { OutfitFailed(job, e.Message); }
            }
            SaveRunReport();
            return !LaunchNextOutfit();
        }

        /// <summary>The user stopped the outfits: the one running is killed, the rest are not started, the body goes on.</summary>
        void SkipRemainingOutfits()
        {
            try { if (_blenderProcess != null && !_blenderProcess.HasExited) _blenderProcess.Kill(); } catch { }
            try { _blenderProcess?.Dispose(); } catch { }
            _blenderProcess = null;
            var skipped = _outfitJobs.Skip(Math.Max(0, _outfitJobIndex)).Where(j => !j.ok).Select(j => Path.GetFileName(j.fbxPath)).ToList();
            _outfitJobIndex = _outfitJobs.Count;
            if (skipped.Count > 0)
                _runReport.AddIssue("U_OUTFIT_SKIPPED_BY_USER", "info",
                    "Stopped before these outfits were done; their FBXs are unchanged: " + string.Join(", ", skipped) + ".");
            if (_state == State.OutfitsRunning) _state = State.FBXSwapping;
            else if (_state == State.OutfitsRestoring)
            {
                FinishOutfitRestores();
                CleanOutfitTemp(keep: false);
                FinishRunReport();
                _state = State.Done;
                _currentStepLabel = "Restore done!";
            }
            Repaint();
        }

        void OutfitFailed(OutfitJob job, string why)
        {
            job.ok = false;
            _runReport.AddIssue("U_OUTFIT_FAILED", "warning",
                Path.GetFileName(job.fbxPath) + " was " + (_outfitRestoreMode ? "not restored" : "not rigged")
                + " and is unchanged: " + why,
                "The body was not affected. Open the run log (section OUTFIT) for details.");
        }

        void CommitOutfit(OutfitJob job)
        {
            if (!_outfitRestoreMode && _exportMode == ExportMode.Duplicate)
            {
                job.ok = true;      // copied into place by the swap step, for the duplicated avatar only
                return;
            }
            if (_outfitRestoreMode)
            {
                string dest = ShuganBackups.BackupAsset(job.fbxPath);
                if (dest != null) UnityEngine.Debug.Log("[AutoRig Feet] Outfit backup before restore: " + dest);
            }
            ReplaceFileSafely(job.exportAbs, ToAbsPath(job.fbxPath));
            AssetDatabase.ImportAsset(job.fbxPath, ImportAssetOptions.ForceUpdate);
            if (!_outfitRestoreMode && _recRun != null) { _recRun.outfitsReplaced.Add(job.fbxPath); _rec?.Save(); }
            RepairOutfitHumanoid(job, job.fbxPath);
            job.ok = true;
        }

        void RepairOutfitHumanoid(OutfitJob job, string fbxPath)
        {
            if (!job.hasHuman) return;
            try
            {
                string note = HumanoidRigMapping.RepairAfterSkeletonChange(fbxPath, true, job.human, skeletonOnly: true);
                if (!string.IsNullOrEmpty(note))
                    _runReport.AddIssue("U_HUMANOID_REPAIRED", "info", Path.GetFileName(fbxPath) + ": " + note);
            }
            catch (Exception e)
            {
                _runReport.AddIssue("U_HUMANOID_MAP", "warning",
                    Path.GetFileName(fbxPath) + ": the humanoid description could not be checked: " + e.Message);
            }
        }

        // ─── After the swap: the scene ────────────────────────────────────────

        /// <summary>Put the rigged outfits into the result avatar. Replace: the outfit FBXs were rewritten, so each
        /// outfit is brought in step with its file. Duplicate: the copied avatar gets its own copy of each outfit.</summary>
        void ApplyExternalOutfits()
        {
            if (_outfitRestoreMode || _resultInstance == null) return;
            var done = _outfitJobs.Where(j => j.ok).ToList();
            if (done.Count == 0) return;

            string bodyFbx = null;
            if (EffectiveSwapMethod() == SwapMethod.Standard)
                HumanoidRigMapping.TryResolveRigFbx(_resultInstance, out bodyFbx);
            else
                bodyFbx = ToProjectRelative(_exportPath);
            var bodySmr = string.IsNullOrEmpty(bodyFbx) ? null : ArmatureSync.RenderersFrom(_resultInstance, bodyFbx).FirstOrDefault();
            var bodyArmature = ArmatureSync.ArmatureOf(bodySmr);

            foreach (var job in done)
            {
                try
                {
                    string target = job.fbxPath;
                    if (_exportMode == ExportMode.Duplicate)
                    {
                        target = OutfitDuplicate.CopyForDuplicate(job.fbxPath, job.exportAbs, _resultInstance.name);
                        job.copyPath = target;
                        RepairOutfitHumanoid(job, target);
                        var map = OutfitDuplicate.Repoint(_resultInstance, job.fbxPath, target, _exportSuffix);
                        foreach (var kv in map) _prefabRepointed[kv.Key] = kv.Value;
                        RecordRepoint(map);
                    }
                    var result = ArmatureSync.SyncOutfit(_resultInstance, target, job.before, bodyArmature, UnlinkedPrefabMode);
                    ReportOutfitResult(job, target, result);
                }
                catch (Exception e)
                {
                    _runReport.AddIssue("U_OUTFIT_FAILED", "warning",
                        Path.GetFileName(job.fbxPath) + " could not be put into the avatar: " + e.Message,
                        "The body is fine. Open the run log for details.");
                }
            }
        }

        void ReportOutfitResult(OutfitJob job, string fbxPath, ArmatureSync.Result r)
        {
            string label = Path.GetFileName(fbxPath);
            if (r.error != null)
                _runReport.AddIssue("U_OUTFIT_RESYNC_FAILED", "warning", label + ": " + r.error);
            else if (r.Changed || r.prefabAssetUpdated != null)
                _runReport.AddIssue("U_OUTFIT_RESYNCED", "info",
                    label + ": the outfit in the scene was brought in step with its FBX (" + r.Summary() + ")"
                    + (r.prefabAssetUpdated != null ? "; the prefab " + r.prefabAssetUpdated + " was updated too." : "."));
            if (r.prefabAssetStale != null)
                _runReport.AddIssue("U_ARMATURE_PREFAB_STALE", "warning",
                    label + ": the prefab " + r.prefabAssetStale + " still has the old skeleton.",
                    "Switch \"Unlinked prefab\" in Advanced Settings back to updating the prefab itself.");
            if (r.unbound.Count > 0)
                _runReport.AddIssue("U_ARMATURE_BIND_MISMATCH", "warning",
                    label + ": these meshes don't match their skeleton and won't render: " + string.Join("; ", r.unbound.Take(6)),
                    "Restore the outfit FBX from the Backups tab.");
        }

        // ─── Prefab links ──────────────────────────────────────────────────────

        static List<PrefabLink> CapturePrefabLinks(GameObject root)
        {
            var list = new List<PrefabLink>();
            if (root == null) return list;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) continue;
                list.Add(new PrefabLink
                {
                    path  = t == root.transform ? "." : AvatarRelativePath(t, root.transform),
                    asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject),
                });
            }
            return list;
        }

        static string AvatarRelativePath(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>Every prefab that was linked under the avatar before the run is still linked to the same asset
        /// (or to the copy Duplicate made of it on purpose).</summary>
        void CheckPrefabLinks()
        {
            if (_prefabLinksBefore == null || _resultInstance == null) return;
            bool copy = _resultInstance != _avatarObject;
            var lost = new List<string>();
            foreach (var link in _prefabLinksBefore)
            {
                if (link.path == "." && copy) continue;     // a duplicate is its own object, not the prefab's
                var t = link.path == "." ? _resultInstance.transform : _resultInstance.transform.Find(link.path);
                string expected = _prefabRepointed.TryGetValue(link.asset, out var repointed) ? repointed : link.asset;
                string now = t != null && PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)
                    ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject) : null;
                if (t == null) continue;                    // removed on purpose (a cleaned toe bone, a pruned object)
                if (!string.Equals(now, expected, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(now, link.asset, StringComparison.OrdinalIgnoreCase))
                    lost.Add(link.path + " (" + Path.GetFileName(link.asset) + ")");
            }
            if (lost.Count > 0)
                _runReport.AddIssue("U_PREFAB_LINK_LOST", "warning",
                    lost.Count + " prefab(s) under the avatar are no longer linked to their prefab: "
                    + string.Join("; ", lost.Take(6)) + (lost.Count > 6 ? "…" : "") + ".",
                    "Nothing is broken in the scene, but changes to those prefabs will no longer reach this avatar.");
        }

        // ─── Restore Original Rig ──────────────────────────────────────────────

        /// <summary>Outfit FBXs under the avatar that carry an AutoRig Feet backup.</summary>
        List<(string fbx, string[] meshes)> RiggedExternalOutfits()
        {
            var list = new List<(string, string[])>();
            if (_avatarObject == null || _sourceFbxAsset == null) return list;
            string bodyFbx = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            foreach (var g in _avatarObject.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                         .Where(s => s.sharedMesh != null)
                         .GroupBy(s => AssetDatabase.GetAssetPath(s.sharedMesh), StringComparer.OrdinalIgnoreCase))
            {
                if (!g.Key.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(g.Key, bodyFbx, StringComparison.OrdinalIgnoreCase)) continue;
                bool has;
                try { has = FileContains(ToAbsPath(g.Key), WeightBackupMarker); } catch { has = false; }
                if (has) list.Add((g.Key, g.Select(s => s.sharedMesh.name).Distinct().ToArray()));
            }
            return list;
        }

        /// <summary>After the body restore: queue a restore for every rigged outfit FBX. True when one started.</summary>
        bool BeginOutfitRestores()
        {
            _outfitJobs.Clear();
            _outfitJobIndex = -1;
            _outfitRestoreMode = true;
            if (!ScriptSupportsExternalOutfits()) return false;
            var rigged = RiggedExternalOutfits();
            if (rigged.Count == 0) return false;
            _outfitTempDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp", "ARF",
                                                           DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_restore"));
            Directory.CreateDirectory(_outfitTempDir);
            foreach (var (fbx, meshes) in rigged)
            {
                var importer = AssetImporter.GetAtPath(fbx) as ModelImporter;
                _outfitJobs.Add(new OutfitJob
                {
                    fbxPath   = fbx,
                    meshes    = meshes,
                    exportAbs = Path.Combine(_outfitTempDir, _outfitJobs.Count + "_" + Path.GetFileName(fbx)),
                    before    = ArmatureSync.Capture(fbx),
                    hasHuman  = importer != null && importer.animationType == ModelImporterAnimationType.Human,
                    human     = importer != null ? importer.humanDescription : default(HumanDescription),
                });
            }
            return LaunchNextOutfit();
        }

        void FinishOutfitRestores()
        {
            foreach (var job in _outfitJobs.Where(j => j.ok))
            {
                try
                {
                    var result = ArmatureSync.SyncOutfit(_avatarObject, job.fbxPath, job.before, null, UnlinkedPrefabMode);
                    ReportOutfitResult(job, job.fbxPath, result);
                }
                catch (Exception e)
                {
                    _runReport.AddIssue("U_OUTFIT_RESYNC_FAILED", "warning", Path.GetFileName(job.fbxPath) + ": " + e.Message);
                }
            }
            if (_outfitJobs.Any(j => j.ok))
                _runReport.AddIssue("U_OUTFIT_RESTORED", "info",
                    "The feet rig was also removed from " + string.Join(", ", _outfitJobs.Where(j => j.ok).Select(j => Path.GetFileName(j.fbxPath))) + ".");
            _outfitRestoreMode = false;
        }

        // ─── Automation (TestLab) ─────────────────────────────────────────────

        internal void AutomationSetExternalOutfit(string fbxPath, string mesh, bool ticked)
        {
            LoadExternalChoices(out var set);
            string key = ExternalKey(fbxPath, mesh);
            if (ticked) set.Add(key); else set.Remove(key);
            SaveExternalChoices(set);
        }

        /// <summary>The meshes from other FBXs the table lists, or only the ticked ones.</summary>
        internal List<string> AutomationExternalOutfitMeshes(bool tickedOnly)
        {
            LoadExternalChoices(out var ticked);
            return ExternalOutfitRows().Where(r => !tickedOnly || ticked.Contains(r.key))
                                       .Select(r => r.mesh).ToList();
        }

        internal List<string> AutomationTickAllExternalOutfits(string fbxPath)
        {
            LoadExternalChoices(out var set);
            var names = new List<string>();
            foreach (var r in ExternalOutfitRows())
            {
                if (!string.IsNullOrEmpty(fbxPath) &&
                    !string.Equals(r.fbxPath, fbxPath, StringComparison.OrdinalIgnoreCase)) continue;
                set.Add(r.key);
                names.Add(r.mesh);
            }
            SaveExternalChoices(set);
            return names;
        }

        internal List<string> AutomationExternalOutfits()
        {
            var ticked = TickedExternalOutfits();
            return ExternalOutfitRows().Select(r =>
                r.mesh + " (" + Path.GetFileName(r.fbxPath) + ", " + OutfitFormLabel(r.smr, r.fbxPath) + ", "
                + (r.detected ? r.why + ", " + r.hits + " vertices" : "added") + ")"
                + (ticked.TryGetValue(r.fbxPath, out var l) && l.Contains(r.mesh) ? " ticked" : " not ticked")).ToList();
        }

        void CleanOutfitTemp(bool keep)
        {
            if (keep || string.IsNullOrEmpty(_outfitTempDir)) return;
            try { if (Directory.Exists(_outfitTempDir)) Directory.Delete(_outfitTempDir, true); } catch { }
        }
    }
}
