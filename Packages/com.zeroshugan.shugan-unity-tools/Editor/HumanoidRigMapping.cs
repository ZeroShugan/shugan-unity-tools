// ╔══════════════════════════════════════════════════════════════════════╗
// ║   SHUGAN'S HUMANOID RIG MAPPING                                       ║
// ║   Set an FBX avatar to Humanoid and correct Unity's auto-mapping,     ║
// ║   VRChat-flavoured. v1: foot + toes correction + Jaw removal.         ║
// ║   Exposes a static API so other tools can ensure feet/toes are mapped.║
// ╚══════════════════════════════════════════════════════════════════════╝

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    public class HumanoidRigMapping : EditorWindow
    {
        const string TOOL_NAME    = "Humanoid Rig Mapping";
        const string TOOL_VERSION = "1.0";
        const string WIKI_URL     = "https://www.notion.so/shugan/Humanoid-Rig-Mapping";

        // ── Keyword tables (ported from the AutoRig Feet detector) ───────────────
        static readonly string[] FOOT_PRIMARY   = { "foot", "feet", "ankle", "talus", "heel" };
        static readonly string[] FOOT_SECONDARY = { "sole", "ball", "pad", "plantar" };
        static readonly string[] TOE_PRIMARY    = { "toe", "toes", "digit", "phalange" };
        static readonly string[] TOE_SECONDARY  = { "tip", "knuckle" };
        static readonly string[] LEG_KEYWORDS   = { "leg", "thigh", "knee", "calf", "shin", "femur", "tibia" };
        static readonly string[] HIP_KEYWORDS   = { "hip", "pelvis", "root", "base" };

        // Scoring weights / penalties (mirrors the Python WEIGHTS/PENALTIES).
        const float W_NAME_PRIMARY = 10f, W_NAME_SECONDARY = 3f, W_SIDE = 3f, W_DEPTH = 5f,
                    W_INFLUENCE = 12f, W_LEAF = 2f, W_PARENT = 20f, W_LOW = 6f;
        const float P_LEG = 4f, P_ZERO_INFLUENCE = 1000f;
        const int   IDEAL_FOOT_DEPTH = 3, IDEAL_TOE_DEPTH = 4;
        const float MIN_INFLUENCE = 0.0001f;
        const float OVERRIDE_MIN_CONF = 11f;        // our pick must be at least this strong to replace Unity
        const float OVERRIDE_MARGIN   = 6f;         // …and beat Unity's bone by this much

        // Humanoid slot names this v1 manages.
        const string H_LFOOT = "LeftFoot", H_RFOOT = "RightFoot",
                     H_LTOES = "LeftToes", H_RTOES = "RightToes", H_JAW = "Jaw";

        /// <summary>Outcome of EnsureFeetAndToesMapped, for other tools to inspect.</summary>
        public struct MappingResult
        {
            public bool   avatarValid;       // resulting humanoid avatar is valid + human
            public int    slotsMapped;       // foot/toe slots this call set
            public bool   jawRemoved;
            public bool   feetToesComplete;  // all of LeftFoot/RightFoot/LeftToes/RightToes are mapped
            public string message;
        }

        // ── State (UI) ─────────────────────────────────────────────────────────────
        GameObject _target;
        string     _fbxPath;
        ModelImporterAnimationType _animType;
        Scorer     _scorer;
        string     _armatureName = "";

        bool _analyzed;
        bool _tableView;            // list (cards) vs table view — like the Texture Optimizer toggle
        readonly List<Slot> _slots = new List<Slot>();
        string _jawBone;

        string _status = "";
        MessageType _statusType = MessageType.None;
        Vector2 _scroll;

        class Slot { public string humanName, unityBone, ourBone, action; public float ourScore; }

        [MenuItem("Tools/Shugan/Humanoid Rig Mapping", false, 1950)]   // second category (priority gap)
        static void Open()
        {
            var win = GetWindow<HumanoidRigMapping>("Humanoid Rig Mapping");
            win.minSize = new Vector2(500, 460);
        }

        // ── GUI ──────────────────────────────────────────────────────────────────

        void OnGUI()
        {
            ShuganToolUI.DrawHeader("Humanoid Rig Mapping");
            ShuganToolUI.DrawSocialLinks(WIKI_URL);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            GUILayout.Label("Avatar", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Drop an FBX model, or an avatar root from the scene.", EditorStyles.miniLabel);
            EditorGUI.BeginChangeCheck();
            _target = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("FBX or Avatar Root", "The avatar FBX, or a scene root whose Animator/first armature points to the rig."),
                _target, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) ResolveTarget();

            if (_target != null && string.IsNullOrEmpty(_fbxPath))
                EditorGUILayout.HelpBox("Couldn't find a rig FBX for this object. Drop the FBX asset, or a scene avatar with an Animator/SkinnedMeshRenderer.", MessageType.Warning);

            if (!string.IsNullOrEmpty(_fbxPath))
            {
                EditorGUILayout.LabelField("Target FBX", System.IO.Path.GetFileName(_fbxPath), EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Animation Type", _animType.ToString(), EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Armature", string.IsNullOrEmpty(_armatureName) ? "—" : $"{_armatureName}  ({(_scorer != null ? _scorer.bones.Length : 0)} bones)", EditorStyles.miniLabel);

                EditorGUILayout.Space(4);
                if (GUILayout.Button("Analyze (preview mapping)", GUILayout.Height(26)))
                    Analyze();
            }

            if (_analyzed)
            {
                Separator();
                DrawPreview();
            }

            EditorGUILayout.EndScrollView();

            if (_analyzed)
            {
                EditorGUI.BeginDisabledGroup(_slots.All(s => s.ourBone == null) && _jawBone == null);
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
                if (GUILayout.Button("▶  Apply Humanoid Mapping", GUILayout.Height(32)))
                    Apply();
                GUI.backgroundColor = prev;
                EditorGUI.EndDisabledGroup();
            }

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, _statusType);

            ShuganToolUI.DrawCredits(TOOL_NAME, TOOL_VERSION);
        }

        void DrawPreview()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Foot / Toe Mapping (v1)", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_tableView ? "List View" : "Table View", EditorStyles.miniButton, GUILayout.Width(80)))
                _tableView = !_tableView;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                _animType == ModelImporterAnimationType.Human
                    ? "Comparing our detector against Unity's current humanoid map."
                    : "Not Humanoid yet — Unity's auto-map is computed on Apply; our picks fill any slot Unity leaves empty.",
                EditorStyles.miniLabel);

            if (_tableView) DrawPreviewTable();
            else            DrawPreviewCards();

            EditorGUILayout.Space(2);
            Color jc = GUI.color;
            GUI.color = _jawBone != null ? new Color(1f, 0.55f, 0.55f) : Color.gray;
            EditorGUILayout.LabelField(_jawBone != null
                ? $"Jaw → will be REMOVED (currently '{_jawBone}'); VRChat doesn't use it."
                : "Jaw → not mapped (nothing to remove).", EditorStyles.miniLabel);
            GUI.color = jc;
        }

        void DrawPreviewCards()
        {
            foreach (var s in _slots)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(s.humanName, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("  Unity:", UnityCell(s), EditorStyles.miniLabel);

                Color c = GUI.color;
                GUI.color = s.ourBone != null ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.7f, 0.4f);
                EditorGUILayout.LabelField("  Ours:", s.ourBone != null ? $"{s.ourBone}   (score {s.ourScore:0.0})" : "— no confident candidate", EditorStyles.miniLabel);
                GUI.color = c;

                EditorGUILayout.LabelField("  →", s.action, EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
            }
        }

        void DrawPreviewTable()
        {
            // Header
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label("Humanoid", EditorStyles.miniBoldLabel, GUILayout.Width(80));
            GUILayout.Label("Unity",    EditorStyles.miniBoldLabel, GUILayout.Width(130));
            GUILayout.Label("Ours",     EditorStyles.miniBoldLabel, GUILayout.Width(120));
            GUILayout.Label("Score",    EditorStyles.miniBoldLabel, GUILayout.Width(46));
            GUILayout.Label("Action",   EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();

            foreach (var s in _slots)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(s.humanName, EditorStyles.miniLabel, GUILayout.Width(80));
                GUILayout.Label(UnityCell(s), EditorStyles.miniLabel, GUILayout.Width(130));

                Color c = GUI.color;
                GUI.color = s.ourBone != null ? new Color(0.6f, 1f, 0.6f) : new Color(0.7f, 0.7f, 0.7f);
                GUILayout.Label(s.ourBone ?? "—", EditorStyles.miniLabel, GUILayout.Width(120));
                GUILayout.Label(s.ourBone != null ? s.ourScore.ToString("0.0") : "", EditorStyles.miniLabel, GUILayout.Width(46));
                GUI.color = c;

                bool useOurs = s.action.StartsWith("USE OURS");
                if (useOurs) GUI.color = new Color(0.6f, 1f, 0.6f);
                GUILayout.Label(s.action, EditorStyles.miniLabel);
                GUI.color = c;
                EditorGUILayout.EndHorizontal();
            }

            // Jaw row
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Jaw", EditorStyles.miniLabel, GUILayout.Width(80));
            GUILayout.Label(_jawBone ?? "—", EditorStyles.miniLabel, GUILayout.Width(130));
            GUILayout.Label("—", EditorStyles.miniLabel, GUILayout.Width(120));
            GUILayout.Label("", EditorStyles.miniLabel, GUILayout.Width(46));
            Color jc = GUI.color;
            if (_jawBone != null) GUI.color = new Color(1f, 0.55f, 0.55f);
            GUILayout.Label(_jawBone != null ? "REMOVE (VRChat)" : "—", EditorStyles.miniLabel);
            GUI.color = jc;
            EditorGUILayout.EndHorizontal();
        }

        string UnityCell(Slot s)
            => string.IsNullOrEmpty(s.unityBone)
                ? (_animType == ModelImporterAnimationType.Human ? "— (empty)" : "(auto on apply)")
                : s.unityBone;

        // ── Resolve target → FBX ───────────────────────────────────────────────────

        void ResolveTarget()
        {
            _analyzed = false;
            _slots.Clear();
            _jawBone = null;
            _scorer = null;
            _armatureName = "";
            _status = "";
            _fbxPath = "";

            if (_target == null) return;

            string directPath = AssetDatabase.GetAssetPath(_target);
            if (IsModelPath(directPath)) _fbxPath = directPath;

            if (string.IsNullOrEmpty(_fbxPath))
            {
                var animator = _target.GetComponentInChildren<Animator>(true);
                if (animator != null && animator.avatar != null)
                {
                    string p = AssetDatabase.GetAssetPath(animator.avatar);
                    if (IsModelPath(p)) _fbxPath = p;
                }
            }
            if (string.IsNullOrEmpty(_fbxPath))
            {
                var smr = _target.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (smr != null && smr.sharedMesh != null)
                {
                    string p = AssetDatabase.GetAssetPath(smr.sharedMesh);
                    if (IsModelPath(p)) _fbxPath = p;
                }
            }
            if (string.IsNullOrEmpty(_fbxPath)) return;

            var importer = AssetImporter.GetAtPath(_fbxPath) as ModelImporter;
            _animType = importer != null ? importer.animationType : ModelImporterAnimationType.None;
            _scorer = Scorer.FromFbx(_fbxPath);
            _armatureName = _scorer.armatureName;
        }

        void Analyze()
        {
            ResolveTarget();
            if (_scorer == null || _scorer.bones.Length == 0) { SetStatus("Could not read the FBX hierarchy.", MessageType.Error); return; }

            Transform lFoot = _scorer.BestFoot("L");
            Transform rFoot = _scorer.BestFoot("R");
            Transform lToes = _scorer.BestToe(lFoot, "L");
            Transform rToes = _scorer.BestToe(rFoot, "R");

            var unityMap = ReadUnityMap();

            _slots.Clear();
            _slots.Add(MakeSlot(H_LFOOT, unityMap, lFoot, isToe: false));
            _slots.Add(MakeSlot(H_RFOOT, unityMap, rFoot, isToe: false));
            _slots.Add(MakeSlot(H_LTOES, unityMap, lToes, isToe: true,  foot: lFoot));
            _slots.Add(MakeSlot(H_RTOES, unityMap, rToes, isToe: true,  foot: rFoot));

            _jawBone = unityMap != null && unityMap.TryGetValue(H_JAW, out string jb) && !string.IsNullOrEmpty(jb) ? jb : null;

            _analyzed = true;
            int picks = _slots.Count(s => s.ourBone != null);
            SetStatus($"Analyzed '{System.IO.Path.GetFileNameWithoutExtension(_fbxPath)}'. Our detector found {picks}/4 foot-region bones." +
                      (_jawBone != null ? "  Jaw will be removed on apply." : ""), MessageType.Info);
        }

        Slot MakeSlot(string human, Dictionary<string, string> unityMap, Transform ours, bool isToe, Transform foot = null)
        {
            string unity = unityMap != null && unityMap.TryGetValue(human, out string u) ? u : "";
            float ourScore = ours != null
                ? (isToe ? _scorer.ScoreToe(ours, foot) : _scorer.ScoreFoot(ours, SideOf(ours)))
                : 0f;

            string action;
            if (ours == null)
                action = string.IsNullOrEmpty(unity) ? "leave empty (no candidate)" : "keep Unity (no candidate)";
            else if (_animType != ModelImporterAnimationType.Human)
                action = "fill if Unity empty, else keep";
            else if (string.IsNullOrEmpty(unity))
                action = "USE OURS (Unity empty)";
            else
            {
                Transform unityT = _scorer.FindBone(unity);
                float unityScore = unityT != null ? (isToe ? _scorer.ScoreToe(unityT, foot) : _scorer.ScoreFoot(unityT, SideOf(unityT))) : -999f;
                bool over = ours != unityT && ourScore >= OVERRIDE_MIN_CONF && ourScore >= unityScore + OVERRIDE_MARGIN;
                action = over ? "USE OURS (Unity low-confidence)" : "keep Unity";
            }
            return new Slot { humanName = human, unityBone = unity, ourBone = ours != null ? ours.name : null, ourScore = ourScore, action = action };
        }

        Dictionary<string, string> ReadUnityMap()
        {
            var importer = AssetImporter.GetAtPath(_fbxPath) as ModelImporter;
            if (importer == null || importer.animationType != ModelImporterAnimationType.Human) return null;
            var human = importer.humanDescription.human;
            if (human == null) return null;
            var map = new Dictionary<string, string>();
            foreach (var h in human)
                if (!string.IsNullOrEmpty(h.humanName)) map[h.humanName] = h.boneName;
            return map;
        }

        void Apply()
        {
            if (string.IsNullOrEmpty(_fbxPath)) return;
            if (!EditorUtility.DisplayDialog("Apply Humanoid Mapping?",
                $"This sets '{System.IO.Path.GetFileName(_fbxPath)}' to Humanoid and reimports it, correcting the foot/toe mapping and removing the Jaw.\n\n" +
                "The FBX importer settings are modified (reversible via the model's Rig tab).",
                "Apply", "Cancel"))
            { SetStatus("Cancelled.", MessageType.None); return; }

            // Manual tool = full policy: replace low-confidence picks + strip Jaw.
            var r = EnsureFeetAndToesMapped(_fbxPath, replaceLowConfidence: true, removeJaw: true);
            Analyze();
            SetStatus(r.message + (r.avatarValid ? "" : "  Open Rig → Configure to finish remaining bones."),
                r.avatarValid ? MessageType.Info : MessageType.Warning);
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  PUBLIC STATIC API — other tools (e.g. AutoRig Feet Distributor) call this.
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Ensure LeftFoot/RightFoot/LeftToes/RightToes are mapped on the FBX's humanoid avatar.
        /// Sets the importer to Humanoid if needed, fills any foot/toe slot Unity left empty using our
        /// scorer, and (only if replaceLowConfidence) replaces Unity's mapped-but-weak picks. Never
        /// touches non-foot/toe bones. Optionally strips the Jaw. Reimports and reports the result.
        /// </summary>
        public static MappingResult EnsureFeetAndToesMapped(string fbxPath, bool replaceLowConfidence = false, bool removeJaw = false)
        {
            var result = new MappingResult();
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null) { result.message = "Not a model importer: " + fbxPath; return result; }

            if (importer.animationType != ModelImporterAnimationType.Human)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.SaveAndReimport();
                importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            }

            var scorer = Scorer.FromFbx(fbxPath);
            var picks = new Dictionary<string, Transform>
            {
                { H_LFOOT, scorer.BestFoot("L") }, { H_RFOOT, scorer.BestFoot("R") },
            };
            picks[H_LTOES] = scorer.BestToe(picks[H_LFOOT], "L");
            picks[H_RTOES] = scorer.BestToe(picks[H_RFOOT], "R");

            var desc  = importer.humanDescription;
            var human = desc.human != null ? desc.human.ToList() : new List<HumanBone>();

            int changed = 0;
            foreach (var kv in picks)
            {
                if (kv.Value == null) continue;
                int idx = human.FindIndex(h => h.humanName == kv.Key);
                string unityBone = idx >= 0 ? human[idx].boneName : "";

                bool doOverride;
                if (string.IsNullOrEmpty(unityBone)) doOverride = true;           // fill empty
                else if (!replaceLowConfidence)      doOverride = false;           // keep already-mapped
                else
                {
                    Transform unityT = scorer.FindBone(unityBone);
                    bool isToe = kv.Key == H_LTOES || kv.Key == H_RTOES;
                    Transform foot = kv.Key == H_LTOES ? picks[H_LFOOT] : kv.Key == H_RTOES ? picks[H_RFOOT] : null;
                    float ourScore   = isToe ? scorer.ScoreToe(kv.Value, foot) : scorer.ScoreFoot(kv.Value, SideOf(kv.Value));
                    float unityScore = unityT != null ? (isToe ? scorer.ScoreToe(unityT, foot) : scorer.ScoreFoot(unityT, SideOf(unityT))) : -999f;
                    doOverride = kv.Value != unityT && ourScore >= OVERRIDE_MIN_CONF && ourScore >= unityScore + OVERRIDE_MARGIN;
                }
                if (!doOverride) continue;

                if (idx >= 0) { var hb = human[idx]; hb.boneName = kv.Value.name; human[idx] = hb; }
                else          human.Add(NewHumanBone(kv.Key, kv.Value.name));
                changed++;
            }

            if (removeJaw)
            {
                int jawIdx = human.FindIndex(h => h.humanName == H_JAW);
                result.jawRemoved = jawIdx >= 0 && !string.IsNullOrEmpty(human[jawIdx].boneName);
                human.RemoveAll(h => h.humanName == H_JAW);
            }

            desc.human = human.ToArray();
            importer.humanDescription = desc;
            importer.SaveAndReimport();

            var avatar = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Avatar>().FirstOrDefault();
            result.avatarValid = avatar != null && avatar.isValid && avatar.isHuman;
            result.slotsMapped = changed;

            var finalMap = human.GroupBy(h => h.humanName).ToDictionary(g => g.Key, g => g.First().boneName);
            result.feetToesComplete = new[] { H_LFOOT, H_RFOOT, H_LTOES, H_RTOES }
                .All(k => finalMap.TryGetValue(k, out var b) && !string.IsNullOrEmpty(b));

            result.message = $"Humanoid map: {changed} foot/toe slot(s) set" +
                             (result.jawRemoved ? ", Jaw removed" : "") +
                             $"; avatar {(result.avatarValid ? "valid ✓" : "INVALID")}.";
            return result;
        }

        /// <summary>True if the FBX's avatar already has all foot + toe humanoid slots mapped.</summary>
        public static bool AreFeetAndToesMapped(string fbxPath)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null || importer.animationType != ModelImporterAnimationType.Human) return false;
            var human = importer.humanDescription.human;
            if (human == null) return false;
            var map = human.Where(h => !string.IsNullOrEmpty(h.humanName))
                           .GroupBy(h => h.humanName).ToDictionary(g => g.Key, g => g.First().boneName);
            return new[] { H_LFOOT, H_RFOOT, H_LTOES, H_RTOES }
                .All(k => map.TryGetValue(k, out var b) && !string.IsNullOrEmpty(b));
        }

        // Build a new optional HumanBone. Older Unity exposes HumanLimit.useDefault; newer versions
        // apply defaults automatically and dropped the field — set it via reflection only if present.
        static HumanBone NewHumanBone(string humanName, string boneName)
        {
            var hb = new HumanBone { humanName = humanName, boneName = boneName };
            FieldInfo f = typeof(HumanLimit).GetField("useDefault");
            if (f != null) { object boxed = hb.limit; f.SetValue(boxed, true); hb.limit = (HumanLimit)boxed; }
            return hb;
        }

        // ── Scorer: bone hierarchy + skin influence context + the scoring methods ───

        class Scorer
        {
            public Transform[] bones = new Transform[0];
            public readonly Dictionary<Transform, float> influence = new Dictionary<Transform, float>();
            public float minY, maxY, maxInf;
            public string armatureName = "";

            public static Scorer FromFbx(string fbxPath)
            {
                var s = new Scorer();
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                if (root == null) return s;
                s.bones = root.GetComponentsInChildren<Transform>(true);
                var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var rootBone = smrs.Select(x => x.rootBone).FirstOrDefault(b => b != null);
                s.armatureName = rootBone != null ? rootBone.name : (s.bones.Length > 1 ? s.bones[1].name : root.name);
                s.ComputeInfluence(smrs);
                if (s.bones.Length > 0) { s.minY = s.bones.Min(b => b.position.y); s.maxY = s.bones.Max(b => b.position.y); }
                s.maxInf = s.influence.Count > 0 ? s.influence.Values.Max() : 0f;
                return s;
            }

            void ComputeInfluence(SkinnedMeshRenderer[] smrs)
            {
                foreach (var smr in smrs)
                {
                    if (smr.sharedMesh == null || smr.bones == null) continue;
                    BoneWeight[] bw;
                    try { bw = smr.sharedMesh.boneWeights; } catch { bw = null; }
                    if (bw == null || bw.Length == 0) continue;
                    var b = smr.bones;
                    foreach (var w in bw)
                    {
                        Add(b, w.boneIndex0, w.weight0); Add(b, w.boneIndex1, w.weight1);
                        Add(b, w.boneIndex2, w.weight2); Add(b, w.boneIndex3, w.weight3);
                    }
                }
                void Add(Transform[] b, int idx, float weight)
                {
                    if (weight <= 0f || idx < 0 || idx >= b.Length || b[idx] == null) return;
                    influence.TryGetValue(b[idx], out float cur);
                    influence[b[idx]] = cur + weight;
                }
            }

            float Influence(Transform t) { influence.TryGetValue(t, out float v); return v; }
            float Lowness(Transform t) => maxY <= minY ? 0f : Mathf.Clamp01(1f - (t.position.y - minY) / (maxY - minY));

            public Transform FindBone(string name) => bones.FirstOrDefault(b => b != null && b.name == name);

            public float ScoreFoot(Transform t, string side)
            {
                string n = t.name.ToLowerInvariant();
                float score = 0f;
                if (HasAny(n, FOOT_PRIMARY))   score += W_NAME_PRIMARY;
                if (HasAny(n, FOOT_SECONDARY)) score += W_NAME_SECONDARY;
                if (!string.IsNullOrEmpty(side)) score += W_SIDE;
                score += Mathf.Max(0, 5 - Mathf.Abs(DepthFromHip(t) - IDEAL_FOOT_DEPTH)) / 5f * W_DEPTH;
                if (maxInf > 0) score += Influence(t) / maxInf * W_INFLUENCE;
                score += Lowness(t) * W_LOW;
                if (HasAny(n, LEG_KEYWORDS)) score -= P_LEG;
                if (Influence(t) < MIN_INFLUENCE && maxInf > 0) score -= P_ZERO_INFLUENCE;
                return score;
            }

            public float ScoreToe(Transform t, Transform foot)
            {
                if (t == null) return -1000f;
                string n = t.name.ToLowerInvariant();
                if (maxInf > 0 && Influence(t) < MIN_INFLUENCE) return -1000f;
                float score = 0f;
                if (HasAny(n, TOE_PRIMARY))   score += W_NAME_PRIMARY;
                if (HasAny(n, TOE_SECONDARY)) score += W_NAME_SECONDARY;
                if (HasAny(n, FOOT_PRIMARY))  score += W_NAME_PRIMARY * 0.6f;
                if (!string.IsNullOrEmpty(SideOf(t))) score += W_SIDE;
                score += Mathf.Max(0, 5 - Mathf.Abs(DepthFromHip(t) - IDEAL_TOE_DEPTH)) / 5f * W_DEPTH;
                if (t.childCount == 0) score += W_LEAF;
                if (maxInf > 0) score += Influence(t) / maxInf * W_INFLUENCE;
                if (foot != null && t.parent == foot) score += W_PARENT;
                return score;
            }

            public Transform BestFoot(string side)
            {
                Transform best = null; float bestScore = 1f;
                foreach (var t in bones)
                {
                    if (SideOf(t) != side) continue;
                    string n = t.name.ToLowerInvariant();
                    if (!HasAny(n, FOOT_PRIMARY) && !HasAny(n, FOOT_SECONDARY)) continue;
                    float sc = ScoreFoot(t, side);
                    if (sc > bestScore) { bestScore = sc; best = t; }
                }
                return best;
            }

            public Transform BestToe(Transform foot, string side)
            {
                if (foot == null) return null;
                Transform best = null; float bestScore = 1f;
                foreach (var t in bones)
                {
                    if (SideOf(t) != side) continue;
                    if (!IsDescendantOf(t, foot)) continue;
                    string n = t.name.ToLowerInvariant();
                    if (!HasAny(n, TOE_PRIMARY) && !HasAny(n, TOE_SECONDARY)) continue;
                    float sc = ScoreToe(t, foot);
                    if (sc > bestScore) { bestScore = sc; best = t; }
                }
                return best;
            }
        }

        // ── Static bone helpers (no context) ─────────────────────────────────────

        static int DepthFromHip(Transform t)
        {
            int depth = 0; var p = t.parent;
            while (p != null && depth < 64)
            {
                if (HasAny(p.name.ToLowerInvariant(), HIP_KEYWORDS)) return depth;
                depth++; p = p.parent;
            }
            return depth;
        }

        static bool IsDescendantOf(Transform t, Transform ancestor)
        {
            var p = t.parent;
            while (p != null) { if (p == ancestor) return true; p = p.parent; }
            return false;
        }

        static bool HasAny(string lowerName, string[] keys)
        {
            foreach (var k in keys) if (lowerName.Contains(k)) return true;
            return false;
        }

        // Detect L/R from a bone name; tokenises on separators so "Foot.L"/"Foot_L"/"LeftFoot" resolve
        // without over-matching names that merely end in 'l'/'r' (e.g. "Skull", "Hair").
        static string SideOf(Transform t)
        {
            string n = " " + t.name.ToLowerInvariant().Replace("_", " ").Replace(".", " ").Replace("-", " ") + " ";
            if (n.Contains(" left ")  || n.Contains(" l ")  || n.Contains(" lft ")) return "L";
            if (n.Contains(" right ") || n.Contains(" r ")  || n.Contains(" rgt ")) return "R";
            return null;
        }

        static bool IsModelPath(string path)
            => !string.IsNullOrEmpty(path) && (AssetImporter.GetAtPath(path) is ModelImporter);

        void SetStatus(string msg, MessageType type) { _status = msg; _statusType = type; }

        void Separator()
        {
            EditorGUILayout.Space(6);
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 1), new Color(0.5f, 0.5f, 0.5f, 0.3f));
            EditorGUILayout.Space(6);
        }
    }
}
