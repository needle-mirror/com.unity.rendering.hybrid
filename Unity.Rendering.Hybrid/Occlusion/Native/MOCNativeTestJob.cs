#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_MOC_NATIVE_AVAILABLE

namespace Unity.Rendering.Occlusion
{
    [BurstCompile]
    unsafe struct MOCNativeTestJob : IJobChunk
    {
        [ReadOnly]
        [NativeDisableUnsafePtrRestriction]
        public NativeArray<IntPtr> mocNativePtrArray;
        public int mocNativeIndexToUse;

        [ReadOnly] public NativeArray<int> InternalToExternalRemappingTable;

        [ReadOnly] public ComponentTypeHandle<HybridChunkInfo> HybridChunkInfo;
        [ReadOnly] public ComponentTypeHandle<OcclusionTest> OcclusionTest;

        [NativeDisableParallelForRestriction] public NativeArray<int> IndexList;
        [NativeDisableParallelForRestriction] public NativeArray<BatchVisibility> Batches;

#if UNITY_EDITOR
        [NativeDisableUnsafePtrRestriction]
        public CullingStats* Stats;

#pragma warning disable 649
        [NativeSetThreadIndex]
        public int ThreadIndex;
#pragma warning restore 649

        public bool displayOccluded;
#endif

        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            var chunkInfo = chunk.GetChunkComponentData(HybridChunkInfo);
            if (!chunkInfo.Valid)
                return;

            var occlusionTestArray = chunk.GetNativeArray(OcclusionTest);

            var chunkData = chunkInfo.CullingData;
            int internalBatchIndex = chunkInfo.InternalIndex;
            int externalBatchIndex = InternalToExternalRemappingTable[internalBatchIndex];

            var batch = Batches[externalBatchIndex];

            int batchOutputOffset = chunkData.StartIndex;
            int batchOutputCount = 0;

            var indices = (int*)IndexList.GetUnsafeReadOnlyPtr() + chunkData.StartIndex;


            void* mocNative = (void*)mocNativePtrArray[mocNativeIndexToUse];

            for (var entityIndex = 0; entityIndex < chunkData.Visible; entityIndex++)
            {
                // TODO:  we could reuse the HLOD logic from the frustum culling code here, but
                // this would require some supporting structures and components.  For now, we just
                // test what was written into the index list.
                var index = indices[entityIndex] - chunkData.BatchOffset;
                var test = occlusionTestArray[index];

                if (!test.enabled)
                {
                    batchOutputCount++;
                    continue;
                }
				
                INTEL_MOC.CullingResult cullingResult = INTEL_MOC.CullingResult.VISIBLE;


                cullingResult = INTEL_MOC.MOCNative.TestRect(
                    mocNative,
                    test.screenMin.x, test.screenMin.y,
                    test.screenMax.x, test.screenMax.y, test.screenMin.w);


                bool visible = (cullingResult == INTEL_MOC.CullingResult.VISIBLE);

#if UNITY_EDITOR
                visible = (visible != displayOccluded);
#endif
                int advance = visible ? 1 : 0;

#if UNITY_EDITOR
                ref var stats = ref Stats[ThreadIndex];
                stats.Stats[CullingStats.kCountOcclusionCulled] += (1 - advance);
                stats.Stats[CullingStats.kCountOcclusionInput]++;
#endif

                if (!visible)
                {
                    indices[entityIndex] = -1;
                }
                batchOutputCount += advance;
            }
        }
    }
}

#endif

#endif
