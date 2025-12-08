#if UNITY_EDITOR && VRC_SDK_VRCSDK3
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDKBase;
using VRC.SDKBase.Editor.BuildPipeline;

/// <summary>
/// Build processor for DisplaceComponent
/// Uses VRChat SDK's proper build callbacks for restoration - no timers needed!
/// </summary>
public class DisplaceComponentBuildProcessor : IVRCSDKBuildRequestedCallback, IVRCSDKPostprocessAvatarCallback
{
    public int callbackOrder => -2000;

    [System.Serializable]
    private class ComponentBackup
    {
        public GameObject targetGameObject;
        public int componentIndex;

        // Operation mode
        public DisplaceComponent.OperationMode operationMode;

        // DisplaceComponent configuration
        public GameObject linkFrom;
        public HumanBodyBones linkToBone;
        public GameObject linkToManual;
        public bool enableAdvancedLinkTargetMode;
        public List<string> offsetPaths = new List<string>();

        // Store component type names and their GameObjects for reconstruction (Displace mode)
        public List<ComponentToMoveData> componentsToMoveData = new List<ComponentToMoveData>();

        // Store field mappings for reconstruction (FillFields mode)
        public List<FieldMappingBackup> fieldMappingBackups = new List<FieldMappingBackup>();
    }

    [System.Serializable]
    private class ComponentToMoveData
    {
        public string componentTypeName;
        public string componentTypeFullName;
        public GameObject sourceGameObject;
    }

    [System.Serializable]
    private class FieldMappingBackup
    {
        public GameObject sourceGameObject;
        public GameObject componentGameObject;
        public string componentTypeName;
        public string fieldPath;
        public HumanBodyBones targetBone;
        public string searchObjectName;
        public string textValue;
        public string searchPath;
        public bool skipIfAlreadyFilled;
    }

    private static List<ComponentBackup> componentBackups = new List<ComponentBackup>();
    private static List<DisplaceComponent> processedComponents = new List<DisplaceComponent>();
    private static bool isProcessing = false;

    public static bool IsProcessingBuild()
    {
        return isProcessing && processedComponents.Count > 0;
    }

    public static void ForceRestoreAllStates()
    {
        Debug.Log("[DisplaceComponent] Force restore triggered");
        RestoreAllStates();
    }

