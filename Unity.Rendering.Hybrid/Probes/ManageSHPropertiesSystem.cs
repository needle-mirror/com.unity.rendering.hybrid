// #define DISABLE_HYBRID_LIGHT_PROBES

using System;
using System.Linq;
using Unity.Entities;
using UnityEngine;

#if !DISABLE_HYBRID_LIGHT_PROBES
namespace Unity.Rendering
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.EntitySceneOptimizations)]
    [UpdateInGroup(typeof(StructuralChangePresentationSystemGroup))]
    [ExecuteAlways]
    partial class ManageSHPropertiesSystem : SystemBase
    {
        // Queries that match entities with CustomProbeTag, but without SH components
        EntityQuery[] m_MissingSHQueriesCustom;
        // Queries that match entities with BlendProbeTag, but without SH components
        EntityQuery[] m_MissingSHQueriesBlend;

        // Matches entities with SH components, but neither CustomProbeTag or BlendProbeTag
        EntityQuery m_MissingProbeTagQuery;
        // Matches entities with SH components and BlendProbeTag
        EntityQuery m_RemoveSHFromBlendProbeTagQuery;

        ComponentType[] m_ComponentTypes;

        protected override void OnCreate()
        {
            m_ComponentTypes = new[]
            {
                ComponentType.ReadOnly<BuiltinMaterialPropertyUnity_SHAr>(),
                ComponentType.ReadOnly<BuiltinMaterialPropertyUnity_SHAg>(),
                ComponentType.ReadOnly<BuiltinMaterialPropertyUnity_SHAb>(),
                ComponentType.ReadOnly<BuiltinMaterialPropertyUnity_SHBr>(),
                ComponentType.ReadOnly<BuiltinMaterialPropertyUnity_SHBg>(),
                ComponentType.ReadOnly<BuiltinMaterialPropertyUnity_SHBb>(),
                ComponentType.ReadOnly<BuiltinMaterialPropertyUnity_SHC>(),
            };

            m_MissingSHQueriesCustom = new EntityQuery[m_ComponentTypes.Length];
            m_MissingSHQueriesBlend = new EntityQuery[m_ComponentTypes.Length];

            for (var i = 0; i < m_ComponentTypes.Length; i++)
            {
                m_MissingSHQueriesCustom[i] = GetEntityQuery(new EntityQueryDesc
                {
                    Any = new[]
                    {
                        ComponentType.ReadOnly<CustomProbeTag>()
                    },
                    None = new[] {m_ComponentTypes[i]},
                    Options = EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludePrefab
                });

                m_MissingSHQueriesBlend[i] = GetEntityQuery(new EntityQueryDesc
                {
                    Any = new[]
                    {
                        ComponentType.ReadOnly<BlendProbeTag>(),
                    },
                    None = new[] {m_ComponentTypes[i]},
                    Options = EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludePrefab
                });
            }

            m_MissingProbeTagQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = m_ComponentTypes,
                None = new[]
                {
                    ComponentType.ReadOnly<AmbientProbeTag>(),
                    ComponentType.ReadOnly<BlendProbeTag>(),
                    ComponentType.ReadOnly<CustomProbeTag>()
                },
                Options = EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludePrefab
            });

            m_RemoveSHFromBlendProbeTagQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = m_ComponentTypes,
                All = new []{ ComponentType.ReadOnly<BlendProbeTag>(), },
            });
        }

        protected override void OnUpdate()
        {
            // If there is a valid light probe grid, BlendProbeTag entities should have SH components
            // If there is no valid light probe grid, BlendProbeTag entities will not have SH components
            // and behave as if they had AmbientProbeTag instead (read from global probe).
            bool validGrid = LightProbeUpdateSystem.IsValidLightProbeGrid();

            for (var i = 0; i < m_ComponentTypes.Length; i++)
            {
                // CustomProbeTag entities should always have SH components
                EntityManager.AddComponent(m_MissingSHQueriesCustom[i], m_ComponentTypes[i]);

                // BlendProbeTag entities have SH components if and only if there's a valid light probe grid
                if (validGrid)
                    EntityManager.AddComponent(m_MissingSHQueriesBlend[i], m_ComponentTypes[i]);
                else
                    EntityManager.RemoveComponent(m_RemoveSHFromBlendProbeTagQuery, m_ComponentTypes[i]);

                // AmbientProbeTag entities never have SH components

                EntityManager.RemoveComponent(m_MissingProbeTagQuery, m_ComponentTypes[i]);
            }
        }
    }
}
#endif
