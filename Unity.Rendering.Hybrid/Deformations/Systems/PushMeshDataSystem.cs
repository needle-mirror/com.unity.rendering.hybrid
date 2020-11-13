using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Profiling;

namespace Unity.Rendering
{
    public abstract partial class PushMeshDataSystemBase : SystemBase
    {
        static readonly ProfilerMarker k_PushSharedMeshData = new ProfilerMarker("Push SharedMeshData");
        static readonly ProfilerMarker k_MaterialPropertyUpdate = new ProfilerMarker("Update DeformedMeshBufferIndex Material Property");

        EntityQuery m_RemovedMeshQuery;
        EntityQuery m_AddedMeshQuery;
        EntityQuery m_BufferIndexQuery;

        ResizeBuffersSystem m_ResizeBuffersSystem;
        internal int SkinMatrixCount { get { return m_ResizeBuffersSystem.totalSkinMatrixCount; } }

        internal NativeHashMap<int, int> MeshHashToSkinMatrixIndex;
        internal NativeHashMap<int, int> MeshHashToInstanceCount;

        internal List<SharedMeshData> UniqueSharedMeshData { get; private set; }

        internal SkinningBufferManager SkinningBufferManager { get; private set; }

        bool m_RebuildDeformedMeshBuffers;

#if ENABLE_COMPUTE_DEFORMATIONS
        PushSharedMeshDataSystem m_PushSharedMeshDataSystem;

        internal int BlendShapeWeightCount { get { return m_ResizeBuffersSystem.totalBlendShapeWeightCount; } }

        Dictionary<int, RenderMesh> m_MeshHashToRenderMesh;

        internal NativeHashMap<int, SharedMeshBufferIndex> MeshHashToSharedBuffer;
        internal NativeHashMap<int, uint> MeshHashToDeformedMeshIndex;
        internal NativeHashMap<int, int> MeshHashToBlendWeightIndex;

        internal BlendShapeBufferManager BlendShapeBufferManager { get; private set; }
        internal MeshBufferManager MeshBufferManager { get; private set; }

        bool m_RebuildSharedMeshBuffers;
#endif

        protected override void OnCreate()
        {
#if ENABLE_COMPUTE_DEFORMATIONS
            if (!UnityEngine.SystemInfo.supportsComputeShaders)
            {
                UnityEngine.Debug.LogWarning("Warning: Current platform does not support compute shaders. Compute shaders are required for Compute Deformations. Compute Deformation systems will be disabled.");
                Enabled = false;
                return;
            }
#endif

            m_RemovedMeshQuery = GetEntityQuery(
                ComponentType.ReadOnly<SharedMeshData>(),
                ComponentType.Exclude<RenderMesh>(),
                ComponentType.Exclude<DeformedEntity>()
            );

            m_AddedMeshQuery = GetEntityQuery(
                ComponentType.ReadOnly<RenderMesh>(),
                ComponentType.ReadOnly<DeformedEntity>(),
                ComponentType.Exclude<SharedMeshData>()
            );

            var query = new EntityQueryDesc
            {
                All = new ComponentType[] {
#if ENABLE_COMPUTE_DEFORMATIONS
                    typeof(DeformedMeshIndex),
#endif
                    ComponentType.ReadOnly<RenderMesh>(),
                    ComponentType.ReadOnly<SharedMeshData>() },
                Any = new ComponentType[] {
#if ENABLE_COMPUTE_DEFORMATIONS
                    typeof(BlendWeightBufferIndex),
#endif
                    typeof(SkinMatrixBufferIndex) }
            };

            m_BufferIndexQuery = GetEntityQuery(query);

            m_ResizeBuffersSystem = World.GetOrCreateSystem<ResizeBuffersSystem>();
            m_ResizeBuffersSystem.Parent = this;

#if ENABLE_COMPUTE_DEFORMATIONS
            m_PushSharedMeshDataSystem = World.GetOrCreateSystem<PushSharedMeshDataSystem>();
            m_PushSharedMeshDataSystem.Parent = this;

            MeshHashToSharedBuffer = new NativeHashMap<int, SharedMeshBufferIndex>(64, Allocator.Persistent);
            MeshHashToDeformedMeshIndex = new NativeHashMap<int, uint>(64, Allocator.Persistent);
            MeshHashToBlendWeightIndex = new NativeHashMap<int, int>(64, Allocator.Persistent);
            m_MeshHashToRenderMesh = new Dictionary<int, RenderMesh>(64);

            MeshBufferManager = new MeshBufferManager();
            MeshBufferManager.OnCreate();

            BlendShapeBufferManager = new BlendShapeBufferManager();
            BlendShapeBufferManager.OnCreate();

            m_RebuildSharedMeshBuffers = true;
#endif
            MeshHashToInstanceCount = new NativeHashMap<int, int>(64, Allocator.Persistent);
            MeshHashToSkinMatrixIndex = new NativeHashMap<int, int>(64, Allocator.Persistent);

            UniqueSharedMeshData = new List<SharedMeshData>();

            SkinningBufferManager = new SkinningBufferManager();
            SkinningBufferManager.OnCreate();

            m_RebuildDeformedMeshBuffers = true;
        }

