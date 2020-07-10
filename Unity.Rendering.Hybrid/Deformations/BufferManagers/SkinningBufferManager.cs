using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.Rendering
{
    internal class SkinningBufferManager
    {
        const int k_ChunkSize = 2048;

#if ENABLE_COMPUTE_DEFORMATIONS
        ComputeBufferWrapper<BoneWeight> m_SharedBoneWeightsBuffer;
        ComputeBufferWrapper<uint2> m_InfluenceOffsetBuffer;

        ComputeShader m_ComputeShader;
        int m_Kernel;
#endif
        ComputeBufferWrapper<float3x4> m_SkinMatrices;

        public void OnCreate()
        {
#if ENABLE_COMPUTE_DEFORMATIONS
            m_ComputeShader = Resources.Load<ComputeShader>("SkinningComputeShader");
            Debug.Assert(m_ComputeShader != null, "Compute shader was not found!");

            m_Kernel = m_ComputeShader.FindKernel("SkinningComputeKernel");

            m_SharedBoneWeightsBuffer = new ComputeBufferWrapper<BoneWeight>("_SharedMeshBoneWeights", k_ChunkSize, m_ComputeShader);
            m_InfluenceOffsetBuffer = new ComputeBufferWrapper<uint2>("_InfluencesOffsetAndCount", k_ChunkSize, m_ComputeShader);
#endif
            m_SkinMatrices = new ComputeBufferWrapper<float3x4>("_SkinMatrices", k_ChunkSize);
        }

        public void OnDestroy()
        {
#if ENABLE_COMPUTE_DEFORMATIONS
            m_SharedBoneWeightsBuffer.Destroy();
            m_InfluenceOffsetBuffer.Destroy();
#endif
            m_SkinMatrices.Destroy();
        }

#if ENABLE_COMPUTE_DEFORMATIONS
        public bool ResizeSharedBufferIfRequired(int requiredBoneWeightsSize, int requiredVertexSize)
        {
            bool didResize = false;

            var offsetBufferSize = m_InfluenceOffsetBuffer.BufferSize;
            if (offsetBufferSize <= requiredVertexSize || offsetBufferSize - requiredVertexSize > k_ChunkSize)
            {
                var newVertexSize = ((requiredVertexSize / k_ChunkSize) + 1) * k_ChunkSize;
                m_InfluenceOffsetBuffer.Resize(newVertexSize);
                didResize = true;
            }

            var boneWeightsSize = m_SharedBoneWeightsBuffer.BufferSize;
            if (boneWeightsSize <= requiredBoneWeightsSize || boneWeightsSize - requiredBoneWeightsSize > k_ChunkSize)
            {
                var newBoneWeightsSize = ((requiredBoneWeightsSize / k_ChunkSize) + 1) * k_ChunkSize;
                m_SharedBoneWeightsBuffer.Resize(newBoneWeightsSize);
                didResize = true;
            }

            return didResize;
        }
#endif

        public bool ResizePassBufferIfRequired(int requiredSize)
        {
            var size = m_SkinMatrices.BufferSize;
            if (size <= requiredSize || size - requiredSize > k_ChunkSize)
            {
                var newSize = ((requiredSize / k_ChunkSize) + 1) * k_ChunkSize;
                m_SkinMatrices.Resize(newSize);
                return true;
            }

            return false;
        }

#if ENABLE_COMPUTE_DEFORMATIONS
        public void FetchMeshData(Mesh mesh, int meshOffset, int boneInfluenceOffset)
        {
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var vertexCount = bonesPerVertex.Length;

            var boneWeights = new NativeArray<BoneWeight>(weights.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var indexOffsets = new NativeArray<uint2>(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            int meshBoneOffset = 0;
            for (int vertexIndex = 0; vertexIndex < vertexCount; ++vertexIndex)
            {
                var boneInfluenceCount = bonesPerVertex[vertexIndex];

                for (int boneIndex = 0; boneIndex < boneInfluenceCount; ++boneIndex)
                {
                    int weightIndex = meshBoneOffset + boneIndex;
                    boneWeights[weightIndex] = new BoneWeight { Weight = weights[weightIndex].weight, Index = (uint)weights[weightIndex].boneIndex };
                }
                indexOffsets[vertexIndex] = new uint2((uint)boneInfluenceOffset + (uint)meshBoneOffset, boneInfluenceCount);

                meshBoneOffset += boneInfluenceCount;
            }

            m_SharedBoneWeightsBuffer.SetData(boneWeights, 0, boneInfluenceOffset, boneWeights.Length);
            m_InfluenceOffsetBuffer.SetData(indexOffsets, 0, meshOffset, indexOffsets.Length);

            boneWeights.Dispose();
            indexOffsets.Dispose();
        }

        public void PushSharedMeshData()
        {
            m_SharedBoneWeightsBuffer.PushDataToKernel(m_Kernel);
            m_InfluenceOffsetBuffer.PushDataToKernel(m_Kernel);
        }
#endif

        public void SetSkinMatrixData(NativeArray<float3x4> skinMatrices)
        {
            Debug.Assert(skinMatrices.Length <= m_SkinMatrices.BufferSize);
            m_SkinMatrices.SetData(skinMatrices, 0, 0, skinMatrices.Length);
        }

        public void PushDeformPassData()
        {
            m_SkinMatrices.PushDataToGlobal();
        }
    }
}
