using Unity.Entities;

#if ENABLE_HYBRID_RENDERER_V2 && URP_9_0_0_OR_NEWER
namespace Unity.Rendering
{
    public struct CustomProbeTag : IComponentData
    {
    }
}
#endif