    /// <summary>
    /// Called at the START of the build process
    /// </summary>
    public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
    {
        if (requestedBuildType != VRCSDKRequestedBuildType.Avatar)
        {
            Debug.Log("[DisplaceComponent] Build processor: Not an avatar build, skipping");
            return true;
        }

        Debug.Log("========================================");
        Debug.Log("[DisplaceComponent] BUILD STARTED - OnBuildRequested");
        Debug.Log($"[DisplaceComponent] Callback Order: {callbackOrder}");
        Debug.Log("========================================");

        try
        {
            isProcessing = true;
            componentBackups.Clear();
            processedComponents.Clear();

            var displaceComponents = Object.FindObjectsOfType<DisplaceComponent>()
                .Where(dc => dc != null && dc.gameObject != null && dc.enabled)
                .ToList();

            Debug.Log($"[DisplaceComponent] Found {displaceComponents.Count} DisplaceComponent(s) in scene");

            if (displaceComponents.Count == 0)
            {
                Debug.Log("[DisplaceComponent] No DisplaceComponent instances to process");
                isProcessing = false;
                return true;
            }

            if (DisplaceComponentMenu.ShouldShowBuildPopup())
            {
                bool proceed = EditorUtility.DisplayDialog(
                    "Displace Component - Running",
                    $"DisplaceComponent build processor is running!\n\n" +
                    $"Found: {displaceComponents.Count} component(s) to process\n" +
                    $"Callback Order: {callbackOrder} (runs BEFORE VRCFury)\n\n" +
                    "Components will be displaced now, then automatically restored when build completes.\n\n" +
                    "Click OK to continue build.\n\n" +
                    "(Disable this popup: Tools > Displace Component > Settings)",
                    "OK",
                    "Cancel Build"
                );

                if (!proceed)
                {
                    Debug.LogWarning("[DisplaceComponent] Build cancelled by user");
                    isProcessing = false;
                    return false;
                }
            }

            Debug.Log("[DisplaceComponent] Starting displacement process...");

            int successCount = 0;
            foreach (var component in displaceComponents)
            {
                Debug.Log($"[DisplaceComponent] Processing: {component.gameObject.name}");
                
                ComponentBackup backup = CreateBackup(component);
                if (backup != null)
                {
                    componentBackups.Add(backup);
                    Debug.Log($"[DisplaceComponent]   ✓ Backup created");
                    
                    if (component.PerformBuildTimeDisplacement())
                    {
                        processedComponents.Add(component);
                        successCount++;
                        Debug.Log($"[DisplaceComponent]   ✓ Displacement successful");
                    }
                    else
                    {
                        Debug.LogWarning($"[DisplaceComponent]   ✗ Displacement failed");
                    }
                }
                else
                {
                    Debug.LogError($"[DisplaceComponent]   ✗ Backup creation failed");
                }
            }

            Debug.Log("========================================");
            Debug.Log($"[DisplaceComponent] DISPLACEMENT COMPLETED: {successCount}/{displaceComponents.Count}");
            Debug.Log($"[DisplaceComponent] Waiting for build to complete...");
            Debug.Log("========================================");

            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError("========================================");
            Debug.LogError($"[DisplaceComponent] ERROR during displacement:");
            Debug.LogError($"{e.Message}");
            Debug.LogError($"{e.StackTrace}");
            Debug.LogError("========================================");
            
            EditorUtility.DisplayDialog(
                "Displace Component - Error",
                $"An error occurred during component displacement:\n\n{e.Message}\n\n" +
                "Check the Console for details.",
                "OK"
            );
            
            RestoreAllStates();
            return true;
        }
    }

    /// <summary>
    /// Called AFTER the build process completes (success or failure)
    /// This is the magic - VRChat SDK tells us when it's done!
    /// </summary>
    public void OnPostprocessAvatar()
    {
        Debug.Log("========================================");
        Debug.Log("[DisplaceComponent] BUILD COMPLETED - OnPostprocessAvatar");
        Debug.Log("[DisplaceComponent] SDK build finished, starting restoration...");
        Debug.Log("========================================");
        
        // Restore everything now that build is complete
        RestoreAllStates();
    }

