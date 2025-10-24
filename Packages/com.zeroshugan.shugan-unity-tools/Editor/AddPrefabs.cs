using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace ShuganTools
{
    public class AddPrefabs : EditorWindow
    {
        #region Variables
        
        // Target object
        private GameObject targetObject;
        private bool autoDetectSelection = true;
        
        // Folder-based prefab selection
        private string prefabsBasePath = "Assets/! Shugan/!_Prefabs/Custom";
        private List<string> availableFolders = new List<string>();
        private int selectedFolderIndex = 0;
        private List<GameObject> availablePrefabs = new List<GameObject>();
        private List<bool> prefabSelectionStates = new List<bool>();
        
        // Manual prefab selection
        private List<GameObject> manualPrefabs = new List<GameObject>();
        private bool showManualPrefabs = true;
        
        // UI scroll positions
        private Vector2 scrollPosition;
        private Vector2 prefabListScrollPosition;
        private Vector2 manualPrefabScrollPosition;
        
        #endregion
        
        #region Window Setup
        
        [MenuItem("Tools/Shugan/Add Prefabs")]
        static void OpenWindow()
        {
            var window = GetWindow<AddPrefabs>("Add Prefabs");
            window.minSize = new Vector2(400, 500);
        }
        
        void OnEnable()
        {
            if (autoDetectSelection && Selection.activeGameObject != null)
            {
                targetObject = Selection.activeGameObject;
            }
            
            RefreshAvailableFolders();
        }
        
        void OnSelectionChange()
        {
            if (autoDetectSelection && Selection.activeGameObject != null)
            {
                targetObject = Selection.activeGameObject;
                Repaint();
            }
        }
        
        #endregion
        
        #region GUI Methods
        
        void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            
            DrawHeader();
            DrawSocialLinksSection();
            EditorGUILayout.Space();
            
            DrawInfoSection();
            EditorGUILayout.Space();
            
            DrawTargetObjectSection();
            EditorGUILayout.Space();
            
            DrawFolderSelectionSection();
            EditorGUILayout.Space();
            
            DrawManualPrefabSection();
            EditorGUILayout.Space();
            
            DrawAddPrefabsButton();
            EditorGUILayout.Space();
            
            DrawCreditsSection();
            
            EditorGUILayout.EndScrollView();
        }
        
        void DrawHeader()
        {
            GUILayout.Label("Add Prefabs to Scene Object", EditorStyles.largeLabel);
            
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
            
            GUILayout.Label("Add Prefabs Tool - Created by Shugan", style);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
        
        void DrawInfoSection()
        {
            EditorGUILayout.HelpBox(
                "This tool helps you quickly add multiple prefabs as children to a selected object in your scene. " +
                "Select a model folder to add all its prefabs, or manually choose specific prefabs to add.",
                MessageType.Info);
        }
        
        void DrawTargetObjectSection()
        {
            EditorGUILayout.LabelField("1. Target Scene Object", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            autoDetectSelection = EditorGUILayout.Toggle("Auto-Detect Selection", autoDetectSelection);
            
            if (GUILayout.Button("Refresh", GUILayout.Width(70)))
            {
                if (Selection.activeGameObject != null)
                {
                    targetObject = Selection.activeGameObject;
                }
            }
            EditorGUILayout.EndHorizontal();
            
            GameObject newTarget = (GameObject)EditorGUILayout.ObjectField(
                "Target Object", targetObject, typeof(GameObject), true);
            
            if (newTarget != targetObject)
            {
                targetObject = newTarget;
            }
            
            if (targetObject != null)
            {
                var originalColor = GUI.color;
                GUI.color = Color.green;
                EditorGUILayout.HelpBox($"✓ Target: {targetObject.name}\nPrefabs will be added as children of this object", MessageType.None);
                GUI.color = originalColor;
            }
            else
            {
                EditorGUILayout.HelpBox("⚠ Please select a target object in the scene", MessageType.Warning);
            }
        }
        
        void DrawFolderSelectionSection()
        {
            EditorGUILayout.LabelField("2. Select Model Folder", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Prefabs Base Path:", GUILayout.Width(120));
            prefabsBasePath = EditorGUILayout.TextField(prefabsBasePath);
            
            var originalButtonColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.7f, 0.9f, 1f); // Light blue
            
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string selectedPath = EditorUtility.OpenFolderPanel("Select Prefabs Base Folder", "Assets", "");
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    if (selectedPath.StartsWith(Application.dataPath))
                    {
                        prefabsBasePath = "Assets" + selectedPath.Substring(Application.dataPath.Length);
                        RefreshAvailableFolders();
                    }
                }
            }
            
            GUI.backgroundColor = new Color(0.9f, 1f, 0.7f); // Light green
            
            if (GUILayout.Button("Refresh", GUILayout.Width(60)))
            {
                RefreshAvailableFolders();
            }
            
            GUI.backgroundColor = originalButtonColor;
            EditorGUILayout.EndHorizontal();
            
            if (availableFolders.Count > 0)
            {
                string[] folderNames = availableFolders.Select(f => Path.GetFileName(f)).ToArray();
                int newSelectedFolder = EditorGUILayout.Popup("Model Folder", selectedFolderIndex, folderNames);
                
                if (newSelectedFolder != selectedFolderIndex)
                {
                    selectedFolderIndex = newSelectedFolder;
                    RefreshAvailablePrefabs();
                    Repaint();
                }
                
                if (availablePrefabs.Count > 0)
                {
                    EditorGUILayout.Space(5);
                    
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"Prefabs in {folderNames[selectedFolderIndex]}:", EditorStyles.boldLabel);
                    
                    if (GUILayout.Button("Select All", GUILayout.Width(80)))
                    {
                        for (int i = 0; i < prefabSelectionStates.Count; i++)
                        {
                            prefabSelectionStates[i] = true;
                        }
                    }
                    
                    if (GUILayout.Button("Deselect All", GUILayout.Width(80)))
                    {
                        for (int i = 0; i < prefabSelectionStates.Count; i++)
                        {
                            prefabSelectionStates[i] = false;
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                    
                    prefabListScrollPosition = EditorGUILayout.BeginScrollView(prefabListScrollPosition, GUILayout.Height(150));
                    
                    for (int i = 0; i < availablePrefabs.Count; i++)
                    {
                        if (availablePrefabs[i] != null)
                        {
                            EditorGUILayout.BeginHorizontal();
                            
                            // Checkbox for selection
                            prefabSelectionStates[i] = EditorGUILayout.Toggle(prefabSelectionStates[i], GUILayout.Width(20));
                            
                            // Prefab object field
                            EditorGUI.BeginDisabledGroup(true); // Make it read-only
                            EditorGUILayout.ObjectField(availablePrefabs[i], typeof(GameObject), false);
                            EditorGUI.EndDisabledGroup();
                            
                            EditorGUILayout.EndHorizontal();
                        }
                    }
                    
                    EditorGUILayout.EndScrollView();
                    
                    int selectedCount = prefabSelectionStates.Count(selected => selected);
                    
                    var originalColor = GUI.color;
                    GUI.color = selectedCount > 0 ? Color.cyan : Color.yellow;
                    EditorGUILayout.HelpBox($"✓ Found {availablePrefabs.Count} prefab(s) - {selectedCount} selected", MessageType.None);
                    GUI.color = originalColor;
                }
                else
                {
                    EditorGUILayout.HelpBox("No prefabs found in selected folder", MessageType.Warning);
                }
            }
            else
            {
                EditorGUILayout.HelpBox($"No model folders found in:\n{prefabsBasePath}\n\nMake sure the path is correct and contains subfolders with prefabs.", MessageType.Warning);
            }
        }
        
        void DrawManualPrefabSection()
        {
            EditorGUILayout.LabelField("3. Manual Prefab Selection (Optional)", EditorStyles.boldLabel);
            
            showManualPrefabs = EditorGUILayout.Foldout(showManualPrefabs, "Manual Prefabs", true);
            
            if (showManualPrefabs)
            {
                EditorGUI.indentLevel++;
                
                EditorGUILayout.HelpBox(
                    "Add individual prefabs here to include them alongside the folder prefabs, or use this exclusively if you don't want to use the folder system.",
                    MessageType.Info);
                
                manualPrefabScrollPosition = EditorGUILayout.BeginScrollView(manualPrefabScrollPosition, GUILayout.Height(150));
                
                for (int i = 0; i < manualPrefabs.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    
                    manualPrefabs[i] = (GameObject)EditorGUILayout.ObjectField(
                        $"Prefab {i + 1}", manualPrefabs[i], typeof(GameObject), false);
                    
                    if (GUILayout.Button("✖", GUILayout.Width(25)))
                    {
                        manualPrefabs.RemoveAt(i);
                        i--;
                    }
                    
                    EditorGUILayout.EndHorizontal();
                }
                
                EditorGUILayout.EndScrollView();
                
                if (GUILayout.Button("+ Add Prefab Slot"))
                {
                    manualPrefabs.Add(null);
                }
                
                if (manualPrefabs.Count > 0)
                {
                    if (GUILayout.Button("Clear All Manual Prefabs"))
                    {
                        if (EditorUtility.DisplayDialog("Clear Manual Prefabs", 
                            "Are you sure you want to clear all manual prefab slots?", "Yes", "No"))
                        {
                            manualPrefabs.Clear();
                        }
                    }
                }
                
                EditorGUI.indentLevel--;
            }
        }
        
        void DrawAddPrefabsButton()
        {
            EditorGUILayout.LabelField("4. Add Prefabs to Scene", EditorStyles.boldLabel);
            
            int selectedFolderPrefabs = 0;
            for (int i = 0; i < prefabSelectionStates.Count; i++)
            {
                if (prefabSelectionStates[i] && i < availablePrefabs.Count && availablePrefabs[i] != null)
                {
                    selectedFolderPrefabs++;
                }
            }
            
            int validManualPrefabs = manualPrefabs.Count(p => p != null);
            int totalPrefabs = selectedFolderPrefabs + validManualPrefabs;
            
            bool canAdd = targetObject != null && totalPrefabs > 0;
            
            if (!canAdd)
            {
                if (targetObject == null)
                    EditorGUILayout.HelpBox("❌ Please select a target object", MessageType.Error);
                if (totalPrefabs == 0)
                    EditorGUILayout.HelpBox("❌ No prefabs selected (check prefabs in folder or add manual prefabs)", MessageType.Error);
            }
            else
            {
                var originalColor = GUI.color;
                GUI.color = Color.green;
                EditorGUILayout.HelpBox(
                    $"✅ Ready to add {totalPrefabs} prefab(s) to '{targetObject.name}'\n" +
                    $"• Selected folder prefabs: {selectedFolderPrefabs}\n" +
                    $"• Manual prefabs: {validManualPrefabs}",
                    MessageType.None);
                GUI.color = originalColor;
            }
            
            EditorGUI.BeginDisabledGroup(!canAdd);
            
            var buttonColor = GUI.backgroundColor;
            GUI.backgroundColor = Color.green;
            
            if (GUILayout.Button($"🎯 Add {totalPrefabs} Prefab(s) to Scene", GUILayout.Height(40)))
            {
                AddPrefabsToTarget();
            }
            
            GUI.backgroundColor = buttonColor;
            EditorGUI.EndDisabledGroup();
        }
        
        #endregion
        
        #region Core Functionality
        
        void RefreshAvailableFolders()
        {
            availableFolders.Clear();
            
            if (!Directory.Exists(prefabsBasePath))
            {
                Debug.LogWarning($"Prefabs base path does not exist: {prefabsBasePath}");
                return;
            }
            
            string[] directories = Directory.GetDirectories(prefabsBasePath);
            availableFolders.AddRange(directories);
            
            if (availableFolders.Count > 0)
            {
                selectedFolderIndex = Mathf.Clamp(selectedFolderIndex, 0, availableFolders.Count - 1);
                RefreshAvailablePrefabs();
            }
            
            Debug.Log($"Found {availableFolders.Count} model folder(s) in {prefabsBasePath}");
        }
        
        void RefreshAvailablePrefabs()
        {
            availablePrefabs.Clear();
            prefabSelectionStates.Clear();
            
            if (selectedFolderIndex < 0 || selectedFolderIndex >= availableFolders.Count)
                return;
            
            string selectedFolder = availableFolders[selectedFolderIndex];
            
            if (!Directory.Exists(selectedFolder))
            {
                Debug.LogWarning($"Selected folder does not exist: {selectedFolder}");
                return;
            }
            
            string[] prefabFiles = Directory.GetFiles(selectedFolder, "*.prefab", SearchOption.TopDirectoryOnly);
            
            foreach (string prefabPath in prefabFiles)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab != null)
                {
                    availablePrefabs.Add(prefab);
                    prefabSelectionStates.Add(true); // All selected by default
                }
            }
            
            Debug.Log($"Found {availablePrefabs.Count} prefab(s) in {selectedFolder}");
        }
        
        void AddPrefabsToTarget()
        {
            if (targetObject == null)
            {
                EditorUtility.DisplayDialog("Error", "No target object selected!", "OK");
                return;
            }
            
            int successCount = 0;
            List<GameObject> addedObjects = new List<GameObject>();
            
            // Add selected folder prefabs only
            for (int i = 0; i < availablePrefabs.Count && i < prefabSelectionStates.Count; i++)
            {
                if (prefabSelectionStates[i] && availablePrefabs[i] != null)
                {
                    GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(availablePrefabs[i], targetObject.transform);
                    if (instance != null)
                    {
                        addedObjects.Add(instance);
                        successCount++;
                        Debug.Log($"Added prefab: {availablePrefabs[i].name} to {targetObject.name}");
                    }
                }
            }
            
            // Add manual prefabs
            foreach (var prefab in manualPrefabs)
            {
                if (prefab != null)
                {
                    GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, targetObject.transform);
                    if (instance != null)
                    {
                        addedObjects.Add(instance);
                        successCount++;
                        Debug.Log($"Added manual prefab: {prefab.name} to {targetObject.name}");
                    }
                }
            }
            
            if (addedObjects.Count > 0)
            {
                foreach (var obj in addedObjects)
                {
                    Undo.RegisterCreatedObjectUndo(obj, "Add Prefabs");
                }
            }
            
            string message = $"Successfully added {successCount} prefab(s) to '{targetObject.name}'";
            
            if (successCount > 0)
            {
                EditorUtility.DisplayDialog("Success! 🎉", message, "Awesome!");
                
                Selection.activeGameObject = targetObject;
                EditorGUIUtility.PingObject(targetObject);
            }
            else
            {
                EditorUtility.DisplayDialog("Warning", "No prefabs were added. Please check your selections.", "OK");
            }
        }
        
        #endregion
    }
}