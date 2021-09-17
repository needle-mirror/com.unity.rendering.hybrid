// #define DEBUG_LOG_REFLECTION_TRIGGERED_RECREATE

using System.Collections.Generic;
using Unity.Entities;
using Unity.Rendering;
#if UNITY_2020_1_OR_NEWER
// This API only exists since 2020.1
using Unity.Rendering.HybridV2;
#endif
using UnityEngine;


#if UNITY_EDITOR

namespace Unity.Rendering
{
    /// <summary>
    /// Renders all Entities containing both RenderMesh and LocalToWorld components.
    /// </summary>
    [ExecuteAlways]
    //@TODO: Necessary due to empty component group. When Component group and archetype chunks are unified this should be removed
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(StructuralChangePresentationSystemGroup))]
    public partial class TrackShaderReflectionChangesSystem : SystemBase
    {
        private uint m_PreviousDOTSReflectionVersionNumber = 0;
        private bool m_IsFirstFrame;
        private EntityQuery m_HybridRenderedQuery;
        private bool m_HasReflectionChanged = false;

        protected override void OnCreate()
        {
            m_HybridRenderedQuery = GetEntityQuery(HybridUtils.GetHybridRenderedQueryDesc());

            m_IsFirstFrame = true;
        }

        protected override void OnUpdate()
        {
            if (m_IsFirstFrame)
            {
#if UNITY_2020_1_OR_NEWER
                m_PreviousDOTSReflectionVersionNumber = HybridV2ShaderReflection.GetDOTSReflectionVersionNumber();
#endif
                m_IsFirstFrame = false;
            }

#if UNITY_2020_1_OR_NEWER
            uint reflectionVersionNumber = HybridV2ShaderReflection.GetDOTSReflectionVersionNumber();
            m_HasReflectionChanged = reflectionVersionNumber != m_PreviousDOTSReflectionVersionNumber;
#else
            uint reflectionVersionNumber = 0;
            m_HasReflectionChanged = false;
#endif

            if (HybridEditorTools.DebugSettings.RecreateAllBatches ||
                HasReflectionChanged)
            {
                EntityManager.RemoveChunkComponentData<HybridChunkInfo>(m_HybridRenderedQuery);

                Debug.Assert(m_HybridRenderedQuery.CalculateEntityCount() == 0,
                    "Expected amount of renderable entities to be zero after deleting all HybridChunkInfo components");

                if (HybridEditorTools.DebugSettings.RecreateAllBatches)
                {
                    Debug.Log("Recreate all batches requested, recreating hybrid batches");
                }
                else
                {
#if DEBUG_LOG_REFLECTION_TRIGGERED_RECREATE
                    Debug.Log("New shader reflection info detected, recreating hybrid batches");
#endif
                }

                m_PreviousDOTSReflectionVersionNumber = reflectionVersionNumber;
            }
        }

        public bool HasReflectionChanged => m_HasReflectionChanged;
    }
}

#endif // UNITY_EDITOR