    private static ComponentBackup CreateBackup(DisplaceComponent component)
    {
        try
        {
            ComponentBackup backup = new ComponentBackup();
            backup.targetGameObject = component.gameObject;

            Component[] allComponents = component.gameObject.GetComponents<Component>();
            backup.componentIndex = System.Array.IndexOf(allComponents, component);

            Debug.Log($"[DisplaceComponent]   Creating backup (index: {backup.componentIndex})");

            // Store operation mode
            backup.operationMode = component.operationMode;
            Debug.Log($"[DisplaceComponent]   Operation Mode: {backup.operationMode}");

            // Store DisplaceComponent configuration
            backup.linkFrom = component.linkFrom;
            backup.linkToBone = component.linkToBone;
            backup.linkToManual = component.linkToManual;
            backup.enableAdvancedLinkTargetMode = component.enableAdvancedLinkTargetMode;

            // Store offset paths
            if (component.offsetPaths != null)
            {
                foreach (var offsetPath in component.offsetPaths)
                {
                    if (offsetPath != null)
                    {
                        backup.offsetPaths.Add(offsetPath.path);
                    }
                }
            }

            if (backup.operationMode == DisplaceComponent.OperationMode.Displace)
            {
                // Store components to move data (Displace mode)
                if (component.componentsToMove != null)
                {
                    Debug.Log($"[DisplaceComponent]   Backing up {component.componentsToMove.Count} component reference(s)");
                    foreach (var compRef in component.componentsToMove)
                    {
                        if (compRef != null && compRef.component != null)
                        {
                            ComponentToMoveData data = new ComponentToMoveData();
                            data.componentTypeName = compRef.component.GetType().Name;
                            data.componentTypeFullName = compRef.component.GetType().AssemblyQualifiedName;
                            data.sourceGameObject = compRef.component.gameObject;
                            backup.componentsToMoveData.Add(data);

                            Debug.Log($"[DisplaceComponent]     • {data.componentTypeName}");
                        }
                    }
                }
            }
            else // FillFields mode
            {
                // Store field mappings data (FillFields mode)
                if (component.fieldMappings != null)
                {
                    Debug.Log($"[DisplaceComponent]   Backing up {component.fieldMappings.Count} field mapping(s)");
                    foreach (var mapping in component.fieldMappings)
                    {
                        if (mapping != null && mapping.targetComponent != null && !string.IsNullOrEmpty(mapping.fieldPath))
                        {
                            FieldMappingBackup data = new FieldMappingBackup();
                            data.sourceGameObject = mapping.sourceGameObject;
                            data.componentGameObject = mapping.targetComponent.gameObject;
                            data.componentTypeName = mapping.targetComponent.GetType().AssemblyQualifiedName;
                            data.fieldPath = mapping.fieldPath;
                            data.targetBone = mapping.targetBone;
                            data.searchObjectName = mapping.searchObjectName;
                            data.textValue = mapping.textValue;
                            data.searchPath = mapping.searchPath;
                            data.skipIfAlreadyFilled = mapping.skipIfAlreadyFilled;
                            backup.fieldMappingBackups.Add(data);

                            string fillInfo = !string.IsNullOrEmpty(mapping.searchObjectName) ? $"[search obj: {mapping.searchObjectName}]" :
                                            !string.IsNullOrEmpty(mapping.textValue) ? $"\"{mapping.textValue}\"" :
                                            !string.IsNullOrEmpty(mapping.searchPath) ? $"[search path: {mapping.searchPath}]" :
                                            mapping.targetBone.ToString();
                            Debug.Log($"[DisplaceComponent]     • {mapping.targetComponent.GetType().Name}.{mapping.fieldPath} → {fillInfo}");
                        }
                    }
                }
            }

            return backup;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[DisplaceComponent] Failed to create backup: {e.Message}");
            return null;
        }
    }

