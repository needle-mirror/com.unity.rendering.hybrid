using Unity.Entities;
using UnityEngine;

namespace Unity.Rendering
{
    // MeshRendererConversion is public so UpdateBefore and UpdateAfter can be used with it.
    // It contains no public methods of its own.
    [ConverterVersion("unity", 13)]
    [WorldSystemFilter(WorldSystemFilterFlags.HybridGameObjectConversion)]
    public sealed class MeshRendererConversion : GameObjectConversionSystem
    {
        // Hold a persistent light map conversion context so previously encountered light maps
        // can be reused across multiple conversion batches, which is especially important
        // for incremental conversion (LiveLink).
        private LightMapConversionContext m_LightMapConversionContext;

        protected override void OnCreate()
        {
            base.OnCreate();

#if UNITY_2020_2_OR_NEWER
            m_LightMapConversionContext = new LightMapConversionContext();
#else
            m_LightMapConversionContext = null;
#endif
        }

        protected override void OnUpdate()
        {
            // TODO: When to call m_LightMapConversionContext.Reset() ? When lightmaps are baked?
            var context = new RenderMeshConversionContext(DstEntityManager, this, m_LightMapConversionContext);

            if (m_LightMapConversionContext != null)
            {
                Entities.WithNone<TextMesh>().ForEach((MeshRenderer meshRenderer, MeshFilter meshFilter) =>
                {
                    context.CollectLightMapUsage(meshRenderer);
                });
            }

            context.ProcessLightMapsForConversion();

            Entities.WithNone<TextMesh>().ForEach((MeshRenderer meshRenderer, MeshFilter meshFilter) =>
            {
                context.Convert(meshRenderer, meshFilter.sharedMesh);
            });

            context.EndConversion();
        }
    }
}
