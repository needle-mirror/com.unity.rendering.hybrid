#if ENABLE_HYBRID_RENDERER_V2 && URP_9_0_0_OR_NEWER
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Rendering
{
    struct SHProperties
    {
        public float4 SHAr;
        public float4 SHAg;
        public float4 SHAb;
        public float4 SHBr;
        public float4 SHBg;
        public float4 SHBb;
        public float4 SHC;

        public SHProperties(SphericalHarmonicsL2 sh)
        {
            SHAr = GetSHA(sh, 0);
            SHAg = GetSHA(sh, 1);
            SHAb = GetSHA(sh, 2);

            SHBr = GetSHB(sh, 0);
            SHBg = GetSHB(sh, 1);
            SHBb = GetSHB(sh, 2);

            SHC = GetSHC(sh);
        }

        static float4 GetSHA(SphericalHarmonicsL2 sh, int i)
        {
            return float4(sh[i, 3], sh[i, 1], sh[i, 2], sh[i, 0] - sh[i, 6]);
        }

        static float4 GetSHB(SphericalHarmonicsL2 sh, int i)
        {
            return float4(sh[i, 4], sh[i, 5], sh[i, 6] * 3f, sh[i, 7]);
        }

        static float4 GetSHC(SphericalHarmonicsL2 sh)
        {
            return float4(sh[0, 8], sh[1, 8], sh[2, 8], 1);
        }
    }

    [UpdateInGroup(typeof(UpdatePresentationSystemGroup))]
    [ExecuteAlways]
    class AmbientProbeUpdateSystem : SystemBase
    {
        EntityQuery m_Query;
        SphericalHarmonicsL2 m_LastAmbientProbe;

        protected override void OnCreate()
        {
            m_Query = GetEntityQuery(
                ComponentType.ReadOnly<AmbientProbeTag>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHAr>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHAg>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHAb>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHBr>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHBg>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHBb>(),
                ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_SHC>());
            m_Query.SetChangedVersionFilter(ComponentType.ReadWrite<AmbientProbeTag>());
        }

        protected override void OnUpdate()
        {
            var ambientProbe = RenderSettings.ambientProbe;
            var updateAll = ambientProbe != m_LastAmbientProbe;
            if (updateAll)
            {
                m_Query.ResetFilter();
            }

            m_LastAmbientProbe = ambientProbe;

            var job = new UpdateSHValuesJob
            {
                Properties = new SHProperties(ambientProbe),
                SHArType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAr>(),
                SHAgType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAg>(),
                SHAbType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHAb>(),
                SHBrType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBr>(),
                SHBgType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBg>(),
                SHBbType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHBb>(),
                SHCType = GetComponentTypeHandle<BuiltinMaterialPropertyUnity_SHC>(),
            };

            Dependency = job.ScheduleParallel(m_Query, Dependency);

            if (updateAll)
            {
                m_Query.SetChangedVersionFilter(ComponentType.ReadWrite<AmbientProbeTag>());
            }
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
