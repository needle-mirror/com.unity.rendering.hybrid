#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;
using Unity.Rendering.Occlusion;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Rendering.Occlusion
{
    public unsafe class OcclusionCulling
    {
        [BurstCompile]
        struct ResetDepthJob : IJobParallelFor
        {
            public NativeArray<float> depthBuffer;
            public void Execute(int index)
            {
                depthBuffer[index] = 1.0f;
            }
        }

        public void Create(EntityManager entityManager)
        {
            m_OccluderGroup = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<WorldRenderBounds>(),
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ChunkComponentReadOnly<HybridChunkInfo>(),
                    ComponentType.ReadOnly<OcclusionMesh>(),
                },
            });

            m_ProxyOccluderGroup = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<OcclusionMesh>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<RenderMesh>(),
                }
            });

            m_OcclusionTestTransformGroup = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<RenderBounds>(),
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<OcclusionTest>(),
                },
            });

            m_OcclusionTestGroup = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ChunkComponentReadOnly<HybridChunkInfo>(),
                    ComponentType.ReadOnly<OcclusionTest>(),
                },
            });

            //We rasterize multiple depthBuffer in parallel and then merge them together.
            //For a 512x512 depth buffer the memory size is around 100KB per depth buffer
            //after a certain amount of parallel job we stop getting performance gain when scaling so for now we limit the amount of depth buffer available to 10 to limit our memory usage
            int mocCount = Math.Min(10, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerMaximumCount);

            m_BurstIntrinsicsArray = new NativeArray<IntPtr>(mocCount, Allocator.Persistent);

            for (int i = 0; i < m_BurstIntrinsicsArray.Length; i++)
            {
                Occlusion.MOC.BurstIntrinsics* mocBurstIntrinsics = (MOC.BurstIntrinsics*)Memory.Unmanaged.Allocate(sizeof(MOC.BurstIntrinsics), 64, Allocator.Persistent);
                mocBurstIntrinsics->Create((uint)m_MOCDepthSize, (uint)m_MOCDepthSize);

                m_BurstIntrinsicsArray[i] = (IntPtr)mocBurstIntrinsics;
            }


#if UNITY_MOC_NATIVE_AVAILABLE
            m_MocNativeArray = new NativeArray<IntPtr>(mocCount, Allocator.Persistent);
            for(int i = 0; i < m_MocNativeArray.Length; i++)
            {
                // TODO: make chosable, propagate to settings
                void* mocNative = INTEL_MOC.MOCNative.CreateMOC(
                    //INTEL_MOC.WantedImplementation.AUTO
                    //INTEL_MOC.WantedImplementation.SSE2
                    INTEL_MOC.WantedImplementation.SSE41
                );

               m_MocNativeArray[i] = (IntPtr)mocNative;
            }
#endif
        }

        public void Dispose()
        {
            for (int i = 0; i < m_BurstIntrinsicsArray.Length; i++)
            {
                Occlusion.MOC.BurstIntrinsics* mocBurstIntrinsics = (Occlusion.MOC.BurstIntrinsics*)m_BurstIntrinsicsArray[i];
                mocBurstIntrinsics->Destroy();

                Memory.Unmanaged.Free(mocBurstIntrinsics, Allocator.Persistent);
            }
            m_BurstIntrinsicsArray.Dispose();

#if UNITY_MOC_NATIVE_AVAILABLE
            for (int i = 0; i < m_MocNativeArray.Length; i++)
            {
                INTEL_MOC.MOCNative.DestroyMOC((void*)(m_MocNativeArray[i]));
                m_MocNativeArray[i] = IntPtr.Zero;
            }
            m_MocNativeArray.Dispose();
#endif
        }

#if UNITY_EDITOR
        public JobHandle Cull(EntityManager entityManager, NativeArray<int> InternalToExternalIds, BatchCullingContext cullingContext, JobHandle cullingJobDependency, CullingStats* cullingStats)
#else
        public JobHandle Cull(EntityManager entityManager, NativeArray<int> InternalToExternalIds, BatchCullingContext cullingContext, JobHandle cullingJobDependency)
