#if ENABLE_HYBRID_RENDERER_V2
using System;
using Unity.Entities;
using UnityEngine;

namespace Unity.Rendering
{
    [UpdateInGroup(typeof(StructuralChangePresentationSystemGroup))]
    [ExecuteAlways]
    class ManageSHPropertiesSystem : SystemBase
    {
        EntityQuery[] m_MissingSHQueries;
        EntityQuery m_MissingProbeTagQuery;
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

            m_MissingSHQueries = new EntityQuery[m_ComponentTypes.Length];
            for (var i = 0; i < m_ComponentTypes.Length; i++)
            {
                m_MissingSHQueries[i] = GetEntityQuery(new EntityQueryDesc
                {
                    Any = new[]
                    {
                        ComponentType.ReadOnly<AmbientProbeTag>(),
                        ComponentType.ReadOnly<BlendProbeTag>(),
                        ComponentType.ReadOnly<CustomProbeTag>()
                    },
                    None = new[] {m_ComponentTypes[i]}
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
                }
            });
        }

        protected override void OnUpdate()
        {
            for (var i = 0; i < m_ComponentTypes.Length; i++)
            {
                EntityManager.AddComponent(m_MissingSHQueries[i], m_ComponentTypes[i]);
                EntityManager.RemoveComponent(m_MissingProbeTagQuery, m_ComponentTypes[i]);
            }
        }
    }
}
#endif
