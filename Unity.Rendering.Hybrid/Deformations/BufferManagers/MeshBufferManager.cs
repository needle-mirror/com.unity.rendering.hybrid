using Unity.Collections;
using UnityEngine;

namespace Unity.Rendering
{
#if ENABLE_COMPUTE_DEFORMATIONS
    internal class MeshBufferManager
    {
        const int k_ChunkSize = 2048;

        ComputeBufferWrapper<VertexData> m_DeformedMeshData;
        ComputeBufferWrapper<VertexData> m_SharedMeshBuffer;

        public void OnCreate()
        {
            m_DeformedMeshData = new ComputeBufferWrapper<VertexData>("_DeformedMeshData", k_ChunkSize);
            m_SharedMeshBuffer = new ComputeBufferWrapper<VertexData>("_SharedMeshData", k_ChunkSize);

            m_DeformedMeshData.PushDataToGlobal();
        }

        public void OnDestroy()
        {
            m_DeformedMeshData.Destroy();
            m_SharedMeshBuffer.Destroy();
        }

        public bool ResizeAndPushDeformMeshBuffersIfRequired(int requiredSize)
        {
            var size = m_DeformedMeshData.BufferSize;
            if (size <= requiredSize || size - requiredSize > k_ChunkSize)
            {
                var newSize = ((requiredSize / k_ChunkSize) + 1) * k_ChunkSize;
                m_DeformedMeshData.Resize(newSize);
                m_DeformedMeshData.PushDataToGlobal();
                return true;
            }

            return false;
        }

        public bool ResizeSharedMeshBuffersIfRequired(int requiredSize)
        {
            var size = m_SharedMeshBuffer.BufferSize;
            if (size <= requiredSize || size - requiredSize > k_ChunkSize)
            {
                var newSize = ((requiredSize / k_ChunkSize) + 1) * k_ChunkSize;
                m_SharedMeshBuffer.Resize(newSize);
                return true;
            }

            return false;
        }

        public void FetchMeshData(Mesh mesh, int index)
        {
            var positions = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;

            var vertices = new NativeArray<VertexData>(mesh.vertexCount, Allocator.Temp);

            for (int i = 0; i < mesh.vertexCount; i++)
            {
                var tan4 = tangents[i];
                vertices[i] = new VertexData
                {
                    Position = positions[i],
                    Normal = normals[i],
                    Tangent = new Vector3(tan4.x, tan4.y, tan4.z)
                };
            }

            m_SharedMeshBuffer.SetData(vertices, 0, index, vertices.Length);

            vertices.Dispose();
        }

        public void PushMeshData()
        {
            m_SharedMeshBuffer.PushDataToGlobal();
        }
    }
#endif
}