#endif
        {
            var occlusionSettings = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();
            if (!occlusionSettings.OcclusionEnabled)
                return new JobHandle();

            var occlusionMeshes = new NativeList<OcclusionMesh>(Allocator.TempJob);

            var proxyOcclusionMeshTransformJob = new ProxyOcclusionMeshTransformJob
            {
                OcclusionMeshComponent = entityManager.GetComponentTypeHandle<OcclusionMesh>(false),
                LocalToWorldComponent = entityManager.GetComponentTypeHandle<LocalToWorld>(true),
                OcclusionMeshes = occlusionMeshes,
                ViewProjection = cullingContext.cullingMatrix,
            };

            //ScheduleSingle is required because we populate a list
            var proxyOcclusionMeshTransformJobHandle = proxyOcclusionMeshTransformJob.ScheduleSingle(m_ProxyOccluderGroup, cullingJobDependency);
            cullingJobDependency = JobHandle.CombineDependencies(proxyOcclusionMeshTransformJobHandle, cullingJobDependency);

            var occlusionMeshTransformJob = new OcclusionMeshTransformJob
            {
                InternalToExternalRemappingTable = InternalToExternalIds,
                HybridChunkInfo = entityManager.GetComponentTypeHandle<HybridChunkInfo>(true),
                OcclusionMeshComponent = entityManager.GetComponentTypeHandle<OcclusionMesh>(false),
                LocalToWorldComponent = entityManager.GetComponentTypeHandle<LocalToWorld>(true),
                IndexList = cullingContext.visibleIndices,
                Batches = cullingContext.batchVisibility,
                OcclusionMeshes = occlusionMeshes,
                ViewProjection = cullingContext.cullingMatrix,
#if UNITY_EDITOR
                Stats = cullingStats,
#endif
            };

            //ScheduleSingle is required because we populate a list
            var occlusionTransformJobHandle = occlusionMeshTransformJob.ScheduleSingle(m_OccluderGroup, proxyOcclusionMeshTransformJobHandle);
            cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, occlusionTransformJobHandle);

            var occlusionComputeBoundsJob = new OcclusionComputeBoundsJob
            {
                ViewProjection = cullingContext.cullingMatrix,
                BoundsComponent = entityManager.GetComponentTypeHandle<RenderBounds>(true),
                LocalToWorld = entityManager.GetComponentTypeHandle<LocalToWorld>(true),
                OcclusionTest = entityManager.GetComponentTypeHandle<OcclusionTest>(false),
            };

            var occlusionComputeBoundsJobHandle = occlusionComputeBoundsJob.Schedule(m_OcclusionTestTransformGroup, occlusionTransformJobHandle);
            cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, occlusionComputeBoundsJobHandle);

            var occlusionMeshArray = occlusionMeshes.AsDeferredJobArray();
            var occlusionSortJob = new OcclusionSortMeshesJob
            {
                Meshes = occlusionMeshArray,
            };
            var occlusionSortJobHandle = occlusionSortJob.Schedule(occlusionComputeBoundsJobHandle);
            cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, occlusionSortJobHandle);


            if (occlusionSettings.MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Intrinsic)
            {

                var mocSetResolutionJob = new MOCSetResolutionJob
                {
                    mocBurstIntrinsicPtrArray = m_BurstIntrinsicsArray,
                    wantedDepthWidth = m_MOCDepthSize,
                    wantedDepthHeight = m_MOCDepthSize,
                    wantedNearClipValue = cullingContext.nearPlane, //TODO: use  when we handle correctly the debug view with multiple call to OnPerformCulling
                };
                var mocSetResolutionJobHandle = mocSetResolutionJob.Schedule(cullingJobDependency);// mocSetResolutionJob.Schedule(m_BurstIntrinsicsArray.Length, 1, cullingJobDependency);
                cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, mocSetResolutionJobHandle);


                var mocClearJob = new MOCClearJob
                {
                    mocBurstIntrinsicPtrArray = m_BurstIntrinsicsArray,
                };

                var mocClearJobHandle = mocClearJob.Schedule(m_BurstIntrinsicsArray.Length, 1, cullingJobDependency);
                cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, mocClearJobHandle);



                var mocRasterizejob = new MOCRasterizeJob
                {
                    mocBurstIntrinsicPtrArray = m_BurstIntrinsicsArray,
                    Meshes = occlusionMeshArray,
                };

                var mocRasterizeJobHandle = occlusionSettings.OcclusionParallelEnabled ? mocRasterizejob.Schedule(m_BurstIntrinsicsArray.Length, 1, cullingJobDependency) : mocRasterizejob.Schedule(cullingJobDependency);

                cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, mocRasterizeJobHandle);


                //The amount of jobs to run should be based on our tilesWidth or height, but the MOCSetResolutionJob might modify our buffer size and we don't want to force a sync point
                int totalMergeJob = 10;

                var mocMergeJob = new MOCMergeJob
                {
                    mocBurstIntrinsicPtrArray = m_BurstIntrinsicsArray,
                    indexMergingTo = 0,
                    totalJobCount = totalMergeJob,
                };
                var mocMergeJobHandle = occlusionSettings.OcclusionParallelEnabled ?
                                        mocMergeJob.Schedule(totalMergeJob, 1, cullingJobDependency)
                                        : mocMergeJob.Schedule(cullingJobDependency);
                cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, mocMergeJobHandle);



                var mocTestJob = new MOCTestJob()
                {
                    mocBurstIntrinsicPtrArray = m_BurstIntrinsicsArray,
                    burstIntrinsicIndexToUse = 0,
                    OcclusionTest = entityManager.GetComponentTypeHandle<OcclusionTest>(true),
                    HybridChunkInfo = entityManager.GetComponentTypeHandle<HybridChunkInfo>(true),
                    IndexList = cullingContext.visibleIndices,
                    Batches = cullingContext.batchVisibility,
                    InternalToExternalRemappingTable = InternalToExternalIds,

#if UNITY_EDITOR
                    displayOccluded = occlusionSettings.DisplayOccluded,
                    Stats = cullingStats,
#endif
                };

                var mocTestJobHandle = mocTestJob.Schedule(m_OcclusionTestGroup, cullingJobDependency);
                cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, mocTestJobHandle);


            }
