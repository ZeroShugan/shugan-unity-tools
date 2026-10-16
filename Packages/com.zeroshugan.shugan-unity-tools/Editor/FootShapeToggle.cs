// Copyright (c) 2026 Shugan. All rights reserved.
// Part of Shugan Unity Tools. Use is subject to LICENSE.md in the package root.
// No redistribution or resale, modified or not. Not to be used to train AI models, nor given
// to an AI system to reproduce, rewrite or port it.

using System;
using System.Collections.Generic;
using System.Linq;
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
        public const string MenuPath   = "Animation/Feet/Repair Clipping & Deform/Fix Toes Deform";
        // Shared with the FX feet prefabs, so the fix follows whether the feet system (gesture or Index
        // controller) is in use.
        public const string GlobalParameter = "Gesture_Feet_OR_IndexControllerBOOL";

        // Foot Shrink: one toggle per shrink key that works on the feet (the creator's key that flattens the skin
        // under shoes or socks), each in its own menu entry. They sit just above the Foot Shape toggle.
        public const string ShrinkObjectPrefix = "ARF Foot Shrink ";
        public const string ShrinkMenuPrefix   = "Animation/Feet/Repair Clipping & Deform/Shrink ";
        public static string ShrinkObjectName(string key) => ShrinkObjectNameFor(ShrinkLabel(key));
        public static string ShrinkMenuPath(string key)   => ShrinkMenuPathFor(ShrinkLabel(key));
        public static string ShrinkObjectNameFor(string label) => ShrinkObjectPrefix + label + " (keep last)";
        public static string ShrinkMenuPathFor(string label)   => ShrinkMenuPrefix + label;

        /// <summary>One menu name per key, all different. "ARF Shrink Foot" wants to be called "Foot", and so
        /// does a creator key actually named "Foot" — two toggles on one object would lose one of the keys, so
        /// the second one keeps its own name.</summary>
        public static List<(string key, string label)> ShrinkLabels(IEnumerable<string> keys)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<(string, string)>();
            foreach (string key in keys)
            {
                string label = ShrinkLabel(key);
                if (!used.Add(label))
                {
                    label = key;
                    for (int i = 2; !used.Add(label); i++) label = key + " " + i;
                }
                list.Add((key, label));
            }
            return list;
        }

        /// <summary>What a shrink key is called in the menu. The keys AutoRig Feet builds are already named
        /// "ARF Shrink Toes", and "Foot Shrink ARF Shrink Toes" reads badly, so that part is dropped.</summary>
        public static string ShrinkLabel(string key) =>
            key != null && key.StartsWith(FootShapeFit.OwnShrinkPrefix, StringComparison.Ordinal)
                ? key.Substring(FootShapeFit.OwnShrinkPrefix.Length) : key;
        static bool IsShrinkObject(string name) => name.StartsWith(ShrinkObjectPrefix, StringComparison.Ordinal);

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
        public static Transform Find(GameObject avatarRoot) => Find(avatarRoot, ObjectName);

        public static Transform Find(GameObject avatarRoot, string objectName)
        {
            if (avatarRoot == null) return null;
            var root = avatarRoot.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.name == objectName) return child;
            }
            return null;
        }

        /// <summary>The Foot Shrink toggle objects directly under the avatar.</summary>
        public static List<Transform> FindShrink(GameObject avatarRoot)
        {
            var list = new List<Transform>();
            if (avatarRoot == null) return list;
            foreach (Transform child in avatarRoot.transform)
                if (IsShrinkObject(child.name)) list.Add(child);
            return list;
        }

        /// <summary>Every AutoRig Feet toggle object under the avatar (Foot Shrink ones, then Foot Shape).</summary>
        public static List<Transform> FindAll(GameObject avatarRoot)
        {
            var list = FindShrink(avatarRoot);
            var shape = Find(avatarRoot);
            if (shape != null) list.Add(shape);
            return list;
        }

        /// <summary>True when the AutoRig Feet toggles are the last objects under the avatar, Foot Shape the very
        /// last. VRCFury gives each toggle its own layer in hierarchy order, and a later one animating the same shape
        /// key wins.</summary>
        public static bool IsLastChild(GameObject avatarRoot)
        {
            var all = FindAll(avatarRoot);
            if (all.Count == 0) return false;
            int count = avatarRoot.transform.childCount;
            var shape = Find(avatarRoot);
            if (shape != null && shape.GetSiblingIndex() != count - 1) return false;
            return all.All(t => t.GetSiblingIndex() >= count - all.Count);
        }

        public static void MoveToBottom(GameObject avatarRoot)
        {
            var all = FindAll(avatarRoot);
            if (all.Count == 0) return;
            Undo.RegisterChildrenOrderUndo(avatarRoot.transform, UndoName);
            foreach (var t in all) t.SetAsLastSibling();      // shrink toggles first, Foot Shape last
            MarkDirty(avatarRoot);
        }

        public static bool Remove(GameObject avatarRoot) => Remove(avatarRoot, ObjectName);

        public static bool Remove(GameObject avatarRoot, string objectName)
        {
            var t = Find(avatarRoot, objectName);
            if (t == null) return false;
            Undo.DestroyObjectImmediate(t.gameObject);
            MarkDirty(avatarRoot);
            return true;
        }

        /// <summary>Remove every Foot Shrink toggle object. Returns how many.</summary>
        public static int RemoveAllShrink(GameObject avatarRoot)
        {
            var list = FindShrink(avatarRoot);
            foreach (var t in list) Undo.DestroyObjectImmediate(t.gameObject);
            if (list.Count > 0) MarkDirty(avatarRoot);
            return list.Count;
        }

        // Builds the toggle, replacing any earlier one. Values use VRCFury's 0-100 scale.
        public static GameObject CreateOrUpdate(GameObject avatarRoot,
                                                IList<(string key, float value)> actions,
                                                out string error)
            => CreateOrUpdate(avatarRoot, ObjectName, MenuPath, actions, out error, GlobalParameter);

        public static GameObject CreateOrUpdate(GameObject avatarRoot, string objectName, string menuPath,
                                                IList<(string key, float value)> actions,
                                                out string error, string globalParameter = null)
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
            var old = Find(avatarRoot, objectName);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

            var go = new GameObject(objectName);
            go.transform.SetParent(avatarRoot.transform, false);
            go.transform.SetAsLastSibling();

            try
            {
                object toggle = createToggle.Invoke(null, new object[] { go });
                if (toggle == null) throw new Exception("CreateToggle returned nothing.");

                Call(toggle, "SetMenuPath", new[] { typeof(string) }, new object[] { menuPath });
                if (!string.IsNullOrEmpty(globalParameter))
                    Call(toggle, "SetGlobalParameter", new[] { typeof(string) }, new object[] { globalParameter });

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
