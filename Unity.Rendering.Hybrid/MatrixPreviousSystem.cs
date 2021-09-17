using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Unity.Rendering
{
    [ExecuteAlways]
    //@TODO: Necessary due to empty component group. When Component group and archetype chunks are unified this should be removed
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(HybridRendererSystem))]
    public partial class MatrixPreviousSystem : SystemBase
    {
        private EntityQuery m_GroupPrev;

        [BurstCompile]
        struct UpdateMatrixPrevious : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> LocalToWorldTypeHandle;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_MatrixPreviousM> MatrixPreviousTypeHandle;
            public uint LastSystemVersion;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                var chunkLocalToWorld = chunk.GetNativeArray(LocalToWorldTypeHandle);
                var chunkMatrixPrevious = chunk.GetNativeArray(MatrixPreviousTypeHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    var localToWorld = chunkLocalToWorld[i].Value;
                    chunkMatrixPrevious[i] = new BuiltinMaterialPropertyUnity_MatrixPreviousM {Value = localToWorld};
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
            m_GroupPrev.SetChangedVersionFilter(new[]
            {
                ComponentType.ReadOnly<LocalToWorld>(),
                ComponentType.ReadOnly<BuiltinMaterialPropertyUnity_MatrixPreviousM>()
            });
        }

        protected override void OnUpdate()
        {
            if (!HybridRendererSystem.HybridRendererEnabled)
                return;

            var updateMatrixPreviousJob = new UpdateMatrixPrevious
            {
                LocalToWorldTypeHandle = GetComponentTypeHandle<LocalToWorld>(true),
                MatrixPreviousTypeHandle = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_MatrixPreviousM>(),
                LastSystemVersion = LastSystemVersion
            };
            Dependency = updateMatrixPreviousJob.Schedule(m_GroupPrev, Dependency);
        }
    }
}
