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
    unsafe struct MOCClearJob : IJobParallelFor
    {
        // We do not use [ReadOnly] here, we are not going to modify the content of the array (the ptrs) but we are modifying the content that those ptrs point too.
        [NativeDisableUnsafePtrRestriction]
        public NativeArray<IntPtr> mocBurstIntrinsicPtrArray;

        public void Execute(int index)
        {
            MOC.BurstIntrinsics * mocBurstIntrinsic = (MOC.BurstIntrinsics*)mocBurstIntrinsicPtrArray[index];
            Debug.Assert(mocBurstIntrinsic != null);


            mocBurstIntrinsic->ClearBuffer();

        }
    }
}

#endif
