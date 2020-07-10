using Unity.Mathematics;

namespace Unity.Rendering
{
#if ENABLE_COMPUTE_DEFORMATIONS
    /// <summary>
    /// Vertex data for the SharedMesh buffer
    /// Needs to map between compute shaders and CPU
    /// </summary>
    internal struct VertexData
    {
        public float3 Position;
        public float3 Normal;
        public float3 Tangent;
    }
#endif
}
