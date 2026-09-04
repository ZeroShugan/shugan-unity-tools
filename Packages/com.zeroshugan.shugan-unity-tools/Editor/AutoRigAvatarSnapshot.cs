using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// [ARF-SNAPSHOT] Describes the avatar the user actually ran the tool on, so a failure can be
    /// reproduced without ever receiving their avatar.
    ///
    /// COPYRIGHT / PRIVACY — the rule this whole class exists to enforce:
    ///
    ///   Everything here is METADATA. No asset file is ever opened for its contents, so mesh
    ///   geometry, bone weights, blendshape deltas and texture pixels cannot leak by construction —
    ///   there is no code path that reads them.
    ///
    ///   Names are split by whether they are OUR problem domain or the CREATOR's work:
    ///     • in the clear — bones, meshes, blendshapes, animation clips, controllers, component
    ///       types, shader names, package versions, the avatar's own FBX/prefab path. All of this
    ///       is what a feet-rigging bug is actually about.
    ///     • aliased — materials, textures, audio and anything unclassified, via
    ///       ShuganSanitize.DescribeAsset, which is an allowlist and therefore fails safe.
    ///
    ///   So a report can say "three meshes share one material, which uses two 2048x2048 textures"
    ///   without ever transmitting how a third-party creator named any of it.
    ///
    /// Everything is bounded (see the Max* constants): a customer avatar with 2000 transforms and
    /// 400 blendshapes must not produce a report the server rejects.
    /// </summary>
    public static class AutoRigAvatarSnapshot
    {
        const int MaxTransforms     = 1500;
        const int MaxBlendShapes    = 200;   // per mesh, and only for the meshes being rigged
        const int MaxDependencies   = 400;
        const int MaxComponentTypes = 120;

        /// <summary>Context the tool already knows and that the snapshot should not recompute.</summary>
        public class Extras
        {
            public string       bodyMeshName;
            public List<string> garmentMeshNames = new List<string>();
            /// <summary>Pre-formatted findings from the tool's own feet-shape-key detection.</summary>
            public List<string> shapeKeyFindings = new List<string>();
            /// <summary>Tool settings used for this run, in display order.</summary>
            public List<KeyValuePair<string, string>> settings = new List<KeyValuePair<string, string>>();
        }

        // ─── Entry point ───────────────────────────────────────────────────────

        public static string CaptureJson(GameObject avatar, string fbxAssetPath, Extras extras)
        {
            var j = new Json();
            extras = extras ?? new Extras();

            j.Obj();
            j.Prop("schema", 1);
            j.Prop("capturedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            j.Prop("note", "Metadata only. No geometry, textures or file contents. " +
                           "Material/texture names are aliased as Type#guid8.");

            try { WriteAvatar(j, avatar, fbxAssetPath, extras); }
            catch (Exception ex) { j.Prop("avatarError", ex.Message); }

            try { WriteSettings(j, extras); }
            catch (Exception ex) { j.Prop("settingsError", ex.Message); }

            try { WriteDependencies(j, avatar, fbxAssetPath); }
            catch (Exception ex) { j.Prop("dependencyError", ex.Message); }

            try { WritePackages(j); }
            catch (Exception ex) { j.Prop("packageError", ex.Message); }

            j.End();
            return j.ToString();
        }

        /// <summary>
        /// environment.json — versions and the exact settings a run used. Flat by design: it is the
        /// first thing read during triage, and it must stay readable at a glance. Written through
        /// the same JSON writer as the snapshot so escaping behaves identically.
        /// </summary>
        public static string CaptureEnvironmentJson(List<KeyValuePair<string, string>> entries)
        {
            var j = new Json();
            j.Obj();
            j.Prop("schema", 1);
            if (entries != null)
                foreach (var kv in entries) j.Prop(kv.Key, kv.Value ?? "");
            j.End();
            return j.ToString();
        }

        // ─── Avatar ────────────────────────────────────────────────────────────

        static void WriteAvatar(Json j, GameObject avatar, string fbxAssetPath, Extras extras)
        {
            j.Key("avatar"); j.Obj();

            if (avatar == null)
            {
                j.Prop("present", false);
                j.End();
                return;
            }

            j.Prop("present", true);
            j.Prop("name", avatar.name);
            j.Prop("activeInHierarchy", avatar.activeInHierarchy);
            j.Prop("scale", V3(avatar.transform.localScale));
            // The avatar's own source files are in the clear by design — naming a base is not
            // shipping it, and it is the single most useful field for reproducing a bug.
            j.Prop("sourceFbx", string.IsNullOrEmpty(fbxAssetPath) ? "(not detected)" : fbxAssetPath);

            string prefabPath = "";
            try { prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatar) ?? ""; } catch { }
            j.Prop("prefabAsset", string.IsNullOrEmpty(prefabPath) ? "(none — plain scene object)" : prefabPath);

            var transforms = avatar.GetComponentsInChildren<Transform>(true);
            j.Prop("transformCount", transforms.Length);

            WriteAnimator(j, avatar);
            WriteSkinnedMeshes(j, avatar, extras);
            WriteComponents(j, avatar);
            WriteShapeKeyFindings(j, extras);
            WriteHierarchy(j, avatar, transforms);

            j.End();
        }

        static void WriteAnimator(Json j, GameObject avatar)
        {
            j.Key("animator"); j.Obj();
            var anim = avatar.GetComponent<Animator>();
            if (anim == null) { j.Prop("present", false); j.End(); return; }

            j.Prop("present", true);
            j.Prop("isHuman", anim.isHuman);
            j.Prop("hasValidAvatar", anim.avatar != null && anim.avatar.isValid);
            j.Prop("avatarAsset", anim.avatar != null ? anim.avatar.name : "(none)");
            j.Prop("applyRootMotion", anim.applyRootMotion);
            j.Prop("controller", anim.runtimeAnimatorController != null
                ? anim.runtimeAnimatorController.name : "(none)");

            // The humanoid map is the single most valuable thing here: a wrong or missing Foot/Toes
            // slot is the root cause of a whole class of AutoRig failures.
            if (anim.isHuman)
            {
                j.Key("humanoidBones"); j.Obj();
                foreach (HumanBodyBones hb in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (hb == HumanBodyBones.LastBone) continue;
                    Transform t = null;
                    try { t = anim.GetBoneTransform(hb); } catch { }
                    if (t != null) j.Prop(hb.ToString(), t.name);
                }
                j.End();
            }
            j.End();
        }

        static void WriteSkinnedMeshes(Json j, GameObject avatar, Extras extras)
        {
            j.Key("skinnedMeshes"); j.Arr();
            var smrs = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in smrs)
            {
                if (smr == null) continue;
                j.Obj();
                j.Prop("object", smr.gameObject.name);
                j.Prop("enabled", smr.enabled && smr.gameObject.activeInHierarchy);
                j.Prop("rootBone", smr.rootBone != null ? smr.rootBone.name : "(none)");
                j.Prop("boneCount", smr.bones != null ? smr.bones.Length : 0);

                var mesh = smr.sharedMesh;
                if (mesh == null) { j.Prop("mesh", "(missing!)"); j.End(); continue; }

                j.Prop("mesh", mesh.name);
                j.Prop("vertexCount", mesh.vertexCount);
                j.Prop("subMeshCount", mesh.subMeshCount);
                j.Prop("boundsSize", V3(mesh.bounds.size));
                j.Prop("blendShapeCount", mesh.blendShapeCount);

                // Materials are the creator's work — alias them, but keep the shader name, which is
                // public (Poiyomi, lilToon) and occasionally explains a swap/import oddity.
                j.Key("materials"); j.Arr();
                var mats = smr.sharedMaterials;
                if (mats != null)
                    foreach (var m in mats)
                    {
                        if (m == null) { j.Val("(missing material)"); continue; }
                        string shader = m.shader != null ? m.shader.name : "(no shader)";
                        j.Val(ShuganSanitize.AliasObject(m) + " (shader: " + shader + ")");
                    }
                j.End();

                // Blendshape NAMES only for the meshes actually being rigged. For the rest the count
                // is enough, and dumping 400 expression names per mesh would bloat every report.
                bool rigged = IsRiggedMesh(smr, mesh, extras);
                if (rigged && mesh.blendShapeCount > 0)
                {
                    j.Key("blendShapes"); j.Arr();
                    int n = Math.Min(mesh.blendShapeCount, MaxBlendShapes);
                    for (int i = 0; i < n; i++)
                    {
                        float w = 0f;
                        try { w = smr.GetBlendShapeWeight(i); } catch { }
                        string nm = mesh.GetBlendShapeName(i);
                        j.Val(Math.Abs(w) > 0.0001f
                            ? nm + " = " + w.ToString("0.##", CultureInfo.InvariantCulture)
                            : nm);
                    }
                    j.End();
                    if (mesh.blendShapeCount > n)
                        j.Prop("blendShapesTruncated", mesh.blendShapeCount - n);
                }
                j.End();
            }
            j.End();
        }

        static bool IsRiggedMesh(SkinnedMeshRenderer smr, Mesh mesh, Extras extras)
        {
            string a = smr.gameObject.name, b = mesh != null ? mesh.name : "";
            if (Same(a, extras.bodyMeshName) || Same(b, extras.bodyMeshName)) return true;
            foreach (string g in extras.garmentMeshNames)
                if (Same(a, g) || Same(b, g)) return true;
            return false;
        }

        static bool Same(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
                   string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static void WriteComponents(Json j, GameObject avatar)
        {
            // Type names and counts only — never a component's serialized values, which could hold
            // anything. This is how we see PhysBones, constraints, VRCFury and Modular Avatar setups.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int missing = 0;
            foreach (var c in avatar.GetComponentsInChildren<Component>(true))
            {
                if (c == null) { missing++; continue; }   // a missing script is itself a finding
                string t = c.GetType().FullName ?? c.GetType().Name;
                counts.TryGetValue(t, out int n);
                counts[t] = n + 1;
            }

            var keys = new List<string>(counts.Keys);
            keys.Sort(StringComparer.Ordinal);

            j.Key("components"); j.Obj();
            int written = 0;
            foreach (string k in keys)
            {
                if (written++ >= MaxComponentTypes) break;
                j.Prop(k, counts[k]);
            }
            if (keys.Count > MaxComponentTypes) j.Prop("_truncatedTypes", keys.Count - MaxComponentTypes);
            if (missing > 0) j.Prop("_MISSING_SCRIPTS", missing);
            j.End();
        }

        static void WriteShapeKeyFindings(Json j, Extras extras)
        {
            j.Key("feetShapeKeyFindings"); j.Arr();
            foreach (string s in extras.shapeKeyFindings) j.Val(s);
            j.End();
        }

        static void WriteHierarchy(Json j, GameObject avatar, Transform[] transforms)
        {
            // Flat "depth|name" list: compact, and still shows the full bone structure, which is
            // what foot/toe detection actually operates on.
            j.Key("hierarchy"); j.Arr();
            int n = Math.Min(transforms.Length, MaxTransforms);
            for (int i = 0; i < n; i++)
            {
                var t = transforms[i];
                if (t == null) continue;
                int depth = 0;
                var p = t;
                while (p != null && p != avatar.transform && depth < 64) { p = p.parent; depth++; }
                j.Val(depth + "|" + t.name);
            }
            j.End();
            if (transforms.Length > n) j.Prop("hierarchyTruncated", transforms.Length - n);
        }

        // ─── Settings ──────────────────────────────────────────────────────────

        static void WriteSettings(Json j, Extras extras)
        {
            j.Key("toolSettings"); j.Obj();
            foreach (var kv in extras.settings) j.Prop(kv.Key, kv.Value ?? "");
            j.End();

            j.Key("garmentMeshes"); j.Arr();
            foreach (string g in extras.garmentMeshNames) j.Val(g);
            j.End();
        }

        // ─── Dependencies ──────────────────────────────────────────────────────

        static void WriteDependencies(Json j, GameObject avatar, string fbxAssetPath)
        {
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(fbxAssetPath)) roots.Add(fbxAssetPath);
            if (avatar != null)
            {
                string p = "";
                try { p = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatar) ?? ""; } catch { }
                if (!string.IsNullOrEmpty(p) && !roots.Contains(p)) roots.Add(p);
            }

            j.Key("dependencies"); j.Obj();
            j.Prop("roots", string.Join(", ", roots.ToArray()));

            if (roots.Count == 0) { j.Prop("note", "no source asset resolved"); j.End(); return; }

            string[] deps;
            try { deps = AssetDatabase.GetDependencies(roots.ToArray(), true); }
            catch (Exception ex) { j.Prop("error", ex.Message); j.End(); return; }

            j.Prop("count", deps.Length);
            j.Key("assets"); j.Arr();

            int written = 0;
            long redactedBytes = 0;
            int redactedCount = 0;
            foreach (string d in deps)
            {
                var r = ShuganSanitize.DescribeAsset(d);
                if (r.redacted) { redactedCount++; if (r.sizeBytes > 0) redactedBytes += r.sizeBytes; }
                if (written++ >= MaxDependencies) continue;

                j.Obj();
                j.Prop("asset", r.display);
                j.Prop("type", r.typeName);
                j.Prop("ext", r.extension);
                if (r.sizeBytes >= 0) j.Prop("bytes", r.sizeBytes);
                WriteTextureInfo(j, d, r.typeName);
                j.End();
            }
            j.End();

            if (deps.Length > MaxDependencies) j.Prop("truncated", deps.Length - MaxDependencies);
            j.Prop("redactedAssets", redactedCount);
            j.Prop("redactedBytes", redactedBytes);
            j.End();
        }

        static void WriteTextureInfo(Json j, string assetPath, string typeName)
        {
            // Dimensions and format come from the importer settings and the Texture object header —
            // useful for spotting an 8K texture set or a broken import. Pixels are never touched.
            if (typeName != "Texture2D" && typeName != "Cubemap" && typeName != "Texture") return;
            try
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
                if (tex != null) j.Prop("dims", tex.width + "x" + tex.height);
            }
            catch { }
        }

        // ─── Packages ──────────────────────────────────────────────────────────

        static void WritePackages(Json j)
        {
            // Package ids and versions are public, and a version mismatch (VRChat SDK, Modular
            // Avatar, VRCFury) is a common root cause, so these stay fully in the clear.
            j.Key("packages"); j.Obj();
            try
            {
                string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string pkgDir = Path.Combine(root, "Packages");
                if (!Directory.Exists(pkgDir)) { j.Prop("error", "no Packages folder"); j.End(); return; }

                var names = new List<string>(Directory.GetDirectories(pkgDir));
                names.Sort(StringComparer.Ordinal);
                foreach (string d in names)
                {
                    string manifest = Path.Combine(d, "package.json");
                    if (!File.Exists(manifest)) continue;
                    string id = Path.GetFileName(d), ver = "?";
                    try
                    {
                        string txt = File.ReadAllText(manifest);
                        var m = System.Text.RegularExpressions.Regex.Match(
                            txt, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                        if (m.Success) ver = m.Groups[1].Value;
                    }
                    catch { }
                    j.Prop(id, ver);
                }
            }
            catch (Exception ex) { j.Prop("error", ex.Message); }
            j.End();

            j.Key("unity"); j.Obj();
            j.Prop("version", Application.unityVersion);
            j.Prop("platform", Application.platform.ToString());
            j.End();
        }

        // ─── Small helpers ─────────────────────────────────────────────────────

        static string V3(Vector3 v)
        {
            return v.x.ToString("0.####", CultureInfo.InvariantCulture) + ", " +
                   v.y.ToString("0.####", CultureInfo.InvariantCulture) + ", " +
                   v.z.ToString("0.####", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Minimal hand-rolled JSON writer. JsonUtility cannot express dictionaries or the mixed
        /// nesting this document needs, and pulling in a JSON dependency for one diagnostic file
        /// is not worth it. Indented, because a human reads this in the report preview.
        /// </summary>
        class Json
        {
            readonly StringBuilder _sb = new StringBuilder(8192);
            readonly Stack<bool>   _isArray = new Stack<bool>();
            bool _needComma;
            bool _afterKey;   // a value follows a key on the SAME line, so no comma and no indent
            int  _indent;

            void Pad() { _sb.Append('\n').Append(' ', _indent * 2); }

            void Sep()
            {
                if (_afterKey) { _afterKey = false; return; }
                if (_needComma) _sb.Append(',');
                if (_sb.Length > 0) Pad();
                _needComma = true;
            }

            public void Obj()  { Sep(); _sb.Append('{'); _isArray.Push(false); _indent++; _needComma = false; }
            public void Arr()  { Sep(); _sb.Append('['); _isArray.Push(true);  _indent++; _needComma = false; }

            public void End()
            {
                if (_isArray.Count == 0) return;
                bool arr = _isArray.Pop();
                _indent--;
                if (_needComma) Pad();
                _sb.Append(arr ? ']' : '}');
                _needComma = true;
                _afterKey  = false;
            }

            public void Key(string k)
            {
                Sep();
                _sb.Append(Esc(k)).Append(": ");
                _afterKey  = true;
                _needComma = false;
            }

            void Raw(string literal) { _sb.Append(literal); _afterKey = false; _needComma = true; }

            public void Prop(string k, string v)  { Key(k); Raw(Esc(v)); }
            public void Prop(string k, int v)     { Key(k); Raw(v.ToString(CultureInfo.InvariantCulture)); }
            public void Prop(string k, long v)    { Key(k); Raw(v.ToString(CultureInfo.InvariantCulture)); }
            public void Prop(string k, bool v)    { Key(k); Raw(v ? "true" : "false"); }

            public void Val(string v) { Sep(); _sb.Append(Esc(v)); }

            static string Esc(string s)
            {
                if (s == null) return "null";
                var sb = new StringBuilder(s.Length + 2);
                sb.Append('"');
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"':  sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b");  break;
                        case '\f': sb.Append("\\f");  break;
                        case '\n': sb.Append("\\n");  break;
                        case '\r': sb.Append("\\r");  break;
                        case '\t': sb.Append("\\t");  break;
                        default:
                            // Control chars and anything above the BMP-safe range get escaped, so a
                            // Japanese mesh name survives but a stray control byte cannot break JSON.
                            if (c < 0x20 || c == 0x7f) sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
                return sb.ToString();
            }

            public override string ToString() { return _sb.ToString().TrimStart('\n'); }
        }
    }
}
