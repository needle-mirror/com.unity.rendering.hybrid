#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using Unity.Entities;
using Unity.Jobs;
using UnityEngine;

namespace Unity.Rendering.Occlusion
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [ExecuteAlways]
    [AlwaysUpdateSystem]
    public class OcclusionSettingsSystem : JobComponentSystem
    {
        public bool OcclusionEnabled = true;
#if UNITY_EDITOR
        public bool DisplayOccluded = false;
#endif

        public bool OcclusionParallelEnabled = true;

        public enum MOCOcclusionMode
        {
            Intrinsic = 0,
#if UNITY_MOC_NATIVE_AVAILABLE
            Native = 1,
#endif
        }

#if UNITY_MOC_NATIVE_AVAILABLE
        private MOCOcclusionMode m_MocOcclusionMode = MOCOcclusionMode.Intrinsic;
#endif

        public MOCOcclusionMode MocOcclusionMode
        {
            get
            {
#if UNITY_MOC_NATIVE_AVAILABLE
                    return m_MocOcclusionMode;
#else
                    return MOCOcclusionMode.Intrinsic;
#endif
            }
#if UNITY_MOC_NATIVE_AVAILABLE
            set
            {
                m_MocOcclusionMode = value;
            }
#endif

        }


        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            inputDeps.Complete();

            return new JobHandle();
        }
    }
}

#endif
