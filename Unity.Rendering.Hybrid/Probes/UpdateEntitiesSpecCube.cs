#if ENABLE_HYBRID_RENDERER_V2 && URP_9_0_0_OR_NEWER

using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.Rendering
{
    [UpdateInGroup(typeof(UpdatePresentationSystemGroup))]
    partial class UpdateEntitiesSpecCube : JobComponentSystem
    {
        Vector4 m_LastSpecCube;
        EntityQuery m_Query;

        protected override void OnCreate()
        {
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    typeof(BuiltinMaterialPropertyUnity_SpecCube0_HDR)
                }
            });

            m_LastSpecCube = Vector4.zero;
        }

        protected override JobHandle OnUpdate(JobHandle inputDependencies)
        {
            var defaultSpecCube = ReflectionProbe.defaultTextureHDRDecodeValues;
            var defaultSpecCubeChanged = defaultSpecCube != m_LastSpecCube;
            var lastSystemVersion = LastSystemVersion;

            var jobHandle = new UpdateJob
            {
                SpecCube0Handle = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SpecCube0_HDR>(),
                DefaultSpecCube = defaultSpecCube,
                LastSystemVersion = lastSystemVersion,
                DefaultSpecCubeChanged = defaultSpecCubeChanged,

            }
            .ScheduleParallel(m_Query, inputDependencies);

            m_LastSpecCube = defaultSpecCube;
            return jobHandle;
        }

        [BurstCompile]
        private struct UpdateJob : IJobChunk
        {
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SpecCube0_HDR> SpecCube0Handle;
            public float4 DefaultSpecCube;
            public uint LastSystemVersion;
            public bool DefaultSpecCubeChanged;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                bool structuralChange = chunk.DidChange(SpecCube0Handle, LastSystemVersion);

                if (structuralChange || DefaultSpecCubeChanged)
                {
                    var specCube0s = chunk.GetNativeArray(SpecCube0Handle);

                    for (int i = 0; i < chunk.Count; ++i)
                    {
                        var specCube = specCube0s[i];
                        specCube.Value = DefaultSpecCube;

                        specCube0s[i] = specCube;
                    }
                }
            }
        }
    }
}
#endif
