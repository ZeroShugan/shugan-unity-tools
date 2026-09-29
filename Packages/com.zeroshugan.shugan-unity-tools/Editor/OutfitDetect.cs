using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// Works out which meshes in an FBX are outfits that sit on the feet — socks, thigh-highs, shoes — so
    /// the panel can list them itself instead of asking the user to name each one.
    ///
    /// It reads the FBX asset rather than the avatar in the scene. That way the answer is the same whether
    /// the avatar is packed, unpacked, renamed, or missing some of the FBX's meshes, and it costs one look
    /// at the model instead of one per renderer per repaint.
    /// </summary>
    internal static class OutfitDetect
    {
        internal enum Reason
        {
            None,
            /// <summary>Vertices are weighted to the foot bones or anything under them (toe bones, a rig's
            /// toe bones, the foot helper).</summary>
            WeightedToFeet,
            /// <summary>Not weighted to them, but the geometry sits in the same place as the feet — a shoe
            /// rigged to the ankle only, for instance.</summary>
            AroundTheFeet,
        }

        internal struct Candidate
        {
            public string meshName;
            public Reason reason;
            public int    hits;        // vertices that made the case, for the log

            public string Explain() =>
                reason == Reason.WeightedToFeet ? "weighted to the feet"
              : reason == Reason.AroundTheFeet  ? "around the feet"
              : "";
        }

        // A mesh has to commit to the feet before it counts. One stray vertex inside the box is noise;
        // these are low enough for a thin sock and high enough to keep a long skirt hem out.
        const int MinWeightedVerts = 8;
        const int MinNearVerts     = 30;
        // Any weight at all counts: an outfit whose foot was painted mostly onto the shin still follows the
        // foot a little, and those are exactly the ones that bend apart from the body.
        const float MinWeight      = 0.01f;
        // The body vertices that define "the feet": mostly-foot ones, so the box stops at the ankle rather
        // than climbing the shin where the weights blend.
        const float MostlyFoot     = 0.5f;
        const float BoxMargin      = 0.1f;

        static readonly Dictionary<string, (Hash128 hash, List<Candidate> list)> Cache =
            new Dictionary<string, (Hash128, List<Candidate>)>();

        /// <summary>Every mesh of this FBX that touches the feet, the body mesh excluded. Cached against the
        /// FBX's import hash and the foot bones asked for, so it is recomputed when either changes.</summary>
        /// <param name="extraFootBones">Foot bones picked by hand in the tool, on top of the humanoid mapping
        /// and the name rules.</param>
        internal static List<Candidate> Find(string fbxPath, string bodyMeshName, IEnumerable<string> extraFootBones = null)
        {
            var result = new List<Candidate>();
            if (string.IsNullOrEmpty(fbxPath)) return result;

            var extra = (extraFootBones ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)).ToList();
            var hash = AssetDatabase.GetAssetDependencyHash(fbxPath);
            string key = fbxPath + "|" + bodyMeshName + "|" + string.Join("|", extra);
            if (Cache.TryGetValue(key, out var cached) && cached.hash == hash) return cached.list;

            try
            {
                result = Detect(fbxPath, bodyMeshName, extra);
            }
            catch (Exception e)
            {
                ShuganLog.Warn("AutoRig Feet", "Outfit detection failed: " + e.Message);
            }

            Cache[key] = (hash, result);
            return result;
        }

        internal static void Forget() => Cache.Clear();

        static List<Candidate> Detect(string fbxPath, string bodyMeshName, List<string> extraFootBones)
        {
            var found = new List<Candidate>();
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (model == null) return found;

            var feet = FootBones(fbxPath, model, extraFootBones, out var footRoots);
            if (feet.Count == 0) return found;

            var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var body = renderers.FirstOrDefault(s => s.sharedMesh != null && s.sharedMesh.name == bodyMeshName);
            Bounds? footBox = (body != null ? FootBox(body, feet) : null) ?? BoneBox(footRoots);

            foreach (var smr in renderers)
            {
                var mesh = smr.sharedMesh;
                if (mesh == null || mesh.name == bodyMeshName) continue;

                int weighted = CountWeightedToFeet(smr, feet);
                if (weighted >= MinWeightedVerts)
                {
                    found.Add(new Candidate { meshName = mesh.name, reason = Reason.WeightedToFeet, hits = weighted });
                    continue;
                }

                if (footBox == null) continue;
                int near = BindPosePositions(smr).Count(p => footBox.Value.Contains(p));
                if (near >= MinNearVerts)
                    found.Add(new Candidate { meshName = mesh.name, reason = Reason.AroundTheFeet, hits = near });
            }
            return found;
        }

        /// <summary>The foot bones and everything under them, by name. All three sources are merged, because
        /// each one misses some avatars: the humanoid mapping (the avatar creator's own answer, absent on a
        /// Generic rig), the foot bones picked in the tool, and the name rules the rig detector uses.</summary>
        static HashSet<string> FootBones(string fbxPath, GameObject model, List<string> extra, out List<Transform> roots)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            roots = new List<Transform>();
            var all = model.GetComponentsInChildren<Transform>(true);

            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer != null && importer.humanDescription.human != null)
            {
                foreach (var h in importer.humanDescription.human)
                {
                    if (h.humanName != "LeftFoot" && h.humanName != "RightFoot") continue;
                    var t = all.FirstOrDefault(x => x.name == h.boneName);
                    if (t != null) roots.Add(t);
                }
            }

            foreach (string n in extra)
            {
                var t = all.FirstOrDefault(x => x.name == n);
                if (t != null) roots.Add(t);
            }

            // Name rules only for bones that sit on a leg chain: not a foot-named bone of a mesh accessory
            // hanging off the hips, and not one inside a subtree already found.
            foreach (var t in all)
            {
                if (!HumanoidRigMapping.NameLooksLikeFoot(t.name)) continue;
                if (roots.Any(r => t.IsChildOf(r))) continue;
                if (t.parent == null || t.parent.parent == null) continue;
                roots.Add(t);
            }

            foreach (var root in roots)
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    names.Add(t.name);

            return names;
        }

        static HashSet<int> FootIndices(SkinnedMeshRenderer smr, HashSet<string> feet)
        {
            var bones = smr.bones ?? new Transform[0];
            var footIndices = new HashSet<int>();
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] != null && feet.Contains(bones[i].name)) footIndices.Add(i);
            return footIndices;
        }

        static int CountWeightedToFeet(SkinnedMeshRenderer smr, HashSet<string> feet)
        {
            var footIndices = FootIndices(smr, feet);
            if (footIndices.Count == 0) return 0;

            // All influences, not just the first four: the importer keeps more when the project allows it.
            var mesh    = smr.sharedMesh;
            var perVert = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            int count = 0, k = 0;
            for (int v = 0; v < perVert.Length; v++)
            {
                bool hit = false;
                for (int j = 0; j < perVert[v]; j++, k++)
                {
                    var w = weights[k];
                    if (!hit && w.weight > MinWeight && footIndices.Contains(w.boneIndex)) hit = true;
                }
                if (hit) count++;
            }
            return count;
        }

        /// <summary>Vertex positions in the model's space at bind pose. Meshes of one FBX can carry different
        /// object transforms (a shoe exported with its own rotation or scale), so their raw vertices are not
        /// comparable; skinning each vertex with its strongest bone puts them all in the same space.</summary>
        static IEnumerable<Vector3> BindPosePositions(SkinnedMeshRenderer smr)
        {
            var mesh      = smr.sharedMesh;
            var verts     = mesh.vertices;
            var bones     = smr.bones ?? new Transform[0];
            var bindposes = mesh.bindposes;
            var weights   = mesh.boneWeights;
            var fallback  = smr.transform.localToWorldMatrix;

            var matrices = new Matrix4x4?[bones.Length];
            for (int i = 0; i < bones.Length && i < bindposes.Length; i++)
                if (bones[i] != null) matrices[i] = bones[i].localToWorldMatrix * bindposes[i];

            for (int v = 0; v < verts.Length; v++)
            {
                Matrix4x4? m = null;
                if (v < weights.Length)
                {
                    int b = weights[v].boneIndex0;
                    if (weights[v].weight0 > 0f && b >= 0 && b < matrices.Length) m = matrices[b];
                }
                yield return (m ?? fallback).MultiplyPoint3x4(verts[v]);
            }
        }

        /// <summary>A box around the body's own foot vertices, so "in the same place as the feet" has a
        /// measurement behind it. Only mostly-foot vertices count, or the box climbs the shin.</summary>
        static Bounds? FootBox(SkinnedMeshRenderer body, HashSet<string> feet)
        {
            var footIndices = FootIndices(body, feet);
            if (footIndices.Count == 0) return null;

            var weights = body.sharedMesh.boneWeights;
            bool any = false;
            var box = new Bounds();
            int v = 0;

            foreach (var p in BindPosePositions(body))
            {
                if (v >= weights.Length) break;
                var w = weights[v++];
                float onFeet = 0f;
                if (footIndices.Contains(w.boneIndex0)) onFeet += w.weight0;
                if (footIndices.Contains(w.boneIndex1)) onFeet += w.weight1;
                if (footIndices.Contains(w.boneIndex2)) onFeet += w.weight2;
                if (footIndices.Contains(w.boneIndex3)) onFeet += w.weight3;
                if (onFeet < MostlyFoot) continue;

                if (!any) { box = new Bounds(p, Vector3.zero); any = true; }
                else box.Encapsulate(p);
            }
            if (!any) return null;

            box.Expand(box.size.magnitude * BoxMargin);
            return box;
        }

        /// <summary>When the body can't tell where the feet are (not found, or not weighted to them), a box
        /// around the foot bones themselves, grown by the foot's length so it holds a whole shoe.</summary>
        static Bounds? BoneBox(List<Transform> footRoots)
        {
            bool any = false;
            var box = new Bounds();
            float reach = 0f;
            foreach (var root in footRoots)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!any) { box = new Bounds(t.position, Vector3.zero); any = true; }
                    else box.Encapsulate(t.position);
                    reach = Mathf.Max(reach, Vector3.Distance(root.position, t.position));
                }
            }
            if (!any) return null;
            box.Expand(Mathf.Max(reach, 0.05f));
            return box;
        }
    }
}