#if UNITY_MOC_NATIVE_AVAILABLE
            else if (occlusionSettings.MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Native)
            {
                var mocSetResolutionJob = new MOCNativeSetResolutionJob
                {
                    mocNativeArray = m_MocNativeArray,
                    wantedDepthWidth = m_MOCDepthSize,
                    wantedDepthHeight = m_MOCDepthSize,
                    wantedNearClipValue = cullingContext.nearPlane, //TODO: use cullingContext.nearPlane when we handle correctly the debug view with multiple call to OnPerformCulling
                };
                var mocSetResolutionJobHandle = mocSetResolutionJob.Schedule(m_BurstIntrinsicsArray.Length, 1, cullingJobDependency);
                cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, mocSetResolutionJobHandle);

                var mocClearJob = new MOCNativeClearJob
                {
                    mocNativeArray = m_MocNativeArray,
                };

                var mocClearJobHandle = mocClearJob.Schedule(m_MocNativeArray.Length, 1, cullingJobDependency);
                cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, mocClearJobHandle);
                //mocClearJobHandle.Complete();

                var mocRasterizejob = new MOCNativeRasterizeJob
                {
                    mocNativeArray = m_MocNativeArray,
                    Meshes = occlusionMeshArray,
                };

                var mocRasterizeJobHandle = occlusionSettings.OcclusionParallelEnabled ? mocRasterizejob.Schedule(m_MocNativeArray.Length, 1, cullingJobDependency) : mocRasterizejob.Schedule(cullingJobDependency);

                cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, mocRasterizeJobHandle);

                //The amount of jobs to run should be based on our tilesWidth or height, but the MOCSetResolutionJob might modify our buffer size and we don't want to force a sync point
                int totalMergeJob = 10;
                var mocMergeJob = new MOCNativeMergeJob
                {
                    mocNativePtrArray = m_MocNativeArray,
                    indexMergingTo = 0,
                    totalJobCount = totalMergeJob,
                };
                var mocMergeJobHandle = occlusionSettings.OcclusionParallelEnabled ?
                                        mocMergeJob.Schedule(totalMergeJob, 1, cullingJobDependency)
                                        : mocMergeJob.Schedule(cullingJobDependency);
                cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, mocMergeJobHandle);

                var mocTestJob = new MOCNativeTestJob()
                {
                    mocNativePtrArray = m_MocNativeArray,
                    mocNativeIndexToUse = 0,
                    InternalToExternalRemappingTable = InternalToExternalIds,
                    OcclusionTest = entityManager.GetComponentTypeHandle<OcclusionTest>(true),
                    HybridChunkInfo = entityManager.GetComponentTypeHandle<HybridChunkInfo>(true),
                    IndexList = cullingContext.visibleIndices,
                    Batches = cullingContext.batchVisibility,
#if UNITY_EDITOR
                    displayOccluded = occlusionSettings.DisplayOccluded,
                    Stats = cullingStats,
#endif
                };

                var mocTestJobHandle = mocTestJob.Schedule(m_OcclusionTestGroup, cullingJobDependency);
                cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, mocTestJobHandle);
            }
#endif

            var occlusionCompactBatchesJob = new OcclusionCompactBatchesJob
            {
                IndexList = cullingContext.visibleIndices,
                Batches = cullingContext.batchVisibility
            };

            var occlusionCompactBatchesJobHandle = occlusionCompactBatchesJob.Schedule(cullingJobDependency);
            cullingJobDependency = JobHandle.CombineDependencies(cullingJobDependency, occlusionCompactBatchesJobHandle);

            occlusionMeshes.Dispose(cullingJobDependency);

            //We give a pointer to first depth buffer to the debugSystem, by the time it use it all the depth buffers would have been merged into the first one
            var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();

#if UNITY_MOC_NATIVE_AVAILABLE
            debugSystem.RenderMOCInstances(m_MocNativeArray,
                                        m_BurstIntrinsicsArray,
                                        occlusionSettings.MocOcclusionMode, cullingJobDependency);
#else
            debugSystem.RenderMOCInstances(m_BurstIntrinsicsArray,
                                        occlusionSettings.MocOcclusionMode, cullingJobDependency);
#endif

            return cullingJobDependency;
        }
        private EntityQuery m_OccluderGroup;
        private EntityQuery m_ProxyOccluderGroup;
        private EntityQuery m_OcclusionTestTransformGroup;
        private EntityQuery m_OcclusionTestGroup;

#if UNITY_MOC_NATIVE_AVAILABLE
        private NativeArray<IntPtr> m_MocNativeArray;
#endif

        private NativeArray<IntPtr> m_BurstIntrinsicsArray;

        static readonly int m_MOCDepthSize = 512;

    }
}

#endif
