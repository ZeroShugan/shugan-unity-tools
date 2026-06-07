using System.Collections.Generic;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    /// <summary>
    /// A reusable package-export configuration for the Package Exporter tool:
    /// which root assets to ship, which discovered dependencies to keep/drop,
    /// the naming scheme, and the current semver.
    ///
    /// IMPORTANT: This ScriptableObject lives in the RUNTIME assembly and in its OWN
    /// file (filename == class name). Unity only creates a usable MonoScript for a
    /// ScriptableObject when those conditions hold — otherwise the created .asset has
    /// a broken type association and won't be found by AssetDatabase.FindAssets, which
    /// is exactly why presets weren't appearing when this class lived inside the editor
    /// window's file.
    /// </summary>
    public class PackageExportPreset : ScriptableObject
    {
        public string packagePrefix = "Shugan_";    // prepended to the file name
        public string baseName      = "MyPackage";  // the package name, e.g. "FeetRig"
        public string versionLabel  = "v";          // printed before the numbers: _v1.0.0
        public int    major = 1;
        public int    minor = 0;
        public int    patch = 0;

        public string exportFolder  = "";           // absolute; defaults to <project-parent>/PACKAGE

        // Root assets the user explicitly wants to ship (direct refs — survive renames/moves).
        public List<Object> rootAssets = new List<Object>();

        // GUIDs of discovered dependencies the user chose to EXCLUDE.
        public List<string> excludedDependencyGuids = new List<string>();

        // Snapshot of the dependency GUIDs at the last Refresh — used to diff on the next Refresh.
        public List<string> lastKnownDependencyGuids = new List<string>();
    }
}
