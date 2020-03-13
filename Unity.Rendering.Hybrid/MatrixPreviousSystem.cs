using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

#if ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_1_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
namespace Unity.Rendering
{
    [ExecuteAlways]
    //@TODO: Necessary due to empty component group. When Component group and archetype chunks are unified this should be removed
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(HybridRendererSystem))]
    public class MatrixPreviousSystem : JobComponentSystem
    {
        private EntityQuery m_GroupPrev;
        private EntityQuery m_GroupPrevInverse;

        [BurstCompile]
        struct UpdateMatrixPrevious : IJobChunk
        {
            [ReadOnly] public ArchetypeChunkComponentType<LocalToWorld> LocalToWorldType;
            public ArchetypeChunkComponentType<BuiltinMaterialPropertyUnity_MatrixPreviousM> MatrixPreviousType;
            public uint LastSystemVersion;
            
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                var chunkLocalToWorld = chunk.GetNativeArray(LocalToWorldType);
                var chunkMatrixPrevious = chunk.GetNativeArray(MatrixPreviousType);
                for (int i = 0; i < chunk.Count; i++)
                {
                    var localToWorld = chunkLocalToWorld[i].Value;
                    chunkMatrixPrevious[i] = new BuiltinMaterialPropertyUnity_MatrixPreviousM { Value = localToWorld };
                }
            }
        }
        
        [BurstCompile]
        struct UpdateMatrixPreviousInverse : IJobChunk
        {
            [ReadOnly] public ArchetypeChunkComponentType<WorldToLocal> WorldToLocalType;
            public ArchetypeChunkComponentType<BuiltinMaterialPropertyUnity_MatrixPreviousMI> MatrixPreviousInverseType;
            public uint LastSystemVersion;
            
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                var chunkWorldToLocal = chunk.GetNativeArray(WorldToLocalType);
                var chunkMatrixPrevious = chunk.GetNativeArray(MatrixPreviousInverseType);
                for (int i = 0; i < chunk.Count; i++)
                {
                    var worldToLocal = chunkWorldToLocal[i].Value;
                    chunkMatrixPrevious[i] = new BuiltinMaterialPropertyUnity_MatrixPreviousMI { Value = worldToLocal };
                }
            }
        }
        
        protected override void OnCreate()
        {
            m_GroupPrev = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_MatrixPreviousM>(),
                },
                Options = EntityQueryOptions.FilterWriteGroup
            });
            m_GroupPrev.SetChangedVersionFilter(new [] { ComponentType.ReadOnly<LocalToWorld>(), ComponentType.ReadOnly<BuiltinMaterialPropertyUnity_MatrixPreviousM>() } );
            
            m_GroupPrevInverse = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<WorldToLocal>(),
                    ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_MatrixPreviousMI>(),
                },
                Options = EntityQueryOptions.FilterWriteGroup
            });
            m_GroupPrevInverse.SetChangedVersionFilter(new [] { ComponentType.ReadOnly<WorldToLocal>(), ComponentType.ReadOnly<BuiltinMaterialPropertyUnity_MatrixPreviousMI>() } );
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var updateMatrixPreviousJob = new UpdateMatrixPrevious
            {
                LocalToWorldType = GetArchetypeChunkComponentType<LocalToWorld>(true),
                MatrixPreviousType = GetArchetypeChunkComponentType<BuiltinMaterialPropertyUnity_MatrixPreviousM>(),
                LastSystemVersion = LastSystemVersion
            };
            var updateMatrixPreviousInverseJob = new UpdateMatrixPreviousInverse
            {
                WorldToLocalType = GetArchetypeChunkComponentType<WorldToLocal>(true),
                MatrixPreviousInverseType = GetArchetypeChunkComponentType<BuiltinMaterialPropertyUnity_MatrixPreviousMI>(),
                LastSystemVersion = LastSystemVersion
            };

            var updateMatrixPreviousJobHandle = updateMatrixPreviousJob.Schedule(m_GroupPrev, inputDeps);
            var updateMatrixPreviousInverseJobHandle = updateMatrixPreviousInverseJob.Schedule( m_GroupPrevInverse, inputDeps);
            var combinedJob = JobHandle.CombineDependencies(updateMatrixPreviousJobHandle, updateMatrixPreviousInverseJobHandle);

            return combinedJob;
        }
    }
}
#endif

