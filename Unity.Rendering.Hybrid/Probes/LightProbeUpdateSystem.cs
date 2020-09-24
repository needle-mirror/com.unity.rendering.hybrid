using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;
using UnityEngine;

#if false

namespace Unity.Rendering
{
    [UpdateInGroup(typeof(UpdatePresentationSystemGroup))]
    [ExecuteAlways]
    [AlwaysUpdateSystem]
    class LightProbeUpdateSystem : SystemBase
    {
        private EntityQuery m_Query;
        private Dictionary<JobHandle, LightProbesQuery> m_ScheduledJobs = new Dictionary<JobHandle, LightProbesQuery>(100);

        private bool m_UpdateAll = true;

        private void NeedUpdate()
        {
            m_UpdateAll = true;
        }

        protected override void OnCreate()
        {
            LightProbes.lightProbesUpdated += NeedUpdate;
            m_Query = GetEntityQuery(
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHAr>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHAg>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHAb>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHBr>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHBg>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHBb>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHC>(),
                ComponentType.ReadOnly<LocalToWorld>(),
                ComponentType.ReadOnly<BlendProbeTag>()
            );
            m_Query.SetChangedVersionFilter(ComponentType.ReadOnly<LocalToWorld>());
        }

        protected override void OnDestroy()
        {
            LightProbes.lightProbesUpdated -= NeedUpdate;

            foreach (var query in m_ScheduledJobs)
            {
                query.Key.Complete();
                query.Value.Dispose();
            }
            m_ScheduledJobs.Clear();
        }

        protected override void OnUpdate()
        {
            CleanUpCompletedJobs();

            var lightProbesQuery = new LightProbesQuery(Allocator.Persistent);

            if (m_UpdateAll)
                m_Query.ResetFilter();

            var job = new UpdateSHValuesJob
            {
                lightProbesQuery = lightProbesQuery,
                SHArType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAr>(),
                SHAgType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAg>(),
                SHAbType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAb>(),
                SHBrType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBr>(),
                SHBgType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBg>(),
                SHBbType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBb>(),
                SHCType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHC>(),
                LocalToWorldType = GetComponentTypeHandle<LocalToWorld>(),
            };

            Dependency = job.ScheduleParallel(m_Query, Dependency);
            m_ScheduledJobs.Add(Dependency, lightProbesQuery);

            if (m_UpdateAll)
            {
                m_Query.SetChangedVersionFilter(ComponentType.ReadOnly<LocalToWorld>());
                m_UpdateAll = false;
            }
        }

        List<JobHandle> m_ToRemoveList = new List<JobHandle>(100);
        private void CleanUpCompletedJobs()
        {
            m_ToRemoveList.Clear();
            foreach (var query in m_ScheduledJobs)
            {
                if (query.Key.IsCompleted)
                {
                    query.Value.Dispose();
                    m_ToRemoveList.Add(query.Key);
                }
            }

            foreach (var key in m_ToRemoveList)
                m_ScheduledJobs.Remove(key);

            m_ToRemoveList.Clear();
        }

        [BurstCompile]
        struct UpdateSHValuesJob : IJobChunk
        {
            //public SHProperties Properties;
            [Collections.ReadOnly]
            public LightProbesQuery lightProbesQuery;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAr> SHArType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAg> SHAgType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAb> SHAbType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBr> SHBrType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBg> SHBgType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBb> SHBbType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHC> SHCType;
            public ComponentTypeHandle<LocalToWorld> LocalToWorldType;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                var chunkSHAr = chunk.GetNativeArray(SHArType);
                var chunkSHAg = chunk.GetNativeArray(SHAgType);
                var chunkSHAb = chunk.GetNativeArray(SHAbType);
                var chunkSHBr = chunk.GetNativeArray(SHBrType);
                var chunkSHBg = chunk.GetNativeArray(SHBgType);
                var chunkSHBb = chunk.GetNativeArray(SHBbType);
                var chunkSHC = chunk.GetNativeArray(SHCType);
                var chunkLocalToWorld = chunk.GetNativeArray(LocalToWorldType);

                var tetrahedronIndexGuesses = new NativeArray<int>(chunkSHAr.Length, Allocator.Temp);
                for (var i = 0; i < chunkSHAr.Length; i++)
                    tetrahedronIndexGuesses[i] = -1;

                for (var i = 0; i < chunkSHAr.Length; i++)
                {
                    var position = chunkLocalToWorld[i].Position;
                    int tetrahedronIndex = tetrahedronIndexGuesses[i];
                    int prevTetrahedronIndex = tetrahedronIndex;
                    lightProbesQuery.CalculateInterpolatedLightAndOcclusionProbe(position, tetrahedronIndex, out var lightProbe, out var occlusionProbe);
                    if (tetrahedronIndex != prevTetrahedronIndex)
                        tetrahedronIndexGuesses[i] = tetrahedronIndex;

                    var properties = new SHProperties(lightProbe);
                    chunkSHAr[i] = new BuiltinMaterialPropertyUnity_SHAr {Value = properties.SHAr};
                    chunkSHAg[i] = new BuiltinMaterialPropertyUnity_SHAg {Value = properties.SHAg};
                    chunkSHAb[i] = new BuiltinMaterialPropertyUnity_SHAb {Value = properties.SHAb};
                    chunkSHBr[i] = new BuiltinMaterialPropertyUnity_SHBr {Value = properties.SHBr};
                    chunkSHBg[i] = new BuiltinMaterialPropertyUnity_SHBg {Value = properties.SHBg};
                    chunkSHBb[i] = new BuiltinMaterialPropertyUnity_SHBb {Value = properties.SHBb};
                    chunkSHC[i] = new BuiltinMaterialPropertyUnity_SHC {Value = properties.SHC};
                }
            }
        }
    }
}

#endif
