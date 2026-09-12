using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    // Main-thread half of FootShapeFit: reads the renderer, bones and shape keys into plain arrays.
    public static partial class FootShapeFit
    {
        public static Input Capture(SkinnedMeshRenderer smr, Transform footL, Transform footR,
                                    Transform toeL, Transform toeR, Transform upReference)
        {
            var input = new Input();
            try
            {
                CaptureInto(input, smr, footL, footR, toeL, toeR, upReference);
            }
            catch (Exception e)
            {
                input.error = "the mesh data could not be read (" + e.Message + ")";
            }
            return input;
        }

        static void CaptureInto(Input input, SkinnedMeshRenderer smr, Transform footL, Transform footR,
                                Transform toeL, Transform toeR, Transform upReference)
        {
            if (smr == null || smr.sharedMesh == null) { input.error = "no body mesh"; return; }
            var mesh = smr.sharedMesh;
            input.meshName = mesh.name;
            if (footL == null && footR == null)
            {
                input.error = "no foot bones could be identified (map the avatar to Humanoid, or pick the foot bones)";
                return;
            }

            var bones     = smr.bones;
            var bindposes = mesh.bindposes;
            var boneIndex = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Length && i < bindposes.Length; i++)
                if (bones[i] != null && !boneIndex.ContainsKey(bones[i])) boneIndex[bones[i]] = i;

            var weighted = new HashSet<Transform>();
            foreach (var w in mesh.boneWeights)
            {
                if (w.weight0 > 0f) MarkWeighted(bones, w.boneIndex0, weighted);
                if (w.weight1 > 0f) MarkWeighted(bones, w.boneIndex1, weighted);
                if (w.weight2 > 0f) MarkWeighted(bones, w.boneIndex2, weighted);
                if (w.weight3 > 0f) MarkWeighted(bones, w.boneIndex3, weighted);
            }

            // Bone position in mesh space, from the bind pose rather than the scene pose, so a posed
            // avatar measures the same as one in T-pose. A bone without a bind pose (unweighted bones
            // are stripped from the renderer, like Maya's "Left Toe") hangs off its nearest ancestor's.
            Vector3 MeshSpace(Transform t)
            {
                for (var a = t; a != null; a = a.parent)
                    if (boneIndex.TryGetValue(a, out int i))
                        return bindposes[i].inverse.MultiplyPoint3x4(a == t ? Vector3.zero : a.InverseTransformPoint(t.position));
                return smr.transform.InverseTransformPoint(t.position);
            }

            var verts = mesh.vertices;
            var joints  = new List<Vector3>();
            var samples = new List<Vector3>();
            float scale = 0f;
            foreach (var (side, foot, toe) in new[] { ("L", footL, toeL), ("R", footR, toeR) })
            {
                if (foot == null) continue;
                var included = new List<Transform> { foot };
                CollectJoints(foot, foot, toe, weighted, included);

                var names = side == "L" ? input.jointNamesL : input.jointNamesR;
                var pos   = new Dictionary<Transform, Vector3>();
                foreach (var t in included)
                {
                    pos[t] = MeshSpace(t);
                    names.Add(t.name);
                    joints.Add(pos[t]);
                    samples.Add(pos[t]);
                }
                Vector3 footPos = pos[foot];
                foreach (var t in included)
                {
                    scale = Mathf.Max(scale, (pos[t] - footPos).magnitude);
                    if (t == foot) continue;
                    var p = t.parent;
                    while (p != null && !pos.ContainsKey(p)) p = p.parent;
                    if (p == null || (pos[t] - pos[p]).sqrMagnitude < 1e-12f) continue;
                    for (int k = 1; k < SegmentSamples - 1; k++)
                        samples.Add(Vector3.Lerp(pos[p], pos[t], k / (float)(SegmentSamples - 1)));
                }
            }
            if (joints.Count == 0) { input.error = "no foot bones could be identified"; return; }
            if (scale <= 1e-6f)
            {
                // Nothing below the ankle: the armature has no toe bone yet, which is the ordinary
                // state of a base about to be rigged. The measure still says something — an ankle joint
                // sitting outside the mesh is exactly what a Basis modelled in the wrong pose looks
                // like — it only needs a foot length from somewhere, so it comes from the foot's own
                // geometry instead of from a joint that isn't there. (Blender takes the foot BONE's
                // length here, which Unity has no equivalent of: a Transform has no tail.)
                scale = FootMeshExtent(verts, mesh.boneWeights, boneIndex, footL, footR);
                input.coarse = true;
            }
            if (scale <= 1e-6f) { input.error = "nothing on the body mesh is skinned to the foot bones"; return; }
            input.scale   = scale;
            input.joints  = joints.ToArray();
            input.samples = samples.ToArray();

            int n = verts.Length;
            float r2 = (RegionFactor * scale) * (RegionFactor * scale);
            var near = new bool[n];
            for (int v = 0; v < n; v++)
                foreach (var j in input.joints)
                    if ((verts[v] - j).sqrMagnitude <= r2) { near[v] = true; break; }

            // "Up" in mesh space, for telling the upper leg apart from the foot.
            Vector3 up = upReference != null
                ? smr.transform.InverseTransformDirection(upReference.up)
                : smr.transform.InverseTransformDirection(Vector3.up);
            up = up.sqrMagnitude > 1e-12f ? up.normalized : Vector3.up;
            float topJoint = float.NegativeInfinity;
            foreach (var j in input.joints) topJoint = Mathf.Max(topJoint, Vector3.Dot(j, up));
            float farAbove = topJoint + 2f * scale;

            var tri    = mesh.triangles;
            var remap  = new int[n];
            for (int v = 0; v < n; v++) remap[v] = -1;
            var region = new List<int>();
            var rtris  = new List<int>();
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                int a = tri[t], b = tri[t + 1], c = tri[t + 2];
                if (!(near[a] || near[b] || near[c])) continue;
                foreach (int v in new[] { a, b, c })
                {
                    if (remap[v] < 0) { remap[v] = region.Count; region.Add(v); }
                    rtris.Add(remap[v]);
                }
            }
            if (rtris.Count == 0) { input.error = "the body mesh has no faces around the foot bones"; return; }
            input.regionTris = rtris.ToArray();
            input.regionBase = new Vector3[region.Count];
            for (int i = 0; i < region.Count; i++) input.regionBase[i] = verts[region[i]];

            // Which way round the triangles are wound, checked against the imported normals rather
            // than assumed: the inside test depends on it.
            var normals = mesh.normals;
            if (normals != null && normals.Length == n)
            {
                double agree = 0;
                for (int t = 0; t + 2 < tri.Length; t += 3)
                {
                    int a = tri[t], b = tri[t + 1], c = tri[t + 2];
                    if (!(near[a] || near[b] || near[c])) continue;
                    var face = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
                    agree += Vector3.Dot(face, normals[a] + normals[b] + normals[c]);
                }
                input.winding = agree < 0 ? -1f : 1f;
            }

            var dv = new Vector3[n];
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                int frames = mesh.GetBlendShapeFrameCount(i);
                if (frames == 0) continue;
                mesh.GetBlendShapeFrameVertices(i, frames - 1, dv, null, null);
                float foot2 = 0f, all2 = 0f, far2 = 0f;
                for (int v = 0; v < n; v++)
                {
                    float m2 = dv[v].sqrMagnitude;
                    if (m2 > all2) all2 = m2;
                    if (near[v] && m2 > foot2) foot2 = m2;
                    if (m2 > far2 && Vector3.Dot(verts[v], up) > farAbove) far2 = m2;
                }
                float footMove = Mathf.Sqrt(foot2);
                if (footMove < MinKeyMove * scale) continue;   // does not touch the feet
                var info = new KeyInfo
                {
                    name        = mesh.GetBlendShapeName(i),
                    index       = i,
                    sceneWeight = smr.GetBlendShapeWeight(i),
                    footMove    = footMove,
                    allMove     = Mathf.Sqrt(all2),
                    farMove     = Mathf.Sqrt(far2),
                };
                info.kind = info.allMove > HideFactor * scale ? KeyKind.Hide
                          : info.farMove > MaxFarRatio * footMove ? KeyKind.BodyShape
                          : KeyKind.Candidate;
                // Kept for every key that touches the feet, not only the switched-on ones: a later change
                // of shape-key values then only re-runs the search, without reading the mesh again.
                info.regionDelta = new Vector3[region.Count];
                for (int k = 0; k < region.Count; k++) info.regionDelta[k] = dv[region[k]];
                input.keys.Add(info);
            }
        }

        // Longest side of the box around one foot's own vertices — a stand-in for foot length on a rig
        // whose feet end at the ankle. Measured a foot at a time: both boxes together would span the
        // gap between the legs instead.
        static float FootMeshExtent(Vector3[] verts, BoneWeight[] weights, Dictionary<Transform, int> boneIndex,
                                    params Transform[] feet)
        {
            if (weights == null || weights.Length != verts.Length) return 0f;
            float best = 0f;
            foreach (var foot in feet)
            {
                if (foot == null || !boneIndex.TryGetValue(foot, out int bone)) continue;
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                bool any = false;
                for (int v = 0; v < weights.Length; v++)
                {
                    var w = weights[v];
                    if (!((w.weight0 > 0f && w.boneIndex0 == bone) || (w.weight1 > 0f && w.boneIndex1 == bone) ||
                          (w.weight2 > 0f && w.boneIndex2 == bone) || (w.weight3 > 0f && w.boneIndex3 == bone)))
                        continue;
                    min = Vector3.Min(min, verts[v]);
                    max = Vector3.Max(max, verts[v]);
                    any = true;
                }
                if (!any) continue;
                var size = max - min;
                best = Mathf.Max(best, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
            }
            return best;
        }

        static void MarkWeighted(Transform[] bones, int index, HashSet<Transform> weighted)
        {
            if (index >= 0 && index < bones.Length && bones[index] != null) weighted.Add(bones[index]);
        }

        static bool IsEndBone(string name)
        {
            string n = name.ToLowerInvariant();
            return n.EndsWith("_end") || n.EndsWith(".end") || n.EndsWith(" end");
        }

        // The foot's weighted descendants, plus the toe bone. Unweighted leaves and *_end bones are
        // tip markers or accessories placed anywhere. A rig whose toe bone carries no weight still gets
        // its toe joint: a direct child of the foot that has children of its own.
        static void CollectJoints(Transform foot, Transform node, Transform toe, HashSet<Transform> weighted,
                                  List<Transform> included)
        {
            for (int i = 0; i < node.childCount; i++)
            {
                var c = node.GetChild(i);
                if (!IsEndBone(c.name) &&
                    (c == toe || weighted.Contains(c) ||
                     (toe == null && node == foot && c.childCount > 0 && c.GetComponent<Renderer>() == null)))
                    included.Add(c);
                CollectJoints(foot, c, toe, weighted, included);
            }
        }
    }
}
