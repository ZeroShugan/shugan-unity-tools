#if UNITY_EDITOR && VRC_SDK_VRCSDK3
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDKBase;
using VRC.SDKBase.Editor.BuildPipeline;
using System.IO;

/// <summary>
/// Simple file logger for detailed build logs
/// Writes to Logs folder with timestamps
/// </summary>
public static class DisplaceComponentLogger
{
    private static StreamWriter logWriter;
    private static string logFilePath;

    public static void StartNewLog()
    {
        try
        {
            // Find the script location
            string scriptPath = GetScriptPath();
            string logFolder = Path.Combine(scriptPath, "Logs");

            // Create Logs folder if it doesn't exist
            if (!Directory.Exists(logFolder))
            {
                Directory.CreateDirectory(logFolder);
            }

            // Create log file with timestamp
            string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string logFileName = $"DisplaceComponent_Build_{timestamp}.log";
            logFilePath = Path.Combine(logFolder, logFileName);

            logWriter = new StreamWriter(logFilePath, false);
            logWriter.AutoFlush = true;

            // Write header
            Log("========================================");
            Log("DisplaceComponent Build Log");
            Log($"Started: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("========================================");
            Log("");

            Debug.Log($"[DisplaceComponent] 📝 Detailed logs will be saved to: {logFilePath}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[DisplaceComponent] Failed to create log file: {e.Message}");
        }
    }

    public static void Log(string message)
    {
        if (logWriter != null)
        {
            string timestamp = System.DateTime.Now.ToString("HH:mm:ss.fff");
            logWriter.WriteLine($"[{timestamp}] {message}");
        }
    }

    public static void CloseLog()
    {
        if (logWriter != null)
        {
            Log("");
            Log("========================================");
            Log($"Completed: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("========================================");

            logWriter.Close();
            logWriter = null;

            Debug.Log($"[DisplaceComponent] 📄 Detailed log saved to: {logFilePath}");
        }
    }

    private static string GetScriptPath()
    {
        // Find the DisplaceComponentBuildProcessor.cs file
        string[] guids = AssetDatabase.FindAssets("DisplaceComponentBuildProcessor t:script");
        if (guids.Length > 0)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            string fullPath = Path.GetFullPath(assetPath);
            return Path.GetDirectoryName(fullPath);
        }

        // Fallback to Assets folder
        return Path.GetFullPath("Assets");
    }
}

/// <summary>
/// Prefab Unpacker - Unpacks prefabs with DisplaceComponent BEFORE build starts
/// This allows DisplaceComponent to work with direct scene references instead of prefab references
/// Runs at -100000 to execute before VRCFury (-1024) and everything else
/// </summary>
public class DisplaceComponentPrefabUnpacker : IVRCSDKBuildRequestedCallback, IVRCSDKPostprocessAvatarCallback
{
    public int callbackOrder => -100000;  // Run VERY early, before VRCFury and everything else

    [System.Serializable]
    private class UnpackedPrefabInfo
    {
        public GameObject unpackedInstance;      // The unpacked GameObject in the scene
        public string prefabAssetPath;           // Path to the original prefab asset
        public Transform parentTransform;        // Parent in hierarchy
        public int siblingIndex;                 // Position in parent's children
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public string gameObjectName;
    }

    private static List<UnpackedPrefabInfo> unpackedPrefabs = new List<UnpackedPrefabInfo>();

    static DisplaceComponentPrefabUnpacker()
    {
        Debug.Log("[DisplaceComponent Prefab Unpacker] ✓ Prefab unpacker loaded! (callback order: -100000)");
    }

    public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
    {
        if (requestedBuildType != VRCSDKRequestedBuildType.Avatar)
            return true;

        Debug.Log("========================================");
        Debug.Log("[DisplaceComponent Prefab Unpacker] PREFAB UNPACKING PHASE");
        Debug.Log("[DisplaceComponent Prefab Unpacker] Running BEFORE VRCFury and all other processors");
        Debug.Log("========================================");

        DisplaceComponentLogger.Log("========================================");
        DisplaceComponentLogger.Log("PREFAB UNPACKING PHASE (callback order: -100000)");
        DisplaceComponentLogger.Log("========================================");

        unpackedPrefabs.Clear();

        // Find the avatar being built
        GameObject avatarRoot = FindAvatarRoot();
        if (avatarRoot == null)
        {
            Debug.LogWarning("[DisplaceComponent Prefab Unpacker] Could not find avatar root");
            return true;
        }

        Debug.Log($"[DisplaceComponent Prefab Unpacker] Avatar found: {avatarRoot.name}");
        DisplaceComponentLogger.Log($"Avatar: {avatarRoot.name}");

        // Find all DisplaceComponents on the avatar
        var allDisplaceComponents = avatarRoot.GetComponentsInChildren<DisplaceComponent>(true);
        Debug.Log($"[DisplaceComponent Prefab Unpacker] Found {allDisplaceComponents.Length} DisplaceComponent(s)");
        DisplaceComponentLogger.Log($"Found {allDisplaceComponents.Length} DisplaceComponent(s) to check");

        // Find unique prefab instances that contain DisplaceComponents
        HashSet<GameObject> prefabRootsToUnpack = new HashSet<GameObject>();

        foreach (var component in allDisplaceComponents)
        {
            if (component == null) continue;

            // Check if this component is part of a prefab instance
            if (PrefabUtility.IsPartOfPrefabInstance(component))
            {
                // Get the outermost prefab instance root
                GameObject prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(component);
                if (prefabRoot != null && !prefabRootsToUnpack.Contains(prefabRoot))
                {
                    prefabRootsToUnpack.Add(prefabRoot);
                    Debug.Log($"[DisplaceComponent Prefab Unpacker] Detected prefab to unpack: {prefabRoot.name}");
                    DisplaceComponentLogger.Log($"  Prefab to unpack: {prefabRoot.name}");
                }
            }
        }

        if (prefabRootsToUnpack.Count == 0)
        {
            Debug.Log("[DisplaceComponent Prefab Unpacker] No prefabs to unpack - all DisplaceComponents are on non-prefab objects");
            DisplaceComponentLogger.Log("No prefabs to unpack");
            return true;
        }

        Debug.Log($"[DisplaceComponent Prefab Unpacker] Unpacking {prefabRootsToUnpack.Count} prefab(s)...");
        DisplaceComponentLogger.Log($"Unpacking {prefabRootsToUnpack.Count} prefab(s)...");
        DisplaceComponentLogger.Log("");

        // Unpack each prefab
        foreach (var prefabRoot in prefabRootsToUnpack)
        {
            UnpackPrefab(prefabRoot);
        }

        Debug.Log("========================================");
        Debug.Log($"[DisplaceComponent Prefab Unpacker] ✓ Unpacked {unpackedPrefabs.Count} prefab(s)");
        Debug.Log("[DisplaceComponent Prefab Unpacker] Prefabs are now regular GameObjects");
        Debug.Log("[DisplaceComponent Prefab Unpacker] Proceeding with normal build (VRCFury, DisplaceComponent, etc.)");
        Debug.Log("========================================");

        DisplaceComponentLogger.Log("========================================");
        DisplaceComponentLogger.Log($"✓ Unpacking complete: {unpackedPrefabs.Count} prefab(s) unpacked");
        DisplaceComponentLogger.Log("========================================");
        DisplaceComponentLogger.Log("");

        return true;
    }