    private static void RestoreAllStates()
    {
        if (!isProcessing)
        {
            Debug.Log("[DisplaceComponent] Not processing, nothing to restore");
            return;
        }

        if (processedComponents.Count == 0 && componentBackups.Count == 0)
        {
            Debug.Log("[DisplaceComponent] No components to restore");
            isProcessing = false;
            return;
        }

        Debug.Log("========================================");
        Debug.Log($"[DisplaceComponent] RESTORATION STARTING");
        Debug.Log($"[DisplaceComponent]   - Components to restore: {processedComponents.Count}");
        Debug.Log($"[DisplaceComponent]   - Backups available: {componentBackups.Count}");
        Debug.Log("========================================");

        int restoredCount = 0;
        int displaceComponentsRestored = 0;
        int failedCount = 0;
        List<GameObject> affectedObjects = new List<GameObject>();

        // STEP 1: Restore displaced components
        Debug.Log("[DisplaceComponent] STEP 1: Restoring displaced components...");
        foreach (var component in processedComponents)
        {
            if (component != null)
            {
                try
                {
                    Debug.Log($"[DisplaceComponent]   Restoring: {component.gameObject.name}");
                    component.RestoreBuildTimeState();
                    restoredCount++;
                    
                    if (!affectedObjects.Contains(component.gameObject))
                    {
                        affectedObjects.Add(component.gameObject);
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[DisplaceComponent]   ✗ Failed: {e.Message}");
                    failedCount++;
                }
            }
        }

        // STEP 2: Restore DisplaceComponent instances
        Debug.Log("[DisplaceComponent] STEP 2: Restoring DisplaceComponent instances...");
        foreach (var backup in componentBackups)
        {
            if (backup.targetGameObject == null)
            {
                Debug.LogWarning("[DisplaceComponent]   GameObject is null, skipping");
                failedCount++;
                continue;
            }

            try
            {
                Debug.Log($"[DisplaceComponent]   Restoring on: {backup.targetGameObject.name}");
                
                DisplaceComponent existingComponent = backup.targetGameObject.GetComponent<DisplaceComponent>();
                
                if (existingComponent == null)
                {
                    Debug.Log($"[DisplaceComponent]     Re-adding component...");
                    existingComponent = backup.targetGameObject.AddComponent<DisplaceComponent>();
                    displaceComponentsRestored++;
                }
                
                // Restore configuration
                existingComponent.operationMode = backup.operationMode;
                existingComponent.linkFrom = backup.linkFrom;
                existingComponent.linkToBone = backup.linkToBone;
                existingComponent.linkToManual = backup.linkToManual;
                existingComponent.enableAdvancedLinkTargetMode = backup.enableAdvancedLinkTargetMode;

                Debug.Log($"[DisplaceComponent]     Restoring mode: {backup.operationMode}");

                // Restore offset paths
                existingComponent.offsetPaths.Clear();
                foreach (var path in backup.offsetPaths)
                {
                    existingComponent.offsetPaths.Add(new DisplaceComponent.OffsetPath(path));
                }

                if (backup.operationMode == DisplaceComponent.OperationMode.Displace)
                {
                    // Restore components to move list (Displace mode)
                    existingComponent.componentsToMove.Clear();
                    Debug.Log($"[DisplaceComponent]     Restoring {backup.componentsToMoveData.Count} component reference(s)");

                    foreach (var data in backup.componentsToMoveData)
                    {
                        if (data.sourceGameObject != null)
                        {
                            System.Type componentType = System.Type.GetType(data.componentTypeFullName);
                            if (componentType != null)
                            {
                                Component comp = data.sourceGameObject.GetComponent(componentType);
                                if (comp != null)
                                {
                                    existingComponent.componentsToMove.Add(new DisplaceComponent.ComponentReference(comp));
                                    Debug.Log($"[DisplaceComponent]       ✓ {data.componentTypeName}");
                                }
                                else
                                {
                                    Debug.LogWarning($"[DisplaceComponent]       ✗ Component not found: {data.componentTypeName}");
                                }
                            }
                        }
                    }

                    Debug.Log($"[DisplaceComponent]     Final component count: {existingComponent.componentsToMove.Count}");
                }
                else // FillFields mode
                {
                    // Restore field mappings list (FillFields mode)
                    existingComponent.fieldMappings.Clear();
                    Debug.Log($"[DisplaceComponent]     Restoring {backup.fieldMappingBackups.Count} field mapping(s)");

                    foreach (var data in backup.fieldMappingBackups)
                    {
                        if (data.componentGameObject != null)
                        {
                            System.Type componentType = System.Type.GetType(data.componentTypeName);
                            if (componentType != null)
                            {
                                Component comp = data.componentGameObject.GetComponent(componentType);
                                if (comp != null)
                                {
                                    DisplaceComponent.FieldMapping mapping = new DisplaceComponent.FieldMapping();
                                    mapping.sourceGameObject = data.sourceGameObject;
                                    mapping.targetComponent = comp;
                                    mapping.fieldPath = data.fieldPath;
                                    mapping.targetBone = data.targetBone;
                                    mapping.searchObjectName = data.searchObjectName;
                                    mapping.textValue = data.textValue;
                                    mapping.searchPath = data.searchPath;
                                    mapping.skipIfAlreadyFilled = data.skipIfAlreadyFilled;
                                    existingComponent.fieldMappings.Add(mapping);

                                    string fillInfo = !string.IsNullOrEmpty(data.searchObjectName) ? $"[search obj: {data.searchObjectName}]" :
                                                    !string.IsNullOrEmpty(data.textValue) ? $"\"{data.textValue}\"" :
                                                    !string.IsNullOrEmpty(data.searchPath) ? $"[search path: {data.searchPath}]" :
                                                    data.targetBone.ToString();
                                    Debug.Log($"[DisplaceComponent]       ✓ {comp.GetType().Name}.{data.fieldPath} → {fillInfo}");
                                }
                                else
                                {
                                    Debug.LogWarning($"[DisplaceComponent]       ✗ Component not found on {data.componentGameObject.name}");
                                }
                            }
                        }
                    }

                    Debug.Log($"[DisplaceComponent]     Final mapping count: {existingComponent.fieldMappings.Count}");
                }
                
                // Restore component order
                if (backup.componentIndex >= 0)
                {
                    Component[] components = backup.targetGameObject.GetComponents<Component>();
                    int currentIndex = System.Array.IndexOf(components, existingComponent);
                    
                    if (currentIndex > backup.componentIndex)
                    {
                        int movesNeeded = currentIndex - backup.componentIndex;
                        for (int i = 0; i < movesNeeded && i < 100; i++)
                        {
                            UnityEditorInternal.ComponentUtility.MoveComponentUp(existingComponent);
                        }
                        Debug.Log($"[DisplaceComponent]     Moved up {movesNeeded} positions");
                    }
                }
                
                EditorUtility.SetDirty(backup.targetGameObject);
                
                if (!affectedObjects.Contains(backup.targetGameObject))
                {
                    affectedObjects.Add(backup.targetGameObject);
                }
                
                Debug.Log($"[DisplaceComponent]     ✓ Complete");
                
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DisplaceComponent]   ✗ Failed: {e.Message}");
                failedCount++;
            }
        }

        Debug.Log("========================================");
        Debug.Log($"[DisplaceComponent] RESTORATION COMPLETED:");
        Debug.Log($"  - Displaced components: {restoredCount}");
        Debug.Log($"  - DisplaceComponents: {displaceComponentsRestored}");
        Debug.Log($"  - Failed: {failedCount}");
        Debug.Log("========================================");
        
        foreach (var obj in affectedObjects)
        {
            if (obj != null)
            {
                EditorUtility.SetDirty(obj);
            }
        }

        if (DisplaceComponentMenu.ShouldShowBuildPopup())
        {
            EditorUtility.DisplayDialog(
                "Displace Component - Complete",
                $"Build completed and scene restored!\n\n" +
                $"Displaced Components: {restoredCount}\n" +
                $"DisplaceComponents: {displaceComponentsRestored}\n" +
                $"Failed: {failedCount}",
                "OK"
            );
        }

        componentBackups.Clear();
        processedComponents.Clear();
        isProcessing = false;
    }

