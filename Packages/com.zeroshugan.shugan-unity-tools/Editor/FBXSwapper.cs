using UnityEngine;
using UnityEngine.Animations;
using UnityEditor;
using UnityEditorInternal;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace ShuganTools
{
    public class FBXSwapper : EditorWindow
    {
        #region Variables
        
        // Core references
        private GameObject targetPrefab;
        private GameObject newFbxModel;
        private GameObject oldFbxToReplace;
        
        // Settings
        private string scenePrefabFolder = "Assets/! Shugan/!_Prefabs/Custom/_Generated";
        private bool addToSceneAfterCreation = true;
        
        // Manual selection tracking
        private bool manualTargetSelection = false;
        private bool manualNewFbxSelection = false;
        private bool manualOldFbxSelection = false;
        
        // Warning messages
        private string targetPrefabWarning = "";
        private string newFbxWarning = "";
        private string oldFbxWarning = "";
        
        // Model presets
        [System.Serializable]
        public class ModelPreset
        {
            public string name;
            public string[] prefabPaths;
            public string[] prefabDisplayNames;
            public string oldFbxPath;
            public string newFbxPath;
            public string description;
        }
        
        private ModelPreset[] modelPresets = new ModelPreset[]
        {
            new ModelPreset
            {
                name = "Custom",
                prefabPaths = new string[0],
                prefabDisplayNames = new string[0],
                oldFbxPath = "",
                newFbxPath = "",
                description = "Manually select your own prefabs and FBX files"
            },
            new ModelPreset
            {
                name = "MANUKA_v1_02",
                prefabPaths = new string[] 
                { 
                    "Assets/MANUKA/Prefab/MANUKA_Poiyomi.prefab",
                    "Assets/MANUKA/Prefab/MANUKA_lilToon.prefab"
                },
                prefabDisplayNames = new string[]
                {
                    "MANUKA (Poiyomi)",
                    "MANUKA (lilToon)"
                },
                oldFbxPath = "Assets/MANUKA/FBX/MANUKA.fbx",
                newFbxPath = "Assets/MANUKA/FBX/MANUKA_v1_02_feet_by_shugan.fbx",
                description = "MANUKA model with automatic prefab and FBX detection"
            },
            new ModelPreset
            {
                name = "RINDO",
                prefabPaths = new string[] 
                {
                    "Assets/RINDO/Prefab/RINDO_Default.prefab"
                },
                prefabDisplayNames = new string[]
                {
                    "RINDO (Default)"
                },
                oldFbxPath = "Assets/RINDO/FBX/RINDO.fbx",
                newFbxPath = "Assets/RINDO/FBX/RINDO_Enhanced.fbx",
                description = "RINDO model (configure paths as needed)"
            }
        };
        
        private int selectedModelIndex = 0;
        private int selectedPrefabIndex = 0;
        private bool showAdvancedInformation = false;
        private bool showWarning = true;
        
        // UI scroll position
        private Vector2 scrollPosition;
        
        #endregion
        
        #region Window Setup
        
        [MenuItem("Tools/Shugan/FBX Swapper")]
        static void OpenWindow()
        {
            var window = GetWindow<FBXSwapper>("FBX Swapper");
            window.minSize = new Vector2(400, 500);
        }
        
        #endregion
        
        #region GUI Methods
        
        void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            
            DrawHeader();
            DrawSocialLinksSection();
            EditorGUILayout.Space();
            
            DrawWarningSection();
            EditorGUILayout.Space();
            
            DrawModelSelectionSection();
            EditorGUILayout.Space();
            
            DrawTargetPrefabSection();
            EditorGUILayout.Space();
            
            DrawFBXSelectionSection();
            EditorGUILayout.Space();
            
            DrawScenePrefabSection();
            EditorGUILayout.Space();
            
            DrawAdvancedInformation();
            EditorGUILayout.Space();
            
            DrawGenerationOptionsSection();
            EditorGUILayout.Space();
            
            DrawGenerateButton();
            EditorGUILayout.Space();
            
            DrawCreditsSection();
            
            EditorGUILayout.EndScrollView();
        }
        
        void DrawHeader()
        {
            GUILayout.Label("FBX Model Swapper", EditorStyles.largeLabel);
            
            EditorGUILayout.Space(5);
            
            var rect = EditorGUILayout.GetControlRect(false, 2);
            EditorGUI.DrawRect(rect, Color.gray);
        }
        
        void DrawSocialLinksSection()
        {
            EditorGUILayout.BeginHorizontal();
            
            var originalColor = GUI.color;
            
            GUI.color = Color.cyan;
            if (GUILayout.Button("Discord", EditorStyles.miniButton, GUILayout.Width(60)))
            {
                Application.OpenURL("https://discord.com/invite/6FZmzkb");
            }
            
            GUI.color = Color.red;
            if (GUILayout.Button("Booth", EditorStyles.miniButton, GUILayout.Width(50)))
            {
                Application.OpenURL("https://shugan.booth.pm/");
            }
            
            GUI.color = Color.magenta;
            if (GUILayout.Button("Gumroad", EditorStyles.miniButton, GUILayout.Width(70)))
            {
                Application.OpenURL("https://gumroad.com/shugan");
            }
            
            GUI.color = new Color(1f, 0.5f, 0f);
            if (GUILayout.Button("Blender Market", EditorStyles.miniButton, GUILayout.Width(100)))
            {
                Application.OpenURL("https://blendermarket.com/creators/shugan");
            }
            
            GUI.color = Color.white;
            if (GUILayout.Button("Wiki", EditorStyles.miniButton, GUILayout.Width(50)))
            {
                Application.OpenURL("https://www.notion.so/shugan/FBX-Swapper-253d98525501802ca7c9e7eb7738e0ec");
            }
            
            GUI.color = originalColor;
            EditorGUILayout.EndHorizontal();
        }
        
        void DrawCreditsSection()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            
            var style = new GUIStyle(EditorStyles.miniLabel);
            style.normal.textColor = Color.gray;
            style.alignment = TextAnchor.MiddleCenter;
            
            GUILayout.Label("FBX Swapper - Created by Shugan", style);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
        
        void DrawWarningSection()
        {
            showWarning = EditorGUILayout.Foldout(showWarning, "ℹ️ Important Information", true);
            
            if (showWarning)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "✅ SAFE TO USE: This tool never modifies your original prefabs, scene objects, or FBX files. It only creates new prefab variants with swapped models.\n\n" +
                    "This tool works best with clean Unity projects. For complex custom setups, use the preset model types and rebuild around them. However, if you feel lucky, you can try creating a prefab directly from your custom avatar in the scene.\n\n" +
                    "Need help? Check Discord or Wiki above!", 
                    MessageType.Info);
                EditorGUI.indentLevel--;
            }
        }
        
        void DrawModelSelectionSection()
        {
            EditorGUILayout.LabelField("1. Choose Model Preset", EditorStyles.boldLabel);
            
            var currentPreset = modelPresets[selectedModelIndex];
            bool hasManualSelections = manualTargetSelection || manualNewFbxSelection || manualOldFbxSelection;
            
            if (hasManualSelections && currentPreset.name != "Custom")
            {
                var originalColor = GUI.color;
                GUI.color = Color.yellow;
                EditorGUILayout.HelpBox("⚠️ Manual selections detected! Consider switching to 'Custom' mode for clarity.", MessageType.Warning);
                GUI.color = originalColor;
            }
            
            string[] modelNames = modelPresets.Select(m => m.name).ToArray();
            int newSelectedModel = EditorGUILayout.Popup("Model Type", selectedModelIndex, modelNames);
            
            if (newSelectedModel != selectedModelIndex)
            {
                selectedModelIndex = newSelectedModel;
                selectedPrefabIndex = 0;
                
                manualTargetSelection = false;
                manualNewFbxSelection = false;
                manualOldFbxSelection = false;
                
                AutoFillFields();
            }
            
            if (!string.IsNullOrEmpty(currentPreset.description))
            {
                EditorGUILayout.HelpBox(currentPreset.description, MessageType.Info);
            }
            
            if (currentPreset.prefabPaths.Length > 1 && !manualTargetSelection)
            {
                EditorGUILayout.Space(5);
                
                EditorGUI.BeginDisabledGroup(manualTargetSelection);
                int newSelectedPrefab = EditorGUILayout.Popup("Prefab Variant", selectedPrefabIndex, currentPreset.prefabDisplayNames);
                EditorGUI.EndDisabledGroup();
                
                if (newSelectedPrefab != selectedPrefabIndex && !manualTargetSelection)
                {
                    selectedPrefabIndex = newSelectedPrefab;
                    AutoFillFields();
                }
                
                if (manualTargetSelection)
                {
                    EditorGUILayout.HelpBox("Prefab variant disabled - using manually selected target", MessageType.None);
                }
            }
        }
        
        void DrawTargetPrefabSection()
        {
            EditorGUILayout.LabelField("2. Target Prefab/Scene Object", EditorStyles.boldLabel);
            
            var currentPreset = modelPresets[selectedModelIndex];
            bool isCustom = currentPreset.name == "Custom";
            
            if (isCustom || manualTargetSelection)
            {
                EditorGUILayout.HelpBox(
                    "• Drag your PREFAB here to swap FBX in an existing prefab\n" +
                    "• OR drag your SCENE OBJECT here to create a new prefab with swapped FBX\n" +
                    "• Scene objects will be converted to prefabs automatically", 
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Auto-filled with {currentPreset.name} prefab. You can:\n" +
                    "• Keep the auto-selected prefab, OR\n" +
                    "• Drag your customized scene object here to preserve your modifications\n" +
                    "• Scene objects will create new prefabs with your changes preserved", 
                    MessageType.Info);
            }
            
            GameObject newTargetPrefab = (GameObject)EditorGUILayout.ObjectField(
                "Target (Prefab/Scene Obj)", targetPrefab, typeof(GameObject), true);
            
            if (newTargetPrefab != targetPrefab)
            {
                targetPrefab = newTargetPrefab;
                
                if (targetPrefab != null)
                {
                    targetPrefabWarning = "";
                }
                
                bool isManualSelection = true;
                if (!isCustom && currentPreset.prefabPaths.Length > selectedPrefabIndex)
                {
                    string expectedPrefabPath = currentPreset.prefabPaths[selectedPrefabIndex];
                    if (targetPrefab != null && AssetDatabase.GetAssetPath(targetPrefab) == expectedPrefabPath)
                    {
                        isManualSelection = false;
                    }
                }
                else if (targetPrefab == null)
                {
                    isManualSelection = false;
                }
                
                if (isManualSelection && targetPrefab != null)
                {
                    manualTargetSelection = true;
                    if (selectedModelIndex != 0)
                    {
                        selectedModelIndex = 0;
                        selectedPrefabIndex = 0;
                        Debug.Log("Switched to Custom mode due to manual target selection");
                    }
                }
                else if (targetPrefab == null)
                {
                    manualTargetSelection = false;
                }
            }
            
            if (targetPrefab != null)
            {
                bool isSceneObject = !AssetDatabase.Contains(targetPrefab);
                var originalColor = GUI.color;
                
                if (isSceneObject)
                {
                    GUI.color = Color.yellow;
                    EditorGUILayout.HelpBox("🎯 Scene Object Selected", MessageType.None);
                }
                else
                {
                    GUI.color = Color.green;
                    string statusMessage = manualTargetSelection ? 
                        "📁 Custom Prefab Selected" : 
                        "📁 Default Prefab Selected. To convert your custom model, drag it in the Target!";
                    EditorGUILayout.HelpBox(statusMessage, MessageType.None);
                }
                
                GUI.color = originalColor;
            }
            
            if (!string.IsNullOrEmpty(targetPrefabWarning) && !manualTargetSelection)
            {
                EditorGUILayout.HelpBox($"⚠️ {targetPrefabWarning}", MessageType.Warning);
            }
        }
        
        void DrawFBXSelectionSection()
        {
            EditorGUILayout.LabelField("3. FBX Models", EditorStyles.boldLabel);
            
            var currentPreset = modelPresets[selectedModelIndex];
            bool isCustom = currentPreset.name == "Custom";
            bool hasManualFbxSelections = manualNewFbxSelection || manualOldFbxSelection;
            
            if (isCustom || hasManualFbxSelections)
            {
                EditorGUILayout.HelpBox(
                    "Select which FBX models to swap:\n" +
                    "• New FBX: The model you want to use (with new features/fixes)\n" +
                    "• Old FBX: The model to replace (currently in your prefab)", 
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Auto-filled with {currentPreset.name} FBX files. The swap will replace the old model with the new one while keeping all your materials, animations, and settings.", 
                    MessageType.Info);
            }

            GameObject newNewFbxModel = (GameObject)EditorGUILayout.ObjectField(
                "New FBX Model", newFbxModel, typeof(GameObject), false);

            if (newNewFbxModel != newFbxModel)
            {
                newFbxModel = newNewFbxModel;
                
                if (newFbxModel != null)
                {
                    newFbxWarning = "";
                }
                
                bool isManualSelection = true;
                if (!isCustom && !string.IsNullOrEmpty(currentPreset.newFbxPath))
                {
                    if (newFbxModel != null && AssetDatabase.GetAssetPath(newFbxModel) == currentPreset.newFbxPath)
                    {
                        isManualSelection = false;
                    }
                }
                else if (newFbxModel == null)
                {
                    isManualSelection = false;
                }
                
                if (isManualSelection && newFbxModel != null)
                {
                    manualNewFbxSelection = true;
                    if (selectedModelIndex != 0)
                    {
                        selectedModelIndex = 0;
                        selectedPrefabIndex = 0;
                        Debug.Log("Switched to Custom mode due to manual new FBX selection");
                    }
                }
                else if (newFbxModel == null)
                {
                    manualNewFbxSelection = false;
                }
            }

            GameObject newOldFbxToReplace = (GameObject)EditorGUILayout.ObjectField(
                "Old FBX to Replace", oldFbxToReplace, typeof(GameObject), false);

            if (newOldFbxToReplace != oldFbxToReplace)
            {
                oldFbxToReplace = newOldFbxToReplace;
                
                if (oldFbxToReplace != null)
                {
                    oldFbxWarning = "";
                }
                
                bool isManualSelection = true;
                if (!isCustom && !string.IsNullOrEmpty(currentPreset.oldFbxPath))
                {
                    if (oldFbxToReplace != null && AssetDatabase.GetAssetPath(oldFbxToReplace) == currentPreset.oldFbxPath)
                    {
                        isManualSelection = false;
                    }
                }
                else if (oldFbxToReplace == null)
                {
                    isManualSelection = false;
                }
                
                if (isManualSelection && oldFbxToReplace != null)
                {
                    manualOldFbxSelection = true;
                    if (selectedModelIndex != 0)
                    {
                        selectedModelIndex = 0;
                        selectedPrefabIndex = 0;
                        Debug.Log("Switched to Custom mode due to manual old FBX selection");
                    }
                }
                else if (oldFbxToReplace == null)
                {
                    manualOldFbxSelection = false;
                }
            }
            
            if (!string.IsNullOrEmpty(newFbxWarning) && !manualNewFbxSelection)
            {
                EditorGUILayout.HelpBox($"⚠️ New FBX: {newFbxWarning}", MessageType.Warning);
            }

            if (!string.IsNullOrEmpty(oldFbxWarning) && !manualOldFbxSelection)
            {
                EditorGUILayout.HelpBox($"⚠️ Old FBX: {oldFbxWarning}", MessageType.Warning);
            }
            
            if (!isCustom && !hasManualFbxSelections)
            {
                if (newFbxModel == null && !string.IsNullOrEmpty(currentPreset.newFbxPath))
                {
                    EditorGUILayout.HelpBox($"⚠️ New FBX not found: {currentPreset.newFbxPath}", MessageType.Warning);
                }
                
                if (oldFbxToReplace == null && !string.IsNullOrEmpty(currentPreset.oldFbxPath))
                {
                    EditorGUILayout.HelpBox($"⚠️ Old FBX not found: {currentPreset.oldFbxPath}", MessageType.Warning);
                }
            }
        }
        
        void DrawScenePrefabSection()
        {
            bool isSceneObject = targetPrefab != null && !AssetDatabase.Contains(targetPrefab);
            if (isSceneObject)
            {
                EditorGUILayout.LabelField("4. Scene Object Settings", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Scene object detected! A temporary prefab will be created for processing, then deleted after the FBX swap is complete.", 
                    MessageType.Info);
                
                EditorGUILayout.LabelField("Temporary Prefab Folder:", EditorStyles.boldLabel);
                scenePrefabFolder = EditorGUILayout.TextField("Folder Path", scenePrefabFolder);
                
                if (GUILayout.Button("📁 Select Folder"))
                {
                    string selectedPath = EditorUtility.OpenFolderPanel("Select Destination Folder", "Assets", "");
                    if (!string.IsNullOrEmpty(selectedPath))
                    {
                        if (selectedPath.StartsWith(Application.dataPath))
                        {
                            scenePrefabFolder = "Assets" + selectedPath.Substring(Application.dataPath.Length);
                        }
                    }
                }
            }
        }
        
        void DrawAdvancedInformation()
        {
            showAdvancedInformation = EditorGUILayout.Foldout(showAdvancedInformation, "🔧 Advanced Information", true);
            
            if (showAdvancedInformation)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "Advanced Information:\n" +
                    "• All components, materials, and blendshape values will be preserved\n" +
                    "• Bone constraints and animations will be automatically remapped\n" +
                    "• The process creates a new prefab with '_OldFBX-to-NewFBX' suffix\n" +
                    "• Original prefabs are never modified", 
                    MessageType.Info);
                EditorGUI.indentLevel--;
            }
        }
        
        void DrawGenerationOptionsSection()
        {
            EditorGUILayout.LabelField("4. Generation Options", EditorStyles.boldLabel);
            
            addToSceneAfterCreation = EditorGUILayout.Toggle(
                new GUIContent("Add to Scene After Creation", 
                "When enabled, the generated prefab will be automatically added to the current scene"),
                addToSceneAfterCreation);
            
            if (addToSceneAfterCreation)
            {
                EditorGUILayout.HelpBox(
                    "✓ The new prefab will be added to the scene after creation for immediate preview", 
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "The new prefab will only be created in the Project window", 
                    MessageType.None);
            }
        }
        
        void DrawGenerateButton()
        {
            EditorGUILayout.LabelField("5. Generate New Prefab", EditorStyles.boldLabel);
            
            bool canGenerate = targetPrefab != null && newFbxModel != null && oldFbxToReplace != null;
            
            if (!canGenerate)
            {
                if (targetPrefab == null)
                    EditorGUILayout.HelpBox("❌ Please select a target prefab or scene object", MessageType.Error);
                if (newFbxModel == null)
                    EditorGUILayout.HelpBox("❌ Please select the new FBX model", MessageType.Error);
                if (oldFbxToReplace == null)
                    EditorGUILayout.HelpBox("❌ Please select the old FBX model to replace", MessageType.Error);
            }
            else
            {
                EditorGUILayout.HelpBox("✅ Ready to generate! This will create a new prefab with the FBX swapped.", MessageType.Info);
            }

            EditorGUI.BeginDisabledGroup(!canGenerate);
            if (GUILayout.Button("🚀 Generate FBX-Swapped Prefab", GUILayout.Height(35)))
            {
                CreateAlternativePrefab();
            }
            EditorGUI.EndDisabledGroup();
            
            if (canGenerate)
            {
                EditorGUILayout.Space(5);
                
                bool isSceneObject = targetPrefab != null && !AssetDatabase.Contains(targetPrefab);
                string tipMessage;
                
                if (isSceneObject)
                {
                    tipMessage = $"💡 Tip: The new prefab will be created in {scenePrefabFolder} and automatically selected.";
                }
                else
                {
                    tipMessage = "💡 Tip: The new prefab will be created in the same folder as your target prefab and automatically selected.";
                }
                
                EditorGUILayout.HelpBox(tipMessage, MessageType.Info);
            }
        }
        
        #endregion
        
        #region Helper Methods
        
        void AutoFillFields()
        {
            var currentPreset = modelPresets[selectedModelIndex];
            
            targetPrefabWarning = "";
            newFbxWarning = "";
            oldFbxWarning = "";
            
            if (currentPreset.name == "Custom")
            {
                if (!manualTargetSelection) targetPrefab = null;
                if (!manualNewFbxSelection) newFbxModel = null;
                if (!manualOldFbxSelection) oldFbxToReplace = null;
                return;
            }
            
            if (!manualTargetSelection)
            {
                if (currentPreset.prefabPaths.Length > selectedPrefabIndex)
                {
                    string prefabPath = currentPreset.prefabPaths[selectedPrefabIndex];
                    targetPrefab = FindAssetByName(Path.GetFileName(prefabPath), prefabPath, out targetPrefabWarning);
                }
                else
                {
                    targetPrefab = null;
                }
            }
            
            if (!manualNewFbxSelection)
            {
                if (!string.IsNullOrEmpty(currentPreset.newFbxPath))
                {
                    newFbxModel = FindAssetByName(Path.GetFileName(currentPreset.newFbxPath), currentPreset.newFbxPath, out newFbxWarning);
                }
                else
                {
                    newFbxModel = null;
                }
            }
            
            if (!manualOldFbxSelection)
            {
                if (!string.IsNullOrEmpty(currentPreset.oldFbxPath))
                {
                    oldFbxToReplace = FindAssetByName(Path.GetFileName(currentPreset.oldFbxPath), currentPreset.oldFbxPath, out oldFbxWarning);
                }
                else
                {
                    oldFbxToReplace = null;
                }
            }
            
            Repaint();
        }
        
        GameObject FindAssetByName(string assetName, string expectedPath, out string warningMessage)
        {
            warningMessage = "";
            
            if (File.Exists(expectedPath))
            {
                return AssetDatabase.LoadAssetAtPath<GameObject>(expectedPath);
            }
            
            string[] guids = AssetDatabase.FindAssets($"{Path.GetFileNameWithoutExtension(assetName)} t:GameObject");
            
            if (guids.Length == 0)
            {
                warningMessage = $"Asset '{assetName}' not found in project";
                Debug.LogWarning(warningMessage);
                return null;
            }
            
            List<(GameObject asset, string path, System.DateTime modified)> foundAssets = new List<(GameObject, string, System.DateTime)>();
            
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                
                if (asset != null && asset.name == Path.GetFileNameWithoutExtension(assetName))
                {
                    var fileInfo = new System.IO.FileInfo(path);
                    foundAssets.Add((asset, path, fileInfo.LastWriteTime));
                }
            }
            
            if (foundAssets.Count == 0)
            {
                warningMessage = $"No valid GameObject found for '{assetName}'";
                Debug.LogWarning(warningMessage);
                return null;
            }
            
            foundAssets.Sort((a, b) => b.modified.CompareTo(a.modified));
            
            var mostRecent = foundAssets[0];
            
            if (mostRecent.path != expectedPath)
            {
                if (foundAssets.Count > 1)
                {
                    warningMessage = $"Found {foundAssets.Count} copies, using most recent from: {mostRecent.path}";
                }
                else
                {
                    warningMessage = $"Found at different location: {mostRecent.path}";
                }
                
                Debug.LogWarning($"Asset '{assetName}' not found at expected path '{expectedPath}'");
                Debug.LogWarning($"Found at: '{mostRecent.path}' (modified: {mostRecent.modified})");
                
                if (foundAssets.Count > 1)
                {
                    Debug.LogWarning($"Multiple assets found with name '{assetName}'. Using most recent from: {mostRecent.path}");
                }
            }
            
            return mostRecent.asset;
        }
        
        #endregion
        
        #region FBX Swapping Core Logic
        
        void CreateAlternativePrefab()
        {
            GameObject workingPrefab = targetPrefab;
            string workingPrefabPath = "";
            bool isSceneObject = !AssetDatabase.Contains(targetPrefab);
            bool createdTempPrefab = false;

            try
            {
                if (isSceneObject)
                {
                    workingPrefab = HandleSceneObject();
                    if (workingPrefab == null) return;
                    workingPrefabPath = AssetDatabase.GetAssetPath(workingPrefab);
                    createdTempPrefab = true;
                }
                else
                {
                    workingPrefabPath = AssetDatabase.GetAssetPath(targetPrefab);
                }

                string folder = Path.GetDirectoryName(workingPrefabPath);
                string baseName = Path.GetFileNameWithoutExtension(workingPrefabPath);
                string oldFbxName = oldFbxToReplace.name;
                string newFbxName = newFbxModel.name;
                
                string newName = $"{baseName}_{oldFbxName}-to-{newFbxName}";
                string outPath = Path.Combine(folder, newName + ".prefab");

                if (File.Exists(outPath))
                {
                    AssetDatabase.DeleteAsset(outPath);
                    Debug.Log($"Replaced existing FBX-swapped prefab: {outPath}");
                }

                Debug.Log($"Creating FBX-swapped prefab: {newName}");

                var originalRoot = PrefabUtility.LoadPrefabContents(workingPrefabPath);

                string newFbxPath = AssetDatabase.GetAssetPath(newFbxModel);
                var newModelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(newFbxPath);
                var newRoot = (GameObject)PrefabUtility.InstantiatePrefab(newModelAsset);
                newRoot.name = originalRoot.name;

                HashSet<string> oldFbxMeshNames = new HashSet<string>();
                BuildOldFbxMeshList(oldFbxToReplace, oldFbxMeshNames);
                Debug.Log($"Will replace meshes from old FBX: {string.Join(", ", oldFbxMeshNames)}");

                HashSet<string> newFbxMeshNames = new HashSet<string>();
                BuildOldFbxMeshList(newFbxModel, newFbxMeshNames);
                Debug.Log($"New FBX contains meshes: {string.Join(", ", newFbxMeshNames)}");

                var newHierarchyLookup = BuildHierarchyLookup(newRoot.transform);

                CopyRecursively(originalRoot.transform, newRoot.transform, newHierarchyLookup, oldFbxMeshNames, newFbxMeshNames);

                ValidateMeshVisibility(newRoot.transform);
                ValidateAndFixBoneReferences(newRoot.transform, newHierarchyLookup);
                
                ComparePrefabStates(originalRoot.transform, newRoot.transform);

                var savedPrefab = PrefabUtility.SaveAsPrefabAssetAndConnect(
                    newRoot, outPath, InteractionMode.UserAction);

                GameObject sceneInstance = null;
                if (addToSceneAfterCreation)
                {
                    sceneInstance = (GameObject)PrefabUtility.InstantiatePrefab(savedPrefab);
                    
                    if (isSceneObject && targetPrefab != null)
                    {
                        sceneInstance.transform.position = targetPrefab.transform.position + Vector3.left * 1f;
                        sceneInstance.transform.rotation = targetPrefab.transform.rotation;
                        sceneInstance.transform.localScale = targetPrefab.transform.localScale;
                    }
                    
                    Debug.Log($"Added FBX-swapped prefab to scene: {sceneInstance.name}");
                }

                PrefabUtility.UnloadPrefabContents(originalRoot);
                DestroyImmediate(newRoot);
                AssetDatabase.Refresh();

                Debug.Log($"[FBX Swapper] Created FBX-swapped prefab at:\n{outPath}");
                
                if (addToSceneAfterCreation && sceneInstance != null)
                {
                    Selection.activeObject = sceneInstance;
                    EditorGUIUtility.PingObject(sceneInstance);
                }
                else
                {
                    var newPrefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(outPath);
                    EditorGUIUtility.PingObject(newPrefabAsset);
                    Selection.activeObject = newPrefabAsset;
                }
                
                string dialogMessage = $"Successfully swapped '{oldFbxName}' with '{newFbxName}'\n\n" +
                                      $"New prefab: {newName}.prefab\n\n";
                                      
                if (addToSceneAfterCreation)
                {
                    dialogMessage += "The prefab has been added to the scene and selected.";
                }
                else
                {
                    dialogMessage += "The new prefab has been selected in the Project window.";
                }
                
                EditorUtility.DisplayDialog(
                    "FBX Swap Complete! 🎉",
                    dialogMessage,
                    "Awesome!");
            }
            finally
            {
                if (createdTempPrefab && !string.IsNullOrEmpty(workingPrefabPath))
                {
                    AssetDatabase.DeleteAsset(workingPrefabPath);
                    Debug.Log($"Cleaned up temporary prefab: {workingPrefabPath}");
                }
            }
        }
        
        GameObject HandleSceneObject()
        {
            if (!Directory.Exists(scenePrefabFolder))
            {
                Directory.CreateDirectory(scenePrefabFolder);
                AssetDatabase.Refresh();
            }

            GameObject duplicate = Instantiate(targetPrefab);
            duplicate.name = targetPrefab.name;
            
            string tempPrefabName = $"{targetPrefab.name}.prefab";
            string tempPrefabPath = Path.Combine(scenePrefabFolder, tempPrefabName);
            
            try
            {
                if (File.Exists(tempPrefabPath))
                {
                    AssetDatabase.DeleteAsset(tempPrefabPath);
                    Debug.Log($"Replaced existing prefab: {tempPrefabPath}");
                }
                
                GameObject prefabAsset = PrefabUtility.SaveAsPrefabAssetAndConnect(duplicate, tempPrefabPath, InteractionMode.UserAction);
                
                Debug.Log($"Created prefab for scene object: {tempPrefabPath}");
                
                DestroyImmediate(duplicate);
                
                return prefabAsset;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to create prefab: {e.Message}");
                if (duplicate != null)
                    DestroyImmediate(duplicate);
                return null;
            }
        }
        
        void BuildOldFbxMeshList(GameObject oldFbx, HashSet<string> meshNames)
        {
            var renderers = oldFbx.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                meshNames.Add(renderer.name);
            }
            
            Debug.Log($"Found {meshNames.Count} meshes in FBX '{oldFbx.name}': {string.Join(", ", meshNames)}");
        }
        
        Dictionary<string, Transform> BuildHierarchyLookup(Transform root)
        {
            var lookup = new Dictionary<string, Transform>();
            
            void AddToLookup(Transform t)
            {
                string path = GetTransformPath(t, root);
                if (!lookup.ContainsKey(t.name))
                {
                    lookup[t.name] = t;
                }
                lookup[path] = t;
                
                foreach (Transform child in t)
                {
                    AddToLookup(child);
                }
            }
            
            AddToLookup(root);
            return lookup;
        }
        
        string GetTransformPath(Transform t, Transform root)
        {
            if (t == root) return t.name;
            return GetTransformPath(t.parent, root) + "/" + t.name;
        }
        
        Transform FindTransformByName(string name, Dictionary<string, Transform> lookup, Transform fallbackRoot)
        {
            if (lookup.TryGetValue(name, out Transform result))
            {
                return result;
            }
            
            return FindTransformRecursive(fallbackRoot, name);
        }
        
        Transform FindTransformRecursive(Transform parent, string name)
        {
            if (parent.name == name)
                return parent;
            
            foreach (Transform child in parent)
            {
                Transform found = FindTransformRecursive(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }
        
        enum MeshPreservationStrategy
        {
            Replace,
            UseNewFBX,
            PreserveCompletely,
            ProcessNormally
        }
        
        MeshPreservationStrategy GetMeshPreservationStrategy(Transform srcTransform, HashSet<string> oldFbxMeshNames, HashSet<string> newFbxMeshNames)
        {
            string meshName = srcTransform.name;
            
            if (oldFbxMeshNames.Contains(meshName))
            {
                return MeshPreservationStrategy.Replace;
            }
            
            if (newFbxMeshNames.Contains(meshName))
            {
                return MeshPreservationStrategy.UseNewFBX;
            }
            
            var smr = srcTransform.GetComponent<SkinnedMeshRenderer>();
            var mr = srcTransform.GetComponent<MeshRenderer>();
            
            if (smr != null || mr != null)
            {
                return MeshPreservationStrategy.PreserveCompletely;
            }
            
            return MeshPreservationStrategy.ProcessNormally;
        }
        
        void CopyRecursively(Transform src, Transform dst, Dictionary<string, Transform> newHierarchyLookup, HashSet<string> oldFbxMeshNames, HashSet<string> newFbxMeshNames)
        {
            MeshPreservationStrategy strategy = GetMeshPreservationStrategy(src, oldFbxMeshNames, newFbxMeshNames);
            
            Debug.Log($"Processing {src.name} with strategy: {strategy}");
            
            var srcSMR = src.GetComponent<SkinnedMeshRenderer>();
            var dstSMR = dst.GetComponent<SkinnedMeshRenderer>();
            
            if (srcSMR != null)
            {
                switch (strategy)
                {
                    case MeshPreservationStrategy.Replace:
                        HandleSMRReplacement(srcSMR, dstSMR, dst, newHierarchyLookup);
                        break;
                        
                    case MeshPreservationStrategy.UseNewFBX:
                        HandleSMRNewFBXVersion(srcSMR, dstSMR, dst, newHierarchyLookup);
                        break;
                        
                    case MeshPreservationStrategy.PreserveCompletely:
                        HandleSMRPreservation(srcSMR, dst);
                        break;
                        
                    case MeshPreservationStrategy.ProcessNormally:
                        if (dstSMR != null)
                        {
                            HandleSMRNewFBXVersion(srcSMR, dstSMR, dst, newHierarchyLookup);
                        }
                        break;
                }
            }

            var srcMR = src.GetComponent<MeshRenderer>();
            var dstMR = dst.GetComponent<MeshRenderer>();
            
            if (srcMR != null)
            {
                switch (strategy)
                {
                    case MeshPreservationStrategy.Replace:
                        HandleMRReplacement(srcMR, dstMR, dst, newHierarchyLookup);
                        break;
                        
                    case MeshPreservationStrategy.UseNewFBX:
                        HandleMRNewFBXVersion(srcMR, dstMR, dst, newHierarchyLookup);
                        break;
                        
                    case MeshPreservationStrategy.PreserveCompletely:
                        HandleMRPreservation(srcMR, dst);
                        break;
                        
                    case MeshPreservationStrategy.ProcessNormally:
                        if (dstMR != null)
                        {
                            HandleMRNewFBXVersion(srcMR, dstMR, dst, newHierarchyLookup);
                        }
                        break;
                }
            }

            foreach (var srcComp in src.GetComponents<Component>())
            {
                if (srcComp is Transform || 
                    srcComp is SkinnedMeshRenderer || 
                    srcComp is MeshRenderer || 
                    srcComp is MeshFilter)
                    continue;

                System.Type compType = srcComp.GetType();
                var dstComp = dst.GetComponent(compType);

                try
                {
                    ComponentUtility.CopyComponent(srcComp);

                    if (dstComp != null)
                    {
                        ComponentUtility.PasteComponentValues(dstComp);
                        Debug.Log($"Updated {compType.Name} on {dst.name}");
                    }
                    else
                    {
                        ComponentUtility.PasteComponentAsNew(dst.gameObject);
                        dstComp = dst.GetComponent(compType);
                        Debug.Log($"Added {compType.Name} to {dst.name}");
                    }

                    if (dstComp != null)
                    {
                        FixConstraintSources(dstComp, newHierarchyLookup, dst.root);
                        FixObjectReferences(dstComp, newHierarchyLookup, dst.root);
                        FixAnimatorAvatar(dstComp, dst.root);
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"Failed to copy {compType.Name} from {src.name} to {dst.name}: {e.Message}");
                }
            }

            dst.localPosition = src.localPosition;
            dst.localRotation = src.localRotation;
            dst.localScale = src.localScale;

            foreach (Transform childSrc in src)
            {
                var childDst = dst.Find(childSrc.name);
                
                if (childDst == null)
                {
                    var go = new GameObject(childSrc.name);
                    childDst = go.transform;
                    childDst.SetParent(dst, worldPositionStays: false);
                    
                    go.layer = childSrc.gameObject.layer;
                    go.tag = childSrc.gameObject.tag;
                    go.SetActive(childSrc.gameObject.activeSelf);
                    
                    Debug.Log($"Created missing child '{childSrc.name}' under {dst.name}");
                }
                else
                {
                    childDst.gameObject.layer = childSrc.gameObject.layer;
                    childDst.gameObject.tag = childSrc.gameObject.tag;
                    childDst.gameObject.SetActive(childSrc.gameObject.activeSelf);
                }
                
                CopyRecursively(childSrc, childDst, newHierarchyLookup, oldFbxMeshNames, newFbxMeshNames);
            }
        }
        
        #endregion
        
        #region SkinnedMeshRenderer Handling
        
        void HandleSMRReplacement(SkinnedMeshRenderer srcSMR, SkinnedMeshRenderer dstSMR, Transform dst, Dictionary<string, Transform> newHierarchyLookup)
        {
            if (dstSMR == null) 
                dstSMR = dst.gameObject.AddComponent<SkinnedMeshRenderer>();

            Transform[] originalNewBones = dstSMR.bones;
            Transform originalNewRootBone = dstSMR.rootBone;
            Mesh originalNewMesh = dstSMR.sharedMesh;
            
            Debug.Log($"=== REPLACING SMR on {dst.name} ===");
            Debug.Log($"NEW FBX original bones count: {(originalNewBones != null ? originalNewBones.Length : 0)}");
            Debug.Log($"NEW FBX rootBone: {(originalNewRootBone?.name ?? "null")}");
            
            dstSMR.sharedMaterials = srcSMR.sharedMaterials;
            dstSMR.sharedMesh = originalNewMesh;
            
            if (originalNewBones != null && originalNewBones.Length > 0 && originalNewRootBone != null)
            {
                dstSMR.bones = originalNewBones;
                dstSMR.rootBone = originalNewRootBone;
                Debug.Log($"Using NEW FBX bone setup with {originalNewBones.Length} bones");
            }
            else
            {
                Debug.LogWarning($"New FBX bone setup appears invalid, attempting to remap original bones");
                
                if (srcSMR.bones != null && srcSMR.bones.Length > 0)
                {
                    Transform[] remappedBones = new Transform[srcSMR.bones.Length];
                    int successfulRemaps = 0;
                    
                    for (int i = 0; i < srcSMR.bones.Length; i++)
                    {
                        if (srcSMR.bones[i] != null)
                        {
                            Transform foundBone = FindTransformByName(srcSMR.bones[i].name, newHierarchyLookup, dst.root);
                            if (foundBone != null)
                            {
                                remappedBones[i] = foundBone;
                                successfulRemaps++;
                            }
                        }
                    }
                    
                    if (successfulRemaps >= srcSMR.bones.Length * 0.5f)
                    {
                        dstSMR.bones = remappedBones;
                        Debug.Log($"Used remapped original bones: {successfulRemaps}/{srcSMR.bones.Length} bones found");
                        
                        if (srcSMR.rootBone != null)
                        {
                            Transform foundRootBone = FindTransformByName(srcSMR.rootBone.name, newHierarchyLookup, dst.root);
                            if (foundRootBone != null)
                            {
                                dstSMR.rootBone = foundRootBone;
                            }
                        }
                    }
                }
            }
            
            CopyBlendShapeValues(srcSMR, dstSMR);
            CopySMRProperties(srcSMR, dstSMR, newHierarchyLookup, dst);
            ValidateSMRSetup(dstSMR, dst.name);
        }
        
        void HandleSMRNewFBXVersion(SkinnedMeshRenderer srcSMR, SkinnedMeshRenderer dstSMR, Transform dst, Dictionary<string, Transform> newHierarchyLookup)
        {
            if (dstSMR == null) return;
            
            Transform[] originalNewBones = dstSMR.bones;
            Transform originalNewRootBone = dstSMR.rootBone;
            Mesh originalNewMesh = dstSMR.sharedMesh;
            
            dstSMR.sharedMesh = originalNewMesh;
            dstSMR.sharedMaterials = srcSMR.sharedMaterials;
            
            if (srcSMR.bones != null && srcSMR.bones.Length > 0)
            {
                Transform[] remappedBones = new Transform[srcSMR.bones.Length];
                int successfulRemaps = 0;
                
                for (int i = 0; i < srcSMR.bones.Length; i++)
                {
                    if (srcSMR.bones[i] != null)
                    {
                        Transform foundBone = FindTransformByName(srcSMR.bones[i].name, newHierarchyLookup, dst.root);
                        if (foundBone != null)
                        {
                            remappedBones[i] = foundBone;
                            successfulRemaps++;
                        }
                    }
                }
                
                if (successfulRemaps >= srcSMR.bones.Length * 0.8f)
                {
                    dstSMR.bones = remappedBones;
                    
                    if (srcSMR.rootBone != null)
                    {
                        Transform foundRootBone = FindTransformByName(srcSMR.rootBone.name, newHierarchyLookup, dst.root);
                        dstSMR.rootBone = foundRootBone ?? originalNewRootBone;
                    }
                }
                else
                {
                    dstSMR.bones = originalNewBones;
                    dstSMR.rootBone = originalNewRootBone;
                }
            }
            else
            {
                dstSMR.bones = originalNewBones;
                dstSMR.rootBone = originalNewRootBone;
            }
            
            CopyBlendShapeValues(srcSMR, dstSMR);
            CopySMRProperties(srcSMR, dstSMR, newHierarchyLookup, dst);
            ValidateSMRSetup(dstSMR, dst.name);
        }
        
        void HandleSMRPreservation(SkinnedMeshRenderer srcSMR, Transform dst)
        {
            var existingSMR = dst.GetComponent<SkinnedMeshRenderer>();
            if (existingSMR != null)
            {
                DestroyImmediate(existingSMR);
            }
            
            ComponentUtility.CopyComponent(srcSMR);
            ComponentUtility.PasteComponentAsNew(dst.gameObject);
            
            var newSMR = dst.GetComponent<SkinnedMeshRenderer>();
            if (newSMR != null && srcSMR.bones != null && srcSMR.bones.Length > 0)
            {
                Transform[] remappedBones = new Transform[srcSMR.bones.Length];
                var currentHierarchyLookup = BuildHierarchyLookup(dst.root);
                
                for (int i = 0; i < srcSMR.bones.Length; i++)
                {
                    if (srcSMR.bones[i] != null)
                    {
                        Transform foundBone = FindTransformByName(srcSMR.bones[i].name, currentHierarchyLookup, dst.root);
                        remappedBones[i] = foundBone ?? srcSMR.bones[i];
                    }
                }
                
                newSMR.bones = remappedBones;
                
                if (srcSMR.rootBone != null)
                {
                    Transform foundRootBone = FindTransformByName(srcSMR.rootBone.name, currentHierarchyLookup, dst.root);
                    if (foundRootBone != null)
                    {
                        newSMR.rootBone = foundRootBone;
                    }
                }
                
                ValidateSMRSetup(newSMR, dst.name);
            }
        }
        
        void CopySMRProperties(SkinnedMeshRenderer srcSMR, SkinnedMeshRenderer dstSMR, Dictionary<string, Transform> newHierarchyLookup, Transform dst)
        {
            if (srcSMR.probeAnchor != null)
            {
                Transform foundAnchor = FindTransformByName(srcSMR.probeAnchor.name, newHierarchyLookup, dst.root);
                dstSMR.probeAnchor = foundAnchor;
            }
            else
            {
                dstSMR.probeAnchor = null;
            }

            dstSMR.quality = srcSMR.quality;
            dstSMR.updateWhenOffscreen = srcSMR.updateWhenOffscreen;
            dstSMR.skinnedMotionVectors = srcSMR.skinnedMotionVectors;
            dstSMR.enabled = srcSMR.enabled;
            dstSMR.shadowCastingMode = srcSMR.shadowCastingMode;
            dstSMR.receiveShadows = srcSMR.receiveShadows;
            dstSMR.lightProbeUsage = srcSMR.lightProbeUsage;
            dstSMR.reflectionProbeUsage = srcSMR.reflectionProbeUsage;
            dstSMR.motionVectorGenerationMode = srcSMR.motionVectorGenerationMode;
        }
        
        void ValidateSMRSetup(SkinnedMeshRenderer smr, string objectName)
        {
            if (smr == null || smr.sharedMesh == null) return;
            
            bool hasIssues = false;
            
            if (smr.bones == null || smr.bones.Length == 0)
            {
                Debug.LogError($"SMR on {objectName} has no bones!");
                hasIssues = true;
            }
            else
            {
                int nullBones = smr.bones.Count(b => b == null);
                if (nullBones > 0)
                {
                    Debug.LogWarning($"SMR on {objectName} has {nullBones}/{smr.bones.Length} null bones!");
                    if (nullBones == smr.bones.Length)
                    {
                        hasIssues = true;
                    }
                }
            }
            
            if (smr.rootBone == null)
            {
                Debug.LogError($"SMR on {objectName} has null rootBone!");
                
                if (smr.bones != null && smr.bones.Length > 0)
                {
                    string[] commonRootNames = { "Hips", "Root", "Pelvis", "Spine", "Hip" };
                    
                    foreach (string rootName in commonRootNames)
                    {
                        Transform foundRoot = System.Array.Find(smr.bones, b => b != null && b.name.Contains(rootName));
                        if (foundRoot != null)
                        {
                            smr.rootBone = foundRoot;
                            Debug.Log($"Auto-assigned rootBone for {objectName}: {foundRoot.name}");
                            break;
                        }
                    }
                    
                    if (smr.rootBone == null)
                    {
                        Transform firstBone = System.Array.Find(smr.bones, b => b != null);
                        if (firstBone != null)
                        {
                            smr.rootBone = firstBone;
                        }
                    }
                }
            }
            
            if (smr.localBounds.size.magnitude < 0.001f && smr.sharedMesh.bounds.size.magnitude > 0.001f)
            {
                smr.localBounds = smr.sharedMesh.bounds;
            }
            
            if (!hasIssues)
            {
                Debug.Log($"✓ SMR validation passed for {objectName}");
            }
        }
        
        void CopyBlendShapeValues(SkinnedMeshRenderer srcSMR, SkinnedMeshRenderer dstSMR)
        {
            if (srcSMR == null || dstSMR == null) return;
            
            Mesh srcMesh = srcSMR.sharedMesh;
            Mesh dstMesh = dstSMR.sharedMesh;
            
            if (srcMesh == null || dstMesh == null) return;
            
            int copiedBlendShapes = 0;
            
            for (int srcIndex = 0; srcIndex < srcMesh.blendShapeCount; srcIndex++)
            {
                string srcBlendShapeName = srcMesh.GetBlendShapeName(srcIndex);
                float srcWeight = srcSMR.GetBlendShapeWeight(srcIndex);
                
                if (Mathf.Approximately(srcWeight, 0f)) continue;
                
                for (int dstIndex = 0; dstIndex < dstMesh.blendShapeCount; dstIndex++)
                {
                    string dstBlendShapeName = dstMesh.GetBlendShapeName(dstIndex);
                    
                    if (srcBlendShapeName.Equals(dstBlendShapeName, System.StringComparison.OrdinalIgnoreCase))
                    {
                        dstSMR.SetBlendShapeWeight(dstIndex, srcWeight);
                        copiedBlendShapes++;
                        Debug.Log($"Copied blendshape '{srcBlendShapeName}': {srcWeight}");
                        break;
                    }
                }
            }
            
            Debug.Log($"Successfully copied {copiedBlendShapes} blendshape values");
        }
        
        #endregion
        
        #region MeshRenderer Handling
        
        void HandleMRReplacement(MeshRenderer srcMR, MeshRenderer dstMR, Transform dst, Dictionary<string, Transform> newHierarchyLookup)
        {
            if (dstMR == null) 
                dstMR = dst.gameObject.AddComponent<MeshRenderer>();
            
            CopyMRProperties(srcMR, dstMR, newHierarchyLookup, dst);
        }
        
        void HandleMRNewFBXVersion(MeshRenderer srcMR, MeshRenderer dstMR, Transform dst, Dictionary<string, Transform> newHierarchyLookup)
        {
            if (dstMR == null) return;
            
            CopyMRProperties(srcMR, dstMR, newHierarchyLookup, dst);
        }
        
        void HandleMRPreservation(MeshRenderer srcMR, Transform dst)
        {
            var existingMR = dst.GetComponent<MeshRenderer>();
            var existingMF = dst.GetComponent<MeshFilter>();
            if (existingMR != null) DestroyImmediate(existingMR);
            if (existingMF != null) DestroyImmediate(existingMF);
            
            ComponentUtility.CopyComponent(srcMR);
            ComponentUtility.PasteComponentAsNew(dst.gameObject);
            
            var srcMF = srcMR.GetComponent<MeshFilter>();
            if (srcMF != null)
            {
                ComponentUtility.CopyComponent(srcMF);
                ComponentUtility.PasteComponentAsNew(dst.gameObject);
            }
        }
        
        void CopyMRProperties(MeshRenderer srcMR, MeshRenderer dstMR, Dictionary<string, Transform> newHierarchyLookup, Transform dst)
        {
            dstMR.sharedMaterials = srcMR.sharedMaterials;
            dstMR.enabled = srcMR.enabled;
            dstMR.shadowCastingMode = srcMR.shadowCastingMode;
            dstMR.receiveShadows = srcMR.receiveShadows;
            dstMR.lightProbeUsage = srcMR.lightProbeUsage;
            dstMR.reflectionProbeUsage = srcMR.reflectionProbeUsage;
            dstMR.motionVectorGenerationMode = srcMR.motionVectorGenerationMode;
            dstMR.allowOcclusionWhenDynamic = srcMR.allowOcclusionWhenDynamic;
            dstMR.sortingLayerID = srcMR.sortingLayerID;
            dstMR.sortingOrder = srcMR.sortingOrder;
            
            if (srcMR.probeAnchor != null)
            {
                Transform foundAnchor = FindTransformByName(srcMR.probeAnchor.name, newHierarchyLookup, dst.root);
                dstMR.probeAnchor = foundAnchor;
            }
        }
        
        #endregion
        
        #region Component Fixing
        
        void FixAnimatorAvatar(Component component, Transform newRoot)
        {
            if (component is Animator animator)
            {
                string newFbxPath = AssetDatabase.GetAssetPath(newFbxModel);
                var newFbxAssets = AssetDatabase.LoadAllAssetsAtPath(newFbxPath);
                
                Avatar newAvatar = System.Array.Find(newFbxAssets, asset => asset is Avatar) as Avatar;
                
                if (newAvatar != null)
                {
                    animator.avatar = newAvatar;
                    Debug.Log($"Updated Animator Avatar to '{newAvatar.name}'");
                }
            }
        }
        
        void FixConstraintSources(Component constraint, Dictionary<string, Transform> newHierarchyLookup, Transform newRoot)
        {
            System.Type constraintType = constraint.GetType();
            
            if (constraintType.Name == "PositionConstraint")
            {
                var posConstraint = constraint as PositionConstraint;
                if (posConstraint != null) FixConstraintSourceList(posConstraint, newHierarchyLookup, newRoot);
            }
            else if (constraintType.Name == "RotationConstraint")
            {
                var rotConstraint = constraint as RotationConstraint;
                if (rotConstraint != null) FixConstraintSourceList(rotConstraint, newHierarchyLookup, newRoot);
            }
            else if (constraintType.Name == "ScaleConstraint")
            {
                var scaleConstraint = constraint as ScaleConstraint;
                if (scaleConstraint != null) FixConstraintSourceList(scaleConstraint, newHierarchyLookup, newRoot);
            }
            else if (constraintType.Name == "ParentConstraint")
            {
                var parentConstraint = constraint as ParentConstraint;
                if (parentConstraint != null) FixConstraintSourceList(parentConstraint, newHierarchyLookup, newRoot);
            }
            else if (constraintType.Name == "LookAtConstraint")
            {
                var lookAtConstraint = constraint as LookAtConstraint;
                if (lookAtConstraint != null) FixConstraintSourceList(lookAtConstraint, newHierarchyLookup, newRoot);
            }
            else if (constraintType.Name == "AimConstraint")
            {
                var aimConstraint = constraint as AimConstraint;
                if (aimConstraint != null) FixConstraintSourceList(aimConstraint, newHierarchyLookup, newRoot);
            }
        }
        
        void FixConstraintSourceList(IConstraint constraint, Dictionary<string, Transform> newHierarchyLookup, Transform newRoot)
        {
            var sources = new List<ConstraintSource>();
            
            for (int i = 0; i < constraint.sourceCount; i++)
            {
                var source = constraint.GetSource(i);
                
                if (source.sourceTransform != null)
                {
                    Transform newSourceTransform = FindTransformByName(source.sourceTransform.name, newHierarchyLookup, newRoot);
                    
                    if (newSourceTransform != null)
                    {
                        var newSource = new ConstraintSource
                        {
                            sourceTransform = newSourceTransform,
                            weight = source.weight
                        };
                        sources.Add(newSource);
                    }
                    else
                    {
                        sources.Add(source);
                    }
                }
                else
                {
                    sources.Add(source);
                }
            }
            
            constraint.SetSources(sources);
        }
        
        void FixObjectReferences(Component component, Dictionary<string, Transform> newHierarchyLookup, Transform newRoot)
        {
            if (component == null) return;
            
            SerializedObject serializedObj = new SerializedObject(component);
            SerializedProperty prop = serializedObj.GetIterator();
            
            bool foundReferences = false;
            
            while (prop.NextVisible(true))
            {
                if (prop.propertyType == SerializedPropertyType.ObjectReference && 
                    prop.objectReferenceValue != null)
                {
                    var objRef = prop.objectReferenceValue;
                    
                    if (objRef is Transform oldTransform)
                    {
                        Transform newTransform = FindTransformByName(oldTransform.name, newHierarchyLookup, newRoot);
                        if (newTransform != null && newTransform != oldTransform)
                        {
                            prop.objectReferenceValue = newTransform;
                            foundReferences = true;
                        }
                    }
                    else if (objRef is GameObject oldGameObject)
                    {
                        Transform newTransform = FindTransformByName(oldGameObject.name, newHierarchyLookup, newRoot);
                        if (newTransform != null && newTransform.gameObject != oldGameObject)
                        {
                            prop.objectReferenceValue = newTransform.gameObject;
                            foundReferences = true;
                        }
                    }
                    else if (objRef is Component oldComponent)
                    {
                        Transform newTransform = FindTransformByName(oldComponent.name, newHierarchyLookup, newRoot);
                        if (newTransform != null)
                        {
                            var newComponent = newTransform.GetComponent(oldComponent.GetType());
                            if (newComponent != null && newComponent != oldComponent)
                            {
                                prop.objectReferenceValue = newComponent;
                                foundReferences = true;
                            }
                        }
                    }
                }
            }
            
            if (foundReferences)
            {
                serializedObj.ApplyModifiedProperties();
            }
        }
        
        #endregion
        
        #region Validation Methods
        
        void ValidateMeshVisibility(Transform root)
        {
            Debug.Log("=== MESH VISIBILITY VALIDATION ===");
            
            var allRenderers = root.GetComponentsInChildren<Renderer>();
            foreach (var renderer in allRenderers)
            {
                if (renderer is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh == null || smr.bones == null || smr.bones.Length == 0 || smr.rootBone == null)
                    {
                        Debug.LogWarning($"SkinnedMeshRenderer on {smr.name} may be invisible!");
                    }
                }
                else if (renderer is MeshRenderer mr)
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                    {
                        Debug.LogError($"MeshRenderer on {mr.name} has no valid mesh!");
                    }
                }
            }
        }
        
        void ValidateAndFixBoneReferences(Transform root, Dictionary<string, Transform> hierarchyLookup)
        {
            var allSMRs = root.GetComponentsInChildren<SkinnedMeshRenderer>();
            
            foreach (var smr in allSMRs)
            {
                if (smr.sharedMesh == null) continue;
                
                bool needsFixing = false;
                
                if (smr.rootBone == null || (smr.bones != null && smr.bones.Any(b => b == null)))
                {
                    needsFixing = true;
                }
                
                if (needsFixing)
                {
                    FixSMRBoneReferences(smr, hierarchyLookup, root);
                }
            }
        }
        
        void FixSMRBoneReferences(SkinnedMeshRenderer smr, Dictionary<string, Transform> hierarchyLookup, Transform root)
        {
            var allTransforms = root.GetComponentsInChildren<Transform>();
            var potentialBones = new List<Transform>();
            
            string[] boneKeywords = { "bone", "joint", "spine", "hip", "leg", "arm", "hand", "foot", "finger", "toe", "head", "neck", "shoulder", "elbow", "knee", "ankle", "wrist" };
            
            foreach (var t in allTransforms)
            {
                string nameLower = t.name.ToLower();
                if (boneKeywords.Any(keyword => nameLower.Contains(keyword)))
                {
                    potentialBones.Add(t);
                }
            }
            
            if (smr.bones == null || smr.bones.Length == 0)
            {
                if (potentialBones.Count > 0)
                {
                    smr.bones = potentialBones.ToArray();
                }
                else
                {
                    smr.bones = new Transform[] { root };
                }
            }
            else
            {
                for (int i = 0; i < smr.bones.Length; i++)
                {
                    if (smr.bones[i] == null && i < potentialBones.Count)
                    {
                        smr.bones[i] = potentialBones[i];
                    }
                }
            }
            
            if (smr.rootBone == null)
            {
                string[] commonRootNames = { "hips", "pelvis", "root", "spine", "hip" };
                Transform foundRoot = null;
                
                foreach (string rootName in commonRootNames)
                {
                    foundRoot = potentialBones.Find(t => t.name.ToLower().Contains(rootName));
                    if (foundRoot != null) break;
                }
                
                if (foundRoot != null)
                {
                    smr.rootBone = foundRoot;
                }
                else if (smr.bones != null && smr.bones.Length > 0)
                {
                    smr.rootBone = System.Array.Find(smr.bones, b => b != null);
                }
                else
                {
                    smr.rootBone = root;
                }
            }
            
            ValidateSMRSetup(smr, smr.name);
        }
        
        void ComparePrefabStates(Transform originalRoot, Transform newRoot)
        {
            Debug.Log("=== COMPARING ORIGINAL vs FBX-SWAPPED PREFAB ===");
            
            var originalSMRs = originalRoot.GetComponentsInChildren<SkinnedMeshRenderer>();
            var newSMRs = newRoot.GetComponentsInChildren<SkinnedMeshRenderer>();
            
            Debug.Log($"Original has {originalSMRs.Length} SkinnedMeshRenderers, New has {newSMRs.Length}");
            
            foreach (var origSMR in originalSMRs)
            {
                var newSMR = System.Array.Find(newSMRs, smr => smr.name == origSMR.name);
                if (newSMR == null) continue;
                
                Debug.Log($"Comparing {origSMR.name}:");
                Debug.Log($"  Bones: {(origSMR.bones?.Length ?? 0)} -> {(newSMR.bones?.Length ?? 0)}");
                Debug.Log($"  RootBone: {(origSMR.rootBone?.name ?? "null")} -> {(newSMR.rootBone?.name ?? "null")}");
            }
        }
        
        #endregion
    }
}