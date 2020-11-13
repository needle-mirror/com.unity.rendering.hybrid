using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Deformations;
using Debug = UnityEngine.Debug;

namespace Unity.Rendering
{
    public abstract partial class PrepareSkinMatrixSystemBase : JobComponentSystem
    {
        static readonly ProfilerMarker k_Marker = new ProfilerMarker("PrepareSkinMatrixSystemBase");

        EntityQuery m_Query;
        EntityQuery m_SkinningTagQuery; 

        NativeArray<float3x4> m_AllSkinMatrices;
        PushMeshDataSystemBase m_PushMeshDataSystem;

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
                ComponentType.ReadOnly<SkinMatrix>()
            );

            m_SkinningTagQuery = GetEntityQuery(
                ComponentType.ReadOnly<SkinningTag>()
                );

            m_PushMeshDataSystem = World.GetOrCreateSystem<PushMeshDataSystemBase>();
            Debug.Assert(m_PushMeshDataSystem != null, "PushMeshDataSystemBase system was not found in the world!");
        }

        protected override void OnDestroy()
        {
            if (m_AllSkinMatrices.IsCreated)
                m_AllSkinMatrices.Dispose();
        }

        protected override JobHandle OnUpdate(JobHandle dependency)
        {
            k_Marker.Begin();

            // Resize SkinMatrices array
            if (m_AllSkinMatrices.Length != m_PushMeshDataSystem.SkinMatrixCount)
            {
                if (m_AllSkinMatrices.IsCreated)
                    m_AllSkinMatrices.Dispose();

                m_AllSkinMatrices = new NativeArray<float3x4>(m_PushMeshDataSystem.SkinMatrixCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            var deformedEntityToComputeIndex = new NativeMultiHashMap<Entity, int>(m_SkinningTagQuery.CalculateEntityCount(), Allocator.TempJob);
            var deformedEntityToComputeIndexParallel = deformedEntityToComputeIndex.AsParallelWriter();
            var hashMapDeps = Entities
                .WithName("ConstructHashMap")
                .ForEach((in SkinMatrixBufferIndex index, in DeformedEntity deformedEntity) =>
                    {
                        deformedEntityToComputeIndexParallel.Add(deformedEntity.Value, index.Value);
                    }).Schedule(new JobHandle());

            dependency = JobHandle.CombineDependencies(dependency, hashMapDeps);

            var skinMatricesBuffer = m_AllSkinMatrices;
            dependency = Entities
                .WithName("FlattenSkinMatrices")
                .WithNativeDisableContainerSafetyRestriction(skinMatricesBuffer)
                .WithReadOnly(deformedEntityToComputeIndex)
                .ForEach((ref DynamicBuffer<SkinMatrix> skinMatrices, in Entity entity) =>
                {
                    if (!deformedEntityToComputeIndex.ContainsKey(entity))
                        return;

                    long length = skinMatrices.Length * UnsafeUtility.SizeOf<float3x4>();
                    var indices = deformedEntityToComputeIndex.GetValuesForKey(entity);

                    while (indices.MoveNext())
                    {
                        unsafe
                        {
                            UnsafeUtility.MemCpy(
                                (float3x4*)skinMatricesBuffer.GetUnsafePtr() + indices.Current,
                                skinMatrices.GetUnsafePtr(),
                                length
                            );
                        }
                    }
                }).Schedule(dependency);

            dependency = deformedEntityToComputeIndex.Dispose(dependency);

            k_Marker.End();
            return dependency;
        }

        internal void AssignGlobalBufferToShader()
        {
            Debug.Assert(m_PushMeshDataSystem != null, "PushMeshDataSystemBase has not been assigned!");
            m_PushMeshDataSystem.SkinningBufferManager.SetSkinMatrixData(m_AllSkinMatrices);
            m_PushMeshDataSystem.SkinningBufferManager.PushDeformPassData();
        }
    }

    public abstract class FinalizePushSkinMatrixSystemBase : SystemBase
    {
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
                ComponentType.ReadWrite<SkinMatrix>()
            );
        }

        protected abstract PrepareSkinMatrixSystemBase PrepareSkinMatrixSystem { get; }

        protected override void OnUpdate()
        {
            if (PrepareSkinMatrixSystem != null)
            {
                CompleteDependency();
                PrepareSkinMatrixSystem.AssignGlobalBufferToShader();
            }
        }
    }
}
