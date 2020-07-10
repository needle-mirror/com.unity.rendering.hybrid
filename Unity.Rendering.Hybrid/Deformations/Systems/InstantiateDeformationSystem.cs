using Unity.Entities;
using Unity.Profiling;
using UnityEngine;

namespace Unity.Rendering
{
#if ENABLE_COMPUTE_DEFORMATIONS
    public abstract class InstantiateDeformationSystemBase : SystemBase
    {
        static readonly ProfilerMarker k_InstantiateDeformationMarker = new ProfilerMarker("InstantiateDeformationSystem");

        ComputeShader m_ComputeShader;
        PushMeshDataSystemBase m_PushMeshDataSystem;

        int m_kernel;

        int m_VertexCount;
        int m_SharedMeshStartIndex;
        int m_DeformedMeshStartIndex;
        int m_InstancesCount;

        EntityQuery m_Query;

        protected override void OnCreate()
        {
            if (!UnityEngine.SystemInfo.supportsComputeShaders)
            {
                Enabled = false;
                return;
            }

            m_Query = GetEntityQuery(
                ComponentType.ReadOnly<SharedMeshData>(),
                ComponentType.ReadOnly<DeformedMeshIndex>()
            );

            m_ComputeShader = Resources.Load<ComputeShader>("InstantiateDeformationData");
            Debug.Assert(m_ComputeShader != null, $"Compute shader for { typeof(InstantiateDeformationSystemBase) } was not found!");

            m_PushMeshDataSystem = World.GetOrCreateSystem<PushMeshDataSystemBase>();
            Debug.Assert(m_PushMeshDataSystem != null, "PushMeshDataSystemBase was not found!");

            m_kernel = m_ComputeShader.FindKernel("InstantiateDeformationDataKernel");

            m_VertexCount = Shader.PropertyToID("g_VertexCount");
            m_SharedMeshStartIndex = Shader.PropertyToID("g_SharedMeshStartIndex");
            m_DeformedMeshStartIndex = Shader.PropertyToID("g_DeformedMeshStartIndex");
            m_InstancesCount = Shader.PropertyToID("g_InstanceCount");
        }

        protected override void OnUpdate()
        {
            k_InstantiateDeformationMarker.Begin();

            foreach (var meshData in m_PushMeshDataSystem.UniqueSharedMeshData)
            {
                if (meshData.RenderMeshHash == 0)
                    continue;

                var sharedMeshBufferIndex = m_PushMeshDataSystem.MeshHashToSharedBuffer[meshData.RenderMeshHash];
                int instanceCount = m_PushMeshDataSystem.MeshHashToInstanceCount[meshData.RenderMeshHash];
                var deformedMeshIndex = m_PushMeshDataSystem.MeshHashToDeformedMeshIndex[meshData.RenderMeshHash];

                m_ComputeShader.SetInt(m_VertexCount, meshData.VertexCount);
                m_ComputeShader.SetInt(m_DeformedMeshStartIndex, (int)deformedMeshIndex);
                m_ComputeShader.SetInt(m_SharedMeshStartIndex, sharedMeshBufferIndex.GeometryIndex);
                m_ComputeShader.SetInt(m_InstancesCount, instanceCount);

                m_ComputeShader.Dispatch(m_kernel, 1024, 1, 1);
            }

            k_InstantiateDeformationMarker.End();
        }
    }
#endif
}