    /// <summary>
    /// Safety cleanup on scene/play mode changes
    /// </summary>
    [InitializeOnLoadMethod]
    private static void Initialize()
    {
        EditorApplication.playModeStateChanged += (state) =>
        {
            if (state == PlayModeStateChange.ExitingEditMode && isProcessing)
            {
                Debug.LogWarning("[DisplaceComponent] Entering play mode during build - forcing restore");
                RestoreAllStates();
            }
        };

        EditorApplication.quitting += () =>
        {
            if (isProcessing)
            {
                Debug.LogWarning("[DisplaceComponent] Editor quitting during build - forcing restore");
                RestoreAllStates();
            }
        };

        UnityEditor.SceneManagement.EditorSceneManager.activeSceneChangedInEditMode += (oldScene, newScene) =>
        {
            if (isProcessing)
            {
                Debug.LogWarning("[DisplaceComponent] Scene changed during build - forcing restore");
                RestoreAllStates();
            }
        };
    }
}

public class DisplaceComponentPreprocessor : IVRCSDKPreprocessAvatarCallback
{
    public int callbackOrder => -3000;

    public bool OnPreprocessAvatar(GameObject avatarGameObject)
    {
        Debug.Log("========================================");
        Debug.Log("[DisplaceComponent] PREPROCESSOR");
        Debug.Log($"[DisplaceComponent] Avatar: {avatarGameObject.name}");
        
        var displaceComponents = avatarGameObject.GetComponentsInChildren<DisplaceComponent>(true);
        Debug.Log($"[DisplaceComponent] Found {displaceComponents.Length} component(s)");
        
        Debug.Log("========================================");
        return true;
    }
}

