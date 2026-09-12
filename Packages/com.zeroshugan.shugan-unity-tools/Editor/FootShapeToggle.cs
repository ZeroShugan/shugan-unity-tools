using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    // ─── Foot-shape toggle (VRCFury) ───────────────────────────────────────────
    // Some bases only line up with their own foot bones when certain shape keys are set: Maya's
    // Basis is a high-heel shape, and "High heeled" = 100 is what brings back the flat foot the
    // armature was built for. AutoRig Feet rigs the toes on that shape, so while the feet system is
    // in use the avatar needs those shape keys set too. This builds a VRCFury Toggle that sets them,
    // on a single GameObject under the avatar root.
    //
    // The object has to stay the LAST child of the root. VRCFury gives every toggle its own FX
    // layer in hierarchy order, and when two toggles animate the same shape key the later layer
    // wins, so a toggle placed below this one could undo the fix.
    //
    // VRCFury is reached by REFLECTION, not by an assembly reference. An asmdef reference to
    // com.vrcfury.api would tie this whole package — every tool in it — to VRCFury being installed,
    // and most of what these tools do has nothing to do with VRCFury. With reflection the toggle is
    // simply unavailable when VRCFury is absent, and everything else carries on.
    //
    // Only VRCFury's public API surface is touched (com.vrcfury.api.FuryComponents → FuryToggle →
    // FuryActionSet). Its model types are internal, and the public API is the part VRCFury keeps
    // stable between versions. Every call is guarded: if a future version renames something, the run
    // reports that the toggle could not be built instead of throwing.
    public static class FootShapeToggle
    {
        public const string ObjectName = "ARF Foot Shape (keep last)";
        public const string MenuPath   = "Animation/Feet/Foot Shape";

        const string UndoName    = "AutoRig Feet: foot-shape toggle";
        const string ApiTypeName = "com.vrcfury.api.FuryComponents";

        // Resolved once per domain reload; installing or removing VRCFury causes one, so this never
        // goes stale.
        static bool _apiSearched;
        static Type _apiType;

        static Type ApiType
        {
            get
            {
                if (_apiSearched) return _apiType;
                _apiSearched = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(ApiTypeName, false);
                        if (t != null) { _apiType = t; break; }
                    }
                    catch { /* an assembly that cannot be inspected is simply not VRCFury */ }
                }
                return _apiType;
            }
        }

        public static bool VRCFuryAvailable { get { return ApiType != null; } }

        // Direct children only: the toggle is always created right under the root, and a deeper
        // match would be some other object that merely shares the name.
        public static Transform Find(GameObject avatarRoot)
        {
            if (avatarRoot == null) return null;
            var root = avatarRoot.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.name == ObjectName) return child;
            }
            return null;
        }

        public static bool IsLastChild(GameObject avatarRoot)
        {
            var t = Find(avatarRoot);
            return t != null && t.GetSiblingIndex() == avatarRoot.transform.childCount - 1;
        }

        public static void MoveToBottom(GameObject avatarRoot)
        {
            var t = Find(avatarRoot);
            if (t == null) return;
            Undo.RegisterChildrenOrderUndo(avatarRoot.transform, UndoName);
            t.SetAsLastSibling();
            MarkDirty(avatarRoot);
        }

        public static bool Remove(GameObject avatarRoot)
        {
            var t = Find(avatarRoot);
            if (t == null) return false;
            Undo.DestroyObjectImmediate(t.gameObject);
            MarkDirty(avatarRoot);
            return true;
        }

        // Builds the toggle, replacing any earlier one. Values use VRCFury's 0-100 scale.
        public static GameObject CreateOrUpdate(GameObject avatarRoot,
                                                IList<(string key, float value)> actions,
                                                out string error)
        {
            error = null;
            if (avatarRoot == null) { error = "No avatar selected."; return null; }
            if (actions == null || actions.Count == 0)
            {
                error = "There are no shape keys to put in the toggle.";
                return null;
            }

            var api = ApiType;
            if (api == null) { error = "VRCFury is not installed in this project."; return null; }

            var createToggle = api.GetMethod("CreateToggle", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(GameObject) }, null);
            if (createToggle == null)
            {
                error = "this VRCFury version has no FuryComponents.CreateToggle(GameObject).";
                return null;
            }

            Undo.SetCurrentGroupName(UndoName);
            int group = Undo.GetCurrentGroup();

            // Rebuilt rather than edited: VRCFury's model types are internal, so an existing toggle
            // cannot be read or changed from here. Destroying through Undo keeps a re-run reversible.
            var old = Find(avatarRoot);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

            var go = new GameObject(ObjectName);
            go.transform.SetParent(avatarRoot.transform, false);
            go.transform.SetAsLastSibling();

            try
            {
                object toggle = createToggle.Invoke(null, new object[] { go });
                if (toggle == null) throw new Exception("CreateToggle returned nothing.");

                Call(toggle, "SetMenuPath", new[] { typeof(string) }, new object[] { MenuPath });

                object set = Call(toggle, "GetActions", Type.EmptyTypes, new object[0]);
                if (set == null) throw new Exception("GetActions returned nothing.");

                // The third argument is the renderer, and null means every renderer carrying a shape
                // key of that name — which is what makes socks and shoes follow the body.
                var addBlendshape = set.GetType().GetMethod("AddBlendshape",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(float), typeof(Renderer) }, null);
                if (addBlendshape == null)
                    throw new Exception("this VRCFury version has no AddBlendshape(string, float, Renderer).");

                foreach (var a in actions)
                {
                    if (string.IsNullOrEmpty(a.key)) continue;
                    addBlendshape.Invoke(set, new object[] { a.key, Mathf.Clamp(a.value, 0f, 100f), null });
                }
            }
            catch (Exception e)
            {
                // Leave nothing half-built behind: an object holding an empty VRCFury component would
                // look like a working toggle and quietly do nothing.
                var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                error = inner.Message;
                Undo.DestroyObjectImmediate(go);
                Undo.CollapseUndoOperations(group);
                return null;
            }

            Undo.RegisterCreatedObjectUndo(go, UndoName);
            Undo.CollapseUndoOperations(group);
            MarkDirty(avatarRoot);
            return go;
        }

        static object Call(object target, string method, Type[] signature, object[] args)
        {
            var m = target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance,
                null, signature, null);
            if (m == null) throw new Exception("this VRCFury version has no " + method + "().");
            return m.Invoke(target, args);
        }

        static void MarkDirty(GameObject avatarRoot)
        {
            if (avatarRoot != null && avatarRoot.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(avatarRoot.scene);
        }
    }
}
