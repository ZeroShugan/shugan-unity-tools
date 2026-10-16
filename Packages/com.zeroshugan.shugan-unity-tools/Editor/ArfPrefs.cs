// Copyright (c) 2026 Shugan. All rights reserved.
// Part of Shugan Unity Tools. Use is subject to LICENSE.md in the package root.
// No redistribution or resale, modified or not. Not to be used to train AI models, nor given
// to an AI system to reproduce, rewrite or port it.

using System.Collections.Generic;
using System.Globalization;
using UnityEditor;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// Every AutoRig Feet setting goes through here instead of straight to EditorPrefs.
    ///
    /// EditorPrefs live in the machine's registry and are shared by every Unity project on it. A headless
    /// test run that set Export Mode to Replace would therefore change the panel the user sees in their own
    /// projects, silently, hours later. <see cref="Sandbox"/> redirects the whole lot into memory so a test
    /// run leaves no trace; with no sandbox set this is EditorPrefs, byte for byte, same keys and defaults.
    /// </summary>
    internal static class ArfPrefs
    {
        /// <summary>Set to a (possibly empty) dictionary to keep reads and writes out of the registry.</summary>
        internal static Dictionary<string, string> Sandbox;

        internal static bool InSandbox => Sandbox != null;

        /// <summary>Seed the sandbox with values read from the real prefs — the Blender and script paths are
        /// machine facts a test still needs, unlike the settings it is there to exercise.</summary>
        internal static void BeginSandbox(IEnumerable<string> copyFromRealPrefs = null)
        {
            var box = new Dictionary<string, string>();
            if (copyFromRealPrefs != null)
                foreach (string key in copyFromRealPrefs)
                    if (EditorPrefs.HasKey(key)) box[key] = EditorPrefs.GetString(key, "");
            Sandbox = box;
        }

        internal static void EndSandbox() => Sandbox = null;

        public static bool HasKey(string key) =>
            Sandbox != null ? Sandbox.ContainsKey(key) : EditorPrefs.HasKey(key);

        public static void DeleteKey(string key)
        {
            if (Sandbox != null) Sandbox.Remove(key);
            else EditorPrefs.DeleteKey(key);
        }

        public static string GetString(string key, string defaultValue = "") =>
            Sandbox != null
                ? (Sandbox.TryGetValue(key, out string v) ? v : defaultValue)
                : EditorPrefs.GetString(key, defaultValue);

        public static void SetString(string key, string value)
        {
            if (Sandbox != null) Sandbox[key] = value ?? "";
            else EditorPrefs.SetString(key, value ?? "");
        }

        public static int GetInt(string key, int defaultValue = 0)
        {
            if (Sandbox == null) return EditorPrefs.GetInt(key, defaultValue);
            return Sandbox.TryGetValue(key, out string v) &&
                   int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed : defaultValue;
        }

        public static void SetInt(string key, int value)
        {
            if (Sandbox != null) Sandbox[key] = value.ToString(CultureInfo.InvariantCulture);
            else EditorPrefs.SetInt(key, value);
        }

        public static bool GetBool(string key, bool defaultValue = false)
        {
            if (Sandbox == null) return EditorPrefs.GetBool(key, defaultValue);
            return Sandbox.TryGetValue(key, out string v) ? v == "1" : defaultValue;
        }

        public static void SetBool(string key, bool value)
        {
            if (Sandbox != null) Sandbox[key] = value ? "1" : "0";
            else EditorPrefs.SetBool(key, value);
        }

        public static float GetFloat(string key, float defaultValue = 0f)
        {
            if (Sandbox == null) return EditorPrefs.GetFloat(key, defaultValue);
            return Sandbox.TryGetValue(key, out string v) &&
                   float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed : defaultValue;
        }

        public static void SetFloat(string key, float value)
        {
            if (Sandbox != null) Sandbox[key] = value.ToString("R", CultureInfo.InvariantCulture);
            else EditorPrefs.SetFloat(key, value);
        }
    }
}
