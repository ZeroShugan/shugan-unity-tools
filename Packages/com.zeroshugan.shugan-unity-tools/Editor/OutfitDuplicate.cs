using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// Duplicate mode for an outfit that lives in its own FBX (experimental): the copied avatar gets its own copy of
    /// the rigged outfit, and the original avatar, outfit FBX and outfit prefab stay exactly as they were.
    ///
    /// Everything is re-pointed at ASSET level so the copy's outfit stays a linked prefab: an instance of the outfit
    /// FBX is switched to the copied FBX, and an instance of an outfit .prefab is switched to a copy of that prefab
    /// whose own content points at the copied FBX. ReplacePrefabAssetOfPrefabInstance keeps the instance's
    /// overrides and added components (an Armature Link, a material swap) by matching objects by name. Only an
    /// outfit that is not a prefab at all has its meshes swapped in place.
    /// </summary>
    internal static class OutfitDuplicate
    {
        /// <summary>A private copy of the outfit FBX with the rigged content, next to the Standard swap's own
        /// copies. The .meta is copied (fresh GUID), so the import settings are the original's.</summary>
        internal static string CopyForDuplicate(string originalFbx, string riggedExportAbs, string avatarName)
        {
            FBXSwapperTest.EnsureFolder(FBXSwapperTest.GeneratedFolder);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string name  = $"{Path.GetFileNameWithoutExtension(originalFbx)}_{FBXSwapperTest.Sanitize(avatarName)}_{stamp}.fbx";
            string dst   = AssetDatabase.GenerateUniqueAssetPath(FBXSwapperTest.GeneratedFolder + "/" + name);
            if (!AssetDatabase.CopyAsset(originalFbx, dst))
                throw new Exception("could not copy " + originalFbx + " to " + dst);
            File.Copy(riggedExportAbs, FBXSwapperTest.ToAbs(dst), overwrite: true);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            return dst;
        }

        /// <summary>Switch every use of originalFbx under dup to copyFbx. Returns original asset → replacement asset
        /// for each prefab instance that now points somewhere else.</summary>
        internal static Dictionary<string, string> Repoint(GameObject dup, string originalFbx, string copyFbx, string suffix)
        {
            var repointed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var copies    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var copyModel = AssetDatabase.LoadAssetAtPath<GameObject>(copyFbx);
            if (copyModel == null) throw new Exception("the outfit copy could not be loaded: " + copyFbx);

            foreach (var root in OutermostRootsUsing(dup, originalFbx))
            {
                string asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
                string target = ReplacementFor(asset, originalFbx, copyFbx, suffix, copies);
                if (target == null) continue;
                PrefabUtility.ReplacePrefabAssetOfPrefabInstance(root, AssetDatabase.LoadAssetAtPath<GameObject>(target),
                                                                 Settings(), InteractionMode.AutomatedAction);
                repointed[asset] = target;
            }
            RelinkMeshes(dup, originalFbx, copyFbx);
            // Instances nested inside a copied prefab now point at the copied FBX too.
            repointed[originalFbx] = copyFbx;
            foreach (var kv in copies) repointed[kv.Key] = kv.Value;
            return repointed;
        }

        /// <summary>Restore to original on a copy: instances switched to `copyAsset` go back to `originalAsset`, and plain
        /// renderers of a copied outfit FBX get the original's meshes again.</summary>
        internal static void RepointBack(GameObject root, string copyAsset, string originalAsset)
        {
            var original = AssetDatabase.LoadAssetAtPath<GameObject>(originalAsset);
            if (original == null) return;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || !PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject)) continue;
                if (!string.Equals(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject), copyAsset,
                                   StringComparison.OrdinalIgnoreCase)) continue;
                PrefabUtility.ReplacePrefabAssetOfPrefabInstance(t.gameObject, original, Settings(), InteractionMode.UserAction);
            }
            if (copyAsset.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && originalAsset.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                RelinkMeshes(root, copyAsset, originalAsset);
        }

        static PrefabReplacingSettings Settings() => new PrefabReplacingSettings
        {
            logInfo = false,
            changeRootNameToAssetName = false,
            objectMatchMode = ObjectMatchMode.ByName,
            prefabOverridesOptions = PrefabOverridesOptions.KeepAllPossibleOverrides,
        };

        /// <summary>The outermost prefab instances (below root) that hold a renderer of the FBX.</summary>
        static List<GameObject> OutermostRootsUsing(GameObject root, string fbxPath)
        {
            var list = new List<GameObject>();
            foreach (var smr in ArmatureSync.RenderersFrom(root, fbxPath))
            {
                if (!PrefabUtility.IsPartOfPrefabInstance(smr.gameObject) ||
                    PrefabUtility.IsAddedGameObjectOverride(smr.gameObject)) continue;
                var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(smr.gameObject);
                if (outer == null || outer == root && !PrefabUtility.IsAnyPrefabInstanceRoot(root)) continue;
                if (!list.Contains(outer)) list.Add(outer);
            }
            return list;
        }

        /// <summary>What an instance of `asset` should point at instead: the FBX copy for the FBX itself, a
        /// re-pointed copy for a .prefab that uses the FBX somewhere inside, null for anything else.</summary>
        static string ReplacementFor(string asset, string originalFbx, string copyFbx, string suffix,
                                     Dictionary<string, string> copies)
        {
            if (string.Equals(asset, originalFbx, StringComparison.OrdinalIgnoreCase)) return copyFbx;
            if (string.IsNullOrEmpty(asset) || !asset.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return null;
            if (copies.TryGetValue(asset, out var done)) return done;

            string dir  = Path.GetDirectoryName(asset).Replace("\\", "/");
            string name = Path.GetFileNameWithoutExtension(asset) + "_" + (string.IsNullOrEmpty(suffix) ? "Rig_Feet" : suffix.Trim());
            string dst  = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + name + ".prefab");
            if (!AssetDatabase.CopyAsset(asset, dst)) throw new Exception("could not copy " + asset);
            copies[asset] = dst;

            var contents = PrefabUtility.LoadPrefabContents(dst);
            try
            {
                foreach (var inner in OutermostRootsUsing(contents, originalFbx))
                {
                    string innerAsset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(inner);
                    string innerTarget = ReplacementFor(innerAsset, originalFbx, copyFbx, suffix, copies);
                    if (innerTarget == null) continue;
                    PrefabUtility.ReplacePrefabAssetOfPrefabInstance(inner, AssetDatabase.LoadAssetAtPath<GameObject>(innerTarget),
                                                                     Settings(), InteractionMode.AutomatedAction);
                }
                RelinkMeshes(contents, originalFbx, copyFbx);
                PrefabUtility.SaveAsPrefabAsset(contents, dst);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            return dst;
        }

        /// <summary>Renderers of the FBX that are not part of a prefab instance get the copy's mesh of the same name.
        /// Bones are brought in step afterwards by ArmatureSync.SyncOutfit.</summary>
        static void RelinkMeshes(GameObject root, string originalFbx, string copyFbx)
        {
            var meshes = AssetDatabase.LoadAllAssetsAtPath(copyFbx).OfType<Mesh>()
                                      .GroupBy(m => m.name).ToDictionary(g => g.Key, g => g.First());
            foreach (var smr in ArmatureSync.RenderersFrom(root, originalFbx))
            {
                if (PrefabUtility.IsPartOfPrefabInstance(smr.gameObject) &&
                    !PrefabUtility.IsAddedGameObjectOverride(smr.gameObject)) continue;
                if (!meshes.TryGetValue(smr.sharedMesh.name, out var mesh)) continue;
                Undo.RecordObject(smr, "AutoRig Feet — outfit copy");
                smr.sharedMesh = mesh;
            }
        }
    }
}
