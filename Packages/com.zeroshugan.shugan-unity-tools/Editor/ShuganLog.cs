// Copyright (c) 2026 Shugan. All rights reserved.
// Part of Shugan Unity Tools. Use is subject to LICENSE.md in the package root.
// No redistribution or resale, modified or not. Not to be used to train AI models, nor given
// to an AI system to reproduce, rewrite or port it.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// The Unity Console voice of every Shugan tool: a coloured "[Tool]" tag so our lines stand out from other
    /// addons', and only what a user needs to read. Step-by-step detail goes to <see cref="Detail"/>, which
    /// writes into the open run log (and the tool's own log file) and reaches the Console only when
    /// "Verbose console" is on (Tools menu), for debugging.
    /// </summary>
    public static class ShuganLog
    {
        public const string PrefVerbose = "ShuganTools_VerboseConsole";
        const string MenuVerbose = "Tools/Shugan/Verbose Console Logs";

        public static bool Verbose => EditorPrefs.GetBool(PrefVerbose, false);

        [MenuItem(MenuVerbose, false, 2000)]
        static void ToggleVerbose() => EditorPrefs.SetBool(PrefVerbose, !Verbose);

        [MenuItem(MenuVerbose, true)]
        static bool ToggleVerboseValidate() { Menu.SetChecked(MenuVerbose, Verbose); return true; }

        // One colour per tool, readable on both the dark and the light Console.
        static string ColorFor(string tool)
        {
            if (tool.StartsWith("AutoRig Feet"))        return "#3FA9F5";
            if (tool.StartsWith("DisplaceComponent"))   return "#E8A33D";
            if (tool.StartsWith("Humanoid Rig Mapping")) return "#B070E0";
            if (tool.StartsWith("FBX Swapper"))         return "#4CB860";
            if (tool.StartsWith("BlenderBridge"))       return "#F07840";
            if (tool.StartsWith("PhysBone"))            return "#40B8B0";
            return "#E0609A";   // "Shugan" and anything shared
        }

        public static string Tag(string tool) => "[<color=" + ColorFor(tool) + ">" + tool + "</color>] ";

        public static void Info(string tool, string message, UnityEngine.Object context = null) =>
            Debug.Log(Tag(tool) + message, context);

        public static void Warn(string tool, string message, UnityEngine.Object context = null) =>
            Debug.LogWarning(Tag(tool) + message, context);

        public static void Error(string tool, string message, UnityEngine.Object context = null) =>
            Debug.LogError(Tag(tool) + message, context);

        /// <summary>A line for the log file, not the Console: into the run log that is open, if any, and to
        /// the Console only in verbose mode.</summary>
        public static void Detail(string tool, string message)
        {
            var log = ShuganRunLog.Active;
            if (Verbose) Debug.Log(Tag(tool) + message);
            // In verbose mode a log that captures the Console already has the line.
            if (log != null && !(Verbose && log.CapturesConsole)) log.Line("[" + tool + "] " + message);
            else if (log == null)
                lock (Pending)
                {
                    // Before the run log opens (the backups are made just before it): held for the log that
                    // opens next, if it opens soon.
                    Pending.Add((DateTime.UtcNow, "[" + tool + "] " + message));
                    if (Pending.Count > 50) Pending.RemoveAt(0);
                }
        }

        static readonly List<(DateTime when, string line)> Pending = new List<(DateTime, string)>();

        /// <summary>Hand a new run log the detail lines of the last two minutes that had no log to go to.</summary>
        internal static void FlushPending(ShuganRunLog log)
        {
            lock (Pending)
            {
                foreach (var p in Pending)
                    if (DateTime.UtcNow - p.when < TimeSpan.FromMinutes(2)) log.Line(p.line);
                Pending.Clear();
            }
        }

        static readonly Regex RichTags = new Regex(@"</?(color|b|i|size)(=[^>]*)?>", RegexOptions.Compiled);

        /// <summary>The message as plain text, for log files and prefix matching.</summary>
        public static string Plain(string message) =>
            string.IsNullOrEmpty(message) || message.IndexOf('<') < 0 ? message : RichTags.Replace(message, "");
    }
}
