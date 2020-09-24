#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System;
using Unity.Burst;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine.Rendering;
using Unity.Transforms;
using System.Collections.Generic;


namespace Unity.Rendering.Occlusion
{
    [BurstCompile]
    unsafe struct OcclusionSortMeshesJob : IJob
    {
        public NativeArray<OcclusionMesh> Meshes;

        
        struct Compare : IComparer<OcclusionMesh>
        {
            int IComparer<OcclusionMesh>.Compare(OcclusionMesh x, OcclusionMesh y)
            {
                return x.vertexCount.CompareTo(y.vertexCount);
            }
        }

        public void Execute()
        {
            if (Meshes.Length == 0)
                return;

            // TODO:  might want to do a proper parallel sort instead
            Meshes.Sort(new Compare());
        }
    }
}

#endif