    private void UnpackPrefab(GameObject prefabInstance)
    {
        try
        {
            // Store information about the prefab BEFORE unpacking
            UnpackedPrefabInfo info = new UnpackedPrefabInfo();
            info.unpackedInstance = prefabInstance;
            info.gameObjectName = prefabInstance.name;

            // Get prefab asset path
            GameObject prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(prefabInstance);
            if (prefabAsset != null)
            {
                info.prefabAssetPath = AssetDatabase.GetAssetPath(prefabAsset);
            }

            // Store transform information
            Transform transform = prefabInstance.transform;
            info.parentTransform = transform.parent;
            info.siblingIndex = transform.GetSiblingIndex();
            info.localPosition = transform.localPosition;
            info.localRotation = transform.localRotation;
            info.localScale = transform.localScale;

            Debug.Log($"[DisplaceComponent Prefab Unpacker]   Unpacking: {prefabInstance.name}");
            Debug.Log($"[DisplaceComponent Prefab Unpacker]     Asset: {info.prefabAssetPath}");
            Debug.Log($"[DisplaceComponent Prefab Unpacker]     Parent: {(info.parentTransform != null ? info.parentTransform.name : "Scene Root")}");

            DisplaceComponentLogger.Log($"  Unpacking: {prefabInstance.name}");
            DisplaceComponentLogger.Log($"    Asset path: {info.prefabAssetPath}");
            DisplaceComponentLogger.Log($"    Parent: {(info.parentTransform != null ? info.parentTransform.name : "Scene Root")}");
            DisplaceComponentLogger.Log($"    Sibling index: {info.siblingIndex}");

            // UNPACK THE PREFAB COMPLETELY
            PrefabUtility.UnpackPrefabInstance(prefabInstance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            unpackedPrefabs.Add(info);

            Debug.Log($"[DisplaceComponent Prefab Unpacker]     ✓ Unpacked successfully");
            DisplaceComponentLogger.Log($"    ✓ Unpacked successfully");
            DisplaceComponentLogger.Log("");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[DisplaceComponent Prefab Unpacker] Failed to unpack {prefabInstance.name}: {e.Message}");
            DisplaceComponentLogger.Log($"  ✗ FAILED to unpack {prefabInstance.name}: {e.Message}");
        }
    }

    public void OnPostprocessAvatar()
    {
        Debug.Log("========================================");
        Debug.Log("[DisplaceComponent Prefab Unpacker] PREFAB RESTORATION PHASE");
        Debug.Log("========================================");

        DisplaceComponentLogger.Log("========================================");
        DisplaceComponentLogger.Log("PREFAB RESTORATION PHASE");
        DisplaceComponentLogger.Log("========================================");

        if (unpackedPrefabs.Count == 0)
        {
            Debug.Log("[DisplaceComponent Prefab Unpacker] No prefabs to restore");
            return;
        }

        Debug.Log($"[DisplaceComponent Prefab Unpacker] Restoring {unpackedPrefabs.Count} prefab(s)...");
        DisplaceComponentLogger.Log($"Restoring {unpackedPrefabs.Count} prefab(s)...");

        int successCount = 0;
        int failCount = 0;

        foreach (var info in unpackedPrefabs)
        {
            try
            {
                if (info.unpackedInstance == null)
                {
                    Debug.LogWarning($"[DisplaceComponent Prefab Unpacker] Unpacked instance is null, skipping");
                    failCount++;
                    continue;
                }

                if (string.IsNullOrEmpty(info.prefabAssetPath))
                {
                    Debug.LogWarning($"[DisplaceComponent Prefab Unpacker] No prefab asset path for {info.gameObjectName}, skipping");
                    failCount++;
                    continue;
                }

                Debug.Log($"[DisplaceComponent Prefab Unpacker]   Restoring: {info.gameObjectName}");
                DisplaceComponentLogger.Log($"  Restoring: {info.gameObjectName}");

                // Load the prefab asset
                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(info.prefabAssetPath);
                if (prefabAsset == null)
                {
                    Debug.LogWarning($"[DisplaceComponent Prefab Unpacker] Could not load prefab asset at: {info.prefabAssetPath}");
                    failCount++;
                    continue;
                }

                // Instantiate the prefab at the same location
                GameObject newPrefabInstance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
                if (newPrefabInstance == null)
                {
                    Debug.LogWarning($"[DisplaceComponent Prefab Unpacker] Failed to instantiate prefab");
                    failCount++;
                    continue;
                }

                // Restore transform
                Transform newTransform = newPrefabInstance.transform;
                newTransform.SetParent(info.parentTransform);
                newTransform.SetSiblingIndex(info.siblingIndex);
                newTransform.localPosition = info.localPosition;
                newTransform.localRotation = info.localRotation;
                newTransform.localScale = info.localScale;
                newPrefabInstance.name = info.gameObjectName;

                // Destroy the unpacked instance
                GameObject.DestroyImmediate(info.unpackedInstance);

                Debug.Log($"[DisplaceComponent Prefab Unpacker]     ✓ Restored successfully");
                DisplaceComponentLogger.Log($"    ✓ Restored successfully");
                successCount++;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DisplaceComponent Prefab Unpacker] Failed to restore {info.gameObjectName}: {e.Message}");
                DisplaceComponentLogger.Log($"  ✗ FAILED to restore {info.gameObjectName}: {e.Message}");
                failCount++;
            }
        }

        Debug.Log("========================================");
        Debug.Log($"[DisplaceComponent Prefab Unpacker] ✓ Restoration complete: {successCount}/{unpackedPrefabs.Count}");
        if (failCount > 0)
        {
            Debug.LogWarning($"[DisplaceComponent Prefab Unpacker] {failCount} prefab(s) failed to restore");
        }
        Debug.Log("========================================");

        DisplaceComponentLogger.Log($"✓ Restoration complete: {successCount}/{unpackedPrefabs.Count}");
        if (failCount > 0)
        {
            DisplaceComponentLogger.Log($"⚠ {failCount} prefab(s) failed to restore");
        }
        DisplaceComponentLogger.Log("========================================");

        unpackedPrefabs.Clear();
    }

