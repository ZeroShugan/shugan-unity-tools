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
    /// [ARF-LOG] One diagnostic folder per tool run: a streaming log plus whatever structured
    /// artifacts the tool writes beside it. Tool-agnostic — pass a tool name and get
    /// `Assets/! Shugan/!_Lab/Script/&lt;Tool&gt;_Logs/&lt;stamp&gt;_&lt;label&gt;/`.
    ///
    /// Three things here are deliberate:
    ///
    /// • The `_Logs` suffix is load-bearing. The repo's .gitignore already excludes
    ///   `/Assets/! Shugan/!_Lab/Script/*_Logs/`, so any tool adopting this class inherits the
    ///   ignore rule for free. A folder named "Logs" or "Diagnostics" would get committed.
    ///
    /// • Living under Assets/ (not Packages/) means the folder survives a VCC package update —
    ///   the same reason TextureOptimizer keeps its backups here.
    ///
    /// • The log STREAMS (AutoFlush) rather than buffering. The previous implementation held the
    ///   whole Blender console in a StringBuilder and wrote it once when the process exited, so a
    ///   Unity crash, a watchdog kill or a hung run lost the log at exactly the moment it mattered.
    ///
    /// Retention follows [SDT-LOGCAP] from the Blender addon standard — everything written on a
    /// customer machine must be bounded. Pruned on Begin(), oldest first.
    /// </summary>
    public class ShuganRunLog : IDisposable
    {
        public const string LabScriptRoot = "Assets/! Shugan/!_Lab/Script";

        public const int  DefaultKeepRuns     = 10;
        public const long DefaultMaxTotalBytes = 20L * 1024 * 1024;   // 20 MB

        // ─── Instance ──────────────────────────────────────────────────────────

        readonly object _lock = new object();
        StreamWriter    _writer;
        bool            _capturingConsole;
        bool            _inConsoleCallback;   // reentrancy guard (a write must never re-enter)

        /// <summary>Absolute path of this run's folder.</summary>
        public string FolderAbs { get; private set; }
        /// <summary>Project-relative path ("Assets/...") of this run's folder.</summary>
        public string FolderAssetPath { get; private set; }
        /// <summary>Absolute path of run.log.</summary>
        public string LogFileAbs { get; private set; }

        public bool IsOpen { get { lock (_lock) return _writer != null; } }

        ShuganRunLog() { }

        // ─── Creation ──────────────────────────────────────────────────────────

        /// <summary>
        /// Open a new run folder. Never throws — on any filesystem problem you get back a live
        /// object whose writes are no-ops, because losing a log must never fail a rig run.
        /// </summary>
        public static ShuganRunLog Begin(string toolName, string label,
                                         int keepRuns = DefaultKeepRuns,
                                         long maxTotalBytes = DefaultMaxTotalBytes)
        {
            var log = new ShuganRunLog();
            try
            {
                string rootAsset = ToolRootAssetPath(toolName);
                string rootAbs   = ShuganSanitize.ToAbsolute(rootAsset);
                Directory.CreateDirectory(rootAbs);

                // Prune BEFORE creating this run's folder, so the cap counts finished runs and the
                // new one is never a candidate for its own rotation.
                Prune(rootAbs, keepRuns - 1, maxTotalBytes);

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string name  = string.IsNullOrEmpty(label) ? stamp : stamp + "_" + SafeName(label);

                log.FolderAbs       = Path.Combine(rootAbs, name);
                log.FolderAssetPath = rootAsset + "/" + name;
                Directory.CreateDirectory(log.FolderAbs);

                log.LogFileAbs = Path.Combine(log.FolderAbs, "run.log");
                log._writer = new StreamWriter(log.LogFileAbs, append: false, encoding: new UTF8Encoding(false))
                {
                    AutoFlush = true   // survive a crash / kill mid-run
                };
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Shugan] Could not open a run log folder: " + ex.Message);
                log._writer = null;
            }
            return log;
        }

        /// <summary>
        /// `Assets/! Shugan/!_Lab/Script/&lt;Tool&gt;_Logs~` — note the TRAILING TILDE.
        ///
        /// Unity's asset pipeline ignores any folder whose name ends in `~`, and that is essential
        /// here, not cosmetic. Without it Unity imports `run.log` while it is still being written;
        /// the import emits console warnings; the console capture writes those warnings into
        /// `run.log`; its modification time changes; Unity re-imports... Unity detects this and
        /// reports "An infinite import loop has been detected", with a burst of
        /// "Build asset version error" warnings. Observed on the very first real run.
        ///
        /// The tilde also stops Unity generating a `.meta` for every log file (~50 of them for ten
        /// runs) and keeps this diagnostic output out of the Project window, where it is noise.
        /// The files are plainly visible in Explorer, which is how the tool's buttons open them.
        /// </summary>
        public static string ToolRootAssetPath(string toolName)
        {
            return LabScriptRoot + "/" + SafeName(toolName) + "_Logs~";
        }

        // ─── Writing ───────────────────────────────────────────────────────────

        /// <summary>
        /// Longest single line kept verbatim. Unity's FBX importer emits one console message that
        /// concatenates a sentence per blendshape with NO line breaks — on an avatar with 583
        /// blendshapes that is a single 73 KB line, repeated once per FBX import. Measured on a
        /// real run: four such lines were 295 000 characters, 55% of the whole log, crowding out
        /// everything useful and eating the bug-report budget. Nothing past the first few hundred
        /// characters of such a line carries information.
        ///
        /// Truncation happens only on the way to the FILE. Sentinel parsing reads the untouched
        /// line from the output queue, so `[SHUGAN_ISSUE]` / `[SHUGAN_REPORT]` cannot be affected.
        /// </summary>
        public const int MaxLineChars = 4000;

        /// <summary>Append one timestamped line. Thread-safe: the Blender output arrives on a
        /// background thread.</summary>
        public void Line(string text)
        {
            if (text == null) return;
            if (text.Length > MaxLineChars)
                text = text.Substring(0, MaxLineChars)
                     + "  […line truncated, " + text.Length + " chars total…]";
            lock (_lock)
            {
                if (_writer == null) return;
                try { _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text); }
                catch { /* disk full / folder deleted mid-run — keep the rig run alive */ }
            }
        }

        /// <summary>A visually obvious section break, so a customer's log is skimmable.</summary>
        public void Section(string title)
        {
            Line("");
            Line("======== " + title + " ========");
        }

        /// <summary>Write a sibling artifact (report.json, avatar.json, ...) into the run folder.</summary>
        public string WriteText(string fileName, string content)
        {
            if (string.IsNullOrEmpty(FolderAbs) || string.IsNullOrEmpty(fileName)) return null;
            try
            {
                string path = Path.Combine(FolderAbs, fileName);
                File.WriteAllText(path, content ?? "", new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex)
            {
                Line("[log] could not write " + fileName + ": " + ex.Message);
                return null;
            }
        }

        // ─── Unity Console capture ─────────────────────────────────────────────

        /// <summary>
        /// Fold Unity Console warnings/errors into the run log for the duration of the run. This is
        /// where an unforeseen Modular Avatar / VRCFury / SDK failure actually shows up — none of it
        /// was visible to us before, since only Blender's own stdout was ever captured.
        /// Info-level logs are skipped except our own, to keep the log about this run.
        /// </summary>
        public void BeginConsoleCapture()
        {
            if (_capturingConsole) return;
            _capturingConsole = true;
            Application.logMessageReceivedThreaded += OnConsoleMessage;
        }

        public void EndConsoleCapture()
        {
            if (!_capturingConsole) return;
            _capturingConsole = false;
            Application.logMessageReceivedThreaded -= OnConsoleMessage;
        }

        /// <summary>
        /// Info-level messages worth keeping. The first attempt matched on "Shugan", which our own
        /// messages do not contain — they are prefixed with the TOOL name ("[AutoRig Feet] …",
        /// "[Humanoid Rig Mapping] …"). The result was that every one of our own progress lines was
        /// dropped from the log while Unity's import warnings were kept. Matched case-insensitively
        /// on the bracketed prefix only.
        /// </summary>
        static readonly string[] OwnLogPrefixes =
        {
            "[AutoRig Feet", "[Humanoid Rig Mapping", "[Shugan", "[Texture Optimizer",
            "[FBX Swapper", "[Animation Clip Batch", "[DisplaceComponent",
        };

        void OnConsoleMessage(string condition, string stackTrace, LogType type)
        {
            if (_inConsoleCallback) return;   // our own failure path must not recurse
            bool interesting = type == LogType.Error || type == LogType.Exception ||
                               type == LogType.Assert || type == LogType.Warning ||
                               IsOwnMessage(condition);
            if (!interesting) return;

            _inConsoleCallback = true;
            try
            {
                Line("[console:" + type + "] " + condition);
                // Stack traces only for the genuinely broken cases — warnings would bury the log.
                if ((type == LogType.Exception || type == LogType.Error) && !string.IsNullOrEmpty(stackTrace))
                {
                    var lines = stackTrace.Split('\n');
                    int n = Math.Min(lines.Length, 12);
                    for (int i = 0; i < n; i++)
                        if (!string.IsNullOrEmpty(lines[i].Trim())) Line("    " + lines[i].TrimEnd());
                }
            }
            finally { _inConsoleCallback = false; }
        }

        static bool IsOwnMessage(string condition)
        {
            if (string.IsNullOrEmpty(condition)) return false;
            foreach (string p in OwnLogPrefixes)
                if (condition.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ─── Closing ───────────────────────────────────────────────────────────

        public void End()
        {
            EndConsoleCapture();
            lock (_lock)
            {
                if (_writer == null) return;
                try { _writer.Flush(); _writer.Dispose(); } catch { }
                _writer = null;
            }
        }

        public void Dispose() { End(); }

        // ─── Rotation ──────────────────────────────────────────────────────────

        /// <summary>
        /// [ARF-LOGCAP] Keep at most <paramref name="keepRuns"/> folders AND at most
        /// <paramref name="maxTotalBytes"/> in total, oldest deleted first. The size cap is a second
        /// guard: one pathological run can outweigh ten normal ones.
        /// </summary>
        public static void Prune(string rootAbs, int keepRuns, long maxTotalBytes)
        {
            try
            {
                if (!Directory.Exists(rootAbs)) return;
                var dirs = new List<string>(Directory.GetDirectories(rootAbs));
                // Folder names start with yyyyMMdd_HHmmss, so ordinal sort == chronological.
                dirs.Sort(StringComparer.Ordinal);

                while (dirs.Count > Math.Max(0, keepRuns))
                {
                    DeleteRunFolder(dirs[0]);
                    dirs.RemoveAt(0);
                }

                long total = 0;
                var sizes = new List<long>();
                foreach (string d in dirs) { long s = FolderSize(d); sizes.Add(s); total += s; }
                int idx = 0;
                while (total > maxTotalBytes && idx < dirs.Count - 1)   // never delete the newest
                {
                    DeleteRunFolder(dirs[idx]);
                    total -= sizes[idx];
                    idx++;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Shugan] Log rotation skipped: " + ex.Message);
            }
        }

        static void DeleteRunFolder(string dirAbs)
        {
            try { if (Directory.Exists(dirAbs)) Directory.Delete(dirAbs, recursive: true); } catch { }
            // Unity generates a sibling .meta for every folder under Assets/; orphaned .meta files
            // produce console warnings on the next refresh, so they go with it.
            try { if (File.Exists(dirAbs + ".meta")) File.Delete(dirAbs + ".meta"); } catch { }
        }

        static long FolderSize(string dirAbs)
        {
            long n = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dirAbs, "*", SearchOption.AllDirectories))
                    try { n += new FileInfo(f).Length; } catch { }
            }
            catch { }
            return n;
        }

        // ─── Listing (for the tool UI) ─────────────────────────────────────────

        public class RunFolder
        {
            public string   folderAbs;
            public string   folderName;      // "20260903_141233_Selestia"
            public string   label;           // "Selestia"
            public DateTime when;
            public string   status = "";     // "ok" | "warnings" | "fatal" | "" (no report)
            public long     sizeBytes;
            public bool     hasReport;
            public bool     hasAvatar;
        }

        /// <summary>Newest first. Reads only report.json's status field, never the whole bundle.</summary>
        public static List<RunFolder> ListRuns(string toolName)
        {
            var list = new List<RunFolder>();
            try
            {
                string rootAbs = ShuganSanitize.ToAbsolute(ToolRootAssetPath(toolName));
                if (string.IsNullOrEmpty(rootAbs) || !Directory.Exists(rootAbs)) return list;

                foreach (string d in Directory.GetDirectories(rootAbs))
                {
                    var rf = new RunFolder
                    {
                        folderAbs  = d,
                        folderName = Path.GetFileName(d),
                        sizeBytes  = FolderSize(d),
                    };

                    string n = rf.folderName;
                    if (n.Length >= 15 &&
                        DateTime.TryParseExact(n.Substring(0, 15), "yyyyMMdd_HHmmss",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    {
                        rf.when  = dt;
                        rf.label = n.Length > 16 ? n.Substring(16) : "";
                    }
                    else
                    {
                        try { rf.when = Directory.GetCreationTime(d); } catch { }
                        rf.label = n;
                    }

                    string report = Path.Combine(d, "report.json");
                    rf.hasReport = File.Exists(report);
                    rf.hasAvatar = File.Exists(Path.Combine(d, "avatar.json"));
                    if (rf.hasReport)
                    {
                        try
                        {
                            var dto = JsonUtility.FromJson<StatusOnlyDto>(File.ReadAllText(report));
                            if (dto != null && !string.IsNullOrEmpty(dto.status)) rf.status = dto.status;
                        }
                        catch { }
                    }
                    list.Add(rf);
                }
                list.Sort((a, b) => string.CompareOrdinal(b.folderName, a.folderName));
            }
            catch { }
            return list;
        }

        [Serializable] class StatusOnlyDto { public string status; }

        // ─── Helpers ───────────────────────────────────────────────────────────

        static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "run";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            string outp = sb.ToString().Trim('_', '.');
            if (outp.Length > 48) outp = outp.Substring(0, 48);
            return outp.Length == 0 ? "run" : outp;
        }
    }
}
