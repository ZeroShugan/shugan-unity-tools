using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShuganTools
{
    /// <summary>
    /// One typed issue reported by the AutoRig Feet Blender pipeline (or synthesized by the
    /// Unity side). Parsed from stdout sentinel lines:
    ///   [SHUGAN_ISSUE]  {json}   — one line per issue, emitted the moment it is detected
    ///   [SHUGAN_REPORT] {json}   — one final summary (status + all issues)
    /// Field names match the JSON keys (JsonUtility).
    /// </summary>
    [Serializable]
    public class RunIssue
    {
        public int    v;
        public string code;
        public string severity;   // "fatal" | "warning" | "info"
        public string message;
        public string hint;

        /// <summary>
        /// The verbatim sentinel JSON this issue was parsed from.
        ///
        /// Python attaches a `data` object to several issues — most importantly the 1500-character
        /// traceback on PY_EXCEPTION, and the ranked candidate lists on BONE_CANDIDATES. JsonUtility
        /// cannot deserialize an arbitrary object, and silently drops any field with no matching
        /// member, so all of that used to vanish the moment it reached C#: the report panel showed
        /// "unhandled exception" with no traceback, and the traceback survived only in the raw
        /// console log. Keeping the original line costs nothing and couples us to no schema, so a
        /// future python-side `data` key needs no change here.
        /// </summary>
        public string rawJson = "";

        public bool IsFatal   => severity == "fatal";
        public bool IsWarning => severity == "warning";
        public bool IsInfo    => severity == "info" || string.IsNullOrEmpty(severity);
    }

    /// <summary>
    /// Collected result of one Blender run. Serializable so the tool window can persist it
    /// across domain reloads (and to a small JSON next to the run log).
    /// </summary>
    [Serializable]
    public class RunReport
    {
        public string         status = "";       // "ok" | "warnings" | "fatal" ("" = nothing parsed yet)
        public List<RunIssue> issues = new List<RunIssue>();
        public bool           receivedFinal;     // true once a [SHUGAN_REPORT] line was seen
        public int            exitCode = int.MinValue; // Blender process exit code (Unity fills this)
        public string         logPath = "";      // per-run log file (Unity fills this)
        public long           timestampTicks;    // DateTime.UtcNow.Ticks at evaluation (Unity fills this)

        /// <summary>
        /// The verbatim [SHUGAN_REPORT] line. Preserves python's `stats` object (bones_created,
        /// garments, and whatever gets added later), which FinalReportDto cannot express and so
        /// used to be parsed away entirely. Keeping the raw line means the run folder's report.json
        /// carries the complete python-side result, not just the part C# happens to model.
        /// </summary>
        public string finalRawJson = "";

        /// <summary>Context the Unity side fills in so report.json stands alone in a bug report.</summary>
        public string runFolder = "";
        public string toolVersion = "";
        public string scriptVersion = "";

        public bool HasFatal
        {
            get
            {
                if (status == "fatal") return true;
                foreach (var i in issues) if (i.IsFatal) return true;
                return false;
            }
        }

        public bool HasWarnings
        {
            get { foreach (var i in issues) if (i.IsWarning) return true; return false; }
        }

        public RunIssue FirstFatal
        {
            get { foreach (var i in issues) if (i.IsFatal) return i; return null; }
        }

        /// <summary>Synthesize a Unity-side issue (U_* codes) into the report.</summary>
        public RunIssue AddIssue(string code, string severity, string message, string hint = null)
        {
            var issue = new RunIssue { v = 1, code = code, severity = severity, message = message, hint = hint };
            issues.Add(issue);
            if (severity == "fatal") status = "fatal";
            else if (severity == "warning" && status != "fatal" && status != "") status = "warnings";
            return issue;
        }

        const string IssuePrefix  = "[SHUGAN_ISSUE] ";
        const string ReportPrefix = "[SHUGAN_REPORT] ";

        /// <summary>
        /// Feed one Blender stdout/stderr line. Returns true when the line was a sentinel and
        /// was consumed. Tolerant: malformed JSON is dropped without throwing.
        /// </summary>
        public bool TryParseLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;

            int idx = line.IndexOf(IssuePrefix, StringComparison.Ordinal);
            if (idx >= 0)
            {
                try
                {
                    string json  = line.Substring(idx + IssuePrefix.Length);
                    var    issue = JsonUtility.FromJson<RunIssue>(json);
                    if (issue != null && !string.IsNullOrEmpty(issue.code) && !Contains(issue))
                    {
                        // Set after deserializing: the sentinel JSON has no rawJson key, so
                        // FromJson would have cleared anything assigned beforehand.
                        issue.rawJson = json;
                        issues.Add(issue);
                    }
                }
                catch { /* malformed — ignore, the raw line is still in the run log */ }
                return true;
            }

            idx = line.IndexOf(ReportPrefix, StringComparison.Ordinal);
            if (idx >= 0)
            {
                try
                {
                    string finalJson = line.Substring(idx + ReportPrefix.Length);
                    finalRawJson = finalJson;
                    var final = JsonUtility.FromJson<FinalReportDto>(finalJson);
                    if (final != null)
                    {
                        receivedFinal = true;
                        if (!string.IsNullOrEmpty(final.status)) status = final.status;
                        // Merge any issue present in the final report but missed as a live line
                        // (each issue is normally emitted incrementally first — dedupe by code+message).
                        if (final.issues != null)
                            foreach (var i in final.issues)
                                if (i != null && !string.IsNullOrEmpty(i.code) && !Contains(i))
                                    issues.Add(i);
                    }
                }
                catch { }
                return true;
            }

            return false;
        }

        bool Contains(RunIssue candidate)
        {
            foreach (var i in issues)
                if (i.code == candidate.code && i.message == candidate.message) return true;
            return false;
        }

        [Serializable]
        class FinalReportDto
        {
            public string status;
            public List<RunIssue> issues;
        }
    }
}
