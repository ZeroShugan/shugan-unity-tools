using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using ZeroShugan.ShuganUnityTools;

namespace ShuganTools
{
    public class AnimationClipBatchEditor : EditorWindow
    {
        #region Constants
        private const string VERSION = "Animation Clip Batch Editor v2.7";
        private const string AUTHOR = "Created by Shugan";
        private const string TOOL_NAME = "Animation Clip Batch Editor";
        private const string TOOL_VERSION = "2.7";
        private const string WIKI_URL = "https://www.notion.so/shugan/Shugan-Unity-Tools";
        #endregion

        #region Enums
        private enum OperationType
        {
            Set,
            Add,
            Subtract,
            Multiply,
            Divide
        }

        private enum Tab
        {
            Source,
            ValueEditor,
            PathEditor,
            AnimatorUtils,
            Settings,
            About
        }

        private enum PathEditMode
        {
            ModifyInPlace,
            CreateDuplicate
        }

        private enum ClipAction
        {
            Replace,
            UseExisting
        }

        private enum LayerPlacement
        {
            BelowOriginal,
            AboveOriginal,
            AtEnd
        }
        #endregion

        #region Fields
        // UI State
        private Tab currentTab = Tab.Source;
        private Vector2 scrollPos;
        private Vector2 clipScrollPos;
        private Vector2 propertyScrollPos;
        private Vector2 pathScrollPos;
        private Vector2 animatorScrollPos;
        private Vector2 previewScrollPos;
        private Vector2 rulesScrollPos;

        // Source Selection
        private AnimatorController selectedAnimator;
        private List<AnimatorControllerLayer> selectedLayers = new List<AnimatorControllerLayer>();
        private Dictionary<string, bool> layerSelectionStates = new Dictionary<string, bool>();
        private Dictionary<string, bool> clipSelectionFromAnimator = new Dictionary<string, bool>();
        private List<AnimationClip> manualClips = new List<AnimationClip>();
        private List<bool> manualClipSelectionStates = new List<bool>();

        // Combined clips (from animator + manual)
        private List<AnimationClip> allClips = new List<AnimationClip>();

        // Detected Properties
        private List<string> detectedProperties = new List<string>();
        private Dictionary<string, List<string>> detectedPropertiesByType = new Dictionary<string, List<string>>();
        private List<bool> propertySelectionStates = new List<bool>();
        private bool needsPropertyRefresh = true;

        // Property Filters
        private string propertySearchFilter = "";
        private bool showTransformOnly = false;
        private bool showMuscleOnly = false;

        // Operation
        private OperationType operationType = OperationType.Set;
        private float operationValue = 0f;

        // Filters
        private int startFrame = 0;
        private int endFrame = 0;
        private bool useFrameRange = false;

        // Path Editor - ENHANCED
        private PathEditMode pathEditMode = PathEditMode.ModifyInPlace;
        private List<FindReplaceRule> findReplaceRules = new List<FindReplaceRule>();
        private bool pathCaseSensitive = true;
        private bool pathWholeWordOnly = false;
        private bool duplicateLayers = false;
        private List<string> detectedPaths = new List<string>();
        private Dictionary<string, List<string>> pathPreviewChanges = new Dictionary<string, List<string>>();
        private Dictionary<AnimationClip, string> clipNamePreview = new Dictionary<AnimationClip, string>();
        private Dictionary<AnimationClip, bool> clipWillBeReused = new Dictionary<AnimationClip, bool>();
        private Dictionary<AnimationClip, ClipAction> clipActions = new Dictionary<AnimationClip, ClipAction>();
        private Dictionary<string, HashSet<AnimationClip>> pathToClipsMap = new Dictionary<string, HashSet<AnimationClip>>();
        private bool needsPathRefresh = true;
        private bool showPreview = false;
        
        // NEW: Layer naming and placement
        private string customLayerSuffix = "_Mirror";
        private LayerPlacement layerPlacement = LayerPlacement.BelowOriginal;
        private bool showLayerNameWarning = false;
        
        // Find/Replace Rule class
        [System.Serializable]
        private class FindReplaceRule
        {
            public string find = "";
            public string replace = "";
            public bool enabled = true;
            public bool isSwap = false; // If true, this rule is part of a bidirectional swap
        }

        // Settings
        private bool createBackups = true;
        private string backupFolderPath = "";
        private bool showDetailedLog = true;
        private bool showDebugLog = false;
        private bool autoRefresh = true;

        // Debug
        private string lastOperationLog = "";
        private int lastModifiedCount = 0;

        // Clip duplication tracking
        private Dictionary<AnimationClip, AnimationClip> clipDuplicationMap = new Dictionary<AnimationClip, AnimationClip>();

        // Parameter mapping for layer duplication
        private Dictionary<string, string> parameterNameMap = new Dictionary<string, string>();

        // Animator Utils tab
        private AnimatorController sourceAnimatorUtils;
        private AnimatorController targetAnimatorUtils;
        private Vector2 utilsScrollPos;
        private Dictionary<string, bool> parameterSelectionStates = new Dictionary<string, bool>();
        private Dictionary<string, bool> layerSelectionStatesUtils = new Dictionary<string, bool>();
        #endregion

        #region Menu
        [MenuItem("Tools/Shugan/Animation Clip Batch Editor", false, 1921)]
        static void Init()
        {
            AnimationClipBatchEditor window = GetWindow<AnimationClipBatchEditor>();
            window.titleContent = new GUIContent("Anim Batch Editor");
            window.minSize = new Vector2(500, 600);
            window.Show();
        }
        #endregion

        #region Unity Lifecycle
        private void OnEnable()
        {
            if (string.IsNullOrEmpty(backupFolderPath))
            {
                backupFolderPath = GetDefaultBackupPath();
            }
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawToolbar();

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            switch (currentTab)
            {
                case Tab.Source:
                    DrawSourceTab();
                    break;
                case Tab.ValueEditor:
                    DrawValueEditorTab();
                    break;
                case Tab.PathEditor:
                    DrawPathEditorTab();
                    break;
                case Tab.AnimatorUtils:
                    DrawAnimatorUtilsTab();
                    break;
                case Tab.Settings:
                    DrawSettingsTab();
                    break;
                case Tab.About:
                    DrawAboutTab();
                    break;
            }

            EditorGUILayout.EndScrollView();

            DrawFooter();
        }
        #endregion

        #region Drawing Methods - Header & Toolbar
        private void DrawHeader()
        {
            ShuganToolUI.DrawHeader(VERSION);
            ShuganToolUI.DrawSocialLinks(WIKI_URL);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Toggle(currentTab == Tab.Source, new GUIContent("Source", "Select clips and layers"), EditorStyles.toolbarButton, GUILayout.Width(60)))
                currentTab = Tab.Source;

            if (GUILayout.Toggle(currentTab == Tab.ValueEditor, new GUIContent("Values", "Edit keyframe values"), EditorStyles.toolbarButton, GUILayout.Width(60)))
                currentTab = Tab.ValueEditor;

            if (GUILayout.Toggle(currentTab == Tab.PathEditor, new GUIContent("Paths", "Edit animation paths"), EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                currentTab = Tab.PathEditor;
                if (needsPathRefresh)
                {
                    RefreshDetectedPaths();
                    needsPathRefresh = false;
                }
            }

            if (GUILayout.Toggle(currentTab == Tab.AnimatorUtils, new GUIContent("Utils", "Animator utilities"), EditorStyles.toolbarButton, GUILayout.Width(60)))
                currentTab = Tab.AnimatorUtils;

            if (GUILayout.Toggle(currentTab == Tab.Settings, new GUIContent("Settings", "Configure tool settings"), EditorStyles.toolbarButton, GUILayout.Width(60)))
                currentTab = Tab.Settings;

            if (GUILayout.Toggle(currentTab == Tab.About, new GUIContent("About", "About this tool"), EditorStyles.toolbarButton, GUILayout.Width(60)))
                currentTab = Tab.About;

            GUILayout.FlexibleSpace();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFooter()
        {
            if (lastModifiedCount > 0)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField($"Last: {lastModifiedCount} modified",
                    EditorStyles.miniLabel, GUILayout.Width(140));
                EditorGUILayout.EndHorizontal();
            }

            ShuganToolUI.DrawCredits(TOOL_NAME, TOOL_VERSION);
        }
        #endregion

        #region Drawing Methods - Source Tab
        private void DrawSourceTab()
        {
            EditorGUILayout.Space(10);

            DrawAnimatorSourceSection();
            EditorGUILayout.Space(10);

            DrawManualClipsSection();
            EditorGUILayout.Space(10);

            DrawSourceSummary();
        }

        private void DrawAnimatorSourceSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Animator Controller Source", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Select an Animator Controller and choose layers/clips to batch edit.",
                MessageType.Info);

            EditorGUILayout.Space(5);

            // Animator Controller field
            AnimatorController oldAnimator = selectedAnimator;
            selectedAnimator = (AnimatorController)EditorGUILayout.ObjectField(
                new GUIContent("Animator", "Select an Animator Controller"), 
                selectedAnimator, typeof(AnimatorController), false);

            if (selectedAnimator != oldAnimator)
            {
                RefreshAnimatorData();
            }