public class DisplaceComponentCleanup : IVRCSDKPreprocessAvatarCallback
{
    public int callbackOrder => 1000;

    public bool OnPreprocessAvatar(GameObject avatarGameObject)
    {
        Debug.Log("========================================");
        Debug.Log("[DisplaceComponent] CLEANUP CALLBACK");
        Debug.Log("========================================");

        var displaceComponents = avatarGameObject.GetComponentsInChildren<DisplaceComponent>(true);
        
        if (displaceComponents.Length == 0)
        {
            Debug.Log("[DisplaceComponent] No components to remove");
            return true;
        }

        Debug.Log($"[DisplaceComponent] Removing {displaceComponents.Length} instance(s)");
        
        int removedCount = 0;
        foreach (var component in displaceComponents)
        {
            if (component != null)
            {
                GameObject go = component.gameObject;
                Object.DestroyImmediate(component);
                removedCount++;
                Debug.Log($"[DisplaceComponent] Removed from: {go.name}");
            }
        }

        Debug.Log($"[DisplaceComponent] Cleanup complete - removed {removedCount}");
        Debug.Log("[DisplaceComponent] Will be restored after build via OnPostprocessAvatar");
        Debug.Log("========================================");

        return true;
    }
}

public static class DisplaceComponentMenu
{
    private const string MENU_ROOT = "Tools/Shugan/Displace Component/";
    private const string POPUP_PREF_KEY = "DisplaceComponent_ShowPopup";

    [MenuItem(MENU_ROOT + "Test Displacement (No Build)")]
    private static void TestDisplacement()
    {
        var displaceComponents = Object.FindObjectsOfType<DisplaceComponent>();
        
        if (displaceComponents.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "Displace Component", 
                "No DisplaceComponent instances found in scene", 
                "OK"
            );
            return;
        }

        if (!EditorUtility.DisplayDialog(
            "Displace Component",
            $"Found {displaceComponents.Length} DisplaceComponent(s).\n\n" +
            "This will displace components in Edit mode.\n" +
            "You can manually restore using the Restore menu item.\n\n" +
            "Continue?",
            "Yes", "Cancel"))
        {
            return;
        }

        Debug.Log("========================================");
        Debug.Log("[DisplaceComponent] MANUAL TEST");
        Debug.Log("========================================");

        int successCount = 0;
        foreach (var component in displaceComponents)
        {
            if (component.PerformBuildTimeDisplacement())
            {
                successCount++;
            }
        }

        Debug.Log($"[DisplaceComponent] TEST COMPLETE: {successCount}/{displaceComponents.Length}");
        Debug.Log("========================================");

