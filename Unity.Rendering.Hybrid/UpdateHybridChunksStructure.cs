using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace Unity.Rendering
{
    /// <summary>
    /// Renders all Entities containing both RenderMesh and LocalToWorld components.
    /// </summary>
    [ExecuteAlways]
    //@TODO: Necessary due to empty component group. When Component group and archetype chunks are unified this should be removed
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(StructuralChangePresentationSystemGroup))]
    public partial class UpdateHybridChunksStructure : SystemBase
    {
        private EntityQuery m_MissingHybridChunkInfo;
        private EntityQuery m_DisabledRenderingQuery;

        protected override void OnCreate()
        {
            m_MissingHybridChunkInfo = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(),
                    ComponentType.ReadOnly<WorldRenderBounds>(),
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<RenderMesh>(),
                },
                None = new[]
                {
                    ComponentType.ChunkComponentReadOnly<HybridChunkInfo>(),
                    ComponentType.ReadOnly<DisableRendering>(),
                },

                // TODO: Add chunk component to disabled entities and prefab entities to work around
                // the fragmentation issue where entities are not added to existing chunks with chunk
                // components. Remove this once chunk components don't affect archetype matching
                // on entity creation.
                Options = EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludePrefab,
            });

            m_DisabledRenderingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<DisableRendering>(),
                },
            });
        }

        protected override void OnUpdate()
        {
            UnityEngine.Profiling.Profiler.BeginSample("UpdateHybridChunksStructure");
            {
                EntityManager.AddComponent(m_MissingHybridChunkInfo, ComponentType.ChunkComponent<HybridChunkInfo>());
                EntityManager.RemoveChunkComponentData<HybridChunkInfo>(m_DisabledRenderingQuery);
            }
            UnityEngine.Profiling.Profiler.EndSample();
        }
    }
}
