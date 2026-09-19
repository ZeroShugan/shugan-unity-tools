using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// Brings an avatar in the scene back in step with its FBX after the file changed underneath it.
    ///
    /// Unity does this by itself only while the avatar is still a prefab instance of that FBX. An avatar
    /// that was unpacked — or unpacked and saved as a new prefab — keeps its own copy of the hierarchy and
    /// of every SkinnedMeshRenderer.bones array, so when the FBX gains the toe bones the renderers still
    /// point at the old skeleton. Measured on the Moe base: bones stayed at 276 while the reimported mesh
    /// wanted 376 bind poses, and Unity answers that by skipping skinning altogether, which is the mesh
    /// vanishing from the viewport. Nothing is logged; the avatar just goes quiet.
    ///
    /// The same thing happens in reverse after a restore, where the scene keeps bones the mesh no longer
    /// has (376 against 350), and after someone replaces the FBX by hand in Explorer.
    /// </summary>
    internal static class ArmatureSync
    {
        /// <summary>What the FBX looked like before the run, so the sync can tell what the FILE changed
        /// from what the USER changed.</summary>
        internal class Snapshot
        {
            public string fbxPath;
            public readonly Dictionary<string, BoneSnap> bones =
                new Dictionary<string, BoneSnap>(StringComparer.Ordinal);
        }

        internal struct BoneSnap
        {
            public string     parent;
            public Vector3    localPosition, localScale;
            public Quaternion localRotation;
        }

        /// <summary>Where the new bones should land when the avatar is an instance of a prefab that has no
        /// link to the FBX (someone unpacked the avatar and saved it again).</summary>
        internal enum UnlinkedPrefab
        {
            /// <summary>Fix the .prefab itself, so every instance of it in every scene comes right.</summary>
            PrefabAsset,
            /// <summary>Fix only this avatar; the prefab and its other instances stay on the old skeleton.</summary>
            SceneOnly,
        }

        internal class Result
        {
            public bool   linked;              // Unity keeps this one in step by itself
            public int    created, reparented, reposed, removed, rebound;
            public string prefabAssetUpdated;  // asset path, when the .prefab was fixed at source
            public string prefabAssetStale;    // asset path, when it was deliberately left alone
            public readonly List<string> unbound   = new List<string>();
            public readonly List<string> keptPoses = new List<string>();
            public string error;

            public bool Changed => created + reparented + reposed + removed + rebound > 0;
            public bool Ok      => error == null && unbound.Count == 0;

            public string Summary()
            {
                var parts = new List<string>();
                if (created    > 0) parts.Add(created    + " bone(s) added");
                if (reparented > 0) parts.Add(reparented + " re-parented");
                if (reposed    > 0) parts.Add(reposed    + " moved to the new rest pose");
                if (removed    > 0) parts.Add(removed    + " removed");
                if (rebound    > 0) parts.Add(rebound    + " renderer(s) re-bound");
                return parts.Count == 0 ? "nothing needed changing" : string.Join(", ", parts);
            }
        }

        // Bones AutoRig Feet owns. They may be created and removed freely; anything else in the armature
        // belongs to the user and is only touched when the FBX itself changed it.
        static bool IsOurBone(string name) =>
            name.StartsWith("z_CB ", StringComparison.Ordinal) ||
            name.StartsWith("Toes_", StringComparison.Ordinal) ||
            name.StartsWith("ARF Helper", StringComparison.Ordinal);

        // ─── Snapshot ──────────────────────────────────────────────────────────

        /// <summary>Read the FBX as it stands now. Taken before the Blender run, this is what lets the sync
        /// apply the file's changes while leaving a bone the user moved alone.</summary>
        internal static Snapshot Capture(string fbxPath)
        {
            var snap  = new Snapshot { fbxPath = fbxPath };
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (model == null) return snap;

            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                if (t == model.transform) continue;
                snap.bones[t.name] = new BoneSnap
                {
                    parent        = t.parent == model.transform ? "" : t.parent.name,
                    localPosition = t.localPosition,
                    localRotation = t.localRotation,
                    localScale    = t.localScale,
                };
            }
            return snap;
        }

        // ─── Sync ──────────────────────────────────────────────────────────────

        internal static Result Sync(GameObject avatarRoot, string fbxPath, Snapshot before,
                                    UnlinkedPrefab unlinkedMode = UnlinkedPrefab.PrefabAsset)
        {
            var result = new Result();
            if (avatarRoot == null) { result.error = "no avatar"; return result; }

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (model == null) { result.error = "the FBX could not be loaded: " + fbxPath; return result; }

            // 1. An avatar still linked to this FBX is Unity's job, and Unity already did it. Check it
            //    anyway — an override on a bones array can break a linked avatar too — but change nothing
            //    it doesn't have to.
            if (IsLinkedTo(avatarRoot, fbxPath))
            {
                result.linked = true;
                Validate(avatarRoot, fbxPath, result);
                if (result.unbound.Count > 0) { Rebind(avatarRoot, model, fbxPath, result); Validate(avatarRoot, fbxPath, result); }
                return result;
            }

            // 2. An instance of a prefab that is NOT the FBX: fixing the instance alone leaves the asset
            //    and its other instances broken, which is measurable — in the TestLab the second instance
            //    of such a prefab came out unbound as well. So by default the asset is fixed at source.
            string ownerPrefab = OwningPrefabAsset(avatarRoot, fbxPath);
            if (ownerPrefab != null)
            {
                if (unlinkedMode == UnlinkedPrefab.PrefabAsset)
                {
                    var contents = PrefabUtility.LoadPrefabContents(ownerPrefab);
                    try
                    {
                        var inner = SyncHierarchy(contents, model, fbxPath, before, result);
                        if (inner) PrefabUtility.SaveAsPrefabAsset(contents, ownerPrefab);
                        result.prefabAssetUpdated = ownerPrefab;
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                    }
                    AssetDatabase.Refresh();
                }
                else
                {
                    result.prefabAssetStale = ownerPrefab;
                }
            }

            // 3. The avatar in the scene: unpacked, or an instance whose prefab was just fixed (overrides
            //    can still hold it back), or one the user asked to fix in the scene only.
            SyncHierarchy(avatarRoot, model, fbxPath, before, result);
            Validate(avatarRoot, fbxPath, result);
            return result;
        }

        static bool SyncHierarchy(GameObject root, GameObject model, string fbxPath, Snapshot before,
                                  Result result)
        {
            var armature = ArmatureRoot(root, fbxPath);
            if (armature == null) { result.error = "the avatar has no skinned mesh from this FBX"; return false; }

            var modelArmature = ArmatureRoot(model, fbxPath) ?? model.transform;
            var sceneByName   = BonesByName(armature);
            var modelByName   = BonesByName(modelArmature);

            var needed = WeightedBoneNames(model, root, fbxPath);

            int before0 = result.created + result.reparented + result.reposed + result.removed;

            CreateMissing(root, armature, modelArmature, sceneByName, modelByName, needed, before, result);
            ReparentAndPose(sceneByName, modelByName, before, result);
            RemoveGone(sceneByName, modelByName, before, result);
            Rebind(root, model, fbxPath, result);

            return result.created + result.reparented + result.reposed + result.removed > before0 ||
                   result.rebound > 0;
        }

        // ─── Steps ─────────────────────────────────────────────────────────────

        static void CreateMissing(GameObject root, Transform armature, Transform modelArmature,
                                  Dictionary<string, Transform> sceneByName,
                                  Dictionary<string, Transform> modelByName,
                                  HashSet<string> needed, Snapshot before, Result result)
        {
            // Decide the whole set first. Avatars in the wild are trimmed: the Moe base here keeps 67 bones
            // out of the ~376 its meshes list, and the rest were deleted on purpose. Bringing all of those
            // back would hand the user an armature they had pruned — and bone count decides an avatar's
            // VRChat performance rank — so only bones that actually carry weight, bones this rig owns, and
            // bones the FBX gained in this run are recreated, plus whatever chain they hang from.
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in modelByName)
            {
                if (sceneByName.ContainsKey(kv.Key)) continue;

                bool isNewInThisFbx = before != null && !before.bones.ContainsKey(kv.Key);
                if (!(isNewInThisFbx || needed.Contains(kv.Key) || IsOurBone(kv.Key))) continue;

                for (var t = kv.Value; t != null && t != modelArmature; t = t.parent)
                    if (!sceneByName.ContainsKey(t.name)) wanted.Add(t.name);
            }

            // Parents first, so a chain arrives in one pass.
            foreach (var modelBone in modelArmature.GetComponentsInChildren<Transform>(true)
                                                   .OrderBy(Depth))
            {
                if (modelBone == modelArmature) continue;
                string name = modelBone.name;
                if (sceneByName.ContainsKey(name) || !wanted.Contains(name)) continue;

                Transform parent = armature;
                if (modelBone.parent != null && modelBone.parent != modelArmature &&
                    sceneByName.TryGetValue(modelBone.parent.name, out var p)) parent = p;
                else if (modelBone.parent == modelArmature) parent = armature;

                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "AutoRig Feet — re-sync armature");
                go.transform.SetParent(parent, worldPositionStays: false);
                go.transform.localPosition = modelBone.localPosition;
                go.transform.localRotation = modelBone.localRotation;
                go.transform.localScale    = modelBone.localScale;
                sceneByName[name] = go.transform;
                result.created++;
            }
        }

        static void ReparentAndPose(Dictionary<string, Transform> sceneByName,
                                    Dictionary<string, Transform> modelByName,
                                    Snapshot before, Result result)
        {
            foreach (var kv in modelByName)
            {
                if (!sceneByName.TryGetValue(kv.Key, out var sceneBone)) continue;
                var modelBone = kv.Value;

                // Re-parent when the FBX moved the bone somewhere else in the chain — which is exactly what
                // inserting a helper bone above the toe does.
                string modelParent = modelBone.parent != null ? modelBone.parent.name : "";
                string sceneParent = sceneBone.parent != null ? sceneBone.parent.name : "";
                bool moved = modelParent != sceneParent && sceneByName.ContainsKey(modelParent);
                if (moved)
                {
                    Undo.SetTransformParent(sceneBone, sceneByName[modelParent],
                                            "AutoRig Feet — re-sync armature");
                    CopyLocal(modelBone, sceneBone);
                    result.reparented++;
                    continue;
                }

                if (before == null || !before.bones.TryGetValue(kv.Key, out var was)) continue;

                bool fileMovedIt = !Approximately(was, modelBone);
                if (!fileMovedIt) continue;

                // The file moved this bone. Take the new pose only when the scene still holds the old one:
                // a value the user changed themselves is theirs to keep, and saying so beats overwriting it.
                if (SceneMatches(was, sceneBone))
                {
                    Undo.RecordObject(sceneBone, "AutoRig Feet — re-sync armature");
                    CopyLocal(modelBone, sceneBone);
                    result.reposed++;
                }
                else
                {
                    result.keptPoses.Add(kv.Key);
                }
            }
        }

        static void RemoveGone(Dictionary<string, Transform> sceneByName,
                               Dictionary<string, Transform> modelByName,
                               Snapshot before, Result result, HashSet<Transform> keep = null)
        {
            // Deepest first, so a chain comes apart from its tip.
            foreach (var kv in sceneByName.OrderByDescending(k => Depth(k.Value)).ToList())
            {
                string name = kv.Key;
                var bone = kv.Value;
                if (bone == null || modelByName.ContainsKey(name)) continue;
                // A bone a mesh of another FBX still rides on stays, whatever this FBX says about it.
                if (keep != null && keep.Contains(bone)) continue;

                // With a snapshot, anything the FBX used to have and no longer does may go. Without one
                // (the manual re-sync button), only our own rig bones are safe to remove.
                bool wasInFbx = before != null && before.bones.ContainsKey(name);
                if (!(wasInFbx || IsOurBone(name))) continue;

                // Never destroy something the user hung on the bone, and never take a component with it.
                if (bone.GetComponents<Component>().Length > 1) continue;
                bool hasKeeper = bone.GetComponentsInChildren<Transform>(true)
                                     .Any(c => c != bone && (!sceneByName.ContainsKey(c.name) ||
                                                             modelByName.ContainsKey(c.name)));
                if (hasKeeper)
                {
                    for (int i = bone.childCount - 1; i >= 0; i--)
                        Undo.SetTransformParent(bone.GetChild(i), bone.parent,
                                                "AutoRig Feet — re-sync armature");
                }
                else if (bone.childCount > 0) continue;

                sceneByName.Remove(name);
                Undo.DestroyObjectImmediate(bone.gameObject);
                result.removed++;
            }
        }

        static void Rebind(GameObject root, GameObject model, string fbxPath, Result result)
        {
            var modelSmrs = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var whole     = BonesByName(root.transform);
            var scoped    = new Dictionary<Transform, Dictionary<string, Transform>>();

            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                if (!string.Equals(AssetDatabase.GetAssetPath(mesh), fbxPath,
                                   StringComparison.OrdinalIgnoreCase)) continue;

                // Bones by name inside this renderer's OWN armature. Looked up over the whole avatar, an
                // outfit armature using the body's bone names could hand the body an outfit's bone (or the
                // other way round), whichever came first in the hierarchy.
                var arm = ArmatureOf(smr);
                Dictionary<string, Transform> byName;
                if (arm == null || !arm.IsChildOf(root.transform)) byName = whole;
                else if (!scoped.TryGetValue(arm, out byName)) scoped[arm] = byName = BonesByName(arm);

                var reference = modelSmrs.FirstOrDefault(m => m.sharedMesh == mesh)
                             ?? modelSmrs.FirstOrDefault(m => m.sharedMesh != null &&
                                                              m.sharedMesh.name == mesh.name);
                if (reference == null) continue;

                var refBones = reference.bones ?? new Transform[0];
                var bones    = new Transform[refBones.Length];
                bool changed = false;
                var existing = smr.bones ?? new Transform[0];

                for (int i = 0; i < refBones.Length; i++)
                {
                    if (refBones[i] != null && byName.TryGetValue(refBones[i].name, out var t)) bones[i] = t;
                    if (i >= existing.Length || existing[i] != bones[i]) changed = true;
                }
                if (existing.Length != bones.Length) changed = true;
                if (!changed) continue;

                Undo.RecordObject(smr, "AutoRig Feet — re-sync armature");
                smr.bones = bones;
                if (reference.rootBone != null && byName.TryGetValue(reference.rootBone.name, out var rb))
                    smr.rootBone = rb;
                if (PrefabUtility.IsPartOfPrefabInstance(smr))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(smr);
                result.rebound++;
            }
        }

        /// <summary>How many bones the meshes need that the avatar no longer has. Cheap enough for the panel
        /// to ask while it is drawing, and it catches the case where the renderers still line up but the rig
        /// the FBX gained never arrived.</summary>
        internal static int CountMissingBones(GameObject root, string fbxPath)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (root == null || model == null) return 0;

            var armature = ArmatureRoot(root, fbxPath);
            if (armature == null) return 0;
            var have = BonesByName(armature);

            return WeightedBoneNames(model, root, fbxPath).Count(n => !have.ContainsKey(n));
        }

        /// <summary>Check without changing anything — used after a step that reimports the FBX, where the
        /// meshes should not have moved but saying so out loud costs nothing.</summary>
        internal static Result ValidateOnly(GameObject root, string fbxPath)
        {
            var result = new Result();
            if (root == null) { result.error = "no avatar"; return result; }
            Validate(root, fbxPath, result);
            return result;
        }

        static void Validate(GameObject root, string fbxPath, Result result)
        {
            result.unbound.Clear();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null || mesh.bindposes == null) continue;
                if (!string.Equals(AssetDatabase.GetAssetPath(mesh), fbxPath,
                                   StringComparison.OrdinalIgnoreCase)) continue;

                var bones = smr.bones ?? new Transform[0];
                if (bones.Length != mesh.bindposes.Length)
                {
                    result.unbound.Add($"{smr.name} ({bones.Length} bones vs {mesh.bindposes.Length} bind poses)");
                    continue;
                }

                // A missing bone only breaks the mesh when vertices ride on it: avatars in the wild carry
                // empty slots for bones their owner deleted and never weighted.
                var weighted = new HashSet<int>();
                foreach (var w in mesh.boneWeights)
                {
                    if (w.weight0 > 0f) weighted.Add(w.boneIndex0);
                    if (w.weight1 > 0f) weighted.Add(w.boneIndex1);
                    if (w.weight2 > 0f) weighted.Add(w.boneIndex2);
                    if (w.weight3 > 0f) weighted.Add(w.boneIndex3);
                }
                int missing = weighted.Count(i => i < bones.Length && bones[i] == null);
                if (missing > 0) result.unbound.Add($"{smr.name} ({missing} weighted bone(s) missing)");
            }
        }

        // ─── Where things are ──────────────────────────────────────────────────

        /// <summary>True when Unity itself keeps this avatar in step: it is an instance of the FBX, or of a
        /// Variant of it.</summary>
        internal static bool IsLinkedTo(GameObject root, string fbxPath)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(root)) return false;
            var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(root);
            return source != null &&
                   string.Equals(AssetDatabase.GetAssetPath(source), fbxPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The .prefab this avatar is an instance of, when that prefab is not the FBX itself.</summary>
        internal static string OwningPrefabAsset(GameObject root, string fbxPath)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(root)) return null;
            string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            if (string.IsNullOrEmpty(path)) return null;
            if (string.Equals(path, fbxPath, StringComparison.OrdinalIgnoreCase)) return null;
            return path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        /// <summary>The top of the avatar's own skeleton, found through the renderer that uses this FBX.</summary>
        internal static Transform ArmatureRoot(GameObject root, string fbxPath)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool requireFbx = pass == 0 && !string.IsNullOrEmpty(fbxPath);
                foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr.sharedMesh == null || smr.rootBone == null) continue;
                    if (requireFbx && !string.Equals(AssetDatabase.GetAssetPath(smr.sharedMesh), fbxPath,
                                                     StringComparison.OrdinalIgnoreCase)) continue;
                    var top = smr.rootBone;
                    while (top.parent != null && top.parent != root.transform) top = top.parent;
                    return top;
                }
            }
            return null;
        }

        // ─── Outfits from another FBX ──────────────────────────────────────────
        // An outfit that ships as its own FBX brings its own armature (VRCFury Armature Link or Modular Avatar
        // join it to the body at build time). Its bones use the body's names, so everything here stays inside
        // the armature the outfit's renderers actually use: the body's skeleton is never changed through an
        // outfit, and an armature shared by meshes of several FBXs keeps the bones the other meshes need.

        /// <summary>The top of the skeleton this renderer rides on: walking up from its root bone, the last bone
        /// before the branch that also holds the renderer. For a body at the avatar root that is "Armature"; for
        /// an outfit object with its own armature it is that outfit's "Armature".</summary>
        internal static Transform ArmatureOf(SkinnedMeshRenderer smr)
        {
            if (smr == null) return null;
            var start = smr.rootBone;
            if (start == null && smr.bones != null) start = smr.bones.FirstOrDefault(b => b != null);
            if (start == null) return null;
            var holders = new HashSet<Transform>();
            for (var p = smr.transform; p != null; p = p.parent) holders.Add(p);
            if (holders.Contains(start)) return start;
            var t = start;
            while (t.parent != null && !holders.Contains(t.parent)) t = t.parent;
            return t;
        }

        /// <summary>
        /// Bring the renderers of one outfit FBX under this avatar back in step with that FBX. By how they sit
        /// in the scene: renderers still linked to the FBX (an instance of it, a Variant, or nested inside an
        /// outfit prefab) are Unity's job and only checked; renderers inside a prefab that is not linked to the
        /// FBX get that prefab fixed at source (or reported stale); anything else is fixed in place.
        /// bodyArmature: renderers riding on the body's own skeleton are only re-bound, never given bones.
        /// </summary>
        internal static Result SyncOutfit(GameObject avatarRoot, string fbxPath, Snapshot before, Transform bodyArmature,
                                          UnlinkedPrefab unlinkedMode = UnlinkedPrefab.PrefabAsset)
        {
            var result = new Result();
            if (avatarRoot == null) { result.error = "no avatar"; return result; }
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (model == null) { result.error = "the FBX could not be loaded: " + fbxPath; return result; }

            var renderers = RenderersFrom(avatarRoot, fbxPath);
            if (renderers.Count == 0) { result.error = "no mesh of " + fbxPath + " under the avatar"; return result; }

            bool anyLinked = false;
            var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var smr in renderers)
            {
                var inst = PrefabUtility.GetNearestPrefabInstanceRoot(smr.gameObject);
                if (inst == null || PrefabUtility.IsAddedGameObjectOverride(smr.gameObject)) continue;
                if (IsLinkedTo(inst, fbxPath)) { anyLinked = true; continue; }

                string owner = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(smr.gameObject);
                if (string.IsNullOrEmpty(owner) || !owner.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                    !assets.Add(owner)) continue;

                if (unlinkedMode == UnlinkedPrefab.PrefabAsset)
                {
                    var contents = PrefabUtility.LoadPrefabContents(owner);
                    try
                    {
                        var bodyInContents = bodyArmature != null
                            ? FindSamePath(contents.transform, inst.transform, bodyArmature) : null;
                        if (SyncOutfitHierarchy(contents, model, fbxPath, before, bodyInContents, result))
                            PrefabUtility.SaveAsPrefabAsset(contents, owner);
                        result.prefabAssetUpdated = owner;
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                    }
                    AssetDatabase.Refresh();
                }
                else
                {
                    result.prefabAssetStale = owner;
                }
            }
            result.linked = anyLinked;

            SyncOutfitHierarchy(avatarRoot, model, fbxPath, before, bodyArmature, result);
            ValidateRenderers(RenderersFrom(avatarRoot, fbxPath), result);
            return result;
        }

        /// <summary>Check only the renderers of one FBX, wherever they sit under the avatar.</summary>
        internal static Result ValidateOutfit(GameObject avatarRoot, string fbxPath)
        {
            var result = new Result();
            if (avatarRoot == null) { result.error = "no avatar"; return result; }
            ValidateRenderers(RenderersFrom(avatarRoot, fbxPath), result);
            return result;
        }

        internal static List<SkinnedMeshRenderer> RenderersFrom(GameObject root, string fbxPath) =>
            root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(s => s.sharedMesh != null &&
                            string.Equals(AssetDatabase.GetAssetPath(s.sharedMesh), fbxPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

        static Transform FindSamePath(Transform newRoot, Transform oldRoot, Transform target)
        {
            if (!target.IsChildOf(oldRoot)) return null;
            var names = new List<string>();
            for (var t = target; t != null && t != oldRoot; t = t.parent) names.Add(t.name);
            names.Reverse();
            var cur = newRoot;
            foreach (var n in names)
            {
                cur = cur.Cast<Transform>().FirstOrDefault(c => c.name == n);
                if (cur == null) return null;
            }
            return cur;
        }

        static bool SyncOutfitHierarchy(GameObject root, GameObject model, string fbxPath, Snapshot before,
                                        Transform bodyArmature, Result result)
        {
            int before0 = result.created + result.reparented + result.reposed + result.removed + result.rebound;
            var modelSmrs = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            // Bones a renderer of ANOTHER FBX rides on are never removed through this outfit.
            var keep = new HashSet<Transform>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null || string.Equals(AssetDatabase.GetAssetPath(smr.sharedMesh), fbxPath,
                                                            StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var b in smr.bones ?? new Transform[0]) if (b != null) keep.Add(b);
            }

            foreach (var group in RenderersFrom(root, fbxPath).GroupBy(ArmatureOf).Where(g => g.Key != null).ToList())
            {
                var armature = group.Key;
                var sceneByName = BonesByName(armature);
                var meshNames = new HashSet<string>(group.Select(g => g.sharedMesh.name));
                var refs = modelSmrs.Where(m => m.sharedMesh != null && meshNames.Contains(m.sharedMesh.name)).ToList();
                if (refs.Count == 0) continue;
                var modelArmature = ArmatureOf(refs[0]) ?? model.transform;
                var modelByName = BonesByName(modelArmature);

                bool onBody = bodyArmature != null && armature == bodyArmature;
                // Objects that come from a prefab can't be moved or deleted in the scene: their asset was fixed
                // first, or Unity keeps them in step. New bones may still be added under them.
                bool locked = PrefabUtility.IsPartOfPrefabInstance(armature.gameObject) &&
                              !PrefabUtility.IsAddedGameObjectOverride(armature.gameObject);
                if (!onBody)
                {
                    CreateMissing(root, armature, modelArmature, sceneByName, modelByName, WeightedNames(refs), before, result);
                    if (!locked)
                    {
                        ReparentAndPose(sceneByName, modelByName, before, result);
                        RemoveGone(sceneByName, modelByName, before, result, keep);
                    }
                }
                foreach (var smr in group) RebindOne(smr, modelSmrs, sceneByName, result);
            }
            return result.created + result.reparented + result.reposed + result.removed + result.rebound > before0;
        }

        static HashSet<string> WeightedNames(IEnumerable<SkinnedMeshRenderer> smrs)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var smr in smrs)
            {
                var bones = smr.bones ?? new Transform[0];
                foreach (var w in smr.sharedMesh.boneWeights)
                {
                    if (w.weight0 > 0f) Add(w.boneIndex0);
                    if (w.weight1 > 0f) Add(w.boneIndex1);
                    if (w.weight2 > 0f) Add(w.boneIndex2);
                    if (w.weight3 > 0f) Add(w.boneIndex3);
                }
                void Add(int i) { if (i >= 0 && i < bones.Length && bones[i] != null) names.Add(bones[i].name); }
            }
            return names;
        }

        static void RebindOne(SkinnedMeshRenderer smr, SkinnedMeshRenderer[] modelSmrs,
                              Dictionary<string, Transform> byName, Result result)
        {
            var mesh = smr.sharedMesh;
            var reference = modelSmrs.FirstOrDefault(m => m.sharedMesh == mesh)
                         ?? modelSmrs.FirstOrDefault(m => m.sharedMesh != null && m.sharedMesh.name == mesh.name);
            if (reference == null) return;
            var refBones = reference.bones ?? new Transform[0];
            var bones    = new Transform[refBones.Length];
            var existing = smr.bones ?? new Transform[0];
            bool changed = existing.Length != bones.Length;
            for (int i = 0; i < refBones.Length; i++)
            {
                if (refBones[i] != null && byName.TryGetValue(refBones[i].name, out var t)) bones[i] = t;
                if (i >= existing.Length || existing[i] != bones[i]) changed = true;
            }
            if (!changed) return;
            Undo.RecordObject(smr, "AutoRig Feet — re-sync outfit");
            smr.bones = bones;
            if (reference.rootBone != null && byName.TryGetValue(reference.rootBone.name, out var rb))
                smr.rootBone = rb;
            if (PrefabUtility.IsPartOfPrefabInstance(smr))
                PrefabUtility.RecordPrefabInstancePropertyModifications(smr);
            result.rebound++;
        }

        static void ValidateRenderers(IEnumerable<SkinnedMeshRenderer> renderers, Result result)
        {
            result.unbound.Clear();
            foreach (var smr in renderers)
            {
                var mesh = smr.sharedMesh;
                if (mesh == null || mesh.bindposes == null) continue;
                var bones = smr.bones ?? new Transform[0];
                if (bones.Length != mesh.bindposes.Length)
                {
                    result.unbound.Add($"{smr.name} ({bones.Length} bones vs {mesh.bindposes.Length} bind poses)");
                    continue;
                }
                var weighted = new HashSet<int>();
                foreach (var w in mesh.boneWeights)
                {
                    if (w.weight0 > 0f) weighted.Add(w.boneIndex0);
                    if (w.weight1 > 0f) weighted.Add(w.boneIndex1);
                    if (w.weight2 > 0f) weighted.Add(w.boneIndex2);
                    if (w.weight3 > 0f) weighted.Add(w.boneIndex3);
                }
                int missing = weighted.Count(i => i < bones.Length && bones[i] == null);
                if (missing > 0) result.unbound.Add($"{smr.name} ({missing} weighted bone(s) missing)");
            }
        }

        // ─── Small helpers ─────────────────────────────────────────────────────

        static Dictionary<string, Transform> BonesByName(Transform root)
        {
            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            if (root == null) return map;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (!map.ContainsKey(t.name)) map[t.name] = t;     // first wins: shallowest, the base's own
            return map;
        }

        /// <summary>
        /// Names of the bones the avatar's OWN meshes ride on.
        ///
        /// Two filters, and both were learned the hard way. A renderer's bone array lists bones nothing is
        /// weighted to — Unity gives the mesh a bind pose for every bone in the skeleton — and an avatar
        /// usually carries only some of the FBX's meshes. This one keeps Body and Body all out of fourteen,
        /// and its owner deleted the ~240 bones that the hair, skirt, tail and outfit meshes need. Measured
        /// in Blender, that body rides on 55 groups before the rig and 77 after; counting the whole FBX
        /// instead made the sync add 241 bones to a 67-bone armature, which is the user's pruning undone
        /// and their VRChat performance rank with it.
        /// </summary>
        static HashSet<string> WeightedBoneNames(GameObject model, GameObject sceneRoot, string fbxPath)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var smr in sceneRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.sharedMesh != null &&
                    string.Equals(AssetDatabase.GetAssetPath(smr.sharedMesh), fbxPath,
                                  StringComparison.OrdinalIgnoreCase))
                    present.Add(smr.sharedMesh.name);

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh  = smr.sharedMesh;
                var bones = smr.bones ?? new Transform[0];
                if (mesh == null) continue;
                // A mesh the avatar does not have says nothing about which bones it needs.
                if (present.Count > 0 && !present.Contains(mesh.name)) continue;

                foreach (var w in mesh.boneWeights)
                {
                    if (w.weight0 > 0f) Add(w.boneIndex0);
                    if (w.weight1 > 0f) Add(w.boneIndex1);
                    if (w.weight2 > 0f) Add(w.boneIndex2);
                    if (w.weight3 > 0f) Add(w.boneIndex3);
                }

                void Add(int i)
                {
                    if (i >= 0 && i < bones.Length && bones[i] != null) names.Add(bones[i].name);
                }
            }
            return names;
        }

        static int Depth(Transform t)
        {
            int d = 0;
            for (var p = t; p != null; p = p.parent) d++;
            return d;
        }

        static void CopyLocal(Transform from, Transform to)
        {
            Undo.RecordObject(to, "AutoRig Feet — re-sync armature");
            to.localPosition = from.localPosition;
            to.localRotation = from.localRotation;
            to.localScale    = from.localScale;
            if (PrefabUtility.IsPartOfPrefabInstance(to))
                PrefabUtility.RecordPrefabInstancePropertyModifications(to);
        }

        // Loose enough to survive an FBX round trip. At 0.01 degrees, bones nobody had touched came back
        // looking "moved" after Blender re-exported the file, and the sync then announced that the user had
        // moved them — a wrong accusation in a warning is worse than no warning.
        const float PosEps = 1e-4f, ScaleEps = 1e-4f, AngleEps = 0.25f;

        static bool Approximately(BoneSnap snap, Transform t) =>
            (snap.localPosition - t.localPosition).sqrMagnitude < PosEps * PosEps &&
            Quaternion.Angle(snap.localRotation, t.localRotation) < AngleEps &&
            (snap.localScale - t.localScale).sqrMagnitude < ScaleEps * ScaleEps;

        static bool SceneMatches(BoneSnap snap, Transform t) => Approximately(snap, t);
    }
}
