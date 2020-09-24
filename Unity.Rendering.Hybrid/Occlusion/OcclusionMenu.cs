#if UNITY_EDITOR && ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using UnityEditor;
using UnityEngine;
using Unity.Rendering.Occlusion;
using System.Collections.Generic;
using System;
using System.Linq;
using Unity.Entities;
using Object = UnityEngine.Object;
using UnityEditor.SceneManagement;

namespace Unity.Rendering.Occlusion
{
    static class OcclusionCommands
    {
        const string kOcclusionMenu = "Occlusion/";

        const string kOcclusionToolsSubMenu = "Tools/";

        const string kOcclusionDebugSubMenu = kOcclusionMenu + "Debug/";

        const string kDebugNone = kOcclusionDebugSubMenu + "None";
        const string kDebugDepth = kOcclusionDebugSubMenu + "Depth buffer";
        const string kDebugShowMeshes = kOcclusionDebugSubMenu + "Show occluder meshes";
        const string kDebugShowBounds = kOcclusionDebugSubMenu + "Show occludee bounds";
        const string kDebugShowTest = kOcclusionDebugSubMenu + "Show depth test";

        const string kOcclusionEnable = kOcclusionMenu + "Enable";
        const string kOcclusionDisplayOccluded = "Occlusion/DisplayOccluded";

#if UNITY_MOC_NATIVE_AVAILABLE
        const string kOcclusionModeSubMenu = kOcclusionMenu + "Occlusion Mode/";
        const string kOcclusionModeBurstIntrinsics = kOcclusionModeSubMenu + "MOC Burst Intrinsics";
        const string kOcclusionModeNative = kOcclusionModeSubMenu + "MOC Native";
#endif

        const string kOcclusionParallel = kOcclusionMenu + "Parallel Rasterization";

        [MenuItem(kDebugNone, false)]
        static void ToggleDebugModeNone()
        {
            var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
            debugSystem.m_DebugRenderMode = OcclusionDebugRenderSystem.DebugRenderMode.None;
        }

        [MenuItem(kDebugDepth, false)]
        static void ToggleDebugModeDepth()
        {
            var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
            debugSystem.m_DebugRenderMode = OcclusionDebugRenderSystem.DebugRenderMode.Depth;
        }

        [MenuItem(kDebugShowMeshes, false)]
        static void ToggleDebugModeShowMeshes()
        {
            var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
            debugSystem.m_DebugRenderMode = OcclusionDebugRenderSystem.DebugRenderMode.Mesh;
        }

        [MenuItem(kDebugShowBounds, false)]
        static void ToggleDebugModeShowBounds()
        {
            var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
            debugSystem.m_DebugRenderMode = OcclusionDebugRenderSystem.DebugRenderMode.Bounds;
        }

        [MenuItem(kDebugShowTest, false)]
        static void ToggleDebugModeShowTest()
        {
            var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
            debugSystem.m_DebugRenderMode = OcclusionDebugRenderSystem.DebugRenderMode.Test;
        }

        [MenuItem(kDebugNone, true)]
        static bool ValidateDebugModeNone()
        {
            var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
            Menu.SetChecked(kDebugNone, debugSystem.m_DebugRenderMode == OcclusionDebugRenderSystem.DebugRenderMode.None);
            return true;
        }

        [MenuItem(kDebugDepth, true)]
        static bool ValidateDebugModeDepth()
        {
            var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
            Menu.SetChecked(kDebugDepth, debugSystem.m_DebugRenderMode == OcclusionDebugRenderSystem.DebugRenderMode.Depth);
            return true;
        }

        [MenuItem(kDebugShowMeshes, true)]
        static bool ValidateDebugModeShowMeshes()
        {
            var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
            Menu.SetChecked(kDebugShowMeshes, debugSystem.m_DebugRenderMode == OcclusionDebugRenderSystem.DebugRenderMode.Mesh);
            return true;
        }

