#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Custom Editor for DisplaceComponent - VRCFury Armature Link style UI
/// Now with VRChat SDK build integration
/// </summary>
[CustomEditor(typeof(DisplaceComponent))]
public class DisplaceComponentEditor : Editor
{
    private SerializedProperty operationModeProp;
    private SerializedProperty linkFromProp;
    private SerializedProperty linkToBoneProp;
    private SerializedProperty linkToManualProp;
    private SerializedProperty componentsToMoveProp;
    private SerializedProperty enableAdvancedProp;
    private SerializedProperty offsetPathsProp;
    private SerializedProperty fieldMappingsProp;

    private static GUIStyle headerStyle;
    private static GUIStyle boxStyle;
    private static GUIStyle subHeaderStyle;
    private static GUIStyle successStyle;

    // Humanoid bone names for display
    private static readonly string[] humanBoneNames = System.Enum.GetNames(typeof(HumanBodyBones));

    private void OnEnable()
    {
        operationModeProp = serializedObject.FindProperty("operationMode");
        linkFromProp = serializedObject.FindProperty("linkFrom");
        linkToBoneProp = serializedObject.FindProperty("linkToBone");
        linkToManualProp = serializedObject.FindProperty("linkToManual");
        componentsToMoveProp = serializedObject.FindProperty("componentsToMove");
        enableAdvancedProp = serializedObject.FindProperty("enableAdvancedLinkTargetMode");
        offsetPathsProp = serializedObject.FindProperty("offsetPaths");
        fieldMappingsProp = serializedObject.FindProperty("fieldMappings");
    }

    public override void OnInspectorGUI()
    {
        InitializeStyles();

        serializedObject.Update();

        DisplaceComponent component = (DisplaceComponent)target;

        // Header
        DrawHeader();

        EditorGUILayout.Space(5);

        // Mode Selector
        DrawModeSelector();

        EditorGUILayout.Space(5);

        // Build Process Info
        DrawBuildProcessInfo();

        EditorGUILayout.Space(5);

        DisplaceComponent.OperationMode currentMode = (DisplaceComponent.OperationMode)operationModeProp.enumValueIndex;

        if (currentMode == DisplaceComponent.OperationMode.Displace)
        {
            // DISPLACE MODE UI
            // Main Configuration Box
            DrawMainConfigurationBox(component);

            EditorGUILayout.Space(5);

            // Components List
            DrawComponentsListBox(component);

            EditorGUILayout.Space(5);

            // Advanced Mode Box
            if (enableAdvancedProp.boolValue)
            {
                DrawAdvancedModeBox(component);
            }
        }
        else // FillFields mode
        {
            // FILL FIELDS MODE UI
            DrawFieldMappingsBox(component);
        }

        EditorGUILayout.Space(5);

        // Status Information
        DrawStatusBox(component);

        serializedObject.ApplyModifiedProperties();
    }

    private new void DrawHeader()
    {
        if (headerStyle == null) return;

        EditorGUILayout.BeginVertical(boxStyle);
        GUILayout.Label("Displace Component", headerStyle);
        EditorGUILayout.HelpBox(
            "Two modes available:\n" +
            "• Displace: Moves components to target bone\n" +
            "• FillFields: Fills component fields with bone transforms\n\n" +
            "Both modes work in Play mode and VRChat SDK builds with automatic restoration.",
            MessageType.Info
        );
        EditorGUILayout.EndVertical();
    }

