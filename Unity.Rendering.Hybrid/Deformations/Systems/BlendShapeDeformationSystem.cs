using Unity.Entities;
using Unity.Profiling;
using UnityEngine;

namespace Unity.Rendering
{
#if ENABLE_COMPUTE_DEFORMATIONS
    public abstract class BlendShapeDeformationSystemBase : SystemBase
    {
        static readonly ProfilerMarker k_BlendShapeDeformationMarker = new ProfilerMarker("BlendShapeDeformationDispatch");

        ComputeShader m_ComputeShader;
        PushMeshDataSystemBase m_PushMeshDataSystem;

        int m_Kernel;

        int m_VertexCount;
        int m_SharedMeshStartIndex;
        int m_DeformedMeshStartIndex;
        int m_InstanceCount;
        int m_BlendShapeCount;
        int m_BlendShapeVertexStartIndex;
        int m_BlendShapeWeightStartIndex;

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
                ComponentType.ReadOnly<BlendShapeTag>(),
                ComponentType.ReadOnly<BlendWeightBufferIndex>()
            );

            m_ComputeShader = Resources.Load<ComputeShader>("BlendShapeComputeShader");
            Debug.Assert(m_ComputeShader != null, $"Compute shader for { typeof(BlendShapeDeformationSystemBase) } was not found!");

            m_PushMeshDataSystem = World.GetOrCreateSystem<PushMeshDataSystemBase>();
            Debug.Assert(m_PushMeshDataSystem != null, "PushMeshDataSystemBase was not found!");

            m_Kernel = m_ComputeShader.FindKernel("BlendShapeComputeKernel");

            m_VertexCount = Shader.PropertyToID("g_VertexCount");
            m_SharedMeshStartIndex = Shader.PropertyToID("g_SharedMeshStartIndex");
            m_DeformedMeshStartIndex = Shader.PropertyToID("g_DeformedMeshStartIndex");
            m_InstanceCount = Shader.PropertyToID("g_InstanceCount");
            m_BlendShapeCount = Shader.PropertyToID("g_BlendShapeCount");
            m_BlendShapeVertexStartIndex = Shader.PropertyToID("g_BlendShapeVertexStartIndex");
            m_BlendShapeWeightStartIndex = Shader.PropertyToID("g_BlendShapeWeightstartIndex");
        }

        protected override void OnUpdate()
        {
            k_BlendShapeDeformationMarker.Begin();

            foreach(var meshData in m_PushMeshDataSystem.UniqueSharedMeshData)
            {
                if (meshData.RenderMeshHash == 0)
                    continue;

                if (!meshData.HasBlendShapes)
                    continue;

                var sharedMeshBufferIndex = m_PushMeshDataSystem.MeshHashToSharedBuffer[meshData.RenderMeshHash];
                int instanceCount = m_PushMeshDataSystem.MeshHashToInstanceCount[meshData.RenderMeshHash];
                var deformedMeshIndex = m_PushMeshDataSystem.MeshHashToDeformedMeshIndex[meshData.RenderMeshHash];
                int offset = m_PushMeshDataSystem.MeshHashToBlendWeightIndex[meshData.RenderMeshHash];

                m_ComputeShader.SetInt(m_VertexCount, meshData.VertexCount);
                m_ComputeShader.SetInt(m_SharedMeshStartIndex, sharedMeshBufferIndex.GeometryIndex);
                m_ComputeShader.SetInt(m_DeformedMeshStartIndex, (int)deformedMeshIndex);
                m_ComputeShader.SetInt(m_InstanceCount, instanceCount);
                m_ComputeShader.SetInt(m_BlendShapeCount, meshData.BlendShapeCount);
                m_ComputeShader.SetInt(m_BlendShapeVertexStartIndex, sharedMeshBufferIndex.BlendShapeIndex);
                m_ComputeShader.SetInt(m_BlendShapeWeightStartIndex, offset);

                m_ComputeShader.Dispatch(m_Kernel, 1024, 1, 1);
            }

            k_BlendShapeDeformationMarker.End();
        }
    }
#endif
}