        protected override void OnDestroy()
        {
            if (UniqueSharedMeshData != null)
                UniqueSharedMeshData.Clear();

            if(MeshHashToInstanceCount.IsCreated)
                MeshHashToInstanceCount.Dispose();

            if(MeshHashToSkinMatrixIndex.IsCreated)
                MeshHashToSkinMatrixIndex.Dispose();

#if ENABLE_COMPUTE_DEFORMATIONS
            if(MeshHashToSharedBuffer.IsCreated)
                MeshHashToSharedBuffer.Dispose();

            if(MeshHashToDeformedMeshIndex.IsCreated)
                MeshHashToDeformedMeshIndex.Dispose();

            if(MeshHashToBlendWeightIndex.IsCreated)
                MeshHashToBlendWeightIndex.Dispose();

            if(m_MeshHashToRenderMesh != null)
                m_MeshHashToRenderMesh.Clear();

            if(MeshBufferManager != null)
                MeshBufferManager.OnDestroy();

            if (BlendShapeBufferManager != null)
                BlendShapeBufferManager.OnDestroy();
#endif
            if(SkinningBufferManager != null)
                SkinningBufferManager.OnDestroy();
        }

        protected override void OnUpdate()
        {
            m_ResizeBuffersSystem.Update();

#if ENABLE_COMPUTE_DEFORMATIONS
            k_PushSharedMeshData.Begin();

            if (m_RebuildSharedMeshBuffers)
                m_PushSharedMeshDataSystem.Update();

            k_PushSharedMeshData.End();
#endif

            if (m_RebuildDeformedMeshBuffers)
            {
                k_MaterialPropertyUpdate.Begin();

                // Layout Deformed Meshes in buffer
#if ENABLE_COMPUTE_DEFORMATIONS
                MeshHashToDeformedMeshIndex.Clear();
                MeshHashToBlendWeightIndex.Clear();
                uint deformedMeshOffset = 0;
                int blendShapeOffset = 0;
#endif
                MeshHashToSkinMatrixIndex.Clear();
                int skinMatrixOffset = 0;

                foreach (var meshData in UniqueSharedMeshData)
                {
                    if (meshData.RenderMeshHash == 0)
                        continue;

                    int instanceCount = MeshHashToInstanceCount[meshData.RenderMeshHash];

#if ENABLE_COMPUTE_DEFORMATIONS
                    MeshHashToDeformedMeshIndex.Add(meshData.RenderMeshHash, deformedMeshOffset);
                    deformedMeshOffset += (uint)instanceCount * (uint)meshData.VertexCount;

                    if (meshData.HasBlendShapes)
                    {
                        MeshHashToBlendWeightIndex.Add(meshData.RenderMeshHash, blendShapeOffset);
                        blendShapeOffset += instanceCount * meshData.BlendShapeCount;
                    }
#endif

                    if (meshData.HasSkinning)
                    {
                        MeshHashToSkinMatrixIndex.Add(meshData.RenderMeshHash, skinMatrixOffset);
                        skinMatrixOffset += instanceCount * meshData.BoneCount;
                    }
                }

                // Write deformed mesh index to material property
#if ENABLE_COMPUTE_DEFORMATIONS
                var deformedMeshIndexType = GetComponentTypeHandle<DeformedMeshIndex>();
                var blendWeightIndexType = GetComponentTypeHandle<BlendWeightBufferIndex>();
#endif
                var skinMatrixIndexType = GetComponentTypeHandle<SkinMatrixBufferIndex>();
                var sharedMeshDataType = GetSharedComponentTypeHandle<SharedMeshData>();

                m_BufferIndexQuery.CompleteDependency();


                using (var chunks = m_BufferIndexQuery.CreateArchetypeChunkArray(Allocator.TempJob))
                {
                    var skinMatrixInstancesMap = new NativeHashMap<int, int>(chunks.Length, Allocator.Temp);
#if ENABLE_COMPUTE_DEFORMATIONS
                    var deformedMeshInstancesMap = new NativeHashMap<int, int>(chunks.Length, Allocator.Temp);
                    var blendShapeWeightInstancesMap = new NativeHashMap<int, int>(chunks.Length, Allocator.Temp);
#endif
                    foreach (var chunk in chunks)
                    {
                        var sharedMeshData = chunk.GetSharedComponentData(sharedMeshDataType, EntityManager);
#if ENABLE_COMPUTE_DEFORMATIONS
                        deformedMeshInstancesMap.TryGetValue(sharedMeshData.RenderMeshHash, out int count);

                        var deformedMeshIndices = chunk.GetNativeArray(deformedMeshIndexType);
                        var deformedMeshIndex = MeshHashToDeformedMeshIndex[sharedMeshData.RenderMeshHash];
                        for (int i = 0; i < chunk.Count; i++)
                        {
                            var index = deformedMeshIndex + count + (i * sharedMeshData.VertexCount);
                            deformedMeshIndices[i] = new DeformedMeshIndex { Value = (uint)index };
                        }
                        if (count == 0)
                            deformedMeshInstancesMap.Add(sharedMeshData.RenderMeshHash, chunk.Count * sharedMeshData.VertexCount);
                        else
                            deformedMeshInstancesMap[sharedMeshData.RenderMeshHash] += chunk.Count * sharedMeshData.VertexCount;

                        if (sharedMeshData.HasBlendShapes)
                        {
                            blendShapeWeightInstancesMap.TryGetValue(sharedMeshData.RenderMeshHash, out int instanceCount);
                            var blendWeightIndices = chunk.GetNativeArray(blendWeightIndexType);
                            int blendShapeIndex = MeshHashToBlendWeightIndex[sharedMeshData.RenderMeshHash];

                            for (int i = 0; i < chunk.Count; i++)
                            {
                                var index = blendShapeIndex + instanceCount + (i * sharedMeshData.BlendShapeCount);
                                blendWeightIndices[i] = new BlendWeightBufferIndex { Value = index };
                            }

                            if (instanceCount == 0)
                                blendShapeWeightInstancesMap.Add(sharedMeshData.RenderMeshHash, chunk.Count * sharedMeshData.BlendShapeCount);
                            else
                                blendShapeWeightInstancesMap[sharedMeshData.RenderMeshHash] += chunk.Count * sharedMeshData.BlendShapeCount;
                        }
#endif

                        if (sharedMeshData.HasSkinning)
                        {
                            skinMatrixInstancesMap.TryGetValue(sharedMeshData.RenderMeshHash, out int instanceCount);
                            var skinMatrixIndices = chunk.GetNativeArray(skinMatrixIndexType);
                            int skinMatrixIndex = MeshHashToSkinMatrixIndex[sharedMeshData.RenderMeshHash];

                            for (int i = 0; i < chunk.Count; i++)
                            {
                                var index = skinMatrixIndex + instanceCount + (i * sharedMeshData.BoneCount);
                                skinMatrixIndices[i] = new SkinMatrixBufferIndex { Value = index };
                            }

                            if (instanceCount == 0)
                                skinMatrixInstancesMap.Add(sharedMeshData.RenderMeshHash, chunk.Count * sharedMeshData.BoneCount);
                            else
                                skinMatrixInstancesMap[sharedMeshData.RenderMeshHash] += chunk.Count * sharedMeshData.BoneCount;
                        }
                    }

                    skinMatrixInstancesMap.Dispose();
#if ENABLE_COMPUTE_DEFORMATIONS
                    deformedMeshInstancesMap.Dispose();
                    blendShapeWeightInstancesMap.Dispose();
#endif

                    m_RebuildDeformedMeshBuffers = false;
                }

                k_MaterialPropertyUpdate.End();
            }
        }
    }
}
