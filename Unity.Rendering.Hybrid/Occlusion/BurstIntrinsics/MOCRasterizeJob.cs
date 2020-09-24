#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

// DS: temp dev config
//#define MOC_JOB_STATS

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Unity.Rendering.Occlusion
{
    [BurstCompile]
    unsafe struct MOCRasterizeJob : IJob, IJobParallelFor
    {
        // We do not use [ReadOnly] here, we are not going to modify the content of the array (the ptrs) but we are modifying the content that those ptr point to.
        [NativeDisableUnsafePtrRestriction]
        public NativeArray<IntPtr> mocBurstIntrinsicPtrArray;

        [ReadOnly]
        public NativeArray<OcclusionMesh> Meshes;

        public void Execute()
        {
            for (int i = 0; i < mocBurstIntrinsicPtrArray.Length; i++)
            {
                Execute(i);
            }
        }

        public void Execute(int index)
        {
            void* mocBurstIntrinsicPtr = (void*)mocBurstIntrinsicPtrArray[index];

            for (int i = index; i < Meshes.Length; i += mocBurstIntrinsicPtrArray.Length)
            {
                Rasterize(mocBurstIntrinsicPtr, Meshes[i]);
            }
        }

        private void Rasterize(void* mocBurstIntrinsicPtr, OcclusionMesh mesh)
        {
            MOC.BurstIntrinsics* mocBurstIntrinsic = (MOC.BurstIntrinsics*)mocBurstIntrinsicPtr;
            Debug.Assert(mocBurstIntrinsic != null);

            // Testing before rendering generally give good result, but it's a lot better if we are rasterize front to back
            MOC.CullingResult cullingResult = mocBurstIntrinsic->TestRect(
                mesh.screenMin.x, mesh.screenMin.y,
                mesh.screenMax.x, mesh.screenMax.y, mesh.screenMin.w);

            if (cullingResult == MOC.CullingResult.VISIBLE)
            {
                float* vertices = (float*)mesh.transformedVertexData.GetUnsafePtr();
                uint* indices = (uint*)mesh.indexData.GetUnsafePtr();

                Debug.Assert(mesh.indexCount % 3 == 0);

                cullingResult = mocBurstIntrinsic->RenderTriangles(vertices, indices, mesh.indexCount / 3);

            }
        }
    }
}

#endif
