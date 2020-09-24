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
    unsafe struct MOCSetResolutionJob : IJob, IJobParallelFor
    {
        // We do not use [ReadOnly] here, we are not going to modify the content of the array (the ptrs) but we are modifying the content that those ptrs point too.
        [NativeDisableUnsafePtrRestriction]
        public NativeArray<IntPtr> mocBurstIntrinsicPtrArray;

        public int wantedDepthWidth;
        public int wantedDepthHeight;
        public float wantedNearClipValue;

        public void Execute()
        {
            for(int i = 0; i < mocBurstIntrinsicPtrArray.Length; i++)
            {
                Execute(i);
            }
        }

        public void Execute(int index)
        {
            uint currentDepthWidth = 0;
            uint currentDepthHeight = 0;
            float currentNearPlane = 0;


            MOC.BurstIntrinsics* mocBurstIntrinsic = (MOC.BurstIntrinsics*)mocBurstIntrinsicPtrArray[index];

            mocBurstIntrinsic->GetResolution(out currentDepthWidth, out currentDepthHeight);
            
            if (currentDepthWidth != (uint)wantedDepthWidth
                || currentDepthHeight != (uint)wantedDepthHeight)
            {
                //This will free the buffer before allocating a new one
                mocBurstIntrinsic->SetResolution((uint)wantedDepthWidth, (uint)wantedDepthHeight);
            }


            currentNearPlane = mocBurstIntrinsic->GetNearClipPlane();
            if (currentNearPlane != wantedNearClipValue)
            {
                mocBurstIntrinsic->SetNearClipPlane(wantedNearClipValue);
            }

            
        }
    }
}

#endif
