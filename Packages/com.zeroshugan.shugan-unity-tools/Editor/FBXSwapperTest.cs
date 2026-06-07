// ╔══════════════════════════════════════════════════════════════════════╗
// ║          SHUGAN'S FBX SWAPPER (TEST) — experimental                    ║
// ║          Duplicate-and-relink approach: duplicate the scene avatar,    ║
// ║          give it a PRIVATE copy of the FBX, swap the new FBX content    ║
// ║          into that copy, and graft the new armature bones.             ║
// ║          The original avatar is never touched. — by Shugan             ║
// ╚══════════════════════════════════════════════════════════════════════╝

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    public class FBXSwapperTest : EditorWindow
    {
        const string TOOL_NAME    = "FBX Swapper (Test)";
        const string TOOL_VERSION = "0.1";
        const string WIKI_URL     = "https://www.notion.so/shugan/FBX-Swapper";

        const string GeneratedFolder = "Assets/! Shugan/!_Lab/Script/FBXSwapper";
        const string LogsFolder      = "Assets/! Shugan/!_Lab/Script/FBXSwapper_Logs";

        GameObject _targetSceneObject;
        GameObject _newFbx;
        GameObject _oldFbx;
        bool        _pruneToMatchOriginal = true;
        string      _status = "";
        MessageType _statusType = MessageType.None;
        Vector2     _scroll;

        [MenuItem("Tools/Shugan/FBX Swapper (Test)", false, 1927)]
        static void Open()
        {
            var win = GetWindow<FBXSwapperTest>("FBX Swapper (Test)");
            win.minSize = new Vector2(420, 460);
        }

        // ─── GUI ───────────────────────────────────────────────────────────────

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            ShuganToolUI.DrawHeader("FBX Swapper  —  Test (experimental)");
            ShuganToolUI.DrawSocialLinks(WIKI_URL);

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Experimental swap method. Instead of rebuilding the avatar, it DUPLICATES your scene " +
                "avatar, gives the duplicate a private copy of the FBX, writes the new FBX into that copy, " +
                "and grafts any new bones. Your original avatar is never modified.\n\n" +
                "Best for the AutoRig Feet case (same meshes, added toe bones). Writes a debug log to " +
                LogsFolder + "/.",
                MessageType.Info);

            EditorGUILayout.Space(6);
            GUILayout.Label("Inputs", EditorStyles.boldLabel);

            _targetSceneObject = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Target Avatar (scene)", "The avatar root in the scene to duplicate + re-FBX."),
                _targetSceneObject, typeof(GameObject), allowSceneObjects: true);

            _newFbx = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("New FBX", "The FBX to switch to (e.g. the AutoRig Feet Blender export)."),
                _newFbx, typeof(GameObject), allowSceneObjects: false);

            _oldFbx = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Old FBX to Replace", "The FBX the avatar currently uses."),
                _oldFbx, typeof(GameObject), allowSceneObjects: false);

            EditorGUILayout.Space(4);
            _pruneToMatchOriginal = EditorGUILayout.ToggleLeft(
                new GUIContent("Prune extras to match original",
                    "After swapping, delete any object the duplicate has that the original doesn't — " +
                    "EXCEPT bones the new FBX added (e.g. AutoRig feet bones). Cleans up leaf/_end bones " +
                    "you had deleted from your avatar. Never deletes a bone a mesh is weighted to."),
                _pruneToMatchOriginal);

            EditorGUILayout.Space(8);

            string err = ValidateInputs(_targetSceneObject, _newFbx, _oldFbx);
            bool ready = err == null;
            if (!ready) EditorGUILayout.HelpBox(err, MessageType.Warning);

            EditorGUI.BeginDisabledGroup(!ready);
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = ready ? new Color(0.4f, 0.8f, 0.4f) : Color.white;
            if (GUILayout.Button("▶  Swap FBX (duplicate)", GUILayout.Height(34)))
            {
                var result = ExecuteSwap(_targetSceneObject, _newFbx, _oldFbx, true, _pruneToMatchOriginal);
                if (result != null)
                    SetStatus($"✓ Created '{result.name}'. Original untouched. See log in {LogsFolder}/.", MessageType.Info);
                else
                    SetStatus("Swap failed — check the Console and the log file.", MessageType.Error);
            }
            GUI.backgroundColor = prev;
            EditorGUI.EndDisabledGroup();

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.HelpBox(_status, _statusType);
            }

            EditorGUILayout.EndScrollView();
            ShuganToolUI.DrawCredits(TOOL_NAME, TOOL_VERSION);
        }

        // ─── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Duplicate-and-relink FBX swap. Returns the new duplicate GameObject (or null on failure).
        /// The original <paramref name="sceneAvatar"/> is never modified.
        /// When <paramref name="pruneToMatchOriginal"/> is true, a final pass deletes objects the
        /// duplicate has that the original lacks (e.g. leaf/_end bones the user removed from their
        /// avatar) — but keeps the bones the new FBX added (AutoRig feet bones).
        /// </summary>
        public static GameObject ExecuteSwap(GameObject sceneAvatar, GameObject newFbx, GameObject oldFbx, bool offset = true, bool pruneToMatchOriginal = true)
        {
            string err = ValidateInputs(sceneAvatar, newFbx, oldFbx);
            if (err != null) { Debug.LogError("[FBX Swapper (Test)] " + err); return null; }

            string oldFbxPath = AssetDatabase.GetAssetPath(oldFbx);
            string newFbxPath = AssetDatabase.GetAssetPath(newFbx);
            string stamp      = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string avatarName = sceneAvatar.name;

            var log = new StringBuilder();
            log.AppendLine($"# FBX Swap (Test) — {avatarName} — {stamp}");
            log.AppendLine();
            log.AppendLine($"- Avatar: `{avatarName}`");
            log.AppendLine($"- Old FBX: `{oldFbxPath}`");
            log.AppendLine($"- New FBX: `{newFbxPath}`");
            log.AppendLine();
            log.AppendLine("## BEFORE (source avatar)");
            log.AppendLine(SnapshotAvatar(sceneAvatar));

            GameObject dup = null;
            bool ok = false;
            try
            {
                // Step 2 — duplicate the scene avatar
                dup = Object.Instantiate(sceneAvatar);
                dup.name = sceneAvatar.name + "_swap";
                if (offset) dup.transform.position += Vector3.right * 1f;
                Undo.RegisterCreatedObjectUndo(dup, "FBX Swapper (Test)");
                if (PrefabUtility.IsPartOfPrefabInstance(dup))
                    PrefabUtility.UnpackPrefabInstance(dup, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                log.AppendLine("## STEP TRACE");
                log.AppendLine("- [OK] duplicate avatar + unpack");

                // Step 3 — private FBX copy (inherits old FBX import settings via its .meta)
                EnsureFolder(GeneratedFolder);
                string dupName  = $"{Path.GetFileNameWithoutExtension(oldFbxPath)}_{Sanitize(avatarName)}_{stamp}.fbx";
                string dupFbxPath = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedFolder}/{dupName}");
                if (!AssetDatabase.CopyAsset(oldFbxPath, dupFbxPath))
                    throw new System.Exception("CopyAsset failed: " + dupFbxPath);
                log.AppendLine($"- [OK] copied old FBX → `{dupFbxPath}`");

                // Step 4 — relink the duplicate to the PRIVATE FBX sub-assets (isolates the original)
                RelinkToFbx(dup, AssetDatabase.LoadAllAssetsAtPath(dupFbxPath), log);
                log.AppendLine("- [OK] relinked duplicate → private FBX (original now isolated)");

                // Step 5 — overwrite the private FBX bytes with the NEW FBX, keep its .meta, reimport
                string dupFbxAbs = ToAbs(dupFbxPath);
                File.Copy(ToAbs(newFbxPath), dupFbxAbs, overwrite: true);
                AssetDatabase.ImportAsset(dupFbxPath, ImportAssetOptions.ForceUpdate);
                log.AppendLine("- [OK] wrote new FBX content into the private FBX + reimported");

                // Step 5b — relink again against the freshly imported sub-assets (covers fileID drift)
                RelinkToFbx(dup, AssetDatabase.LoadAllAssetsAtPath(dupFbxPath), log);
                log.AppendLine("- [OK] re-relinked to reimported sub-assets");

                // Step 6 — graft new bones + rebuild SkinnedMeshRenderer bone arrays
                GraftBonesAndFixArrays(dup, dupFbxPath, log);
                log.AppendLine("- [OK] grafted new bones + rebuilt bone arrays");

                // Step 7 — prune objects the original doesn't have (keeps AutoRig additions)
                if (pruneToMatchOriginal)
                {
                    PruneToMatchOriginal(dup, sceneAvatar, oldFbx, log);
                    log.AppendLine("- [OK] pruned re-introduced objects to match the original");
                }

                log.AppendLine();
                log.AppendLine("## AFTER (produced duplicate)");
                log.AppendLine(SnapshotAvatar(dup));

                Selection.activeGameObject = dup;
                ok = true;
            }
            catch (System.Exception e)
            {
                log.AppendLine($"- [FAIL] {e.Message}");
                log.AppendLine();
                log.AppendLine("```");
                log.AppendLine(e.ToString());
                log.AppendLine("```");
                Debug.LogError("[FBX Swapper (Test)] " + e);
                if (dup != null) { Object.DestroyImmediate(dup); dup = null; }
            }
            finally
            {
                WriteLog(avatarName, stamp, log);
            }

            return ok ? dup : null;
        }

        // ─── Pipeline helpers ────────────────────────────────────────────────────

        static void RelinkToFbx(GameObject dup, Object[] fbxAssets, StringBuilder log)
        {
            var meshByName = new Dictionary<string, Mesh>();
            foreach (var m in fbxAssets.OfType<Mesh>())
                if (!meshByName.ContainsKey(m.name)) meshByName[m.name] = m;
            var avatar = fbxAssets.OfType<Avatar>().FirstOrDefault();

            foreach (var smr in dup.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.sharedMesh != null && meshByName.TryGetValue(smr.sharedMesh.name, out var m))
                    smr.sharedMesh = m;

            foreach (var mf in dup.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null && meshByName.TryGetValue(mf.sharedMesh.name, out var m))
                    mf.sharedMesh = m;

            if (avatar != null)
                foreach (var anim in dup.GetComponentsInChildren<Animator>(true))
                    if (anim.avatar != null) anim.avatar = avatar;
        }

        static void GraftBonesAndFixArrays(GameObject dup, string dupFbxPath, StringBuilder log)
        {
            var fbxMain = AssetDatabase.LoadAssetAtPath<GameObject>(dupFbxPath);
            if (fbxMain == null) { log.AppendLine("  - graft skipped: could not load FBX prefab"); return; }

            var refRoot = (GameObject)Object.Instantiate(fbxMain);
            try
            {
                var dupMap = BuildTransformMap(dup.transform);

                // 1) graft bones present in the new FBX but missing in the duplicate (parents first)
                int grafted = 0;
                foreach (var refT in refRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (refT == refRoot.transform) continue;
                    if (dupMap.ContainsKey(refT.name)) continue;

                    Transform dupParent = dup.transform;
                    if (refT.parent != null && dupMap.TryGetValue(refT.parent.name, out var p)) dupParent = p;

                    var go = new GameObject(refT.name);
                    Undo.RegisterCreatedObjectUndo(go, "FBX Swapper (Test) — graft bone");
                    go.transform.SetParent(dupParent, false);
                    go.transform.localPosition = refT.localPosition;
                    go.transform.localRotation = refT.localRotation;
                    go.transform.localScale    = refT.localScale;
                    dupMap[refT.name] = go.transform;
                    grafted++;
                    log.AppendLine($"  - grafted bone `{refT.name}` under `{dupParent.name}`");
                }
                log.AppendLine($"  - bones grafted: {grafted}");

                // 2) rebuild each SkinnedMeshRenderer's bone array from the reference skeleton
                var refSMRs = refRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (var dupSMR in dup.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var refSMR = refSMRs.FirstOrDefault(r => r.name == dupSMR.name)
                              ?? refSMRs.FirstOrDefault(r => r.sharedMesh != null && dupSMR.sharedMesh != null
                                                            && r.sharedMesh.name == dupSMR.sharedMesh.name);
                    if (refSMR == null) { log.AppendLine($"  - SMR `{dupSMR.name}`: no reference match, bones untouched"); continue; }

                    var refBones = refSMR.bones;
                    var newBones = new Transform[refBones.Length];
                    int matched = 0, unmapped = 0;
                    for (int i = 0; i < refBones.Length; i++)
                    {
                        if (refBones[i] != null && dupMap.TryGetValue(refBones[i].name, out var t)) { newBones[i] = t; matched++; }
                        else unmapped++;
                    }
                    dupSMR.bones = newBones;
                    if (refSMR.rootBone != null && dupMap.TryGetValue(refSMR.rootBone.name, out var rb)) dupSMR.rootBone = rb;

                    log.AppendLine($"  - SMR `{dupSMR.name}`: bones matched {matched}, unmapped {unmapped}, rootBone `{(dupSMR.rootBone ? dupSMR.rootBone.name : "null")}`");
                }
            }
            finally
            {
                Object.DestroyImmediate(refRoot);
            }
        }

        // ─── Prune extras to match the original ──────────────────────────────────

        // Make the duplicate's hierarchy match the original's: delete any object the duplicate has
        // that the original lacks — EXCEPT bones the new FBX added (AutoRig feet bones).
        //
        // Why this is safe & name-independent: the duplicate is Instantiate(original) + grafted
        // bones. So every "extra" came from the graft. Each extra is either
        //   (a) a name that ALSO exists in the OLD FBX  -> it came from the model and the user had
        //       deleted it from their scene avatar (e.g. an _end / leaf bone)  -> prune it, OR
        //   (b) a name NOT in the OLD FBX               -> it's new this swap (added by the AutoRig
        //       Blender script)                          -> keep it.
        // A bone a mesh is actually weighted to is never deleted (would break the mesh).
        static void PruneToMatchOriginal(GameObject dup, GameObject original, GameObject oldFbx, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("## PRUNE (match original; keep AutoRig additions)");

            var originalNames = new HashSet<string>();
            foreach (var t in original.GetComponentsInChildren<Transform>(true)) originalNames.Add(t.name);

            // Names present in the source FBX (read straight off the prefab asset — no instantiate).
            var oldFbxNames = new HashSet<string>();
            foreach (var t in oldFbx.GetComponentsInChildren<Transform>(true)) oldFbxNames.Add(t.name);

            // Deform bones in the duplicate — never delete these (would break meshes).
            var deformBones = new HashSet<Transform>();
            foreach (var smr in dup.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.bones != null) foreach (var b in smr.bones) if (b != null) deformBones.Add(b);

            // Deepest-first so leaf / _end bones are removed before their parents.
            var all = dup.GetComponentsInChildren<Transform>(true)
                         .Where(t => t != dup.transform)
                         .OrderByDescending(Depth)
                         .ToList();

            int deleted = 0, keptAutoRig = 0, keptSafety = 0;
            foreach (var t in all)
            {
                if (t == null) continue;
                if (originalNames.Contains(t.name)) continue;          // part of the original → keep
                if (!oldFbxNames.Contains(t.name)) { keptAutoRig++; continue; }  // new in FBX → AutoRig → keep

                // Re-introduced object (in old FBX, the user had removed it). Prune — with safety guards.
                if (deformBones.Contains(t)) { keptSafety++; log.AppendLine($"  - KEPT (mesh is weighted to it): {t.name}"); continue; }
                if (t.childCount > 0)        { keptSafety++; log.AppendLine($"  - KEPT (still has children):   {t.name}"); continue; }

                log.AppendLine($"  - deleted: {GetPath(t, dup.transform)}");
                Undo.DestroyObjectImmediate(t.gameObject);
                deleted++;
            }
            log.AppendLine($"  - summary: deleted {deleted}, kept-AutoRig {keptAutoRig}, kept-for-safety {keptSafety}");
        }

        static int Depth(Transform t)
        {
            int d = 0;
            while (t.parent != null) { d++; t = t.parent; }
            return d;
        }

        // ─── Snapshots & logging ─────────────────────────────────────────────────

        static string SnapshotAvatar(GameObject go)
        {
            var sb = new StringBuilder();
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                sb.AppendLine($"- SMR `{GetPath(smr.transform, go.transform)}`: mesh=`{(smr.sharedMesh ? smr.sharedMesh.name : "null")}` " +
                              $"bones={(smr.bones != null ? smr.bones.Length : 0)} rootBone=`{(smr.rootBone ? smr.rootBone.name : "null")}` " +
                              $"mats=[{string.Join(", ", smr.sharedMaterials.Select(m => m ? m.name : "null"))}]");

            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                sb.AppendLine($"- MeshFilter `{GetPath(mf.transform, go.transform)}`: mesh=`{(mf.sharedMesh ? mf.sharedMesh.name : "null")}`");

            var anim = go.GetComponentInChildren<Animator>(true);
            if (anim != null)
                sb.AppendLine($"- Animator: avatar=`{(anim.avatar ? anim.avatar.name : "null")}` isHuman={anim.isHuman}");

            var bones = go.GetComponentsInChildren<Transform>(true);
            sb.AppendLine($"- Armature transforms ({bones.Length}):");
            foreach (var t in bones)
                sb.AppendLine($"    {t.name}  (parent: {(t.parent ? t.parent.name : "-")})");
            return sb.ToString();
        }

        static void WriteLog(string avatarName, string stamp, StringBuilder log)
        {
            try
            {
                Directory.CreateDirectory(ToAbs(LogsFolder));
                string file = $"{LogsFolder}/{Sanitize(avatarName)}_{stamp}.md";
                File.WriteAllText(ToAbs(file), log.ToString());
                AssetDatabase.ImportAsset(file);
                Debug.Log($"[FBX Swapper (Test)] Log written: {file}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[FBX Swapper (Test)] Could not write log: " + e.Message);
            }
        }

        // ─── Small helpers ───────────────────────────────────────────────────────

        static string ValidateInputs(GameObject sceneAvatar, GameObject newFbx, GameObject oldFbx)
        {
            if (sceneAvatar == null) return "Assign the target avatar (a scene object).";
            if (EditorUtility.IsPersistent(sceneAvatar)) return "Target must be a scene object, not a project asset.";
            if (newFbx == null) return "Assign the New FBX.";
            if (oldFbx == null) return "Assign the Old FBX to replace.";
            if (!IsFbx(newFbx)) return "New FBX is not an .fbx asset.";
            if (!IsFbx(oldFbx)) return "Old FBX is not an .fbx asset.";
            return null;
        }

        static bool IsFbx(Object o)
        {
            string p = AssetDatabase.GetAssetPath(o);
            return !string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);
        }

        static Dictionary<string, Transform> BuildTransformMap(Transform root)
        {
            var map = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (!map.ContainsKey(t.name)) map[t.name] = t;
            return map;
        }

        static string GetPath(Transform t, Transform root)
        {
            if (t == root || t == null) return t != null ? t.name : "";
            var stack = new List<string>();
            while (t != null && t != root) { stack.Add(t.name); t = t.parent; }
            stack.Reverse();
            return string.Join("/", stack);
        }

        static string ToAbs(string assetPath)
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace(' ', '_');
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
    }
}
