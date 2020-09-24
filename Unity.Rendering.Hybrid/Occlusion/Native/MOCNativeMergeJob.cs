#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;
using System;

#if UNITY_MOC_NATIVE_AVAILABLE

namespace Unity.Rendering.Occlusion
{
    [BurstCompile]
    unsafe struct MOCNativeMergeJob : IJob, IJobParallelFor
    {
        // We do not use [ReadOnly] here, we are not going to modify the content of the array (the ptrs) but we are modifying the content that those ptr point too.
        [NativeDisableParallelForRestriction]
        [NativeDisableUnsafePtrRestriction]
        public NativeArray<IntPtr> mocNativePtrArray;

        public int indexMergingTo;
        public int totalJobCount;

        public void Execute()
        {
            for (int i = 0; i < totalJobCount; i++)
            {
                Execute(i);
            }
        }

        public void Execute(int index)
        {
            void* mocNative = (void*)mocNativePtrArray[indexMergingTo];

            uint tilesWidth;
            uint tilesHeight;
            INTEL_MOC.MOCNative.GetResolutionTiles(mocNative, out tilesWidth, out tilesHeight);

            int totalTiles = (int)(tilesWidth * tilesHeight);
            int tileCountPerJob = totalTiles / totalJobCount;

            for (int i = 0; i < mocNativePtrArray.Length; i++)
            {
                if (i == indexMergingTo)
                {
                    continue;
                }
                void* mocNativeToMerge = (void*)mocNativePtrArray[i];
                INTEL_MOC.MOCNative.MergeBufferTile(mocNative, mocNativeToMerge, index * tileCountPerJob, tileCountPerJob);
            }
        }
    }
}

#endif

#endif
