using Unity.Collections;
using Unity.Entities;
using Unity.Profiling;
using Debug = UnityEngine.Debug;

namespace Unity.Rendering
{
    public abstract partial class PushMeshDataSystemBase : SystemBase
    {
        [DisableAutoCreation]
        internal partial class ResizeBuffersSystem : SystemBase
        {
            static readonly ProfilerMarker k_ChangesMarker = new ProfilerMarker("Detect Entity Changes");
            static readonly ProfilerMarker k_ResizeMarker = new ProfilerMarker("Resize Shared Mesh Buffer");
            static readonly ProfilerMarker k_OutputBuffer = new ProfilerMarker("Resize Deformed Mesh Buffer");
            static readonly ProfilerMarker k_OutputCountBuffer = new ProfilerMarker("Counting Deformed Mesh Buffer");
            static readonly ProfilerMarker k_OutputResizeBuffer = new ProfilerMarker("Pushing Deformed Mesh Buffer");

            public PushMeshDataSystemBase Parent;

            public int totalSkinMatrixCount { get; private set; }
#if ENABLE_COMPUTE_DEFORMATIONS
            public int totalBlendShapeWeightCount { get; private set; }

            public int totalSharedVertexCount { get; private set; }
            public int totalDeformedVertexCount { get; private set; }
            public int totalBlendshapeVertexCount { get; private set; }
            public int totalBoneWeightCount { get; private set; }
#endif

            EntityQuery m_SharedMeshQuery;
            EntityQuery m_RenderMeshQuery;

            NativeHashMap<int, SharedMeshData> m_MeshHashToSharedMeshData;

            protected override void OnCreate()
            {
                m_SharedMeshQuery = GetEntityQuery(
                    ComponentType.ReadOnly<SharedMeshData>()
                );

                m_RenderMeshQuery = GetEntityQuery(
                    ComponentType.ReadOnly<RenderMesh>(),
                    ComponentType.ReadOnly<DeformedEntity>()
                );

                m_MeshHashToSharedMeshData = new NativeHashMap<int, SharedMeshData>(64, Allocator.Persistent);
            }

            protected override void OnDestroy()
            {
                m_MeshHashToSharedMeshData.Dispose();
            }

