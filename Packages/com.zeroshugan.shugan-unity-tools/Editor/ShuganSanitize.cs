using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// Privacy layer for anything that can leave the customer's machine (diagnostic bundles,
    /// bug reports).
    ///
    /// Two independent jobs:
    ///
    ///   1. <see cref="Text"/> — strips machine-identifying strings (Windows/POSIX user folders,
    ///      the login name, the project root) out of free-form log text. Ported from the
    ///      Data Transfer++ addon's `_sanitize()` so both products behave identically.
    ///
    ///   2. <see cref="DescribeAsset"/> — decides whether an asset may be named in the clear or
    ///      must appear as a stable alias like `Texture2D#a1b2c3d4`.
    ///
    /// The asset policy is an ALLOWLIST, deliberately: an unrecognised asset type is redacted.
    /// A new Unity asset type, or a package we have never seen, therefore fails safe instead of
    /// leaking a third-party creator's filenames the first time someone uses it.
    ///
    /// What stays in the clear is rig/animation data we genuinely need to reproduce a bug —
    /// bones, meshes, blendshapes, clips, controllers. What gets aliased is everything whose
    /// NAME is the creator's work rather than ours: textures, materials, audio, and anything
    /// unclassified. The alias keeps the graph readable (you can still see that three meshes
    /// share one material) without ever transmitting how the creator named it.
    /// </summary>
    public static class ShuganSanitize
    {
        // ─── 1. Free-form text ─────────────────────────────────────────────────

        const string UserToken    = "%USER%";
        const string ProjectToken = "%PROJECT%";

        // "C:\Users\shugan\..." / "D:/Users/shugan/..."  →  "C:\Users\%USER%\..."
        static readonly Regex WindowsHome = new Regex(
            @"([A-Za-z]:[\\/]Users[\\/])[^\\/\r\n]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "/home/shugan/..." / "/Users/shugan/..."  →  "/home/%USER%/..."
        static readonly Regex PosixHome = new Regex(
            @"(/(?:home|Users)/)[^/\r\n]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static Regex _loginName;      // built lazily from Environment.UserName
        static Regex _projectRoot;    // built lazily from Application.dataPath
        static bool  _patternsBuilt;

        /// <summary>
        /// Redact machine-identifying strings. Safe to call twice — the replacement tokens do not
        /// match any of the patterns, so the operation is idempotent.
        /// </summary>
        public static string Text(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            BuildPatterns();

            // Project root first: it is the longest and most specific match, and on this machine it
            // sits under H:\Mega\... rather than a user folder, so the home patterns would miss it.
            if (_projectRoot != null) s = _projectRoot.Replace(s, ProjectToken);

            s = WindowsHome.Replace(s, "$1" + UserToken);
            s = PosixHome.Replace(s, "$1" + UserToken);

            // Catch the bare login name where the path patterns miss it (Blender printing
            // "launched by <user>", env dumps, non-home drives like D:\Backup\<user>\...).
            if (_loginName != null) s = _loginName.Replace(s, UserToken);

            return s;
        }

        static void BuildPatterns()
        {
            if (_patternsBuilt) return;
            _patternsBuilt = true;

            try
            {
                string user = Environment.UserName;
                if (!string.IsNullOrEmpty(user) && user.Length > 2)
                {
                    // WHOLE-IDENTIFIER match only. A bare substring replace corrupts the log:
                    // a user called "max" would turn the bone name "Maxilla" into "%USER%illa",
                    // "ann" would turn a mesh "Hannah_body" into "H%USER%ah_body", and on this
                    // developer's own machine "shugan" rewrote the product's own files
                    // ("shugan_autorig_feet.py" -> "%USER%_autorig_feet.py") and class names in
                    // stack traces ("ShuganRunLog" -> "%USER%RunLog"). The length > 2 guard alone
                    // does not save you — plenty of real usernames are substrings of real words.
                    //
                    // The genuine leak vectors (home folders, the project root) are already
                    // covered by the patterns above, so this pass only needs to catch the name
                    // standing on its own.
                    //
                    // Known cosmetic side effect on the developer's own machine only: the folder
                    // "Assets/! Shugan/..." becomes "Assets/! %USER%/..." there, because "Shugan"
                    // IS a standalone identifier and IS that machine's login name. Harmless — the
                    // path is already %PROJECT%-redacted — and it cannot happen for a customer,
                    // whose login name is not "shugan".
                    _loginName = new Regex(
                        @"(?<![A-Za-z0-9_])" + Regex.Escape(user) + @"(?![A-Za-z0-9_])",
                        RegexOptions.IgnoreCase);
                }
            }
            catch { }

            try
            {
                // Application.dataPath ends in "/Assets"; we want the folder above it, in both
                // slash styles, because Unity and Blender print paths differently.
                string assets = Application.dataPath;
                string root   = assets.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase)
                    ? assets.Substring(0, assets.Length - "/Assets".Length)
                    : assets;
                if (!string.IsNullOrEmpty(root) && root.Length > 3)
                {
                    string fwd  = root.Replace('\\', '/');
                    string back = root.Replace('/', '\\');
                    string pat  = fwd == back
                        ? Regex.Escape(fwd)
                        : Regex.Escape(fwd) + "|" + Regex.Escape(back);
                    _projectRoot = new Regex(pat, RegexOptions.IgnoreCase);
                }
            }
            catch { }
        }

        // ─── 2. Asset naming policy ────────────────────────────────────────────

        /// <summary>
        /// Asset types whose NAME is rig/animation data we need, and is ours or the user's own
        /// structure rather than a creator's art. Everything not listed here is aliased.
        /// </summary>
        static readonly HashSet<string> ClearTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "GameObject",                  // FBX / prefab — the avatar itself
            "AnimationClip",               // clip names explain which animation drives a blendshape
            "AnimatorController",
            "AnimatorOverrideController",
            "AvatarMask",
            "Avatar",                      // the humanoid Avatar sub-asset
            "MonoScript",                  // identifies the package/tool, not creator art
            "AssemblyDefinitionAsset",
        };

        /// <summary>
        /// A dependency as it may safely appear in a diagnostic bundle. <see cref="display"/> is
        /// either the real asset path (allowlisted types) or a stable alias.
        /// </summary>
        public struct AssetRef
        {
            public string display;    // "Assets/Booth/X.fbx"  OR  "Texture2D#a1b2c3d4"
            public string typeName;   // "Texture2D"
            public string extension;  // ".png"
            public long   sizeBytes;  // -1 when unknown
            public bool   redacted;
        }

        /// <summary>
        /// Classify one asset path. Never opens the file for its contents — only the AssetDatabase
        /// (type + GUID) and, for the size, the file length reported by the filesystem.
        /// </summary>
        public static AssetRef DescribeAsset(string assetPath)
        {
            var r = new AssetRef { sizeBytes = -1, extension = "", typeName = "Unknown", display = "" };
            if (string.IsNullOrEmpty(assetPath)) return r;

            try { r.extension = System.IO.Path.GetExtension(assetPath) ?? ""; } catch { }

            Type t = null;
            try { t = AssetDatabase.GetMainAssetTypeAtPath(assetPath); } catch { }
            r.typeName = t != null ? t.Name : "Unknown";

            try
            {
                string abs = ToAbsolute(assetPath);
                if (!string.IsNullOrEmpty(abs) && System.IO.File.Exists(abs))
                    r.sizeBytes = new System.IO.FileInfo(abs).Length;
            }
            catch { }

            // Packages/... is public, versioned, non-creator content — its paths are useful for
            // telling which SDK/tool version is installed and carry nothing private.
            bool isPackage = assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);

            if (isPackage || ClearTypeNames.Contains(r.typeName))
            {
                r.display  = assetPath;
                r.redacted = false;
            }
            else
            {
                r.display  = Alias(r.typeName, assetPath);
                r.redacted = true;
            }
            return r;
        }

        /// <summary>
        /// Stable, non-reversible label for a redacted asset: type + the first 8 characters of its
        /// GUID. Stable matters — the same texture reads identically across two reports from the
        /// same user, so repeat reports can be correlated. Falls back to a hash of the path when
        /// the asset has no GUID (which happens for assets outside the database).
        /// </summary>
        public static string Alias(string typeName, string assetPath)
        {
            string key = null;
            try { key = AssetDatabase.AssetPathToGUID(assetPath); } catch { }
            if (string.IsNullOrEmpty(key))
                key = Math.Abs(assetPath.GetHashCode()).ToString("x8");
            if (key.Length > 8) key = key.Substring(0, 8);
            return (string.IsNullOrEmpty(typeName) ? "Asset" : typeName) + "#" + key;
        }

        /// <summary>Alias for a loaded object (used for materials referenced from a renderer).</summary>
        public static string AliasObject(UnityEngine.Object o)
        {
            if (o == null) return "(none)";
            string path = "";
            try { path = AssetDatabase.GetAssetPath(o); } catch { }
            if (string.IsNullOrEmpty(path))
                return o.GetType().Name + "#scene" + Math.Abs(o.GetInstanceID()).ToString("x8");
            var r = DescribeAsset(path);
            return r.display;
        }

        /// <summary>Project-relative asset path → absolute path on disk.</summary>
        public static string ToAbsolute(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            try
            {
                return System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, "..", assetPath));
            }
            catch { return null; }
        }
    }
}
