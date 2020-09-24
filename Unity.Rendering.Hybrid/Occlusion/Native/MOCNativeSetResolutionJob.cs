#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

#if UNITY_MOC_NATIVE_AVAILABLE

namespace Unity.Rendering.Occlusion
{
    [BurstCompile]
    unsafe struct MOCNativeSetResolutionJob : IJobParallelFor
    {
        // We do not use [ReadOnly] here, we are not going to modify the content of the array (the ptrs) but we are modifying the content that those ptrs point too.
        public NativeArray<IntPtr> mocNativeArray;

        public int wantedDepthWidth;
        public int wantedDepthHeight;

        public float wantedNearClipValue;

        public void Execute(int index)
        {
            uint currentDepthWidth = 0;
            uint currentDepthHeight = 0;
            float currentNearPlane = 0;

            void* mocNative = (void*)mocNativeArray[index];

            INTEL_MOC.MOCNative.GetResolution(mocNative, out currentDepthWidth, out currentDepthHeight);
            INTEL_MOC.MOCNative.GetNearClipPlane(mocNative, out currentNearPlane);

            if (currentDepthWidth != (uint)wantedDepthWidth
                || currentDepthHeight != (uint)wantedDepthHeight)
            {
                INTEL_MOC.MOCNative.SetResolution(mocNative, (uint)wantedDepthWidth, (uint)wantedDepthHeight);
            }


            if(currentNearPlane != wantedNearClipValue)
            {
                INTEL_MOC.MOCNative.SetNearClipPlane(mocNative, wantedNearClipValue);
            }

        }
    }
}

#endif

#endif
