#if ENABLE_UNITY_OCCLUSION && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

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
    unsafe struct MOCNativeClearJob : IJobParallelFor
    {
        // We do not use [ReadOnly] here, we are not going to modify the content of the array (the ptrs) but we are modifying the content that those ptrs point too.
        public NativeArray<IntPtr> mocNativeArray;

        public void Execute(int index)
        {
            INTEL_MOC.MOCNative.ClearBuffer((void*)(mocNativeArray[index]));
        }
    }
}

#endif

#endif
