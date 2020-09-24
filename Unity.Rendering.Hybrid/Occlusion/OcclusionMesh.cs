#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Unity.Rendering.Occlusion
{
    public struct OcclusionMeshAsset : IComponentData, System.IEquatable<OcclusionMeshAsset>
    {
        public BlobAssetReference<float4> vertexData;
        public BlobAssetReference<int> indexData;
        public int vertexCount;
        public int indexCount;


        public bool Equals(OcclusionMeshAsset other)
        {
            return (vertexData.GetHashCode() == other.vertexData.GetHashCode() && indexData.GetHashCode() == other.indexData.GetHashCode());
        }

        public override int GetHashCode()
        {
            return vertexData.GetHashCode() ^ indexData.GetHashCode();
        }
    }

    public struct OcclusionMesh : IComponentData
    {
        unsafe public OcclusionMesh(ref OcclusionMeshAsset sharedMesh, Occluder occluder)
        {
            vertexCount = sharedMesh.vertexCount;
            indexCount = sharedMesh.indexCount;
            vertexData = sharedMesh.vertexData;
            indexData = sharedMesh.indexData;

            int size = UnsafeUtility.SizeOf<float4>() * vertexCount;
            var data = Memory.Unmanaged.Allocate(size, 64, Allocator.Persistent);
            transformedVertexData = BlobAssetReference<float4>.Create(data, size);

            screenMin = float.MaxValue;
            screenMax = -float.MaxValue;

            localToWorld = occluder.localTransform;
        }

        public unsafe void Transform(float4x4 MVP)
        {
            screenMin = float.MaxValue;
            screenMax = -float.MaxValue;
            float4* vin = (float4*)vertexData.GetUnsafePtr();
            float4* vout = (float4*)transformedVertexData.GetUnsafePtr();

            for (int v = 0; v < vertexCount; ++v, ++vin, ++vout)
            {
                *vout = math.mul(MVP, *vin);
                vout->y = -vout->y;

                screenMin.xyz = math.min(screenMin.xyz, vout->xyz);
                screenMax.xyz = math.max(screenMax.xyz, vout->xyz);
            }
        }

        public int vertexCount;
        public int indexCount;

        public BlobAssetReference<float4> vertexData, transformedVertexData;
        public BlobAssetReference<int> indexData;

        public float4 screenMin, screenMax;
        public float4x4 localToWorld;
    }

    public struct OcclusionTest : IComponentData
    {
        public OcclusionTest(bool enabled)
        {
            this.enabled = enabled;
            screenMin = float.MaxValue;
            screenMax = -float.MaxValue;
        }

        // this flag is for toggling occlusion testing without having to add a component at runtime.
        public bool enabled;
        public float4 screenMin, screenMax;
    }
}

#endif
