using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// What AutoRig Feet did to an FBX and the avatars using it, kept next to that FBX's backups
    /// (`_Backups/&lt;fbx&gt;_arf_record.json`) so it travels with them and is never pruned like a run log.
    ///
    /// Two jobs:
    ///   • **Restore to original.** It knows which backup holds the FBX as it was before the very first run, which
    ///     objects each run added under the avatar, and which copies a Duplicate run made, so everything can be taken
    ///     back out again without guessing.
    ///   • **Interrupted runs.** Every run writes the stage it is in before it starts that stage. A run whose last
    ///     stage is not a finished one was cut off (Unity crashed or was closed, Blender was killed), and the panel
    ///     can say exactly how far it got.
    /// </summary>
    [Serializable]
    internal class ArfRecord
    {
        public int    v = 1;
        public string fbx;                 // project path of the FBX this record belongs to
        public string originalBackup;      // project path of the backup holding the FBX before the first run that changed it
        public string originalSha1;        // SHA1 of that original file
        public string originalWhen;        // yyyy-MM-dd HH:mm
        public List<OutfitOriginal> outfits = new List<OutfitOriginal>();
        public List<RunEntry> runs = new List<RunEntry>();

        [Serializable]
        internal class OutfitOriginal
        {
            public string fbx, backup, sha1;
        }

        [Serializable]
        internal class RunEntry
        {
            public string stamp;             // yyyyMMdd_HHmmss
            public string kind;              // "rig", "clean", "restore-original"
            public string mode;              // "Replace" / "Duplicate"
            public string swap;              // "Standard" / "Legacy"
            public string stage;             // see Stage
            public string scene;
            public string avatarPath, avatarId;      // the avatar the user ran on (hierarchy path, GlobalObjectId)
            public string resultPath, resultId;      // the avatar the run produced (the same one in Replace)
            public string exportFbx;         // the rigged FBX: the source in Replace, the _Rig_Feet file in Duplicate
            public string privateFbx;        // Duplicate / Standard: the copy the duplicate uses
            public string createdPrefab;     // Duplicate / Legacy: the prefab the swap built for the copy
            public List<string> toggleRows = new List<string>();      // "key=value" the Foot Shape toggle was planned with
            public List<string> shrinkKeys = new List<string>();      // the Foot Shrink keys planned
            public string tempExport;        // where Blender wrote before the file was put in place
            public int    blenderPid;
            public bool   fbxReplaced;       // the source FBX was overwritten by this run
            public List<string> added = new List<string>();          // "GlobalObjectId|relative path" of objects added under the avatar
            public List<string> outfitsReplaced = new List<string>(); // outfit FBXs overwritten by this run
            public List<string> repointFrom = new List<string>();     // Duplicate: outfit assets the copy was switched away from
            public List<string> repointTo = new List<string>();
            public string note;
        }

        internal static class Stage
        {
            public const string Started   = "started";
            public const string Blender   = "blender";
            public const string Outfits   = "outfits";
            public const string Swap      = "swap";
            public const string Scene     = "scene";
            public const string Done      = "done";
            public const string Failed    = "failed";
            public const string Cancelled = "cancelled";
            public const string Dismissed = "dismissed";

            public static bool IsFinished(string stage) =>
                stage == Done || stage == Failed || stage == Cancelled || stage == Dismissed;
        }

        internal RunEntry LastRun => runs.Count > 0 ? runs[runs.Count - 1] : null;

        // ─── Files ────────────────────────────────────────────────────────────

        internal static string PathFor(string fbxAssetPath)
        {
            string abs = ShuganSanitize.ToAbsolute(fbxAssetPath);
            if (string.IsNullOrEmpty(abs)) return null;
            return Path.Combine(Path.GetDirectoryName(abs), ShuganBackups.FolderName,
                                Path.GetFileNameWithoutExtension(abs) + "_arf_record.json");
        }

        internal static ArfRecord Load(string fbxAssetPath)
        {
            try
            {
                string p = PathFor(fbxAssetPath);
                if (p == null || !File.Exists(p)) return null;
                var r = JsonUtility.FromJson<ArfRecord>(File.ReadAllText(p));
                if (r == null) return null;
                r.fbx = fbxAssetPath;
                r.outfits = r.outfits ?? new List<OutfitOriginal>();
                r.runs = r.runs ?? new List<RunEntry>();
                return r;
            }
            catch (Exception e)
            {
                ShuganLog.Warn("AutoRig Feet", "The AutoRig Feet record of " + fbxAssetPath + " could not be read: " + e.Message);
                return null;
            }
        }

        internal static ArfRecord LoadOrCreate(string fbxAssetPath) =>
            Load(fbxAssetPath) ?? new ArfRecord { fbx = fbxAssetPath };

        /// <summary>Written straight to disk (never through the AssetDatabase): it has to survive a crash one line later.</summary>
        internal void Save()
        {
            try
            {
                string p = PathFor(fbx);
                if (p == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(this, true));
                if (File.Exists(p)) File.Replace(tmp, p, null);
                else File.Move(tmp, p);
            }
            catch (Exception e)
            {
                ShuganLog.Warn("AutoRig Feet", "The AutoRig Feet record of " + fbx + " could not be written: " + e.Message);
            }
        }

        /// <summary>Every record in the project (Duplicate copies point back at their source FBX's record).</summary>
        internal static List<ArfRecord> All()
        {
            var list = new List<ArfRecord>();
            try
            {
                foreach (string file in Directory.GetFiles(Application.dataPath, "*_arf_record.json", SearchOption.AllDirectories))
                {
                    string dir = Path.GetDirectoryName(file);
                    if (!string.Equals(Path.GetFileName(dir), ShuganBackups.FolderName, StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileName(file);
                    string fbxAbs = Path.Combine(Path.GetDirectoryName(dir), name.Substring(0, name.Length - "_arf_record.json".Length) + ".fbx");
                    var r = Load(ToProjectRelative(fbxAbs));
                    if (r != null) list.Add(r);
                }
            }
            catch (Exception e)
            {
                ShuganLog.Warn("AutoRig Feet", "Scanning AutoRig Feet records failed: " + e.Message);
            }
            return list;
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        internal static string Sha1(string absPath)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA1.Create())
                using (var fs = File.OpenRead(absPath))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch { return null; }
        }

        internal static string IdOf(GameObject go)
        {
            if (go == null) return "";
            try { return GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(); }
            catch { return ""; }
        }

        internal static GameObject Resolve(string id)
        {
            if (string.IsNullOrEmpty(id) || !GlobalObjectId.TryParse(id, out var gid)) return null;
            try { return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as GameObject; }
            catch { return null; }
        }

        internal static string RelativePath(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        internal static string ToProjectRelative(string abs)
        {
            string root = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
            string p = abs.Replace('\\', '/');
            return p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? p.Substring(root.Length).TrimStart('/') : abs;
        }
    }
}
