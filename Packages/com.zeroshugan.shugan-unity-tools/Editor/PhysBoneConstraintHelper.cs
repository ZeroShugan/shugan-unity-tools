using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace ShuganTools
{
    public class PhysBoneConstraintHelper : EditorWindow
    {
        #region Variables
        
        // Selection and analysis
        private List<GameObject> selectedObjects = new List<GameObject>();
        private List<GameObject> objectsWithPhysBone = new List<GameObject>();
        private List<GameObject> objectsWithPhysBoneCollider = new List<GameObject>();
        private List<GameObject> objectsWithContactSender = new List<GameObject>();
        private List<GameObject> objectsWithContactReceiver = new List<GameObject>();
        private List<GameObject> objectsWithConstraints = new List<GameObject>();
        private Dictionary<GameObject, List<GameObject>> matchingObjectsUnderRoot = new Dictionary<GameObject, List<GameObject>>();
        private Dictionary<string, int> manualMatchSelection = new Dictionary<string, int>();
        
        // Root finding
        private Transform commonRoot = null;
        private Transform customSearchRoot = null;
        
        // UI
        private Vector2 scrollPosition;
        private Vector2 matchingObjectsScrollPosition;
        
        // Constraint types
        private readonly string[] constraintTypes = new string[]
        {
            "VRCParentConstraint",
            "VRCAimConstraint",
            "VRCLookAtConstraint",
            "VRCPositionConstraint",
            "VRCRotationConstraint",
            "VRCScaleConstraint"
        };
        
        #endregion
        
        #region Window Setup
        
        [MenuItem("Tools/Shugan/PhysBone & Constraint Helper")]
        static void OpenWindow()
        {
            var window = GetWindow<PhysBoneConstraintHelper>("PhysBone & Constraint Helper");
            window.minSize = new Vector2(400, 600);
        }
        
        void ProcessRootTransformComponent(List<GameObject> objects, string componentTypeName, string displayName,
            ref int success, ref int skippedAlreadyFilled, ref int skippedAmbiguous, ref int skippedNotFound, 
            List<string> warnings)
        {
            Transform searchRoot = customSearchRoot != null ? customSearchRoot : commonRoot;
            
            foreach (var obj in objects)
            {
                var component = obj.GetComponent(componentTypeName);
                if (component == null) continue;
                
                SerializedObject so = new SerializedObject(component);
                SerializedProperty rootTransformProp = so.FindProperty("rootTransform");
                
                if (rootTransformProp == null)
                {
                    warnings.Add($"⚠ {displayName} on {obj.name}: Could not find 'rootTransform' property");
                    continue;
                }
                
                if (rootTransformProp.objectReferenceValue != null)
                {
                    skippedAlreadyFilled++;
                    continue;
                }
                
                if (!matchingObjectsUnderRoot.ContainsKey(obj))
                {
                    warnings.Add($"⚠ {displayName} on {obj.name}: No matching objects found");
                    skippedNotFound++;
                    continue;
                }
                
                List<GameObject> matches = matchingObjectsUnderRoot[obj];
                string key = GetPhysBoneKey(obj);
                
                if (matches.Count == 0)
                {
                    warnings.Add($"⚠ {displayName} on {obj.name}: No matching objects found under root");
                    skippedNotFound++;
                }
                else if (matches.Count > 1)
                {
                    if (manualMatchSelection.ContainsKey(key) && 
                        manualMatchSelection[key] >= 0 && 
                        manualMatchSelection[key] < matches.Count)
                    {
                        Undo.RecordObject(component, $"Auto-fill {displayName} Root Transform");
                        rootTransformProp.objectReferenceValue = matches[manualMatchSelection[key]].transform;
                        so.ApplyModifiedProperties();
                        success++;
                        Debug.Log($"✓ {displayName}: Set root transform for {obj.name} to {matches[manualMatchSelection[key]].name} (manual selection)");
                    }
                    else
                    {
                        warnings.Add($"⚠ {displayName} on {obj.name}: Found {matches.Count} matching objects - please select one manually");
                        skippedAmbiguous++;
                    }
                }
                else
                {
                    Undo.RecordObject(component, $"Auto-fill {displayName} Root Transform");
                    rootTransformProp.objectReferenceValue = matches[0].transform;
                    so.ApplyModifiedProperties();
                    success++;
                    Debug.Log($"✓ {displayName}: Set root transform for {obj.name} to {matches[0].name}");
                }
            }
        }
        
        #endregion
        
        #region Helper Methods for Keys
        
        private string GetPhysBoneKey(GameObject obj)
        {
            return obj.GetInstanceID() + "_physbone";
        }
        
        private string GetConstraintTargetKey(GameObject obj)
        {
            return obj.GetInstanceID() + "_target";
        }
        
        private string GetConstraintSourceKey(GameObject obj)
        {
            return obj.GetInstanceID() + "_source";
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
            
            DrawSelectionAnalysis();
            EditorGUILayout.Space();
            
            DrawRootFinding();
            EditorGUILayout.Space();
            
            DrawMatchingObjects();
            EditorGUILayout.Space();
            
            DrawCreditsSection();
            
            EditorGUILayout.EndScrollView();
        }
        
        void DrawHeader()
        {
            GUILayout.Label("VRC PhysBone & Constraint Helper", EditorStyles.largeLabel);
            
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
            
            GUILayout.Label("PhysBone & Constraint Helper - Created by Shugan", style);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
        
        void DrawInfoSection()
        {
            EditorGUILayout.HelpBox(
                "This tool helps you automatically fill transform fields in VRC components:\n" +
                "• VRC Phys Bone: Root Transform\n" +
                "• VRC Phys Bone Collider: Root Transform\n" +
                "• VRC Contact Sender: Root Transform\n" +
                "• VRC Contact Receiver: Root Transform\n" +
                "• VRC Constraints: Source Transform\n\n" +
                "Select parent objects (children will be included automatically), and this tool will find matching objects under the common root.",
                MessageType.Info);
        }
        
        void DrawSelectionAnalysis()
        {
            EditorGUILayout.LabelField("1. Selected Objects Analysis", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            
            if (GUILayout.Button("🔄 Analyze Selection", GUILayout.Height(30)))
            {
                AnalyzeSelectedObjects();
            }
            
            if (GUILayout.Button("Clear", GUILayout.Width(60), GUILayout.Height(30)))
            {
                ClearAnalysis();
            }
            
            EditorGUILayout.EndHorizontal();
            
            if (selectedObjects.Count == 0)
            {
                EditorGUILayout.HelpBox("No objects selected. Please select objects in the scene hierarchy and click 'Analyze Selection'. Children will be included automatically.", MessageType.Warning);
                return;
            }
            
            // Show summary
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField($"Total Objects: {selectedObjects.Count}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"🦴 With PhysBone: {objectsWithPhysBone.Count}");
            EditorGUILayout.LabelField($"⚪ With PhysBone Collider: {objectsWithPhysBoneCollider.Count}");
            EditorGUILayout.LabelField($"📤 With Contact Sender: {objectsWithContactSender.Count}");
            EditorGUILayout.LabelField($"📥 With Contact Receiver: {objectsWithContactReceiver.Count}");
            EditorGUILayout.LabelField($"🔗 With Constraints: {objectsWithConstraints.Count}");
            EditorGUILayout.EndVertical();
        }
        
        void DrawRootFinding()
        {
            EditorGUILayout.LabelField("2. Search Root Configuration", EditorStyles.boldLabel);
            
            if (selectedObjects.Count == 0)
            {
                EditorGUILayout.HelpBox("Analyze selection first to find the common root.", MessageType.Info);
                return;
            }
            
            if (commonRoot != null)
            {
                var originalColor = GUI.color;
                GUI.color = Color.cyan;
                EditorGUILayout.HelpBox($"✓ Auto-detected Common Root: {commonRoot.name}", MessageType.None);
                GUI.color = originalColor;
            }
            else
            {
                EditorGUILayout.HelpBox("⚠ Could not find a common root for selected objects", MessageType.Warning);
            }
            
            EditorGUILayout.Space(5);
            
            EditorGUILayout.LabelField("Custom Search Root (Optional):", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Override the search root to limit where matches are found. Leave empty to use the auto-detected common root.",
                MessageType.Info);
            
            Transform newCustomRoot = (Transform)EditorGUILayout.ObjectField(
                "Search Root", customSearchRoot, typeof(Transform), true);
            
            if (newCustomRoot != customSearchRoot)
            {
                customSearchRoot = newCustomRoot;
                if (customSearchRoot != null)
                {
                    Debug.Log($"Custom search root set to: {customSearchRoot.name}");
                    RefreshMatchingObjects();
                }
            }
            
            if (customSearchRoot != null)
            {
                EditorGUILayout.BeginHorizontal();
                var btnColor = GUI.backgroundColor;
                GUI.backgroundColor = Color.yellow;
                
                if (GUILayout.Button("🔄 Refresh Matches with Custom Root"))
                {
                    RefreshMatchingObjects();
                }
                
                GUI.backgroundColor = Color.red;
                if (GUILayout.Button("Clear Custom Root", GUILayout.Width(150)))
                {
                    customSearchRoot = null;
                    RefreshMatchingObjects();
                }
                
                GUI.backgroundColor = btnColor;
                EditorGUILayout.EndHorizontal();
            }
        }
        
        void DrawMatchingObjects()
        {
            EditorGUILayout.LabelField("3. Matching Objects", EditorStyles.boldLabel);
            
            if (commonRoot == null || selectedObjects.Count == 0)
            {
                EditorGUILayout.HelpBox("Configure search root first to see matching objects.", MessageType.Info);
                return;
            }
            
            Transform searchRoot = customSearchRoot != null ? customSearchRoot : commonRoot;
            EditorGUILayout.HelpBox($"Searching under: {searchRoot.name}", MessageType.None);
            
            if (matchingObjectsUnderRoot.Count == 0)
            {
                EditorGUILayout.HelpBox("No matching objects found. Try analyzing again or check your search root.", MessageType.Warning);
                return;
            }
            
            matchingObjectsScrollPosition = EditorGUILayout.BeginScrollView(matchingObjectsScrollPosition, GUILayout.Height(400));
            
            // Group by PhysBone
            var physBoneObjects = selectedObjects.Where(obj => objectsWithPhysBone.Contains(obj)).ToList();
            if (physBoneObjects.Count > 0)
            {
                EditorGUILayout.LabelField($"🦴 PhysBone Components ({physBoneObjects.Count}):", EditorStyles.boldLabel);
                EditorGUILayout.Space(3);
                
                foreach (var obj in physBoneObjects)
                {
                    DrawPhysBoneMatchingRow(obj, searchRoot, "PhysBone");
                }
                
                EditorGUILayout.Space(10);
            }
            
            // Group by PhysBone Collider
            var physBoneColliderObjects = selectedObjects.Where(obj => objectsWithPhysBoneCollider.Contains(obj)).ToList();
            if (physBoneColliderObjects.Count > 0)
            {
                EditorGUILayout.LabelField($"⚪ PhysBone Collider Components ({physBoneColliderObjects.Count}):", EditorStyles.boldLabel);
                EditorGUILayout.Space(3);
                
                foreach (var obj in physBoneColliderObjects)
                {
                    DrawPhysBoneMatchingRow(obj, searchRoot, "PhysBone Collider");
                }
                
                EditorGUILayout.Space(10);
            }
            
            // Group by Contact Sender
            var contactSenderObjects = selectedObjects.Where(obj => objectsWithContactSender.Contains(obj)).ToList();
            if (contactSenderObjects.Count > 0)
            {
                EditorGUILayout.LabelField($"📤 Contact Sender Components ({contactSenderObjects.Count}):", EditorStyles.boldLabel);
                EditorGUILayout.Space(3);
                
                foreach (var obj in contactSenderObjects)
                {
                    DrawPhysBoneMatchingRow(obj, searchRoot, "Contact Sender");
                }
                
                EditorGUILayout.Space(10);
            }
            
            // Group by Contact Receiver
            var contactReceiverObjects = selectedObjects.Where(obj => objectsWithContactReceiver.Contains(obj)).ToList();
            if (contactReceiverObjects.Count > 0)
            {
                EditorGUILayout.LabelField($"📥 Contact Receiver Components ({contactReceiverObjects.Count}):", EditorStyles.boldLabel);
                EditorGUILayout.Space(3);
                
                foreach (var obj in contactReceiverObjects)
                {
                    DrawPhysBoneMatchingRow(obj, searchRoot, "Contact Receiver");
                }
                
                EditorGUILayout.Space(10);
            }
            
            // Group by Constraints
            var constraintObjects = selectedObjects.Where(obj => objectsWithConstraints.Contains(obj)).ToList();
            if (constraintObjects.Count > 0)
            {
                EditorGUILayout.LabelField($"🔗 Constraint Components ({constraintObjects.Count}):", EditorStyles.boldLabel);
                EditorGUILayout.Space(3);
                
                foreach (var obj in constraintObjects)
                {
                    DrawConstraintMatchingRow(obj, searchRoot);
                }
            }
            
            EditorGUILayout.EndScrollView();
            
            // Auto-fill button at bottom
            EditorGUILayout.Space(5);
            
            var buttonColor = GUI.backgroundColor;
            GUI.backgroundColor = Color.green;
            
            if (GUILayout.Button("🎯 Auto-Fill Transform Fields", GUILayout.Height(35)))
            {
                AutoFillRootTransforms();
            }
            
            GUI.backgroundColor = buttonColor;
        }
        
        void DrawPhysBoneMatchingRow(GameObject selectedObj, Transform searchRoot, string componentLabel)
        {
            if (selectedObj == null || !matchingObjectsUnderRoot.ContainsKey(selectedObj)) return;
            
            List<GameObject> matches = matchingObjectsUnderRoot[selectedObj];
            string key = GetPhysBoneKey(selectedObj);
            
            EditorGUILayout.BeginVertical("box");
            
            // Determine color based on match count
            var originalBgColor = GUI.backgroundColor;
            var originalContentColor = GUI.contentColor;
            
            if (matches.Count == 1)
            {
                GUI.backgroundColor = new Color(0.5f, 1f, 0.5f); // Light green
            }
            else if (matches.Count == 0)
            {
                GUI.backgroundColor = new Color(1f, 1f, 0.5f); // Light yellow
            }
            else
            {
                GUI.backgroundColor = new Color(1f, 0.7f, 0.5f); // Light orange
            }
            
            EditorGUILayout.BeginHorizontal();
            
            // Show object name
            EditorGUILayout.LabelField($"{selectedObj.name} ({componentLabel} Root Transform)", EditorStyles.boldLabel, GUILayout.Width(300));
            
            GUI.backgroundColor = originalBgColor;
            
            // Show match count indicator
            if (matches.Count == 0)
            {
                GUI.contentColor = Color.yellow;
                EditorGUILayout.LabelField("⚠ No match", GUILayout.Width(80));
            }
            else if (matches.Count == 1)
            {
                GUI.contentColor = Color.green;
                EditorGUILayout.LabelField("✓ 1 match", GUILayout.Width(80));
            }
            else
            {
                GUI.contentColor = Color.red;
                EditorGUILayout.LabelField($"⚠ {matches.Count} matches", GUILayout.Width(100));
            }
            
            GUI.contentColor = originalContentColor;
            
            EditorGUILayout.EndHorizontal();
            
            // Show match field(s)
            if (matches.Count == 0)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField("Match", null, typeof(GameObject), true);
                EditorGUI.EndDisabledGroup();
            }
            else if (matches.Count == 1)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField("Match", matches[0], typeof(GameObject), true);
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                // Multiple matches - show dropdown with full paths
                if (!manualMatchSelection.ContainsKey(key))
                {
                    manualMatchSelection[key] = -1;
                }
                
                string[] matchOptions = new string[matches.Count + 1];
                matchOptions[0] = "--- Select Match ---";
                for (int i = 0; i < matches.Count; i++)
                {
                    matchOptions[i + 1] = GetFullObjectPath(matches[i].transform, searchRoot);
                }
                
                int currentSelection = manualMatchSelection[key] + 1;
                int newSelection = EditorGUILayout.Popup("Manual Selection", currentSelection, matchOptions);
                
                if (newSelection != currentSelection)
                {
                    manualMatchSelection[key] = newSelection - 1;
                }
                
                if (manualMatchSelection[key] >= 0 && manualMatchSelection[key] < matches.Count)
                {
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.ObjectField("→ Selected", matches[manualMatchSelection[key]], typeof(GameObject), true);
                    EditorGUI.EndDisabledGroup();
                }
            }
            
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }
        
        void DrawConstraintMatchingRow(GameObject selectedObj, Transform searchRoot)
        {
            if (selectedObj == null || !matchingObjectsUnderRoot.ContainsKey(selectedObj)) return;
            
            List<GameObject> targetMatches = matchingObjectsUnderRoot[selectedObj];
            
            // Get source matches (based on first child name)
            // The parent object has the constraint, and we search for matches of the child's name
            List<GameObject> sourceMatches = new List<GameObject>();
            string childName = "";
            GameObject firstChild = null;
            if (selectedObj.transform.childCount > 0)
            {
                firstChild = selectedObj.transform.GetChild(0).gameObject;
                childName = firstChild.name;
                // Exclude the child itself from matches
                sourceMatches = FindMatchingObjectsUnderRoot(childName, searchRoot, firstChild);
            }
            
            string targetKey = GetConstraintTargetKey(selectedObj);
            string sourceKey = GetConstraintSourceKey(selectedObj);
            
            EditorGUILayout.BeginVertical("box");
            
            // Header for the constraint object
            EditorGUILayout.LabelField($"{selectedObj.name}", EditorStyles.boldLabel);
            
            EditorGUI.indentLevel++;
            
            // --- TARGET TRANSFORM ROW ---
            EditorGUILayout.BeginVertical("box");
            
            var originalBgColor = GUI.backgroundColor;
            var originalContentColor = GUI.contentColor;
            
            if (targetMatches.Count == 1)
            {
                GUI.backgroundColor = new Color(0.5f, 1f, 0.5f);
            }
            else if (targetMatches.Count == 0)
            {
                GUI.backgroundColor = new Color(1f, 1f, 0.5f);
            }
            else
            {
                GUI.backgroundColor = new Color(1f, 0.7f, 0.5f);
            }
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Target Transform", GUILayout.Width(150));
            
            GUI.backgroundColor = originalBgColor;
            
            if (targetMatches.Count == 0)
            {
                GUI.contentColor = Color.yellow;
                EditorGUILayout.LabelField("⚠ No match", GUILayout.Width(80));
            }
            else if (targetMatches.Count == 1)
            {
                GUI.contentColor = Color.green;
                EditorGUILayout.LabelField("✓ 1 match", GUILayout.Width(80));
            }
            else
            {
                GUI.contentColor = Color.red;
                EditorGUILayout.LabelField($"⚠ {targetMatches.Count} matches", GUILayout.Width(100));
            }
            
            GUI.contentColor = originalContentColor;
            EditorGUILayout.EndHorizontal();
            
            // Show target match field
            if (targetMatches.Count == 0)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField("Match", null, typeof(GameObject), true);
                EditorGUI.EndDisabledGroup();
            }
            else if (targetMatches.Count == 1)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField("Match", targetMatches[0], typeof(GameObject), true);
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                if (!manualMatchSelection.ContainsKey(targetKey))
                {
                    manualMatchSelection[targetKey] = -1;
                }
                
                string[] matchOptions = new string[targetMatches.Count + 1];
                matchOptions[0] = "--- Select Match ---";
                for (int i = 0; i < targetMatches.Count; i++)
                {
                    matchOptions[i + 1] = GetFullObjectPath(targetMatches[i].transform, searchRoot);
                }
                
                int currentSelection = manualMatchSelection[targetKey] + 1;
                int newSelection = EditorGUILayout.Popup("Manual Selection", currentSelection, matchOptions);
                
                if (newSelection != currentSelection)
                {
                    manualMatchSelection[targetKey] = newSelection - 1;
                }
                
                if (manualMatchSelection[targetKey] >= 0 && manualMatchSelection[targetKey] < targetMatches.Count)
                {
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.ObjectField("→ Selected", targetMatches[manualMatchSelection[targetKey]], typeof(GameObject), true);
                    EditorGUI.EndDisabledGroup();
                }
            }
            
            EditorGUILayout.EndVertical();
            
            // --- SOURCE TRANSFORM ROW ---
            EditorGUILayout.BeginVertical("box");
            
            if (sourceMatches.Count == 1)
            {
                GUI.backgroundColor = new Color(0.5f, 1f, 0.5f);
            }
            else if (sourceMatches.Count == 0)
            {
                GUI.backgroundColor = new Color(1f, 1f, 0.5f);
            }
            else
            {
                GUI.backgroundColor = new Color(1f, 0.7f, 0.5f);
            }
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Source Transform (child: {childName})", GUILayout.Width(250));
            
            GUI.backgroundColor = originalBgColor;
            
            if (string.IsNullOrEmpty(childName))
            {
                GUI.contentColor = Color.gray;
                EditorGUILayout.LabelField("No children", GUILayout.Width(80));
            }
            else if (sourceMatches.Count == 0)
            {
                GUI.contentColor = Color.yellow;
                EditorGUILayout.LabelField("⚠ No match", GUILayout.Width(80));
            }
            else if (sourceMatches.Count == 1)
            {
                GUI.contentColor = Color.green;
                EditorGUILayout.LabelField("✓ 1 match", GUILayout.Width(80));
            }
            else
            {
                GUI.contentColor = Color.red;
                EditorGUILayout.LabelField($"⚠ {sourceMatches.Count} matches", GUILayout.Width(100));
            }
            
            GUI.contentColor = originalContentColor;
            EditorGUILayout.EndHorizontal();
            
            // Show source match field
            if (string.IsNullOrEmpty(childName) || sourceMatches.Count == 0)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField("Match", null, typeof(GameObject), true);
                EditorGUI.EndDisabledGroup();
            }
            else if (sourceMatches.Count == 1)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField("Match", sourceMatches[0], typeof(GameObject), true);
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                if (!manualMatchSelection.ContainsKey(sourceKey))
                {
                    manualMatchSelection[sourceKey] = -1;
                }
                
                string[] matchOptions = new string[sourceMatches.Count + 1];
                matchOptions[0] = "--- Select Match ---";
                for (int i = 0; i < sourceMatches.Count; i++)
                {
                    matchOptions[i + 1] = GetFullObjectPath(sourceMatches[i].transform, searchRoot);
                }
                
                int currentSelection = manualMatchSelection[sourceKey] + 1;
                int newSelection = EditorGUILayout.Popup("Manual Selection", currentSelection, matchOptions);
                
                if (newSelection != currentSelection)
                {
                    manualMatchSelection[sourceKey] = newSelection - 1;
                }
                
                if (manualMatchSelection[sourceKey] >= 0 && manualMatchSelection[sourceKey] < sourceMatches.Count)
                {
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.ObjectField("→ Selected", sourceMatches[manualMatchSelection[sourceKey]], typeof(GameObject), true);
                    EditorGUI.EndDisabledGroup();
                }
            }
            
            EditorGUILayout.EndVertical();
            
            EditorGUI.indentLevel--;
            
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }
        
        #endregion
        
        #region Core Functionality
        
        void AnalyzeSelectedObjects()
        {
            selectedObjects.Clear();
            objectsWithPhysBone.Clear();
            objectsWithConstraints.Clear();
            matchingObjectsUnderRoot.Clear();
            manualMatchSelection.Clear();
            commonRoot = null;
            
            // Get all selected objects
            GameObject[] selection = Selection.gameObjects;
            
            if (selection.Length == 0)
            {
                Debug.LogWarning("No objects selected in the scene.");
                return;
            }
            
            // Add all selected objects AND their children
            HashSet<GameObject> allObjectsSet = new HashSet<GameObject>();
            foreach (var obj in selection)
            {
                allObjectsSet.Add(obj);
                
                // Add all children recursively
                Transform[] children = obj.GetComponentsInChildren<Transform>(true);
                foreach (Transform child in children)
                {
                    if (child.gameObject != obj) // Don't add the parent again
                    {
                        allObjectsSet.Add(child.gameObject);
                    }
                }
            }
            
            selectedObjects.AddRange(allObjectsSet);
            
            Debug.Log($"Analyzing {selectedObjects.Count} objects (including children from {selection.Length} selected parent(s))");
            
            // Find objects with VRC Phys Bone component
            foreach (var obj in selectedObjects)
            {
                // Check for PhysBone
                if (obj.GetComponent("VRCPhysBone") != null)
                {
                    objectsWithPhysBone.Add(obj);
                }
                
                // Check for PhysBone Collider
                if (obj.GetComponent("VRCPhysBoneCollider") != null)
                {
                    objectsWithPhysBoneCollider.Add(obj);
                }
                
                // Check for Contact Sender
                if (obj.GetComponent("VRCContactSender") != null)
                {
                    objectsWithContactSender.Add(obj);
                }
                
                // Check for Contact Receiver
                if (obj.GetComponent("VRCContactReceiver") != null)
                {
                    objectsWithContactReceiver.Add(obj);
                }
                
                // Check for any constraint type
                bool hasConstraint = false;
                foreach (string constraintType in constraintTypes)
                {
                    if (obj.GetComponent(constraintType) != null)
                    {
                        hasConstraint = true;
                        break;
                    }
                }
                
                if (hasConstraint && !objectsWithConstraints.Contains(obj))
                {
                    objectsWithConstraints.Add(obj);
                }
            }
            
            Debug.Log($"Found {objectsWithPhysBone.Count} objects with VRC Phys Bone");
            Debug.Log($"Found {objectsWithPhysBoneCollider.Count} objects with VRC Phys Bone Collider");
            Debug.Log($"Found {objectsWithContactSender.Count} objects with VRC Contact Sender");
            Debug.Log($"Found {objectsWithContactReceiver.Count} objects with VRC Contact Receiver");
            Debug.Log($"Found {objectsWithConstraints.Count} objects with VRC Constraints");
            
            // Find common root automatically
            commonRoot = FindCommonRoot(selection.ToList());
            
            if (commonRoot != null)
            {
                Debug.Log($"Common root found: {commonRoot.name}");
                
                // Use custom search root if specified, otherwise use common root
                Transform searchRoot = customSearchRoot != null ? customSearchRoot : commonRoot;
                Debug.Log($"Using search root: {searchRoot.name}");
                
                // Find matching objects under root for each selected object
                foreach (var selectedObj in selectedObjects)
                {
                    List<GameObject> matches = FindMatchingObjectsUnderRoot(selectedObj.name, searchRoot, selectedObj);
                    matchingObjectsUnderRoot[selectedObj] = matches;
                    
                    if (matches.Count > 0)
                    {
                        Debug.Log($"Found {matches.Count} matching object(s) for '{selectedObj.name}' under '{searchRoot.name}'");
                    }
                }
            }
            else
            {
                Debug.LogWarning("Could not find a common root for selected objects.");
            }
            
            Repaint();
        }
        
        void ClearAnalysis()
        {
            selectedObjects.Clear();
            objectsWithPhysBone.Clear();
            objectsWithPhysBoneCollider.Clear();
            objectsWithContactSender.Clear();
            objectsWithContactReceiver.Clear();
            objectsWithConstraints.Clear();
            matchingObjectsUnderRoot.Clear();
            manualMatchSelection.Clear();
            commonRoot = null;
            customSearchRoot = null;
            Repaint();
        }
        
        void RefreshMatchingObjects()
        {
            if (selectedObjects.Count == 0) return;
            
            matchingObjectsUnderRoot.Clear();
            manualMatchSelection.Clear();
            
            Transform searchRoot = customSearchRoot != null ? customSearchRoot : commonRoot;
            
            if (searchRoot == null)
            {
                Debug.LogWarning("No search root available");
                return;
            }
            
            Debug.Log($"Refreshing matches using search root: {searchRoot.name}");
            
            foreach (var selectedObj in selectedObjects)
            {
                List<GameObject> matches = FindMatchingObjectsUnderRoot(selectedObj.name, searchRoot, selectedObj);
                matchingObjectsUnderRoot[selectedObj] = matches;
                
                if (matches.Count > 0)
                {
                    Debug.Log($"Found {matches.Count} matching object(s) for '{selectedObj.name}' under '{searchRoot.name}'");
                }
            }
            
            Repaint();
        }
        
        Transform FindCommonRoot(List<GameObject> objects)
        {
            if (objects.Count == 0) return null;
            if (objects.Count == 1) return objects[0].transform.root;
            
            // Get all ancestors for first object
            List<Transform> ancestors = new List<Transform>();
            Transform current = objects[0].transform;
            while (current != null)
            {
                ancestors.Add(current);
                current = current.parent;
            }
            
            // Find deepest common ancestor
            for (int i = 0; i < ancestors.Count; i++)
            {
                Transform potentialRoot = ancestors[i];
                bool isCommonRoot = true;
                
                foreach (var obj in objects)
                {
                    if (!IsChildOf(obj.transform, potentialRoot))
                    {
                        isCommonRoot = false;
                        break;
                    }
                }
                
                if (isCommonRoot)
                {
                    return potentialRoot;
                }
            }
            
            return null;
        }
        
        bool IsChildOf(Transform child, Transform parent)
        {
            Transform current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = current.parent;
            }
            return false;
        }
        
        List<GameObject> FindMatchingObjectsUnderRoot(string targetName, Transform root, GameObject excludeObject)
        {
            List<GameObject> matches = new List<GameObject>();
            
            Transform[] allChildren = root.GetComponentsInChildren<Transform>(true);
            
            foreach (Transform child in allChildren)
            {
                if (child.gameObject != excludeObject && child.name == targetName)
                {
                    matches.Add(child.gameObject);
                }
            }
            
            return matches;
        }
        
        string GetFullObjectPath(Transform obj, Transform root)
        {
            if (obj == root) return obj.name;
            
            List<string> path = new List<string>();
            Transform current = obj;
            
            while (current != null && current != root)
            {
                path.Insert(0, current.name);
                current = current.parent;
            }
            
            return string.Join("/", path.ToArray());
        }
        
        #endregion
        
        #region Auto-Fill Logic
        
        void AutoFillRootTransforms()
        {
            int totalComponents = objectsWithPhysBone.Count + objectsWithPhysBoneCollider.Count + 
                                objectsWithContactSender.Count + objectsWithContactReceiver.Count + 
                                objectsWithConstraints.Count;
                                
            if (totalComponents == 0 || commonRoot == null)
            {
                EditorUtility.DisplayDialog("Error", "Cannot auto-fill: No objects with VRC components or no common root found.", "OK");
                return;
            }
            
            Transform searchRoot = customSearchRoot != null ? customSearchRoot : commonRoot;
            
            int successPhysBone = 0;
            int skippedAlreadyFilledPhysBone = 0;
            int skippedAmbiguousPhysBone = 0;
            int skippedNotFoundPhysBone = 0;
            
            int successCollider = 0;
            int skippedAlreadyFilledCollider = 0;
            int skippedAmbiguousCollider = 0;
            int skippedNotFoundCollider = 0;
            
            int successSender = 0;
            int skippedAlreadyFilledSender = 0;
            int skippedAmbiguousSender = 0;
            int skippedNotFoundSender = 0;
            
            int successReceiver = 0;
            int skippedAlreadyFilledReceiver = 0;
            int skippedAmbiguousReceiver = 0;
            int skippedNotFoundReceiver = 0;
            
            int successConstraint = 0;
            int skippedAlreadyFilledConstraint = 0;
            int skippedAmbiguousConstraint = 0;
            int skippedNotFoundConstraint = 0;
            
            List<string> warnings = new List<string>();
            
            // Process PhysBones
            ProcessRootTransformComponent(objectsWithPhysBone, "VRCPhysBone", "PhysBone", 
                ref successPhysBone, ref skippedAlreadyFilledPhysBone, 
                ref skippedAmbiguousPhysBone, ref skippedNotFoundPhysBone, warnings);
            
            // Process PhysBone Colliders
            ProcessRootTransformComponent(objectsWithPhysBoneCollider, "VRCPhysBoneCollider", "PhysBone Collider", 
                ref successCollider, ref skippedAlreadyFilledCollider, 
                ref skippedAmbiguousCollider, ref skippedNotFoundCollider, warnings);
            
            // Process Contact Senders
            ProcessRootTransformComponent(objectsWithContactSender, "VRCContactSender", "Contact Sender", 
                ref successSender, ref skippedAlreadyFilledSender, 
                ref skippedAmbiguousSender, ref skippedNotFoundSender, warnings);
            
            // Process Contact Receivers
            ProcessRootTransformComponent(objectsWithContactReceiver, "VRCContactReceiver", "Contact Receiver", 
                ref successReceiver, ref skippedAlreadyFilledReceiver, 
                ref skippedAmbiguousReceiver, ref skippedNotFoundReceiver, warnings);
            
            // Process Constraints
            foreach (var obj in objectsWithConstraints)
            {
                foreach (string constraintType in constraintTypes)
                {
                    var constraint = obj.GetComponent(constraintType);
                    if (constraint == null) continue;
                    
                    SerializedObject so = new SerializedObject(constraint);
                    
                    // Fill TargetTransform
                    SerializedProperty targetTransformProp = so.FindProperty("TargetTransform");
                    
                    if (targetTransformProp == null)
                    {
                        warnings.Add($"⚠ {constraintType} on {obj.name}: Could not find 'TargetTransform' property");
                        continue;
                    }
                    
                    bool targetFilled = false;
                    bool sourceFilled = false;
                    
                    string targetKey = GetConstraintTargetKey(obj);
                    string sourceKey = GetConstraintSourceKey(obj);
                    
                    // Fill target transform
                    if (targetTransformProp.objectReferenceValue == null)
                    {
                        if (matchingObjectsUnderRoot.ContainsKey(obj))
                        {
                            List<GameObject> matches = matchingObjectsUnderRoot[obj];
                            
                            if (matches.Count == 1)
                            {
                                Undo.RecordObject(constraint, "Auto-fill Constraint Target Transform");
                                targetTransformProp.objectReferenceValue = matches[0].transform;
                                targetFilled = true;
                            }
                            else if (matches.Count > 1)
                            {
                                if (manualMatchSelection.ContainsKey(targetKey) && 
                                    manualMatchSelection[targetKey] >= 0 && 
                                    manualMatchSelection[targetKey] < matches.Count)
                                {
                                    Undo.RecordObject(constraint, "Auto-fill Constraint Target Transform");
                                    targetTransformProp.objectReferenceValue = matches[manualMatchSelection[targetKey]].transform;
                                    targetFilled = true;
                                }
                                else
                                {
                                    warnings.Add($"⚠ {constraintType} on {obj.name}: Multiple matches for target - select manually");
                                }
                            }
                            else
                            {
                                warnings.Add($"⚠ {constraintType} on {obj.name}: No target matches found");
                            }
                        }
                    }
                    else
                    {
                        skippedAlreadyFilledConstraint++;
                    }
                    
                    // Fill source transform based on child name
                    // The constraint is on the PARENT object, we need to find matches for the CHILD's name
                    Debug.Log($"\n=== ATTEMPTING TO FILL SOURCE TRANSFORM for '{obj.name}' ===");
                    
                    // VRCParentConstraint structure:
                    // - Sources (Generic container)
                    //   - source0 (Generic)
                    //     - SourceTransform (ObjectReference) ← This is what we want to fill!
                    
                    // First get the Sources container
                    SerializedProperty sourcesProp = so.FindProperty("Sources");
                    
                    if (sourcesProp == null)
                    {
                        Debug.LogWarning($"  ⚠ Could not find 'Sources' property on {constraintType}");
                        continue;
                    }
                    
                    Debug.Log($"  Found 'Sources' property!");
                    
                    // Now get source0 from within Sources
                    SerializedProperty source0Prop = sourcesProp.FindPropertyRelative("source0");
                    
                    if (source0Prop != null)
                    {
                        Debug.Log($"  Found 'source0' property inside Sources!");
                        
                        SerializedProperty sourceTransform = source0Prop.FindPropertyRelative("SourceTransform");
                        
                        if (sourceTransform != null)
                        {
                            Debug.Log($"  Found SourceTransform property inside source0!");
                            Debug.Log($"  Current SourceTransform value: {(sourceTransform.objectReferenceValue != null ? sourceTransform.objectReferenceValue.name : "null")}");
                        }
                        else
                        {
                            Debug.LogWarning($"  ⚠ Could not find SourceTransform inside source0");
                        }
                        
                        if (sourceTransform != null && sourceTransform.objectReferenceValue == null)
                        {
                            // Look at children of this constraint object (the parent)
                            if (obj.transform.childCount > 0)
                            {
                                // Get first child of the parent constraint object
                                GameObject firstChild = obj.transform.GetChild(0).gameObject;
                                string childName = firstChild.name;
                                
                                Debug.Log($"  Parent '{obj.name}' has child: '{childName}'");
                                Debug.Log($"  Searching for matches of child name '{childName}' under root '{searchRoot.name}'...");
                                
                                // Search for matching object with this child's name, EXCLUDING the child itself
                                // This match will be assigned to the PARENT's constraint Sources[0].SourceTransform
                                List<GameObject> childMatches = FindMatchingObjectsUnderRoot(childName, searchRoot, firstChild);
                                
                                Debug.Log($"  Found {childMatches.Count} match(es) for child name '{childName}':");
                                for (int i = 0; i < childMatches.Count; i++)
                                {
                                    Debug.Log($"    Match {i}: {GetFullObjectPath(childMatches[i].transform, searchRoot)}");
                                }
                                
                                if (childMatches.Count == 1)
                                {
                                    Debug.Log($"  ATTEMPTING TO SET: Parent '{obj.name}' source0.SourceTransform = '{childMatches[0].name}'");
                                    Undo.RecordObject(constraint, "Auto-fill Constraint Source Transform");
                                    sourceTransform.objectReferenceValue = childMatches[0].transform;
                                    so.ApplyModifiedProperties();
                                    sourceFilled = true;
                                    Debug.Log($"  ✓ SUCCESS! Set source transform on parent '{obj.name}' to: {childMatches[0].name}");
                                }
                                else if (childMatches.Count > 1)
                                {
                                    Debug.Log($"  Multiple matches found - checking for manual selection...");
                                    if (manualMatchSelection.ContainsKey(sourceKey) && 
                                        manualMatchSelection[sourceKey] >= 0 && 
                                        manualMatchSelection[sourceKey] < childMatches.Count)
                                    {
                                        Debug.Log($"  ATTEMPTING TO SET (manual): Parent '{obj.name}' Sources[0].SourceTransform = '{childMatches[manualMatchSelection[sourceKey]].name}'");
                                        Undo.RecordObject(constraint, "Auto-fill Constraint Source Transform");
                                        sourceTransform.objectReferenceValue = childMatches[manualMatchSelection[sourceKey]].transform;
                                        so.ApplyModifiedProperties();
                                        sourceFilled = true;
                                        Debug.Log($"  ✓ SUCCESS! Set source transform on parent '{obj.name}' to: {childMatches[manualMatchSelection[sourceKey]].name} (manual selection)");
                                    }
                                    else
                                    {
                                        warnings.Add($"⚠ {constraintType} on {obj.name}: Found {childMatches.Count} matches for child name '{childName}' - please select manually");
                                        Debug.LogWarning($"  ⚠ Multiple matches found for child name, no manual selection made");
                                    }
                                }
                                else
                                {
                                    warnings.Add($"⚠ {constraintType} on {obj.name}: No source match found for child name '{childName}'");
                                    Debug.LogWarning($"  ⚠ No matches found for child name '{childName}' (after excluding the child itself)");
                                }
                            }
                            else
                            {
                                warnings.Add($"⚠ {constraintType} on {obj.name}: No children to determine source transform");
                                Debug.LogWarning($"  ⚠ Parent constraint object '{obj.name}' has no children");
                            }
                        }
                        else if (sourceTransform != null && sourceTransform.objectReferenceValue != null)
                        {
                            Debug.Log($"  Source transform on '{obj.name}' already filled with: {sourceTransform.objectReferenceValue.name}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"  ⚠ Could not find 'source0' inside 'Sources' property on {constraintType}");
                    }
                    
                    Debug.Log($"=== END SOURCE TRANSFORM FILL ATTEMPT ===\n");
                    
                    // Apply changes
                    if (targetFilled || sourceFilled)
                    {
                        so.ApplyModifiedProperties();
                        successConstraint++;
                    }
                }
            }
            
            // Build result message
            int totalSuccess = successPhysBone + successCollider + successSender + successReceiver + successConstraint;
            string message = $"Auto-fill complete!\n\n" +
                            $"PhysBone:\n" +
                            $"  ✓ Filled: {successPhysBone}\n" +
                            $"  • Already filled: {skippedAlreadyFilledPhysBone}\n" +
                            $"  • Ambiguous (need manual): {skippedAmbiguousPhysBone}\n" +
                            $"  • Not found: {skippedNotFoundPhysBone}\n\n" +
                            $"PhysBone Collider:\n" +
                            $"  ✓ Filled: {successCollider}\n" +
                            $"  • Already filled: {skippedAlreadyFilledCollider}\n" +
                            $"  • Ambiguous (need manual): {skippedAmbiguousCollider}\n" +
                            $"  • Not found: {skippedNotFoundCollider}\n\n" +
                            $"Contact Sender:\n" +
                            $"  ✓ Filled: {successSender}\n" +
                            $"  • Already filled: {skippedAlreadyFilledSender}\n" +
                            $"  • Ambiguous (need manual): {skippedAmbiguousSender}\n" +
                            $"  • Not found: {skippedNotFoundSender}\n\n" +
                            $"Contact Receiver:\n" +
                            $"  ✓ Filled: {successReceiver}\n" +
                            $"  • Already filled: {skippedAlreadyFilledReceiver}\n" +
                            $"  • Ambiguous (need manual): {skippedAmbiguousReceiver}\n" +
                            $"  • Not found: {skippedNotFoundReceiver}\n\n" +
                            $"Constraints:\n" +
                            $"  ✓ Filled: {successConstraint}\n" +
                            $"  • Already filled: {skippedAlreadyFilledConstraint}\n" +
                            $"  • Ambiguous (need manual): {skippedAmbiguousConstraint}\n" +
                            $"  • Not found: {skippedNotFoundConstraint}";
            
            if (warnings.Count > 0 && warnings.Count <= 15)
            {
                message += "\n\nWarnings:\n" + string.Join("\n", warnings.ToArray());
            }
            else if (warnings.Count > 15)
            {
                message += $"\n\n{warnings.Count} warnings (check Console for details)";
            }
            
            if (totalSuccess > 0)
            {
                EditorUtility.DisplayDialog("Success! 🎉", message, "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("No Changes Made", message, "OK");
            }
            
            foreach (var warning in warnings)
            {
                Debug.LogWarning(warning);
            }
        }
        
        #endregion
    }
}