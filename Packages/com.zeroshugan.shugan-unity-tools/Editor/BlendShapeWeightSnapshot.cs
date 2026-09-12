using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    // ─── Shape-key values of a scene avatar ─────────────────────────────────────
    // AutoRig Feet rebuilds the avatar from a new FBX, and the result must keep whatever the user had
    // dialled in on the scene avatar: a heel key at 100, a face key at 35. The rig itself is built on
    // the foot shape that fits the bones, and the VRCFury foot-shape toggle is what switches the feet
    // into that shape while the feet system is in use, so the scene values never need to change.
    //
    // Values are stored by renderer PATH and shape-key NAME, never by index: a re-export can reorder
    // shape keys, and index-based prefab overrides would then land on the wrong ones.
    public static class BlendShapeWeightSnapshot
    {
        [Serializable]
        public class Entry
        {
            public string path;   // renderer, relative to the avatar root
            public string mesh;   // fallback match when the path changed
            public string key;
            public float  value;
        }

        [Serializable]
        public class Snapshot
        {
            public string avatar;
            public List<Entry> weights = new List<Entry>();
        }

        public static Snapshot Capture(GameObject root)
        {
            var snap = new Snapshot { avatar = root != null ? root.name : "" };
            if (root == null) return snap;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                string path = RelativePath(root.transform, smr.transform);
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    snap.weights.Add(new Entry
                    {
                        path  = path,
                        mesh  = mesh.name,
                        key   = mesh.GetBlendShapeName(i),
                        value = smr.GetBlendShapeWeight(i),
                    });
            }
            return snap;
        }

        public static void Save(Snapshot snap, string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, JsonUtility.ToJson(snap, true));
        }

        public static Snapshot Load(string file) =>
            File.Exists(file) ? JsonUtility.FromJson<Snapshot>(File.ReadAllText(file)) : null;

        // Sets every saved value on the matching renderer of `root`, zeros included, so a reimport
        // that changed a default can't shift anything either. Returns how many values matched and how
        // many actually changed; `missing` receives the non-zero values that found no renderer or key.
        public static (int matched, int changed) Apply(GameObject root, Snapshot snap, List<string> missing)
        {
            if (root == null || snap == null) return (0, 0);

            var byPath = new Dictionary<string, SkinnedMeshRenderer>();
            var byMesh = new Dictionary<string, SkinnedMeshRenderer>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string path = RelativePath(root.transform, smr.transform);
                if (!byPath.ContainsKey(path)) byPath[path] = smr;
                if (smr.sharedMesh != null && !byMesh.ContainsKey(smr.sharedMesh.name)) byMesh[smr.sharedMesh.name] = smr;
            }

            int matched = 0, changed = 0;
            var recorded = new HashSet<SkinnedMeshRenderer>();
            foreach (var e in snap.weights)
            {
                if (!byPath.TryGetValue(e.path ?? "", out var smr) && !byMesh.TryGetValue(e.mesh ?? "", out smr))
                {
                    if (Mathf.Abs(e.value) > 0.001f) missing?.Add($"{e.path}: {e.key} = {e.value:0.##} (renderer not found)");
                    continue;
                }
                int idx = smr.sharedMesh != null ? smr.sharedMesh.GetBlendShapeIndex(e.key) : -1;
                if (idx < 0)
                {
                    if (Mathf.Abs(e.value) > 0.001f) missing?.Add($"{e.path}: {e.key} = {e.value:0.##} (shape key not found)");
                    continue;
                }
                matched++;
                if (Mathf.Approximately(smr.GetBlendShapeWeight(idx), e.value)) continue;
                if (recorded.Add(smr)) Undo.RecordObject(smr, "AutoRig Feet: restore shape-key values");
                smr.SetBlendShapeWeight(idx, e.value);
                changed++;
            }
            foreach (var smr in recorded)
                PrefabUtility.RecordPrefabInstancePropertyModifications(smr);
            return (matched, changed);
        }

        static string RelativePath(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