    private GameObject FindAvatarRoot()
    {
        // Find all VRCAvatarDescriptor components in the scene
        var avatarDescriptors = GameObject.FindObjectsOfType<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();

        if (avatarDescriptors.Length == 0)
        {
            Debug.LogWarning("[DisplaceComponent Prefab Unpacker] No VRCAvatarDescriptor found in scene");
            return null;
        }

        if (avatarDescriptors.Length == 1)
        {
            return avatarDescriptors[0].gameObject;
        }

        // Multiple avatars - find the one in the SDK control panel
        foreach (var descriptor in avatarDescriptors)
        {
            if (descriptor.gameObject.activeInHierarchy)
            {
                return descriptor.gameObject;
            }
        }

        // Fallback to first one
        return avatarDescriptors[0].gameObject;
    }
}

/// <summary>
/// Build processor for DisplaceComponent
/// Uses VRChat SDK's proper build callbacks for restoration - no timers needed!
/// </summary>
public class DisplaceComponentBuildProcessor : IVRCSDKBuildRequestedCallback, IVRCSDKPostprocessAvatarCallback
{
    public int callbackOrder => -1024;  // VRCFury uses -1024, run early for prefab compatibility

    // Add static constructor to verify class is loaded
    static DisplaceComponentBuildProcessor()
    {
        Debug.Log("========================================");
        Debug.Log($"[DisplaceComponent] ✓ Build processor class loaded!");
        Debug.Log($"[DisplaceComponent] Callback order: -1024 (same as VRCFury)");
        Debug.Log($"[DisplaceComponent] Implements:");
        Debug.Log($"[DisplaceComponent]   - IVRCSDKBuildRequestedCallback (OnBuildRequested)");
        Debug.Log($"[DisplaceComponent]   - IVRCSDKPostprocessAvatarCallback (OnPostprocessAvatar)");

        // Verify the interfaces are available
        var buildRequestedType = typeof(IVRCSDKBuildRequestedCallback);
        var postprocessType = typeof(IVRCSDKPostprocessAvatarCallback);
        Debug.Log($"[DisplaceComponent] ✓ Interfaces found in SDK");

        // List all methods
        var thisType = typeof(DisplaceComponentBuildProcessor);
        var methods = thisType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Debug.Log($"[DisplaceComponent] Public instance methods: {string.Join(", ", methods.Where(m => m.DeclaringType == thisType).Select(m => m.Name))}");
        Debug.Log("========================================");
    }

    [System.Serializable]
    private class ComponentBackup
    {
        // Store the AVATAR ROOT so we know where to search from
        public GameObject avatarRoot;

        // Store PATHS instead of direct references
        public string targetGameObjectPath;  // Path to the GameObject with DisplaceComponent
        public int componentIndex;

        // Operation mode
        public DisplaceComponent.OperationMode operationMode;

        // DisplaceComponent configuration (store paths, not direct references)
        public string linkFromPath;
        public HumanBodyBones linkToBone;
        public string linkToManualPath;
        public bool enableAdvancedLinkTargetMode;
        public List<string> offsetPaths = new List<string>();

        // For Displace mode
        public List<ComponentToMoveData> componentsToMoveData = new List<ComponentToMoveData>();

        // For FillFields mode
        public List<FieldMappingBackup> fieldMappingBackups = new List<FieldMappingBackup>();
    }

    [System.Serializable]
    private class ComponentToMoveData
    {
        public string componentTypeName;
        public string componentTypeFullName;
        public string sourceGameObjectPath;  // CHANGED: was GameObject, now string path
        public int componentIndex;  // Which component of this type on that GameObject
    }

