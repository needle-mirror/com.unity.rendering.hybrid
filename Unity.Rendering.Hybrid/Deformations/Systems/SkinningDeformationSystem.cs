using Unity.Entities;
using Unity.Profiling;
using UnityEngine;

namespace Unity.Rendering
{
#if ENABLE_COMPUTE_DEFORMATIONS
    public abstract class SkinningDeformationSystemBase : SystemBase
    {
        static readonly ProfilerMarker k_SkinningDeformationMarker = new ProfilerMarker("SkinningDeformationDispatch");

        ComputeShader m_ComputeShader;
        PushMeshDataSystemBase m_PushMeshDataSystem;

        int m_Kernel;

        int m_VertexCount;
        int m_SharedMeshStartIndex;
        int m_DeformedMeshStartIndex;
        int m_InstancesCount;
        int m_SharedMeshBoneCount;
        int m_SkinMatricesStartIndex;

        EntityQuery m_Query;

        protected override void OnCreate()
        {
#if ENABLE_COMPUTE_DEFORMATIONS
            if (!UnityEngine.SystemInfo.supportsComputeShaders)
            {
                Enabled = false;
                return;
            }
#endif

            m_Query = GetEntityQuery(
                ComponentType.ReadOnly<SharedMeshData>(),
                ComponentType.ReadOnly<SkinningTag>(),
                ComponentType.ReadOnly<SkinMatrixBufferIndex>()
            );

            m_ComputeShader = Resources.Load<ComputeShader>("SkinningComputeShader");
            Debug.Assert(m_ComputeShader != null, $"Compute shader for { typeof(SkinningDeformationSystemBase) } was not found!");

            m_PushMeshDataSystem = World.GetOrCreateSystem<PushMeshDataSystemBase>();
            Debug.Assert(m_PushMeshDataSystem != null, "PushMeshDataSystemBase was not found!");

            m_Kernel = m_ComputeShader.FindKernel("SkinningComputeKernel");

            m_VertexCount = Shader.PropertyToID("g_VertexCount");
            m_SharedMeshStartIndex = Shader.PropertyToID("g_SharedMeshStartIndex");
            m_DeformedMeshStartIndex = Shader.PropertyToID("g_DeformedMeshStartIndex");
            m_InstancesCount = Shader.PropertyToID("g_InstanceCount");
            m_SharedMeshBoneCount = Shader.PropertyToID("g_SharedMeshBoneCount");
            m_SkinMatricesStartIndex = Shader.PropertyToID("g_SkinMatricesStartIndex");
        }

        protected override void OnUpdate()
        {
            k_SkinningDeformationMarker.Begin();

            foreach (var meshData in m_PushMeshDataSystem.UniqueSharedMeshData)
            {
                if (meshData.RenderMeshHash == 0)
                    continue;

                if (!meshData.HasSkinning)
                    continue;

                var sharedMeshBufferIndex = m_PushMeshDataSystem.MeshHashToSharedBuffer[meshData.RenderMeshHash];
                int instanceCount = m_PushMeshDataSystem.MeshHashToInstanceCount[meshData.RenderMeshHash];
                var deformedMeshIndex = m_PushMeshDataSystem.MeshHashToDeformedMeshIndex[meshData.RenderMeshHash];
                int offset = m_PushMeshDataSystem.MeshHashToSkinMatrixIndex[meshData.RenderMeshHash];

                m_ComputeShader.SetInt(m_VertexCount, meshData.VertexCount);
                m_ComputeShader.SetInt(m_SharedMeshStartIndex, sharedMeshBufferIndex.GeometryIndex);
                m_ComputeShader.SetInt(m_DeformedMeshStartIndex, (int)deformedMeshIndex);
                m_ComputeShader.SetInt(m_InstancesCount, instanceCount);
                m_ComputeShader.SetInt(m_SharedMeshBoneCount, meshData.BoneCount);
                m_ComputeShader.SetInt(m_SkinMatricesStartIndex, offset);

                m_ComputeShader.Dispatch(m_Kernel, 1024, 1, 1);
            }

            k_SkinningDeformationMarker.End();
        }
    }
#endif
}