        [MenuItem(kDebugShowBounds, true)]
        static bool ValidateDebugModeShowBounds()
        {
            var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
            Menu.SetChecked(kDebugShowBounds, debugSystem.m_DebugRenderMode == OcclusionDebugRenderSystem.DebugRenderMode.Bounds);
            return true;
        }

        [MenuItem(kDebugShowTest, true)]
        static bool ValidateDebugModeShowTest()
        {
            var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
            Menu.SetChecked(kDebugShowTest, debugSystem.m_DebugRenderMode == OcclusionDebugRenderSystem.DebugRenderMode.Test);
            return true;
        }

        [MenuItem(kOcclusionEnable, false)]
        static void ToggleOcclusionEnable()
        {
            var settingsSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();
            settingsSystem.OcclusionEnabled = !settingsSystem.OcclusionEnabled;
        }

        [MenuItem(kOcclusionEnable, true)]
        static bool ValidateOcclusionEnable()
        {
            var settingsSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();
            Menu.SetChecked(kOcclusionEnable, settingsSystem.OcclusionEnabled);
            return true;
        }

        [MenuItem(kOcclusionDisplayOccluded, false)]
        static void ToggleDisplayOccluded()
        {
            var settingsSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();
            settingsSystem.DisplayOccluded = !settingsSystem.DisplayOccluded;
        }

        [MenuItem(kOcclusionDisplayOccluded, true)]
        static bool ValidatekDisplayOccluded()
        {
            var settingsSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();

            bool menuEnabled = true;
            if (settingsSystem.OcclusionEnabled == false)
            {
                settingsSystem.DisplayOccluded = false;
                menuEnabled = false;
            }

            Menu.SetChecked(kOcclusionDisplayOccluded, settingsSystem.DisplayOccluded);
            return menuEnabled;
        }

#if UNITY_MOC_NATIVE_AVAILABLE
        [MenuItem(kOcclusionModeBurstIntrinsics, false)]
        static void ToggleOcclusionEnableBurstIntrinsics()
        {
            var settingsSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();
            settingsSystem.MocOcclusionMode = OcclusionSettingsSystem.MOCOcclusionMode.Intrinsic;
        }

        [MenuItem(kOcclusionModeBurstIntrinsics, true)]
        static bool ValidateOcclusionEnableBurstIntrinsics()
        {
            var settingsSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();
            Menu.SetChecked(kOcclusionModeBurstIntrinsics, settingsSystem.MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Intrinsic);
            return true;
        }

        [MenuItem(kOcclusionModeNative, false)]
        static void ToggleOcclusionEnableNative()
        {
            var settingsSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();
            settingsSystem.MocOcclusionMode = OcclusionSettingsSystem.MOCOcclusionMode.Native;
        }

        [MenuItem(kOcclusionModeNative, true)]
        static bool ValidateOcclusionEnableNative()
        {
            var settingsSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();
            Menu.SetChecked(kOcclusionModeNative, settingsSystem.MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Native);
            return true;
        }
#endif



        [MenuItem(kOcclusionParallel, false)]
        static void ToggleOcclusionParallel()
        {
            var settingsSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();
            settingsSystem.OcclusionParallelEnabled = !settingsSystem.OcclusionParallelEnabled;
        }

        [MenuItem(kOcclusionParallel, true)]
        static bool ValidateOcclusionParallel()
        {
            var settingsSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();
            Menu.SetChecked(kOcclusionParallel, settingsSystem.OcclusionParallelEnabled);
            return true;
        }

        static void AddComponentIfNeeded<T>(this MeshRenderer meshRenderer) where T : MonoBehaviour
        {
            var gameObject = meshRenderer.gameObject;
            if (!gameObject.TryGetComponent<T>(out var occluder))
            {
                occluder = gameObject.AddComponent<T>();
                occluder.enabled = true;
            }
        }

        static void DestroyComponentIfNeeded<T>(this MeshRenderer meshRenderer) where T : MonoBehaviour
        {
            var gameObject = meshRenderer.gameObject;
            if (gameObject.TryGetComponent<T>(out var occluder))
                Object.DestroyImmediate(occluder);
        }

