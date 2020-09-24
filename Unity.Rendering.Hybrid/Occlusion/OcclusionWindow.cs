#if UNITY_EDITOR && ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System.Linq;
using UnityEditor;
using UnityEngine;
using Unity.Rendering.Occlusion;
using Unity.Entities;

public class OcclusionWindow : EditorWindow
{
    enum VisibilityFilter
    {
        None,
        Occluders,
        Occludees,
    }

    private VisibilityFilter _visibilityFilter = VisibilityFilter.None;
    bool occlusionEnabled;

    // Add menu item named "My Window" to the Window menu
    public static void ShowWindow()
    {
        //Show existing window instance. If one doesn't exist, make one.
        EditorWindow.GetWindow(typeof(OcclusionWindow));
    }

    void OnGUI()
    {
        var settingsSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();

        GUILayout.Space(5);
        GUILayout.Label ("Options", EditorStyles.boldLabel);

        settingsSystem.OcclusionEnabled = GUILayout.Toggle(settingsSystem.OcclusionEnabled, "Enable Occlusion");
        settingsSystem.OcclusionParallelEnabled = GUILayout.Toggle(settingsSystem.OcclusionParallelEnabled, "Enable Parallel Work");

#if UNITY_MOC_NATIVE_AVAILABLE
        GUILayout.Label ("Occlusion Mode:");
        if (GUILayout.Toggle(OcclusionSettingsSystem.MOCOcclusionMode.Intrinsic == settingsSystem.MocOcclusionMode, "MOC Burst Intrinsics"))
        {
            settingsSystem.MocOcclusionMode = OcclusionSettingsSystem.MOCOcclusionMode.Intrinsic;
        }

        if (GUILayout.Toggle(OcclusionSettingsSystem.MOCOcclusionMode.Native == settingsSystem.MocOcclusionMode, "MOC Native"))
        {
            settingsSystem.MocOcclusionMode = OcclusionSettingsSystem.MOCOcclusionMode.Native;
        }
#endif

        GUILayout.Space(20);
        GUILayout.Label ("Tools", EditorStyles.boldLabel);
        GUILayout.Space(5);

        VisibilityFilter currentVis = _visibilityFilter;
        if (GUILayout.Toggle(_visibilityFilter == VisibilityFilter.Occluders, "Show Only Ocludders"))
        {
            var list = FindObjectsOfType<Occluder>().Select(x => x.gameObject).ToArray();
            ScriptableSingleton<SceneVisibilityManager>.instance.Isolate(list, true);
            _visibilityFilter = VisibilityFilter.Occluders;
        }
        else
        {
            if (_visibilityFilter == VisibilityFilter.Occluders)
            {
                ScriptableSingleton<SceneVisibilityManager>.instance.ExitIsolation();
                _visibilityFilter = VisibilityFilter.None;
            }
        }

        if (GUILayout.Toggle(_visibilityFilter == VisibilityFilter.Occludees, "Show Only Occludees"))
        {
            var list = FindObjectsOfType<Occludee>().Select(x => x.gameObject).ToArray();
            ScriptableSingleton<SceneVisibilityManager>.instance.Isolate(list, true);
            _visibilityFilter = VisibilityFilter.Occludees;
        }
        else
        {
            if (_visibilityFilter == VisibilityFilter.Occludees)
            {
                ScriptableSingleton<SceneVisibilityManager>.instance.ExitIsolation();
                _visibilityFilter = VisibilityFilter.None;
            }
        }

        if (GUILayout.Button("Select Ocludders"))
        {
            Selection.objects = FindObjectsOfType<Occluder>().Select(x => x.gameObject).ToArray();
        }

        if (GUILayout.Button("Select Ocluddees"))
        {
            Selection.objects = FindObjectsOfType<Occludee>().Select(x => x.gameObject).ToArray();
        }


        if (GUILayout.Button("Add Occluder Volumes to Selected"))
        {
            foreach (var selected in Selection.gameObjects)
            {
                Bounds bounds = new Bounds();
                foreach (Transform transform in selected.transform)
                {
                    if (transform.gameObject.GetComponent<MeshFilter>() != null)
                    {
                        Mesh mesh = transform.gameObject.GetComponent<MeshFilter>().mesh;
                        bounds.Encapsulate(mesh.bounds.min);
                        bounds.Encapsulate(mesh.bounds.max);
                    }
                }
                Occluder occluder = selected.AddComponent<Occluder>();
                occluder.Type = Occluder.OccluderType.Volume;
                OccluderVolume vol = new OccluderVolume();
                vol.CreateCube();
                occluder.Mesh = vol.CalculateMesh();

                occluder.relativePosition = bounds.center;
                Vector3 boundsScale;
                boundsScale.x = (bounds.max.x - bounds.min.x);
                boundsScale.y = (bounds.max.y - bounds.min.y);
                boundsScale.z = (bounds.max.z - bounds.min.z);
                occluder.relativeScale = boundsScale;
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(selected.gameObject.scene);

            }
        }

        if (GUILayout.Button("Add Occludees to Selected"))
        {
            foreach (var go in Selection.gameObjects)
            {
                if (go.GetComponent<MeshRenderer>())
                {
                    if (!go.TryGetComponent<Occludee>(out var occludee))
                    {
                        go.AddComponent<Occludee>();
                        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
                    }
                }
            }
        }

        if (GUILayout.Button("Add Occludees to All Mesh Renderers"))
        {
            foreach (var meshRender in FindObjectsOfType<MeshRenderer>())
            {
                var go = meshRender.gameObject;
                if (go.GetComponent<MeshRenderer>())
                {
                    if (!go.TryGetComponent<Occludee>(out var occludee))
                    {
                        go.AddComponent<Occludee>();
                    }
                }
            }
        }

        if (GUILayout.Button("Remove Occluders from Selected"))
        {
            foreach (var go in Selection.gameObjects)
            {
                foreach (var o in go.GetComponents<Occluder>())
                {
                    DestroyImmediate(o);
                }
            }
        }

        if (GUILayout.Button("Remove Occludees from Selected"))
        {
            foreach (var go in Selection.gameObjects)
            {
                foreach (var o in go.GetComponents<Occludee>())
                {
                    DestroyImmediate(o);
                }
            }
        }



        GUILayout.Space(20);
        GUILayout.Label("Debug Modes", EditorStyles.boldLabel);
        GUILayout.Space(5);

        var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
        if (GUILayout.Toggle(OcclusionDebugRenderSystem.DebugRenderMode.None == debugSystem.m_DebugRenderMode, "Turn off Debug"))
        {
            debugSystem.m_DebugRenderMode = OcclusionDebugRenderSystem.DebugRenderMode.None;
        }

        if (GUILayout.Toggle(OcclusionDebugRenderSystem.DebugRenderMode.Depth == debugSystem.m_DebugRenderMode, "Show Depth Buffer"))
        {
            debugSystem.m_DebugRenderMode = OcclusionDebugRenderSystem.DebugRenderMode.Depth;
        }

        if (GUILayout.Toggle(OcclusionDebugRenderSystem.DebugRenderMode.Test == debugSystem.m_DebugRenderMode, "Show Depth Test"))
        {
            debugSystem.m_DebugRenderMode = OcclusionDebugRenderSystem.DebugRenderMode.Test;
        }

        if(OcclusionDebugRenderSystem.DebugRenderMode.Depth == debugSystem.m_DebugRenderMode || OcclusionDebugRenderSystem.DebugRenderMode.Test == debugSystem.m_DebugRenderMode)
        {
            debugSystem.WantedOcclusionDraw = EditorGUILayout.IntSlider(debugSystem.WantedOcclusionDraw, 1, debugSystem.TotalOcclusionDrawPerFrame);            
        }


        if (GUILayout.Toggle(OcclusionDebugRenderSystem.DebugRenderMode.Mesh == debugSystem.m_DebugRenderMode, "Show Occluder Meshes"))
        {
            debugSystem.m_DebugRenderMode = OcclusionDebugRenderSystem.DebugRenderMode.Mesh;
        }

        if (GUILayout.Toggle(OcclusionDebugRenderSystem.DebugRenderMode.Bounds == debugSystem.m_DebugRenderMode, "Show Occludee Bounds"))
        {
            debugSystem.m_DebugRenderMode = OcclusionDebugRenderSystem.DebugRenderMode.Bounds;
        }


        settingsSystem.DisplayOccluded = GUILayout.Toggle(settingsSystem.DisplayOccluded, "Display Occluded");
    }
}
#endif
