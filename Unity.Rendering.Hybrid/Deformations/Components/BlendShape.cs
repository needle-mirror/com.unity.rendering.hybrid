using Unity.Entities;

namespace Unity.Rendering
{
    internal struct BlendShapeTag : IComponentData { }

#if ENABLE_COMPUTE_DEFORMATIONS
    internal struct BlendWeightBufferIndex : IComponentData
    {
        public int Value;
    }
#endif
}
