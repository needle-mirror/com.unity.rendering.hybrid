#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Unity.Rendering.Occlusion
{
    [BurstCompile]
    unsafe struct MOCMergeJob : IJob, IJobParallelFor
    {
        // We do not use [ReadOnly] here, we are not going to modify the content of the array (the ptrs) but we are modifying the content that those ptr point too.
        [NativeDisableParallelForRestriction]
        [NativeDisableUnsafePtrRestriction]
        public NativeArray<IntPtr> mocBurstIntrinsicPtrArray;

        public int indexMergingTo;
        public int totalJobCount;

        public void Execute()
        {
            for(int i = 0; i < totalJobCount; i++)
            {
                Execute(i);
            }
        }

        public void Execute(int index)
        {
            MOC.BurstIntrinsics* mocBurstIntrinsic = (MOC.BurstIntrinsics*)mocBurstIntrinsicPtrArray[indexMergingTo];

            uint tilesWidth;
            uint tilesHeight;
            mocBurstIntrinsic->GetResolutionTiles(out tilesWidth, out tilesHeight);

            int totalTiles = (int)(tilesWidth * tilesHeight);
            int tileCountPerJob = totalTiles / totalJobCount;

            for (int i = 0; i < mocBurstIntrinsicPtrArray.Length; i++)
            {
                if(i == indexMergingTo)
                {
                    continue;
                }
                MOC.BurstIntrinsics* mocBurstIntrinsicToMerge = (MOC.BurstIntrinsics*)mocBurstIntrinsicPtrArray[i];
                mocBurstIntrinsic->MergeBufferTile(mocBurstIntrinsicToMerge, index * tileCountPerJob, tileCountPerJob);
            }        
        }
    }
}

#endif
