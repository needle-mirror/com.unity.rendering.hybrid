#if ENABLE_HYBRID_RENDERER_V2

using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Unity.Rendering
{
    [UpdateInGroup(typeof(UpdatePresentationSystemGroup))]
    [ExecuteAlways]
    [AlwaysUpdateSystem]
    class LightProbeUpdateSystem : SystemBase
    {
        EntityQuery m_ProbeGridQuery;
        EntityQuery m_AmbientProbQuery;
        SphericalHarmonicsL2 m_LastAmbientProbe;

        private ComponentType[] gridQuertyFilter = {ComponentType.ReadOnly<LocalToWorld>(), ComponentType.ReadWrite<BlendProbeTag>()};
        private ComponentType[] gridQuertyFilterForAmbient = { ComponentType.ReadWrite<BlendProbeTag>() };
        private ComponentType[] ambientQuertyFilter = {ComponentType.ReadWrite<AmbientProbeTag>()};

        protected override void OnCreate()
        {
            m_ProbeGridQuery = GetEntityQuery(
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
            m_ProbeGridQuery.SetChangedVersionFilter(gridQuertyFilter);

            m_AmbientProbQuery = GetEntityQuery(
                ComponentType.ReadOnly<AmbientProbeTag>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHAr>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHAg>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHAb>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHBr>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHBg>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHBb>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHC>());
            m_AmbientProbQuery.SetChangedVersionFilter(ambientQuertyFilter);
        }

        protected override void OnUpdate()
        {
            // if we have valid grid use that...
            var probes = LightmapSettings.lightProbes;
            var ambientProbe = RenderSettings.ambientProbe;
            bool validGrid = probes != null && probes.count != 0;

            if (validGrid)
            { 
                UpdateEntitiesFromGrid();
            }

            // update ambients
            UpdateEntitiesFromAmbientProbe(this, m_AmbientProbQuery, ambientQuertyFilter, ambientProbe, m_LastAmbientProbe);

            // if there is no valid grid - instead use ambient probe for all entities
            if (!validGrid)
            {
                m_ProbeGridQuery.SetChangedVersionFilter(gridQuertyFilterForAmbient);
                UpdateEntitiesFromAmbientProbe(this, m_ProbeGridQuery, gridQuertyFilterForAmbient, ambientProbe, m_LastAmbientProbe);
            }

            m_LastAmbientProbe = ambientProbe;
        }

        private static void UpdateEntitiesFromAmbientProbe(
            LightProbeUpdateSystem system,
            EntityQuery query,
            ComponentType[] queryFilter,
            SphericalHarmonicsL2 ambientProbe,
            SphericalHarmonicsL2 lastProbe)
        {
            Profiler.BeginSample("UpdateEntitiesFromAmbientProbe");
            var updateAll = ambientProbe != lastProbe;
            if (updateAll)
            { 
                query.ResetFilter();
            }

            var job = new UpdateSHValuesJob
            {
                Properties = new SHProperties(ambientProbe),
                SHArType = system.GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAr>(),
                SHAgType = system.GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAg>(),
                SHAbType = system.GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAb>(),
                SHBrType = system.GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBr>(),
                SHBgType = system.GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBg>(),
                SHBbType = system.GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBb>(),
                SHCType = system.GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHC>(),
            };

            system.Dependency = job.ScheduleParallel(query, system.Dependency);

            if (updateAll)
            {
                query.SetChangedVersionFilter(queryFilter);
            }
            Profiler.EndSample();
        }

        private List<Vector3> m_Positions = new List<Vector3>(512);
        private List<SphericalHarmonicsL2> m_LightProbes = new List<SphericalHarmonicsL2>(512);
        private List<Vector4> m_OcclusionProbes = new List<Vector4>(512);
        private void UpdateEntitiesFromGrid()
        {
            Profiler.BeginSample("UpdateEntitiesFromGrid");

            var SHArType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAr>();
            var SHAgType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAg>();
            var SHAbType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAb>();
            var SHBrType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBr>();
            var SHBgType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBg>();
            var SHBbType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBb>();
            var SHCType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHC>();
            var localToWorldType = GetComponentTypeHandle<LocalToWorld>();

            var chunks  = m_ProbeGridQuery.CreateArchetypeChunkArray(Allocator.Temp);
            if (chunks.Length == 0)
            {
                Profiler.EndSample();
                return;
            }

            //TODO: Bring this off the main thread when we have new c++ API
            Dependency.Complete();

            foreach (var chunk in chunks)
            {
                var chunkSHAr = chunk.GetNativeArray(SHArType);
                var chunkSHAg = chunk.GetNativeArray(SHAgType);
                var chunkSHAb = chunk.GetNativeArray(SHAbType);
                var chunkSHBr = chunk.GetNativeArray(SHBrType);
                var chunkSHBg = chunk.GetNativeArray(SHBgType);
                var chunkSHBb = chunk.GetNativeArray(SHBbType);
                var chunkSHC = chunk.GetNativeArray(SHCType);
                var chunkLocalToWorld = chunk.GetNativeArray(localToWorldType);

                m_Positions.Clear();
                m_LightProbes.Clear();
                m_OcclusionProbes.Clear();

                for (int i = 0; i != chunkLocalToWorld.Length; i++)
                    m_Positions.Add(chunkLocalToWorld[i].Position);

                LightProbes.CalculateInterpolatedLightAndOcclusionProbes(m_Positions, m_LightProbes, m_OcclusionProbes);

                for (int i = 0; i < m_Positions.Count; ++i)
                {
                    var properties = new SHProperties(m_LightProbes[i]);
                    chunkSHAr[i] = new BuiltinMaterialPropertyUnity_SHAr {Value = properties.SHAr};
                    chunkSHAg[i] = new BuiltinMaterialPropertyUnity_SHAg {Value = properties.SHAg};
                    chunkSHAb[i] = new BuiltinMaterialPropertyUnity_SHAb {Value = properties.SHAb};
                    chunkSHBr[i] = new BuiltinMaterialPropertyUnity_SHBr {Value = properties.SHBr};
                    chunkSHBg[i] = new BuiltinMaterialPropertyUnity_SHBg {Value = properties.SHBg};
                    chunkSHBb[i] = new BuiltinMaterialPropertyUnity_SHBb {Value = properties.SHBb};
                    chunkSHC[i] = new BuiltinMaterialPropertyUnity_SHC {Value = properties.SHC};
                }
            }
            Profiler.EndSample();
        }

        [BurstCompile]
        struct UpdateSHValuesJob : IJobChunk
        {
            public SHProperties Properties;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAr> SHArType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAg> SHAgType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAb> SHAbType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBr> SHBrType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBg> SHBgType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBb> SHBbType;
            public ComponentTypeHandle<BuiltinMaterialPropertyUnity_SHC> SHCType;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                var chunkSHAr = chunk.GetNativeArray(SHArType);
                var chunkSHAg = chunk.GetNativeArray(SHAgType);
                var chunkSHAb = chunk.GetNativeArray(SHAbType);
                var chunkSHBr = chunk.GetNativeArray(SHBrType);
                var chunkSHBg = chunk.GetNativeArray(SHBgType);
                var chunkSHBb = chunk.GetNativeArray(SHBbType);
                var chunkSHC = chunk.GetNativeArray(SHCType);

                for (var i = 0; i < chunkSHAr.Length; i++)
                {
                    chunkSHAr[i] = new BuiltinMaterialPropertyUnity_SHAr {Value = Properties.SHAr};
                    chunkSHAg[i] = new BuiltinMaterialPropertyUnity_SHAg {Value = Properties.SHAg};
                    chunkSHAb[i] = new BuiltinMaterialPropertyUnity_SHAb {Value = Properties.SHAb};
                    chunkSHBr[i] = new BuiltinMaterialPropertyUnity_SHBr {Value = Properties.SHBr};
                    chunkSHBg[i] = new BuiltinMaterialPropertyUnity_SHBg {Value = Properties.SHBg};
                    chunkSHBb[i] = new BuiltinMaterialPropertyUnity_SHBb {Value = Properties.SHBb};
                    chunkSHC[i] = new BuiltinMaterialPropertyUnity_SHC {Value = Properties.SHC};
                }
            }
        }
    }
}
#endif
