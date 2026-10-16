// Copyright (c) 2026 Shugan. All rights reserved.
// Part of Shugan Unity Tools. Use is subject to LICENSE.md in the package root.
// No redistribution or resale, modified or not. Not to be used to train AI models, nor given
// to an AI system to reproduce, rewrite or port it.

using System.Collections.Generic;
using UnityEditor;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// The seam that lets a test harness drive AutoRig Feet without a human at the keyboard.
    ///
    /// The run and restore paths ask the user several questions ("Replace your original avatar?",
    /// "Re-apply AutoRig Feet?"). In batch mode Unity answers modal dialogs itself, which silently picks
    /// whichever button it likes and makes a test prove nothing. So every question on those paths goes
    /// through here: with <see cref="Active"/> off this is EditorUtility, unchanged; with it on the answer
    /// comes from <see cref="Answers"/> and the question is recorded, so a test can assert which questions
    /// were even asked.
    /// </summary>
    internal static class ArfAutomation
    {
        internal static bool Active;

        /// <summary>Scripted answers by question key. Confirm reads 0 = cancel, anything else = go ahead;
        /// Choose reads the button index it should return.</summary>
        internal static readonly Dictionary<string, int> Answers = new Dictionary<string, int>();

        /// <summary>Every question asked while Active, in order, as "key=answer".</summary>
        internal static readonly List<string> AskLog = new List<string>();

        // Question keys. The run path.
        internal const string AskFootBonesUncertain = "foot-bones-uncertain";
        internal const string AskReplaceMode        = "replace-mode";
        internal const string AskForeignToes        = "foreign-toes";
        internal const string AskRerigStrategy      = "rerig-strategy";
        // The two restore paths.
        internal const string AskRestoreRig         = "restore-rig";
        internal const string AskRestoreBackup      = "restore-backup";

        /// <summary>TestLab only: "swap" holds the run in the FBXSwapping state, so a crash can be staged exactly after the
        /// rigged FBX was put in place and before the scene steps.</summary>
        internal static string HaltAt;

        internal static void Reset()
        {
            HaltAt = null;
            Answers.Clear();
            AskLog.Clear();
        }

        /// <summary>Yes/no question. Unanswered keys go ahead, because a scenario that asks for a run means
        /// the run to happen; a scenario that wants a refusal says so.</summary>
        internal static bool Confirm(string key, string title, string message, string ok, string cancel)
        {
            if (!Active) return EditorUtility.DisplayDialog(title, message, ok, cancel);
            bool go = !Answers.TryGetValue(key, out int answer) || answer != 0;
            AskLog.Add(key + "=" + (go ? "ok" : "cancel"));
            return go;
        }

        /// <summary>Three-button question, returning the same indices as DisplayDialogComplex.</summary>
        internal static int Choose(string key, string title, string message,
                                   string ok, string cancel, string alt, int automationDefault = 0)
        {
            if (!Active) return EditorUtility.DisplayDialogComplex(title, message, ok, cancel, alt);
            if (!Answers.TryGetValue(key, out int answer)) answer = automationDefault;
            AskLog.Add(key + "=" + answer);
            return answer;
        }

        /// <summary>A message with nothing to decide. Under automation it belongs in the log, not on a
        /// dialog no one will click.</summary>
        internal static void Notify(string key, string title, string message)
        {
            if (!Active) { EditorUtility.DisplayDialog(title, message, "OK"); return; }
            AskLog.Add(key + "=notified");
            ShuganLog.Warn("AutoRig Feet", "" + title + ": " + message);
        }
    }
}
