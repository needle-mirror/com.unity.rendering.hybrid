using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Profiling;
using Unity.Deformations;
using Debug = UnityEngine.Debug;

namespace Unity.Rendering
{
#if ENABLE_COMPUTE_DEFORMATIONS
    public abstract class PrepareBlendWeightSystemBase : JobComponentSystem
    {
        static readonly ProfilerMarker k_Marker = new ProfilerMarker("PrepareBlendWeightSystemBase");

        EntityQuery m_Query;
        EntityQuery m_BlendShapeTagQuery;

        NativeArray<float> m_AllBlendWeights;
        PushMeshDataSystemBase m_PushMeshDataSystem;

        protected override void OnCreate()
        {
            if (!UnityEngine.SystemInfo.supportsComputeShaders)
            {
                Enabled = false;
                return;
            }

            m_Query = GetEntityQuery(
                ComponentType.ReadOnly<BlendShapeWeight>()
            );

            m_BlendShapeTagQuery = GetEntityQuery(
                ComponentType.ReadOnly<BlendShapeTag>()
                );

            m_PushMeshDataSystem = World.GetOrCreateSystem<PushMeshDataSystemBase>();
            Debug.Assert(m_PushMeshDataSystem != null, "PushMeshDataSystemBase system was not found in the world!");
        }

        protected override void OnDestroy()
        {
            if (m_AllBlendWeights.IsCreated)
                m_AllBlendWeights.Dispose();
        }

        protected override JobHandle OnUpdate(JobHandle dependency)
        {
            k_Marker.Begin();

            if (m_AllBlendWeights.Length != m_PushMeshDataSystem.BlendShapeWeightCount)
            {
                if (m_AllBlendWeights.IsCreated)
                    m_AllBlendWeights.Dispose();

                m_AllBlendWeights = new NativeArray<float>(m_PushMeshDataSystem.BlendShapeWeightCount, Allocator.Persistent);
            }

            var deformedEntityToComputeIndex = new NativeMultiHashMap<Entity, int>(m_BlendShapeTagQuery.CalculateEntityCount(), Allocator.TempJob);
            var deformedEntityToComputeIndexParallel = deformedEntityToComputeIndex.AsParallelWriter();
            var hashMapDeps = Entities
                .WithName("ConstructHashMap")
                .ForEach((in BlendWeightBufferIndex index, in DeformedEntity deformedEntity) =>
                    {
                        deformedEntityToComputeIndexParallel.Add(deformedEntity.Value, index.Value);
                    }).Schedule(new JobHandle());

            dependency = JobHandle.CombineDependencies(dependency, hashMapDeps);

            var blendShapeWeightsBuffer = m_AllBlendWeights;
            dependency = Entities
                .WithName("FlattenBlendShapeWeights")
                .WithNativeDisableContainerSafetyRestriction(blendShapeWeightsBuffer)
                .WithReadOnly(deformedEntityToComputeIndex)
                .ForEach((ref DynamicBuffer<BlendShapeWeight> weights, in Entity entity) =>
                {
                    if (!deformedEntityToComputeIndex.ContainsKey(entity))
                        return;

                    var length = weights.Length * UnsafeUtility.SizeOf<float>();
                    var indices = deformedEntityToComputeIndex.GetValuesForKey(entity);

                    while (indices.MoveNext())
                    {
                        unsafe
                        {
                            UnsafeUtility.MemCpy(
                                (float*)blendShapeWeightsBuffer.GetUnsafePtr() + indices.Current,
                                weights.GetUnsafePtr(),
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
            m_PushMeshDataSystem.BlendShapeBufferManager.SetBlendWeightData(m_AllBlendWeights);
            m_PushMeshDataSystem.BlendShapeBufferManager.PushDeformPassData();
        }
    }

    public abstract class FinalizePushBlendWeightSystemBase : SystemBase
    {
        EntityQuery m_Query;

        protected override void OnCreate()
        {
            if (!UnityEngine.SystemInfo.supportsComputeShaders)
            {
                Enabled = false;
                return;
            }

            m_Query = GetEntityQuery(
                ComponentType.ReadWrite<BlendShapeWeight>()
            );
        }

        protected abstract PrepareBlendWeightSystemBase PrepareBlendShapeSystem { get; }

        protected override void OnUpdate()
        {
            if (PrepareBlendShapeSystem != null)
            {
                CompleteDependency();
                PrepareBlendShapeSystem.AssignGlobalBufferToShader();
            }
        }
    }
#endif
}