            protected override void OnUpdate()
            {
                k_ChangesMarker.Begin();

                bool changeDetected = false;

                // Detect removed SharedMeshes
                Entities
                    .WithNone<RenderMesh, DeformedEntity>()
                    .WithStructuralChanges()
                    .ForEach((Entity e, SharedMeshData meshData) =>
                    {
                        Parent.MeshHashToInstanceCount[meshData.RenderMeshHash]--;
                        var count = Parent.MeshHashToInstanceCount[meshData.RenderMeshHash];

                        // Clean-up if this was the last instance
                        if (count == 0)
                        {
                            Parent.MeshHashToInstanceCount.Remove(meshData.RenderMeshHash);
                            m_MeshHashToSharedMeshData.Remove(meshData.RenderMeshHash);
#if ENABLE_COMPUTE_DEFORMATIONS
                            Parent.m_MeshHashToRenderMesh.Remove(meshData.RenderMeshHash);

                            totalSharedVertexCount -= meshData.VertexCount;
                            totalBlendshapeVertexCount -= meshData.HasBlendShapes ? meshData.BlendShapeVertexCount : 0;
                            totalBoneWeightCount -= meshData.HasSkinning ? meshData.BoneInfluencesCount : 0;
#endif
                        }

#if ENABLE_COMPUTE_DEFORMATIONS
                        if (meshData.HasBlendShapes)
                            EntityManager.RemoveComponent<BlendWeightBufferIndex>(e);
#endif

                        if (meshData.HasSkinning)
                            EntityManager.RemoveComponent<SkinMatrixBufferIndex>(e);

                        EntityManager.RemoveComponent<SharedMeshData>(e);
#if ENABLE_COMPUTE_DEFORMATIONS
                        EntityManager.RemoveComponent<DeformedMeshIndex>(e);
#endif

                        changeDetected = true;
                    })
                    .Run();

                // Detect new SharedMeshes
                Entities
                    .WithAll<DeformedEntity>()
                    .WithNone<SharedMeshData>()
                    .WithStructuralChanges()
                    .ForEach((Entity e, RenderMesh renderMesh) =>
                    {
                        if (m_MeshHashToSharedMeshData.TryGetValue(renderMesh.GetHashCode(), out var meshData))
                        {
#if ENABLE_COMPUTE_DEFORMATIONS
                            EntityManager.AddComponent<DeformedMeshIndex>(e);

                            if (meshData.HasBlendShapes)
                                EntityManager.AddComponent<BlendWeightBufferIndex>(e);
#endif

                            if (meshData.HasSkinning)
                                EntityManager.AddComponent<SkinMatrixBufferIndex>(e);

                            EntityManager.AddSharedComponentData(e, meshData);
                            Parent.MeshHashToInstanceCount[meshData.RenderMeshHash]++;
                        }
                        else
                        {
                            var mesh = renderMesh.mesh;
                            var meshHash = renderMesh.GetHashCode();
                            meshData = new SharedMeshData
                            {
                                VertexCount = mesh.vertexCount,
                                BlendShapeCount = mesh.blendShapeCount,
#if ENABLE_COMPUTE_DEFORMATIONS
                                BlendShapeVertexCount = mesh.blendShapeCount > 0 ? Parent.BlendShapeBufferManager.CalculateSparseBlendShapeVertexCount(mesh) : 0,
#else
                                BlendShapeVertexCount = 0,
#endif
                                BoneCount = mesh.bindposes.Length,
                                BoneInfluencesCount = mesh.GetAllBoneWeights().Length,
                                RenderMeshHash = meshHash,
                            };

#if ENABLE_COMPUTE_DEFORMATIONS
                            EntityManager.AddComponent<DeformedMeshIndex>(e);

                            if (meshData.HasBlendShapes)
                                EntityManager.AddComponent<BlendWeightBufferIndex>(e);
#endif

                            if (meshData.HasSkinning)
                                EntityManager.AddComponent<SkinMatrixBufferIndex>(e);

                            EntityManager.AddSharedComponentData(e, meshData);

                            Parent.MeshHashToInstanceCount.Add(meshHash, 1);
                            m_MeshHashToSharedMeshData.Add(meshHash, meshData);
#if ENABLE_COMPUTE_DEFORMATIONS
                            Parent.m_MeshHashToRenderMesh.Add(meshHash, renderMesh);

                            totalSharedVertexCount += meshData.VertexCount;
                            totalBlendshapeVertexCount += meshData.HasBlendShapes ? meshData.BlendShapeVertexCount : 0;
                            totalBoneWeightCount += meshData.HasSkinning ? meshData.BoneInfluencesCount : 0;

                            // A new shared mesh was detected. Force the buffers to be uploaded.
                            Parent.m_RebuildSharedMeshBuffers = true;
#endif
                        }

                        changeDetected = true;
                    }).Run();

#if ENABLE_COMPUTE_DEFORMATIONS
                // Sanity check for desired SharedMesh data sizes
                Debug.Assert(totalSharedVertexCount >= 0);
                Debug.Assert(totalBlendshapeVertexCount >= 0);
                Debug.Assert(totalBoneWeightCount >= 0);
#endif

                k_ChangesMarker.End();

                // Early exit if no changes are detected
                if (!changeDetected)
                    return;

                k_ResizeMarker.Begin();

#if ENABLE_COMPUTE_DEFORMATIONS
                Parent.m_RebuildSharedMeshBuffers |= Parent.MeshBufferManager.ResizeSharedMeshBuffersIfRequired(totalSharedVertexCount);
                Parent.m_RebuildSharedMeshBuffers |= Parent.SkinningBufferManager.ResizeSharedBufferIfRequired(totalBoneWeightCount, totalSharedVertexCount);
                Parent.m_RebuildSharedMeshBuffers |= Parent.BlendShapeBufferManager.ResizeSharedBufferIfRequired(totalBlendshapeVertexCount, totalSharedVertexCount);
#endif

                k_ResizeMarker.End();
                k_OutputBuffer.Begin();

                k_OutputCountBuffer.Begin();
#if ENABLE_COMPUTE_DEFORMATIONS
                int deformedMeshVertexCount = 0;
                int blendShapeWeightCount = 0;
#endif
                int skinMatrixCount = 0;

                m_SharedMeshQuery.CompleteDependency();

                Parent.UniqueSharedMeshData.Clear();
                EntityManager.GetAllUniqueSharedComponentData(Parent.UniqueSharedMeshData);
                foreach (var meshData in Parent.UniqueSharedMeshData)
                {
                    if (meshData.RenderMeshHash == 0)
                        continue;

                    int instanceCount = Parent.MeshHashToInstanceCount[meshData.RenderMeshHash];
#if ENABLE_COMPUTE_DEFORMATIONS
                    deformedMeshVertexCount += instanceCount * meshData.VertexCount;

                    if (meshData.HasBlendShapes)
                        blendShapeWeightCount += instanceCount * meshData.BlendShapeCount;
#endif

                    if (meshData.HasSkinning)
                        skinMatrixCount += instanceCount * meshData.BoneCount;
                }

#if ENABLE_COMPUTE_DEFORMATIONS
                totalDeformedVertexCount = deformedMeshVertexCount;
                totalBlendShapeWeightCount = blendShapeWeightCount;
#endif
                totalSkinMatrixCount = skinMatrixCount;

                k_OutputCountBuffer.End();

                k_OutputResizeBuffer.Begin();

                Parent.SkinningBufferManager.ResizePassBufferIfRequired(totalSkinMatrixCount);
#if ENABLE_COMPUTE_DEFORMATIONS
                Parent.BlendShapeBufferManager.ResizePassBufferIfRequired(totalBlendshapeVertexCount);
                Parent.MeshBufferManager.ResizeAndPushDeformMeshBuffersIfRequired(totalDeformedVertexCount);
#endif
                k_OutputResizeBuffer.End();

                // Force the DeformedMesh layout to be updated.
                // As either; an deformed mesh has been removed, or a new one has been added.
                // Both result in a shift of indices.
                Parent.m_RebuildDeformedMeshBuffers = true;

                k_OutputBuffer.End();
            }
        }
    }
}
