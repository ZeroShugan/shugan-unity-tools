using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;
#endif

#if UNITY_EDITOR && VRC_SDK_VRCSDK3
using VRC.SDKBase;
#endif

/// <summary>
/// Displace Component - Moves (cuts/pastes) components from one GameObject to another
/// OR fills component fields with humanoid bone transforms
/// Works in both Play mode and during VRChat SDK build process
/// NOTE: Implements IEditorOnly so VRChat SDK ignores it and doesn't show warnings!
/// </summary>
[DefaultExecutionOrder(-100000)]
[ExecuteAlways]
[AddComponentMenu("Displace Component")]
#if UNITY_EDITOR && VRC_SDK_VRCSDK3
public class DisplaceComponent : MonoBehaviour, IEditorOnly
#else
public class DisplaceComponent : MonoBehaviour
#endif
{
    public enum OperationMode
    {
        Displace,    // Move components from one GameObject to another
        FillFields   // Fill component fields with bone transforms
    }

    [Serializable]
    public class ComponentReference
    {
        public Component component;
        public string componentTypeName = "";

        public ComponentReference() { }
        public ComponentReference(Component comp)
        {
            component = comp;
            if (comp != null)
            {
                componentTypeName = comp.GetType().Name;
            }
        }
    }

    [Serializable]
    public class OffsetPath
    {
        public string path = "";

        public OffsetPath() { }
        public OffsetPath(string path)
        {
            this.path = path;
        }
    }

    [Serializable]
    public class FieldMapping
    {
        public GameObject sourceGameObject;  // GameObject containing the component
        public Component targetComponent;
        public string componentTypeName = "";  // For display purposes
        public string fieldPath = "";
        public HumanBodyBones targetBone = HumanBodyBones.Hips;
        public string searchObjectName = "";  // For object reference fields - search by object name, then fill with that object
        public string textValue = "";  // For string/text fields - direct text input
        public string searchPath = "";  // For string/text fields - search by object name, then fill with path
        public bool skipIfAlreadyFilled = true;

        [HideInInspector] public UnityEngine.Object originalValue;
        [HideInInspector] public string originalTextValue = "";  // For text field restoration
        [HideInInspector] public string fieldDisplayName = "";

        public FieldMapping() { }
        public FieldMapping(Component comp, string path, HumanBodyBones bone)
        {
            targetComponent = comp;
            sourceGameObject = comp != null ? comp.gameObject : null;
            componentTypeName = comp != null ? comp.GetType().Name : "";
            fieldPath = path;
            targetBone = bone;
        }
    }

    [Serializable]
    public class FieldFillState
    {
        public Component targetComponent;
        public string fieldPath;
        public UnityEngine.Object originalValue;
        public UnityEngine.Object filledValue;
    }

    [Header("Operation Mode")]
    [Tooltip("Displace: Move components to target bone | FillFields: Fill component fields with bone transforms")]
    public OperationMode operationMode = OperationMode.Displace;

    [Header("Link Configuration")]
    [Tooltip("The GameObject containing the components to move (auto-filled with this GameObject)")]
    public GameObject linkFrom;

    [Tooltip("Target humanoid bone where components will be moved")]
    public HumanBodyBones linkToBone = HumanBodyBones.Hips;

    [Tooltip("Manual override for Link To (used if no Animator found)")]
    public GameObject linkToManual;

    [Space(5)]
    [Tooltip("Components to move from Link From to Link To (Displace mode only)")]
    public List<ComponentReference> componentsToMove = new List<ComponentReference>();

    [Space(10)]
    [Tooltip("Enable to specify multiple offset paths for advanced targeting")]
    public bool enableAdvancedLinkTargetMode = false;

    [Tooltip("Offset paths (relative to selected bone) - tries first path, then second, then third, etc.")]
    public List<OffsetPath> offsetPaths = new List<OffsetPath>();

    [Space(10)]
    [Tooltip("Field mappings for FillFields mode - specify which component fields to fill with bone transforms")]
    public List<FieldMapping> fieldMappings = new List<FieldMapping>();

    // Runtime/Build state tracking
    [Serializable]
    public class DisplacementState
    {
        public Component originalComponent;
        public GameObject originalGameObject;
        public Component newComponent;
        public GameObject targetGameObject;
        public bool wasEnabled;
        public int componentIndex;
        
        // Serialization data for restoration
        public string componentData;
        public string componentTypeName;
    }
    
    [SerializeField] [HideInInspector]
    private List<DisplacementState> displacementStates = new List<DisplacementState>();

    [SerializeField] [HideInInspector]
    private List<FieldFillState> fieldFillStates = new List<FieldFillState>();

    [SerializeField] [HideInInspector]
    private bool isExitingPlayMode = false;

    [SerializeField] [HideInInspector]
    private bool hasPerformedDisplacement = false;

    [SerializeField] [HideInInspector]
    private bool isProcessingBuild = false;

    // Static flag to track if we've processed all components in the scene (including inactive ones)
    private static bool hasProcessedAllInScene = false;

    /// <summary>
    /// Safe wrapper to log to DisplaceComponentLogger if it exists (Editor-only)
    /// Uses reflection to avoid compilation errors since logger is in Editor folder
    /// </summary>
    private static void SafeLog(string message)
    {
        #if UNITY_EDITOR
        try
        {
            var loggerType = System.Type.GetType("DisplaceComponentLogger");
            if (loggerType != null)
            {
                var logMethod = loggerType.GetMethod("Log", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (logMethod != null)
                {
                    logMethod.Invoke(null, new object[] { message });
                }
            }
        }
        catch
        {
            // Silently fail if logger not available
        }
        #endif
    }

    #if UNITY_EDITOR
    private void OnEnable()
    {
        if (linkFrom == null)
        {
            linkFrom = gameObject;
        }

        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

        if (Application.isPlaying && !isProcessingBuild)
        {
            Debug.Log($"========================================");
            Debug.Log($"[DisplaceComponent] 🎮 PLAY MODE - Component Active on: {gameObject.name}");
            Debug.Log($"[DisplaceComponent]    Mode: {operationMode}");
            Debug.Log($"[DisplaceComponent]    Field Mappings: {fieldMappings?.Count ?? 0}");
            Debug.Log($"[DisplaceComponent]    Full Path: {GetFullPath(transform)}");
            Debug.Log($"========================================");

            // If this is the first active component, find and process ALL components (including inactive)
            if (!hasProcessedAllInScene)
            {
                Debug.Log($"[DisplaceComponent] 🔍 First active component - searching for ALL DisplaceComponents (including inactive)...");
                ProcessAllComponentsInScene();
            }
            else if (!hasPerformedDisplacement)
            {
                // Fallback: process just this component if it wasn't processed yet
                Debug.Log($"[DisplaceComponent] Processing individual component...");
                PerformDisplacement();
            }
        }
    }

    private void OnDisable()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        
        if (isExitingPlayMode || (!Application.isPlaying && !isProcessingBuild))
        {
            RestoreOriginalState();
        }
    }

    private void OnPlayModeStateChanged(PlayModeStateChange stateChange)
    {
        switch (stateChange)
        {
            case PlayModeStateChange.EnteredPlayMode:
                // Reset the static flag when entering play mode
                hasProcessedAllInScene = false;

                if (!hasPerformedDisplacement && !isProcessingBuild)
                {
                    PerformDisplacement();
                }
                break;

            case PlayModeStateChange.ExitingPlayMode:
                isExitingPlayMode = true;
                hasProcessedAllInScene = false;  // Reset for next play session
                RestoreOriginalState();
                break;
        }
    }
    #else
    private void Awake()
    {
        if (!hasPerformedDisplacement)
        {
            PerformDisplacement();
        }
    }
    #endif

    #if UNITY_EDITOR
    /// <summary>
    /// Finds and processes ALL DisplaceComponents in the scene, including those on inactive GameObjects
    /// This allows the script to work even when GameObjects are disabled
    /// </summary>
    private void ProcessAllComponentsInScene()
    {
        Debug.Log($"[DisplaceComponent] ========================================");
        Debug.Log($"[DisplaceComponent] 🔍 PROCESSING ALL COMPONENTS IN SCENE");
        Debug.Log($"[DisplaceComponent] ========================================");

        // Find ALL DisplaceComponents, including those on inactive GameObjects
        DisplaceComponent[] allComponents = FindObjectsOfType<DisplaceComponent>(includeInactive: true);

        Debug.Log($"[DisplaceComponent] Found {allComponents.Length} DisplaceComponent(s) total (active + inactive)");

        int processedCount = 0;
        int skippedCount = 0;

        foreach (var component in allComponents)
        {
            if (component == null)
            {
                Debug.LogWarning($"[DisplaceComponent] Component is null, skipping");
                continue;
            }

            // Skip if already processed
            if (component.hasPerformedDisplacement)
            {
                Debug.Log($"[DisplaceComponent] [{processedCount + skippedCount + 1}/{allComponents.Length}] Already processed: {component.gameObject.name} - SKIPPING");
                skippedCount++;
                continue;
            }

            // Skip if processing a build
            if (component.isProcessingBuild)
            {
                Debug.Log($"[DisplaceComponent] [{processedCount + skippedCount + 1}/{allComponents.Length}] Build processing: {component.gameObject.name} - SKIPPING");
                skippedCount++;
                continue;
            }

            string activeState = component.gameObject.activeInHierarchy ? "ACTIVE" : "INACTIVE";
            Debug.Log($"[DisplaceComponent] ----------------------------------------");
            Debug.Log($"[DisplaceComponent] [{processedCount + skippedCount + 1}/{allComponents.Length}] Processing: {component.gameObject.name} ({activeState})");
            Debug.Log($"[DisplaceComponent]   Path: {GetFullPath(component.transform)}");
            Debug.Log($"[DisplaceComponent]   Mode: {component.operationMode}");

            // Process the component directly
            bool success = component.PerformDisplacement();

            if (success)
            {
                processedCount++;
                Debug.Log($"[DisplaceComponent]   ✓ SUCCESS");
            }
            else
            {
                Debug.LogWarning($"[DisplaceComponent]   ✗ FAILED or no work to do");
            }
        }

        Debug.Log($"[DisplaceComponent] ========================================");
        Debug.Log($"[DisplaceComponent] 🎯 SCENE PROCESSING COMPLETE");
        Debug.Log($"[DisplaceComponent]   Total found: {allComponents.Length}");
        Debug.Log($"[DisplaceComponent]   Processed: {processedCount}");
        Debug.Log($"[DisplaceComponent]   Skipped: {skippedCount}");
        Debug.Log($"[DisplaceComponent] ========================================");

        // Mark that we've processed all components in the scene
        hasProcessedAllInScene = true;
    }
    #endif

    public bool PerformBuildTimeDisplacement()
    {
        #if UNITY_EDITOR
        if (hasPerformedDisplacement)
        {
            Debug.LogWarning($"[DisplaceComponent] Already performed displacement on {gameObject.name}");
            return false;
        }

        Debug.Log($"[DisplaceComponent] PerformBuildTimeDisplacement called on: {gameObject.name}");
        isProcessingBuild = true;
        bool success = PerformDisplacement();
        
        if (success)
        {
            Debug.Log($"[DisplaceComponent] Build-time displacement completed on {gameObject.name}");
        }
        else
        {
            Debug.LogWarning($"[DisplaceComponent] Build-time displacement failed on {gameObject.name}");
        }
        
        return success;
        #else
        return false;
        #endif
    }

    public void RestoreBuildTimeState()
    {
        #if UNITY_EDITOR
        Debug.Log($"[DisplaceComponent] RestoreBuildTimeState called on: {gameObject.name}");
        Debug.Log($"[DisplaceComponent]   - isProcessingBuild: {isProcessingBuild}");
        Debug.Log($"[DisplaceComponent]   - displacementStates count: {displacementStates.Count}");

        if (!isProcessingBuild && displacementStates.Count == 0)
        {
            Debug.Log($"[DisplaceComponent] No build-time state to restore on {gameObject.name}");
            return;
        }

        RestoreOriginalState();

        // Clear flags after restoration
        isProcessingBuild = false;
        hasPerformedDisplacement = false;

        Debug.Log($"[DisplaceComponent] Build-time state restored and flags cleared on {gameObject.name}");
        #endif
    }

    #if UNITY_EDITOR
    /// <summary>
    /// Test method for Fill Fields mode - fills fields in edit mode for testing (can be undone)
    /// </summary>
    public bool TestFillFields()
    {
        if (operationMode != OperationMode.FillFields)
        {
            Debug.LogWarning($"[DisplaceComponent] TestFillFields called but mode is not FillFields");
            return false;
        }

        Debug.Log($"[DisplaceComponent] TestFillFields called on: {gameObject.name}");
        Debug.Log($"[DisplaceComponent]   - fieldMappings count: {fieldMappings?.Count ?? 0}");

        if (fieldMappings == null || fieldMappings.Count == 0)
        {
            Debug.LogWarning($"[DisplaceComponent] No field mappings configured");
            return false;
        }

        int successCount = 0;
        int totalCount = 0;

        foreach (var mapping in fieldMappings)
        {
            totalCount++;

            if (mapping.targetComponent == null)
            {
                Debug.LogWarning($"[DisplaceComponent]   [{totalCount}] Field mapping has null component, skipping");
                continue;
            }

            if (string.IsNullOrEmpty(mapping.fieldPath))
            {
                Debug.LogWarning($"[DisplaceComponent]   [{totalCount}] Field mapping has empty field path for {mapping.targetComponent.GetType().Name}, skipping");
                continue;
            }

            try
            {
                SerializedObject so = new SerializedObject(mapping.targetComponent);
                SerializedProperty property = so.FindProperty(mapping.fieldPath);

                if (property == null)
                {
                    Debug.LogWarning($"[DisplaceComponent]   [{totalCount}] ✗ Property not found: {mapping.fieldPath}");
                    continue;
                }

                // Check if this is a string field
                if (property.propertyType == SerializedPropertyType.String)
                {
                    // Determine what value to fill
                    string valueToFill = null;

                    if (!string.IsNullOrEmpty(mapping.textValue))
                    {
                        valueToFill = mapping.textValue;
                        Debug.Log($"[DisplaceComponent]   [{totalCount}] Testing text value: \"{valueToFill}\"");
                    }
                    else if (!string.IsNullOrEmpty(mapping.searchPath))
                    {
                        Debug.Log($"[DisplaceComponent]   [{totalCount}] Testing search path: '{mapping.searchPath}'");

                        valueToFill = FindObjectPathByName(mapping.searchPath);

                        if (valueToFill == null)
                        {
                            Debug.LogWarning($"[DisplaceComponent]   [{totalCount}] ✗ Failed to find object: {mapping.searchPath}");
                            continue;
                        }

                        Debug.Log($"[DisplaceComponent]   [{totalCount}] Found path: \"{valueToFill}\"");
                    }
                    else
                    {
                        Debug.LogWarning($"[DisplaceComponent]   [{totalCount}] ⚠ Both textValue and searchPath are empty, skipping");
                        continue;
                    }

                    // Record undo and set the value
                    UnityEditor.Undo.RecordObject(mapping.targetComponent, "Test Fill Field");
                    property.stringValue = valueToFill;
                    so.ApplyModifiedProperties();
                    UnityEditor.EditorUtility.SetDirty(mapping.targetComponent);

                    successCount++;
                    Debug.Log($"[DisplaceComponent]   [{totalCount}] ✓ Filled {mapping.targetComponent.GetType().Name}.{mapping.fieldPath} = \"{valueToFill}\"");
                }
                else if (property.propertyType == SerializedPropertyType.ObjectReference)
                {
                    Transform targetTransform = null;

                    // Priority 1: Use searchObjectName if provided
                    if (!string.IsNullOrEmpty(mapping.searchObjectName))
                    {
                        Debug.Log($"[DisplaceComponent]   [{totalCount}] Using search by name: '{mapping.searchObjectName}'");
                        targetTransform = FindObjectByName(mapping.searchObjectName);
                        if (targetTransform == null)
                        {
                            Debug.LogWarning($"[DisplaceComponent]   [{totalCount}] ✗ Object not found by name: {mapping.searchObjectName}");
                            continue;
                        }
                    }
                    // Priority 2: Use targetBone as fallback
                    else
                    {
                        Debug.Log($"[DisplaceComponent]   [{totalCount}] Using humanoid bone: {mapping.targetBone}");
                        targetTransform = GetBoneTransform(mapping.targetBone);
                        if (targetTransform == null)
                        {
                            Debug.LogWarning($"[DisplaceComponent]   [{totalCount}] ✗ Bone transform not found: {mapping.targetBone}");
                            continue;
                        }
                    }

                    // Determine the field type and set appropriate value
                    System.Type fieldType = GetFieldType(property);
                    UnityEngine.Object valueToSet = null;

                    if (fieldType == typeof(Transform))
                    {
                        valueToSet = targetTransform;
                    }
                    else if (fieldType == typeof(GameObject))
                    {
                        valueToSet = targetTransform.gameObject;
                    }
                    else
                    {
                        Debug.LogWarning($"[DisplaceComponent]   [{totalCount}] ✗ Unsupported field type: {fieldType}");
                        continue;
                    }

                    // Record undo and set the value
                    UnityEditor.Undo.RecordObject(mapping.targetComponent, "Test Fill Field");
                    property.objectReferenceValue = valueToSet;
                    so.ApplyModifiedProperties();
                    UnityEditor.EditorUtility.SetDirty(mapping.targetComponent);

                    successCount++;
                    Debug.Log($"[DisplaceComponent]   [{totalCount}] ✓ Filled {mapping.targetComponent.GetType().Name}.{mapping.fieldPath} = {valueToSet.name}");
                }
                else
                {
                    Debug.LogWarning($"[DisplaceComponent]   [{totalCount}] ✗ Unsupported property type: {property.propertyType}");
                    continue;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DisplaceComponent]   [{totalCount}] ✗ Error filling field: {e.Message}");
            }
        }

        Debug.Log($"[DisplaceComponent] Test fill complete: {successCount}/{totalCount} succeeded");
        return successCount > 0;
    }
    #endif

    private bool PerformDisplacement()
    {
        Debug.Log($"[DisplaceComponent] PerformDisplacement called on: {gameObject.name}");
        Debug.Log($"[DisplaceComponent]   - Operation Mode: {operationMode}");
        Debug.Log($"[DisplaceComponent]   - hasPerformedDisplacement: {hasPerformedDisplacement}");

        if (hasPerformedDisplacement)
        {
            Debug.Log($"[DisplaceComponent] Already performed operation, skipping");
            return false;
        }

        bool success = false;

        // Delegate to appropriate method based on operation mode
        if (operationMode == OperationMode.FillFields)
        {
            success = PerformFieldFilling();
        }
        else // OperationMode.Displace
        {
            success = PerformComponentDisplacement();
        }

        if (success)
        {
            hasPerformedDisplacement = true;

            #if UNITY_EDITOR
            if (Application.isPlaying && !isProcessingBuild)
            {
                Debug.Log($"[DisplaceComponent] Play mode operation complete, self-destructing");
                SelfDestruct();
            }
            #else
            Destroy(this);
            #endif
        }

        return success;
    }

    private bool PerformComponentDisplacement()
    {
        Debug.Log($"[DisplaceComponent] PerformComponentDisplacement called on: {gameObject.name}");
        Debug.Log($"[DisplaceComponent]   - linkFrom: {(linkFrom != null ? linkFrom.name : "NULL")}");
        Debug.Log($"[DisplaceComponent]   - componentsToMove count: {componentsToMove?.Count ?? 0}");

        if (linkFrom == null)
        {
            Debug.LogWarning($"[DisplaceComponent] Link From is not set on {gameObject.name}");
            return false;
        }

        if (componentsToMove == null || componentsToMove.Count == 0)
        {
            Debug.LogWarning($"[DisplaceComponent] No components selected to move on {gameObject.name}");
            return false;
        }

        GameObject targetObject = GetTargetObject();
        if (targetObject == null)
        {
            Debug.LogWarning($"[DisplaceComponent] Could not find target object for displacement on {gameObject.name}");
            return false;
        }

        Debug.Log($"[DisplaceComponent] Target object found: {targetObject.name}");
        Debug.Log($"[DisplaceComponent] Starting displacement of {componentsToMove.Count} component(s)...");

        int successCount = 0;
        foreach (var componentRef in componentsToMove)
        {
            if (componentRef.component == null)
            {
                Debug.LogWarning($"[DisplaceComponent] Component reference is null, skipping");
                continue;
            }

            Debug.Log($"[DisplaceComponent]   Displacing: {componentRef.component.GetType().Name}");
            if (DisplaceOneComponent(componentRef.component, targetObject))
            {
                successCount++;
                Debug.Log($"[DisplaceComponent]     ✓ Success");
            }
            else
            {
                Debug.LogWarning($"[DisplaceComponent]     ✗ Failed");
            }
        }

        Debug.Log($"[DisplaceComponent] Component displacement complete: {successCount}/{componentsToMove.Count} succeeded");

        if (successCount > 0)
        {
            Debug.Log($"[DisplaceComponent] Successfully displaced {successCount} component(s) from '{linkFrom.name}' to '{targetObject.name}'");
        }

        return successCount > 0;
    }

    private bool DisplaceOneComponent(Component component, GameObject targetObject)
    {
        try
        {
            DisplacementState state = new DisplacementState();
            state.originalComponent = component;
            state.originalGameObject = linkFrom;
            state.targetGameObject = targetObject;
            state.componentTypeName = component.GetType().AssemblyQualifiedName;
            
            Component[] allComponents = linkFrom.GetComponents<Component>();
            state.componentIndex = System.Array.IndexOf(allComponents, component);
            
            if (component is Behaviour behaviour)
            {
                state.wasEnabled = behaviour.enabled;
            }
            
            #if UNITY_EDITOR
            Debug.Log($"[DisplaceComponent]       Copying component...");
            bool copySuccess = ComponentUtility.CopyComponent(component);
            if (!copySuccess)
            {
                Debug.LogError($"[DisplaceComponent]       Failed to copy component: {component.GetType().Name}");
                return false;
            }
            
            Debug.Log($"[DisplaceComponent]       Pasting to target...");
            bool pasteSuccess = ComponentUtility.PasteComponentAsNew(targetObject);
            
            if (pasteSuccess)
            {
                Component[] targetComponents = targetObject.GetComponents(component.GetType());
                state.newComponent = targetComponents[targetComponents.Length - 1];
                
                displacementStates.Add(state);
                
                Debug.Log($"[DisplaceComponent]       Component pasted successfully, storing state");
                
                if (!Application.isPlaying)
                {
                    EditorUtility.SetDirty(targetObject);
                    EditorUtility.SetDirty(linkFrom);
                }
                
                Debug.Log($"[DisplaceComponent]       Destroying original component...");
                if (Application.isPlaying)
                {
                    Destroy(component);
                }
                else
                {
                    DestroyImmediate(component);
                }
                
                Debug.Log($"[DisplaceComponent]       Original component destroyed");
                return true;
            }
            else
            {
                Debug.LogError($"[DisplaceComponent]       Failed to paste component to target");
                return false;
            }
            #else
            Debug.LogWarning($"[DisplaceComponent] Component displacement in builds requires manual implementation for type: {component.GetType().Name}");
            return false;
            #endif
        }
        catch (Exception e)
        {
            Debug.LogError($"[DisplaceComponent] Failed to displace component: {e.Message}\n{e.StackTrace}");
            return false;
        }
    }

    private GameObject GetTargetObject()
    {
        GameObject baseTarget = null;

        Animator animator = null;

        if (linkFrom != null)
        {
            // Find ALL animators in parent chain and use the topmost one (same logic as GetAvatarRoot)
            Transform current = linkFrom.transform;
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
            Transform boneTransform = animator.GetBoneTransform(linkToBone);
            if (boneTransform != null)
            {
                baseTarget = boneTransform.gameObject;
            }
        }

        if (baseTarget == null && linkToManual != null)
        {
            baseTarget = linkToManual;
        }

        if (baseTarget == null)
        {
            return null;
        }

        if (!enableAdvancedLinkTargetMode || offsetPaths == null || offsetPaths.Count == 0)
        {
            return baseTarget;
        }

        foreach (var offsetPath in offsetPaths)
        {
            if (string.IsNullOrEmpty(offsetPath.path))
            {
                continue;
            }

            Transform target = baseTarget.transform.Find(offsetPath.path);
            if (target != null)
            {
                Debug.Log($"[DisplaceComponent] Found target using path: {offsetPath.path}");
                return target.gameObject;
            }
        }

        Debug.LogWarning($"[DisplaceComponent] No offset paths matched, using base bone: {linkToBone}");
        return baseTarget;
    }

    private Transform GetBoneTransform(HumanBodyBones bone)
    {
        Animator animator = null;

        Debug.Log($"[DisplaceComponent]     GetBoneTransform: linkFrom = {(linkFrom != null ? linkFrom.name : "NULL")}");

        if (linkFrom != null)
        {
            // Find ALL animators in parent chain and use the topmost one (same logic as GetAvatarRoot)
            Transform current = linkFrom.transform;
            while (current != null)
            {
                Animator anim = current.GetComponent<Animator>();
                if (anim != null)
                {
                    animator = anim;  // Keep updating to get topmost
                    Debug.Log($"[DisplaceComponent]     GetBoneTransform: Found animator on: {animator.gameObject.name}");
                }
                current = current.parent;
            }
        }

        if (animator == null)
        {
            animator = FindObjectOfType<Animator>();
            Debug.Log($"[DisplaceComponent]     GetBoneTransform: Using FindObjectOfType, found: {(animator != null ? animator.gameObject.name : "NULL")}");
        }

        if (animator != null && animator.isHuman)
        {
            Transform boneTransform = animator.GetBoneTransform(bone);
            Debug.Log($"[DisplaceComponent]     GetBoneTransform: animator.GetBoneTransform({bone}) returned: {(boneTransform != null ? boneTransform.name : "NULL")}");
            return boneTransform;
        }
        else
        {
            Debug.LogWarning($"[DisplaceComponent]     GetBoneTransform: Animator is null or not human! animator={(animator != null ? animator.gameObject.name : "NULL")}, isHuman={(animator != null ? animator.isHuman.ToString() : "N/A")}");
        }

        return null;
    }

    /// <summary>
    /// Sets a field value using reflection (for play mode)
    /// </summary>
    private bool SetFieldValueByReflection(Component component, string fieldPath, UnityEngine.Object value)
    {
        try
        {
            string[] pathParts = fieldPath.Split('.');
            object currentObject = component;
            System.Reflection.FieldInfo finalField = null;

            for (int i = 0; i < pathParts.Length; i++)
            {
                string part = pathParts[i];

                // Skip "Array" - it's Unity's SerializedProperty notation, not a real field
                if (part == "Array")
                {
                    Debug.Log($"[DisplaceComponent]   Skipping 'Array' notation at part {i}");
                    continue;
                }

                // Handle array indexing: data[0], data[1], etc.
                if (part.StartsWith("data[") && part.EndsWith("]"))
                {
                    string indexStr = part.Substring(5, part.Length - 6); // Extract "0" from "data[0]"
                    int index = int.Parse(indexStr);

                    Debug.Log($"[DisplaceComponent]   Accessing array/list element at index {index}");

                    // Current object should be an IList
                    if (currentObject is System.Collections.IList list)
                    {
                        if (index < list.Count)
                        {
                            currentObject = list[index];
                            Debug.Log($"[DisplaceComponent]   Got list element at index {index}, type: {currentObject.GetType().Name}");
                        }
                        else
                        {
                            Debug.LogWarning($"[DisplaceComponent] Index {index} out of bounds (list size: {list.Count})");
                            return false;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[DisplaceComponent] Expected IList but got {currentObject.GetType().Name}");
                        return false;
                    }
                    continue;
                }

                System.Type currentType = currentObject.GetType();

                System.Reflection.FieldInfo field = currentType.GetField(part,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

                if (field == null)
                {
                    Debug.LogWarning($"[DisplaceComponent] Field not found via reflection: {part} in type {currentType.Name}");
                    return false;
                }

                if (i == pathParts.Length - 1)
                {
                    // This is the final field to set
                    finalField = field;
                    field.SetValue(currentObject, value);
                    Debug.Log($"[DisplaceComponent]   Set field '{fieldPath}' to {(value != null ? value.name : "null")} via reflection");
                    return true;
                }
                else
                {
                    // Navigate deeper
                    currentObject = field.GetValue(currentObject);
                    if (currentObject == null)
                    {
                        Debug.LogWarning($"[DisplaceComponent] Null value encountered at '{part}' in path '{fieldPath}'");
                        return false;
                    }
                    Debug.Log($"[DisplaceComponent]   Navigated to field '{part}', type: {currentObject.GetType().Name}");
                }
            }

            return false;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[DisplaceComponent] Reflection error setting field '{fieldPath}': {e.Message}\n{e.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// Sets a string field value using reflection (for play mode)
    /// </summary>
    private bool SetStringFieldValueByReflection(Component component, string fieldPath, string value)
    {
        try
        {
            string[] pathParts = fieldPath.Split('.');
            object currentObject = component;

            for (int i = 0; i < pathParts.Length; i++)
            {
                string part = pathParts[i];

                // Skip "Array" - it's Unity's SerializedProperty notation, not a real field
                if (part == "Array")
                {
                    Debug.Log($"[DisplaceComponent]   Skipping 'Array' notation at part {i}");
                    continue;
                }

                // Handle array indexing: data[0], data[1], etc.
                if (part.StartsWith("data[") && part.EndsWith("]"))
                {
                    string indexStr = part.Substring(5, part.Length - 6); // Extract "0" from "data[0]"
                    int index = int.Parse(indexStr);

                    Debug.Log($"[DisplaceComponent]   Accessing array/list element at index {index}");

                    // Current object should be an IList
                    if (currentObject is System.Collections.IList list)
                    {
                        if (index < list.Count)
                        {
                            currentObject = list[index];
                            Debug.Log($"[DisplaceComponent]   Got list element at index {index}, type: {currentObject.GetType().Name}");
                        }
                        else
                        {
                            Debug.LogWarning($"[DisplaceComponent] Index {index} out of bounds (list size: {list.Count})");
                            return false;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[DisplaceComponent] Expected IList but got {currentObject.GetType().Name}");
                        return false;
                    }
                    continue;
                }

                System.Type currentType = currentObject.GetType();

                System.Reflection.FieldInfo field = currentType.GetField(part,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

                if (field == null)
                {
                    Debug.LogWarning($"[DisplaceComponent] Field not found via reflection: {part} in type {currentType.Name}");
                    return false;
                }

                if (i == pathParts.Length - 1)
                {
                    // This is the final field to set
                    field.SetValue(currentObject, value);
                    Debug.Log($"[DisplaceComponent]   Set string field '{fieldPath}' to \"{value}\" via reflection");
                    return true;
                }
                else
                {
                    // Navigate deeper
                    currentObject = field.GetValue(currentObject);
                    if (currentObject == null)
                    {
                        Debug.LogWarning($"[DisplaceComponent] Null value encountered at '{part}' in path '{fieldPath}'");
                        return false;
                    }
                    Debug.Log($"[DisplaceComponent]   Navigated to field '{part}', type: {currentObject.GetType().Name}");
                }
            }

            return false;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[DisplaceComponent] Reflection error setting string field '{fieldPath}': {e.Message}\n{e.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// Finds an object by name in the hierarchy and returns its relative path from the avatar root
    /// </summary>
    private string FindObjectPathByName(string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
        {
            Debug.LogWarning($"[DisplaceComponent] Search object name is empty");
            return null;
        }

        // Get the avatar root (GameObject with Animator)
        GameObject avatarRoot = GetAvatarRoot();
        if (avatarRoot == null)
        {
            Debug.LogWarning($"[DisplaceComponent] Could not find avatar root (Animator)");
            return null;
        }

        // Build comprehensive debug info
        System.Text.StringBuilder debugInfo = new System.Text.StringBuilder();
        debugInfo.AppendLine($"[DisplaceComponent] ========== SEARCH PATH DEBUG ==========");
        debugInfo.AppendLine($"Searching for object: '{objectName}'");
        debugInfo.AppendLine($"DisplaceComponent location: {gameObject.name}");
        debugInfo.AppendLine($"Link From: {(linkFrom != null ? linkFrom.name : "NULL")}");
        debugInfo.AppendLine($"");

        // Show parent chain
        debugInfo.AppendLine($"Parent Chain from DisplaceComponent:");
        Transform current = gameObject.transform;
        int depth = 0;
        while (current != null && depth < 20)
        {
            string indent = new string(' ', depth * 2);
            Animator anim = current.GetComponent<Animator>();
            string animInfo = anim != null ? " [HAS ANIMATOR]" : "";
            debugInfo.AppendLine($"{indent}↑ {current.name}{animInfo}");
            current = current.parent;
            depth++;
        }
        debugInfo.AppendLine($"");

        // Show avatar root info
        debugInfo.AppendLine($"Avatar Root Found: {avatarRoot.name}");
        debugInfo.AppendLine($"Avatar Root has Animator: {avatarRoot.GetComponent<Animator>() != null}");
        debugInfo.AppendLine($"");

        // Count children under avatar root
        int childCount = CountAllChildren(avatarRoot.transform);
        debugInfo.AppendLine($"Total objects under Avatar Root: {childCount}");
        debugInfo.AppendLine($"");

        Transform foundTransform = null;
        Animator animator = avatarRoot.GetComponent<Animator>();

        // PRIORITY 1: Search within humanoid bone children
        if (animator != null && animator.isHuman)
        {
            debugInfo.AppendLine($"Priority search: Looking within humanoid bone children...");

            // Get all humanoid bones
            List<Transform> boneTransforms = new List<Transform>();
            foreach (HumanBodyBones bone in System.Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;

                Transform boneTransform = animator.GetBoneTransform(bone);
                if (boneTransform != null)
                {
                    boneTransforms.Add(boneTransform);
                }
            }

            debugInfo.AppendLine($"Found {boneTransforms.Count} humanoid bones to search within");

            // Search within each bone's children
            foreach (Transform boneTransform in boneTransforms)
            {
                Transform found = FindChildByName(boneTransform, objectName);
                if (found != null)
                {
                    foundTransform = found;
                    debugInfo.AppendLine($"✓ FOUND (Priority): '{objectName}' under bone '{boneTransform.name}'");
                    break;
                }
            }

            if (foundTransform == null)
            {
                debugInfo.AppendLine($"Not found in humanoid bone children");
            }
        }

        // PRIORITY 2: Search entire hierarchy if not found
        if (foundTransform == null)
        {
            debugInfo.AppendLine($"Fallback search: Looking in entire hierarchy...");
            foundTransform = FindChildByName(avatarRoot.transform, objectName);
        }

        if (foundTransform == null)
        {
            debugInfo.AppendLine($"❌ OBJECT NOT FOUND: '{objectName}'");
            debugInfo.AppendLine($"");
            debugInfo.AppendLine($"Searched hierarchy under: {avatarRoot.name}");
            debugInfo.AppendLine($"Total objects searched: {childCount}");
            debugInfo.AppendLine($"===========================================");
            Debug.LogWarning(debugInfo.ToString());
            return null;
        }

        // Get the relative path (excluding the root object name)
        string relativePath = GetRelativePathFromRoot(foundTransform, avatarRoot.transform);

        // Show found object info
        debugInfo.AppendLine($"✓ FOUND: '{objectName}'");
        debugInfo.AppendLine($"Full Path: {GetFullPath(foundTransform)}");
        debugInfo.AppendLine($"Relative Path (from root): {relativePath}");
        debugInfo.AppendLine($"===========================================");

        Debug.Log(debugInfo.ToString());
        return relativePath;
    }

    /// <summary>
    /// Finds an object by name in the hierarchy and returns the Transform
    /// Priority: First searches within humanoid bone children, then entire hierarchy
    /// </summary>
    private Transform FindObjectByName(string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
        {
            Debug.LogWarning($"[DisplaceComponent] Search object name is empty");
            return null;
        }

        // Get the avatar root (GameObject with Animator)
        GameObject avatarRoot = GetAvatarRoot();
        if (avatarRoot == null)
        {
            Debug.LogWarning($"[DisplaceComponent] Could not find avatar root (Animator)");
            return null;
        }

        Animator animator = avatarRoot.GetComponent<Animator>();

        Debug.Log($"[DisplaceComponent] ========== SEARCH OBJECT DEBUG ==========");
        Debug.Log($"[DisplaceComponent] Searching for object: '{objectName}'");
        Debug.Log($"[DisplaceComponent] Avatar Root: {avatarRoot.name}");

        // PRIORITY 1: Search within humanoid bone children
        if (animator != null && animator.isHuman)
        {
            Debug.Log($"[DisplaceComponent] Priority search: Looking within humanoid bone children...");

            // Get all humanoid bones
            List<Transform> boneTransforms = new List<Transform>();
            foreach (HumanBodyBones bone in System.Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;

                Transform boneTransform = animator.GetBoneTransform(bone);
                if (boneTransform != null)
                {
                    boneTransforms.Add(boneTransform);
                }
            }

            Debug.Log($"[DisplaceComponent] Found {boneTransforms.Count} humanoid bones to search within");

            // Search within each bone's children
            foreach (Transform boneTransform in boneTransforms)
            {
                Transform found = FindChildByName(boneTransform, objectName);
                if (found != null)
                {
                    Debug.Log($"[DisplaceComponent] ✓ FOUND (Priority): '{objectName}' under bone '{boneTransform.name}'");
                    Debug.Log($"[DisplaceComponent] Full Path: {GetFullPath(found)}");
                    Debug.Log($"[DisplaceComponent] ===========================================");
                    return found;
                }
            }

            Debug.Log($"[DisplaceComponent] Not found in humanoid bone children");
        }

        // PRIORITY 2: Search entire hierarchy under avatar root
        Debug.Log($"[DisplaceComponent] Fallback search: Looking in entire hierarchy...");
        Transform result = FindChildByName(avatarRoot.transform, objectName);

        if (result != null)
        {
            Debug.Log($"[DisplaceComponent] ✓ FOUND (Fallback): '{objectName}'");
            Debug.Log($"[DisplaceComponent] Full Path: {GetFullPath(result)}");
            Debug.Log($"[DisplaceComponent] ===========================================");
            return result;
        }

        Debug.Log($"[DisplaceComponent] ❌ NOT FOUND: '{objectName}'");
        Debug.Log($"[DisplaceComponent] ===========================================");
        return null;
    }

    /// <summary>
    /// Counts all children recursively
    /// </summary>
    private int CountAllChildren(Transform parent)
    {
        int count = 1; // Count self
        foreach (Transform child in parent)
        {
            count += CountAllChildren(child);
        }
        return count;
    }

    /// <summary>
    /// Gets the full path of a transform from scene root
    /// </summary>
    private string GetFullPath(Transform transform)
    {
        List<string> pathParts = new List<string>();
        Transform current = transform;

        while (current != null)
        {
            pathParts.Add(current.name);
            current = current.parent;
        }

        pathParts.Reverse();
        return string.Join("/", pathParts);
    }

    /// <summary>
    /// Gets the avatar root GameObject (the one with the topmost Animator component in the hierarchy)
    /// </summary>
    private GameObject GetAvatarRoot()
    {
        Animator animator = null;

        if (linkFrom != null)
        {
            // Find ALL animators in parent chain and use the topmost one
            Transform current = linkFrom.transform;
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

        return animator != null ? animator.gameObject : null;
    }

    /// <summary>
    /// Recursively searches for a child Transform by name (depth-first search)
    /// </summary>
    private Transform FindChildByName(Transform parent, string name)
    {
        // Check the current transform
        if (parent.name == name)
        {
            return parent;
        }

        // Search children
        foreach (Transform child in parent)
        {
            Transform result = FindChildByName(child, name);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the relative path from root to target, excluding the root object name
    /// Example: "MANUKA_Poiyomi/Armature/Hips/Foot" -> "Armature/Hips/Foot"
    /// </summary>
    private string GetRelativePathFromRoot(Transform target, Transform root)
    {
        if (target == root)
        {
            return "";
        }

        List<string> pathParts = new List<string>();
        Transform current = target;

        // Build path from target up to root
        while (current != null && current != root)
        {
            pathParts.Add(current.name);
            current = current.parent;
        }

        // If we didn't reach the root, target is not a child of root
        if (current != root)
        {
            Debug.LogWarning($"[DisplaceComponent] Target {target.name} is not a child of root {root.name}");
            return null;
        }

        // Reverse to get path from root to target (excluding root)
        pathParts.Reverse();
        return string.Join("/", pathParts);
    }

    private bool PerformFieldFilling()
    {
        Debug.Log($"[DisplaceComponent] PerformFieldFilling called on: {gameObject.name}");
        Debug.Log($"[DisplaceComponent]   - fieldMappings count: {fieldMappings?.Count ?? 0}");
        Debug.Log($"[DisplaceComponent]   - Application.isPlaying: {Application.isPlaying}");

        if (fieldMappings == null || fieldMappings.Count == 0)
        {
            Debug.LogWarning($"[DisplaceComponent] No field mappings configured on {gameObject.name}");
            return false;
        }

        #if UNITY_EDITOR
        int successCount = 0;
        fieldFillStates.Clear();

        foreach (var mapping in fieldMappings)
        {
            if (mapping.targetComponent == null)
            {
                Debug.LogWarning($"[DisplaceComponent] Field mapping has null component, skipping");
                continue;
            }

            if (string.IsNullOrEmpty(mapping.fieldPath))
            {
                Debug.LogWarning($"[DisplaceComponent] Field mapping has empty field path for {mapping.targetComponent.GetType().Name}, skipping");
                continue;
            }

            try
            {
                SerializedObject so = new SerializedObject(mapping.targetComponent);
                SerializedProperty property = so.FindProperty(mapping.fieldPath);

                if (property == null)
                {
                    Debug.LogWarning($"[DisplaceComponent]   ✗ Property not found: {mapping.fieldPath}");
                    continue;
                }

                // Check if this is a string field
                if (property.propertyType == SerializedPropertyType.String)
                {
                    // Store original text value
                    mapping.originalTextValue = property.stringValue;

                    // Check if we should skip (already filled)
                    if (mapping.skipIfAlreadyFilled && !string.IsNullOrEmpty(property.stringValue))
                    {
                        Debug.Log($"[DisplaceComponent]   ⊘ Skipped (already filled): \"{property.stringValue}\"");
                        continue;
                    }

                    // Determine what value to fill: textValue or search path result
                    string valueToFill = null;

                    // Priority 1: Use direct text value if not empty
                    if (!string.IsNullOrEmpty(mapping.textValue))
                    {
                        valueToFill = mapping.textValue;
                        Debug.Log($"[DisplaceComponent]   Processing string field (text value): {mapping.targetComponent.GetType().Name}.{mapping.fieldPath} -> \"{valueToFill}\"");
                        SafeLog($"    String field (text): {mapping.targetComponent.GetType().Name}.{mapping.fieldPath} = \"{valueToFill}\"");
                    }
                    // Priority 2: Use search path if text value is empty but search path is not
                    else if (!string.IsNullOrEmpty(mapping.searchPath))
                    {
                        Debug.Log($"[DisplaceComponent]   Processing string field (search path): {mapping.targetComponent.GetType().Name}.{mapping.fieldPath} -> searching for '{mapping.searchPath}'");
                        SafeLog($"    String field (search): {mapping.targetComponent.GetType().Name}.{mapping.fieldPath} searching '{mapping.searchPath}'");

                        valueToFill = FindObjectPathByName(mapping.searchPath);

                        if (valueToFill == null)
                        {
                            Debug.LogWarning($"[DisplaceComponent]   ✗ Failed to find object: {mapping.searchPath}");
                            SafeLog($"      ✗ FAILED to find object: {mapping.searchPath}");
                            continue;
                        }

                        Debug.Log($"[DisplaceComponent]   Found path: \"{valueToFill}\"");
                        SafeLog($"      Found path: \"{valueToFill}\"");
                    }
                    else
                    {
                        Debug.LogWarning($"[DisplaceComponent]   ⚠ Both textValue and searchPath are empty, skipping");
                        SafeLog($"    ⚠ Both textValue and searchPath empty, skipping");
                        continue;
                    }

                    // Set the text value
                    if (Application.isPlaying)
                    {
                        // In play mode, use reflection to set the value directly
                        if (SetStringFieldValueByReflection(mapping.targetComponent, mapping.fieldPath, valueToFill))
                        {
                            successCount++;
                            Debug.Log($"[DisplaceComponent]   ✓ Filled with: \"{valueToFill}\" (play mode)");
                        }
                    }
                    else
                    {
                        // In edit mode, use SerializedProperty
                        property.stringValue = valueToFill;
                        so.ApplyModifiedPropertiesWithoutUndo();  // VRCFury pattern for prefab compatibility

                        // Mark the component dirty to ensure Unity/VRCFury detects the change
                        #if UNITY_EDITOR
                        UnityEditor.EditorUtility.SetDirty(mapping.targetComponent);
                        // Also mark the GameObject dirty
                        UnityEditor.EditorUtility.SetDirty(mapping.targetComponent.gameObject);
                        #endif

                        successCount++;

                        Debug.Log($"[DisplaceComponent]   ✓ Filled with: \"{valueToFill}\"");
                    }
                }
                // Object reference field (Transform/GameObject)
                else if (property.propertyType == SerializedPropertyType.ObjectReference)
                {
                    Debug.Log($"[DisplaceComponent]   Processing object field: {mapping.targetComponent.GetType().Name}.{mapping.fieldPath}");
                    Debug.Log($"[DisplaceComponent]     Component GameObject: {mapping.targetComponent.gameObject.name}");
                    Debug.Log($"[DisplaceComponent]     Component full path: {GetFullPath(mapping.targetComponent.transform)}");
                    Debug.Log($"[DisplaceComponent]     Application.isPlaying: {Application.isPlaying}");

                    SafeLog($"    Object field: {mapping.targetComponent.GetType().Name}.{mapping.fieldPath}");
                    SafeLog($"      Component on: {mapping.targetComponent.gameObject.name}");

                    // Store original value
                    FieldFillState state = new FieldFillState();
                    state.targetComponent = mapping.targetComponent;
                    state.fieldPath = mapping.fieldPath;
                    state.originalValue = property.objectReferenceValue;

                    // Check if we should skip (already filled)
                    UnityEngine.Object currentValue = property.objectReferenceValue;
                    if (mapping.skipIfAlreadyFilled && currentValue != null)
                    {
                        Debug.Log($"[DisplaceComponent]   ⊘ Skipped (already filled): {currentValue.name}");
                        continue;
                    }

                    Transform targetTransform = null;

                    // Priority 1: Use searchObjectName if provided
                    if (!string.IsNullOrEmpty(mapping.searchObjectName))
                    {
                        Debug.Log($"[DisplaceComponent]     Using search by name: '{mapping.searchObjectName}'");
                        SafeLog($"      Searching for object: '{mapping.searchObjectName}'");

                        targetTransform = FindObjectByName(mapping.searchObjectName);
                        if (targetTransform == null)
                        {
                            Debug.LogWarning($"[DisplaceComponent]   ✗ Object not found by name: {mapping.searchObjectName}");
                            SafeLog($"      ✗ Object NOT FOUND: '{mapping.searchObjectName}'");
                            continue;
                        }
                        Debug.Log($"[DisplaceComponent]     Found object: {targetTransform.name} at path: {GetFullPath(targetTransform)}");
                        SafeLog($"      Found: {targetTransform.name}");
                    }
                    // Priority 2: Use targetBone as fallback
                    else
                    {
                        Debug.Log($"[DisplaceComponent]     Using humanoid bone: {mapping.targetBone}");
                        SafeLog($"      Using bone: {mapping.targetBone}");

                        targetTransform = GetBoneTransform(mapping.targetBone);
                        if (targetTransform == null)
                        {
                            Debug.LogWarning($"[DisplaceComponent]   ✗ Bone transform not found: {mapping.targetBone}");
                            SafeLog($"      ✗ Bone NOT FOUND: {mapping.targetBone}");
                            continue;
                        }
                        Debug.Log($"[DisplaceComponent]     Found bone: {targetTransform.name} at path: {GetFullPath(targetTransform)}");
                        SafeLog($"      Found bone: {targetTransform.name}");
                    }

                    // Determine the field type and set appropriate value
                    System.Type fieldType = GetFieldType(property);
                    UnityEngine.Object valueToSet = null;

                    if (fieldType == typeof(Transform))
                    {
                        valueToSet = targetTransform;
                    }
                    else if (fieldType == typeof(GameObject))
                    {
                        valueToSet = targetTransform.gameObject;
                    }
                    else
                    {
                        Debug.LogWarning($"[DisplaceComponent]   ✗ Unsupported field type: {fieldType}");
                        continue;
                    }

                    if (Application.isPlaying)
                    {
                        // In play mode, use reflection to set the value directly
                        Debug.Log($"[DisplaceComponent]     Attempting reflection set:");
                        Debug.Log($"[DisplaceComponent]       Component: {mapping.targetComponent.GetType().Name}");
                        Debug.Log($"[DisplaceComponent]       Field path: {mapping.fieldPath}");
                        Debug.Log($"[DisplaceComponent]       Value: {valueToSet.name} (Type: {valueToSet.GetType().Name})");

                        if (SetFieldValueByReflection(mapping.targetComponent, mapping.fieldPath, valueToSet))
                        {
                            state.filledValue = valueToSet;
                            fieldFillStates.Add(state);
                            successCount++;
                            Debug.Log($"[DisplaceComponent]   ✓ Filled with: {valueToSet.name} (play mode) - SUCCESS!");

                            // Verify the value was actually set by reading it back
                            SerializedObject verifyObj = new SerializedObject(mapping.targetComponent);
                            SerializedProperty verifyProp = verifyObj.FindProperty(mapping.fieldPath);
                            if (verifyProp != null && verifyProp.objectReferenceValue != null)
                            {
                                Debug.Log($"[DisplaceComponent]   ✓ VERIFIED: Field now contains: {verifyProp.objectReferenceValue.name}");
                            }
                            else
                            {
                                Debug.LogWarning($"[DisplaceComponent]   ⚠ WARNING: Field appears to be null after setting!");
                            }
                        }
                        else
                        {
                            Debug.LogError($"[DisplaceComponent]   ✗ FAILED to set field via reflection!");
                        }
                    }
                    else
                    {
                        // In edit mode, use SerializedProperty
                        property.objectReferenceValue = valueToSet;
                        state.filledValue = valueToSet;
                        so.ApplyModifiedPropertiesWithoutUndo();  // VRCFury pattern for prefab compatibility

                        // Mark the component dirty to ensure Unity/VRCFury detects the change
                        #if UNITY_EDITOR
                        UnityEditor.EditorUtility.SetDirty(mapping.targetComponent);
                        UnityEditor.EditorUtility.SetDirty(mapping.targetComponent.gameObject);
                        #endif

                        fieldFillStates.Add(state);
                        successCount++;

                        Debug.Log($"[DisplaceComponent]   ✓ Filled with: {targetTransform.name}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[DisplaceComponent]   ✗ Unsupported property type: {property.propertyType}");
                    continue;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DisplaceComponent]   ✗ Error filling field: {e.Message}");
            }
        }

        Debug.Log($"[DisplaceComponent] Field filling complete: {successCount}/{fieldMappings.Count} succeeded");
        return successCount > 0;
        #else
        return false;
        #endif
    }

    private void RestoreFieldValues()
    {
        Debug.Log($"[DisplaceComponent] RestoreFieldValues called on: {gameObject.name}");
        Debug.Log($"[DisplaceComponent]   - fieldFillStates count: {fieldFillStates.Count}");
        Debug.Log($"[DisplaceComponent]   - fieldMappings count: {fieldMappings?.Count ?? 0}");

        #if UNITY_EDITOR
        int restoredCount = 0;

        // Restore string fields from fieldMappings (they store originalTextValue)
        if (fieldMappings != null)
        {
            foreach (var mapping in fieldMappings)
            {
                if (mapping.targetComponent == null || string.IsNullOrEmpty(mapping.fieldPath))
                    continue;

                try
                {
                    SerializedObject so = new SerializedObject(mapping.targetComponent);
                    SerializedProperty property = so.FindProperty(mapping.fieldPath);

                    if (property == null)
                        continue;

                    // Only restore string fields (object references are handled by fieldFillStates)
                    if (property.propertyType == SerializedPropertyType.String)
                    {
                        Debug.Log($"[DisplaceComponent]   Restoring text field: {mapping.targetComponent.GetType().Name}.{mapping.fieldPath}");

                        property.stringValue = mapping.originalTextValue;
                        so.ApplyModifiedProperties();
                        restoredCount++;

                        Debug.Log($"[DisplaceComponent]   ✓ Restored to: \"{mapping.originalTextValue}\"");

                        // Clear the stored original value
                        mapping.originalTextValue = "";
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[DisplaceComponent]   ✗ Error restoring text field: {e.Message}");
                }
            }
        }

        // Restore object reference fields from fieldFillStates
        foreach (var state in fieldFillStates)
        {
            if (state.targetComponent == null)
            {
                Debug.LogWarning($"[DisplaceComponent]   Component is null, skipping");
                continue;
            }

            try
            {
                Debug.Log($"[DisplaceComponent]   Restoring object field: {state.targetComponent.GetType().Name}.{state.fieldPath}");

                SerializedObject so = new SerializedObject(state.targetComponent);
                SerializedProperty property = so.FindProperty(state.fieldPath);

                if (property == null)
                {
                    Debug.LogWarning($"[DisplaceComponent]   ✗ Property not found: {state.fieldPath}");
                    continue;
                }

                property.objectReferenceValue = state.originalValue;
                so.ApplyModifiedProperties();
                restoredCount++;

                string originalName = state.originalValue != null ? state.originalValue.name : "null";
                Debug.Log($"[DisplaceComponent]   ✓ Restored to: {originalName}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DisplaceComponent]   ✗ Error restoring object field: {e.Message}");
            }
        }

        Debug.Log($"[DisplaceComponent] Restored {restoredCount} field(s) total");
        fieldFillStates.Clear();
        #endif
    }

    private void RestoreOriginalState()
    {
        Debug.Log($"[DisplaceComponent] RestoreOriginalState called on: {gameObject.name}");
        Debug.Log($"[DisplaceComponent]   - Operation Mode: {operationMode}");
        Debug.Log($"[DisplaceComponent]   - displacementStates count: {displacementStates.Count}");
        Debug.Log($"[DisplaceComponent]   - fieldFillStates count: {fieldFillStates.Count}");

        // Handle based on operation mode
        if (operationMode == OperationMode.FillFields)
        {
            RestoreFieldValues();
            return;
        }

        // Displace mode restoration
        if (displacementStates.Count == 0)
        {
            Debug.Log($"[DisplaceComponent] No displacement states to restore");
            return;
        }

        #if UNITY_EDITOR
        int restoredCount = 0;
        foreach (var state in displacementStates)
        {
            try
            {
                Debug.Log($"[DisplaceComponent]   Restoring component: {state.componentTypeName}");
                
                if (state.newComponent != null && state.originalGameObject != null)
                {
                    Debug.Log($"[DisplaceComponent]     Copying from target back to original...");
                    bool copySuccess = ComponentUtility.CopyComponent(state.newComponent);
                    if (!copySuccess)
                    {
                        Debug.LogError($"[DisplaceComponent]     Failed to copy component during restoration");
                        continue;
                    }
                    
                    bool pasteSuccess = ComponentUtility.PasteComponentAsNew(state.originalGameObject);
                    
                    if (pasteSuccess)
                    {
                        Component[] components = state.originalGameObject.GetComponents(state.newComponent.GetType());
                        Component restoredComponent = components[components.Length - 1];
                        
                        Debug.Log($"[DisplaceComponent]     Component pasted back, restoring order...");
                        
                        if (state.componentIndex >= 0)
                        {
                            int currentIndex = components.Length - 1;
                            int movesNeeded = currentIndex - state.componentIndex;
                            for (int i = 0; i < movesNeeded; i++)
                            {
                                ComponentUtility.MoveComponentUp(restoredComponent);
                            }
                            Debug.Log($"[DisplaceComponent]     Moved up {movesNeeded} positions");
                        }
                        
                        if (restoredComponent is Behaviour behaviour)
                        {
                            behaviour.enabled = state.wasEnabled;
                        }
                        
                        if (!Application.isPlaying)
                        {
                            EditorUtility.SetDirty(state.originalGameObject);
                            EditorUtility.SetDirty(state.targetGameObject);
                        }
                        
                        Debug.Log($"[DisplaceComponent]     Destroying component from target...");
                        if (Application.isPlaying)
                        {
                            Destroy(state.newComponent);
                        }
                        else
                        {
                            DestroyImmediate(state.newComponent);
                        }
                        
                        restoredCount++;
                        Debug.Log($"[DisplaceComponent]     ✓ Component fully restored");
                    }
                    else
                    {
                        Debug.LogError($"[DisplaceComponent]     Failed to paste component back to original");
                    }
                }
                else
                {
                    Debug.LogWarning($"[DisplaceComponent]     Cannot restore - newComponent or originalGameObject is null");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[DisplaceComponent]   Failed to restore component: {e.Message}");
            }
        }
        
        Debug.Log($"[DisplaceComponent] Restored {restoredCount}/{displacementStates.Count} component(s) to original locations");
        
        displacementStates.Clear();
        isExitingPlayMode = false;
        hasPerformedDisplacement = false;
        
        Debug.Log($"[DisplaceComponent] State cleared on: {gameObject.name}");
        #endif
    }

    private void SelfDestruct()
    {
        #if UNITY_EDITOR
        Debug.Log($"[DisplaceComponent] Self-destructing on: {gameObject.name}");
        if (!Application.isPlaying)
        {
            DestroyImmediate(this);
        }
        else
        {
            Destroy(this);
        }
        #else
        Destroy(this);
        #endif
    }

    private void OnDestroy()
    {
        if (Application.isPlaying && !isExitingPlayMode)
        {
            // Component is being destroyed during play mode (not exiting)
        }
        else if (!isProcessingBuild)
        {
            RestoreOriginalState();
        }
    }

    #if UNITY_EDITOR
    /// <summary>
    /// Discovers all fillable fields in a component: Transform, GameObject, and string/text fields (including nested fields)
    /// </summary>
    public static List<string> GetFillableFields(Component component)
    {
        List<string> fieldPaths = new List<string>();

        if (component == null) return fieldPaths;

        SerializedObject so = new SerializedObject(component);
        SerializedProperty property = so.GetIterator();

        // Traverse all properties via SerializedProperty
        bool enterChildren = true;
        while (property.Next(enterChildren))
        {
            enterChildren = true;

            // Skip script reference
            if (property.name == "m_Script")
                continue;

            // Check if this is an object reference (Transform/GameObject)
            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                System.Type fieldType = GetFieldType(property);

                if (fieldType != null && (fieldType == typeof(Transform) || fieldType == typeof(GameObject)))
                {
                    fieldPaths.Add(property.propertyPath);
                }
            }
            // Check if this is a string field
            else if (property.propertyType == SerializedPropertyType.String)
            {
                fieldPaths.Add(property.propertyPath);
            }
            // Check if this is an array/list - inspect element type for nested fields
            else if (property.isArray && property.propertyType == SerializedPropertyType.Generic)
            {
                // Try to inspect the first element if it exists
                if (property.arraySize > 0)
                {
                    SerializedProperty firstElement = property.GetArrayElementAtIndex(0);
                    InspectNestedFields(firstElement, property.propertyPath + ".Array.data[0]", fieldPaths);
                }
                else
                {
                    // Array is empty - use reflection to inspect the element type
                    System.Type elementType = GetArrayElementType(property);
                    if (elementType != null)
                    {
                        InspectTypeForFillableFields(elementType, property.propertyPath + ".Array.data[0]", fieldPaths);
                    }
                }
            }
        }

        return fieldPaths;
    }

    /// <summary>
    /// Recursively inspects nested fields in a SerializedProperty
    /// </summary>
    private static void InspectNestedFields(SerializedProperty property, string basePath, List<string> fieldPaths)
    {
        SerializedProperty iterator = property.Copy();
        SerializedProperty endProperty = iterator.GetEndProperty();

        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, endProperty))
        {
            enterChildren = true;

            if (iterator.propertyType == SerializedPropertyType.ObjectReference)
            {
                System.Type fieldType = GetFieldType(iterator);
                if (fieldType != null && (fieldType == typeof(Transform) || fieldType == typeof(GameObject)))
                {
                    // Build the full path (replace data[0] with data[{index}] to make it generic)
                    string fullPath = iterator.propertyPath;
                    fieldPaths.Add(fullPath);
                }
            }
            else if (iterator.propertyType == SerializedPropertyType.String)
            {
                string fullPath = iterator.propertyPath;
                fieldPaths.Add(fullPath);
            }
        }
    }

    /// <summary>
    /// Uses reflection to inspect a type and find fillable fields (for empty arrays)
    /// </summary>
    private static void InspectTypeForFillableFields(System.Type type, string basePath, List<string> fieldPaths)
    {
        if (type == null) return;

        var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        foreach (var field in fields)
        {
            // Check for string fields
            if (field.FieldType == typeof(string))
            {
                string path = basePath + "." + field.Name;
                // Only add if not already present
                if (!fieldPaths.Contains(path))
                {
                    fieldPaths.Add(path);
                }
            }
            // Check for Transform/GameObject fields
            else if (field.FieldType == typeof(Transform) || field.FieldType == typeof(GameObject))
            {
                string path = basePath + "." + field.Name;
                if (!fieldPaths.Contains(path))
                {
                    fieldPaths.Add(path);
                }
            }
            // Recurse into nested serializable types (but avoid infinite loops)
            else if (field.FieldType.IsSerializable &&
                     !field.FieldType.IsPrimitive &&
                     field.FieldType != typeof(string) &&
                     !field.FieldType.IsArray &&
                     !field.FieldType.IsGenericType)
            {
                InspectTypeForFillableFields(field.FieldType, basePath + "." + field.Name, fieldPaths);
            }
        }
    }

    /// <summary>
    /// Gets the element type of an array/list SerializedProperty
    /// </summary>
    private static System.Type GetArrayElementType(SerializedProperty arrayProperty)
    {
        try
        {
            System.Type parentType = arrayProperty.serializedObject.targetObject.GetType();
            string[] pathParts = arrayProperty.propertyPath.Split('.');

            System.Reflection.FieldInfo fieldInfo = null;
            System.Type currentType = parentType;

            foreach (string part in pathParts)
            {
                fieldInfo = currentType.GetField(part, System.Reflection.BindingFlags.Public |
                                                       System.Reflection.BindingFlags.NonPublic |
                                                       System.Reflection.BindingFlags.Instance);

                if (fieldInfo != null)
                {
                    currentType = fieldInfo.FieldType;
                }
            }

            if (currentType != null)
            {
                // Handle arrays
                if (currentType.IsArray)
                {
                    return currentType.GetElementType();
                }
                // Handle Lists
                else if (currentType.IsGenericType && currentType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    return currentType.GetGenericArguments()[0];
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    /// <summary>
    /// Legacy method name for backward compatibility
    /// </summary>
    public static List<string> GetTransformAndGameObjectFields(Component component)
    {
        return GetFillableFields(component);
    }

    /// <summary>
    /// Gets the actual type of a SerializedProperty field
    /// </summary>
    private static System.Type GetFieldType(SerializedProperty property)
    {
        try
        {
            System.Type parentType = property.serializedObject.targetObject.GetType();
            string[] pathParts = property.propertyPath.Replace("Array.data[", "[").Split('.');

            System.Reflection.FieldInfo fieldInfo = null;

            for (int i = 0; i < pathParts.Length; i++)
            {
                string part = pathParts[i];

                // Handle array elements
                if (part.Contains("["))
                {
                    // Extract field name before the bracket
                    string fieldName = part.Substring(0, part.IndexOf('['));
                    fieldInfo = parentType.GetField(fieldName, System.Reflection.BindingFlags.Public |
                                                            System.Reflection.BindingFlags.NonPublic |
                                                            System.Reflection.BindingFlags.Instance);

                    if (fieldInfo != null)
                    {
                        // Get the element type if it's an array or list
                        if (fieldInfo.FieldType.IsArray)
                        {
                            parentType = fieldInfo.FieldType.GetElementType();
                        }
                        else if (fieldInfo.FieldType.IsGenericType)
                        {
                            parentType = fieldInfo.FieldType.GetGenericArguments()[0];
                        }
                    }
                }
                else
                {
                    fieldInfo = parentType.GetField(part, System.Reflection.BindingFlags.Public |
                                                         System.Reflection.BindingFlags.NonPublic |
                                                         System.Reflection.BindingFlags.Instance);

                    if (fieldInfo != null)
                    {
                        parentType = fieldInfo.FieldType;
                    }
                }
            }

            return fieldInfo != null ? fieldInfo.FieldType : null;
        }
        catch
        {
            return null;
        }
    }
    #endif

    private void OnValidate()
    {
        if (linkFrom == null)
        {
            linkFrom = gameObject;
        }

        if (enableAdvancedLinkTargetMode && offsetPaths.Count == 0)
        {
            offsetPaths.Add(new OffsetPath());
        }

        foreach (var componentRef in componentsToMove)
        {
            if (componentRef != null && componentRef.component != null)
            {
                componentRef.componentTypeName = componentRef.component.GetType().Name;
            }
        }

        // Auto-fill sourceGameObject for field mappings (FillFields mode)
        foreach (var mapping in fieldMappings)
        {
            if (mapping != null && mapping.sourceGameObject == null)
            {
                mapping.sourceGameObject = gameObject;
            }

            // Update component type name for display
            if (mapping != null && mapping.targetComponent != null)
            {
                mapping.componentTypeName = mapping.targetComponent.GetType().Name;
            }
        }
    }
}