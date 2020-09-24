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
    unsafe struct MOCNativeRasterizeJob : IJob,IJobParallelFor
    {
        // We do not use [ReadOnly] here, we are not going to modify the content of the array (the ptrs) but we are modifying the content that those ptr point too.
        [NativeDisableUnsafePtrRestriction]
        public NativeArray<IntPtr> mocNativeArray;

        [ReadOnly] public NativeArray<OcclusionMesh> Meshes;

        public void Execute(int index)
        {
            void* mocNative =  (void*)mocNativeArray[index];       

            for (int i = index; i < Meshes.Length; i += mocNativeArray.Length)
            {
                Rasterize(mocNative, Meshes[i]);
            }
        }

        public void Execute()
        {
            for(int i = 0; i < mocNativeArray.Length; i++)
            {
                Execute(i);
            }
        }

        private void Rasterize(void* mocNative, OcclusionMesh mesh)
        {
            //Testing before rendering generally give good result, but it's a lot better if we are rasterize front to back

            INTEL_MOC.CullingResult cullingResult = INTEL_MOC.CullingResult.VISIBLE;

            cullingResult = INTEL_MOC.MOCNative.TestRect(
                mocNative,
                mesh.screenMin.x, mesh.screenMin.y,
                mesh.screenMax.x, mesh.screenMax.y, mesh.screenMin.w);

            if (cullingResult == INTEL_MOC.CullingResult.VISIBLE)
            {
                float* vertices = (float*)mesh.transformedVertexData.GetUnsafePtr();
                uint* indices = (uint*)mesh.indexData.GetUnsafePtr();

                Debug.Assert(mesh.indexCount % 3 == 0);

                INTEL_MOC.MOCNative.RenderTriangles(mocNative, vertices, indices, mesh.indexCount / 3);
            }
        }
    }
}

#endif

#endif
