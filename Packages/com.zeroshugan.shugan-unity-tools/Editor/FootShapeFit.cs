using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    // ─── Foot-shape fit ──────────────────────────────────────────────────────────
    // Which of the body's shape keys, each at 0 or 100, put the avatar's own foot and toe joints
    // inside the foot mesh. Some bases ship a Basis modelled in the wrong pose: Maya's Basis is a high
    // heel, and "High heeled" = 100 brings back the flat foot the armature was built for. Rigging the
    // toes on that Basis builds them around a foot that sits outside its own bones.
    //
    // Port of analyze_rig_shape in shugan_autorig_feet.py. Keep the two in step: same joints, same
    // inside test, same thresholds. They were calibrated on 17 Booth bases, where exactly Maya and Airi
    // need a shape and every other base stays on Basis.
    //
    // This half is plain math on captured arrays and runs on a worker thread. FootShapeFitCapture.cs
    // reads the mesh on the main thread, since Unity's API is not thread-safe.
    public static partial class FootShapeFit
    {
        // A combination must beat the current best by all of these to replace it.
        public const float MinSampleGain = 0.25f;   // share of sample points inside
        public const float MinDepthGain  = 0.08f;   // mean signed depth, x foot length
        // Which shape keys are searched.
        public const float MinKeyMove    = 0.02f;   // moves the feet by at least this x foot length
        public const float HideFactor    = 1.5f;    // moves any vertex more than this x foot length = hides geometry
        public const float MaxFarRatio   = 0.25f;   // moves the upper leg or body more than this x its foot move
        // Search and sampling.
        public const int   ExhaustiveMax  = 8;
        public const int   GreedyRounds   = 4;
        public const float RegionFactor   = 1.5f;   // triangles within this x foot length of a joint are tested
        public const int   SegmentSamples = 11;     // points per joint-to-joint segment, ends included
        // A key that is switched on in the scene fits worse than 0 when turning it on loses a joint,
        // or this much of either measure.
        public const float WorseSampleDrop = 0.05f;
        public const float WorseDepthDrop  = 0.02f;
        // A key matters for the toggle when flipping it away from the best shape costs this much fit;
        // either measure is enough. Measured on the Booth bases: Airi's High heels_ON (-64 points,
        // -0.11 foot) is in, its Toes_L (-25 points, -0.01 foot) and Foot_L (-14, -0.02) are not.
        public const float SignificantSampleDrop = 0.30f;
        public const float SignificantDepthDrop  = 0.08f;

        public struct Metrics
        {
            public float joints;    // share of joints inside the mesh
            public float samples;   // share of joint and segment points inside
            public float depth;     // mean signed distance to the surface, mesh units (+ inside)
        }

        public enum KeyKind { Candidate, Hide, BodyShape }

        public sealed class KeyInfo
        {
            public string  name;
            public int     index;
            public float   sceneWeight;   // 0-100, as set on the scene renderer
            public KeyKind kind;
            public float   footMove, allMove, farMove;
            internal Vector3[] regionDelta;   // kept for candidates and for keys that are switched on
        }

        public enum RowReason
        {
            BestFit,         // part of the best combination: the toggle sets it to 100
            FitsWorse,       // switching it on costs real fit: the toggle holds it at 0
            OnAndFitsWorse,  // switched on in the scene, and the feet fit better without it
            Minor,           // moves the feet but barely changes the fit
            HidesGeometry,   // listed, never ticked by default
            ReshapesBody,    // listed, never ticked by default
        }

        // One row per shape key that moves the feet.
        public sealed class ToggleRow
        {
            public string    key;
            public float     value;             // what the toggle sets: 100 for the best fit, otherwise 0
            public float     sceneWeight;       // the value on the scene avatar now
            public RowReason reason;
            public bool      includedByDefault;
            public float     samplesDrop;       // fit lost when this key is flipped away from the best shape
            public float     depthDrop;         // same, mean depth in foot lengths
        }

        public sealed class Input
        {
            public string error;
            public string meshName;
            public float  scale;
            public List<string> jointNamesL = new List<string>();
            public List<string> jointNamesR = new List<string>();
            public List<KeyInfo> keys = new List<KeyInfo>();
            internal Vector3[] joints;
            internal Vector3[] samples;
            internal Vector3[] regionBase;
            internal int[]     regionTris;
            internal float     winding = 1f;
        }

        public sealed class Result
        {
            public string error;
            public string meshName;
            public float  scale;
            public Metrics basis, best;
            public List<string> combo = new List<string>();
            public Dictionary<string, Metrics> singles = new Dictionary<string, Metrics>();
            public List<KeyInfo>   keys = new List<KeyInfo>();
            public List<ToggleRow> rows = new List<ToggleRow>();
            public List<string> jointNamesL = new List<string>();
            public List<string> jointNamesR = new List<string>();
            public int  evaluations;
            public long milliseconds;
            public bool NeedsShape => combo.Count > 0;
        }

        public static Result Run(Input input)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new Result
            {
                error = input.error, meshName = input.meshName, scale = input.scale, keys = input.keys,
                jointNamesL = input.jointNamesL, jointNamesR = input.jointNamesR,
            };
            if (input.error != null) return r;

            var bvh    = new TriBvh(input.regionTris, input.regionBase, input.winding);
            var coords = new Vector3[input.regionBase.Length];
            var cache  = new Dictionary<string, Metrics>();
            float scale = input.scale;

            Metrics Measure(List<(KeyInfo key, float amount)> parts)
            {
                var ids = new List<string>();
                foreach (var p in parts) ids.Add(p.key.index + "@" + p.amount.ToString("R"));
                ids.Sort(StringComparer.Ordinal);
                string id = string.Join("|", ids);
                if (cache.TryGetValue(id, out var cached)) return cached;

                Array.Copy(input.regionBase, coords, coords.Length);
                foreach (var p in parts)
                {
                    var d = p.key.regionDelta;
                    for (int v = 0; v < coords.Length; v++) coords[v] += d[v] * p.amount;
                }
                bvh.Refit(coords);

                int jointsIn = 0;
                foreach (var j in input.joints) if (bvh.Inside(j)) jointsIn++;
                int samplesIn = 0;
                double depth = 0;
                foreach (var s in input.samples)
                {
                    bool inside = bvh.Inside(s);
                    float dist = bvh.NearestDistance(s);
                    depth += inside ? dist : -dist;
                    if (inside) samplesIn++;
                }
                var m = new Metrics
                {
                    joints  = jointsIn / (float)input.joints.Length,
                    samples = samplesIn / (float)input.samples.Length,
                    depth   = (float)(depth / input.samples.Length),
                };
                cache[id] = m;
                return m;
            }

            List<(KeyInfo, float)> Parts(IEnumerable<KeyInfo> keys)
            {
                var list = new List<(KeyInfo, float)>();
                foreach (var k in keys) list.Add((k, 1f));
                return list;
            }

            var cands = input.keys.FindAll(k => k.kind == KeyKind.Candidate);
            r.basis = Measure(new List<(KeyInfo, float)>());
            foreach (var c in cands) r.singles[c.name] = Measure(Parts(new[] { c }));

            var best  = new List<KeyInfo>();
            var bestM = r.basis;

            if (cands.Count <= ExhaustiveMax)
            {
                // Size by size: a bigger combination only replaces the best so far when it beats it by
                // the full margin, so a cosmetic key tagging along (toenails) can never win.
                for (int size = 1; size <= cands.Count; size++)
                {
                    List<KeyInfo> sizeBest = null;
                    Metrics sizeBestM = default;
                    foreach (var combo in Combinations(cands, size))
                    {
                        var m = Measure(Parts(combo));
                        if (!Beats(m, bestM, scale)) continue;
                        if (sizeBest == null || Better(m, sizeBestM)) { sizeBest = combo; sizeBestM = m; }
                    }
                    if (sizeBest != null) { best = sizeBest; bestM = sizeBestM; }
                }
            }
            else
            {
                // Too many for every combination: each round re-tests every remaining key alone and every
                // pair, from the best shape so far. A key that only helps once another is on is found in
                // a later round; one that only helps together with another is found as a pair.
                for (int round = 0; round < GreedyRounds; round++)
                {
                    var remaining = cands.FindAll(c => !best.Contains(c));
                    List<KeyInfo> pick = null;
                    Metrics pickM = default;
                    int pickSize = int.MaxValue;
                    foreach (var move in SinglesAndPairs(remaining))
                    {
                        var combo = new List<KeyInfo>(best);
                        combo.AddRange(move);
                        var m = Measure(Parts(combo));
                        if (!Beats(m, bestM, scale)) continue;
                        if (pick == null || move.Count < pickSize || (move.Count == pickSize && Better(m, pickM)))
                        {
                            pick = combo; pickM = m; pickSize = move.Count;
                        }
                    }
                    if (pick == null) break;
                    best = pick; bestM = pickM;
                }
            }

            r.best = bestM;
            foreach (var k in best) r.combo.Add(k.name);

            // Rows: every shape key that moves the feet, at the value it has in the best shape. Each one is
            // flipped away from the best shape to see what it costs. Ticked by default: the best fit, keys
            // whose flip costs real fit, and keys switched on in the scene that fit worse than 0. A toggle
            // holding all of those puts the feet in the rigged shape whatever else switches them.
            // Keys that hide geometry or reshape the body are listed but never ticked by default.
            // (Rows are Unity-only; the Blender script only needs the combination.)
            var rowList = new List<(ToggleRow row, float magnitude)>();
            foreach (var k in input.keys)
            {
                if (k.regionDelta == null) continue;
                bool inBest = best.Contains(k);
                List<(KeyInfo, float)> flipped;
                if (inBest)
                {
                    var without = new List<KeyInfo>(best);
                    without.Remove(k);
                    flipped = Parts(without);
                }
                else
                {
                    flipped = Parts(best);
                    flipped.Add((k, 1f));
                }
                var fm = Measure(flipped);
                float samplesDrop = bestM.samples - fm.samples;
                float depthDrop   = (bestM.depth - fm.depth) / scale;
                bool significant  = samplesDrop >= SignificantSampleDrop || depthDrop >= SignificantDepthDrop;

                bool onAndWorse = false;
                if (!inBest && Mathf.Abs(k.sceneWeight) > 0.01f)
                {
                    var on = Parts(best);
                    on.Add((k, k.sceneWeight / 100f));
                    onAndWorse = Worse(Measure(on), bestM, scale);
                }

                RowReason reason = inBest ? RowReason.BestFit
                                 : k.kind == KeyKind.Hide ? RowReason.HidesGeometry
                                 : k.kind == KeyKind.BodyShape ? RowReason.ReshapesBody
                                 : onAndWorse ? RowReason.OnAndFitsWorse
                                 : significant ? RowReason.FitsWorse
                                 : RowReason.Minor;
                rowList.Add((new ToggleRow
                {
                    key = k.name,
                    value = inBest ? 100f : 0f,
                    sceneWeight = k.sceneWeight,
                    reason = reason,
                    includedByDefault = inBest || (k.kind == KeyKind.Candidate && (significant || onAndWorse)),
                    samplesDrop = samplesDrop,
                    depthDrop = depthDrop,
                }, samplesDrop + depthDrop));
            }
            int Rank(ToggleRow row) => row.reason == RowReason.BestFit ? 0 : row.includedByDefault ? 1 : 2;
            rowList.Sort((a, b) =>
                Rank(a.row) != Rank(b.row) ? Rank(a.row).CompareTo(Rank(b.row))
                : a.magnitude != b.magnitude ? b.magnitude.CompareTo(a.magnitude)
                : string.CompareOrdinal(a.row.key, b.row.key));
            foreach (var entry in rowList) r.rows.Add(entry.row);

            r.evaluations  = cache.Count;
            r.milliseconds = sw.ElapsedMilliseconds;
            return r;
        }

        static bool Beats(Metrics a, Metrics b, float scale) =>
            a.joints >= b.joints - 1e-6f &&
            a.samples >= b.samples + MinSampleGain &&
            a.depth >= b.depth + MinDepthGain * scale;

        static bool Worse(Metrics a, Metrics b, float scale) =>
            a.joints < b.joints - 1e-6f ||
            a.samples < b.samples - WorseSampleDrop ||
            a.depth < b.depth - WorseDepthDrop * scale;

        static bool Better(Metrics a, Metrics b) =>
            a.samples > b.samples || (a.samples == b.samples && a.depth > b.depth);

        static IEnumerable<List<KeyInfo>> Combinations(List<KeyInfo> items, int size)
        {
            var idx = new int[size];
            for (int i = 0; i < size; i++) idx[i] = i;
            while (true)
            {
                var combo = new List<KeyInfo>(size);
                foreach (int i in idx) combo.Add(items[i]);
                yield return combo;
                int p = size - 1;
                while (p >= 0 && idx[p] == items.Count - size + p) p--;
                if (p < 0) yield break;
                idx[p]++;
                for (int q = p + 1; q < size; q++) idx[q] = idx[q - 1] + 1;
            }
        }

        static IEnumerable<List<KeyInfo>> SinglesAndPairs(List<KeyInfo> items)
        {
            foreach (var a in items) yield return new List<KeyInfo> { a };
            for (int i = 0; i < items.Count; i++)
                for (int j = i + 1; j < items.Count; j++)
                    yield return new List<KeyInfo> { items[i], items[j] };
        }

        // ── Triangle BVH ────────────────────────────────────────────────────────
        // Built once on the Basis, refitted for every shape tried: the shape keys move vertices by a
        // few centimetres, which loosens the boxes a little but never changes the answer.
        sealed class TriBvh
        {
            struct Node
            {
                public Vector3 min, max;
                public int left, right, start, count;   // leaf when count > 0
            }

            static readonly Vector3[] Rays =
            {
                Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
            };

            readonly int[]  _tri;
            readonly int[]  _order;
            readonly Node[] _nodes;
            readonly int[]  _stack = new int[128];
            readonly float  _winding;
            int       _nodeCount;
            Vector3[] _v;

            public TriBvh(int[] tri, Vector3[] v, float winding)
            {
                _tri = tri;
                _v = v;
                _winding = winding;
                int count = tri.Length / 3;
                _order = new int[count];
                for (int i = 0; i < count; i++) _order[i] = i;
                _nodes = new Node[Math.Max(1, 2 * count)];
                var centroids = new Vector3[count];
                for (int i = 0; i < count; i++)
                    centroids[i] = (v[tri[3 * i]] + v[tri[3 * i + 1]] + v[tri[3 * i + 2]]) / 3f;
                Build(0, count, centroids);
                Refit(v);
            }

            int Build(int start, int count, Vector3[] centroids)
            {
                int ni = _nodeCount++;
                if (count <= 4) { _nodes[ni] = new Node { left = -1, right = -1, start = start, count = count }; return ni; }
                Vector3 cmin = centroids[_order[start]], cmax = cmin;
                for (int i = start + 1; i < start + count; i++)
                {
                    cmin = Vector3.Min(cmin, centroids[_order[i]]);
                    cmax = Vector3.Max(cmax, centroids[_order[i]]);
                }
                Vector3 ext = cmax - cmin;
                int axis = ext.x >= ext.y ? (ext.x >= ext.z ? 0 : 2) : (ext.y >= ext.z ? 1 : 2);
                if (ext[axis] <= 0f) { _nodes[ni] = new Node { left = -1, right = -1, start = start, count = count }; return ni; }
                Array.Sort(_order, start, count,
                           Comparer<int>.Create((a, b) => centroids[a][axis].CompareTo(centroids[b][axis])));
                int half  = count / 2;
                int left  = Build(start, half, centroids);
                int right = Build(start + half, count - half, centroids);
                _nodes[ni] = new Node { left = left, right = right, start = start, count = 0 };
                return ni;
            }

            // Children always come after their parent in the array, so walking it backwards sees every
            // child before its parent.
            public void Refit(Vector3[] v)
            {
                _v = v;
                for (int ni = _nodeCount - 1; ni >= 0; ni--)
                {
                    ref Node nd = ref _nodes[ni];
                    if (nd.count > 0)
                    {
                        Vector3 mn = v[_tri[3 * _order[nd.start]]], mx = mn;
                        for (int k = nd.start; k < nd.start + nd.count; k++)
                            for (int c = 0; c < 3; c++)
                            {
                                var p = v[_tri[3 * _order[k] + c]];
                                mn = Vector3.Min(mn, p);
                                mx = Vector3.Max(mx, p);
                            }
                        nd.min = mn;
                        nd.max = mx;
                    }
                    else
                    {
                        nd.min = Vector3.Min(_nodes[nd.left].min, _nodes[nd.right].min);
                        nd.max = Vector3.Max(_nodes[nd.left].max, _nodes[nd.right].max);
                    }
                }
            }

            // Inside when at least 4 of the 6 axis rays first hit a surface from behind.
            public bool Inside(Vector3 p)
            {
                int votes = 0;
                foreach (var d in Rays)
                    if (FirstHitIsBackFace(p, d)) votes++;
                return votes >= 4;
            }

            bool FirstHitIsBackFace(Vector3 o, Vector3 d)
            {
                float best = float.PositiveInfinity;
                int bestTri = -1;
                int sp = 0;
                _stack[sp++] = 0;
                while (sp > 0)
                {
                    int ni = _stack[--sp];
                    ref Node nd = ref _nodes[ni];
                    if (!RayHitsBox(o, d, nd.min, nd.max, best)) continue;
                    if (nd.count > 0)
                    {
                        for (int k = nd.start; k < nd.start + nd.count; k++)
                            if (RayHitsTriangle(o, d, _order[k], out float t) && t < best) { best = t; bestTri = _order[k]; }
                    }
                    else
                    {
                        _stack[sp++] = nd.left;
                        _stack[sp++] = nd.right;
                    }
                }
                if (bestTri < 0) return false;
                Vector3 a = _v[_tri[3 * bestTri]], b = _v[_tri[3 * bestTri + 1]], c = _v[_tri[3 * bestTri + 2]];
                return Vector3.Dot(Vector3.Cross(b - a, c - a) * _winding, d) > 0f;
            }

            public float NearestDistance(Vector3 p)
            {
                float best2 = float.PositiveInfinity;
                int sp = 0;
                _stack[sp++] = 0;
                while (sp > 0)
                {
                    int ni = _stack[--sp];
                    ref Node nd = ref _nodes[ni];
                    if (BoxDistance2(p, nd.min, nd.max) >= best2) continue;
                    if (nd.count > 0)
                    {
                        for (int k = nd.start; k < nd.start + nd.count; k++)
                        {
                            int t = _order[k];
                            var q = ClosestPointOnTriangle(p, _v[_tri[3 * t]], _v[_tri[3 * t + 1]], _v[_tri[3 * t + 2]]);
                            float d2 = (q - p).sqrMagnitude;
                            if (d2 < best2) best2 = d2;
                        }
                    }
                    else
                    {
                        // Nearer child on top of the stack, so it tightens the bound first.
                        float dl = BoxDistance2(p, _nodes[nd.left].min, _nodes[nd.left].max);
                        float dr = BoxDistance2(p, _nodes[nd.right].min, _nodes[nd.right].max);
                        if (dl < dr) { _stack[sp++] = nd.right; _stack[sp++] = nd.left; }
                        else         { _stack[sp++] = nd.left;  _stack[sp++] = nd.right; }
                    }
                }
                return float.IsInfinity(best2) ? 0f : Mathf.Sqrt(best2);
            }

            static bool RayHitsBox(Vector3 o, Vector3 d, Vector3 mn, Vector3 mx, float tMax)
            {
                float t0 = 0f, t1 = tMax;
                for (int a = 0; a < 3; a++)
                {
                    float oa = o[a], da = d[a];
                    if (Mathf.Abs(da) < 1e-12f)
                    {
                        if (oa < mn[a] || oa > mx[a]) return false;
                        continue;
                    }
                    float inv = 1f / da;
                    float tn = (mn[a] - oa) * inv, tf = (mx[a] - oa) * inv;
                    if (tn > tf) { float s = tn; tn = tf; tf = s; }
                    if (tn > t0) t0 = tn;
                    if (tf < t1) t1 = tf;
                    if (t0 > t1) return false;
                }
                return true;
            }

            bool RayHitsTriangle(Vector3 o, Vector3 d, int t, out float dist)
            {
                dist = 0f;
                Vector3 v0 = _v[_tri[3 * t]], v1 = _v[_tri[3 * t + 1]], v2 = _v[_tri[3 * t + 2]];
                Vector3 e1 = v1 - v0, e2 = v2 - v0;
                Vector3 pv = Vector3.Cross(d, e2);
                float det = Vector3.Dot(e1, pv);
                if (Mathf.Abs(det) < 1e-20f) return false;
                float inv = 1f / det;
                Vector3 s = o - v0;
                float u = Vector3.Dot(s, pv) * inv;
                if (u < 0f || u > 1f) return false;
                Vector3 q = Vector3.Cross(s, e1);
                float w = Vector3.Dot(d, q) * inv;
                if (w < 0f || u + w > 1f) return false;
                dist = Vector3.Dot(e2, q) * inv;
                return dist > 0f;
            }

            static float BoxDistance2(Vector3 p, Vector3 mn, Vector3 mx)
            {
                float dx = Mathf.Max(0f, Mathf.Max(mn.x - p.x, p.x - mx.x));
                float dy = Mathf.Max(0f, Mathf.Max(mn.y - p.y, p.y - mx.y));
                float dz = Mathf.Max(0f, Mathf.Max(mn.z - p.z, p.z - mx.z));
                return dx * dx + dy * dy + dz * dz;
            }

            // Ericson, Real-Time Collision Detection, 5.1.5.
            static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 ab = b - a, ac = c - a, ap = p - a;
                float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
                if (d1 <= 0f && d2 <= 0f) return a;
                Vector3 bp = p - b;
                float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
                if (d3 >= 0f && d4 <= d3) return b;
                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
                Vector3 cp = p - c;
                float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
                if (d6 >= 0f && d5 <= d6) return c;
                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
                float va = d3 * d6 - d5 * d4;
                if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
                    return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
                float sum = va + vb + vc;
                if (Mathf.Abs(sum) < 1e-30f) return a;
                return a + ab * (vb / sum) + ac * (vc / sum);
            }
        }
    }
}
