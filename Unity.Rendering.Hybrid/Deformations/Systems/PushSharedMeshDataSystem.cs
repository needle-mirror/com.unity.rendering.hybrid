using Unity.Entities;
using Unity.Profiling;
using Debug = UnityEngine.Debug;

namespace Unity.Rendering
{
#if ENABLE_COMPUTE_DEFORMATIONS
    public abstract partial class PushMeshDataSystemBase : SystemBase
    {
        [DisableAutoCreation]
        internal class PushSharedMeshDataSystem : SystemBase
        {
            static readonly ProfilerMarker k_LayoutSharedMeshData = new ProfilerMarker("Layout Shared Mesh Data");
            static readonly ProfilerMarker k_CollectSharedMeshData = new ProfilerMarker("Collect Shared Mesh Data");
            static readonly ProfilerMarker k_UploadSharedMeshData = new ProfilerMarker("Upload Shared Mesh Data");

            public PushMeshDataSystemBase Parent;

            EntityQuery m_Query;

            protected override void OnCreate()
            {
                m_Query = GetEntityQuery(
                    ComponentType.ReadOnly<RenderMesh>(),
                    ComponentType.ReadOnly<SharedMeshData>()
                );
            }

            protected override void OnUpdate()
            {
                k_LayoutSharedMeshData.Begin();

                int vertexCount = 0;
                int boneInfluencesCount = 0;
                int blendShapeVertexCount = 0;

                Parent.MeshHashToSharedBuffer.Clear();
                foreach (var meshData in Parent.UniqueSharedMeshData)
                {
                    Parent.MeshHashToSharedBuffer.Add(meshData.RenderMeshHash, new SharedMeshBufferIndex
                    {
                        GeometryIndex = vertexCount,
                        BoneInfluencesIndex = boneInfluencesCount,
                        BlendShapeIndex = blendShapeVertexCount
                    });

                    vertexCount += meshData.VertexCount;
                    boneInfluencesCount += meshData.BoneInfluencesCount;
                    blendShapeVertexCount += meshData.BlendShapeVertexCount;
                }

                Debug.Assert(vertexCount == Parent.m_ResizeBuffersSystem.totalSharedVertexCount, $"vertexCount: {vertexCount} is expected to be equal to totalVertexCount {Parent.m_ResizeBuffersSystem.totalSharedVertexCount}.");

                k_LayoutSharedMeshData.End();
                k_CollectSharedMeshData.Begin();

                foreach (var meshData in Parent.UniqueSharedMeshData)
                {
                    if (meshData.RenderMeshHash == 0)
                        continue;

                    var renderMesh = Parent.m_MeshHashToRenderMesh[meshData.RenderMeshHash];
                    var bufferIndex = Parent.MeshHashToSharedBuffer[meshData.RenderMeshHash];

                    Parent.MeshBufferManager.FetchMeshData(renderMesh.mesh, bufferIndex.GeometryIndex);

                    if (meshData.HasSkinning)
                        Parent.SkinningBufferManager.FetchMeshData(renderMesh.mesh, bufferIndex.GeometryIndex, bufferIndex.BoneInfluencesIndex);

                    if (meshData.HasBlendShapes)
                        Parent.BlendShapeBufferManager.FetchMeshData(renderMesh.mesh, bufferIndex.GeometryIndex, bufferIndex.BlendShapeIndex);
                }

                k_CollectSharedMeshData.End();
                k_UploadSharedMeshData.Begin();

                Parent.MeshBufferManager.PushMeshData();
                Parent.BlendShapeBufferManager.PushSharedMeshData();
                Parent.SkinningBufferManager.PushSharedMeshData();

                k_UploadSharedMeshData.End();
                Parent.m_RebuildSharedMeshBuffers = false;
            }
        }
    }
#endif
}
