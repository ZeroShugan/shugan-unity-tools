using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// [ARF-BACKUP] Timestamped file backups kept in a `_Backups` folder NEXT TO the asset they
    /// protect, and the scan that finds them again later.
    ///
    /// Beside the asset, not in a central folder, on purpose: a backup is only useful if it travels
    /// with the thing it restores. Move an avatar folder to another project and its backups come
    /// along. (Diagnostics are the opposite case and live centrally — see ShuganRunLog.)
    ///
    /// **Nothing here ever deletes a backup** — there is deliberately no delete path in this class
    /// at all, not by age, not by count, not on request. A run log is regenerable and rotates; a
    /// backup is the only copy of a state the user may need back. The Backups tab reports what they
    /// cost on disk, and removing one is the user's own decision to make in Explorer.
    /// </summary>
    public static class ShuganBackups
    {
        public const string FolderName = "_Backups";

        public enum Kind { Fbx, Scene, RigJson, Other }

        /// <summary>One backup file on disk.</summary>
        public class Entry
        {
            public string   filePath;      // absolute
            public string   fileName;
            public DateTime when;
            public long     sizeBytes;
            public Kind     kind;
            /// <summary>
            /// True for the copy taken BEFORE the user's unsaved scene changes were written out.
            /// A run makes two scene backups when the scene is dirty: this one holds the file as it
            /// sat on disk, the other holds it after saving.
            /// </summary>
            public bool     isPreSave;
        }

        /// <summary>All backups belonging to one asset, newest first.</summary>
        public class AssetGroup
        {
            public string      assetPath;     // project-relative, e.g. "Assets/Booth/Airi/Airi.fbx"
            public string      assetName;
            public Kind        kind;
            public bool        assetExists;
            public List<Entry> entries = new List<Entry>();
            public long        totalBytes;
        }

        // "<name>_backup_<yyyyMMdd_HHmmss>[_presave].<ext>"
        static readonly Regex BackupName = new Regex(
            @"^(?<name>.+)_backup_(?<stamp>\d{8}_\d{6})(?<pre>_presave)?$",
            RegexOptions.Compiled);

        // "<name>_rigbackup_<yyyyMMdd_HHmmss>.json" — the rig-only backup the Blender script writes.
        static readonly Regex RigBackupName = new Regex(
            @"^(?<name>.+)_rigbackup_(?<stamp>\d{8}_\d{6})$", RegexOptions.Compiled);

        public static string Stamp() =>
            DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        // ─── Creating a backup ─────────────────────────────────────────────────

        /// <summary>
        /// Copy <paramref name="assetPath"/> into its sibling `_Backups` folder. Returns the backup's
        /// absolute path, or null when there was nothing to copy.
        /// </summary>
        /// <param name="suffix">e.g. "_presave"; appended after the timestamp.</param>
        public static string BackupAsset(string assetPath, string suffix = "", string stamp = null)
        {
            try
            {
                string abs = ShuganSanitize.ToAbsolute(assetPath);
                if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) return null;

                string dir = Path.Combine(Path.GetDirectoryName(abs), FolderName);
                Directory.CreateDirectory(dir);

                string name = Path.GetFileNameWithoutExtension(abs);
                string ext  = Path.GetExtension(abs);
                string dest = Path.Combine(dir,
                    name + "_backup_" + (stamp ?? Stamp()) + (suffix ?? "") + ext);

                if (File.Exists(dest)) return dest;      // same second, same suffix — already done
                File.Copy(abs, dest, overwrite: false);
                CopyMetaWithFreshGuid(abs, dest);
                return dest;
            }
            catch (Exception ex)
            {
                ShuganLog.Warn("Shugan", "Backup failed for " + assetPath + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Copy the asset's `.meta` so the backup keeps its import settings — but give it a FRESH
        /// guid. A verbatim copy duplicates the original's guid and Unity then logs
        /// "GUID [...] conflicts with '&lt;original&gt;' (current owner). Assigning a new guid."
        /// on every import, forever.
        /// </summary>
        public static void CopyMetaWithFreshGuid(string srcAbs, string destAbs)
        {
            try
            {
                string metaSrc = srcAbs + ".meta";
                if (!File.Exists(metaSrc)) return;
                string meta = File.ReadAllText(metaSrc);
                meta = Regex.Replace(meta, @"(?m)^guid:\s*[0-9a-fA-F]{32}\s*$",
                                     "guid: " + Guid.NewGuid().ToString("N"));
                File.WriteAllText(destAbs + ".meta", meta);
            }
            catch (Exception ex)
            {
                ShuganLog.Warn("Shugan", "Backup .meta not written: " + ex.Message);
            }
        }

        // ─── Restoring ─────────────────────────────────────────────────────────

        /// <summary>
        /// Put a backup back over its asset. The CURRENT file is backed up first — restoring is
        /// itself a destructive act, and someone who picks the wrong entry needs a way back.
        /// Returns the safety backup's path via <paramref name="safetyBackup"/>.
        /// </summary>
        public static bool Restore(Entry entry, string assetPath, out string safetyBackup, out string error)
        {
            safetyBackup = null;
            error        = null;
            try
            {
                string abs = ShuganSanitize.ToAbsolute(assetPath);
                if (string.IsNullOrEmpty(abs)) { error = "Could not resolve " + assetPath; return false; }
                if (entry == null || !File.Exists(entry.filePath))
                {
                    error = "That backup is no longer on disk.";
                    return false;
                }

                if (File.Exists(abs)) safetyBackup = BackupAsset(assetPath);

                File.Copy(entry.filePath, abs, overwrite: true);
                // The asset keeps its own .meta (and therefore its guid and every reference to it) —
                // replacing that too would break every prefab and scene pointing at this asset.
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ─── Scanning ──────────────────────────────────────────────────────────

        /// <summary>
        /// Every `_Backups` folder under Assets/, grouped by the asset each backup belongs to,
        /// newest first. Walks the whole project, so cache the result — callers should not run this
        /// from OnGUI.
        /// </summary>
        public static List<AssetGroup> Scan()
        {
            var groups = new Dictionary<string, AssetGroup>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string dir in Directory.GetDirectories(
                             Application.dataPath, FolderName, SearchOption.AllDirectories))
                {
                    foreach (string file in Directory.GetFiles(dir))
                    {
                        if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;

                        string bare = Path.GetFileNameWithoutExtension(file);
                        string ext  = Path.GetExtension(file);

                        string ownerName;
                        DateTime when;
                        bool isPreSave = false;
                        Kind kind;

                        var m = BackupName.Match(bare);
                        if (m.Success)
                        {
                            ownerName = m.Groups["name"].Value;
                            isPreSave = m.Groups["pre"].Success;
                            kind = ext.Equals(".fbx", StringComparison.OrdinalIgnoreCase) ? Kind.Fbx
                                 : ext.Equals(".unity", StringComparison.OrdinalIgnoreCase) ? Kind.Scene
                                 : Kind.Other;
                        }
                        else
                        {
                            var r = RigBackupName.Match(bare);
                            if (!r.Success) continue;          // not ours — leave it alone
                            ownerName = r.Groups["name"].Value;
                            kind      = Kind.RigJson;
                            m         = r;
                        }

                        if (!DateTime.TryParseExact(m.Groups["stamp"].Value, "yyyyMMdd_HHmmss",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out when))
                        {
                            try { when = File.GetLastWriteTime(file); } catch { }
                        }

                        // The asset sits one level up from its _Backups folder. For a rig JSON the
                        // owner is the FBX, not a .json.
                        string ownerDir  = Path.GetDirectoryName(dir);
                        string ownerExt  = kind == Kind.RigJson ? ".fbx" : ext;
                        string ownerAbs  = Path.Combine(ownerDir, ownerName + ownerExt);
                        string ownerRel  = ToProjectRelative(ownerAbs);

                        if (!groups.TryGetValue(ownerRel, out var g))
                        {
                            g = new AssetGroup
                            {
                                assetPath   = ownerRel,
                                assetName   = ownerName + ownerExt,
                                kind        = kind == Kind.RigJson ? Kind.Fbx : kind,
                                assetExists = File.Exists(ownerAbs),
                            };
                            groups[ownerRel] = g;
                        }

                        long size = 0;
                        try { size = new FileInfo(file).Length; } catch { }

                        g.entries.Add(new Entry
                        {
                            filePath  = file,
                            fileName  = Path.GetFileName(file),
                            when      = when,
                            sizeBytes = size,
                            kind      = kind,
                            isPreSave = isPreSave,
                        });
                        g.totalBytes += size;
                    }
                }
            }
            catch (Exception ex)
            {
                ShuganLog.Warn("Shugan", "Backup scan failed: " + ex.Message);
            }

            var list = new List<AssetGroup>(groups.Values);
            foreach (var g in list)
                g.entries.Sort((a, b) => b.when.CompareTo(a.when));      // newest first
            list.Sort((a, b) =>
            {
                int k = a.kind.CompareTo(b.kind);                        // FBX, then Scene, then rest
                return k != 0 ? k : string.Compare(a.assetName, b.assetName, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        static string ToProjectRelative(string abs)
        {
            try
            {
                string root = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
                string p    = abs.Replace('\\', '/');
                if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return p.Substring(root.Length).TrimStart('/');
                return abs;
            }
            catch { return abs; }
        }

        public static string FormatBytes(long b)
        {
            if (b <= 0) return "—";
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024f).ToString("0.#") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / (1024f * 1024f)).ToString("0.#") + " MB";
            return (b / (1024f * 1024f * 1024f)).ToString("0.##") + " GB";
        }
    }
}
