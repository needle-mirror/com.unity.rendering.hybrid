using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace Unity.Rendering
{
    internal static class HybridUtils
    {
        public static EntityQueryDesc GetHybridRenderedQueryDesc()
        {
            return new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(),
                    ComponentType.ReadOnly<WorldRenderBounds>(),
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<RenderMesh>(),
                    ComponentType.ChunkComponent<HybridChunkInfo>(),
                },
            };
        }
    }
}
