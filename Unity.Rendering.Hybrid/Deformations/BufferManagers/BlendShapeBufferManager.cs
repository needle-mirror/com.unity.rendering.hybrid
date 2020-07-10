using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.Rendering
{
#if ENABLE_COMPUTE_DEFORMATIONS
    internal class BlendShapeBufferManager
    {
        const int k_ChunkSize = 2048;

        ComputeBufferWrapper<BlendShapeVertexDelta> m_BlendShapeVertexBuffer;
        ComputeBufferWrapper<uint2> m_BlendShapeOffsetBuffer;
        ComputeBufferWrapper<float> m_BlendWeights;

        ComputeShader m_ComputeShader;
        int m_Kernel;

        public void OnCreate()
        {
            m_ComputeShader = Resources.Load<ComputeShader>("BlendShapeComputeShader");
            Debug.Assert(m_ComputeShader != null, "BlendShapeCompute shader was not found!");

            m_Kernel = m_ComputeShader.FindKernel("BlendShapeComputeKernel");

            m_BlendShapeVertexBuffer = new ComputeBufferWrapper<BlendShapeVertexDelta>("_BlendShapeVertexDeltas", k_ChunkSize, m_ComputeShader);
            m_BlendShapeOffsetBuffer = new ComputeBufferWrapper<uint2>("_BlendShapeOffsetAndCount", k_ChunkSize, m_ComputeShader);
            m_BlendWeights = new ComputeBufferWrapper<float>("_BlendShapeWeights", k_ChunkSize);
        }

        public void OnDestroy()
        {
            m_BlendShapeVertexBuffer.Destroy();
            m_BlendShapeOffsetBuffer.Destroy();
            m_BlendWeights.Destroy();
        }

        public bool ResizeSharedBufferIfRequired(int requiredBlendShapeVertexSize, int requiredVertexSize)
        {
            var resized = false;
            var blendShapeSize = m_BlendShapeVertexBuffer.BufferSize;
            if (blendShapeSize <= requiredBlendShapeVertexSize || blendShapeSize - requiredBlendShapeVertexSize > k_ChunkSize)
            {
                var newSize = ((requiredBlendShapeVertexSize / k_ChunkSize) + 1) * k_ChunkSize;
                m_BlendShapeVertexBuffer.Resize(newSize);
                resized = true;
            }

            var vertexSize = m_BlendShapeOffsetBuffer.BufferSize;
            if (vertexSize <= requiredVertexSize || vertexSize - requiredVertexSize > k_ChunkSize)
            {
                var newSize = ((requiredVertexSize / k_ChunkSize) + 1) * k_ChunkSize;
                m_BlendShapeOffsetBuffer.Resize(newSize);
                resized = true;
            }

            return resized;
        }

        public bool ResizePassBufferIfRequired(int requiredSize)
        {
            var size = m_BlendWeights.BufferSize;
            if (size <= requiredSize || size - requiredSize > k_ChunkSize)
            {
                var newSize = ((requiredSize / k_ChunkSize) + 1) * k_ChunkSize;
                m_BlendWeights.Resize(newSize);
                return true;
            }

            return false;
        }

        public void FetchMeshData(Mesh mesh, int meshIndex, int blendShapeIndex)
        {
            var vertexCount = mesh.vertexCount;
            var blendShapeCount = mesh.blendShapeCount;

            var blendShapePositions = new Vector3[blendShapeCount][];
            var blendShapeNormals = new Vector3[blendShapeCount][];
            var blendShapeTangents = new Vector3[blendShapeCount][];

            int blendShapeVertexCount = 0;
            for (int i = 0; i < blendShapeCount; i++)
            {
                var positions = new Vector3[vertexCount];
                var normals = new Vector3[vertexCount];
                var tangents = new Vector3[vertexCount];

                mesh.GetBlendShapeFrameVertices(i, 0, positions, normals, tangents);

                blendShapePositions[i] = positions;
                blendShapeNormals[i] = normals;
                blendShapeTangents[i] = tangents;

                for (int j = 0; j < vertexCount; j++)
                {
                    if (!Mathf.Equals(positions[j], Vector3.zero) || !Mathf.Equals(normals[j], Vector3.zero) || !Mathf.Equals(tangents[j], Vector3.zero))
                        blendShapeVertexCount++;
                }
            }

            var blendShapeVertexDeltas = new NativeArray<BlendShapeVertexDelta>(blendShapeVertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var blendShapeOffset = new NativeArray<uint2>(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var sparseBlendShapeVertexCount = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                var vertexInfluenceCount = 0;
                for (int j = 0; j < blendShapeCount; j++)
                {
                    if (!Mathf.Equals(blendShapePositions[j][i], Vector3.zero) || !Mathf.Equals(blendShapeNormals[j][i], Vector3.zero) || !Mathf.Equals(blendShapeTangents[j][i], Vector3.zero))
                    {
                        var vertexDelta = new BlendShapeVertexDelta
                        {
                            BlendShapeIndex = j,
                            Position = blendShapePositions[j][i],
                            Normal = blendShapeNormals[j][i],
                            Tangent = blendShapeTangents[j][i],
                        };

                        blendShapeVertexDeltas[sparseBlendShapeVertexCount + vertexInfluenceCount] = vertexDelta;
                        vertexInfluenceCount++;
                    }
                }

                blendShapeOffset[i] = new uint2((uint)sparseBlendShapeVertexCount, (uint)vertexInfluenceCount);
                sparseBlendShapeVertexCount += vertexInfluenceCount;
            }

            m_BlendShapeVertexBuffer.SetData(blendShapeVertexDeltas, 0, blendShapeIndex, blendShapeVertexDeltas.Length);
            m_BlendShapeOffsetBuffer.SetData(blendShapeOffset, 0, meshIndex, blendShapeOffset.Length);

            blendShapeVertexDeltas.Dispose();
        }

        public int CalculateSparseBlendShapeVertexCount(Mesh mesh)
        {
            var blendShapeCount = mesh.blendShapeCount;
            var vertexCount = mesh.vertexCount;

            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var tangents = new Vector3[vertexCount];

            int count = 0;
            for (int i = 0; i < blendShapeCount; i++)
            {
                mesh.GetBlendShapeFrameVertices(i, 0, positions, normals, tangents);

                for (int j = 0; j < vertexCount; j++)
                {
                    if (!Mathf.Equals(positions[j], Vector3.zero) || !Mathf.Equals(normals[j], Vector3.zero) || !Mathf.Equals(tangents[j], Vector3.zero))
                        count++;
                }
            }

            return count;
        }

        public void PushSharedMeshData()
        {
            m_BlendShapeVertexBuffer.PushDataToKernel(m_Kernel);
            m_BlendShapeOffsetBuffer.PushDataToKernel(m_Kernel);
        }

        public void SetBlendWeightData(NativeArray<float> blendWeights)
        {
            Debug.Assert(blendWeights.Length <= m_BlendWeights.BufferSize);
            m_BlendWeights.SetData(blendWeights, 0, 0, blendWeights.Length);
        }

        public void PushDeformPassData()
        {
            m_BlendWeights.PushDataToGlobal();
        }
    }
#endif
}
