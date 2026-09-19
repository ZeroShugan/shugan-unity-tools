using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    // ─── Restore to original, interrupted runs, safe file replacement ───────────
    // Beginners need one button that undoes AutoRig Feet completely: the FBX back to the file it was before the
    // first run (byte for byte, from the backup taken then), and every object a run added under the avatar gone.
    // The most common reason is an FBX patch (face tracking) that only accepts the creator's original FBX.
    //
    // Everything here works from the ArfRecord kept next to the FBX's backups. Avatars rigged before that record
    // existed still work: the original is then the oldest backup of the FBX that carries no AutoRig Feet bones, and
    // the added objects are recognised by name, and the panel says so.
    public partial class AutoRigFeetDistributor
    {
        internal const string AskRestoreOriginal = "restore-original";
        internal const string AskRecoverFinish   = "recover-finish";

        ArfRecord          _rec;
        ArfRecord.RunEntry _recRun;
        State              _recLastState = State.Idle;
        string             _lastFbxBackup;       // the backup BackupOriginalFbx made this run
        string             _blenderExportPath;   // where Blender writes (a temp file in Replace)
        string             _restoreTempExport;
        bool               _reloadLocked;

        bool Busy => _state != State.Idle && _state != State.Done && _state != State.Error;

        // ─── The record, while a run goes ─────────────────────────────────────

        /// <summary>Called by Execute once the backups exist and just before Blender starts.</summary>
        void RecordBeginRun(string kind)
        {
            try
            {
                string src = AssetDatabase.GetAssetPath(_sourceFbxAsset);
                _rec = ArfRecord.LoadOrCreate(src);

                // The original: the backup taken before the FIRST run that changed this file. A file already rigged
                // before any record existed gets the oldest clean backup instead.
                if (string.IsNullOrEmpty(_rec.originalBackup) && kind == "rig" && _exportMode == ExportMode.Replace)
                {
                    bool rigged = _sourceFbxAsset != null && HasFeetRigBones(_sourceFbxAsset);
                    string original = !rigged && _lastFbxBackup != null ? ArfRecord.ToProjectRelative(_lastFbxBackup)
                                                                          : FindCleanBackup(src);
                    SetOriginal(_rec, original);
                }

                _recRun = new ArfRecord.RunEntry
                {
                    stamp      = DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                    kind       = kind,
                    mode       = _exportMode.ToString(),
                    swap       = EffectiveSwapMethod().ToString(),
                    stage      = ArfRecord.Stage.Started,
                    scene      = _avatarObject != null ? _avatarObject.scene.path : "",
                    avatarPath = _avatarObject != null ? _avatarObject.name : "",
                    avatarId   = ArfRecord.IdOf(_avatarObject),
                    exportFbx  = kind == "rig" ? ArfRecord.ToProjectRelative(_exportPath ?? "") : src,
                    tempExport = kind == "rig" ? _blenderExportPath : _restoreTempExport,
                };
                _rec.runs.Add(_recRun);
                _recLastState = _state;
                _rec.Save();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[AutoRig Feet] Could not start the AutoRig Feet record: " + e.Message);
                _rec = null;
                _recRun = null;
            }
        }

        static void SetOriginal(ArfRecord rec, string backupAssetPath)
        {
            if (string.IsNullOrEmpty(backupAssetPath)) return;
            string abs = ShuganSanitize.ToAbsolute(backupAssetPath);
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) return;
            rec.originalBackup = backupAssetPath;
            rec.originalSha1   = ArfRecord.Sha1(abs);
            try { rec.originalWhen = File.GetLastWriteTime(abs).ToString("yyyy-MM-dd HH:mm"); } catch { }
        }

        /// <summary>Outfit FBXs this run overwrites: their first backup is their original.</summary>
        void RecordOutfitOriginal(string outfitFbx, string backupAbs)
        {
            if (_rec == null || string.IsNullOrEmpty(backupAbs)) return;
            if (_rec.outfits.Any(o => string.Equals(o.fbx, outfitFbx, StringComparison.OrdinalIgnoreCase) &&
                                      File.Exists(ShuganSanitize.ToAbsolute(o.backup) ?? ""))) return;
            bool rigged;
            try { rigged = FileContains(ToAbsPath(outfitFbx), WeightBackupMarker); } catch { rigged = false; }
            string backup = rigged ? FindCleanBackup(outfitFbx) : ArfRecord.ToProjectRelative(backupAbs);
            if (string.IsNullOrEmpty(backup)) return;
            _rec.outfits.RemoveAll(o => string.Equals(o.fbx, outfitFbx, StringComparison.OrdinalIgnoreCase));
            _rec.outfits.Add(new ArfRecord.OutfitOriginal
            {
                fbx = outfitFbx, backup = backup, sha1 = ArfRecord.Sha1(ShuganSanitize.ToAbsolute(backup)),
            });
            _rec.Save();
        }

        void RecordAdded(GameObject go)
        {
            if (_recRun == null || go == null) return;
            var root = _resultInstance != null ? _resultInstance.transform : null;
            _recRun.added.Add(ArfRecord.IdOf(go) + "|" + (root != null ? ArfRecord.RelativePath(go.transform, root) : go.name));
            _rec?.Save();
        }

        /// <summary>Called every Update: the record follows the pipeline's state, so a run cut off at any point says where.</summary>
        void SyncRecordStage()
        {
            if (_recRun == null || _state == _recLastState) return;
            _recLastState = _state;
            string stage = null;
            switch (_state)
            {
                case State.BlenderRunning:
                case State.Restoring:        stage = ArfRecord.Stage.Blender; break;
                case State.OutfitsRunning:
                case State.OutfitsRestoring: stage = ArfRecord.Stage.Outfits; break;
                case State.FBXSwapping:      stage = ArfRecord.Stage.Swap; break;
                case State.AddingPrefabs:    stage = ArfRecord.Stage.Scene; break;
                case State.Done:             stage = ArfRecord.Stage.Done; break;
                case State.Error:            stage = ArfRecord.Stage.Failed; break;
                case State.Idle:             stage = ArfRecord.Stage.Cancelled; break;
            }
            if (stage == null) return;
            _recRun.stage = stage;
            if (_resultInstance != null)
            {
                _recRun.resultId   = ArfRecord.IdOf(_resultInstance);
                _recRun.resultPath = _resultInstance.name;
                if (_exportMode == ExportMode.Duplicate && string.IsNullOrEmpty(_recRun.privateFbx) &&
                    HumanoidRigMapping.TryResolveRigFbx(_resultInstance, out string rigFbx))
                    _recRun.privateFbx = rigFbx;
            }
            if (_blenderProcess != null) { try { _recRun.blenderPid = _blenderProcess.Id; } catch { } }
            _rec?.Save();
            if (ArfRecord.Stage.IsFinished(stage)) { _recRun = null; }
        }

        void RecordRepoint(Dictionary<string, string> map)
        {
            if (_recRun == null || map == null) return;
            foreach (var kv in map)
            {
                _recRun.repointFrom.Add(kv.Key);
                _recRun.repointTo.Add(kv.Value);
            }
            _rec?.Save();
        }

        // ─── Files: never a half-written FBX ──────────────────────────────────

        string NewTempExport(string label)
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp", "ARF",
                                                       DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + label));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, label + ".fbx");
        }

        /// <summary>Put a finished file over the target in one step. The copy lands next to the target first and is
        /// checked, then File.Replace swaps it in, so an interruption leaves either the old file or the new one.</summary>
        static void ReplaceFileSafely(string sourceAbs, string targetAbs)
        {
            var info = new FileInfo(sourceAbs);
            if (!info.Exists || info.Length == 0) throw new IOException("the exported file is missing or empty: " + sourceAbs);
            string staged = targetAbs + ".arfnew";
            File.Copy(sourceAbs, staged, overwrite: true);
            if (new FileInfo(staged).Length != info.Length)
            {
                File.Delete(staged);
                throw new IOException("the copy of the exported file is incomplete");
            }
            if (File.Exists(targetAbs))
            {
                var attr = File.GetAttributes(targetAbs);
                if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(targetAbs, attr & ~FileAttributes.ReadOnly);
                File.Replace(staged, targetAbs, null);
            }
            else File.Move(staged, targetAbs);
        }

        static void DeleteTempDir(string tempFile)
        {
            try
            {
                string dir = Path.GetDirectoryName(tempFile);
                if (dir != null && dir.Replace('\\', '/').Contains("/Temp/ARF/") && Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { }
        }

        /// <summary>Why this FBX can't be written right now, or null.</summary>
        static string FbxNotWritable(string abs)
        {
            try
            {
                if (!File.Exists(abs)) return null;
                if ((File.GetAttributes(abs) & FileAttributes.ReadOnly) != 0) return "the file is read-only";
                using (File.Open(abs, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return null;
            }
            catch (Exception e) { return "it is locked by another program (" + e.Message + ")"; }
        }

        /// <summary>Replace mode, after Blender succeeded: the temp export goes over the source FBX. False = the run failed.</summary>
        bool CommitBodyExport()
        {
            if (_exportMode != ExportMode.Replace || string.IsNullOrEmpty(_blenderExportPath) ||
                string.Equals(_blenderExportPath, _exportPath, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                ReplaceFileSafely(_blenderExportPath, _exportPath);
                if (_recRun != null) { _recRun.fbxReplaced = true; _rec?.Save(); }
                DeleteTempDir(_blenderExportPath);
                AssetDatabase.ImportAsset(ToProjectRelative(_exportPath), ImportAssetOptions.ForceUpdate);
                return true;
            }
            catch (Exception e)
            {
                _runReport.AddIssue("U_FBX_WRITE_FAILED", "fatal",
                    "The rigged FBX could not be put in place: " + e.Message,
                    "Your FBX was not changed. Close any program that has it open and run again.");
                FinishRunReport();
                SetError("The rigged FBX could not be written: " + e.Message);
                return false;
            }
        }

        /// <summary>The clean path, after Blender succeeded: back up the FBX, then put the cleaned file over it.</summary>
        bool CommitRestoreExport()
        {
            if (string.IsNullOrEmpty(_restoreTempExport)) return true;
            string src = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            try
            {
                string safety = ShuganBackups.BackupAsset(src);
                ReplaceFileSafely(_restoreTempExport, ToAbsPath(src));
                if (_recRun != null) { _recRun.fbxReplaced = true; _rec?.Save(); }
                DeleteTempDir(_restoreTempExport);
                AssetDatabase.ImportAsset(src, ImportAssetOptions.ForceUpdate);
                if (safety != null)
                    _runReport.AddIssue("U_FILE_BACKED_UP", "info", "The FBX was backed up first as " + Path.GetFileName(safety) + ".");
                return true;
            }
            catch (Exception e)
            {
                _runReport.AddIssue("U_FBX_WRITE_FAILED", "fatal", "The cleaned FBX could not be put in place: " + e.Message,
                    "Your FBX was not changed. Close any program that has it open and try again.");
                FinishRunReport();
                SetError("The cleaned FBX could not be written: " + e.Message);
                return false;
            }
            finally
            {
                _restoreTempExport = null;
            }
        }

        // ─── Script reloads and quitting mid-run ──────────────────────────────

        /// <summary>No domain reload while a run is going: a reload wipes the run's state halfway through the scene
        /// steps. Called every Update and when the window closes.</summary>
        void SyncReloadLock()
        {
            bool want = Busy;
            if (want && !_reloadLocked) { EditorApplication.LockReloadAssemblies(); _reloadLocked = true; }
            else if (!want && _reloadLocked) { EditorApplication.UnlockReloadAssemblies(); _reloadLocked = false; }
        }

        void RegisterInterruptionHandlers()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReloadDuringRun;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReloadDuringRun;
            EditorApplication.quitting -= OnQuittingDuringRun;
            EditorApplication.quitting += OnQuittingDuringRun;
        }

        void UnregisterInterruptionHandlers()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReloadDuringRun;
            EditorApplication.quitting -= OnQuittingDuringRun;
            if (_reloadLocked) { EditorApplication.UnlockReloadAssemblies(); _reloadLocked = false; }
        }

        void OnBeforeReloadDuringRun() => InterruptRun("scripts were reloaded");
        void OnQuittingDuringRun()     => InterruptRun("Unity was closed");

        void InterruptRun(string why)
        {
            if (!Busy) return;
            try { if (_blenderProcess != null && !_blenderProcess.HasExited) _blenderProcess.Kill(); } catch { }
            if (_recRun != null)
            {
                _recRun.note = "interrupted: " + why + " during " + _recRun.stage;
                _rec?.Save();       // the stage stays unfinished: the panel offers the way forward next time
            }
            try { _runLogger?.Line("[log] run interrupted: " + why); } catch { }
        }

        // ─── Interrupted runs: the panel ──────────────────────────────────────

        (ArfRecord rec, ArfRecord.RunEntry run) InterruptedRun()
        {
            if (Busy || _sourceFbxAsset == null) return (null, null);
            string src = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            var rec = ArfRecord.Load(src);
            var last = rec?.LastRun;
            if (last == null || ArfRecord.Stage.IsFinished(last.stage)) return (null, null);
            return (rec, last);
        }

        static bool BlenderStillRunning(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                var p = System.Diagnostics.Process.GetProcessById(pid);
                return !p.HasExited && p.ProcessName.IndexOf("blender", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        void DrawInterruptedRunPanel()
        {
            var (rec, run) = InterruptedRun();
            if (run == null) return;
            bool blender = BlenderStillRunning(run.blenderPid);
            bool canFinish = run.kind == "rig" && run.mode == "Replace" && run.fbxReplaced && !blender;

            string facts = $"A previous AutoRig Feet {(run.kind == "rig" ? "run" : "restore")} on this FBX ({run.stamp}) "
                         + $"did not finish: it stopped during \"{run.stage}\""
                         + (string.IsNullOrEmpty(run.note) ? "" : " (" + run.note + ")") + ".\n"
                         + (run.fbxReplaced ? "• The FBX had already been replaced with the rigged one; the avatar in the "
                                              + "scene was not finished (bones, prefabs, toggles).\n"
                                            : "• The FBX was NOT changed.\n")
                         + (blender ? "• Blender is still running in the background.\n" : "");
            EditorGUILayout.HelpBox(facts, MessageType.Error);
            EditorGUILayout.BeginHorizontal();
            if (blender && GUILayout.Button("Stop Blender"))
            {
                try { System.Diagnostics.Process.GetProcessById(run.blenderPid).Kill(); } catch { }
            }
            EditorGUI.BeginDisabledGroup(!canFinish || _avatarObject == null);
            if (GUILayout.Button(new GUIContent("Finish it", "Do the scene steps the run did not get to: bones, outfits, "
                                                            + "prefabs, toggles, humanoid mapping.")))
                RecoverFinish(rec, run);
            EditorGUI.EndDisabledGroup();
            if (GUILayout.Button("Restore to original")) RestoreToOriginal();
            if (GUILayout.Button("Dismiss"))
            {
                run.stage = ArfRecord.Stage.Dismissed;
                rec.Save();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Finish a Replace run whose FBX was already written: the Unity-side steps, on the selected avatar.</summary>
        internal void RecoverFinish(ArfRecord rec, ArfRecord.RunEntry run)
        {
            if (_avatarObject == null || rec == null || run == null) return;
            if (!ArfAutomation.Confirm(AskRecoverFinish, "Finish the interrupted run?",
                    "The rigged FBX is already in place. This adds what the run did not get to on \"" + _avatarObject.name
                    + "\": the new bones, the outfits' bones, the prefabs, the toggles and the humanoid mapping.\n\n"
                    + "The scene is backed up first.", "Finish it", "Cancel")) return;

            BackupScene();
            BeginRunLog("recover");
            _runReport = new ShuganTools.RunReport();
            _runStartTicksUtc = DateTime.UtcNow.Ticks;
            _exportMode = ExportMode.Replace;
            _exportPath = ToAbsPath(run.exportFbx);
            _blenderExportPath = _exportPath;
            _resultInstance = _avatarObject;
            _armatureBefore = null;
            _cleanToeBoneNames = new List<string>();
            _prefabLinksBefore = CapturePrefabLinks(_avatarObject);
            _prefabRepointed.Clear();
            _outfitJobs.Clear();
            _outfitRestoreMode = false;
            foreach (string o in run.outfitsReplaced)
                _outfitJobs.Add(new OutfitJob { fbxPath = o, ok = true, meshes = new string[0] });
            _blendShapeSnapshot = null;
            _plannedToggleRows = null;
            _plannedShrinkKeys = null;
            if (run.toggleRows.Count > 0 || run.shrinkKeys.Count > 0)
            {
                _plannedToggleRows = run.toggleRows.Select(t =>
                {
                    int eq = t.LastIndexOf('=');
                    float.TryParse(t.Substring(eq + 1), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float v);
                    return new FootShapeFit.ToggleRow { key = t.Substring(0, eq), value = v };
                }).ToList();
                _plannedShrinkKeys = new List<string>(run.shrinkKeys);
            }
            else if (_rigOnBestShape && _meshNames.Length > 0)
            {
                var fit = FootShapeFitNow(_meshNames[Mathf.Clamp(_selectedMeshIndex, 0, _meshNames.Length - 1)]);
                if (fit != null && fit.error == null)
                {
                    _plannedToggleRows = IncludedFootShapeRows(fit);
                    _plannedShrinkKeys = IncludedShrinkKeys(fit);
                }
            }
            _rec = rec;
            _recRun = run;
            run.note = (run.note ?? "") + " / finished by hand";
            _recLastState = State.Idle;
            _runReport.AddIssue("U_RUN_RECOVERED", "info",
                "An interrupted run was finished: its FBX was already rigged, and the scene steps were done now.");
            _state = State.AddingPrefabs;
            Repaint();
        }

        // ─── Restore to original: what there is to restore ────────────────────

        internal class OriginalPlan
        {
            public string kind;          // "replace", "duplicate", "clean-only", "objects-only", "none"
            public string fbx;           // the FBX to put back (the original source for a duplicate)
            public string backup;        // project path of the original backup ("" for a duplicate)
            public string when;
            public bool   fromRecord;
            public string copyFbx;       // duplicate: the FBX the copy uses now
            public ArfRecord rec;
            public List<ArfRecord.RunEntry> runs = new List<ArfRecord.RunEntry>();
            public List<(string fbx, string backup)> outfits = new List<(string, string)>();
            public List<string> arfObjects = new List<string>();
            public string Explain()
            {
                switch (kind)
                {
                    case "replace":      return "Original FBX: " + Path.GetFileName(backup) + (string.IsNullOrEmpty(when) ? "" : " (" + when + ")")
                                                + (fromRecord ? "" : " — found by looking for the oldest backup without the toe rig");
                    case "duplicate":    return "This avatar is a copy AutoRig Feet made from " + Path.GetFileName(fbx)
                                                + ". It will be switched back to that original FBX and cleaned.";
                    case "clean-only":   return "No backup of the original FBX was found, so the rig will be removed from the "
                                                + "current FBX in Blender instead (the file will not be byte-identical).";
                    case "objects-only": return "The FBX was never changed; only the objects AutoRig Feet added are removed.";
                    default:             return "AutoRig Feet has not changed this avatar.";
                }
            }
        }

        OriginalPlan _originalPlan;
        string       _originalPlanKey;

        OriginalPlan OriginalPlanFor()
        {
            if (_avatarObject == null || _sourceFbxAsset == null) return null;
            string src = AssetDatabase.GetAssetPath(_sourceFbxAsset);
            string key = ArfRecord.IdOf(_avatarObject) + "|" + src + "|" + SafeWriteTicks(src) + "|" + _backupScanDirty;
            if (_originalPlan != null && key == _originalPlanKey) return _originalPlan;
            _originalPlanKey = key;
            _originalPlan = BuildOriginalPlan(src);
            return _originalPlan;
        }

        long SafeWriteTicks(string assetPath)
        {
            try { return File.GetLastWriteTimeUtc(ToAbsPath(assetPath)).Ticks; } catch { return 0; }
        }

        OriginalPlan BuildOriginalPlan(string src)
        {
            var plan = new OriginalPlan { fbx = src };
            string avatarId = ArfRecord.IdOf(_avatarObject);

            // A copy made by Duplicate: some record lists this avatar, or this FBX, as a Duplicate result.
            foreach (var rec in ArfRecord.All())
                foreach (var run in rec.runs)
                {
                    if (run.kind != "rig" || run.mode != "Duplicate") continue;
                    bool mine = (!string.IsNullOrEmpty(run.resultId) && run.resultId == avatarId) ||
                                string.Equals(run.privateFbx, src, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(run.exportFbx, src, StringComparison.OrdinalIgnoreCase);
                    if (!mine) continue;
                    plan.kind = "duplicate"; plan.fbx = rec.fbx; plan.copyFbx = src; plan.rec = rec; plan.fromRecord = true;
                    plan.runs.Add(run);
                }
            if (plan.kind == "duplicate")
            {
                plan.arfObjects = ArfObjectsUnder(_avatarObject, plan.runs).Select(t => t.name).ToList();
                return plan;
            }

            // The same without a record: an FBX in the Standard swap's folder, or a "_Rig_Feet" export next to its source.
            string guess = GuessDuplicateSource(src);
            if (guess == null && src.Replace("\\", "/").Contains("/FBXSwapper/"))
            {
                string name = Path.GetFileNameWithoutExtension(src);
                var owner = ArfRecord.All().Where(r => name.StartsWith(Path.GetFileNameWithoutExtension(r.fbx) + "_", StringComparison.Ordinal))
                                           .OrderByDescending(r => Path.GetFileNameWithoutExtension(r.fbx).Length).FirstOrDefault();
                if (owner != null)
                {
                    plan.kind = "duplicate"; plan.fbx = owner.fbx; plan.copyFbx = src; plan.rec = owner;
                    plan.runs.AddRange(owner.runs.Where(r => r.kind == "rig" && r.mode == "Duplicate"));
                    plan.arfObjects = ArfObjectsUnder(_avatarObject, plan.runs).Select(t => t.name).ToList();
                    return plan;
                }
            }
            if (guess != null)
            {
                plan.kind = "duplicate"; plan.fbx = guess; plan.copyFbx = src;
                plan.arfObjects = ArfObjectsUnder(_avatarObject, plan.runs).Select(t => t.name).ToList();
                return plan;
            }

            plan.rec = ArfRecord.Load(src);
            if (plan.rec != null)
                plan.runs.AddRange(plan.rec.runs.Where(r => r.kind == "rig" &&
                    (string.IsNullOrEmpty(r.resultId) || r.resultId == avatarId || r.mode == "Replace")));
            if (plan.rec != null && !string.IsNullOrEmpty(plan.rec.originalBackup) &&
                File.Exists(ShuganSanitize.ToAbsolute(plan.rec.originalBackup) ?? ""))
            {
                plan.backup = plan.rec.originalBackup; plan.when = plan.rec.originalWhen; plan.fromRecord = true;
            }
            bool rigged = HasFeetRigBones(_sourceFbxAsset);
            if (plan.backup == null && rigged)
            {
                plan.backup = FindCleanBackup(src);
                if (plan.backup != null)
                    try { plan.when = File.GetLastWriteTime(ShuganSanitize.ToAbsolute(plan.backup)).ToString("yyyy-MM-dd HH:mm"); } catch { }
            }

            // Outfit FBXs under the avatar that carry the rig.
            foreach (var (fbx, _) in RiggedExternalOutfits())
            {
                var rec = plan.rec?.outfits.FirstOrDefault(o => string.Equals(o.fbx, fbx, StringComparison.OrdinalIgnoreCase));
                string b = rec != null && File.Exists(ShuganSanitize.ToAbsolute(rec.backup) ?? "") ? rec.backup : FindCleanBackup(fbx);
                plan.outfits.Add((fbx, b));
            }

            plan.arfObjects = ArfObjectsUnder(_avatarObject, plan.runs).Select(t => t.name).ToList();
            if (rigged) plan.kind = plan.backup != null ? "replace" : "clean-only";
            else plan.kind = plan.arfObjects.Count > 0 || plan.outfits.Count > 0 ? "objects-only" : "none";
            return plan;
        }

        /// <summary>The oldest backup of this FBX that holds no AutoRig Feet rig: the file as it was before the first run.</summary>
        string FindCleanBackup(string fbxAssetPath)
        {
            try
            {
                string abs = ShuganSanitize.ToAbsolute(fbxAssetPath);
                string dir = Path.Combine(Path.GetDirectoryName(abs), ShuganBackups.FolderName);
                if (!Directory.Exists(dir)) return null;
                string stem = Path.GetFileNameWithoutExtension(abs) + "_backup_";
                foreach (string f in Directory.GetFiles(dir, stem + "*.fbx").OrderBy(f => f, StringComparer.Ordinal))
                {
                    if (FileContains(f, WeightBackupMarker)) continue;
                    string rel = ArfRecord.ToProjectRelative(f);
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(rel);
                    if (asset != null && HasFeetRigBones(asset)) continue;
                    if (asset == null && (FileContains(f, System.Text.Encoding.ASCII.GetBytes("Toes_a1")))) continue;
                    return rel;
                }
            }
            catch { }
            return null;
        }

        static string GuessDuplicateSource(string fbxAssetPath)
        {
            string name = Path.GetFileNameWithoutExtension(fbxAssetPath);
            string dir  = Path.GetDirectoryName(fbxAssetPath).Replace("\\", "/");
            // "<fbx>_Rig_Feet" / "<fbx>_Rig_Feet_001" next to the source
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(?<src>.+)_Rig_Feet(_\d{3})?$");
            if (m.Success)
            {
                string cand = dir + "/" + m.Groups["src"].Value + ".fbx";
                if (File.Exists(ShuganSanitize.ToAbsolute(cand) ?? "")) return cand;
            }
            return null;
        }

        /// <summary>Objects AutoRig Feet added directly under the avatar: the ones a run recorded, the toggles (always
        /// ours by name), and, when no run recorded anything, instances of the default feet prefabs.</summary>
        List<Transform> ArfObjectsUnder(GameObject avatar, List<ArfRecord.RunEntry> runs)
        {
            var list = new List<Transform>();
            if (avatar == null) return list;
            void Add(Transform t) { if (t != null && t != avatar.transform && t.IsChildOf(avatar.transform) && !list.Contains(t)) list.Add(t); }

            bool anyRecorded = false;
            foreach (var run in runs ?? new List<ArfRecord.RunEntry>())
                foreach (string entry in run.added)
                {
                    anyRecorded = true;
                    int bar = entry.IndexOf('|');
                    var go = ArfRecord.Resolve(bar >= 0 ? entry.Substring(0, bar) : entry);
                    if (go != null) { Add(go.transform); continue; }
                    if (bar >= 0) Add(avatar.transform.Find(entry.Substring(bar + 1)));
                }
            foreach (var t in FootShapeToggle.FindAll(avatar)) Add(t);
            if (!anyRecorded)
                foreach (Transform child in avatar.transform)
                {
                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject)) continue;
                    string asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(child.gameObject);
                    if (DefaultPrefabPaths.Contains(asset)) Add(child);
                }
            return list;
        }

        /// <summary>Scenes and prefabs in the project that use this asset (by GUID), besides the backups.</summary>
        internal static List<string> FindAssetUsers(string assetPath)
        {
            var users = new List<string>();
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) return users;
            string needle = "guid: " + guid;
            try
            {
                foreach (string pattern in new[] { "*.unity", "*.prefab" })
                    foreach (string f in Directory.GetFiles(Application.dataPath, pattern, SearchOption.AllDirectories))
                    {
                        if (f.Replace('\\', '/').Contains("/" + ShuganBackups.FolderName + "/")) continue;
                        bool hit = false;
                        foreach (string line in File.ReadLines(f))
                            if (line.Contains(needle)) { hit = true; break; }
                        if (hit) users.Add(ArfRecord.ToProjectRelative(f));
                    }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[AutoRig Feet] Searching for other users of " + assetPath + " failed: " + e.Message);
            }
            return users;
        }

        /// <summary>Other avatars in every open scene whose meshes come from this FBX.</summary>
        List<GameObject> OtherOpenSceneUsers(string fbxPath, GameObject except)
        {
            var list = new List<GameObject>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == except) continue;
                    if (root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(s => s.sharedMesh != null &&
                            string.Equals(AssetDatabase.GetAssetPath(s.sharedMesh), fbxPath, StringComparison.OrdinalIgnoreCase)))
                        list.Add(root);
                }
            }
            return list;
        }

        string UsersWarning(string fbxPath, GameObject except)
        {
            var files = FindAssetUsers(fbxPath);
            var others = OtherOpenSceneUsers(fbxPath, except);
            if (files.Count <= 1 && others.Count == 0) return "";
            return "\n\n⚠ " + Path.GetFileName(fbxPath) + " is also used by:\n"
                   + string.Join("\n", others.Take(6).Select(o => "   • " + o.name + " (in the open scene " + o.scene.name + ")"))
                   + (others.Count > 0 ? "\n" : "")
                   + string.Join("\n", files.Take(10).Select(f => "   • " + f)) + (files.Count > 10 ? "\n   • …" : "")
                   + "\nThey all see the restored file. Avatars in the open scenes are brought in step automatically; for the "
                   + "others, open them, select the avatar and press Re-sync armature.";
        }

        // ─── Restore to original: doing it ─────────────────────────────────────

        internal void RestoreToOriginal()
        {
            var plan = OriginalPlanFor();
            if (plan == null || plan.kind == "none")
            {
                SetStatus("AutoRig Feet has not changed this avatar — nothing to restore.", MessageType.Info);
                return;
            }
            if (Busy) return;

            string changes;
            switch (plan.kind)
            {
                case "replace":
                    changes = "• " + plan.fbx + " is put back to " + Path.GetFileName(plan.backup)
                              + " (the file from before AutoRig Feet)\n"
                              + string.Join("", plan.outfits.Where(o => o.backup != null)
                                  .Select(o => "• outfit " + o.fbx + " is put back to " + Path.GetFileName(o.backup) + "\n"));
                    break;
                case "duplicate":
                    changes = "• the copy's meshes and bones go back to " + plan.fbx + "\n";
                    break;
                case "clean-only":
                    changes = "• the toe rig is removed from " + plan.fbx + " in Blender (no backup of the original file exists)\n";
                    break;
                default:
                    changes = "";
                    break;
            }
            changes += plan.arfObjects.Count > 0
                ? "• removed from the avatar: " + string.Join(", ", plan.arfObjects.Take(8)) + (plan.arfObjects.Count > 8 ? "…" : "") + "\n"
                : "";
            changes += "• the toe bones are removed from the avatar, and the humanoid mapping goes back to the original\n";

            string msg = "Restore \"" + _avatarObject.name + "\" to how it was before AutoRig Feet?\n\n" + changes
                         + "\nThe scene and every file that changes are backed up first, so this can be undone from this tab."
                         + (plan.kind == "duplicate" ? "" : UsersWarning(plan.fbx, _avatarObject));
            if (!ArfAutomation.Confirm(AskRestoreOriginal, "Restore to original", msg, "Restore to original", "Cancel")) return;

            if (plan.kind == "clean-only")
            {
                RunRestore(null, removeObjectsAfter: true);
                return;
            }

            BackupScene();
            BeginRunLog("restore-original");
            _runReport = new ShuganTools.RunReport();
            _runStartTicksUtc = DateTime.UtcNow.Ticks;
            var entry = new ArfRecord.RunEntry
            {
                stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss"), kind = "restore-original", stage = ArfRecord.Stage.Started,
                scene = _avatarObject.scene.path, avatarPath = _avatarObject.name, avatarId = ArfRecord.IdOf(_avatarObject),
                exportFbx = plan.fbx,
            };
            var rec = plan.rec ?? ArfRecord.LoadOrCreate(plan.fbx);
            rec.runs.Add(entry);
            rec.Save();

            try
            {
                if (plan.kind == "replace") RestoreReplacedAvatar(plan);
                else if (plan.kind == "duplicate") RestoreDuplicateCopy(plan);
                else if (plan.kind == "objects-only") RestoreOutfitsOnly(plan);

                var removed = RemoveArfObjects(_avatarObject, plan.runs);
                if (removed > 0)
                    _runReport.AddIssue("U_ARF_OBJECTS_REMOVED", "info", removed + " object(s) AutoRig Feet had added were removed from the avatar.");

                var check = ArmatureSync.ValidateOnly(_avatarObject, plan.fbx);
                if (check.unbound.Count > 0) ReportArmatureResult(check);

                entry.stage = ArfRecord.Stage.Done;
                rec.Save();
                _runReport.AddIssue("U_RESTORED_ORIGINAL", "info",
                    "\"" + _avatarObject.name + "\" was restored to its state before AutoRig Feet (" + plan.Explain() + ").");
                FinishRunReport();
                _backupScanDirty = true;
                _originalPlan = null;
                AutoDetectFbx();
                RefreshRigState();
                SetStatus("Restored to original. The scene and the replaced files were backed up first (Backups tab).",
                          MessageType.Info);
            }
            catch (Exception e)
            {
                entry.stage = ArfRecord.Stage.Failed;
                entry.note = e.Message;
                rec.Save();
                _runReport.AddIssue("U_RESTORE_ORIGINAL_FAILED", "fatal", "Restore to original stopped: " + e.Message,
                    "Everything that was replaced had a backup made first; see the Backups tab.");
                FinishRunReport();
                SetStatus("Restore to original failed: " + e.Message, MessageType.Error);
                UnityEngine.Debug.LogError("[AutoRig Feet] Restore to original failed: " + e);
            }
            Repaint();
        }

        void RestoreReplacedAvatar(OriginalPlan plan)
        {
            var before = ArmatureSync.Capture(plan.fbx);
            PutBackFile(plan.fbx, plan.backup);
            var result = ArmatureSync.Sync(_avatarObject, plan.fbx, before, UnlinkedPrefabMode);
            ReportArmatureResult(result);

            var bodySmr = ArmatureSync.RenderersFrom(_avatarObject, plan.fbx).FirstOrDefault();
            var bodyArmature = ArmatureSync.ArmatureOf(bodySmr);
            RestoreOutfitFiles(plan, bodyArmature);

            foreach (var other in OtherOpenSceneUsers(plan.fbx, _avatarObject))
                ReportArmatureResult(ArmatureSync.Sync(other, plan.fbx, before, UnlinkedPrefabMode));
        }

        void RestoreOutfitsOnly(OriginalPlan plan)
        {
            var bodySmr = ArmatureSync.RenderersFrom(_avatarObject, plan.fbx).FirstOrDefault();
            RestoreOutfitFiles(plan, ArmatureSync.ArmatureOf(bodySmr));
        }

        void RestoreOutfitFiles(OriginalPlan plan, Transform bodyArmature)
        {
            foreach (var (fbx, backup) in plan.outfits)
            {
                if (backup == null)
                {
                    _runReport.AddIssue("U_OUTFIT_NO_ORIGINAL", "warning",
                        Path.GetFileName(fbx) + " has no backup from before AutoRig Feet, so it was left as it is.",
                        "Use \"Clean AutoRig Feet from the FBX\" to remove the rig from it.");
                    continue;
                }
                var before = ArmatureSync.Capture(fbx);
                PutBackFile(fbx, backup);
                ReportOutfitResult(null, fbx, ArmatureSync.SyncOutfit(_avatarObject, fbx, before, bodyArmature, UnlinkedPrefabMode));
            }
        }

        /// <summary>The backup over the asset (the current file is backed up first by ShuganBackups.Restore), with the
        /// backup's own import settings for the humanoid mapping, which the runs changed in the .meta.</summary>
        void PutBackFile(string assetPath, string backupAssetPath)
        {
            string backupAbs = ShuganSanitize.ToAbsolute(backupAssetPath);
            var e = new ShuganBackups.Entry { filePath = backupAbs, fileName = Path.GetFileName(backupAbs) };
            if (!ShuganBackups.Restore(e, assetPath, out string safety, out string error))
                throw new Exception("could not put back " + assetPath + ": " + error);
            _runReport.AddIssue("U_FILE_RESTORED", "info",
                assetPath + " was put back from " + Path.GetFileName(backupAbs)
                + (safety != null ? " (the replaced file was kept as " + Path.GetFileName(safety) + ")." : "."));

            var target = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            var source = AssetImporter.GetAtPath(backupAssetPath) as ModelImporter;
            if (target != null && source != null)
            {
                target.animationType = source.animationType;
                target.avatarSetup = source.avatarSetup;
                if (source.animationType == ModelImporterAnimationType.Human)
                    target.humanDescription = source.humanDescription;
                target.SaveAndReimport();
            }
        }

        void RestoreDuplicateCopy(OriginalPlan plan)
        {
            // A Legacy copy is an instance of the prefab the swap built: its bones and prefabs live in that asset, where
            // the scene can't delete them. That prefab is AutoRig Feet's own file, so it is cleaned at source.
            string created = null;
            if (PrefabUtility.IsAnyPrefabInstanceRoot(_avatarObject))
            {
                string asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(_avatarObject);
                if (plan.runs.Any(r => string.Equals(r.createdPrefab, asset, StringComparison.OrdinalIgnoreCase)) ||
                    (asset != null && asset.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                     Path.GetFileNameWithoutExtension(asset).Contains("-to-")))
                    created = asset;
            }
            if (created != null)
            {
                ShuganBackups.BackupAsset(created);
                var contents = PrefabUtility.LoadPrefabContents(created);
                try
                {
                    CleanCopyHierarchy(contents, plan, contents.transform);
                    PrefabUtility.SaveAsPrefabAsset(contents, created);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
                AssetDatabase.Refresh();
                _runReport.AddIssue("U_COPY_RELINKED", "info",
                    "The prefab AutoRig Feet built for this copy (" + created + ") was cleaned and points at " + plan.fbx + " again.");
            }
            CleanCopyHierarchy(_avatarObject, plan, null);
        }

        /// <summary>Everything that makes a hierarchy an AutoRig Feet copy, undone: meshes and Animator avatar back to the
        /// original FBX, bones in step with it, re-pointed outfits back. `prefabRoot` is set when cleaning inside a prefab
        /// asset, where the objects AutoRig Feet added are removed right away.</summary>
        void CleanCopyHierarchy(GameObject root, OriginalPlan plan, Transform prefabRoot)
        {
            var before = ArmatureSync.Capture(plan.copyFbx);
            var originalAssets = AssetDatabase.LoadAllAssetsAtPath(plan.fbx);
            var meshes = originalAssets.OfType<Mesh>().GroupBy(m => m.name).ToDictionary(g => g.Key, g => g.First());
            var avatarAsset = originalAssets.OfType<Avatar>().FirstOrDefault();

            int relinked = 0;
            foreach (var smr in ArmatureSync.RenderersFrom(root, plan.copyFbx))
            {
                if (!meshes.TryGetValue(smr.sharedMesh.name, out var mesh)) continue;
                Undo.RecordObject(smr, "AutoRig Feet — restore to original");
                smr.sharedMesh = mesh;
                if (PrefabUtility.IsPartOfPrefabInstance(smr)) PrefabUtility.RecordPrefabInstancePropertyModifications(smr);
                relinked++;
            }
            foreach (var anim in root.GetComponentsInChildren<Animator>(true))
            {
                if (anim.avatar == null || avatarAsset == null) continue;
                if (!string.Equals(AssetDatabase.GetAssetPath(anim.avatar), plan.copyFbx, StringComparison.OrdinalIgnoreCase)) continue;
                Undo.RecordObject(anim, "AutoRig Feet — restore to original");
                anim.avatar = avatarAsset;
                if (PrefabUtility.IsPartOfPrefabInstance(anim)) PrefabUtility.RecordPrefabInstancePropertyModifications(anim);
            }
            if (relinked > 0)
                _runReport.AddIssue("U_COPY_RELINKED", "info",
                    relinked + " mesh(es) of the copy point at " + plan.fbx + " again.");
            ReportArmatureResult(ArmatureSync.Sync(root, plan.fbx, before,
                prefabRoot != null ? ArmatureSync.UnlinkedPrefab.SceneOnly : UnlinkedPrefabMode));
            if (prefabRoot != null)
            {
                foreach (var t in ArfObjectsUnder(root, plan.runs)) if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
                return;
            }

            // Outfits the Duplicate run switched to copies go back to their own prefab or FBX.
            var bodyArmature = ArmatureSync.ArmatureOf(ArmatureSync.RenderersFrom(root, plan.fbx).FirstOrDefault());
            foreach (var run in plan.runs)
                for (int i = 0; i < run.repointTo.Count && i < run.repointFrom.Count; i++)
                {
                    string to = run.repointTo[i], from = run.repointFrom[i];
                    OutfitDuplicate.RepointBack(_avatarObject, to, from);
                    if (from.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                        ReportOutfitResult(null, from, ArmatureSync.SyncOutfit(_avatarObject, from, ArmatureSync.Capture(to),
                                                                                  bodyArmature, UnlinkedPrefabMode));
                }
        }

        int RemoveArfObjects(GameObject avatar, List<ArfRecord.RunEntry> runs)
        {
            int n = 0;
            foreach (var t in ArfObjectsUnder(avatar, runs))
            {
                if (t == null) continue;
                try
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(t.gameObject) && !PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject) &&
                        !PrefabUtility.IsAddedGameObjectOverride(t.gameObject))
                    {
                        _runReport?.AddIssue("U_ARF_OBJECT_KEPT", "warning",
                            "\"" + t.name + "\" is part of the avatar's prefab and could not be removed here.",
                            "Remove it from the prefab itself.");
                        continue;
                    }
                    Undo.DestroyObjectImmediate(t.gameObject);
                    n++;
                }
                catch (Exception e)
                {
                    _runReport?.AddIssue("U_ARF_OBJECT_KEPT", "warning", "\"" + t.name + "\" could not be removed: " + e.Message);
                }
            }
            if (n > 0 && avatar.scene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.scene);
            return n;
        }

        // ─── Backups tab: the panel ───────────────────────────────────────────

        void DrawOriginalPanel()
        {
            if (_avatarObject == null) return;
            DrawInterruptedRunPanel();
            var plan = OriginalPlanFor();
            if (plan == null) return;

            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.6f, 1f, 0.65f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prev;
            GUILayout.Label("Restore \"" + _avatarObject.name + "\" to its original state", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(plan.Explain(), EditorStyles.wordWrappedMiniLabel);
            if (plan.kind != "none")
            {
                EditorGUILayout.LabelField(
                    "Puts the FBX back to the file you had before AutoRig Feet, removes the toe bones, the prefabs and the "
                    + "toggles AutoRig Feet added, and sets the humanoid mapping back. Everything is backed up first.\n"
                    + "Use this before applying an FBX patch (face tracking and similar need the creator's original FBX): "
                    + "restore, patch, then run AutoRig Feet again.",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUI.BeginDisabledGroup(Busy);
                if (GUILayout.Button(new GUIContent("Restore to original (remove everything AutoRig Feet did)"), GUILayout.Height(28)))
                    RestoreToOriginal();
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.EndVertical();

            DrawRestoreSection();
        }

        HashSet<string> OriginalBackupFiles()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in ArfRecord.All())
            {
                if (!string.IsNullOrEmpty(rec.originalBackup)) set.Add(ShuganSanitize.ToAbsolute(rec.originalBackup) ?? "");
                foreach (var o in rec.outfits) if (!string.IsNullOrEmpty(o.backup)) set.Add(ShuganSanitize.ToAbsolute(o.backup) ?? "");
            }
            var plan = OriginalPlanFor();
            if (plan?.backup != null) set.Add(ShuganSanitize.ToAbsolute(plan.backup) ?? "");
            return set;
        }

        // ─── Automation (TestLab) ─────────────────────────────────────────────

        internal void AutomationRestoreOriginal() => RestoreToOriginal();
        internal string AutomationUsersWarning() =>
            _sourceFbxAsset == null ? "" : UsersWarning(AssetDatabase.GetAssetPath(_sourceFbxAsset), _avatarObject);
        internal string AutomationStatus => _statusMsg ?? "";
        internal void AutomationDismissInterrupted()
        {
            var (rec, run) = InterruptedRun();
            if (run == null) return;
            run.stage = ArfRecord.Stage.Dismissed;
            rec.Save();
        }
        internal void AutomationSetOutfit(string mesh, bool ticked)
        {
            LoadOutfitChoices(out var unticked, out var added);
            if (ticked) { unticked.Remove(mesh); if (!added.Contains(mesh)) added.Add(mesh); }
            else { unticked.Add(mesh); added.Remove(mesh); }
            SaveOutfitChoices(unticked, added);
        }
        internal bool AutomationGetOption(string option)
        {
            switch (option)
            {
                case "footHelper":     return _footHelper;
                case "rigOnBestShape": return _rigOnBestShape;
                case "autoMapFeet":    return _autoMapFeet;
                case "backupEnabled":  return _backupEnabled;
                case "autoSwapMethod": return _autoSwapMethod;
                case "makeShrinkKeys": return _makeShrinkKeys;
                default: throw new ArgumentException("unknown option: " + option);
            }
        }
        internal string AutomationOriginalPlan() { _originalPlan = null; return OriginalPlanFor()?.kind + ": " + OriginalPlanFor()?.Explain(); }
        internal string AutomationInterruptedRun()
        {
            var (_, run) = InterruptedRun();
            return run == null ? null : run.stage + (run.fbxReplaced ? " (fbx replaced)" : "") + " " + run.note;
        }
        internal void AutomationRecoverFinish()
        {
            var (rec, run) = InterruptedRun();
            if (run != null) RecoverFinish(rec, run);
        }
        internal void AutomationKillBlender()
        {
            try { if (_blenderProcess != null && !_blenderProcess.HasExited) _blenderProcess.Kill(); } catch { }
        }
        internal string AutomationLastRecordStage() =>
            _sourceFbxAsset == null ? null : ArfRecord.Load(AssetDatabase.GetAssetPath(_sourceFbxAsset))?.LastRun?.stage;
    }
}