    [System.Serializable]
    private class FieldMappingBackup
    {
        public string sourceGameObjectPath;  // CHANGED: was GameObject, now path
        public string componentGameObjectPath;  // CHANGED: was GameObject, now path
        public string componentTypeFullName;  // CHANGED: use full name to find component
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
    /// PRE-CLONE HOOK REGISTRATION - VRCFury Pattern for Prefab Compatibility
    /// Registers callback that fires BEFORE SDK creates the clone
    /// This is CRITICAL for prefab support!
    /// </summary>
    [InitializeOnLoadMethod]
    private static void RegisterPreCloneHook()
    {
        Debug.Log("[DisplaceComponent] Registering pre-clone hook (VRCFury pattern)");

        try
        {
            // Use reflection for backward compatibility with different SDK versions
            var controlPanelType = typeof(VRCSdkControlPanel);
            var onSdkPanelEnableEvent = controlPanelType.GetEvent("OnSdkPanelEnable");

            if (onSdkPanelEnableEvent != null)
            {
                System.Action<object, System.EventArgs> handler = (panel, ev) =>
                {
                    // Try to get builder using reflection for SDK compatibility
                    var tryGetBuilderMethod = controlPanelType.GetMethod("TryGetBuilder");
                    if (tryGetBuilderMethod != null)
                    {
                        try
                        {
                            // Get the builder interface type dynamically
                            var builderInterfaceType = System.Type.GetType("VRC.SDKBase.Editor.BuildPipeline.IVRCSdkAvatarBuilderApi, VRCSDKBase-Editor");
                            if (builderInterfaceType == null)
                            {
                                builderInterfaceType = System.Type.GetType("VRC.SDKBase.Editor.Api.IVRCSdkAvatarBuilderApi, VRCSDKBase-Editor");
                            }

                            if (builderInterfaceType != null)
                            {
                                var genericMethod = tryGetBuilderMethod.MakeGenericMethod(builderInterfaceType);
                                var parameters = new object[] { null };
                                var result = (bool)genericMethod.Invoke(null, parameters);

                                if (result && parameters[0] != null)
                                {
                                    var builder = parameters[0];
                                    var onSdkBuildStartEvent = builderInterfaceType.GetEvent("OnSdkBuildStart");
                                    if (onSdkBuildStartEvent != null)
                                    {
                                        var delegateType = onSdkBuildStartEvent.EventHandlerType;
                                        var handlerDelegate = System.Delegate.CreateDelegate(delegateType, typeof(DisplaceComponentBuildProcessor).GetMethod("OnSdkBuildStart", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
                                        onSdkBuildStartEvent.AddEventHandler(builder, handlerDelegate);
                                        Debug.Log("[DisplaceComponent] ✓ Pre-clone hook registered successfully via reflection");
                                        return;
                                    }
                                }
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"[DisplaceComponent] Could not register via reflection: {ex.Message}");
                        }
                    }

                    Debug.LogWarning("[DisplaceComponent] Pre-clone hook not available in this SDK version - prefab validation will be limited");
                };

                var delegateType = onSdkPanelEnableEvent.EventHandlerType;
                var handlerDelegate = System.Delegate.CreateDelegate(delegateType, handler.Target, handler.Method);
                onSdkPanelEnableEvent.AddEventHandler(null, handlerDelegate);
            }
            else
            {
                Debug.LogWarning("[DisplaceComponent] OnSdkPanelEnable event not found - using callback-only approach");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[DisplaceComponent] Pre-clone hook registration failed: {e.Message}");
            Debug.LogWarning("[DisplaceComponent] Falling back to callback-only approach (still works, but less validation)");
        }
    }

    /// <summary>
    /// Called BEFORE the SDK creates the clone - VRCFury's secret sauce!
    /// At this point, prefab references are completely intact
    /// </summary>
    private static void OnSdkBuildStart(object sender, object target)
    {
        if (!(target is GameObject avatarObj))
            return;

        Debug.Log("========================================");
        Debug.Log("[DisplaceComponent] 🎯 PRE-CLONE VALIDATION (VRCFury Pattern)");
        Debug.Log($"[DisplaceComponent] Avatar: {avatarObj.name}");
        Debug.Log($"[DisplaceComponent] This runs BEFORE clone is created!");
        Debug.Log("========================================");

        // Find all DisplaceComponents BEFORE clone is created
        var allComponents = avatarObj.GetComponentsInChildren<DisplaceComponent>(true);
        Debug.Log($"[DisplaceComponent] Found {allComponents.Length} component(s) to pre-validate");

        foreach (var component in allComponents)
        {
            PreValidateComponent(component);
        }

        Debug.Log($"[DisplaceComponent] ✓ Pre-clone validation complete");
        Debug.Log("========================================");
    }

    /// <summary>
    /// Pre-validate a component BEFORE cloning
    /// Ensures all references are valid when the clone is created
    /// </summary>
    private static void PreValidateComponent(DisplaceComponent component)
    {
        if (component == null) return;

        bool isPrefab = PrefabUtility.IsPartOfPrefabInstance(component);
        string prefabMark = isPrefab ? "📦" : "";

        // Compact logging - single line per component for console, detailed for file
        if (component.operationMode == DisplaceComponent.OperationMode.FillFields)
        {
            if (component.fieldMappings == null) return;

            int validMappings = 0;
            int nullMappings = 0;
            DisplaceComponentLogger.Log($"  {prefabMark} {component.gameObject.name} (FillFields mode):");

            for (int i = 0; i < component.fieldMappings.Count; i++)
            {
                var mapping = component.fieldMappings[i];
                if (mapping == null || mapping.targetComponent == null)
                {
                    nullMappings++;
                    DisplaceComponentLogger.Log($"    [{i}] NULL mapping");
                    continue;
                }
                validMappings++;
                DisplaceComponentLogger.Log($"    [{i}] {mapping.targetComponent.GetType().Name}.{mapping.fieldPath}");
            }

            if (nullMappings > 0)
            {
                Debug.LogWarning($"[DisplaceComponent]   {prefabMark} {component.gameObject.name}: {validMappings}/{component.fieldMappings.Count} mappings valid ({nullMappings} null)");
            }
            else
            {
                Debug.Log($"[DisplaceComponent]   {prefabMark} {component.gameObject.name}: {validMappings} mappings OK");
            }
        }
        else
        {
            if (component.componentsToMove == null) return;

            int validComponents = 0;
            DisplaceComponentLogger.Log($"  {prefabMark} {component.gameObject.name} (Displace mode):");

            for (int i = 0; i < component.componentsToMove.Count; i++)
            {
                var compRef = component.componentsToMove[i];
                if (compRef?.component == null)
                {
                    DisplaceComponentLogger.Log($"    [{i}] NULL component");
                    continue;
                }
                validComponents++;
                DisplaceComponentLogger.Log($"    [{i}] {compRef.component.GetType().Name}");
            }

            Debug.Log($"[DisplaceComponent]   {prefabMark} {component.gameObject.name}: {validComponents} components to move");
        }
    }

    /// <summary>
    /// Finds the avatar root GameObject that VRChat SDK is building
    /// This is the GameObject with VRCAvatarDescriptor component
    /// </summary>
    private static GameObject FindAvatarRootBeingBuilt()
    {
        Debug.Log("[DisplaceComponent] 🔍 Finding avatar root being built...");

        // During build, VRChat SDK creates a clone with "(Clone)" in the name
        // Find ALL GameObjects with VRCAvatarDescriptor
        VRC.SDK3.Avatars.Components.VRCAvatarDescriptor[] descriptors =
            Object.FindObjectsOfType<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();

        Debug.Log($"[DisplaceComponent] Found {descriptors.Length} avatar descriptor(s):");
        foreach (var descriptor in descriptors)
        {
            bool isClone = descriptor.gameObject.name.Contains("(Clone)");
            bool isPrefab = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(descriptor.gameObject);
            string prefabInfo = isPrefab ? " [PREFAB]" : " [NOT PREFAB]";
            Debug.Log($"[DisplaceComponent]   • {descriptor.gameObject.name}{prefabInfo} (Clone: {isClone})");
        }

        // Prefer the one with "(Clone)" in the name (that's the one being built)
        foreach (var descriptor in descriptors)
        {
            if (descriptor.gameObject.name.Contains("(Clone)"))
            {
                Debug.Log($"[DisplaceComponent] ✓ Selected avatar clone: {descriptor.gameObject.name}");
                return descriptor.gameObject;
            }
        }

        // Fallback: return the first one found
        if (descriptors.Length > 0)
        {
            Debug.Log($"[DisplaceComponent] ⚠ No clone found, using first avatar: {descriptors[0].gameObject.name}");
            return descriptors[0].gameObject;
        }

        Debug.LogError("[DisplaceComponent] ✗ No avatar root found!");
        return null;
    }

    /// <summary>
    /// Gets the path from avatar root to a GameObject
    /// Example: "Armature/Hips/Chest" or "MyPrefab/VRCFuryComponent"
    /// </summary>
    private static string GetPathFromRoot(GameObject obj, GameObject root)
    {
        if (obj == root)
        {
            Debug.Log($"[DisplaceComponent]     📍 Path: <ROOT> (object is avatar root)");
            return "";
        }

        List<string> path = new List<string>();
        Transform current = obj.transform;

        while (current != null && current.gameObject != root)
        {
            bool isPrefab = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(current.gameObject);
            path.Add(current.name);
            current = current.parent;
        }

        if (current == null)
        {
            Debug.LogError($"[DisplaceComponent] ✗ {obj.name} is not a child of {root.name}!");
            Debug.LogError($"[DisplaceComponent]   Object hierarchy: {GetFullHierarchyPath(obj)}");
            Debug.LogError($"[DisplaceComponent]   Root: {root.name}");
            return null;
        }

        path.Reverse();
        string fullPath = string.Join("/", path);
        bool objIsPrefab = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(obj);
        string prefabMark = objIsPrefab ? " 📦" : "";
        Debug.Log($"[DisplaceComponent]     📍 Path: {fullPath}{prefabMark}");
        return fullPath;
    }

    private static string GetFullHierarchyPath(GameObject obj)
    {
        List<string> path = new List<string>();
        Transform current = obj.transform;
        while (current != null)
        {
            path.Add(current.name);
            current = current.parent;
        }
        path.Reverse();
        return string.Join("/", path);
    }

    /// <summary>
    /// Finds a GameObject by path from avatar root
    /// Example: FindByPath(avatarRoot, "Armature/Hips/Chest")
    /// </summary>
    private static GameObject FindByPath(GameObject root, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            Debug.Log($"[DisplaceComponent]     🔍 FindByPath: <ROOT> (empty path)");
            return root;
        }

        Debug.Log($"[DisplaceComponent]     🔍 FindByPath: Looking for '{path}' under {root.name}");

        Transform current = root.transform;
        string[] parts = path.Split('/');
        string currentPath = root.name;

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            Transform found = null;

            Debug.Log($"[DisplaceComponent]       [{i+1}/{parts.Length}] Looking for child: '{part}' under '{current.name}'");
            Debug.Log($"[DisplaceComponent]         Current has {current.childCount} children");

            foreach (Transform child in current)
            {
                if (child.name == part)
                {
                    found = child;
                    bool isPrefab = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(child.gameObject);
                    string prefabMark = isPrefab ? " 📦" : "";
                    Debug.Log($"[DisplaceComponent]         ✓ Found: '{child.name}'{prefabMark}");
                    break;
                }
            }

            if (found == null)
            {
                Debug.LogError($"[DisplaceComponent]         ✗ NOT FOUND: '{part}'");
                Debug.LogError($"[DisplaceComponent]       Available children under '{current.name}':");
                foreach (Transform child in current)
                {
                    bool isPrefab = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(child.gameObject);
                    string prefabMark = isPrefab ? " 📦" : "";
                    Debug.LogError($"[DisplaceComponent]         • {child.name}{prefabMark}");
                }
                Debug.LogError($"[DisplaceComponent]       Full path attempted: {path}");
                Debug.LogError($"[DisplaceComponent]       Failed at: {part} (part {i+1}/{parts.Length})");
                return null;
            }

            current = found;
            currentPath += "/" + part;
        }

        bool finalIsPrefab = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(current.gameObject);
        string finalPrefabMark = finalIsPrefab ? " 📦" : "";
        Debug.Log($"[DisplaceComponent]     ✓ FindByPath SUCCESS: {current.gameObject.name}{finalPrefabMark}");
        return current.gameObject;
    }

    /// <summary>
    /// Finds the ORIGINAL (non-clone) avatar root
    /// </summary>
    private static GameObject FindOriginalAvatarRoot()
    {
        VRC.SDK3.Avatars.Components.VRCAvatarDescriptor[] descriptors =
            Object.FindObjectsOfType<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();

        // Find the one WITHOUT "(Clone)" in the name
        foreach (var descriptor in descriptors)
        {
            if (!descriptor.gameObject.name.Contains("(Clone)"))
            {
                Debug.Log($"[DisplaceComponent]       Found original avatar: {descriptor.gameObject.name}");
                return descriptor.gameObject;
            }
        }

        Debug.LogWarning("[DisplaceComponent]       Could not find original avatar root!");
        return null;
    }

    /// <summary>
    /// Remaps field mappings to use components from the clone instead of the original
    /// This is CRITICAL for prefabs - Unity doesn't automatically update these references
    /// </summary>
    private static void RemapFieldMappingsToClone(DisplaceComponent component, GameObject cloneAvatarRoot)
    {
        if (component.fieldMappings == null || component.fieldMappings.Count == 0)
            return;

        // Find the ORIGINAL avatar root (the one that was cloned)
        GameObject originalAvatarRoot = FindOriginalAvatarRoot();
        if (originalAvatarRoot == null)
        {
            Debug.LogError("[DisplaceComponent] Cannot remap without original avatar root!");
            return;
        }

        int successCount = 0;
        int failCount = 0;

        for (int i = 0; i < component.fieldMappings.Count; i++)
        {
            var mapping = component.fieldMappings[i];
            if (mapping == null) continue;

            bool mappingSuccess = true;

            // Remap sourceGameObject if it exists
            if (mapping.sourceGameObject != null)
            {
                string sourcePath = GetPathFromRoot(mapping.sourceGameObject, originalAvatarRoot);
                if (!string.IsNullOrEmpty(sourcePath))
                {
                    GameObject clonedSource = FindByPath(cloneAvatarRoot, sourcePath);
                    if (clonedSource != null)
                    {
                        mapping.sourceGameObject = clonedSource;
                    }
                    else
                    {
                        Debug.LogWarning($"[DisplaceComponent] ✗ Could not find sourceGameObject at: {sourcePath}");
                        mappingSuccess = false;
                    }
                }
            }

            // Remap targetComponent
            if (mapping.targetComponent != null)
            {
                string componentPath = GetPathFromRoot(mapping.targetComponent.gameObject, originalAvatarRoot);
                if (!string.IsNullOrEmpty(componentPath))
                {
                    GameObject clonedGameObject = FindByPath(cloneAvatarRoot, componentPath);
                    if (clonedGameObject != null)
                    {
                        System.Type componentType = mapping.targetComponent.GetType();
                        Component clonedComponent = clonedGameObject.GetComponent(componentType);
                        if (clonedComponent != null)
                        {
                            mapping.targetComponent = clonedComponent;
                        }
                        else
                        {
                            Debug.LogWarning($"[DisplaceComponent] ✗ Component {componentType.Name} not found on clone");
                            mappingSuccess = false;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[DisplaceComponent] ✗ Could not find GameObject at: {componentPath}");
                        mappingSuccess = false;
                    }
                }
            }

            if (mappingSuccess)
                successCount++;
            else
                failCount++;
        }

        // Only log summary to console, detailed to file
        if (failCount > 0)
        {
            Debug.LogWarning($"[DisplaceComponent] Remapped {successCount}/{component.fieldMappings.Count} field mappings ({failCount} failed)");
            DisplaceComponentLogger.Log($"  ⚠ Field mapping remapping: {successCount}/{component.fieldMappings.Count} successful, {failCount} failed");
        }
        else
        {
            DisplaceComponentLogger.Log($"  ✓ All {successCount} field mappings remapped successfully");
        }

    }

    /// <summary>
    /// Remaps componentsToMove to use components from the clone instead of the original
    /// </summary>
    private static void RemapComponentsToMoveToClone(DisplaceComponent component, GameObject cloneAvatarRoot)
    {
        if (component.componentsToMove == null || component.componentsToMove.Count == 0)
            return;

        // Find the ORIGINAL avatar root (the one that was cloned)
        GameObject originalAvatarRoot = FindOriginalAvatarRoot();
        if (originalAvatarRoot == null)
        {
            Debug.LogError("[DisplaceComponent] Cannot remap without original avatar root!");
            return;
        }

        int successCount = 0;
        int failCount = 0;

        for (int i = 0; i < component.componentsToMove.Count; i++)
        {
            var compRef = component.componentsToMove[i];
            if (compRef == null || compRef.component == null) continue;

            string componentPath = GetPathFromRoot(compRef.component.gameObject, originalAvatarRoot);
            if (!string.IsNullOrEmpty(componentPath))
            {
                GameObject clonedGameObject = FindByPath(cloneAvatarRoot, componentPath);
                if (clonedGameObject != null)
                {
                    System.Type componentType = compRef.component.GetType();
                    Component[] components = clonedGameObject.GetComponents(componentType);
                    Component[] originalComponents = compRef.component.gameObject.GetComponents(componentType);
                    int index = System.Array.IndexOf(originalComponents, compRef.component);

                    if (index >= 0 && index < components.Length)
                    {
                        compRef.component = components[index];
                        successCount++;
                    }
                    else
                    {
                        Debug.LogWarning($"[DisplaceComponent] ✗ Component index mismatch: {componentType.Name}");
                        failCount++;
                    }
                }
                else
                {
                    Debug.LogWarning($"[DisplaceComponent] ✗ Could not find GameObject at: {componentPath}");
                    failCount++;
                }
            }
        }

        // Only log summary to console, detailed to file
        if (failCount > 0)
        {
            Debug.LogWarning($"[DisplaceComponent] Remapped {successCount}/{component.componentsToMove.Count} components ({failCount} failed)");
            DisplaceComponentLogger.Log($"  ⚠ Component remapping: {successCount}/{component.componentsToMove.Count} successful, {failCount} failed");
        }
        else
        {
            DisplaceComponentLogger.Log($"  ✓ All {successCount} components remapped successfully");
        }
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

        // Start detailed file logging
        DisplaceComponentLogger.StartNewLog();

        Debug.Log("========================================");
        Debug.Log("[DisplaceComponent] BUILD STARTED - OnBuildRequested");
        Debug.Log($"[DisplaceComponent] Callback Order: {callbackOrder}");
        Debug.Log("========================================");

        DisplaceComponentLogger.Log("========================================");
        DisplaceComponentLogger.Log("BUILD STARTED - OnBuildRequested");
        DisplaceComponentLogger.Log($"Callback Order: {callbackOrder}");
        DisplaceComponentLogger.Log("========================================");

        try
        {
            isProcessing = true;
            componentBackups.Clear();
            processedComponents.Clear();

            // CRITICAL FIX: Find the avatar root being built (the clone)
            GameObject avatarRoot = FindAvatarRootBeingBuilt();
            if (avatarRoot == null)
            {
                Debug.LogError("[DisplaceComponent] Cannot find avatar root being built!");
                isProcessing = false;
                return false;
            }

            // ONLY find DisplaceComponents within THIS avatar hierarchy
            var displaceComponents = avatarRoot.GetComponentsInChildren<DisplaceComponent>(true)
                .Where(dc => dc != null && dc.gameObject != null && dc.enabled)
                .ToList();

            Debug.Log($"[DisplaceComponent] Processing avatar: {avatarRoot.name}");
            Debug.Log($"[DisplaceComponent] Found {displaceComponents.Count} component(s) in this avatar");

            DisplaceComponentLogger.Log($"Processing avatar: {avatarRoot.name}");
            DisplaceComponentLogger.Log($"Found {displaceComponents.Count} component(s) in this avatar");

            // Compact logging - summary only for console, detailed for file
            if (displaceComponents.Count > 0)
            {
                int prefabCount = displaceComponents.Count(dc => UnityEditor.PrefabUtility.IsPartOfPrefabInstance(dc.gameObject));
                int fillFieldsCount = displaceComponents.Count(dc => dc.operationMode == DisplaceComponent.OperationMode.FillFields);
                int displaceCount = displaceComponents.Count - fillFieldsCount;

                Debug.Log($"[DisplaceComponent] Summary: {prefabCount} prefabs, {fillFieldsCount} FillFields, {displaceCount} Displace");
                DisplaceComponentLogger.Log($"Summary: {prefabCount} prefabs, {fillFieldsCount} FillFields, {displaceCount} Displace");
                DisplaceComponentLogger.Log("");
                DisplaceComponentLogger.Log("Component Details:");

                // Write detailed list to file
                for (int i = 0; i < displaceComponents.Count; i++)
                {
                    var dc = displaceComponents[i];
                    bool isPrefab = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(dc.gameObject);
                    string prefabMark = isPrefab ? "📦" : "";
                    DisplaceComponentLogger.Log($"  [{i+1}] {prefabMark} {dc.gameObject.name}");
                    DisplaceComponentLogger.Log($"      Mode: {dc.operationMode}");
                    if (dc.operationMode == DisplaceComponent.OperationMode.FillFields)
                    {
                        DisplaceComponentLogger.Log($"      Field mappings: {dc.fieldMappings?.Count ?? 0}");
                    }
                    else
                    {
                        DisplaceComponentLogger.Log($"      Components to move: {dc.componentsToMove?.Count ?? 0}");
                    }
                }
                DisplaceComponentLogger.Log("");
            }

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
            DisplaceComponentLogger.Log("Starting displacement process...");
            DisplaceComponentLogger.Log("");

            int successCount = 0;
            int failCount = 0;
            foreach (var component in displaceComponents)
            {
                DisplaceComponentLogger.Log($"Processing: {component.gameObject.name}");

                ComponentBackup backup = CreateBackup(component);
                if (backup != null)
                {
                    componentBackups.Add(backup);
                    DisplaceComponentLogger.Log("  ✓ Backup created");

                    // CRITICAL FIX: Remap component references to the clone before performing displacement
                    if (component.operationMode == DisplaceComponent.OperationMode.FillFields)
                    {
                        DisplaceComponentLogger.Log("  Remapping field references to clone...");
                        RemapFieldMappingsToClone(component, avatarRoot);
                    }
                    else
                    {
                        DisplaceComponentLogger.Log("  Remapping component references to clone...");
                        RemapComponentsToMoveToClone(component, avatarRoot);
                    }

                    if (component.PerformBuildTimeDisplacement())
                    {
                        processedComponents.Add(component);
                        successCount++;
                        DisplaceComponentLogger.Log("  ✓ Displacement successful");
                    }
                    else
                    {
                        Debug.LogWarning($"[DisplaceComponent] ✗ Failed: {component.gameObject.name}");
                        DisplaceComponentLogger.Log("  ✗ Displacement FAILED");
                        failCount++;
                    }
                }
                else
                {
                    Debug.LogError($"[DisplaceComponent] ✗ Backup failed: {component.gameObject.name}");
                    DisplaceComponentLogger.Log("  ✗ Backup creation FAILED");
                    failCount++;
                }
                DisplaceComponentLogger.Log("");
            }

            // Show summary instead of per-component logs
            if (failCount > 0)
            {
                Debug.LogWarning($"[DisplaceComponent] Processed: {successCount} successful, {failCount} failed");
            }
            else
            {
                Debug.Log($"[DisplaceComponent] ✓ All {successCount} components processed successfully");
            }

            Debug.Log("========================================");
            Debug.Log($"[DisplaceComponent] DISPLACEMENT COMPLETED: {successCount}/{displaceComponents.Count}");
            Debug.Log($"[DisplaceComponent] Waiting for build to complete...");
            Debug.Log("========================================");

            DisplaceComponentLogger.Log("========================================");
            DisplaceComponentLogger.Log($"DISPLACEMENT COMPLETED: {successCount}/{displaceComponents.Count}");
            if (failCount > 0)
            {
                DisplaceComponentLogger.Log($"Failures: {failCount}");
            }
            DisplaceComponentLogger.Log("Waiting for build to complete...");
            DisplaceComponentLogger.Log("========================================");

            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError("========================================");
            Debug.LogError($"[DisplaceComponent] ERROR during displacement:");
            Debug.LogError($"{e.Message}");
            Debug.LogError($"{e.StackTrace}");
            Debug.LogError("========================================");

            DisplaceComponentLogger.Log("");
            DisplaceComponentLogger.Log("========================================");
            DisplaceComponentLogger.Log("ERROR during displacement:");
            DisplaceComponentLogger.Log($"Message: {e.Message}");
            DisplaceComponentLogger.Log($"Stack Trace: {e.StackTrace}");
            DisplaceComponentLogger.Log("========================================");

            EditorUtility.DisplayDialog(
                "Displace Component - Error",
                $"An error occurred during component displacement:\n\n{e.Message}\n\n" +
                "Check the Console for details.",
                "OK"
            );

            RestoreAllStates();
            DisplaceComponentLogger.CloseLog();
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

        DisplaceComponentLogger.Log("");
        DisplaceComponentLogger.Log("========================================");
        DisplaceComponentLogger.Log("BUILD COMPLETED - OnPostprocessAvatar");
        DisplaceComponentLogger.Log("SDK build finished, starting restoration...");
        DisplaceComponentLogger.Log("========================================");

        // Restore everything now that build is complete
        RestoreAllStates();

        // Close the log file
        DisplaceComponentLogger.CloseLog();
    }

    private static ComponentBackup CreateBackup(DisplaceComponent component)
    {
        try
        {
            ComponentBackup backup = new ComponentBackup();

            // ADD THIS LINE - CRITICAL!
            backup.avatarRoot = FindAvatarRootBeingBuilt();

            backup.targetGameObjectPath = GetPathFromRoot(component.gameObject, backup.avatarRoot);

            Component[] allComponents = component.gameObject.GetComponents<Component>();
            backup.componentIndex = System.Array.IndexOf(allComponents, component);

            bool isPrefab = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(component.gameObject);
            string prefabMark = isPrefab ? " 📦" : "";
            Debug.Log($"[DisplaceComponent]   Creating backup{prefabMark} (index: {backup.componentIndex})");
            Debug.Log($"[DisplaceComponent]   GameObject: {component.gameObject.name}");
            Debug.Log($"[DisplaceComponent]   GameObject Path: {backup.targetGameObjectPath}");

            // Store operation mode
            backup.operationMode = component.operationMode;
            Debug.Log($"[DisplaceComponent]   Operation Mode: {backup.operationMode}");

            // Store DisplaceComponent configuration
            if (component.linkFrom != null)
            {
                backup.linkFromPath = GetPathFromRoot(component.linkFrom, backup.avatarRoot);
            }
            backup.linkToBone = component.linkToBone;
            if (component.linkToManual != null)
            {
                backup.linkToManualPath = GetPathFromRoot(component.linkToManual, backup.avatarRoot);
            }
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

                            // CHANGED: Store path instead of direct reference
                            data.sourceGameObjectPath = GetPathFromRoot(compRef.component.gameObject, backup.avatarRoot);

                            // Store which component of this type (in case multiple)
                            Component[] compsOfType = compRef.component.gameObject.GetComponents(compRef.component.GetType());
                            data.componentIndex = System.Array.IndexOf(compsOfType, compRef.component);

                            backup.componentsToMoveData.Add(data);

                            Debug.Log($"[DisplaceComponent]     • {data.componentTypeName} at {data.sourceGameObjectPath}");
                        }
                    }
                }
            }
            else // FillFields mode
            {
                // Store field mappings data (FillFields mode)
                if (component.fieldMappings != null)
                {
                    Debug.Log($"[DisplaceComponent]   Backing up {component.fieldMappings.Count} field mapping(s) for FillFields mode");
                    int mappingIndex = 0;
                    foreach (var mapping in component.fieldMappings)
                    {
                        mappingIndex++;
                        Debug.Log($"[DisplaceComponent]     --- Field Mapping [{mappingIndex}/{component.fieldMappings.Count}] ---");

                        if (mapping == null)
                        {
                            Debug.LogWarning($"[DisplaceComponent]       ✗ Mapping is null, skipping");
                            continue;
                        }

                        if (mapping.targetComponent == null)
                        {
                            Debug.LogWarning($"[DisplaceComponent]       ✗ Target component is null, skipping");
                            continue;
                        }

                        if (string.IsNullOrEmpty(mapping.fieldPath))
                        {
                            Debug.LogWarning($"[DisplaceComponent]       ✗ Field path is empty, skipping");
                            continue;
                        }

                        FieldMappingBackup data = new FieldMappingBackup();

                        // CRITICAL CHANGES: Store paths instead of references
                        Debug.Log($"[DisplaceComponent]       Target Component: {mapping.targetComponent.GetType().Name}");
                        Debug.Log($"[DisplaceComponent]       On GameObject: {mapping.targetComponent.gameObject.name}");

                        if (mapping.sourceGameObject != null)
                        {
                            Debug.Log($"[DisplaceComponent]       Source GameObject: {mapping.sourceGameObject.name}");
                            data.sourceGameObjectPath = GetPathFromRoot(mapping.sourceGameObject, backup.avatarRoot);
                        }
                        else
                        {
                            Debug.Log($"[DisplaceComponent]       Source GameObject: <none>");
                        }

                        data.componentGameObjectPath = GetPathFromRoot(mapping.targetComponent.gameObject, backup.avatarRoot);
                        data.componentTypeFullName = mapping.targetComponent.GetType().AssemblyQualifiedName;

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
                        Debug.Log($"[DisplaceComponent]       Field: {mapping.fieldPath}");
                        Debug.Log($"[DisplaceComponent]       Fill Value: {fillInfo}");
                        Debug.Log($"[DisplaceComponent]       ✓ Backup stored");
                    }
                    Debug.Log($"[DisplaceComponent]   Total field mappings backed up: {backup.fieldMappingBackups.Count}");
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
        Debug.Log($"[DisplaceComponent] Total backups to restore: {componentBackups.Count}");

        int backupIndex = 0;
        foreach (var backup in componentBackups)
        {
            backupIndex++;
            Debug.Log($"[DisplaceComponent] ==================== Backup [{backupIndex}/{componentBackups.Count}] ====================");

            // CRITICAL FIX: Find GameObject by path in the CURRENT avatar (the clone)
            GameObject currentAvatarRoot = backup.avatarRoot;

            if (currentAvatarRoot == null)
            {
                Debug.LogError("[DisplaceComponent]   ✗ Avatar root is null, cannot restore!");
                failedCount++;
                continue;
            }

            Debug.Log($"[DisplaceComponent]   Avatar root: {currentAvatarRoot.name}");
            Debug.Log($"[DisplaceComponent]   Target path: {backup.targetGameObjectPath}");
            Debug.Log($"[DisplaceComponent]   Operation mode: {backup.operationMode}");

            GameObject targetGameObject = FindByPath(currentAvatarRoot, backup.targetGameObjectPath);
            if (targetGameObject == null)
            {
                Debug.LogError($"[DisplaceComponent]   ✗ Cannot find GameObject at path: {backup.targetGameObjectPath}");
                failedCount++;
                continue;
            }

            try
            {
                bool isPrefab = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(targetGameObject);
                string prefabMark = isPrefab ? " 📦" : "";
                Debug.Log($"[DisplaceComponent]   ✓ Found target: {targetGameObject.name}{prefabMark}");

                DisplaceComponent existingComponent = targetGameObject.GetComponent<DisplaceComponent>();

                if (existingComponent == null)
                {
                    Debug.Log($"[DisplaceComponent]     Re-adding component...");
                    existingComponent = targetGameObject.AddComponent<DisplaceComponent>();
                    displaceComponentsRestored++;
                }

                // Restore configuration
                existingComponent.operationMode = backup.operationMode;

                if (!string.IsNullOrEmpty(backup.linkFromPath))
                {
                    existingComponent.linkFrom = FindByPath(currentAvatarRoot, backup.linkFromPath);
                }

                existingComponent.linkToBone = backup.linkToBone;

                if (!string.IsNullOrEmpty(backup.linkToManualPath))
                {
                    existingComponent.linkToManual = FindByPath(currentAvatarRoot, backup.linkToManualPath);
                }
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
                        // Find GameObject by path
                        GameObject sourceGameObject = FindByPath(currentAvatarRoot, data.sourceGameObjectPath);
                        if (sourceGameObject != null)
                        {
                            System.Type componentType = System.Type.GetType(data.componentTypeFullName);
                            if (componentType != null)
                            {
                                Component[] compsOfType = sourceGameObject.GetComponents(componentType);
                                if (data.componentIndex < compsOfType.Length)
                                {
                                    Component comp = compsOfType[data.componentIndex];
                                    existingComponent.componentsToMove.Add(new DisplaceComponent.ComponentReference(comp));
                                    Debug.Log($"[DisplaceComponent]       ✓ {data.componentTypeName}");
                                }
                                else
                                {
                                    Debug.LogWarning($"[DisplaceComponent]       ✗ Component index out of range: {data.componentTypeName}");
                                }
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[DisplaceComponent]       ✗ GameObject not found at path: {data.sourceGameObjectPath}");
                        }
                    }

                    Debug.Log($"[DisplaceComponent]     Final component count: {existingComponent.componentsToMove.Count}");
                }
                else // FillFields mode
                {
                    // Restore field mappings list (FillFields mode)
                    existingComponent.fieldMappings.Clear();
                    Debug.Log($"[DisplaceComponent]     === FillFields Mode Restoration ===");
                    Debug.Log($"[DisplaceComponent]     Restoring {backup.fieldMappingBackups.Count} field mapping(s)");

                    int restoreIndex = 0;
                    foreach (var data in backup.fieldMappingBackups)
                    {
                        restoreIndex++;
                        Debug.Log($"[DisplaceComponent]     --- Restoring Field Mapping [{restoreIndex}/{backup.fieldMappingBackups.Count}] ---");
                        Debug.Log($"[DisplaceComponent]       Looking for component at path: {data.componentGameObjectPath}");

                        // CRITICAL FIX: Find GameObjects by path in the CURRENT avatar (the clone)
                        GameObject componentGameObject = FindByPath(currentAvatarRoot, data.componentGameObjectPath);
                        if (componentGameObject == null)
                        {
                            Debug.LogError($"[DisplaceComponent]       ✗ Cannot find GameObject at path: {data.componentGameObjectPath}");
                            continue;
                        }

                        bool compGoPrefab = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(componentGameObject);
                        string compGoPrefabMark = compGoPrefab ? " 📦" : "";
                        Debug.Log($"[DisplaceComponent]       ✓ Found GameObject: {componentGameObject.name}{compGoPrefabMark}");

                        // Find the specific component by type
                        Debug.Log($"[DisplaceComponent]       Looking for component type: {data.componentTypeFullName}");
                        System.Type componentType = System.Type.GetType(data.componentTypeFullName);
                        if (componentType == null)
                        {
                            Debug.LogError($"[DisplaceComponent]       ✗ Cannot find type: {data.componentTypeFullName}");
                            continue;
                        }

                        Component comp = componentGameObject.GetComponent(componentType);
                        if (comp != null)
                        {
                            Debug.Log($"[DisplaceComponent]       ✓ Found component: {comp.GetType().Name}");

                            DisplaceComponent.FieldMapping mapping = new DisplaceComponent.FieldMapping();

                            // Restore sourceGameObject if it had one
                            if (!string.IsNullOrEmpty(data.sourceGameObjectPath))
                            {
                                Debug.Log($"[DisplaceComponent]       Source GameObject path: {data.sourceGameObjectPath}");
                                mapping.sourceGameObject = FindByPath(currentAvatarRoot, data.sourceGameObjectPath);
                            }
                            else
                            {
                                Debug.Log($"[DisplaceComponent]       No source GameObject");
                            }

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
                            Debug.Log($"[DisplaceComponent]       Field: {data.fieldPath}");
                            Debug.Log($"[DisplaceComponent]       Fill Value: {fillInfo}");
                            Debug.Log($"[DisplaceComponent]       ✓ Field mapping restored successfully");
                        }
                        else
                        {
                            Debug.LogError($"[DisplaceComponent]       ✗ Component type {componentType.Name} not found on GameObject: {componentGameObject.name}");
                            Debug.LogError($"[DisplaceComponent]         Available components on {componentGameObject.name}:");
                            foreach (Component c in componentGameObject.GetComponents<Component>())
                            {
                                Debug.LogError($"[DisplaceComponent]           • {c.GetType().Name}");
                            }
                        }
                    }

                    Debug.Log($"[DisplaceComponent]     === FillFields Restoration Complete ===");
                    Debug.Log($"[DisplaceComponent]     Final mapping count: {existingComponent.fieldMappings.Count}");
                }
                
                // Restore component order
                if (backup.componentIndex >= 0)
                {
                    Component[] components = targetGameObject.GetComponents<Component>();
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

                EditorUtility.SetDirty(targetGameObject);

                if (!affectedObjects.Contains(targetGameObject))
                {
                    affectedObjects.Add(targetGameObject);
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