using Unity.Entities;

namespace Unity.Rendering
{
    internal struct SharedMeshData : ISystemStateSharedComponentData
    {
        public int VertexCount;
        public int BlendShapeCount;
        public int BlendShapeVertexCount;
        public int BoneCount;
        public int BoneInfluencesCount;
        public int RenderMeshHash;

        public bool HasSkinning => BoneCount > 0;
        public bool HasBlendShapes => BlendShapeCount > 0;
    }

#if ENABLE_COMPUTE_DEFORMATIONS
    internal struct SharedMeshBufferIndex
    {
        public int GeometryIndex;
        public int BoneInfluencesIndex;
        public int BlendShapeIndex;
    }
#endif
}
