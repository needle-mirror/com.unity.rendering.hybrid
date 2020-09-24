#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using Unity.Burst;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine.Rendering;
using Unity.Jobs;

namespace Unity.Rendering.Occlusion
{
    [BurstCompile]
    unsafe struct OcclusionCompactBatchesJob : IJob
    {
        [NativeDisableParallelForRestriction]
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<int> IndexList;

        [NativeDisableContainerSafetyRestriction]
        public NativeArray<BatchVisibility> Batches;

        public void Execute()
        {
            int batchCount = Batches.Length;

            if (batchCount == 0)
                return;

            var batch = (BatchVisibility*)Batches.GetUnsafeReadOnlyPtr();
            var indexBase = (int*)IndexList.GetUnsafePtr();

            for (int i = 0; i < batchCount; i++, batch++)
            {
                int writePos = 0;

                var indices = indexBase + batch->offset;
                for (int readPos = 0; readPos < batch->visibleCount; readPos++)
                {
                    int current = indices[readPos];
                    indices[writePos] = current;
                    writePos += (current >= 0 ? 1 : 0);
                }

                batch->visibleCount = writePos;
            }
        }
    }
}
#endif