        EditorUtility.DisplayDialog(
            "Displace Component",
            $"Test completed: {successCount}/{displaceComponents.Length} succeeded\n\n" +
            "Check Console for details.",
            "OK"
        );
    }

    [MenuItem(MENU_ROOT + "Restore All Displacements")]
    private static void RestoreDisplacements()
    {
        if (DisplaceComponentBuildProcessor.IsProcessingBuild())
        {
            if (EditorUtility.DisplayDialog(
                "Displace Component",
                "A build is in progress.\n\n" +
                "Force restoration now?",
                "Yes",
                "Cancel"))
            {
                DisplaceComponentBuildProcessor.ForceRestoreAllStates();
                return;
            }
            else
            {
                return;
            }
        }
        
        var displaceComponents = Object.FindObjectsOfType<DisplaceComponent>();
        
        if (displaceComponents.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "Displace Component",
                "No DisplaceComponent instances found",
                "OK"
            );
            return;
        }

        Debug.Log("========================================");
        Debug.Log("[DisplaceComponent] MANUAL RESTORE");
        Debug.Log("========================================");

        int restoredCount = 0;
        foreach (var component in displaceComponents)
        {
            component.RestoreBuildTimeState();
            restoredCount++;
        }

        Debug.Log($"[DisplaceComponent] RESTORED: {restoredCount}");
        Debug.Log("========================================");

        EditorUtility.DisplayDialog(
            "Displace Component",
            $"Restored {restoredCount} component(s)",
            "OK"
        );
    }

    [MenuItem(MENU_ROOT + "Settings/Show Build Popup", true)]
    private static bool ShowPopupValidate()
    {
        Menu.SetChecked(MENU_ROOT + "Settings/Show Build Popup", GetShowPopupPreference());
        return true;
    }

    [MenuItem(MENU_ROOT + "Settings/Show Build Popup")]
    private static void ToggleShowPopup()
    {
        bool current = GetShowPopupPreference();
        SetShowPopupPreference(!current);
        
        EditorUtility.DisplayDialog(
            "Displace Component",
            $"Build popup is now {(!current ? "ENABLED" : "DISABLED")}",
            "OK"
        );
    }

    [MenuItem(MENU_ROOT + "Debug/Log All DisplaceComponents")]
    private static void LogAllComponents()
    {
        var displaceComponents = Object.FindObjectsOfType<DisplaceComponent>();
        
        Debug.Log("========================================");
        Debug.Log($"[DisplaceComponent] FOUND: {displaceComponents.Length}");
        Debug.Log("========================================");
        
        foreach (var dc in displaceComponents)
        {
            Debug.Log($"GameObject: {dc.gameObject.name}");
            Debug.Log($"  Link From: {(dc.linkFrom != null ? dc.linkFrom.name : "NULL")}");
            Debug.Log($"  Link To: {dc.linkToBone}");
            Debug.Log($"  Components: {dc.componentsToMove?.Count ?? 0}");
        }
        
        Debug.Log("========================================");
    }

    [MenuItem(MENU_ROOT + "About")]
    private static void About()
    {
        EditorUtility.DisplayDialog(
            "Displace Component",
            "Displace Component v2.3\n\n" +
            "✓ Implements IEditorOnly (no SDK warnings!)\n" +
            "✓ Uses VRChat SDK's OnPostprocessAvatar callback\n" +
            "✓ No timers or frame counting needed\n" +
            "✓ Automatic restoration when build completes\n" +
            "✓ Works with VRCFury and other build tools\n\n" +
            "Build Order:\n" +
            "• Preprocessor (-3000)\n" +
            "• Displace (-2000)\n" +
            "• VRCFury (~-1024)\n" +
            "• Cleanup (1000)\n" +
            "• SDK Build\n" +
            "• OnPostprocessAvatar → Restore!\n\n" +
            "The Magic:\n" +
            "IEditorOnly tells SDK to ignore this component,\n" +
            "so you never see 'will be removed' warnings!",
            "OK"
        );
    }

    private static bool GetShowPopupPreference()
    {
        return EditorPrefs.GetBool(POPUP_PREF_KEY, true);
    }

    private static void SetShowPopupPreference(bool value)
    {
        EditorPrefs.SetBool(POPUP_PREF_KEY, value);
    }

    public static bool ShouldShowBuildPopup()
    {
        return GetShowPopupPreference();
    }
}
#endif