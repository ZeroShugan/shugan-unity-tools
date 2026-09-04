using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// [ARF-BUGREPORT] Consent-first bug reporting, ported from the Data Transfer++ addon's
    /// operators/bug_report.py so both products behave identically and hit the same ingest service.
    /// Tool-agnostic: pass a product slug and the log parts to include.
    ///
    /// Privacy contract (mirrors H:\Infrastructure\docs\services\bug-reports.md):
    ///
    ///   • NOTHING is ever sent automatically. The user clicks Send; View shows the exact text first.
    ///   • <see cref="Build"/> is the SINGLE source of truth for preview and send, so what the user
    ///     reviews cannot drift from what is transmitted. This is the property that makes
    ///     "View Report" worth trusting — keep it that way if you extend this.
    ///   • Usernames, home folders and the project root are replaced before preview OR send.
    ///   • The only identifier is a random UUID generated locally on first use. It exists for rate
    ///     limiting and for correlating repeat reports, and is traceable to nobody.
    ///   • One-way: the server can only answer "received" / "slow down". Nothing it returns is ever
    ///     executed or shown as an instruction, so there is no remote-control surface.
    ///
    /// The server is already multi-product and needs no change to accept a new slug.
    /// </summary>
    public static class ShuganBugReport
    {
        public const string DefaultUrl = "https://shugan.dev/api/bug-report";
        /// <summary>Env override, for testing against the LAN service before a release.</summary>
        public const string UrlEnvVar = "SHUGAN_BUG_REPORT_URL";

        // Client-side limits. The server enforces its own (256 KB body, 200 KB logs, 10/day per IP,
        // 5/day per install); these keep us comfortably inside them and give better error messages.
        public const int MinSendIntervalSec = 300;
        public const int MaxLogsChars       = 150000;
        /// <summary>Server refuses a logs field over 200 KB; stay under with headroom.</summary>
        public const int MaxLogsBytes       = 180000;

        /// <summary>
        /// Marks the start of each artifact in the bundle. Deliberately distinctive: the first
        /// version used `===== name =====`, which collides with the banner rules the Blender script
        /// prints (`===== Nail =====`, `===== Body_Delete =====`), so scanning a received report for
        /// its parts turned up phantom entries. Grep this exact string to split a report.
        /// </summary>
        public const string PartHeader = "#### SHUGAN REPORT PART:";
        public const int MaxMessageChars    = 4000;
        public const int MaxContactChars    = 200;
        public const int MaxSmallChars      = 100;

        // ─── Payload ───────────────────────────────────────────────────────────

        /// <summary>
        /// Exactly the eight fields the ingest server accepts. It rebuilds an allowlisted dict on
        /// its side, so extra fields are silently dropped — keep this in sync rather than adding.
        /// </summary>
        [Serializable]
        public class Payload
        {
            public string product;
            public string product_version;
            public string blender_version;   // the server's "runtime" slot — Unity + Blender here
            public string os;
            public string install_id;
            public string message;
            public string contact;
            public string logs;
        }

        /// <summary>
        /// One artifact in the bundle. <see cref="trimmable"/> separates the big console log from
        /// the small structured documents, which matters a lot — see <see cref="BuildLogs"/>.
        /// </summary>
        public class LogPart
        {
            public string name;
            public string text;
            public bool   trimmable;

            public LogPart(string name, string text, bool trimmable = false)
            {
                this.name = name; this.text = text; this.trimmable = trimmable;
            }
        }

        /// <summary>What the calling tool supplies. Log parts are concatenated in order.</summary>
        public class Request
        {
            public string product         = "";
            public string productVersion  = "";
            public string runtime         = "";
            public string message         = "";
            public string contact         = "";
            public List<LogPart> logParts = new List<LogPart>();
        }

        // ─── Build (single source of truth) ────────────────────────────────────

        public static Payload Build(Request req)
        {
            req = req ?? new Request();
            return new Payload
            {
                product         = Clip(req.product, MaxSmallChars),
                product_version = Clip(req.productVersion, MaxSmallChars),
                blender_version = Clip(req.runtime, MaxSmallChars),
                os              = Clip(DescribeOs(), MaxSmallChars),
                install_id      = InstallId(),
                message         = Clip(req.message ?? "", MaxMessageChars),
                contact         = Clip(req.contact ?? "", MaxContactChars),
                logs            = BuildLogs(req.logParts),
            };
        }

        /// <summary>
        /// Assemble the bundle within budget, giving the small structured documents priority.
        ///
        /// The first version simply concatenated everything and trimmed the middle. Measured against
        /// a real run that was fatal to the result: the bundle was 289 000 chars, of which the
        /// console log alone was 233 000, so the middle trim ate `avatar.json` almost entirely —
        /// the anonymized avatar description, the package list and the dependency graph all
        /// vanished, to preserve more of a log we already had plenty of.
        ///
        /// A middle trim is right for ONE long log. It is wrong for a concatenation of documents
        /// with very different value per character. So: everything except the console log is kept
        /// whole, and the log gets whatever budget remains (head + tail, since its opening carries
        /// the settings and its tail carries the failure).
        /// </summary>
        static string BuildLogs(List<LogPart> parts)
        {
            if (parts == null || parts.Count == 0) return "";

            int overheadPerPart = 24;                       // the "===== name =====" framing
            int keepWholeChars  = 0;
            foreach (var p in parts)
                if (!p.trimmable)
                    keepWholeChars += (p.text != null ? p.text.Length : 0)
                                    + (p.name != null ? p.name.Length : 0) + overheadPerPart;

            // Leave the trimmable parts a fair share, and never let the keep-whole set starve them
            // completely — if the structured files alone blow the budget, everything gets trimmed
            // together by the final CapMiddle below.
            int trimBudget = Math.Max(0, MaxLogsChars - keepWholeChars);

            var trimmables = parts.FindAll(p => p.trimmable);
            int perTrimmable = trimmables.Count > 0 ? trimBudget / trimmables.Count : 0;

            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                string text = p.text ?? "(empty)";
                if (p.trimmable) text = CapMiddle(text, perTrimmable);
                sb.Append(PartHeader).Append(' ').Append(p.name).Append('\n');
                sb.Append(text).Append("\n\n");
            }

            // Sanitize once, over the whole bundle, so nothing can slip through a part boundary.
            string outp = ShuganSanitize.Text(sb.ToString());

            // Backstop for the pathological case (huge structured files).
            outp = CapMiddle(outp, MaxLogsChars);

            // Byte backstop: the server rejects a logs field over 200 KB, and a char is not a byte.
            // An avatar with Japanese bone names is 3 bytes per character in UTF-8, so 150 000
            // characters could be 400 KB and get the whole report refused with a 413.
            return CapBytes(outp, MaxLogsBytes);
        }

        /// <summary>Shrink until the UTF-8 encoding fits, trimming from the middle each round.</summary>
        static string CapBytes(string s, int maxBytes)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            int chars = s.Length;
            for (int guard = 0; guard < 8; guard++)
            {
                int bytes = Encoding.UTF8.GetByteCount(s);
                if (bytes <= maxBytes) return s;
                // Scale by the observed bytes-per-char, with headroom so we converge quickly.
                chars = (int)(chars * (maxBytes / (double)bytes) * 0.95);
                if (chars < 1000) return s.Substring(0, Math.Min(s.Length, 1000));
                s = CapMiddle(s, chars);
            }
            return s;
        }

        /// <summary>
        /// Trim from the MIDDLE, not the end. A run log's opening section carries the settings and
        /// detection phase, and its tail carries the failure; cutting either loses half the story.
        /// </summary>
        public static string CapMiddle(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            int head = Math.Min(20000, max / 4);
            int tail = Math.Max(0, max - head - 120);
            int cut  = s.Length - head - tail;
            return s.Substring(0, head)
                 + "\n\n[... " + cut + " characters trimmed from the middle of this bundle ...]\n\n"
                 + s.Substring(s.Length - tail);
        }

        // ─── Human-readable rendering (what View Report shows) ─────────────────

        public static string AsText(Payload p)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SHUGAN UNITY TOOLS — BUG REPORT");
            sb.AppendLine("This is EXACTLY what will be sent. Nothing else leaves your machine.");
            sb.AppendLine("Note: the data below includes names you used in Unity (objects, bones,");
            sb.AppendLine("meshes, shape keys). Material and texture names are replaced with");
            sb.AppendLine("anonymous ids. No mesh geometry, textures or files are included.");
            sb.AppendLine(new string('=', 70));
            sb.AppendLine("Product           : " + p.product + " v" + p.product_version);
            sb.AppendLine("Runtime           : " + p.blender_version);
            sb.AppendLine("OS                : " + p.os);
            sb.AppendLine("Anonymous ID      : " + p.install_id + " (random, not linked to you)");
            sb.AppendLine("Your message      : " + (string.IsNullOrEmpty(p.message) ? "(empty)" : p.message));
            sb.AppendLine("Contact (optional): " + (string.IsNullOrEmpty(p.contact)
                ? "(none — report is anonymous)" : p.contact));
            sb.AppendLine(new string('=', 70));
            sb.AppendLine("LOGS (paths sanitized, asset names anonymized):");
            sb.AppendLine();
            sb.AppendLine(p.logs);
            return sb.ToString();
        }

        /// <summary>Write the preview and open it in the OS text editor. Sends nothing.</summary>
        public static string OpenPreview(Payload p)
        {
            string path = Path.Combine(Path.GetTempPath(), "shugan_bug_report_preview.txt");
            try
            {
                File.WriteAllText(path, AsText(p), new UTF8Encoding(false));
                EditorUtility.OpenWithDefaultApp(path);
                return path;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Shugan] Could not open the report preview: " + ex.Message);
                return null;
            }
        }

        // ─── Local state: anonymous id + cooldown ──────────────────────────────

        [Serializable] class State { public string install_id = ""; public string last_send_utc = ""; }

        static string StatePath()
        {
            // Machine-wide, not per-project: a user with several Unity projects is one installation,
            // and should get one identity and one rate-limit bucket.
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir  = Path.Combine(root, "Shugan");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "unity_bug_report_state.json");
        }

        static State LoadState()
        {
            try
            {
                string p = StatePath();
                if (File.Exists(p))
                    return JsonUtility.FromJson<State>(File.ReadAllText(p)) ?? new State();
            }
            catch { }
            return new State();
        }

        static void SaveState(State s)
        {
            // Every failure here degrades to "new id, no cooldown" rather than breaking the feature —
            // the same tolerance the addon has for a read-only config dir.
            try { File.WriteAllText(StatePath(), JsonUtility.ToJson(s, true)); } catch { }
        }

        public static string InstallId()
        {
            var s = LoadState();
            if (string.IsNullOrEmpty(s.install_id))
            {
                s.install_id = Guid.NewGuid().ToString();
                SaveState(s);
            }
            return s.install_id;
        }

        /// <summary>Seconds still to wait before another send is allowed; 0 when ready.</summary>
        public static int CooldownRemaining()
        {
            try
            {
                var s = LoadState();
                if (string.IsNullOrEmpty(s.last_send_utc)) return 0;
                if (!DateTime.TryParse(s.last_send_utc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out DateTime last))
                    return 0;
                double elapsed = (DateTime.UtcNow - last).TotalSeconds;
                if (elapsed < 0) return 0;                      // clock moved backwards
                return (int)Math.Max(0, MinSendIntervalSec - elapsed);
            }
            catch { return 0; }
        }

        static void MarkSent()
        {
            var s = LoadState();
            s.last_send_utc = DateTime.UtcNow.ToString("o");
            SaveState(s);
        }

        // ─── Send ──────────────────────────────────────────────────────────────

        [Serializable] class Answer { public bool ok; public string error; }

        static UnityWebRequest _req;
        static Action<bool, string> _onDone;

        public static bool IsSending { get { return _req != null; } }

        public static string Endpoint()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(UrlEnvVar);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { }
            return DefaultUrl;
        }

        /// <summary>
        /// POST the payload. <paramref name="consentGiven"/> is re-checked here even though the UI
        /// already gates it — the same belt-and-braces the addon applies at execute time, so no
        /// future caller can send without consent by wiring up the wrong button.
        /// </summary>
        public static void Send(Payload p, bool consentGiven, Action<bool, string> onDone)
        {
            if (onDone == null) onDone = delegate { };

            if (!consentGiven)      { onDone(false, "Please tick the consent checkbox first."); return; }
            if (p == null)          { onDone(false, "Nothing to send."); return; }
            if (_req != null)       { onDone(false, "A report is already being sent."); return; }
            if (string.IsNullOrEmpty((p.message ?? "").Trim()))
            {
                onDone(false, "Please describe what happened first.");
                return;
            }

            int wait = CooldownRemaining();
            if (wait > 0)
            {
                onDone(false, "Please wait " + wait + "s before sending another report.");
                return;
            }

            try
            {
                byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(p));
                _req = new UnityWebRequest(Endpoint(), "POST")
                {
                    uploadHandler   = new UploadHandlerRaw(body),
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout         = 15,
                };
                _req.SetRequestHeader("Content-Type", "application/json");
                _onDone = onDone;
                _req.SendWebRequest();
                EditorApplication.update += Poll;
            }
            catch (Exception ex)
            {
                Cleanup();
                onDone(false, "Could not start the upload (" + ex.Message + ").");
            }
        }

        static void Poll()
        {
            if (_req == null) { EditorApplication.update -= Poll; return; }
            if (!_req.isDone) return;
            EditorApplication.update -= Poll;

            bool   ok  = false;
            string msg;
            try
            {
                if (_req.result != UnityWebRequest.Result.Success)
                {
                    msg = "Could not send the report (" + _req.error + "). You can use " +
                          "View Report and email the file instead.";
                }
                else
                {
                    Answer a = null;
                    try { a = JsonUtility.FromJson<Answer>(_req.downloadHandler.text); } catch { }
                    if (a != null && a.ok)
                    {
                        ok  = true;
                        msg = "Bug report sent — thank you!";
                        MarkSent();
                    }
                    else
                    {
                        string err = a != null && !string.IsNullOrEmpty(a.error) ? a.error : "unknown";
                        msg = "The server refused the report: " + err;
                    }
                }
            }
            catch (Exception ex) { msg = "Send failed: " + ex.Message; }

            var cb = _onDone;
            Cleanup();
            if (cb != null) cb(ok, msg);
        }

        static void Cleanup()
        {
            if (_req != null) { try { _req.Dispose(); } catch { } }
            _req    = null;
            _onDone = null;
        }

        // ─── Misc ──────────────────────────────────────────────────────────────

        static string DescribeOs()
        {
            try { return SystemInfo.operatingSystem; } catch { return "unknown"; }
        }

        static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