            if (selectedAnimator != null)
            {
                EditorGUILayout.Space(5);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("Select All Layers", "Select all layers from animator"), GUILayout.Height(20)))
                {
                    foreach (var key in layerSelectionStates.Keys.ToList())
                    {
                        layerSelectionStates[key] = true;
                    }
                    RefreshClipsFromAnimator();
                }

                if (GUILayout.Button(new GUIContent("Deselect All", "Deselect all layers"), GUILayout.Height(20)))
                {
                    foreach (var key in layerSelectionStates.Keys.ToList())
                    {
                        layerSelectionStates[key] = false;
                    }
                    RefreshClipsFromAnimator();
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                // Layers with larger scroll area
                animatorScrollPos = EditorGUILayout.BeginScrollView(animatorScrollPos, GUILayout.MinHeight(200), GUILayout.MaxHeight(400));

                foreach (var layer in selectedAnimator.layers)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                    EditorGUILayout.BeginHorizontal();
                    
                    bool oldState = layerSelectionStates.ContainsKey(layer.name) && layerSelectionStates[layer.name];
                    bool newState = EditorGUILayout.Toggle(new GUIContent("", "Select this layer"), oldState, GUILayout.Width(20));
                    
                    if (newState != oldState)
                    {
                        layerSelectionStates[layer.name] = newState;
                        if (newState && !selectedLayers.Contains(layer))
                        {
                            selectedLayers.Add(layer);
                        }
                        else if (!newState)
                        {
                            selectedLayers.Remove(layer);
                        }
                        RefreshClipsFromAnimator();
                    }

                    EditorGUILayout.LabelField($"Layer: {layer.name}", EditorStyles.boldLabel);
                    EditorGUILayout.EndHorizontal();

                    // Show clips in this layer
                    if (newState)
                    {
                        var clips = GetClipsFromLayer(layer);
                        EditorGUI.indentLevel++;
                        
                        foreach (var clip in clips)
                        {
                            if (clip == null) continue;
                            
                            EditorGUILayout.BeginHorizontal();
                            
                            string clipKey = GetClipKey(clip);
                            bool clipSelected = clipSelectionFromAnimator.ContainsKey(clipKey) && clipSelectionFromAnimator[clipKey];
                            bool newClipState = EditorGUILayout.Toggle(new GUIContent("", "Select this clip"), clipSelected, GUILayout.Width(20));
                            
                            if (newClipState != clipSelected)
                            {
                                clipSelectionFromAnimator[clipKey] = newClipState;
                                RefreshAllClips();
                            }

                            // Add clip object field (non-editable but clickable)
                            EditorGUI.BeginDisabledGroup(true);
                            EditorGUILayout.ObjectField(clip, typeof(AnimationClip), false);
                            EditorGUI.EndDisabledGroup();
                            
                            EditorGUILayout.EndHorizontal();
                        }
                        
                        EditorGUI.indentLevel--;
                    }

                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(2);
                }

                EditorGUILayout.EndScrollView();
            }
            else
            {
                EditorGUILayout.HelpBox("No animator selected.", MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawManualClipsSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Manual Clip Selection", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Add individual animation clips manually.",
                MessageType.Info);

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(new GUIContent("+ Add Slot", "Add an empty clip slot"), GUILayout.Height(20)))
            {
                manualClips.Add(null);
                manualClipSelectionStates.Add(true);
            }

            var buttonColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.5f, 1f, 0.5f);

            if (GUILayout.Button(new GUIContent("📂 Add Selected", "Add selected clips from Project window"), GUILayout.Height(20)))
            {
                AddSelectedClipsFromProject();
            }

            GUI.backgroundColor = buttonColor;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            if (manualClips.Count == 0)
            {
                EditorGUILayout.HelpBox("No manual clips added.", MessageType.None);
            }
            else
            {
                clipScrollPos = EditorGUILayout.BeginScrollView(clipScrollPos, GUILayout.Height(120));

                for (int i = 0; i < manualClips.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                    while (manualClipSelectionStates.Count <= i)
                        manualClipSelectionStates.Add(true);

                    bool oldState = manualClipSelectionStates[i];
                    manualClipSelectionStates[i] = EditorGUILayout.Toggle(new GUIContent("", "Select this clip"), manualClipSelectionStates[i], GUILayout.Width(20));
                    
                    AnimationClip oldClip = manualClips[i];
                    manualClips[i] = (AnimationClip)EditorGUILayout.ObjectField(
                        manualClips[i], typeof(AnimationClip), false);

                    if (manualClips[i] != oldClip || manualClipSelectionStates[i] != oldState)
                    {
                        RefreshAllClips();
                    }

                    if (GUILayout.Button(new GUIContent("✖", "Remove this clip"), GUILayout.Width(20), GUILayout.Height(18)))
                    {
                        manualClips.RemoveAt(i);
                        manualClipSelectionStates.RemoveAt(i);
                        RefreshAllClips();
                        i--;
                    }

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSourceSummary()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Summary", EditorStyles.boldLabel);

            // Don't refresh on every frame - only refresh when explicitly needed
            // RefreshAllClips is already called when clips/layers are modified

            int selectedLayerCount = layerSelectionStates.Count(kvp => kvp.Value);
            int totalClips = allClips.Count;

            var summaryColor = GUI.color;
            GUI.color = totalClips > 0 ? Color.green : Color.yellow;
            EditorGUILayout.HelpBox(
                $"Selected Layers: {selectedLayerCount}\n" +
                $"Total Clips Selected: {totalClips}",
                MessageType.None);
            GUI.color = summaryColor;

            EditorGUILayout.EndVertical();
        }
        #endregion

        #region Drawing Methods - Value Editor Tab
        private void DrawValueEditorTab()
        {
            EditorGUILayout.Space(10);

            DrawClipsSummaryBox();
            EditorGUILayout.Space(10);

            DrawPropertySelectionSection();
            EditorGUILayout.Space(10);

            DrawOperationSection();
            EditorGUILayout.Space(10);

            DrawFrameRangeSection();
            EditorGUILayout.Space(10);

            DrawExecuteSection();

            if (!string.IsNullOrEmpty(lastOperationLog))
            {
                EditorGUILayout.Space(10);
                DrawLogSection();
            }
        }

        private void DrawClipsSummaryBox()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Selected Clips", EditorStyles.boldLabel);

            var summaryColor = GUI.color;
            GUI.color = allClips.Count > 0 ? Color.cyan : Color.yellow;
            EditorGUILayout.HelpBox(
                $"Total clips selected: {allClips.Count}\n" +
                $"(Go to Source tab to manage clips)",
                MessageType.None);
            GUI.color = summaryColor;

            EditorGUILayout.EndVertical();
        }

        private void DrawPropertySelectionSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("2. Property Selection", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Select which properties to modify. Properties are auto-detected from your animation clips.",
                MessageType.Info);

            EditorGUILayout.Space(5);

            // Refresh properties if needed
            if (needsPropertyRefresh || detectedProperties.Count == 0)
            {
                Debug.Log($"[PROPERTY REFRESH] Refreshing properties... needsPropertyRefresh={needsPropertyRefresh}, count={detectedProperties.Count}");
                RefreshDetectedProperties();
                needsPropertyRefresh = false;
                Debug.Log($"[PROPERTY REFRESH] After refresh: detectedProperties={detectedProperties.Count}, propertySelectionStates={propertySelectionStates.Count}");
            }

            // Refresh button
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("🔄 Refresh Properties", GUILayout.Height(25)))
            {
                Debug.Log("[REFRESH BUTTON] Refreshing properties manually...");
                RefreshDetectedProperties();
                Debug.Log($"[REFRESH BUTTON] After refresh: {detectedProperties.Count} properties, {propertySelectionStates.Count} states");
            }

            if (GUILayout.Button("Select All Properties", GUILayout.Height(25)))
            {
                Debug.Log($"[SELECT ALL BUTTON] Selecting all {propertySelectionStates.Count} properties...");
                for (int i = 0; i < propertySelectionStates.Count; i++)
                    propertySelectionStates[i] = true;
                Debug.Log($"[SELECT ALL BUTTON] All properties set to TRUE");
            }

            if (GUILayout.Button("Deselect All", GUILayout.Height(25)))
            {
                Debug.Log($"[DESELECT ALL BUTTON] Deselecting all {propertySelectionStates.Count} properties...");
                for (int i = 0; i < propertySelectionStates.Count; i++)
                    propertySelectionStates[i] = false;
                Debug.Log($"[DESELECT ALL BUTTON] All properties set to FALSE");
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            if (detectedProperties.Count == 0)
            {
                EditorGUILayout.HelpBox("No properties detected. Add animation clips first.", MessageType.Warning);
            }
            else
            {
                // Filters
                EditorGUILayout.BeginHorizontal();
                propertySearchFilter = EditorGUILayout.TextField("Search:", propertySearchFilter);
                if (GUILayout.Button("Clear", GUILayout.Width(50)))
                {
                    propertySearchFilter = "";
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                showTransformOnly = EditorGUILayout.Toggle("Transform Only", showTransformOnly);
                showMuscleOnly = EditorGUILayout.Toggle("Muscle Only", showMuscleOnly);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                // Group by type
                EditorGUILayout.LabelField($"Detected Properties ({detectedProperties.Count}):", EditorStyles.boldLabel);

                propertyScrollPos = EditorGUILayout.BeginScrollView(propertyScrollPos, GUILayout.Height(250));

                foreach (var kvp in detectedPropertiesByType.OrderBy(x => x.Key))
                {
                    string typeName = kvp.Key;
                    List<string> properties = kvp.Value;

                    // Apply filters
                    if (showTransformOnly && typeName != "Transform") continue;
                    if (showMuscleOnly && typeName != "Animator") continue;

                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField($"[{typeName}]", EditorStyles.boldLabel);

                    foreach (string prop in properties.OrderBy(p => p))
                    {
                        // Apply search filter
                        if (!string.IsNullOrEmpty(propertySearchFilter) &&
                            !prop.ToLower().Contains(propertySearchFilter.ToLower()))
                            continue;

                        int index = detectedProperties.IndexOf(prop);
                        if (index >= 0 && index < propertySelectionStates.Count)
                        {
                            EditorGUILayout.BeginHorizontal();

                            // DEBUG: Log state before toggle
                            bool oldValue = propertySelectionStates[index];

                            // Draw the toggle
                            bool newValue = EditorGUILayout.Toggle(oldValue, GUILayout.Width(20));

                            // DEBUG: Check if value changed
                            if (newValue != oldValue)
                            {
                                Debug.Log($"✓ [CHECKBOX CHANGED] Property: '{prop}' | Index: {index} | Old: {oldValue} | New: {newValue} | Event: {Event.current.type}");
                                propertySelectionStates[index] = newValue;
                            }

                            EditorGUILayout.LabelField(prop);
                            EditorGUILayout.EndHorizontal();
                        }
                        else if (index < 0)
                        {
                            Debug.LogError($"✖ [INDEX ERROR] Property '{prop}' not found in detectedProperties list!");
                        }
                        else if (index >= propertySelectionStates.Count)
                        {
                            Debug.LogError($"✖ [INDEX OUT OF RANGE] Property '{prop}' index {index} >= list count {propertySelectionStates.Count}");
                        }
                    }

                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(2);
                }

                EditorGUILayout.EndScrollView();

                // Summary
                int selectedProperties = propertySelectionStates.Count(s => s);
                var summaryColor = GUI.color;
                GUI.color = selectedProperties > 0 ? Color.cyan : Color.yellow;
                EditorGUILayout.HelpBox(
                    $"✓ {selectedProperties} properties selected",
                    MessageType.None);
                GUI.color = summaryColor;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawOperationSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Operation", EditorStyles.boldLabel);

            operationType = (OperationType)EditorGUILayout.EnumPopup(new GUIContent("Type", "Operation type to apply"), operationType);
            operationValue = EditorGUILayout.FloatField(new GUIContent("Value", "Value to use in operation"), operationValue);

            EditorGUILayout.Space(3);
            DrawOperationPreview();

            EditorGUILayout.EndVertical();
        }

        private void DrawOperationPreview()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            string previewText = "";
            switch (operationType)
            {
                case OperationType.Set:
                    previewText = $"All → {operationValue}";
                    break;
                case OperationType.Add:
                    previewText = $"value + {operationValue}";
                    break;
                case OperationType.Subtract:
                    previewText = $"value - {operationValue}";
                    break;
                case OperationType.Multiply:
                    previewText = $"value × {operationValue}";
                    break;
                case OperationType.Divide:
                    if (Mathf.Approximately(operationValue, 0f))
                        previewText = "⚠️ Cannot divide by zero!";
                    else
                        previewText = $"value ÷ {operationValue}";
                    break;
            }

            EditorGUILayout.LabelField(previewText, EditorStyles.boldLabel);

            EditorGUILayout.EndVertical();
        }

        private void DrawFrameRangeSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Frame Range (Optional)", EditorStyles.boldLabel);

            useFrameRange = EditorGUILayout.Toggle(new GUIContent("Use Range", "Apply operation only to specific frame range"), useFrameRange);

            if (useFrameRange)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(new GUIContent("Start", "Start frame"), GUILayout.Width(40));
                startFrame = EditorGUILayout.IntField(startFrame, GUILayout.Width(80));
                GUILayout.Space(10);
                EditorGUILayout.LabelField(new GUIContent("End", "End frame"), GUILayout.Width(40));
                endFrame = EditorGUILayout.IntField(endFrame, GUILayout.Width(80));
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawExecuteSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Execute", EditorStyles.boldLabel);

            int validClips = allClips.Count;
            int selectedProperties = propertySelectionStates.Count(s => s);

            bool canExecute = validClips > 0 && selectedProperties > 0;

            if (operationType == OperationType.Divide && Mathf.Approximately(operationValue, 0f))
            {
                canExecute = false;
            }

            if (canExecute)
            {
                var summaryColor = GUI.color;
                GUI.color = Color.green;
                EditorGUILayout.HelpBox(
                    $"✅ Ready:\n" +
                    $"• {validClips} clip(s)\n" +
                    $"• {selectedProperties} property(ies)",
                    MessageType.None);
                GUI.color = summaryColor;
            }
            else
            {
                EditorGUILayout.HelpBox("Select clips and properties!", MessageType.Warning);
            }

            EditorGUI.BeginDisabledGroup(!canExecute);

            var buttonColor = GUI.backgroundColor;
            GUI.backgroundColor = Color.green;

            if (GUILayout.Button(new GUIContent($"🎯 Apply to {validClips} Clip(s)", "Apply value operations to selected clips"), GUILayout.Height(35)))
            {
                ExecuteOperation();
            }

            GUI.backgroundColor = buttonColor;

            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndVertical();
        }

        private void DrawLogSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Operation Log", EditorStyles.boldLabel);

            EditorGUILayout.TextArea(lastOperationLog, GUILayout.Height(100));

            if (GUILayout.Button(new GUIContent("Clear Log", "Clear operation log")))
            {
                lastOperationLog = "";
                lastModifiedCount = 0;
            }

            EditorGUILayout.EndVertical();
        }
        #endregion

        #region Drawing Methods - Path Editor Tab
        private void DrawPathEditorTab()
        {
            EditorGUILayout.Space(10);

            DrawClipsSummaryBox();
            EditorGUILayout.Space(10);

            DrawPathModeSection();
            EditorGUILayout.Space(10);

            DrawPathFindReplaceSection();
            EditorGUILayout.Space(10);

            DrawPathPreviewSection();
            EditorGUILayout.Space(10);

            // Show status/warnings
            DrawPathStatusSection();

            if (!string.IsNullOrEmpty(lastOperationLog))
            {
                EditorGUILayout.Space(10);
                DrawLogSection();
            }
        }

        private void DrawPathModeSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Operation Mode", EditorStyles.boldLabel);

            pathEditMode = (PathEditMode)EditorGUILayout.EnumPopup(new GUIContent("Mode", "Choose operation mode"), pathEditMode);

            if (pathEditMode == PathEditMode.CreateDuplicate)
            {
                EditorGUILayout.HelpBox(
                    "Creates duplicate clips with modified paths and names.",
                    MessageType.Info);
                
                if (selectedAnimator != null)
                {
                    EditorGUILayout.Space(5);
                    duplicateLayers = EditorGUILayout.Toggle(new GUIContent("Duplicate Layers", "Duplicate entire animator layers"), duplicateLayers);
                    
                    if (duplicateLayers && selectedLayers.Count > 0)
                    {
                        EditorGUILayout.Space(5);
                        DrawLayerDuplicationSettings();
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Modifies paths directly in selected clips.",
                    MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawLayerDuplicationSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Layer Naming", EditorStyles.boldLabel);

            customLayerSuffix = EditorGUILayout.TextField(
                new GUIContent("Layer Suffix", "Suffix to add to duplicated layer names"), 
                customLayerSuffix);

            // Check for naming conflicts
            showLayerNameWarning = false;
            if (selectedAnimator != null && selectedLayers.Count > 0)
            {
                foreach (var layer in selectedLayers)
                {
                    string newLayerName = ApplyFindReplaceRules(layer.name) + customLayerSuffix;
                    
                    if (newLayerName == layer.name)
                    {
                        showLayerNameWarning = true;
                        GUI.color = Color.yellow;
                        EditorGUILayout.HelpBox(
                            $"⚠️ WARNING: Layer '{layer.name}' will have the same name!\n" +
                            $"Result: '{newLayerName}'",
                            MessageType.Warning);
                        GUI.color = Color.white;
                    }
                    else if (selectedAnimator.layers.Any(l => l.name == newLayerName))
                    {
                        showLayerNameWarning = true;
                        GUI.color = Color.yellow;
                        EditorGUILayout.HelpBox(
                            $"⚠️ WARNING: Layer '{newLayerName}' already exists!",
                            MessageType.Warning);
                        GUI.color = Color.white;
                    }
                }
            }

            EditorGUILayout.Space(3);

            layerPlacement = (LayerPlacement)EditorGUILayout.EnumPopup(
                new GUIContent("Placement", "Where to place duplicated layers"), 
                layerPlacement);

            EditorGUILayout.EndVertical();
        }

        private void DrawPathFindReplaceSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Find and Replace Rules", EditorStyles.boldLabel);

            // Quick presets
            EditorGUILayout.BeginHorizontal();

            GUI.backgroundColor = new Color(0.5f, 1f, 0.5f);
            if (GUILayout.Button(new GUIContent("L→R Full", "Add Left to Right rules"), GUILayout.Height(20)))
            {
                AddPreset_LeftToRight();
            }

            GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button(new GUIContent("R→L Full", "Add Right to Left rules"), GUILayout.Height(20)))
            {
                AddPreset_RightToLeft();
            }

            GUI.backgroundColor = new Color(0.7f, 0.7f, 1f);
            if (GUILayout.Button(new GUIContent("L↔R Invert", "Swap Left and Right (atomic swap operation)"), GUILayout.Height(20)))
            {
                AddPreset_LeftRightSwap();
            }

            GUI.backgroundColor = Color.red;
            if (GUILayout.Button(new GUIContent("Clear All", "Clear all rules"), GUILayout.Height(20)))
            {
                findReplaceRules.Clear();
                pathPreviewChanges.Clear();
                clipNamePreview.Clear();
                showPreview = false;
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Options
            pathCaseSensitive = EditorGUILayout.Toggle(new GUIContent("Case Sensitive", "Match case"), pathCaseSensitive);
            pathWholeWordOnly = EditorGUILayout.Toggle(new GUIContent("Whole Word", "Match whole words only"), pathWholeWordOnly);

            EditorGUILayout.Space(5);

            // Rules list - fixed height to prevent shrinking
            EditorGUILayout.LabelField("Rules:", EditorStyles.boldLabel);

            rulesScrollPos = EditorGUILayout.BeginScrollView(rulesScrollPos, GUILayout.Height(200));

            for (int i = 0; i < findReplaceRules.Count; i++)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();

                findReplaceRules[i].enabled = EditorGUILayout.Toggle(findReplaceRules[i].enabled, GUILayout.Width(20));
                EditorGUILayout.LabelField($"Rule {i + 1}", EditorStyles.boldLabel, GUILayout.Width(50));
                GUILayout.FlexibleSpace();

                EditorGUI.BeginDisabledGroup(i == 0);
                if (GUILayout.Button("↑", GUILayout.Width(25)))
                {
                    var temp = findReplaceRules[i];
                    findReplaceRules[i] = findReplaceRules[i - 1];
                    findReplaceRules[i - 1] = temp;
                    showPreview = false;
                }
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(i == findReplaceRules.Count - 1);
                if (GUILayout.Button("↓", GUILayout.Width(25)))
                {
                    var temp = findReplaceRules[i];
                    findReplaceRules[i] = findReplaceRules[i + 1];
                    findReplaceRules[i + 1] = temp;
                    showPreview = false;
                }
                EditorGUI.EndDisabledGroup();

                GUI.backgroundColor = Color.red;
                if (GUILayout.Button("✖", GUILayout.Width(25)))
                {
                    findReplaceRules.RemoveAt(i);
                    showPreview = false;
                    GUI.backgroundColor = Color.white;
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    continue;
                }
                GUI.backgroundColor = Color.white;

                EditorGUILayout.EndHorizontal();

                EditorGUI.BeginChangeCheck();
                findReplaceRules[i].find = EditorGUILayout.TextField("Find:", findReplaceRules[i].find);
                findReplaceRules[i].replace = EditorGUILayout.TextField("Replace:", findReplaceRules[i].replace);
                
                if (EditorGUI.EndChangeCheck())
                {
                    showPreview = false;
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();

            GUI.backgroundColor = Color.cyan;
            if (GUILayout.Button(new GUIContent("+ Add Rule", "Add new rule"), GUILayout.Height(25)))
            {
                findReplaceRules.Add(new FindReplaceRule());
                showPreview = false;
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(5);

            // Preview and Execute buttons side by side
            EditorGUILayout.BeginHorizontal();
            
            var buttonColor = GUI.backgroundColor;
            GUI.backgroundColor = Color.yellow;

            if (GUILayout.Button(new GUIContent("🔍 Preview Changes", "Preview changes"), GUILayout.Height(35)))
            {
                UpdatePathPreview();
            }

            GUI.backgroundColor = Color.white;
            
            // Execute button next to preview
            if (findReplaceRules.Count > 0 && findReplaceRules.Any(r => r.enabled && !string.IsNullOrEmpty(r.find)))
            {
                int validClips = allClips.Count;
                bool hasRules = findReplaceRules.Any(r => r.enabled && !string.IsNullOrEmpty(r.find));
                bool canExecute = validClips > 0 && hasRules && showPreview;

                // Block execution if layer name warning exists
                if (showLayerNameWarning && duplicateLayers && pathEditMode == PathEditMode.CreateDuplicate)
                {
                    canExecute = false;
                }

                EditorGUI.BeginDisabledGroup(!canExecute);
                
                GUI.backgroundColor = canExecute ? Color.green : Color.gray;

                string buttonText = pathEditMode == PathEditMode.CreateDuplicate 
                    ? $"🎯 Create Duplicates" 
                    : $"🎯 Apply Changes";

                if (GUILayout.Button(buttonText, GUILayout.Height(35)))
                {
                    ExecutePathReplace();
                }

                GUI.backgroundColor = Color.white;
                EditorGUI.EndDisabledGroup();
            }
            
            EditorGUILayout.EndHorizontal();

            GUI.backgroundColor = buttonColor;

            EditorGUILayout.EndVertical();
        }

        private void DrawPathPreviewSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            
            // Pop-out button
            if (showPreview && (clipNamePreview.Count > 0 || pathPreviewChanges.Count > 0))
            {
                GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
                if (GUILayout.Button(new GUIContent("↗ Pop Out", "Open preview in separate window"), GUILayout.Width(80), GUILayout.Height(20)))
                {
                    PreviewWindow.ShowWindow(this);
                }
                GUI.backgroundColor = Color.white;
            }
            
            EditorGUILayout.EndHorizontal();

            if (findReplaceRules.Count == 0 || !findReplaceRules.Any(r => r.enabled && !string.IsNullOrEmpty(r.find)))
            {
                EditorGUILayout.HelpBox("Add rules and click 'Preview Changes'.", MessageType.Info);
            }
            else if (!showPreview)
            {
                EditorGUILayout.HelpBox("Click 'Preview Changes' to see results.", MessageType.Warning);
            }
            else
            {
                // Fixed height scroll view to prevent shrinking
                previewScrollPos = EditorGUILayout.BeginScrollView(previewScrollPos, GUILayout.Height(300));

                DrawPreviewContent();

                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawPreviewContent()
        {
            // Clip name changes
            if (pathEditMode == PathEditMode.CreateDuplicate && clipNamePreview.Count > 0)
            {
                EditorGUILayout.LabelField("Animation Clips:", EditorStyles.boldLabel);

                // Add bulk action buttons
                int conflictCount = 0;
                foreach (var kvp in clipNamePreview)
                {
                    if (clipWillBeReused.ContainsKey(kvp.Key) && clipWillBeReused[kvp.Key])
                        continue;
                    
                    string sourcePath = AssetDatabase.GetAssetPath(kvp.Key);
                    string sourceDir = Path.GetDirectoryName(sourcePath);
                    string extension = Path.GetExtension(sourcePath);
                    string targetPath = Path.Combine(sourceDir, kvp.Value + extension);
                    
                    if (File.Exists(targetPath))
                    {
                        conflictCount++;
                    }
                }

                if (conflictCount > 0)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"{conflictCount} file(s) exist:", EditorStyles.boldLabel, GUILayout.Width(120));
                    
                    GUI.backgroundColor = new Color(1f, 0.8f, 0.5f);
                    if (GUILayout.Button("Replace All", GUILayout.Height(25)))
                    {
                        foreach (var kvp in clipNamePreview)
                        {
                            if (clipActions.ContainsKey(kvp.Key))
                            {
                                clipActions[kvp.Key] = ClipAction.Replace;
                            }
                        }
                    }
                    
                    GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
                    if (GUILayout.Button("Use Existing All", GUILayout.Height(25)))
                    {
                        foreach (var kvp in clipNamePreview)
                        {
                            if (clipActions.ContainsKey(kvp.Key))
                            {
                                clipActions[kvp.Key] = ClipAction.UseExisting;
                            }
                        }
                    }
                    GUI.backgroundColor = Color.white;
                    
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.Space(5);
                }

                // Show all clips (no limit)
                foreach (var kvp in clipNamePreview)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    
                    bool willBeReused = clipWillBeReused.ContainsKey(kvp.Key) && clipWillBeReused[kvp.Key];
                    
                    if (willBeReused)
                    {
                        GUI.color = Color.cyan;
                        EditorGUILayout.LabelField("↻ Reuse: " + kvp.Key.name);
                        GUI.color = Color.white;
                    }
                    else
                    {
                        GUI.color = new Color(0.7f, 1f, 0.7f);
                        EditorGUILayout.LabelField("✓ Create: " + kvp.Value);
                        GUI.color = Color.white;
                        
                        // Check if file exists
                        string sourcePath = AssetDatabase.GetAssetPath(kvp.Key);
                        string sourceDir = Path.GetDirectoryName(sourcePath);
                        string extension = Path.GetExtension(sourcePath);
                        string targetPath = Path.Combine(sourceDir, kvp.Value + extension);
                        
                        if (File.Exists(targetPath))
                        {
                            EditorGUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("⚠️ File exists:", GUILayout.Width(90));
                            
                            if (!clipActions.ContainsKey(kvp.Key))
                            {
                                clipActions[kvp.Key] = ClipAction.Replace;
                            }
                            
                            clipActions[kvp.Key] = (ClipAction)EditorGUILayout.EnumPopup(clipActions[kvp.Key]);
                            EditorGUILayout.EndHorizontal();
                        }
                    }
                    
                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(2);
                }

                int willCreate = clipNamePreview.Count(kvp => kvp.Value != kvp.Key.name);
                int willReuse = clipNamePreview.Count(kvp => kvp.Value == kvp.Key.name);
                
                GUI.color = Color.green;
                EditorGUILayout.HelpBox(
                    $"✓ {willCreate} to create | {willReuse} to reuse",
                    MessageType.None);
                GUI.color = Color.white;

                EditorGUILayout.Space(5);
            }

            // Path changes - show only paths that will actually be modified
            if (pathPreviewChanges.Count > 0)
            {
                // Filter out paths that only exist in reused or UseExisting clips
                var filteredPathChanges = new Dictionary<string, List<string>>();

                foreach (var kvp in pathPreviewChanges)
                {
                    string originalPath = kvp.Key;

                    // Check if this path belongs to any clip that will be modified
                    bool pathWillBeModified = false;

                    if (pathToClipsMap.ContainsKey(originalPath))
                    {
                        foreach (var clip in pathToClipsMap[originalPath])
                        {
                            // Path will be modified if the clip is NOT being reused and NOT set to UseExisting
                            bool isReused = clipWillBeReused.ContainsKey(clip) && clipWillBeReused[clip];
                            bool isUseExisting = clipActions.ContainsKey(clip) && clipActions[clip] == ClipAction.UseExisting;

                            if (!isReused && !isUseExisting)
                            {
                                pathWillBeModified = true;
                                break;
                            }
                        }
                    }
                    else
                    {
                        // If path has no associated clips, show it anyway (shouldn't happen, but safe default)
                        pathWillBeModified = true;
                    }

                    if (pathWillBeModified)
                    {
                        filteredPathChanges[originalPath] = kvp.Value;
                    }
                }

                if (filteredPathChanges.Count > 0)
                {
                    EditorGUILayout.LabelField("Path Changes:", EditorStyles.boldLabel);

                    foreach (var kvp in filteredPathChanges)
                    {
                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                        GUI.color = new Color(1f, 0.7f, 0.7f);
                        EditorGUILayout.LabelField("Before: " + kvp.Key, EditorStyles.wordWrappedLabel);

                        GUI.color = new Color(0.7f, 1f, 0.7f);
                        foreach (string newPath in kvp.Value)
                        {
                            EditorGUILayout.LabelField("After:  " + newPath, EditorStyles.wordWrappedLabel);
                        }

                        GUI.color = Color.white;

                        EditorGUILayout.EndVertical();
                        EditorGUILayout.Space(3);
                    }

                    int totalChanges = filteredPathChanges.Sum(kvp => kvp.Value.Count);
                    GUI.color = Color.cyan;
                    EditorGUILayout.HelpBox(
                        $"✓ {filteredPathChanges.Count} unique paths | {totalChanges} total bindings",
                        MessageType.None);
                    GUI.color = Color.white;
                }
            }
        }

        private void DrawPathStatusSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);

            int validClips = allClips.Count;
            bool hasRules = findReplaceRules.Any(r => r.enabled && !string.IsNullOrEmpty(r.find));
            bool canExecute = validClips > 0 && hasRules && showPreview;

            if (showLayerNameWarning && duplicateLayers && pathEditMode == PathEditMode.CreateDuplicate)
            {
                GUI.color = Color.red;
                EditorGUILayout.HelpBox("⛔ Fix layer naming conflicts before executing!", MessageType.Error);
                GUI.color = Color.white;
            }
            else if (canExecute)
            {
                GUI.color = Color.green;
                string modeText = pathEditMode == PathEditMode.CreateDuplicate ? "Create Duplicates" : "Modify In Place";
                EditorGUILayout.HelpBox(
                    $"✅ Ready: {modeText}\n" +
                    $"• {validClips} clips • {findReplaceRules.Count(r => r.enabled)} rules",
                    MessageType.None);
                GUI.color = Color.white;
            }
            else if (!showPreview && hasRules)
            {
                EditorGUILayout.HelpBox("Click 'Preview Changes' first!", MessageType.Warning);
            }
            else if (!hasRules)
            {
                EditorGUILayout.HelpBox("Add rules to get started.", MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }
        #endregion

        #region Drawing Methods - Animator Utils Tab
        private void DrawAnimatorUtilsTab()
        {
            EditorGUILayout.Space(10);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Animator Utilities", EditorStyles.largeLabel);
            EditorGUILayout.HelpBox("Copy parameters and layers between Animator Controllers.", MessageType.Info);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            DrawAnimatorSelection();
            EditorGUILayout.Space(10);

            DrawParameterCopySection();
            EditorGUILayout.Space(10);

            DrawLayerCopySection();

            if (!string.IsNullOrEmpty(lastOperationLog))
            {
                EditorGUILayout.Space(10);
                DrawLogSection();
            }
        }

        private void DrawAnimatorSelection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Animator Controllers", EditorStyles.boldLabel);

            AnimatorController oldSource = sourceAnimatorUtils;
            sourceAnimatorUtils = (AnimatorController)EditorGUILayout.ObjectField(
                new GUIContent("Source", "Animator to copy from"),
                sourceAnimatorUtils, typeof(AnimatorController), false);

            if (sourceAnimatorUtils != oldSource)
            {
                RefreshAnimatorUtilsData();
            }

            targetAnimatorUtils = (AnimatorController)EditorGUILayout.ObjectField(
                new GUIContent("Target", "Animator to copy to"),
                targetAnimatorUtils, typeof(AnimatorController), false);

            EditorGUILayout.EndVertical();
        }

        private void DrawParameterCopySection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Parameter Copying", EditorStyles.boldLabel);

            if (sourceAnimatorUtils == null || targetAnimatorUtils == null)
            {
                EditorGUILayout.HelpBox("Select both source and target animators.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox("Select parameters to copy from source to target animator.", MessageType.Info);

                EditorGUILayout.Space(5);

                // Selection buttons
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Select All", GUILayout.Height(20)))
                {
                    foreach (var key in parameterSelectionStates.Keys.ToList())
                    {
                        parameterSelectionStates[key] = true;
                    }
                }
                if (GUILayout.Button("Deselect All", GUILayout.Height(20)))
                {
                    foreach (var key in parameterSelectionStates.Keys.ToList())
                    {
                        parameterSelectionStates[key] = false;
                    }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                // Parameters list
                utilsScrollPos = EditorGUILayout.BeginScrollView(utilsScrollPos, GUILayout.Height(150));

                var sourceParams = sourceAnimatorUtils.parameters;
                foreach (var param in sourceParams)
                {
                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                    if (!parameterSelectionStates.ContainsKey(param.name))
                    {
                        parameterSelectionStates[param.name] = false;
                    }

                    parameterSelectionStates[param.name] = EditorGUILayout.Toggle(
                        parameterSelectionStates[param.name], GUILayout.Width(20));

                    EditorGUILayout.LabelField($"{param.name} ({param.type})");

                    // Check if parameter exists in target
                    bool existsInTarget = targetAnimatorUtils.parameters.Any(p => p.name == param.name);
                    if (existsInTarget)
                    {
                        GUI.color = Color.yellow;
                        EditorGUILayout.LabelField("⚠ Exists", GUILayout.Width(60));
                        GUI.color = Color.white;
                    }

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.EndScrollView();

                EditorGUILayout.Space(5);

                int selectedCount = parameterSelectionStates.Count(kvp => kvp.Value);

                GUI.backgroundColor = Color.green;
                EditorGUI.BeginDisabledGroup(selectedCount == 0);
                if (GUILayout.Button($"Copy {selectedCount} Parameter(s)", GUILayout.Height(30)))
                {
                    CopyParameters();
                }
                EditorGUI.EndDisabledGroup();
                GUI.backgroundColor = Color.white;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawLayerCopySection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Layer Copying", EditorStyles.boldLabel);

            if (sourceAnimatorUtils == null || targetAnimatorUtils == null)
            {
                EditorGUILayout.HelpBox("Select both source and target animators.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox("Select layers to copy from source to target animator.", MessageType.Info);

                EditorGUILayout.Space(5);

                // Selection buttons
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Select All", GUILayout.Height(20)))
                {
                    foreach (var key in layerSelectionStatesUtils.Keys.ToList())
                    {
                        layerSelectionStatesUtils[key] = true;
                    }
                }
                if (GUILayout.Button("Deselect All", GUILayout.Height(20)))
                {
                    foreach (var key in layerSelectionStatesUtils.Keys.ToList())
                    {
                        layerSelectionStatesUtils[key] = false;
                    }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                // Layers list
                utilsScrollPos = EditorGUILayout.BeginScrollView(utilsScrollPos, GUILayout.Height(150));

                var sourceLayers = sourceAnimatorUtils.layers;
                foreach (var layer in sourceLayers)
                {
                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                    if (!layerSelectionStatesUtils.ContainsKey(layer.name))
                    {
                        layerSelectionStatesUtils[layer.name] = false;
                    }

                    layerSelectionStatesUtils[layer.name] = EditorGUILayout.Toggle(
                        layerSelectionStatesUtils[layer.name], GUILayout.Width(20));

                    EditorGUILayout.LabelField(layer.name);

                    // Check if layer exists in target
                    bool existsInTarget = targetAnimatorUtils.layers.Any(l => l.name == layer.name);
                    if (existsInTarget)
                    {
                        GUI.color = Color.yellow;
                        EditorGUILayout.LabelField("⚠ Exists", GUILayout.Width(60));
                        GUI.color = Color.white;
                    }

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.EndScrollView();

                EditorGUILayout.Space(5);

                int selectedCount = layerSelectionStatesUtils.Count(kvp => kvp.Value);

                GUI.backgroundColor = Color.green;
                EditorGUI.BeginDisabledGroup(selectedCount == 0);
                if (GUILayout.Button($"Copy {selectedCount} Layer(s)", GUILayout.Height(30)))
                {
                    CopyLayers();
                }
                EditorGUI.EndDisabledGroup();
                GUI.backgroundColor = Color.white;
            }

            EditorGUILayout.EndVertical();
        }

        private void RefreshAnimatorUtilsData()
        {
            parameterSelectionStates.Clear();
            layerSelectionStatesUtils.Clear();

            if (sourceAnimatorUtils != null)
            {
                foreach (var param in sourceAnimatorUtils.parameters)
                {
                    parameterSelectionStates[param.name] = false;
                }

                foreach (var layer in sourceAnimatorUtils.layers)
                {
                    layerSelectionStatesUtils[layer.name] = false;
                }
            }
        }
        #endregion

        #region Drawing Methods - Settings Tab
        private void DrawSettingsTab()
        {
            EditorGUILayout.Space(10);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Backup Settings", EditorStyles.boldLabel);

            createBackups = EditorGUILayout.Toggle(new GUIContent("Create Backups", "Create backup files"), createBackups);

            if (createBackups)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.LabelField("Folder:", EditorStyles.boldLabel);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.TextField(backupFolderPath);
                if (GUILayout.Button("...", GUILayout.Width(30)))
                {
                    string newPath = EditorUtility.OpenFolderPanel("Select Backup Folder", backupFolderPath, "");
                    if (!string.IsNullOrEmpty(newPath))
                    {
                        backupFolderPath = newPath;
                    }
                }
                EditorGUILayout.EndHorizontal();

                if (GUILayout.Button("Reset Path"))
                {
                    backupFolderPath = GetDefaultBackupPath();
                }

                if (GUILayout.Button("Open Folder", GUILayout.Height(20)))
                {
                    if (!Directory.Exists(backupFolderPath))
                        Directory.CreateDirectory(backupFolderPath);
                    EditorUtility.RevealInFinder(backupFolderPath);
                }

                EditorGUI.indentLevel--;
            }
            else
            {
                EditorGUILayout.HelpBox("⚠️ Backups disabled!", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Display Settings", EditorStyles.boldLabel);

            showDetailedLog = EditorGUILayout.Toggle("Detailed Log", showDetailedLog);
            showDebugLog = EditorGUILayout.Toggle("Debug Log", showDebugLog);

            EditorGUILayout.EndVertical();
        }
        #endregion

        #region Drawing Methods - About Tab
        private void DrawAboutTab()
        {
            EditorGUILayout.Space(10);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(VERSION, EditorStyles.largeLabel);
            EditorGUILayout.LabelField(AUTHOR, EditorStyles.miniLabel);

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Features:", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("✓ Batch edit clips from Animator");
            EditorGUILayout.LabelField("✓ Select layers and individual clips");
            EditorGUILayout.LabelField("✓ Modify values and paths");
            EditorGUILayout.LabelField("✓ Smart clip duplication");
            EditorGUILayout.LabelField("✓ Per-clip handling options");
            EditorGUILayout.LabelField("✓ Pop-out preview window");
            EditorGUILayout.LabelField("✓ Layer duplication & placement");
            EditorGUILayout.LabelField("✓ Copy parameters between animators");
            EditorGUILayout.LabelField("✓ Copy layers between animators");

            EditorGUILayout.Space(10);

            EditorGUILayout.HelpBox("💡 Always preview before executing!", MessageType.Info);

            EditorGUILayout.EndVertical();
        }
        #endregion

        #region Preset Methods
        private void AddPreset_LeftToRight()
        {
            findReplaceRules.Add(new FindReplaceRule { find = "_L", replace = "_R", enabled = true });
            findReplaceRules.Add(new FindReplaceRule { find = ".L", replace = ".R", enabled = true });
            findReplaceRules.Add(new FindReplaceRule { find = "Left", replace = "Right", enabled = true });
            findReplaceRules.Add(new FindReplaceRule { find = "left", replace = "right", enabled = true });
            showPreview = false;
        }

        private void AddPreset_RightToLeft()
        {
            findReplaceRules.Add(new FindReplaceRule { find = "_R", replace = "_L", enabled = true });
            findReplaceRules.Add(new FindReplaceRule { find = ".R", replace = ".L", enabled = true });
            findReplaceRules.Add(new FindReplaceRule { find = "Right", replace = "Left", enabled = true });
            findReplaceRules.Add(new FindReplaceRule { find = "right", replace = "left", enabled = true });
            showPreview = false;
        }

        private void AddPreset_LeftRightSwap()
        {
            // Add swap rules that will be processed together atomically
            findReplaceRules.Add(new FindReplaceRule { find = "_L", replace = "_R", enabled = true, isSwap = true });
            findReplaceRules.Add(new FindReplaceRule { find = "_R", replace = "_L", enabled = true, isSwap = true });
            findReplaceRules.Add(new FindReplaceRule { find = ".L", replace = ".R", enabled = true, isSwap = true });
            findReplaceRules.Add(new FindReplaceRule { find = ".R", replace = ".L", enabled = true, isSwap = true });
            findReplaceRules.Add(new FindReplaceRule { find = "Left", replace = "Right", enabled = true, isSwap = true });
            findReplaceRules.Add(new FindReplaceRule { find = "Right", replace = "Left", enabled = true, isSwap = true });
            findReplaceRules.Add(new FindReplaceRule { find = "left", replace = "right", enabled = true, isSwap = true });
            findReplaceRules.Add(new FindReplaceRule { find = "right", replace = "left", enabled = true, isSwap = true });
            showPreview = false;
        }
        #endregion

        #region Preview Methods
        private void UpdatePathPreview()
        {
            pathPreviewChanges.Clear();
            clipNamePreview.Clear();
            clipWillBeReused.Clear();
            clipActions.Clear();
            pathToClipsMap.Clear();

            // Refresh clips from animator to get latest changes
            if (selectedAnimator != null)
            {
                RefreshClipsFromAnimator();
            }

            RefreshDetectedPaths();

            // Build path to clips mapping
            foreach (var clip in allClips)
            {
                if (clip == null) continue;

                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
                foreach (var binding in bindings)
                {
                    string pathKey;
                    if (!string.IsNullOrEmpty(binding.path))
                    {
                        pathKey = binding.path;
                    }
                    else if (binding.type == typeof(Animator) && !string.IsNullOrEmpty(binding.propertyName))
                    {
                        pathKey = $"[Param] {binding.propertyName}";
                    }
                    else
                    {
                        continue;
                    }

                    if (!pathToClipsMap.ContainsKey(pathKey))
                    {
                        pathToClipsMap[pathKey] = new HashSet<AnimationClip>();
                    }
                    pathToClipsMap[pathKey].Add(clip);
                }
            }

            // Generate path changes
            foreach (string originalPath in detectedPaths)
            {
                string modifiedPath;

                // Handle Animator parameters (prefixed with [Param])
                if (originalPath.StartsWith("[Param] "))
                {
                    string paramName = originalPath.Substring(8); // Remove "[Param] " prefix
                    string newParamName = ApplyFindReplaceRules(paramName);
                    modifiedPath = $"[Param] {newParamName}";
                }
                else
                {
                    modifiedPath = ApplyFindReplaceRules(originalPath);
                }

                if (modifiedPath != originalPath)
                {
                    if (!pathPreviewChanges.ContainsKey(originalPath))
                    {
                        pathPreviewChanges[originalPath] = new List<string>();
                    }
                    pathPreviewChanges[originalPath].Add(modifiedPath);
                }
            }

            // Generate clip name changes
            foreach (var clip in allClips)
            {
                if (clip == null) continue;

                string originalName = clip.name;
                string newName = ApplyFindReplaceRules(originalName);
                
                clipNamePreview[clip] = newName;
                clipWillBeReused[clip] = (originalName == newName);
                
                // Default to Replace for all clips
                if (originalName != newName)
                {
                    clipActions[clip] = ClipAction.Replace;
                }
            }

            showPreview = true;
        }

        private string ApplyFindReplaceRules(string input)
        {
            string result = input;

            // Separate swap rules from regular rules
            var swapRules = findReplaceRules.Where(r => r.enabled && !string.IsNullOrEmpty(r.find) && r.isSwap).ToList();
            var normalRules = findReplaceRules.Where(r => r.enabled && !string.IsNullOrEmpty(r.find) && !r.isSwap).ToList();

            // Step 1: Apply swap rules using temporary placeholders to avoid double-replacement
            if (swapRules.Count > 0)
            {
                // Use a unique prefix that won't appear in normal paths
                string tempPrefix = "__SWAP_TEMP_" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
                var swapMap = new Dictionary<string, string>();

                // First pass: Replace all swap rule finds with temporary placeholders
                for (int i = 0; i < swapRules.Count; i++)
                {
                    var rule = swapRules[i];
                    string tempPlaceholder = tempPrefix + i + "_";

                    result = ApplySingleRule(result, rule.find, tempPlaceholder);
                    swapMap[tempPlaceholder] = rule.replace;
                }

                // Second pass: Replace all temporary placeholders with final values
                foreach (var kvp in swapMap)
                {
                    result = result.Replace(kvp.Key, kvp.Value);
                }
            }

            // Step 2: Apply normal (non-swap) rules
            foreach (var rule in normalRules)
            {
                result = ApplySingleRule(result, rule.find, rule.replace);
            }

            return result;
        }

        private string ApplySingleRule(string input, string find, string replace)
        {
            string result = input;

            StringComparison comparison = pathCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (pathWholeWordOnly)
            {
                string[] separators = new[] { " ", "/", "_", "." };
                foreach (var sep in separators)
                {
                    string[] parts = result.Split(new[] { sep }, StringSplitOptions.None);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (string.Equals(parts[i], find, comparison))
                        {
                            parts[i] = replace;
                        }
                    }
                    result = string.Join(sep, parts);
                }
            }
            else
            {
                if (pathCaseSensitive)
                {
                    result = result.Replace(find, replace);
                }
                else
                {
                    int index = 0;
                    while ((index = result.IndexOf(find, index, comparison)) != -1)
                    {
                        result = result.Remove(index, find.Length);
                        result = result.Insert(index, replace);
                        index += replace.Length;
                    }
                }
            }

            return result;
        }

        // Public method for preview window
        public void DrawPreviewContentPublic()
        {
            DrawPreviewContent();
        }
        #endregion

        #region Value Operation Methods
        private void ExecuteOperation()
        {
            lastOperationLog = "";
            lastModifiedCount = 0;

            RefreshAllClips();

            if (allClips.Count == 0)
            {
                EditorUtility.DisplayDialog("No Selection", "Select clips first.", "OK");
                return;
            }

            List<string> propertiesToModify = new List<string>();
            for (int i = 0; i < detectedProperties.Count && i < propertySelectionStates.Count; i++)
            {
                if (propertySelectionStates[i])
                    propertiesToModify.Add(detectedProperties[i]);
            }

            if (propertiesToModify.Count == 0)
            {
                EditorUtility.DisplayDialog("No Properties", "Select properties first.", "OK");
                return;
            }

            // Collect clips that will be modified (for selective backup)
            if (createBackups)
            {
                List<AnimationClip> clipsToBackup = new List<AnimationClip>();

                foreach (var clip in allClips)
                {
                    if (clip == null) continue;

                    // Check if this clip will have any value modifications
                    if (WillClipValuesBeModified(clip, propertiesToModify))
                    {
                        clipsToBackup.Add(clip);
                    }
                }

                // Create backups only for clips that will be modified
                if (clipsToBackup.Count > 0)
                {
                    CreateBackups(clipsToBackup);
                }
            }

            lastOperationLog += $"=== VALUE OPERATION ===\n";
            lastOperationLog += $"Operation: {operationType} {operationValue}\n";

            foreach (var clip in allClips)
            {
                int clipModifiedCount = 0;

                foreach (string property in propertiesToModify)
                {
                    int propertyModifiedCount = ProcessClipProperty(clip, property);
                    clipModifiedCount += propertyModifiedCount;
                }

                lastModifiedCount += clipModifiedCount;

                if (showDetailedLog && clipModifiedCount > 0)
                {
                    lastOperationLog += $"[{clip.name}] {clipModifiedCount} keyframes\n";
                }
            }

            lastOperationLog += $"\nTotal: {lastModifiedCount} keyframes modified";

            if (autoRefresh)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            EditorUtility.DisplayDialog("Success! 🎉", 
                $"Modified {lastModifiedCount} keyframes", "OK");
        }

        private int ProcessClipProperty(AnimationClip clip, string targetProperty)
        {
            int modifiedCount = 0;

            try
            {
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);

                foreach (var binding in bindings)
                {
                    if (binding.propertyName != targetProperty)
                        continue;

                    AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve == null || curve.keys.Length == 0)
                        continue;

                    int keysModified = ModifyCurve(curve, clip);
                    if (keysModified > 0)
                    {
                        Undo.RecordObject(clip, "Batch Edit Animation");
                        AnimationUtility.SetEditorCurve(clip, binding, curve);
                        modifiedCount += keysModified;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error processing {targetProperty} in {clip.name}: {e.Message}");
            }

            return modifiedCount;
        }

        private int ModifyCurve(AnimationCurve curve, AnimationClip clip)
        {
            int modifiedCount = 0;
            Keyframe[] keys = curve.keys;
            float frameRate = clip.frameRate;

            for (int i = 0; i < keys.Length; i++)
            {
                if (useFrameRange)
                {
                    int frameNumber = Mathf.RoundToInt(keys[i].time * frameRate);
                    if (frameNumber < startFrame || frameNumber > endFrame)
                        continue;
                }

                float oldValue = keys[i].value;
                float newValue = ApplyOperation(oldValue);

                if (!Mathf.Approximately(oldValue, newValue))
                {
                    keys[i].value = newValue;
                    modifiedCount++;
                }
            }

            if (modifiedCount > 0)
            {
                curve.keys = keys;
            }

            return modifiedCount;
        }

        private float ApplyOperation(float originalValue)
        {
            switch (operationType)
            {
                case OperationType.Set:
                    return operationValue;
                case OperationType.Add:
                    return originalValue + operationValue;
                case OperationType.Subtract:
                    return originalValue - operationValue;
                case OperationType.Multiply:
                    return originalValue * operationValue;
                case OperationType.Divide:
                    if (Mathf.Approximately(operationValue, 0f))
                        return originalValue;
                    return originalValue / operationValue;
                default:
                    return originalValue;
            }
        }
        #endregion

        #region Path Operation Methods
        private void ExecutePathReplace()
        {
            if (allClips.Count == 0)
            {
                EditorUtility.DisplayDialog("No Clips", "Select clips first.", "OK");
                return;
            }

            if (findReplaceRules.Count == 0)
            {
                EditorUtility.DisplayDialog("No Rules", "Add rules first.", "OK");
                return;
            }

            lastOperationLog = "";
            lastModifiedCount = 0;
            clipDuplicationMap.Clear();
            parameterNameMap.Clear();

            // Clear preview after execution
            bool wasShowingPreview = showPreview;
            showPreview = false;

            try
            {
                if (pathEditMode == PathEditMode.ModifyInPlace)
                {
                    ExecuteModifyInPlace();
                }
                else
                {
                    ExecuteCreateDuplicates();
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // Final step: Ensure all .anim files have m_Name matching their filename
                FixAllAnimationClipNames();

                EditorUtility.DisplayDialog("Success! 🎉", lastOperationLog, "OK");
                
                // Restore preview if needed
                if (wasShowingPreview)
                {
                    UpdatePathPreview();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error: {e.Message}");
                lastOperationLog += $"\nERROR: {e.Message}";
                EditorUtility.DisplayDialog("Error", $"Operation failed: {e.Message}", "OK");
                showPreview = wasShowingPreview;
            }
        }

        private void ExecuteModifyInPlace()
        {
            StringComparison comparison = pathCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            try
            {
                // Collect clips that will be modified (for selective backup)
                List<AnimationClip> clipsToBackup = new List<AnimationClip>();

                if (createBackups)
                {
                    EditorUtility.DisplayProgressBar("Scanning clips...", "Checking which clips will be modified", 0f);

                    foreach (var clip in allClips)
                    {
                        if (clip == null) continue;

                        // Check if this clip will have any modifications
                        if (WillClipBeModified(clip))
                        {
                            clipsToBackup.Add(clip);
                        }
                    }

                    EditorUtility.ClearProgressBar();

                    // Create backups only for clips that will be modified
                    if (clipsToBackup.Count > 0)
                    {
                        EditorUtility.DisplayProgressBar("Creating backups...", $"Backing up {clipsToBackup.Count} clip(s)", 0f);
                        CreateBackups(clipsToBackup);
                        EditorUtility.ClearProgressBar();
                    }
                }

                // Apply modifications
                int totalClips = allClips.Count;
                int currentClip = 0;

                foreach (var clip in allClips)
                {
                    if (clip == null) continue;

                    currentClip++;
                    float progress = (float)currentClip / totalClips;
                    EditorUtility.DisplayProgressBar("Modifying clips...", $"Processing {clip.name} ({currentClip}/{totalClips})", progress);

                    int modified = ProcessClipPathReplace(clip, comparison);
                    if (modified > 0)
                    {
                        lastModifiedCount++;
                        if (showDetailedLog)
                        {
                            lastOperationLog += $"[MODIFIED] {clip.name}\n";
                        }
                    }
                }

                EditorUtility.ClearProgressBar();
                lastOperationLog += $"Modified {lastModifiedCount} clips";
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"Error during modification: {e.Message}");
                throw;
            }
        }

        private bool WillClipBeModified(AnimationClip clip)
        {
            if (clip == null) return false;

            try
            {
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);

                foreach (var binding in bindings)
                {
                    string newPath = ApplyFindReplaceRules(binding.path);

                    // If any path will change, the clip will be modified
                    if (!string.IsNullOrEmpty(newPath) && newPath != binding.path)
                    {
                        return true;
                    }
                }

                // Also check object reference bindings
                EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                foreach (var binding in objectBindings)
                {
                    string newPath = ApplyFindReplaceRules(binding.path);

                    if (!string.IsNullOrEmpty(newPath) && newPath != binding.path)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // If we can't determine, assume it will be modified to be safe
                return true;
            }

            return false;
        }

        private bool WillClipValuesBeModified(AnimationClip clip, List<string> propertiesToModify)
        {
            if (clip == null || propertiesToModify == null || propertiesToModify.Count == 0)
                return false;

            try
            {
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);

                foreach (var binding in bindings)
                {
                    // Check if this binding's property is in the list of properties to modify
                    if (propertiesToModify.Contains(binding.propertyName))
                    {
                        AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);

                        // If the curve exists and has keys, it will be modified
                        if (curve != null && curve.keys.Length > 0)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // If we can't determine, assume it will be modified to be safe
                return true;
            }

            return false;
        }

        private void ExecuteCreateDuplicates()
        {
            StringComparison comparison = pathCaseSensitive 
                ? StringComparison.Ordinal 
                : StringComparison.OrdinalIgnoreCase;

            try
            {
                int totalSteps = allClips.Count;
                int currentStep = 0;

                // Collect clips to backup
                List<AnimationClip> clipsToBackup = new List<AnimationClip>();
                
                EditorUtility.DisplayProgressBar("Scanning clips...", "Checking for conflicts", 0f);
                
                foreach (var clip in allClips)
                {
                    if (clip == null) continue;
                    
                    string originalName = clip.name;
                    string newName = ApplyFindReplaceRules(originalName);
                    
                    if (originalName != newName)
                    {
                        ClipAction action = clipActions.ContainsKey(clip) ? clipActions[clip] : ClipAction.Replace;
                        
                        if (action == ClipAction.Replace)
                        {
                            string sourcePath = AssetDatabase.GetAssetPath(clip);
                            string sourceDir = Path.GetDirectoryName(sourcePath);
                            string extension = Path.GetExtension(sourcePath);
                            string targetPath = Path.Combine(sourceDir, newName + extension);
                            
                            if (File.Exists(targetPath))
                            {
                                AnimationClip existingClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);
                                if (existingClip != null)
                                {
                                    clipsToBackup.Add(existingClip);
                                }
                            }
                        }
                    }
                }

                EditorUtility.ClearProgressBar();

                // Create backups
                if (createBackups && clipsToBackup.Count > 0)
                {
                    EditorUtility.DisplayProgressBar("Creating backups...", $"Backing up {clipsToBackup.Count} clip(s)", 0f);
                    CreateBackups(clipsToBackup);
                    EditorUtility.ClearProgressBar();
                }

                // Process clips with progress bar
                foreach (var clip in allClips)
                {
                    if (clip == null) continue;
                    
                    currentStep++;
                    float progress = (float)currentStep / totalSteps;
                    EditorUtility.DisplayProgressBar("Duplicating clips...", $"Processing {clip.name} ({currentStep}/{totalSteps})", progress);
                    
                    string originalName = clip.name;
                    string newName = ApplyFindReplaceRules(originalName);
                    
                    if (originalName != newName)
                    {
                        AnimationClip duplicatedClip = HandleClipDuplication(clip, newName);
                        
                        if (duplicatedClip != null)
                        {
                            ProcessClipPathReplace(duplicatedClip, comparison);
                            clipDuplicationMap[clip] = duplicatedClip;
                            lastModifiedCount++;
                            
                            if (showDetailedLog)
                            {
                                lastOperationLog += $"[CREATED] {newName}\n";
                            }
                        }
                    }
                    else
                    {
                        clipDuplicationMap[clip] = clip;
                        
                        if (showDetailedLog)
                        {
                            lastOperationLog += $"[REUSED] {originalName}\n";
                        }
                    }
                }

                EditorUtility.ClearProgressBar();

                // Force asset database to process all changes before layer duplication
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                // Handle layer duplication
                if (duplicateLayers && selectedAnimator != null && selectedLayers.Count > 0)
                {
                    EditorUtility.DisplayProgressBar("Duplicating layers...", "Creating layer structure", 0.5f);

                    // Build parameter map and ensure parameters exist
                    BuildParameterMap();

                    DuplicateLayersWithSmartClips();
                    EditorUtility.ClearProgressBar();
                }

                lastOperationLog += $"Created {lastModifiedCount} clips\n";
                lastOperationLog += $"Reused {clipDuplicationMap.Count - lastModifiedCount} clips";
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"Error during duplication: {e.Message}\n{e.StackTrace}");
                throw;
            }
        }

        private AnimationClip HandleClipDuplication(AnimationClip source, string newName)
        {
            string sourcePath = AssetDatabase.GetAssetPath(source);
            string sourceDir = Path.GetDirectoryName(sourcePath);
            string extension = Path.GetExtension(sourcePath);
            string targetPath = Path.Combine(sourceDir, newName + extension);

            // Check if file exists
            if (File.Exists(targetPath))
            {
                ClipAction action = clipActions.ContainsKey(source) ? clipActions[source] : ClipAction.Replace;

                if (action == ClipAction.UseExisting)
                {
                    AnimationClip existingClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);
                    if (existingClip != null)
                    {
                        if (showDetailedLog)
                        {
                            lastOperationLog += $"[USING EXISTING] {newName}\n";
                        }
                        return existingClip;
                    }
                }
                else // Replace - EDIT IN-PLACE to maintain references!
                {
                    AnimationClip existingClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);
                    if (existingClip != null)
                    {
                        // Edit the existing clip in-place to maintain all references
                        CopyClipDataInPlace(source, existingClip);

                        if (showDetailedLog)
                        {
                            lastOperationLog += $"[REPLACED IN-PLACE] {newName}\n";
                        }
                        return existingClip;
                    }
                    else
                    {
                        // File exists but couldn't load as clip - delete it
                        AssetDatabase.DeleteAsset(targetPath);
                    }
                }
            }

            // Create new clip by reading source YAML and applying find/replace
            string sourceYAML = File.ReadAllText(sourcePath);
            string modifiedYAML = ApplyYAMLFindReplace(sourceYAML, newName);

            // Write the modified YAML to the target path
            File.WriteAllText(targetPath, modifiedYAML);
            AssetDatabase.Refresh();

            // Load the newly created clip
            AnimationClip newClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);

            if (showDetailedLog && newClip != null)
            {
                lastOperationLog += $"[CREATED] {newName}\n";
            }

            return newClip;
        }

        private void CopyClipDataInPlace(AnimationClip source, AnimationClip target)
        {
            Debug.Log($"[COPY IN-PLACE] Copying from '{source.name}' to '{target.name}'");

            StringComparison comparison = pathCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            // Read source YAML, apply find/replace, write to target
            string sourcePath = AssetDatabase.GetAssetPath(source);
            string targetPath = AssetDatabase.GetAssetPath(target);

            if (!string.IsNullOrEmpty(sourcePath) && !string.IsNullOrEmpty(targetPath))
            {
                // Read source YAML content
                string yamlContent = File.ReadAllText(sourcePath);

                // Get the target filename without extension
                string targetFileName = Path.GetFileNameWithoutExtension(targetPath);

                // Apply find/replace to the entire YAML content
                yamlContent = ApplyYAMLFindReplace(yamlContent, targetFileName);

                // Write to target file
                File.WriteAllText(targetPath, yamlContent);
                AssetDatabase.Refresh();

                Debug.Log($"[COPY IN-PLACE] Successfully copied and processed '{targetFileName}'");
                return;
            }

            // Fallback to old method if file copy fails
            Debug.LogWarning("[COPY IN-PLACE] File copy failed, using fallback method");

            Undo.RecordObject(target, "Replace Animation Clip Data");

            // Copy clip settings
            target.frameRate = source.frameRate;
            target.wrapMode = source.wrapMode;
            target.legacy = source.legacy;

            // Clear all existing bindings from target
            EditorCurveBinding[] existingBindings = AnimationUtility.GetCurveBindings(target);
            foreach (var binding in existingBindings)
            {
                AnimationUtility.SetEditorCurve(target, binding, null);
            }

            EditorCurveBinding[] existingObjectBindings = AnimationUtility.GetObjectReferenceCurveBindings(target);
            foreach (var binding in existingObjectBindings)
            {
                AnimationUtility.SetObjectReferenceCurve(target, binding, null);
            }

            EditorCurveBinding[] sourceBindings = AnimationUtility.GetCurveBindings(source);
            Debug.Log($"[COPY] Found {sourceBindings.Length} curve bindings in source '{source.name}'");

            int emptyPathCount = 0;
            foreach (var binding in sourceBindings)
            {
                if (string.IsNullOrEmpty(binding.path))
                {
                    emptyPathCount++;
                    Debug.Log($"[COPY] Empty path binding: propertyName='{binding.propertyName}', type={binding.type}");
                }

                AnimationCurve curve = AnimationUtility.GetEditorCurve(source, binding);
                if (curve != null)
                {
                    // Apply path modifications
                    string newPath = ApplyFindReplaceRules(binding.path);
                    string newPropertyName = binding.propertyName;

                    // For Animator parameters (empty path, type Animator), apply rules to propertyName
                    if (string.IsNullOrEmpty(binding.path) && binding.type == typeof(Animator))
                    {
                        Debug.Log($"[COPY] Detected Animator parameter: '{binding.propertyName}' → '{ApplyFindReplaceRules(binding.propertyName)}'");
                        newPropertyName = ApplyFindReplaceRules(binding.propertyName);
                    }

                    EditorCurveBinding newBinding = new EditorCurveBinding
                    {
                        path = newPath,
                        propertyName = newPropertyName,
                        type = binding.type
                    };

                    AnimationUtility.SetEditorCurve(target, newBinding, curve);
                }
            }

            // Copy object reference bindings
            EditorCurveBinding[] sourceObjectBindings = AnimationUtility.GetObjectReferenceCurveBindings(source);
            foreach (var binding in sourceObjectBindings)
            {
                ObjectReferenceKeyframe[] keyframes = AnimationUtility.GetObjectReferenceCurve(source, binding);
                if (keyframes != null && keyframes.Length > 0)
                {
                    // Apply path modifications
                    string newPath = ApplyFindReplaceRules(binding.path);
                    string newPropertyName = binding.propertyName;

                    // For Animator parameters (empty path, type Animator), apply rules to propertyName
                    if (string.IsNullOrEmpty(binding.path) && binding.type == typeof(Animator))
                    {
                        newPropertyName = ApplyFindReplaceRules(binding.propertyName);
                    }

                    EditorCurveBinding newBinding = new EditorCurveBinding
                    {
                        path = newPath,
                        propertyName = newPropertyName,
                        type = binding.type
                    };

                    AnimationUtility.SetObjectReferenceCurve(target, newBinding, keyframes);
                }
            }

            // Copy events
            AnimationEvent[] events = AnimationUtility.GetAnimationEvents(source);
            AnimationUtility.SetAnimationEvents(target, events);

            EditorUtility.SetDirty(target);

            // Apply Animator parameter name changes using SerializedObject (low-level modification)
            // Note: We don't need to copy the curves - AnimationUtility.SetEditorCurve already copied them
            // We just need to modify the Animator parameter names in the internal m_FloatCurves/m_EditorCurves
            ModifyAnimatorParameterNames(target);
        }

        private void CopyAnimatorParameterCurvesRaw(AnimationClip source, AnimationClip target)
        {
            Debug.Log($"[COPY CURVES RAW] Copying Animator parameter curves from '{source.name}' to '{target.name}'");

            try
            {
                // Use YAML file manipulation to copy the internal curve data
                string sourcePath = AssetDatabase.GetAssetPath(source);
                string targetPath = AssetDatabase.GetAssetPath(target);

                if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(targetPath))
                {
                    Debug.LogWarning("[COPY CURVES RAW] Source or target path is empty, skipping curve copy");
                    return;
                }

                // Read source YAML
                string[] sourceLines = File.ReadAllLines(sourcePath);
                string[] targetLines = File.ReadAllLines(targetPath);

                // Extract m_FloatCurves, m_EditorCurves, and m_PPtrCurves sections from source
                List<string> floatCurvesLines = ExtractYAMLSection(sourceLines, "m_FloatCurves:");
                List<string> editorCurvesLines = ExtractYAMLSection(sourceLines, "m_EditorCurves:");
                List<string> pptrCurvesLines = ExtractYAMLSection(sourceLines, "m_PPtrCurves:");

                Debug.Log($"[COPY CURVES RAW] Extracted {floatCurvesLines.Count} m_FloatCurves lines, {editorCurvesLines.Count} m_EditorCurves lines, {pptrCurvesLines.Count} m_PPtrCurves lines");

                if (floatCurvesLines.Count == 0 && editorCurvesLines.Count == 0 && pptrCurvesLines.Count == 0)
                {
                    Debug.Log("[COPY CURVES RAW] No Animator parameter curves found in source");
                    return;
                }

                // Replace sections in target YAML
                List<string> newTargetLines = new List<string>(targetLines);
                newTargetLines = ReplaceYAMLSection(newTargetLines, "m_FloatCurves:", floatCurvesLines);
                newTargetLines = ReplaceYAMLSection(newTargetLines, "m_EditorCurves:", editorCurvesLines);
                newTargetLines = ReplaceYAMLSection(newTargetLines, "m_PPtrCurves:", pptrCurvesLines);

                // Write back to target file
                File.WriteAllLines(targetPath, newTargetLines);
                AssetDatabase.Refresh();

                Debug.Log("[COPY CURVES RAW] Successfully copied Animator parameter curves via YAML");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[COPY CURVES RAW] Error copying curves: {e.Message}\n{e.StackTrace}");
            }
        }

        private List<string> ExtractYAMLSection(string[] lines, string sectionName)
        {
            List<string> result = new List<string>();
            int sectionIndex = -1;

            // Find the section
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().StartsWith(sectionName))
                {
                    sectionIndex = i;
                    break;
                }
            }

            if (sectionIndex == -1) return result;

            // Get the indentation level of the section
            int sectionIndent = lines[sectionIndex].TakeWhile(char.IsWhiteSpace).Count();
            result.Add(lines[sectionIndex]);

            // Extract all lines that belong to this section (have greater indentation)
            for (int i = sectionIndex + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                int lineIndent = line.TakeWhile(char.IsWhiteSpace).Count();

                // If we hit another top-level section, stop
                if (lineIndent <= sectionIndent && !string.IsNullOrWhiteSpace(line.Trim()))
                {
                    break;
                }

                result.Add(line);
            }

            return result;
        }

        private List<string> ReplaceYAMLSection(List<string> lines, string sectionName, List<string> newSectionLines)
        {
            if (newSectionLines.Count == 0) return lines;

            List<string> result = new List<string>();
            int sectionIndex = -1;

            // Find the section
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim().StartsWith(sectionName))
                {
                    sectionIndex = i;
                    break;
                }
            }

            if (sectionIndex == -1)
            {
                // Section doesn't exist, just return original
                return lines;
            }

            // Get the indentation level of the section
            int sectionIndent = lines[sectionIndex].TakeWhile(char.IsWhiteSpace).Count();

            // Copy lines before the section
            for (int i = 0; i < sectionIndex; i++)
            {
                result.Add(lines[i]);
            }

            // Add the new section
            result.AddRange(newSectionLines);

            // Skip old section lines and find where to resume
            int resumeIndex = sectionIndex + 1;
            for (int i = resumeIndex; i < lines.Count; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    resumeIndex++;
                    continue;
                }

                int lineIndent = line.TakeWhile(char.IsWhiteSpace).Count();

                // If we hit another top-level section, stop skipping
                if (lineIndent <= sectionIndent && !string.IsNullOrWhiteSpace(line.Trim()))
                {
                    resumeIndex = i;
                    break;
                }
                resumeIndex++;
            }

            // Copy remaining lines
            for (int i = resumeIndex; i < lines.Count; i++)
            {
                result.Add(lines[i]);
            }

            return result;
        }

        private void FixAllAnimationClipNames()
        {
            Debug.Log("[FIX NAMES] Fixing m_Name in all animation clips to match filenames...");

            try
            {
                // Get all animation clips that were processed
                HashSet<string> processedPaths = new HashSet<string>();

                // Collect all clip paths from allClips
                foreach (var clip in allClips)
                {
                    if (clip != null)
                    {
                        string path = AssetDatabase.GetAssetPath(clip);
                        if (!string.IsNullOrEmpty(path))
                        {
                            processedPaths.Add(path);
                        }
                    }
                }

                // Also collect from clipDuplicationMap (duplicated clips)
                foreach (var kvp in clipDuplicationMap)
                {
                    if (kvp.Value != null)
                    {
                        string path = AssetDatabase.GetAssetPath(kvp.Value);
                        if (!string.IsNullOrEmpty(path))
                        {
                            processedPaths.Add(path);
                        }
                    }
                }

                int fixedCount = 0;
                foreach (string clipPath in processedPaths)
                {
                    if (FixAnimationClipName(clipPath))
                    {
                        fixedCount++;
                    }
                }

                if (fixedCount > 0)
                {
                    AssetDatabase.Refresh();
                    Debug.Log($"[FIX NAMES] Fixed m_Name in {fixedCount} animation clip(s)");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[FIX NAMES] Error: {e.Message}");
            }
        }

        private bool FixAnimationClipName(string clipPath)
        {
            try
            {
                if (!File.Exists(clipPath))
                {
                    return false;
                }

                // Get the filename without extension
                string fileName = Path.GetFileNameWithoutExtension(clipPath);

                // Read the YAML file
                string yamlContent = File.ReadAllText(clipPath);

                // Fix the m_Name field to match the filename
                string namePattern = @"(^\s*m_Name:\s+)(.*)$";
                string newYamlContent = System.Text.RegularExpressions.Regex.Replace(
                    yamlContent,
                    namePattern,
                    m => m.Groups[1].Value + fileName,
                    System.Text.RegularExpressions.RegexOptions.Multiline
                );

                // Only write if changed
                if (newYamlContent != yamlContent)
                {
                    File.WriteAllText(clipPath, newYamlContent);
                    Debug.Log($"[FIX NAMES] Fixed '{fileName}' - m_Name now matches filename");
                    return true;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[FIX NAMES] Error fixing '{clipPath}': {e.Message}");
            }

            return false;
        }

        private string ApplyYAMLFindReplace(string yamlContent, string targetFileName)
        {
            Debug.Log($"[YAML FIND/REPLACE] Processing for target: '{targetFileName}'");

            int totalReplacements = 0;

            // First, fix the m_Name field to match the filename
            string namePattern = @"(^\s*m_Name:\s+)(.*)$";
            yamlContent = System.Text.RegularExpressions.Regex.Replace(
                yamlContent,
                namePattern,
                m => m.Groups[1].Value + targetFileName,
                System.Text.RegularExpressions.RegexOptions.Multiline
            );
            Debug.Log($"[YAML FIND/REPLACE] Set m_Name to '{targetFileName}'");

            // Apply all find/replace rules to the entire content
            foreach (var rule in findReplaceRules)
            {
                if (!rule.enabled) continue;

                string findPattern = rule.find;
                string replacePattern = rule.replace;

                // Count occurrences before replacement
                int countBefore = System.Text.RegularExpressions.Regex.Matches(yamlContent, System.Text.RegularExpressions.Regex.Escape(findPattern)).Count;

                // Simple text replacement throughout the entire file
                yamlContent = yamlContent.Replace(findPattern, replacePattern);

                // Count occurrences after replacement
                int countAfter = System.Text.RegularExpressions.Regex.Matches(yamlContent, System.Text.RegularExpressions.Regex.Escape(findPattern)).Count;
                int replaced = countBefore - countAfter;

                if (replaced > 0)
                {
                    totalReplacements += replaced;
                    Debug.Log($"[YAML FIND/REPLACE] Replaced {replaced} occurrence(s) of '{findPattern}' → '{replacePattern}'");
                }
            }

            Debug.Log($"[YAML FIND/REPLACE] Total replacements: {totalReplacements}");
            return yamlContent;
        }

        private void ModifyAnimatorParameterNames(AnimationClip clip)
        {
            Debug.Log($"========== MODIFYING ANIMATOR PARAMETERS VIA YAML: {clip.name} ==========");

            try
            {
                string clipPath = AssetDatabase.GetAssetPath(clip);
                if (string.IsNullOrEmpty(clipPath))
                {
                    Debug.LogWarning("[YAML REPLACE] Clip path is empty, skipping");
                    return;
                }

                // Read the YAML file
                string yamlContent = File.ReadAllText(clipPath);
                string originalContent = yamlContent;
                int totalReplacements = 0;

                // Apply find/replace rules to attribute fields
                // We need to find lines like:    attribute: Gesture_Foot_Open_Weight_Smooth_Output_L
                // and replace them with:         attribute: Gesture_Foot_Open_Weight_Smooth_Output_R
                // But SKIP m_Name field

                foreach (var rule in findReplaceRules)
                {
                    if (!rule.enabled) continue;

                    string findPattern = rule.find;
                    string replacePattern = rule.replace;

                    // Use regex to find "attribute: <value>" lines, but NOT "m_Name: <value>" lines
                    // Pattern: match "attribute: <text containing our find string>" where path is empty
                    string regexPattern = @"(^\s+attribute:\s+)(" + System.Text.RegularExpressions.Regex.Escape(findPattern) + @"[^\r\n]*)";

                    System.Text.RegularExpressions.MatchCollection matches =
                        System.Text.RegularExpressions.Regex.Matches(yamlContent, regexPattern, System.Text.RegularExpressions.RegexOptions.Multiline);

                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        string fullLine = match.Value;
                        string indent = match.Groups[1].Value;
                        string attributeValue = match.Groups[2].Value;

                        // Apply the replacement
                        string newAttributeValue = attributeValue.Replace(findPattern, replacePattern);

                        if (newAttributeValue != attributeValue)
                        {
                            string newLine = indent + newAttributeValue;
                            yamlContent = yamlContent.Replace(fullLine, newLine);
                            totalReplacements++;
                            Debug.Log($"✓ [YAML REPLACE] '{attributeValue}' → '{newAttributeValue}'");
                        }
                    }
                }

                // Write back if changes were made
                if (yamlContent != originalContent)
                {
                    File.WriteAllText(clipPath, yamlContent);
                    AssetDatabase.Refresh();
                    Debug.Log($"[YAML REPLACE] Successfully modified {totalReplacements} attribute(s) in '{clip.name}'");
                }
                else
                {
                    Debug.Log($"[YAML REPLACE] No changes needed for '{clip.name}'");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[YAML REPLACE] Error modifying YAML: {e.Message}\n{e.StackTrace}");
            }

            Debug.Log($"========== END MODIFYING ANIMATOR PARAMETERS ==========\n");
        }

        private void ModifyAnimatorParameterNames_OLD_BROKEN(AnimationClip clip)
        {
            Debug.Log($"========== MODIFYING ANIMATOR PARAMETERS: {clip.name} ==========");

            // Use SerializedObject to directly modify the internal animation data
            SerializedObject serializedClip = new SerializedObject(clip);
            serializedClip.Update(); // Ensure we have the latest data
            int totalModified = 0;

            // m_FloatCurves contains the Animator float parameter curves
            SerializedProperty floatCurves = serializedClip.FindProperty("m_FloatCurves");
            Debug.Log($"[DEBUG] m_FloatCurves found: {floatCurves != null}");

            if (floatCurves != null && floatCurves.isArray)
            {
                Debug.Log($"[DEBUG] m_FloatCurves array size: {floatCurves.arraySize}");

                for (int i = 0; i < floatCurves.arraySize; i++)
                {
                    SerializedProperty curve = floatCurves.GetArrayElementAtIndex(i);
                    SerializedProperty attribute = curve.FindPropertyRelative("attribute");
                    SerializedProperty path = curve.FindPropertyRelative("path");

                    Debug.Log($"[DEBUG] FloatCurve {i}:");
                    Debug.Log($"  - attribute: '{(attribute != null ? attribute.stringValue : "null")}'");
                    Debug.Log($"  - path: '{(path != null ? path.stringValue : "null")}'");

                    // Only process Animator parameters (empty path)
                    if (path != null && string.IsNullOrEmpty(path.stringValue) &&
                        attribute != null && !string.IsNullOrEmpty(attribute.stringValue))
                    {
                        string oldName = attribute.stringValue;
                        string newName = ApplyFindReplaceRules(oldName);

                        Debug.Log($"[DEBUG] Applying rules: '{oldName}' → '{newName}'");

                        if (newName != oldName)
                        {
                            Debug.Log($"✓ [MODIFYING ATTRIBUTE] '{oldName}' → '{newName}'");
                            attribute.stringValue = newName;
                            totalModified++;
                        }
                        else
                        {
                            Debug.Log($"[SKIP] No change needed for '{oldName}'");
                        }
                    }
                    else
                    {
                        Debug.Log($"[SKIP] Not an Animator parameter or invalid data");
                    }
                }
            }
            else
            {
                Debug.LogWarning("[WARNING] m_FloatCurves is null or not an array!");
            }

            // CRITICAL: Also modify m_EditorCurves (Unity stores curves in both places!)
            SerializedProperty editorCurves = serializedClip.FindProperty("m_EditorCurves");
            Debug.Log($"[DEBUG] m_EditorCurves found: {editorCurves != null}");

            if (editorCurves != null && editorCurves.isArray)
            {
                Debug.Log($"[DEBUG] m_EditorCurves array size: {editorCurves.arraySize}");

                for (int i = 0; i < editorCurves.arraySize; i++)
                {
                    SerializedProperty curve = editorCurves.GetArrayElementAtIndex(i);
                    SerializedProperty attribute = curve.FindPropertyRelative("attribute");
                    SerializedProperty path = curve.FindPropertyRelative("path");

                    Debug.Log($"[DEBUG] EditorCurve {i}:");
                    Debug.Log($"  - attribute: '{(attribute != null ? attribute.stringValue : "null")}'");
                    Debug.Log($"  - path: '{(path != null ? path.stringValue : "null")}'");

                    // Only process Animator parameters (empty path)
                    if (path != null && string.IsNullOrEmpty(path.stringValue) &&
                        attribute != null && !string.IsNullOrEmpty(attribute.stringValue))
                    {
                        string oldName = attribute.stringValue;
                        string newName = ApplyFindReplaceRules(oldName);

                        Debug.Log($"[DEBUG] Applying rules to EditorCurve: '{oldName}' → '{newName}'");

                        if (newName != oldName)
                        {
                            Debug.Log($"✓ [MODIFYING EDITOR CURVE ATTRIBUTE] '{oldName}' → '{newName}'");
                            attribute.stringValue = newName;
                            totalModified++;
                        }
                        else
                        {
                            Debug.Log($"[SKIP] No change needed for EditorCurve '{oldName}'");
                        }
                    }
                    else
                    {
                        Debug.Log($"[SKIP] EditorCurve not an Animator parameter or invalid data");
                    }
                }
            }
            else
            {
                Debug.LogWarning("[WARNING] m_EditorCurves is null or not an array!");
            }

            // Also check m_PPtrCurves
            SerializedProperty pptrCurves = serializedClip.FindProperty("m_PPtrCurves");
            Debug.Log($"[DEBUG] m_PPtrCurves found: {pptrCurves != null}");

            if (pptrCurves != null && pptrCurves.isArray)
            {
                Debug.Log($"[DEBUG] m_PPtrCurves array size: {pptrCurves.arraySize}");

                for (int i = 0; i < pptrCurves.arraySize; i++)
                {
                    SerializedProperty curve = pptrCurves.GetArrayElementAtIndex(i);
                    SerializedProperty attribute = curve.FindPropertyRelative("attribute");
                    SerializedProperty path = curve.FindPropertyRelative("path");

                    if (path != null && string.IsNullOrEmpty(path.stringValue) &&
                        attribute != null && !string.IsNullOrEmpty(attribute.stringValue))
                    {
                        string oldName = attribute.stringValue;
                        string newName = ApplyFindReplaceRules(oldName);

                        if (newName != oldName)
                        {
                            Debug.Log($"✓ [MODIFYING PPtrCurve] '{oldName}' → '{newName}'");
                            attribute.stringValue = newName;
                            totalModified++;
                        }
                    }
                }
            }

            bool applied = serializedClip.ApplyModifiedProperties();
            Debug.Log($"[DEBUG] ApplyModifiedProperties returned: {applied}");
            Debug.Log($"[SUMMARY] Total Animator parameters modified: {totalModified}");
            Debug.Log($"========== END MODIFYING ANIMATOR PARAMETERS ==========\n");

            if (totalModified > 0)
            {
                EditorUtility.SetDirty(clip);
                AssetDatabase.SaveAssets();
            }
        }

        private int ProcessClipPathReplace(AnimationClip clip, StringComparison comparison)
        {
            int modifiedCount = 0;

            try
            {
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
                List<EditorCurveBinding> bindingsToRemove = new List<EditorCurveBinding>();
                Dictionary<EditorCurveBinding, AnimationCurve> bindingsToAdd = new Dictionary<EditorCurveBinding, AnimationCurve>();

                foreach (var binding in bindings)
                {
                    string newPath = ApplyFindReplaceRules(binding.path);
                    string newPropertyName = binding.propertyName;

                    // For Animator parameters (empty path, type Animator), apply rules to propertyName
                    bool isAnimatorParameter = string.IsNullOrEmpty(binding.path) && binding.type == typeof(Animator);
                    if (isAnimatorParameter)
                    {
                        newPropertyName = ApplyFindReplaceRules(binding.propertyName);
                    }

                    // Check if anything changed
                    bool pathChanged = !string.IsNullOrEmpty(newPath) && newPath != binding.path;
                    bool propertyNameChanged = newPropertyName != binding.propertyName;

                    if (pathChanged || propertyNameChanged)
                    {
                        AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve != null)
                        {
                            EditorCurveBinding newBinding = new EditorCurveBinding
                            {
                                path = newPath,
                                propertyName = newPropertyName,
                                type = binding.type
                            };

                            bindingsToRemove.Add(binding);
                            bindingsToAdd[newBinding] = curve;
                            modifiedCount++;
                        }
                    }
                }

                if (bindingsToRemove.Count > 0)
                {
                    Undo.RecordObject(clip, "Path Replace");

                    foreach (var binding in bindingsToRemove)
                    {
                        AnimationUtility.SetEditorCurve(clip, binding, null);
                    }

                    foreach (var kvp in bindingsToAdd)
                    {
                        AnimationUtility.SetEditorCurve(clip, kvp.Key, kvp.Value);
                    }

                    EditorUtility.SetDirty(clip);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error in {clip.name}: {e.Message}");
            }

            // Apply Animator parameter name changes using SerializedObject (low-level modification)
            ModifyAnimatorParameterNames(clip);

            return modifiedCount;
        }

        private void BuildParameterMap()
        {
            parameterNameMap.Clear();

            if (selectedAnimator == null) return;

            // Collect all parameters from selected layers
            HashSet<string> parametersToMap = new HashSet<string>();

            StringComparison comparison = pathCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            foreach (var layer in selectedLayers)
            {
                CollectParametersFromStateMachine(layer.stateMachine, parametersToMap);
            }

            // For each parameter, apply find/replace rules and ensure it exists
            foreach (string originalParam in parametersToMap)
            {
                string newParam = ApplyFindReplaceRules(originalParam);

                if (originalParam != newParam)
                {
                    parameterNameMap[originalParam] = newParam;
                    EnsureParameterExists(originalParam, newParam);

                    if (showDetailedLog)
                    {
                        lastOperationLog += $"[PARAM] '{originalParam}' → '{newParam}'\n";
                    }
                }
                else
                {
                    // If name didn't change, still map to itself
                    parameterNameMap[originalParam] = originalParam;
                }
            }
        }

        private void CollectParametersFromStateMachine(AnimatorStateMachine stateMachine, HashSet<string> parameters)
        {
            if (stateMachine == null) return;

            // Collect from state transitions
            foreach (var childState in stateMachine.states)
            {
                if (childState.state == null) continue;

                // Collect from blend trees
                if (childState.state.motion is BlendTree blendTree)
                {
                    CollectParametersFromBlendTree(blendTree, parameters);
                }

                // Collect from state transitions
                foreach (var transition in childState.state.transitions)
                {
                    foreach (var condition in transition.conditions)
                    {
                        if (!string.IsNullOrEmpty(condition.parameter))
                        {
                            parameters.Add(condition.parameter);
                        }
                    }
                }
            }

            // Collect from any state transitions
            foreach (var transition in stateMachine.anyStateTransitions)
            {
                foreach (var condition in transition.conditions)
                {
                    if (!string.IsNullOrEmpty(condition.parameter))
                    {
                        parameters.Add(condition.parameter);
                    }
                }
            }

            // Collect from entry transitions
            foreach (var transition in stateMachine.entryTransitions)
            {
                foreach (var condition in transition.conditions)
                {
                    if (!string.IsNullOrEmpty(condition.parameter))
                    {
                        parameters.Add(condition.parameter);
                    }
                }
            }

            // Collect from sub-state machines
            foreach (var childMachine in stateMachine.stateMachines)
            {
                CollectParametersFromStateMachine(childMachine.stateMachine, parameters);
            }
        }

        private void CollectParametersFromBlendTree(BlendTree blendTree, HashSet<string> parameters)
        {
            if (blendTree == null) return;

            if (!string.IsNullOrEmpty(blendTree.blendParameter))
            {
                parameters.Add(blendTree.blendParameter);
            }

            if (!string.IsNullOrEmpty(blendTree.blendParameterY))
            {
                parameters.Add(blendTree.blendParameterY);
            }

            // Collect from child blend trees
            foreach (var child in blendTree.children)
            {
                if (child.motion is BlendTree childTree)
                {
                    CollectParametersFromBlendTree(childTree, parameters);
                }

                if (!string.IsNullOrEmpty(child.directBlendParameter))
                {
                    parameters.Add(child.directBlendParameter);
                }
            }
        }

        private void EnsureParameterExists(string originalParamName, string newParamName)
        {
            if (selectedAnimator == null) return;

            // Check if new parameter already exists
            foreach (var param in selectedAnimator.parameters)
            {
                if (param.name == newParamName)
                {
                    // Parameter exists, no need to create
                    return;
                }
            }

            // Find original parameter to copy settings from
            AnimatorControllerParameter originalParam = null;
            foreach (var param in selectedAnimator.parameters)
            {
                if (param.name == originalParamName)
                {
                    originalParam = param;
                    break;
                }
            }

            if (originalParam != null)
            {
                // Create new parameter with same settings
                selectedAnimator.AddParameter(newParamName, originalParam.type);

                // Find the newly created parameter and copy default value
                foreach (var param in selectedAnimator.parameters)
                {
                    if (param.name == newParamName)
                    {
                        param.defaultBool = originalParam.defaultBool;
                        param.defaultFloat = originalParam.defaultFloat;
                        param.defaultInt = originalParam.defaultInt;
                        break;
                    }
                }

                if (showDetailedLog)
                {
                    lastOperationLog += $"[CREATED PARAM] '{newParamName}' (type: {originalParam.type})\n";
                }
            }
        }

        private string GetMappedParameterName(string originalName)
        {
            if (string.IsNullOrEmpty(originalName)) return originalName;

            if (parameterNameMap.ContainsKey(originalName))
            {
                return parameterNameMap[originalName];
            }

            return originalName;
        }

        private void DuplicateLayersWithSmartClips()
        {
            if (selectedAnimator == null || selectedLayers.Count == 0) return;

            try
            {
                // Get fresh copy of layers array
                var originalLayers = selectedAnimator.layers;
                
                // Create a list to track insertions: (originalIndex, newLayer, placement)
                List<(int originalIndex, AnimatorControllerLayer newLayer)> layersToInsert = new List<(int, AnimatorControllerLayer)>();

                StringComparison comparison = pathCaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                // Track created layer names to avoid duplicates
                HashSet<string> createdLayerNames = new HashSet<string>();

                // First pass: Create all new layers
                foreach (var sourceLayer in selectedLayers)
                {
                    // Find original index by NAME (not by reference - Unity returns a copy!)
                    int originalIndex = -1;
                    for (int i = 0; i < originalLayers.Length; i++)
                    {
                        if (originalLayers[i].name == sourceLayer.name)
                        {
                            originalIndex = i;
                            break;
                        }
                    }

                    if (originalIndex == -1)
                    {
                        Debug.LogWarning($"Could not find layer '{sourceLayer.name}' in animator");
                        continue;
                    }

                    string newLayerName = ApplyFindReplaceRules(sourceLayer.name) + customLayerSuffix;

                    // Skip if we already created a layer with this name
                    if (createdLayerNames.Contains(newLayerName))
                    {
                        if (showDetailedLog)
                        {
                            lastOperationLog += $"[SKIPPED DUPLICATE] '{newLayerName}' (from '{sourceLayer.name}')\n";
                        }
                        Debug.LogWarning($"Skipping duplicate layer '{newLayerName}' - already created from another source layer");
                        continue;
                    }

                    // Check if layer with this name already exists in animator
                    if (originalLayers.Any(l => l.name == newLayerName))
                    {
                        if (showDetailedLog)
                        {
                            lastOperationLog += $"[SKIPPED EXISTING] '{newLayerName}' already exists in animator\n";
                        }
                        Debug.LogWarning($"Skipping layer '{newLayerName}' - already exists in animator");
                        continue;
                    }

                    createdLayerNames.Add(newLayerName);

                    AnimatorControllerLayer newLayer = new AnimatorControllerLayer
                    {
                        name = newLayerName,
                        stateMachine = new AnimatorStateMachine
                        {
                            name = newLayerName,
                            hideFlags = HideFlags.HideInHierarchy
                        },
                        avatarMask = sourceLayer.avatarMask,
                        blendingMode = sourceLayer.blendingMode,
                        defaultWeight = 1.0f,
                        syncedLayerIndex = sourceLayer.syncedLayerIndex,
                        iKPass = sourceLayer.iKPass
                    };

                    AssetDatabase.AddObjectToAsset(newLayer.stateMachine, selectedAnimator);
                    
                    // Use the ORIGINAL layer from the array, not selectedLayers reference
                    DuplicateStateMachine(originalLayers[originalIndex].stateMachine, newLayer.stateMachine, comparison);

                    layersToInsert.Add((originalIndex, newLayer));

                    if (showDetailedLog)
                    {
                        lastOperationLog += $"[LAYER] '{newLayerName}' (from '{sourceLayer.name}')\n";
                    }
                }

                if (layersToInsert.Count == 0)
                {
                    Debug.LogWarning("No layers were duplicated. Check if selected layers exist in animator.");
                    return;
                }

                // Second pass: Insert layers at correct positions
                // Build the final layer array
                List<AnimatorControllerLayer> finalLayers = new List<AnimatorControllerLayer>(originalLayers);

                // Sort insertions based on placement strategy
                switch (layerPlacement)
                {
                    case LayerPlacement.BelowOriginal:
                        // Insert from bottom to top to maintain correct indices
                        layersToInsert.Sort((a, b) => b.originalIndex.CompareTo(a.originalIndex));
                        foreach (var insertion in layersToInsert)
                        {
                            finalLayers.Insert(insertion.originalIndex + 1, insertion.newLayer);
                        }
                        break;

                    case LayerPlacement.AboveOriginal:
                        // Insert from bottom to top to maintain correct indices
                        layersToInsert.Sort((a, b) => b.originalIndex.CompareTo(a.originalIndex));
                        foreach (var insertion in layersToInsert)
                        {
                            finalLayers.Insert(insertion.originalIndex, insertion.newLayer);
                        }
                        break;

                    case LayerPlacement.AtEnd:
                        // Just add all to the end
                        foreach (var insertion in layersToInsert)
                        {
                            finalLayers.Add(insertion.newLayer);
                        }
                        break;
                }

                // Apply the new layer array
                selectedAnimator.layers = finalLayers.ToArray();
                EditorUtility.SetDirty(selectedAnimator);

                Debug.Log($"Successfully duplicated {layersToInsert.Count} layer(s)");
            }
            catch (Exception e)
            {
                Debug.LogError($"Error duplicating layers: {e.Message}\n{e.StackTrace}");
                lastOperationLog += $"\n[ERROR] Layer duplication failed: {e.Message}";
                throw;
            }
        }

        private void DuplicateStateMachine(AnimatorStateMachine source, AnimatorStateMachine dest, StringComparison comparison)
        {
            Dictionary<AnimatorState, AnimatorState> stateMap = new Dictionary<AnimatorState, AnimatorState>();

            // Copy states
            foreach (var childState in source.states)
            {
                AnimatorState sourceState = childState.state;
                AnimatorState newState = dest.AddState(sourceState.name, childState.position);

                newState.speed = sourceState.speed;
                newState.cycleOffset = sourceState.cycleOffset;
                newState.writeDefaultValues = sourceState.writeDefaultValues;
                newState.tag = sourceState.tag;
                newState.iKOnFeet = sourceState.iKOnFeet;
                newState.mirror = sourceState.mirror;

                // Copy parameter-driven properties
                newState.timeParameterActive = sourceState.timeParameterActive;
                newState.timeParameter = GetMappedParameterName(sourceState.timeParameter);
                newState.speedParameterActive = sourceState.speedParameterActive;
                newState.speedParameter = GetMappedParameterName(sourceState.speedParameter);
                newState.cycleOffsetParameterActive = sourceState.cycleOffsetParameterActive;
                newState.cycleOffsetParameter = GetMappedParameterName(sourceState.cycleOffsetParameter);
                newState.mirrorParameterActive = sourceState.mirrorParameterActive;
                newState.mirrorParameter = GetMappedParameterName(sourceState.mirrorParameter);

                if (sourceState.motion is AnimationClip sourceClip)
                {
                    if (clipDuplicationMap.ContainsKey(sourceClip))
                    {
                        newState.motion = clipDuplicationMap[sourceClip];
                    }
                    else
                    {
                        newState.motion = sourceClip;
                    }
                }
                else if (sourceState.motion is BlendTree sourceTree)
                {
                    BlendTree newTree = DuplicateBlendTree(sourceTree, comparison, dest);
                    newState.motion = newTree;
                }

                stateMap[sourceState] = newState;
            }

            // Copy transitions
            foreach (var childState in source.states)
            {
                AnimatorState sourceState = childState.state;
                AnimatorState newState = stateMap[sourceState];

                foreach (var transition in sourceState.transitions)
                {
                    AnimatorState destState = null;
                    if (transition.destinationState != null && stateMap.ContainsKey(transition.destinationState))
                    {
                        destState = stateMap[transition.destinationState];
                    }

                    AnimatorStateTransition newTransition;
                    if (destState != null)
                    {
                        newTransition = newState.AddTransition(destState);
                    }
                    else
                    {
                        newTransition = newState.AddExitTransition();
                    }

                    CopyTransitionSettings(transition, newTransition);
                }
            }

            if (source.defaultState != null && stateMap.ContainsKey(source.defaultState))
            {
                dest.defaultState = stateMap[source.defaultState];
            }

            foreach (var transition in source.anyStateTransitions)
            {
                if (transition.destinationState != null && stateMap.ContainsKey(transition.destinationState))
                {
                    var newTransition = dest.AddAnyStateTransition(stateMap[transition.destinationState]);
                    CopyTransitionSettings(transition, newTransition);
                }
            }

            foreach (var transition in source.entryTransitions)
            {
                if (transition.destinationState != null && stateMap.ContainsKey(transition.destinationState))
                {
                    var newTransition = dest.AddEntryTransition(stateMap[transition.destinationState]);
                    foreach (var condition in transition.conditions)
                    {
                        string mappedParam = GetMappedParameterName(condition.parameter);
                        newTransition.AddCondition(condition.mode, condition.threshold, mappedParam);
                    }
                }
            }

            foreach (var childMachine in source.stateMachines)
            {
                var newChildMachine = dest.AddStateMachine(childMachine.stateMachine.name, childMachine.position);
                newChildMachine.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(newChildMachine, selectedAnimator);
                DuplicateStateMachine(childMachine.stateMachine, newChildMachine, comparison);
            }
        }

        private BlendTree DuplicateBlendTree(BlendTree source, StringComparison comparison, AnimatorStateMachine parentStateMachine)
        {
            BlendTree newBlendTree = new BlendTree
            {
                name = source.name,
                blendType = source.blendType,
                blendParameter = GetMappedParameterName(source.blendParameter),
                blendParameterY = GetMappedParameterName(source.blendParameterY),
                minThreshold = source.minThreshold,
                maxThreshold = source.maxThreshold,
                useAutomaticThresholds = source.useAutomaticThresholds,
                hideFlags = HideFlags.HideInHierarchy
            };

            if (selectedAnimator != null)
            {
                AssetDatabase.AddObjectToAsset(newBlendTree, selectedAnimator);
            }

            var children = source.children;
            int newChildIndex = 0;
            for (int i = 0; i < children.Length; i++)
            {
                var child = children[i];

                Motion newMotion = null;
                if (child.motion is AnimationClip clip)
                {
                    if (clipDuplicationMap.ContainsKey(clip))
                    {
                        newMotion = clipDuplicationMap[clip];
                    }
                    else
                    {
                        newMotion = clip;
                    }
                }
                else if (child.motion is BlendTree childBlendTree)
                {
                    newMotion = DuplicateBlendTree(childBlendTree, comparison, parentStateMachine);
                }

                if (newMotion != null)
                {
                    newBlendTree.AddChild(newMotion, child.threshold);

                    var newChildren = newBlendTree.children;
                    newChildren[newChildIndex].position = child.position;
                    newChildren[newChildIndex].timeScale = child.timeScale;
                    newChildren[newChildIndex].cycleOffset = child.cycleOffset;
                    newChildren[newChildIndex].directBlendParameter = GetMappedParameterName(child.directBlendParameter);
                    newChildren[newChildIndex].mirror = child.mirror;
                    newBlendTree.children = newChildren;
                    newChildIndex++;
                }
            }

            EditorUtility.SetDirty(newBlendTree);
            return newBlendTree;
        }

        private void CopyTransitionSettings(AnimatorStateTransition source, AnimatorStateTransition dest)
        {
            dest.hasExitTime = source.hasExitTime;
            dest.exitTime = source.exitTime;
            dest.hasFixedDuration = source.hasFixedDuration;
            dest.duration = source.duration;
            dest.offset = source.offset;
            dest.interruptionSource = source.interruptionSource;
            dest.orderedInterruption = source.orderedInterruption;
            dest.canTransitionToSelf = source.canTransitionToSelf;
            dest.mute = source.mute;
            dest.solo = source.solo;

            foreach (var condition in source.conditions)
            {
                string mappedParam = GetMappedParameterName(condition.parameter);
                dest.AddCondition(condition.mode, condition.threshold, mappedParam);
            }
        }
        #endregion

        #region Helper Methods
        private void RefreshAnimatorData()
        {
            layerSelectionStates.Clear();
            clipSelectionFromAnimator.Clear();
            selectedLayers.Clear();

            if (selectedAnimator != null)
            {
                foreach (var layer in selectedAnimator.layers)
                {
                    layerSelectionStates[layer.name] = false;
                }
            }

            RefreshAllClips();
        }

        private void RefreshClipsFromAnimator()
        {
            clipSelectionFromAnimator.Clear();

            if (selectedAnimator == null) return;

            foreach (var layer in selectedAnimator.layers)
            {
                if (!layerSelectionStates.ContainsKey(layer.name) || !layerSelectionStates[layer.name])
                    continue;

                var clips = GetClipsFromLayer(layer);
                foreach (var clip in clips)
                {
                    if (clip != null)
                    {
                        string key = GetClipKey(clip);
                        if (!clipSelectionFromAnimator.ContainsKey(key))
                        {
                            clipSelectionFromAnimator[key] = true;
                        }
                    }
                }
            }

            // Debug: Log selected layers count
            if (showDebugLog)
            {
                Debug.Log($"RefreshClipsFromAnimator: {selectedLayers.Count} layers in selectedLayers list");
                foreach (var layer in selectedLayers)
                {
                    Debug.Log($"  - Layer: {layer.name}");
                }
            }

            RefreshAllClips();
        }

        private List<AnimationClip> GetClipsFromLayer(AnimatorControllerLayer layer)
        {
            List<AnimationClip> clips = new List<AnimationClip>();
            
            if (layer.stateMachine != null)
            {
                GetClipsFromStateMachine(layer.stateMachine, clips);
            }

            return clips;
        }

        private void GetClipsFromStateMachine(AnimatorStateMachine stateMachine, List<AnimationClip> clips)
        {
            foreach (var childState in stateMachine.states)
            {
                if (childState.state.motion is AnimationClip clip)
                {
                    if (!clips.Contains(clip))
                    {
                        clips.Add(clip);
                    }
                }
                else if (childState.state.motion is BlendTree blendTree)
                {
                    GetClipsFromBlendTree(blendTree, clips);
                }
            }

            foreach (var childMachine in stateMachine.stateMachines)
            {
                GetClipsFromStateMachine(childMachine.stateMachine, clips);
            }
        }

        private void GetClipsFromBlendTree(BlendTree blendTree, List<AnimationClip> clips)
        {
            foreach (var child in blendTree.children)
            {
                if (child.motion is AnimationClip clip)
                {
                    if (!clips.Contains(clip))
                    {
                        clips.Add(clip);
                    }
                }
                else if (child.motion is BlendTree childTree)
                {
                    GetClipsFromBlendTree(childTree, clips);
                }
            }
        }

        private string GetClipKey(AnimationClip clip)
        {
            return AssetDatabase.GetAssetPath(clip) + "::" + clip.GetInstanceID();
        }

        private void RefreshAllClips()
        {
            allClips.Clear();
            HashSet<AnimationClip> uniqueClips = new HashSet<AnimationClip>();

            foreach (var kvp in clipSelectionFromAnimator)
            {
                if (kvp.Value)
                {
                    string path = kvp.Key.Split(new[] { "::" }, StringSplitOptions.None)[0];
                    AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                    if (clip != null)
                    {
                        uniqueClips.Add(clip);
                    }
                }
            }

            for (int i = 0; i < manualClips.Count && i < manualClipSelectionStates.Count; i++)
            {
                if (manualClipSelectionStates[i] && manualClips[i] != null)
                {
                    uniqueClips.Add(manualClips[i]);
                }
            }

            allClips = uniqueClips.ToList();

            needsPropertyRefresh = true;
            needsPathRefresh = true;
        }

        private void AddSelectedClipsFromProject()
        {
            UnityEngine.Object[] selectedObjects = Selection.objects;
            int addedCount = 0;

            foreach (var obj in selectedObjects)
            {
                if (obj is AnimationClip clip)
                {
                    if (!manualClips.Contains(clip))
                    {
                        manualClips.Add(clip);
                        manualClipSelectionStates.Add(true);
                        addedCount++;
                    }
                }
            }

            if (addedCount > 0)
            {
                RefreshAllClips();
                Debug.Log($"Added {addedCount} clip(s)");
            }
            else
            {
                EditorUtility.DisplayDialog("No Clips", "No clips in selection.", "OK");
            }
        }

        private void RefreshDetectedProperties()
        {
            // Save current selection states
            Dictionary<string, bool> previousSelectionStates = new Dictionary<string, bool>();
            for (int i = 0; i < detectedProperties.Count && i < propertySelectionStates.Count; i++)
            {
                previousSelectionStates[detectedProperties[i]] = propertySelectionStates[i];
            }

            detectedProperties.Clear();
            detectedPropertiesByType.Clear();
            propertySelectionStates.Clear();

            RefreshAllClips();

            HashSet<string> uniqueProperties = new HashSet<string>();
            Dictionary<string, HashSet<string>> propertiesByType = new Dictionary<string, HashSet<string>>();

            foreach (var clip in allClips)
            {
                if (clip == null) continue;

                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);

                foreach (var binding in bindings)
                {
                    string typeName = binding.type.Name;

                    if (!propertiesByType.ContainsKey(typeName))
                    {
                        propertiesByType[typeName] = new HashSet<string>();
                    }

                    propertiesByType[typeName].Add(binding.propertyName);
                    uniqueProperties.Add(binding.propertyName);
                }
            }

            detectedProperties = uniqueProperties.OrderBy(p => p).ToList();

            foreach (var kvp in propertiesByType)
            {
                detectedPropertiesByType[kvp.Key] = kvp.Value.OrderBy(p => p).ToList();
            }

            // Restore previous selection states for properties that still exist
            propertySelectionStates = new List<bool>();
            for (int i = 0; i < detectedProperties.Count; i++)
            {
                string property = detectedProperties[i];
                bool wasSelected = previousSelectionStates.ContainsKey(property) && previousSelectionStates[property];
                propertySelectionStates.Add(wasSelected);
            }
        }

        private void RefreshDetectedPaths()
        {
            detectedPaths.Clear();
            RefreshAllClips();

            HashSet<string> uniquePaths = new HashSet<string>();

            foreach (var clip in allClips)
            {
                if (clip == null) continue;

                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);

                foreach (var binding in bindings)
                {
                    // Add regular hierarchy paths
                    if (!string.IsNullOrEmpty(binding.path))
                    {
                        uniquePaths.Add(binding.path);
                    }
                    // Add Animator parameter names (shown with [Param] prefix for clarity)
                    else if (binding.type == typeof(Animator) && !string.IsNullOrEmpty(binding.propertyName))
                    {
                        uniquePaths.Add($"[Param] {binding.propertyName}");
                    }
                }
            }

            detectedPaths = uniquePaths.OrderBy(p => p).ToList();
        }

        private void CopyParameters()
        {
            if (sourceAnimatorUtils == null || targetAnimatorUtils == null)
            {
                EditorUtility.DisplayDialog("Error", "Both source and target animators must be selected.", "OK");
                return;
            }

            int copiedCount = 0;
            int skippedCount = 0;
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            log.AppendLine("=== Parameter Copy Operation ===\n");

            List<AnimatorControllerParameter> newParameters = new List<AnimatorControllerParameter>(targetAnimatorUtils.parameters);

            foreach (var kvp in parameterSelectionStates)
            {
                if (!kvp.Value) continue;

                string paramName = kvp.Key;
                var sourceParam = sourceAnimatorUtils.parameters.FirstOrDefault(p => p.name == paramName);

                if (sourceParam != null)
                {
                    // Check if parameter already exists in target
                    var existingParam = targetAnimatorUtils.parameters.FirstOrDefault(p => p.name == paramName);

                    if (existingParam != null)
                    {
                        log.AppendLine($"⚠ Skipped '{paramName}' - already exists in target");
                        skippedCount++;
                    }
                    else
                    {
                        // Create new parameter
                        AnimatorControllerParameter newParam = new AnimatorControllerParameter
                        {
                            name = sourceParam.name,
                            type = sourceParam.type,
                            defaultFloat = sourceParam.defaultFloat,
                            defaultInt = sourceParam.defaultInt,
                            defaultBool = sourceParam.defaultBool
                        };

                        newParameters.Add(newParam);
                        log.AppendLine($"✓ Copied '{paramName}' ({sourceParam.type})");
                        copiedCount++;
                    }
                }
            }

            if (copiedCount > 0)
            {
                targetAnimatorUtils.parameters = newParameters.ToArray();
                EditorUtility.SetDirty(targetAnimatorUtils);
                AssetDatabase.SaveAssets();
            }

            log.AppendLine($"\nCopied: {copiedCount} | Skipped: {skippedCount}");
            lastOperationLog = log.ToString();
            lastModifiedCount = copiedCount;

            if (copiedCount > 0)
            {
                EditorUtility.DisplayDialog("Success", $"Copied {copiedCount} parameter(s) to target animator.", "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("No Changes", "No parameters were copied.", "OK");
            }
        }

        private void CopyLayers()
        {
            if (sourceAnimatorUtils == null || targetAnimatorUtils == null)
            {
                EditorUtility.DisplayDialog("Error", "Both source and target animators must be selected.", "OK");
                return;
            }

            int copiedCount = 0;
            int skippedCount = 0;
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            log.AppendLine("=== Layer Copy Operation ===\n");

            List<AnimatorControllerLayer> newLayers = new List<AnimatorControllerLayer>(targetAnimatorUtils.layers);

            foreach (var kvp in layerSelectionStatesUtils)
            {
                if (!kvp.Value) continue;

                string layerName = kvp.Key;
                var sourceLayer = sourceAnimatorUtils.layers.FirstOrDefault(l => l.name == layerName);

                if (sourceLayer != null)
                {
                    // Check if layer already exists in target
                    var existingLayer = targetAnimatorUtils.layers.FirstOrDefault(l => l.name == layerName);

                    if (existingLayer != null)
                    {
                        log.AppendLine($"⚠ Skipped '{layerName}' - already exists in target");
                        skippedCount++;
                    }
                    else
                    {
                        // Deep copy the layer
                        AnimatorControllerLayer newLayer = new AnimatorControllerLayer
                        {
                            name = sourceLayer.name,
                            stateMachine = DuplicateStateMachine(sourceLayer.stateMachine),
                            avatarMask = sourceLayer.avatarMask,
                            blendingMode = sourceLayer.blendingMode,
                            defaultWeight = sourceLayer.defaultWeight,
                            syncedLayerIndex = sourceLayer.syncedLayerIndex,
                            iKPass = sourceLayer.iKPass
                        };

                        newLayers.Add(newLayer);
                        log.AppendLine($"✓ Copied layer '{layerName}'");
                        copiedCount++;
                    }
                }
            }

            if (copiedCount > 0)
            {
                targetAnimatorUtils.layers = newLayers.ToArray();
                EditorUtility.SetDirty(targetAnimatorUtils);
                AssetDatabase.SaveAssets();
            }

            log.AppendLine($"\nCopied: {copiedCount} | Skipped: {skippedCount}");
            lastOperationLog = log.ToString();
            lastModifiedCount = copiedCount;

            if (copiedCount > 0)
            {
                EditorUtility.DisplayDialog("Success", $"Copied {copiedCount} layer(s) to target animator.\n\nNote: Layer references may need to be adjusted manually.", "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("No Changes", "No layers were copied.", "OK");
            }
        }

        private AnimatorStateMachine DuplicateStateMachine(AnimatorStateMachine source)
        {
            if (source == null) return null;

            // Create a new state machine
            AnimatorStateMachine newStateMachine = new AnimatorStateMachine
            {
                name = source.name,
                anyStatePosition = source.anyStatePosition,
                entryPosition = source.entryPosition,
                exitPosition = source.exitPosition,
                parentStateMachinePosition = source.parentStateMachinePosition,
                hideFlags = source.hideFlags
            };

            // Copy states
            foreach (var childState in source.states)
            {
                AnimatorState newState = new AnimatorState
                {
                    name = childState.state.name,
                    motion = childState.state.motion,
                    speed = childState.state.speed,
                    cycleOffset = childState.state.cycleOffset,
                    writeDefaultValues = childState.state.writeDefaultValues,
                    tag = childState.state.tag,
                    iKOnFeet = childState.state.iKOnFeet,
                    mirror = childState.state.mirror,
                    hideFlags = childState.state.hideFlags
                };

                // Copy behaviours
                foreach (var behaviour in childState.state.behaviours)
                {
                    if (behaviour != null)
                    {
                        newState.AddStateMachineBehaviour(behaviour.GetType());
                    }
                }

                newStateMachine.AddState(newState, childState.position);
            }

            // Copy transitions
            foreach (var transition in source.anyStateTransitions)
            {
                var newTransition = newStateMachine.AddAnyStateTransition(
                    newStateMachine.states.FirstOrDefault(s => s.state.name == transition.destinationState?.name).state);
                CopyTransitionProperties(transition, newTransition);
            }

            foreach (var transition in source.entryTransitions)
            {
                var newTransition = newStateMachine.AddEntryTransition(
                    newStateMachine.states.FirstOrDefault(s => s.state.name == transition.destinationState?.name).state);
                CopyTransitionProperties(transition, newTransition);
            }

            // Copy state transitions
            for (int i = 0; i < source.states.Length; i++)
            {
                var sourceState = source.states[i].state;
                var targetState = newStateMachine.states[i].state;

                foreach (var transition in sourceState.transitions)
                {
                    AnimatorState destState = null;
                    if (transition.destinationState != null)
                    {
                        destState = newStateMachine.states.FirstOrDefault(
                            s => s.state.name == transition.destinationState.name).state;
                    }

                    AnimatorStateTransition newTransition;
                    if (transition.isExit)
                    {
                        newTransition = targetState.AddExitTransition();
                    }
                    else if (destState != null)
                    {
                        newTransition = targetState.AddTransition(destState);
                    }
                    else
                    {
                        continue;
                    }

                    CopyTransitionProperties(transition, newTransition);
                }
            }

            // Copy sub-state machines recursively
            foreach (var childMachine in source.stateMachines)
            {
                var duplicatedChild = DuplicateStateMachine(childMachine.stateMachine);
                newStateMachine.AddStateMachine(duplicatedChild, childMachine.position);
            }

            // Set default state
            if (source.defaultState != null)
            {
                var defaultState = newStateMachine.states.FirstOrDefault(
                    s => s.state.name == source.defaultState.name);
                if (defaultState.state != null)
                {
                    newStateMachine.defaultState = defaultState.state;
                }
            }

            return newStateMachine;
        }

        private void CopyTransitionProperties(AnimatorStateTransition source, AnimatorStateTransition target)
        {
            if (source == null || target == null) return;

            target.duration = source.duration;
            target.offset = source.offset;
            target.exitTime = source.exitTime;
            target.hasExitTime = source.hasExitTime;
            target.hasFixedDuration = source.hasFixedDuration;
            target.interruptionSource = source.interruptionSource;
            target.orderedInterruption = source.orderedInterruption;
            target.canTransitionToSelf = source.canTransitionToSelf;

            // Copy conditions
            foreach (var condition in source.conditions)
            {
                target.AddCondition(condition.mode, condition.threshold, condition.parameter);
            }
        }

        private void CopyTransitionProperties(AnimatorTransition source, AnimatorTransition target)
        {
            if (source == null || target == null) return;

            // Copy conditions
            foreach (var condition in source.conditions)
            {
                target.AddCondition(condition.mode, condition.threshold, condition.parameter);
            }
        }

        private string GetDefaultBackupPath()
        {
            // Always write to the user's Assets so backups survive VCC package updates.
            // Matches the convention used by TextureOptimizer.
            return Path.Combine(
                Application.dataPath, "! Shugan", "!_Lab", "Script", "AnimationClipBatchEditor_Backups");
        }

        private void CreateBackups(List<AnimationClip> clipsToBackup)
        {
            try
            {
                if (!Directory.Exists(backupFolderPath))
                {
                    Directory.CreateDirectory(backupFolderPath);
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                foreach (var clip in clipsToBackup)
                {
                    string clipPath = AssetDatabase.GetAssetPath(clip);
                    if (string.IsNullOrEmpty(clipPath))
                        continue;

                    string backupName = $"{clip.name}_backup_{timestamp}.anim";
                    string backupPath = Path.Combine(backupFolderPath, backupName);

                    if (!backupPath.StartsWith("Assets"))
                    {
                        if (backupPath.StartsWith(Application.dataPath))
                        {
                            backupPath = "Assets" + backupPath.Substring(Application.dataPath.Length);
                        }
                    }

                    AssetDatabase.CopyAsset(clipPath, backupPath);

                    if (showDetailedLog)
                    {
                        lastOperationLog += $"[BACKUP] {clip.name}\n";
                    }
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"Created backups for {clipsToBackup.Count} clips");
            }
            catch (Exception e)
            {
                Debug.LogError($"Backup failed: {e.Message}");
            }
        }
        #endregion
    }

    #region Preview Window
    public class PreviewWindow : EditorWindow
    {
        private AnimationClipBatchEditor parentEditor;
        private Vector2 scrollPos;

        public static void ShowWindow(AnimationClipBatchEditor parent)
        {
            PreviewWindow window = GetWindow<PreviewWindow>();
            window.titleContent = new GUIContent("Preview");
            window.parentEditor = parent;
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        private void OnGUI()
        {
            if (parentEditor == null)
            {
                EditorGUILayout.HelpBox("No parent editor found.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField("Changes Preview", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            parentEditor.DrawPreviewContentPublic();
            EditorGUILayout.EndScrollView();
        }
    }
    #endregion
}
