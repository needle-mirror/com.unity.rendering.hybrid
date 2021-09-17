#if true// USE_BATCH_RENDERER_GROUP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.Rendering
{
#if UNITY_EDITOR
    [ExecuteAlways]
    [ExecuteInEditMode]
    public class CullingStatsDrawer : MonoBehaviour
    {
        private bool m_Enabled = true;//false;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F4))
            {
                m_Enabled = !m_Enabled;
            }
        }

        private unsafe void OnGUI()
        {
            if (m_Enabled)
            {
                var sys = World.DefaultGameObjectInjectionWorld.GetExistingSystem<HybridRendererSystem>();

                var stats = sys.ComputeCullingStats();

                GUILayout.BeginArea(new Rect { x = 10, y = 10, width = 1024, height = 200 }, "Culling Stats", GUI.skin.window);

                GUILayout.Label("Culling stats:");
                GUILayout.Label($"  Chunks: Total={stats.Stats[CullingStats.kChunkTotal]} AnyLOD={stats.Stats[CullingStats.kChunkCountAnyLod]} FullIn={stats.Stats[CullingStats.kChunkCountFullyIn]} w/Instance Culling={stats.Stats[CullingStats.kChunkCountInstancesProcessed]}");
                GUILayout.Label($"  Instances tests: {stats.Stats[CullingStats.kInstanceTests]}");
                GUILayout.Label($"  Select LOD: Total={stats.Stats[CullingStats.kLodTotal]} No Requirements={stats.Stats[CullingStats.kLodNoRequirements]} Chunks Tested={stats.Stats[CullingStats.kLodChunksTested]} Changed={stats.Stats[CullingStats.kLodChanged]}");
                GUILayout.Label($"  Root LODs selected: {stats.Stats[CullingStats.kCountRootLodsSelected]} Failed: {stats.Stats[CullingStats.kCountRootLodsFailed]}");
                GUILayout.Label($"  Camera Move Distance: {stats.CameraMoveDistance} meters");
#if ENABLE_UNITY_OCCLUSION && UNITY_2020_1_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
                GUILayout.Label($"  Occlusion Culled: {stats.Stats[CullingStats.kCountOcclusionCulled]} / {stats.Stats[CullingStats.kCountOcclusionInput]}");
#endif
                GUILayout.EndArea();
            }
        }
    }
#endif
}
#endif