    private void DrawModeSelector()
    {
        EditorGUILayout.BeginVertical(boxStyle);
        GUILayout.Label("Operation Mode", EditorStyles.boldLabel);

        DisplaceComponent.OperationMode currentMode = (DisplaceComponent.OperationMode)operationModeProp.enumValueIndex;

        EditorGUILayout.PropertyField(operationModeProp, new GUIContent("Mode"));

        if (currentMode == DisplaceComponent.OperationMode.Displace)
        {
            EditorGUILayout.HelpBox(
                "Displace Mode: Physically moves components from source to target bone.\n" +
                "Configure which components to move below.",
                MessageType.None
            );
        }
        else
        {
            EditorGUILayout.HelpBox(
                "Fill Fields Mode: Fills component reference fields with bone transforms.\n" +
                "Configure which component fields to fill below.",
                MessageType.None
            );
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawFieldMappingsBox(DisplaceComponent component)
    {
        EditorGUILayout.BeginVertical(boxStyle);

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Field Mappings", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();

        // Test Fill button
        if (GUILayout.Button("Test Fill", GUILayout.Width(70), GUILayout.Height(20)))
        {
            bool success = component.TestFillFields();
            if (success)
            {
                EditorUtility.DisplayDialog("Test Fill Complete",
                    "Field filling test completed successfully!\n\n" +
                    "Check the Console for details.\n" +
                    "Use Ctrl+Z (Undo) to revert the changes.",
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("Test Fill Failed",
                    "Field filling test failed or no fields were filled.\n\n" +
                    "Check the Console for error messages.",
                    "OK");
            }
        }

        // + Button
        if (GUILayout.Button("+", GUILayout.Width(30), GUILayout.Height(20)))
        {
            fieldMappingsProp.arraySize++;
            serializedObject.ApplyModifiedProperties();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(3);

        EditorGUILayout.HelpBox(
            "Select component, field, and value source for each mapping.\n" +
            "Object fields: Use humanoid bone OR search by name (children of bones prioritized).\n" +
            "Text fields: Use direct text OR search path.\n\n" +
            "Click 'Test Fill' to test field filling in edit mode (undoable with Ctrl+Z).",
            MessageType.Info
        );

        EditorGUILayout.Space(3);

        // Draw each field mapping
        for (int i = 0; i < fieldMappingsProp.arraySize; i++)
        {
            DrawFieldMappingElement(i, component);
        }

        if (fieldMappingsProp.arraySize == 0)
        {
            EditorGUILayout.HelpBox("Click + to add field mappings", MessageType.Info);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawFieldMappingElement(int index, DisplaceComponent component)
    {
        SerializedProperty mappingElement = fieldMappingsProp.GetArrayElementAtIndex(index);
        SerializedProperty sourceGameObjectProp = mappingElement.FindPropertyRelative("sourceGameObject");
        SerializedProperty componentProp = mappingElement.FindPropertyRelative("targetComponent");
        SerializedProperty fieldPathProp = mappingElement.FindPropertyRelative("fieldPath");
        SerializedProperty boneProp = mappingElement.FindPropertyRelative("targetBone");
        SerializedProperty searchObjectNameProp = mappingElement.FindPropertyRelative("searchObjectName");
        SerializedProperty textValueProp = mappingElement.FindPropertyRelative("textValue");
        SerializedProperty searchPathProp = mappingElement.FindPropertyRelative("searchPath");
        SerializedProperty skipProp = mappingElement.FindPropertyRelative("skipIfAlreadyFilled");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();

        GUILayout.Label($"{index + 1}.", GUILayout.Width(25));
        EditorGUILayout.LabelField("Field Mapping", EditorStyles.boldLabel, GUILayout.Width(100));

        GUILayout.FlexibleSpace();

        // Remove button
        if (GUILayout.Button("−", GUILayout.Width(25)))
        {
            fieldMappingsProp.DeleteArrayElementAtIndex(index);
            serializedObject.ApplyModifiedProperties();
            return;
        }

        EditorGUILayout.EndHorizontal();

        EditorGUI.indentLevel++;

        // GameObject selector
        EditorGUILayout.PropertyField(sourceGameObjectProp, new GUIContent("Source GameObject"));

        // Component dropdown (if GameObject selected)
        if (sourceGameObjectProp.objectReferenceValue != null)
        {
            GameObject sourceObj = sourceGameObjectProp.objectReferenceValue as GameObject;
            Component[] availableComponents = sourceObj.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform) && !(c is DisplaceComponent))
                .ToArray();

            if (availableComponents.Length > 0)
            {
                string[] componentNames = availableComponents.Select(c => c.GetType().Name).ToArray();

                int currentIndex = -1;
                if (componentProp.objectReferenceValue != null)
                {
                    currentIndex = System.Array.IndexOf(availableComponents, componentProp.objectReferenceValue);
                }

                int newIndex = EditorGUILayout.Popup("Component", currentIndex, componentNames);

                if (newIndex != currentIndex && newIndex >= 0 && newIndex < availableComponents.Length)
                {
                    componentProp.objectReferenceValue = availableComponents[newIndex];
                    serializedObject.ApplyModifiedProperties();
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No components found on this GameObject", MessageType.Warning);
            }
        }
        else
        {
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.Popup("Component", 0, new string[] { "Select GameObject first..." });
            EditorGUI.EndDisabledGroup();
        }

        // Field dropdown (if component selected)
        bool isStringField = false;
        if (componentProp.objectReferenceValue != null)
        {
            Component targetComp = componentProp.objectReferenceValue as Component;
            List<string> fields = DisplaceComponent.GetFillableFields(targetComp);

            if (fields.Count > 0)
            {
                int currentIndex = fields.IndexOf(fieldPathProp.stringValue);
                if (currentIndex < 0) currentIndex = 0;

                string[] fieldArray = fields.ToArray();
                int newIndex = EditorGUILayout.Popup("Field", currentIndex, fieldArray);

                if (newIndex >= 0 && newIndex < fields.Count && fieldArray[newIndex] != fieldPathProp.stringValue)
                {
                    fieldPathProp.stringValue = fieldArray[newIndex];
                    serializedObject.ApplyModifiedProperties();
                }

                // Detect if selected field is a string type
                if (!string.IsNullOrEmpty(fieldPathProp.stringValue))
                {
                    SerializedObject targetSO = new SerializedObject(targetComp);
                    SerializedProperty targetProp = targetSO.FindProperty(fieldPathProp.stringValue);
                    if (targetProp != null)
                    {
                        isStringField = targetProp.propertyType == SerializedPropertyType.String;
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No fillable fields found on this component", MessageType.Warning);
            }
        }
        else
        {
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.Popup("Field", 0, new string[] { "Select component first..." });
            EditorGUI.EndDisabledGroup();
        }

        // Show either text input OR bone selector based on field type
        if (isStringField)
        {
            // Text input for string fields
            EditorGUILayout.PropertyField(textValueProp, new GUIContent("Text Value"));
            EditorGUILayout.PropertyField(searchPathProp, new GUIContent("Search Path"));

            EditorGUILayout.HelpBox(
                "Text/String Field Options:\n" +
                "• Text Value: Direct text to fill (priority 1)\n" +
                "• Search Path: Object name to search for, then use its path (priority 2)\n" +
                "Leave Text Value empty to use Search Path.",
                MessageType.Info
            );
        }
        else
        {
            // Bone selector or search for Transform/GameObject fields
            int currentBoneIndex = boneProp.intValue;
            int newBoneIndex = EditorGUILayout.Popup("Target Bone", currentBoneIndex, humanBoneNames);

            if (newBoneIndex != currentBoneIndex)
            {
                boneProp.intValue = newBoneIndex;
                serializedObject.ApplyModifiedProperties();
            }

            EditorGUILayout.PropertyField(searchObjectNameProp, new GUIContent("Search"));

            EditorGUILayout.HelpBox(
                "Object Reference Field Options:\n" +
                "• Target Bone: Use a humanoid bone transform (priority 2)\n" +
                "• Search: Object name to search for in hierarchy (priority 1)\n\n" +
                "Search Priority: Objects under humanoid bones are searched first, then entire hierarchy.\n" +
                "Leave Search empty to use Target Bone.",
                MessageType.Info
            );
        }

        // Skip if filled checkbox
        EditorGUILayout.PropertyField(skipProp, new GUIContent("Skip if Already Filled"));

        EditorGUI.indentLevel--;

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(2);
    }

    private void DrawBuildProcessInfo()
    {
        EditorGUILayout.BeginVertical(boxStyle);
        
        GUILayout.Label("VRChat SDK Build Integration", EditorStyles.boldLabel);
        
        // Check if restoration is pending
        bool restorationPending = false;
        #if VRC_SDK_VRCSDK3
        restorationPending = DisplaceComponentBuildProcessor.IsProcessingBuild();
        #endif
        
        if (restorationPending)
        {
            EditorGUILayout.HelpBox(
                "⚠ BUILD IN PROGRESS - Restoration Pending\n" +
                "Components are displaced and waiting for automatic restoration.\n" +
                "If build is complete but not restoring, use the Force Restore button below.",
                MessageType.Warning
            );
        }
        else
        {
            EditorGUILayout.HelpBox(
                "✓ Runs BEFORE VRCFury (Callback Order: -2000)\n" +
                "✓ Shows popup during build (configurable)\n" +
                "✓ Removes itself after displacement\n" +
                "✓ Does NOT use IEditorOnly (avoids early removal)\n" +
                "✓ Automatically restores after build",
                MessageType.None
            );
        }

        EditorGUILayout.BeginHorizontal();

        if (restorationPending)
        {
            // Show Force Restore button if build is in progress
            #if VRC_SDK_VRCSDK3
            if (GUILayout.Button("Force Restore", GUILayout.Width(100), GUILayout.Height(25)))
            {
                DisplaceComponentBuildProcessor.ForceRestoreAllStates();
                EditorUtility.DisplayDialog("Success", "Forced restoration completed", "OK");
            }
            #endif
        }
        else
        {
            // Show normal Restore button
            if (GUILayout.Button("Restore", GUILayout.Width(80), GUILayout.Height(25)))
            {
                DisplaceComponent comp = (DisplaceComponent)target;
                comp.RestoreBuildTimeState();
                EditorUtility.DisplayDialog("Success", "Components restored to original state", "OK");
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical();
    }

    private void DrawMainConfigurationBox(DisplaceComponent component)
    {
        EditorGUILayout.BeginVertical(boxStyle);
        
        GUILayout.Label("Link Configuration", EditorStyles.boldLabel);
        
        EditorGUILayout.Space(3);

        // Link From Field (Auto-filled but editable)
        EditorGUILayout.PropertyField(linkFromProp, new GUIContent("Link From (Source Object)"));
        
        if (linkFromProp.objectReferenceValue == null)
        {
            EditorGUILayout.HelpBox("Link From will auto-fill with this GameObject when added. You can change it to any GameObject.", MessageType.Info);
        }

        EditorGUILayout.Space(5);

        // Link To Bone Dropdown (VRCFury style)
        DrawBoneSelector();

        EditorGUILayout.Space(5);

        // Advanced Mode Toggle
        EditorGUILayout.PropertyField(enableAdvancedProp, new GUIContent("Enable Advanced Link Target Mode"));

        EditorGUILayout.EndVertical();
    }

    private void DrawBoneSelector()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        GUILayout.Label("Link To (Target Bone)", EditorStyles.boldLabel);
        
        // Humanoid Bone Dropdown
        int currentBoneIndex = (int)((HumanBodyBones)linkToBoneProp.intValue);
        int newBoneIndex = EditorGUILayout.Popup("Bone", currentBoneIndex, humanBoneNames);
        
        if (newBoneIndex != currentBoneIndex)
        {
            linkToBoneProp.intValue = newBoneIndex;
            serializedObject.ApplyModifiedProperties();
        }
        
        // Show preview of selected bone
        HumanBodyBones selectedBone = (HumanBodyBones)linkToBoneProp.intValue;
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Selected:", EditorStyles.miniLabel, GUILayout.Width(60));
        EditorGUILayout.LabelField(selectedBone.ToString(), EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.Space(3);
        
        // Manual Override
        EditorGUILayout.LabelField("Manual Override (if no Animator)", EditorStyles.miniLabel);
        EditorGUILayout.PropertyField(linkToManualProp, GUIContent.none);
        
        EditorGUILayout.EndVertical();
    }

    private void DrawComponentsListBox(DisplaceComponent component)
    {
        EditorGUILayout.BeginVertical(boxStyle);
        
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Components to Move", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        
        // + Button (VRCFury style)
        if (GUILayout.Button("+", GUILayout.Width(30), GUILayout.Height(20)))
        {
            componentsToMoveProp.arraySize++;
            serializedObject.ApplyModifiedProperties();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(3);

        if (component.linkFrom == null)
        {
            EditorGUILayout.HelpBox("Link From must be set to select components", MessageType.Info);
        }
        else
        {
            // Get available components
            Component[] availableComponents = component.linkFrom.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform) && !(c is DisplaceComponent))
                .ToArray();

            if (availableComponents.Length == 0)
            {
                EditorGUILayout.HelpBox("No moveable components found on Link From GameObject", MessageType.Warning);
            }
            else
            {
                // Draw each component in the list
                for (int i = 0; i < componentsToMoveProp.arraySize; i++)
                {
                    DrawComponentElement(i, availableComponents, component);
                }
            }
        }

        if (componentsToMoveProp.arraySize == 0)
        {
            EditorGUILayout.HelpBox("Click + to add components to move", MessageType.Info);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawComponentElement(int index, Component[] availableComponents, DisplaceComponent component)
    {
        SerializedProperty componentElement = componentsToMoveProp.GetArrayElementAtIndex(index);
        SerializedProperty componentProp = componentElement.FindPropertyRelative("component");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        
        // Component Dropdown
        string[] componentNames = availableComponents.Select(c => c.GetType().Name).ToArray();
        
        int currentIndex = -1;
        if (componentProp.objectReferenceValue != null)
        {
            currentIndex = System.Array.IndexOf(availableComponents, componentProp.objectReferenceValue);
        }
        
        GUILayout.Label($"{index + 1}.", GUILayout.Width(25));
        
        int newIndex = EditorGUILayout.Popup(currentIndex, componentNames);
        
        if (newIndex != currentIndex && newIndex >= 0 && newIndex < availableComponents.Length)
        {
            componentProp.objectReferenceValue = availableComponents[newIndex];
            serializedObject.ApplyModifiedProperties();
        }
        
        // Remove button
        if (GUILayout.Button("−", GUILayout.Width(25)))
        {
            componentsToMoveProp.DeleteArrayElementAtIndex(index);
            serializedObject.ApplyModifiedProperties();
            return;
        }
        
        EditorGUILayout.EndHorizontal();
        
        // Show selected component type
        if (componentProp.objectReferenceValue != null)
        {
            Component comp = componentProp.objectReferenceValue as Component;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(30);
            EditorGUILayout.LabelField($"Type: {comp.GetType().Name}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }
        
        EditorGUILayout.EndVertical();
    }

    private void DrawAdvancedModeBox(DisplaceComponent component)
    {
        EditorGUILayout.BeginVertical(boxStyle);
        
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Advanced Link Target (Offset Paths)", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        
        if (GUILayout.Button("+", GUILayout.Width(30), GUILayout.Height(20)))
        {
            offsetPathsProp.arraySize++;
            serializedObject.ApplyModifiedProperties();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.HelpBox(
            "Specify child paths relative to the selected bone. " +
            "Paths are tried in order (first match wins). " +
            "Leave empty to use the bone itself.",
            MessageType.Info
        );

        EditorGUILayout.Space(3);

        // Draw each offset path
        for (int i = 0; i < offsetPathsProp.arraySize; i++)
        {
            DrawOffsetPathElement(i, component);
        }

        if (offsetPathsProp.arraySize == 0)
        {
            EditorGUILayout.HelpBox("Click + to add offset paths", MessageType.Info);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawOffsetPathElement(int index, DisplaceComponent component)
    {
        SerializedProperty pathElement = offsetPathsProp.GetArrayElementAtIndex(index);
        SerializedProperty pathProp = pathElement.FindPropertyRelative("path");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();

        GUILayout.Label($"Path {index + 1}:", GUILayout.Width(55));
        
        EditorGUILayout.PropertyField(pathProp, GUIContent.none);

        // Browse button
        if (GUILayout.Button("Browse", GUILayout.Width(60)))
        {
            GameObject baseTarget = GetBaseBoneTarget(component);
            if (baseTarget != null)
            {
                BrowseChildPath(baseTarget, pathProp);
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "Cannot Browse",
                    "No base target found. Ensure Link To Bone or Manual Override is set.",
                    "OK"
                );
            }
        }

        // Remove button
        if (GUILayout.Button("−", GUILayout.Width(25)))
        {
            offsetPathsProp.DeleteArrayElementAtIndex(index);
            serializedObject.ApplyModifiedProperties();
            return;
        }

        EditorGUILayout.EndHorizontal();

        // Show target preview if valid
        if (!string.IsNullOrEmpty(pathProp.stringValue))
        {
            GameObject baseTarget = GetBaseBoneTarget(component);
            if (baseTarget != null)
            {
                Transform target = baseTarget.transform.Find(pathProp.stringValue);
                if (target != null)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(60);
                    EditorGUILayout.LabelField($"✓ Found: {target.name}", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
                else
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(60);
                    EditorGUILayout.LabelField("⚠ Path not found", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
            }
        }

        EditorGUILayout.EndVertical();
    }

    private GameObject GetBaseBoneTarget(DisplaceComponent component)
    {
        Animator animator = null;

        if (component.linkFrom != null)
        {
            // Find ALL animators in parent chain and use the topmost one (same logic as GetAvatarRoot)
            Transform current = component.linkFrom.transform;
            while (current != null)
            {
                Animator anim = current.GetComponent<Animator>();
                if (anim != null)
                {
                    animator = anim;  // Keep updating to get topmost
                }
                current = current.parent;
            }
        }

        if (animator == null)
        {
            animator = FindObjectOfType<Animator>();
        }

        if (animator != null && animator.isHuman)
        {
            Transform boneTransform = animator.GetBoneTransform(component.linkToBone);
            if (boneTransform != null)
            {
                return boneTransform.gameObject;
            }
        }

        return component.linkToManual;
    }

    private void DrawStatusBox(DisplaceComponent component)
    {
        EditorGUILayout.BeginVertical(boxStyle);

        GUILayout.Label("Status", EditorStyles.boldLabel);

        DisplaceComponent.OperationMode currentMode = component.operationMode;
        bool isValid = false;

        if (currentMode == DisplaceComponent.OperationMode.Displace)
        {
            // Validation for Displace mode
            bool hasLinkFrom = component.linkFrom != null;
            bool hasComponents = component.componentsToMove != null && component.componentsToMove.Count > 0;
            bool hasValidComponents = hasComponents && component.componentsToMove.Any(c => c.component != null);
            isValid = hasLinkFrom && hasValidComponents;

            if (isValid)
            {
                if (successStyle != null)
                {
                    EditorGUILayout.LabelField("✓ Configuration is valid and ready", successStyle);
                }
                else
                {
                    EditorGUILayout.HelpBox("✓ Configuration is valid and ready", MessageType.None);
                }

                // Show what will be moved
                EditorGUILayout.Space(3);
                EditorGUILayout.LabelField("Will move:", EditorStyles.boldLabel);
                foreach (var compRef in component.componentsToMove)
                {
                    if (compRef.component != null)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(10);
                        EditorGUILayout.LabelField(
                            $"• {compRef.component.GetType().Name} → {component.linkToBone}",
                            EditorStyles.wordWrappedLabel
                        );
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
            else
            {
                string message = "Configuration incomplete:\n";
                if (!hasLinkFrom) message += "• Link From is not set\n";
                if (!hasComponents) message += "• No components added (click + button)\n";
                if (hasComponents && !hasValidComponents) message += "• Select components from dropdowns\n";
                EditorGUILayout.HelpBox(message.TrimEnd(), MessageType.Warning);
            }
        }
        else // FillFields mode
        {
            // Validation for FillFields mode
            bool hasFieldMappings = component.fieldMappings != null && component.fieldMappings.Count > 0;
            bool hasValidMappings = hasFieldMappings && component.fieldMappings.Any(m =>
                m.sourceGameObject != null && m.targetComponent != null && !string.IsNullOrEmpty(m.fieldPath));
            isValid = hasValidMappings;

            if (isValid)
            {
                if (successStyle != null)
                {
                    EditorGUILayout.LabelField("✓ Configuration is valid and ready", successStyle);
                }
                else
                {
                    EditorGUILayout.HelpBox("✓ Configuration is valid and ready", MessageType.None);
                }

                // Show what will be filled
                EditorGUILayout.Space(3);
                EditorGUILayout.LabelField("Will fill:", EditorStyles.boldLabel);
                foreach (var mapping in component.fieldMappings)
                {
                    if (mapping.targetComponent != null && !string.IsNullOrEmpty(mapping.fieldPath))
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(10);

                        // Detect if this is a string field
                        SerializedObject targetSO = new SerializedObject(mapping.targetComponent);
                        SerializedProperty targetProp = targetSO.FindProperty(mapping.fieldPath);
                        bool isStringField = targetProp != null && targetProp.propertyType == SerializedPropertyType.String;

                        string fillValue;
                        if (isStringField)
                        {
                            // For string fields, show what will be used
                            if (!string.IsNullOrEmpty(mapping.textValue))
                            {
                                fillValue = $"\"{mapping.textValue}\"";
                            }
                            else if (!string.IsNullOrEmpty(mapping.searchPath))
                            {
                                fillValue = $"[search: {mapping.searchPath}]";
                            }
                            else
                            {
                                fillValue = "[empty]";
                            }
                        }
                        else
                        {
                            fillValue = mapping.targetBone.ToString();
                        }

                        EditorGUILayout.LabelField(
                            $"• {mapping.targetComponent.GetType().Name}.{mapping.fieldPath} → {fillValue}",
                            EditorStyles.wordWrappedLabel
                        );
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
            else
            {
                string message = "Configuration incomplete:\n";
                if (!hasFieldMappings) message += "• No field mappings added (click + button)\n";
                if (hasFieldMappings && !hasValidMappings) message += "• Configure GameObject, component, and field for each mapping\n";
                EditorGUILayout.HelpBox(message.TrimEnd(), MessageType.Warning);
            }
        }

        EditorGUILayout.Space(5);

        // Mode status
        if (Application.isPlaying)
        {
            string modeText = currentMode == DisplaceComponent.OperationMode.Displace ? "Displacement" : "Field filling";
            EditorGUILayout.HelpBox($"⚡ Play Mode - {modeText} will occur and component will self-destruct", MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "💡 Ready for VRChat SDK Build\n" +
                "Will run BEFORE VRCFury during 'Build & Publish'",
                MessageType.Info
            );
        }

        EditorGUILayout.EndVertical();
    }

    private void BrowseChildPath(GameObject root, SerializedProperty pathProperty)
    {
        if (root == null)
        {
            return;
        }

        GenericMenu menu = new GenericMenu();
        
        // Add root option
        menu.AddItem(new GUIContent("(This Bone)"), false, () => {
            pathProperty.stringValue = "";
            pathProperty.serializedObject.ApplyModifiedProperties();
        });

        menu.AddSeparator("");

        // Recursively add all children
        AddChildrenToMenu(menu, root.transform, "", pathProperty);

        menu.ShowAsContext();
    }

    private void AddChildrenToMenu(GenericMenu menu, Transform parent, string currentPath, SerializedProperty pathProperty)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            string childPath = string.IsNullOrEmpty(currentPath) 
                ? child.name 
                : currentPath + "/" + child.name;

            menu.AddItem(new GUIContent(childPath), false, () => {
                pathProperty.stringValue = childPath;
                pathProperty.serializedObject.ApplyModifiedProperties();
            });

            // Recursively add children (limit depth to avoid performance issues)
            if (child.childCount > 0 && GetPathDepth(childPath) < 10)
            {
                AddChildrenToMenu(menu, child, childPath, pathProperty);
            }
        }
    }

    private int GetPathDepth(string path)
    {
        return string.IsNullOrEmpty(path) ? 0 : path.Split('/').Length;
    }

    private void InitializeStyles()
    {
        if (headerStyle == null)
        {
            headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 5, 5)
            };
        }

        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 10, 10)
            };
        }

        if (subHeaderStyle == null)
        {
            subHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11
            };
        }

        if (successStyle == null)
        {
            successStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(0.0f, 0.6f, 0.0f) },
                fontStyle = FontStyle.Bold
            };
        }
    }
}

/// <summary>
/// Custom property drawer for ComponentReference
/// </summary>
[CustomPropertyDrawer(typeof(DisplaceComponent.ComponentReference))]
public class ComponentReferenceDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);
        
        SerializedProperty componentProp = property.FindPropertyRelative("component");
        SerializedProperty typeNameProp = property.FindPropertyRelative("componentTypeName");
        
        // Display component field
        EditorGUI.PropertyField(position, componentProp, label);
        
        // Update type name when component changes
        if (componentProp.objectReferenceValue != null)
        {
            Component comp = componentProp.objectReferenceValue as Component;
            if (comp != null)
            {
                typeNameProp.stringValue = comp.GetType().Name;
            }
        }
        
        EditorGUI.EndProperty();
    }
}

/// <summary>
/// Custom property drawer for OffsetPath
/// </summary>
[CustomPropertyDrawer(typeof(DisplaceComponent.OffsetPath))]
public class OffsetPathDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);
        
        SerializedProperty pathProp = property.FindPropertyRelative("path");
        EditorGUI.PropertyField(position, pathProp, label);
        
        EditorGUI.EndProperty();
    }
}
#endif