        [MenuItem(kOcclusionMenu + kOcclusionToolsSubMenu + "Add occlusion components to all open scenes and objects")]
        static void AddAllOcclusionComponents()
        {
            ForEachRenderer((meshRenderer) =>
            {
                meshRenderer.AddComponentIfNeeded<Occluder>();
                meshRenderer.AddComponentIfNeeded<Occludee>();
            }, OccluderEditMode.AllObjects);
        }

        [MenuItem(kOcclusionMenu + kOcclusionToolsSubMenu + "Remove occlusion components from all open scenes and objects")]
        static void RemoveAllOcclusionComponents()
        {
            ForEachRenderer((meshRenderer) =>
            {
                meshRenderer.DestroyComponentIfNeeded<Occluder>();
                meshRenderer.DestroyComponentIfNeeded<Occludee>();
            }, OccluderEditMode.AllObjects);
        }

        [MenuItem(kOcclusionMenu + kOcclusionToolsSubMenu + "Add occlusion components to selected")]
        static void AddOcclusionComponentsToSelected()
        {
            ForEachRenderer((meshRenderer) =>
            {
                meshRenderer.AddComponentIfNeeded<Occluder>();
                meshRenderer.AddComponentIfNeeded<Occludee>();
            }, OccluderEditMode.SelectedObjects);
        }

        [MenuItem(kOcclusionMenu + kOcclusionToolsSubMenu + "Remove occlusion components from selected")]
        static void RemoveOcclusionComponentsFromSelected()
        {
            ForEachRenderer((meshRenderer) =>
            {
                meshRenderer.DestroyComponentIfNeeded<Occluder>();
                meshRenderer.DestroyComponentIfNeeded<Occludee>();
            }, OccluderEditMode.SelectedObjects);
        }

        [MenuItem(kOcclusionMenu + kOcclusionToolsSubMenu + "Add occluder component to selected")]
        static void AddOccluderComponentToSelected()
        {
            ForEachRenderer((meshRenderer) =>
            {
                meshRenderer.AddComponentIfNeeded<Occluder>();
            }, OccluderEditMode.SelectedObjects);
        }

        [MenuItem(kOcclusionMenu + kOcclusionToolsSubMenu + "Add occluder component to all open scenes and objects")]
        static void AddOccluderComponentToAll()
                {
            ForEachRenderer((meshRenderer) =>
            {
                meshRenderer.AddComponentIfNeeded<Occluder>();
            }, OccluderEditMode.AllObjects);
        }


        [MenuItem(kOcclusionMenu + kOcclusionToolsSubMenu + "Add occludee component to selected")]
        static void AddOccludeeComponentToSelected()
        {
            ForEachRenderer((meshRenderer) =>
            {
                meshRenderer.AddComponentIfNeeded<Occludee>();
            }, OccluderEditMode.SelectedObjects);
        }

        [MenuItem(kOcclusionMenu + kOcclusionToolsSubMenu + "Add occludee component to all open scenes and objects")]
        static void AddOccludeeComponentToAll()
        {
            ForEachRenderer((meshRenderer) =>
            {
                meshRenderer.AddComponentIfNeeded<Occludee>();
            }, OccluderEditMode.AllObjects);
        }

        [MenuItem(kOcclusionMenu + "Occlusion Window")]
        static void OpenOcclusionWindow()
        {
            OcclusionWindow.ShowWindow();
        }

        enum OccluderEditMode
        {
            AllObjects,
            SelectedObjects,
        }

        static void ForEachRenderer(Action<MeshRenderer> action, OccluderEditMode mode)
        {
            var renderers = mode == OccluderEditMode.AllObjects
                ? Object.FindObjectsOfType<MeshRenderer>()
                : Selection.gameObjects.SelectMany(x => x.GetComponents<MeshRenderer>());
            renderers = renderers.Distinct();

            foreach (var renderer in renderers)
            {
                action(renderer);
                EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
            }
        }
    }
}
#endif

