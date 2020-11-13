#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using Unity.Burst;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine.Rendering;
using Unity.Transforms;
using Unity.Jobs;

namespace Unity.Rendering.Occlusion
{
    [BurstCompile]
    unsafe struct OcclusionComputeBoundsJob : IJobChunk
    {
        [ReadOnly] public ComponentTypeHandle<RenderBounds> BoundsComponent;
        [ReadOnly] public ComponentTypeHandle<LocalToWorld> LocalToWorld;
        public ComponentTypeHandle<OcclusionTest> OcclusionTest;

        [ReadOnly] public float4x4 ViewProjection;

        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            var bounds = chunk.GetNativeArray(BoundsComponent);
            var localToWorld = chunk.GetNativeArray(LocalToWorld);
            var tests = chunk.GetNativeArray(OcclusionTest);

            var verts = stackalloc float4[16];

            var edges = stackalloc int2[]
            {
                new int2(0,1), new int2(1,3), new int2(3,2), new int2(2,0),
                new int2(4,6), new int2(6,7), new int2(7,5), new int2(5,4),
                new int2(4,0), new int2(2,6), new int2(1,5), new int2(7,3)
            };

            for (var entityIndex = 0; entityIndex < chunk.Count; entityIndex++)
            {
                var aabb = bounds[entityIndex].Value;
                var occlusionTest = tests[entityIndex];
                var local = localToWorld[entityIndex].Value;

                // TODO:  optimize
                occlusionTest.screenMin = float.MaxValue;
                occlusionTest.screenMax = -float.MaxValue;

                var mvp = math.mul(ViewProjection, local);

                float4x2 u = new float4x2(mvp.c0 * aabb.Min.x, mvp.c0 * aabb.Max.x);
                float4x2 v = new float4x2(mvp.c1 * aabb.Min.y, mvp.c1 * aabb.Max.y);
                float4x2 w = new float4x2(mvp.c2 * aabb.Min.z, mvp.c2 * aabb.Max.z);

                for (int corner = 0; corner < 8; corner++)
                {
                    float4 p = u[corner & 1] + v[(corner & 2) >> 1] + w[(corner & 4) >> 2] + mvp.c3;
                    p.y = -p.y;
                    verts[corner] = p;
                }

                int vertexCount = 8;
                float clipW = 0.00001f;
                for (int i = 0; i < 12; i++)
                {
                    var e = edges[i];
                    var a = verts[e.x];
                    var b = verts[e.y];

                    if ((a.w < clipW) != (b.w < clipW))
                    {
                        var p = math.lerp(a, b, (clipW - a.w) / (b.w - a.w));
                        verts[vertexCount++] = p;
                    }
                }

                for (int i = 0; i < vertexCount; i++)
                {
                    float4 p = verts[i];
                    if (p.w < clipW)
                        continue;

                    p.xyz /= p.w;
                    occlusionTest.screenMin = math.min(occlusionTest.screenMin, p);
                    occlusionTest.screenMax = math.max(occlusionTest.screenMax, p);
                }

                tests[entityIndex] = occlusionTest;
            }
        }
    }

    [BurstCompile]
    unsafe struct ProxyOcclusionMeshTransformJob : IJobChunk
    {
        public ComponentTypeHandle<OcclusionMesh> OcclusionMeshComponent;
        [ReadOnly] public ComponentTypeHandle<LocalToWorld> LocalToWorldComponent;
        [ReadOnly] public float4x4 ViewProjection;

        // TODO:  This forces us to run on one job, need to use a queue
        [NativeDisableParallelForRestriction] public NativeList<OcclusionMesh> OcclusionMeshes;

        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            var meshes = chunk.GetNativeArray(OcclusionMeshComponent);

            for (int i = 0; i < chunk.Count; i++)
            {
                var mesh = meshes[i];
                mesh.Transform(math.mul(ViewProjection, mesh.localToWorld));
                meshes[i] = mesh;

                // Running on 1 job, this is ok for now
                OcclusionMeshes.Add(mesh);
            }
        }
    }

    [BurstCompile]
    unsafe struct OcclusionMeshTransformJob : IJobChunk
    {
        [ReadOnly] public NativeArray<int> InternalToExternalRemappingTable;

        [ReadOnly] public ComponentTypeHandle<HybridChunkInfo> HybridChunkInfo;
        public ComponentTypeHandle<OcclusionMesh> OcclusionMeshComponent;
        [ReadOnly] public ComponentTypeHandle<LocalToWorld> LocalToWorldComponent;

        [ReadOnly] [NativeDisableParallelForRestriction] public NativeArray<int> IndexList;
        [NativeDisableParallelForRestriction] public NativeArray<BatchVisibility> Batches;
        [ReadOnly] public float4x4 ViewProjection;

        // TODO:  This forces us to run on one job, need to use a queue
        [NativeDisableParallelForRestriction] public NativeList<OcclusionMesh> OcclusionMeshes;

#if UNITY_EDITOR
        [NativeDisableUnsafePtrRestriction]
        public CullingStats* Stats;
#pragma warning disable 649
        [NativeSetThreadIndex]
        public int ThreadIndex;
#pragma warning restore 649
#endif

        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            var chunkInfo = chunk.GetChunkComponentData(HybridChunkInfo);
            if (!chunkInfo.Valid)
                return;

            var meshes = chunk.GetNativeArray(OcclusionMeshComponent);

            var chunkData = chunkInfo.CullingData;

            int internalBatchIndex = chunkInfo.InternalIndex;
            int externalBatchIndex = InternalToExternalRemappingTable[internalBatchIndex];

            var batch = Batches[externalBatchIndex];

            var indices = (int*)IndexList.GetUnsafeReadOnlyPtr() + chunkData.StartIndex;

            for (var entityIndex = 0; entityIndex < chunkData.Visible; entityIndex++)
            {
                int index = indices[entityIndex] - chunkData.BatchOffset;

                var mesh = meshes[index];
                mesh.Transform(math.mul(ViewProjection, mesh.localToWorld));
                meshes[index] = mesh;

                // Running on 1 job, this is ok for now
                OcclusionMeshes.Add(mesh);
            }
        }
    }
}

#endif
