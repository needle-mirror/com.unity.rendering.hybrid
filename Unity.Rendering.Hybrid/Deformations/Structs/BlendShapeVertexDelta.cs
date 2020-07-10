using Unity.Mathematics;

namespace Unity.Rendering
{
#if ENABLE_COMPUTE_DEFORMATIONS
    internal struct BlendShapeVertexDelta
    {
        public int BlendShapeIndex;
        public float3 Position;
        public float3 Normal;
        public float3 Tangent;
    }
#endif
}
